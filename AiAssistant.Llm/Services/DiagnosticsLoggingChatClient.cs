using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.AI;

namespace AiAssistant.Llm.Services;

public class DiagnosticsLoggingChatClient : DelegatingChatClient
{
    private readonly ILogBus _logBus;
    private readonly IModelCacheRepository _modelCacheRepo;
    private readonly string _providerId;
    private readonly string _modelId;

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = true };

    /// <summary>
    /// When non-null, suppresses per-call LlmTransaction creation/publishing so that
    /// a conversation-level transaction in ChatService can capture the full agentic loop.
    /// The value is the LlmTransaction that should receive aggregated request/response data.
    /// </summary>
    public static AsyncLocal<LlmTransaction?> ActiveConversationTransaction = new();

    public DiagnosticsLoggingChatClient(
        IChatClient innerClient,
        ILogBus logBus,
        IModelCacheRepository modelCacheRepo,
        string providerId,
        string modelId) : base(innerClient)
    {
        _logBus = logBus;
        _modelCacheRepo = modelCacheRepo;
        _providerId = providerId;
        _modelId = modelId;
    }

    private static (int? Status, string? Body) Describe(Exception ex) => ex switch
    {
        System.ClientModel.ClientResultException cre => (cre.Status, SafeGetRawContent(cre)),
        Azure.RequestFailedException rfe              => (rfe.Status, SafeGetAzureContent(rfe)),
        _                                            => (null, ex.ToString())
    };

    // GetRawResponse()?.Content calls Azure.Response.get_Content() which can throw
    // MissingMethodException when the VS Designer Cache loads a stale Azure.Core.
    // Read it safely and fall back to the exception message.
    private static string? SafeGetRawContent(System.ClientModel.ClientResultException cre)
    {
        try { return cre.GetRawResponse()?.Content.ToString(); }
        catch { return cre.Message; }
    }

    private static string? SafeGetAzureContent(Azure.RequestFailedException rfe)
    {
        try { return rfe.GetRawResponse()?.Content.ToString(); }
        catch { return rfe.Message; }
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LlmTransaction? transaction = null;
        bool isSuppressed = ActiveConversationTransaction.Value != null;
        if (!isSuppressed)
        {
            var reqPayload = JsonSerializer.Serialize(new { Messages = chatMessages, Options = options }, _jsonOptions);
            transaction = new LlmTransaction(_providerId, _modelId, reqPayload);
            _logBus.Publish(transaction);
        }

        try
        {
            var response = await base.GetResponseAsync(chatMessages, options, cancellationToken);
            stopwatch.Stop();
            
            if (!isSuppressed && transaction != null)
            {
                transaction.Duration = stopwatch.Elapsed;
                transaction.ResponsePayload = JsonSerializer.Serialize(response, _jsonOptions);
                transaction.Status = "200 OK";
                transaction.PromptTokens = (int?)response.Usage?.InputTokenCount;
                transaction.CompletionTokens = (int?)response.Usage?.OutputTokenCount;
            }
            
            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (!isSuppressed && transaction != null)
            {
                var (status, body) = Describe(ex);
                transaction.Duration = stopwatch.Elapsed;
                transaction.Status = status.HasValue ? $"{status} Error" : "Error";
                transaction.Error = body;
            }
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LlmTransaction? transaction = null;
        bool isSuppressed = ActiveConversationTransaction.Value != null;
        if (!isSuppressed)
        {
            var reqPayload = JsonSerializer.Serialize(new { Messages = chatMessages, Options = options }, _jsonOptions);
            transaction = new LlmTransaction(_providerId, _modelId, reqPayload);
            _logBus.Publish(transaction);
        }

        var updates = new List<ChatResponseUpdate>();
        Exception? streamException = null;

        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = base.GetStreamingResponseAsync(chatMessages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (!isSuppressed && transaction != null)
            {
                var (status, body) = Describe(ex);
                transaction.Duration = stopwatch.Elapsed;
                transaction.Status = status.HasValue ? $"{status} Error" : "Error";
                transaction.Error = body;
            }
            throw;
        }

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                streamException = ex;
                break;
            }

            if (!hasNext) break;

            var update = enumerator.Current;
            updates.Add(update);
            yield return update;
        }

        if (enumerator != null) await enumerator.DisposeAsync();
        stopwatch.Stop();

        if (!isSuppressed && transaction != null)
        {
            transaction.Duration = stopwatch.Elapsed;

            if (streamException != null)
            {
                var (status, body) = Describe(streamException);
                transaction.Status = status.HasValue ? $"{status} Error" : "Error";
                transaction.Error = body;
                
                // Try to serialize what we got before the error
                if (updates.Count > 0)
                {
                    try { transaction.ResponsePayload = JsonSerializer.Serialize(updates.ToChatResponse(), _jsonOptions); } catch { }
                }
                throw new Exception($"Stream interrupted by error ({transaction.Status}).", streamException);
            }
            else
            {
                try
                {
                    var finalResponse = updates.ToChatResponse();
                    transaction.ResponsePayload = JsonSerializer.Serialize(finalResponse, _jsonOptions);
                    transaction.Status = "200 OK";
                    transaction.PromptTokens = (int?)finalResponse.Usage?.InputTokenCount;
                    transaction.CompletionTokens = (int?)finalResponse.Usage?.OutputTokenCount;
                }
                catch (Exception ex)
                {
                    transaction.Status = "Parse Error";
                    transaction.Error = ex.ToString();
                }
            }
        }
        else if (streamException != null)
        {
            throw new Exception($"Stream interrupted by error ({Describe(streamException).Status?.ToString() ?? "Error"}).", streamException);
        }
    }
}
