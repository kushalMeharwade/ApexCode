using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Polly;
using System.Linq;

namespace AiAssistant.Llm.Services;

public class ResilientChatClient : IChatClient
{
    private readonly ResiliencePipeline _pipeline;
    private readonly IChatClient _innerClient;

    public ResilientChatClient(IChatClient innerClient, ResiliencePipeline pipeline)
    {
        _innerClient = innerClient;
        _pipeline = pipeline;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _innerClient.GetService(serviceType, serviceKey);

    public void Dispose() => _innerClient.Dispose();

    private static List<ChatMessage> SanitizeHistory(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                bool hasText = !string.IsNullOrWhiteSpace(msg.Text);
                bool hasToolCalls = msg.Contents?.OfType<FunctionCallContent>().Any() == true;
                if (!hasText && !hasToolCalls)
                {
                    continue; // drop empty assistant message
                }
            }
            result.Add(msg);
        }
        return result;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messages = SanitizeHistory(chatMessages);
        
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var response = await _pipeline.ExecuteAsync(
                async ct => await _innerClient.GetResponseAsync(messages, options, ct),
                cancellationToken);

            bool hasText = response.Messages.Any(m => !string.IsNullOrWhiteSpace(m.Text));
            bool hasToolCalls = response.Messages.SelectMany(m => m.Contents ?? Enumerable.Empty<AIContent>()).OfType<FunctionCallContent>().Any();

            if (hasText || hasToolCalls) return response;

            messages = messages.Append(new ChatMessage(ChatRole.User,
                "Your last response was empty. Continue: call the next tool or produce the final answer."))
                .ToList();
        }
        
        throw new InvalidOperationException("The LLM repeatedly returned empty responses without tool calls.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = SanitizeHistory(chatMessages);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            bool hasFirstElement = false;
            try
            {
                (enumerator, hasFirstElement) = await _pipeline.ExecuteAsync(async ct =>
                {
                    var enumerable = _innerClient.GetStreamingResponseAsync(messages, options, ct);
                    var e = enumerable.GetAsyncEnumerator(ct);
                    try
                    {
                        bool hasFirst = await e.MoveNextAsync();
                        return (e, hasFirst);
                    }
                    catch (Exception originalEx)
                    {
                        try { await e.DisposeAsync(); } catch { /* ignore */ }
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(originalEx).Throw();
                        throw;
                    }
                }, cancellationToken);
            }
            catch (Exception)
            {
                if (enumerator != null) await enumerator.DisposeAsync();
                throw;
            }

            if (!hasFirstElement)
            {
                if (enumerator != null) await enumerator.DisposeAsync();
                // Stream was completely empty (not even metadata), treat as empty turn
                messages = messages.Append(new ChatMessage(ChatRole.User, "Your last response was empty. Continue: call the next tool or produce the final answer.")).ToList();
                continue;
            }

            bool hasText = false;
            bool hasToolCalls = false;
            var buffer = new List<ChatResponseUpdate>();
            bool yielded = false;
            Exception? midStreamError = null;

            bool hasNext = true;
            do
            {
                var update = enumerator.Current;
                if (update != null)
                {
                    if (update.Contents != null)
                    {
                        if (update.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text))) hasText = true;
                        if (update.Contents.OfType<FunctionCallContent>().Any()) hasToolCalls = true;
                    }

                    if (hasText || hasToolCalls)
                    {
                        if (!yielded)
                        {
                            foreach (var b in buffer) yield return b;
                            buffer.Clear();
                            yielded = true;
                        }
                        yield return update;
                    }
                    else
                    {
                        buffer.Add(update);
                    }
                }

                try
                {
                    var moveNextTask = enumerator.MoveNextAsync().AsTask();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(90), cancellationToken);
                    var completed = await Task.WhenAny(moveNextTask, timeoutTask);
                    
                    if (completed == timeoutTask)
                    {
                        if (timeoutTask.IsCanceled) cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("The stream timed out waiting for the next chunk from the server.");
                    }
                    hasNext = await moveNextTask;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                    midStreamError = ex;
                    break;
                }
            } while (hasNext);

            if (midStreamError != null)
            {
                if (!yielded)
                {
                    foreach (var b in buffer) yield return b;
                    buffer.Clear();
                    yielded = true;
                }
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = new List<AIContent> { new TextContent($"\n\n[MID_STREAM_ERROR:{midStreamError.Message}]") }
                };
            }

            if (enumerator != null) await enumerator.DisposeAsync();

            if (hasText || hasToolCalls || midStreamError != null)
            {
                yield break;
            }

            // Stream ended gracefully but was completely devoid of text/tools. 
            // Re-prompt for the next attempt.
            messages = messages.Append(new ChatMessage(ChatRole.User, "Your last response was empty. Continue: call the next tool or produce the final answer.")).ToList();
        }

        throw new InvalidOperationException("The LLM repeatedly returned empty responses without tool calls.");
    }
}
