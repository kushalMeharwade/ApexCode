using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AiAssistant.Llm.Services;

/// <summary>Limits are per user turn. Retry counts exclude the initial attempt.</summary>
public sealed class ChatResilienceOptions
{
    public int MaxModelRequests { get; set; } = 300;
    public int MaxToolCalls { get; set; } = 600;
    public int MaxConsecutiveMistakes { get; set; } = 15;
    public int MaxIdenticalToolCalls { get; set; } = 100;
    public int MaxEmptyRetries { get; set; } = 3;
    public int MaxTransportRetries { get; set; } = 3;
    public int MaxRateLimitRetries { get; set; } = 5;
    public int MaxContextRetries { get; set; } = 2;
    public int MaxPlanTaskRetries { get; set; } = 5;
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(45);

    public ChatResilienceOptions Snapshot()
    {
        var copy = (ChatResilienceOptions)MemberwiseClone();
        if (copy.MaxModelRequests < 1 || copy.MaxToolCalls < 1 || copy.MaxConsecutiveMistakes < 1 ||
            copy.MaxIdenticalToolCalls < 1 || copy.MaxEmptyRetries < 0 || copy.MaxTransportRetries < 0 ||
            copy.MaxRateLimitRetries < 0 || copy.MaxContextRetries < 0 || copy.MaxPlanTaskRetries < 0 || copy.BackoffBase < TimeSpan.Zero ||
            copy.MaxBackoff < copy.BackoffBase || copy.StreamIdleTimeout <= TimeSpan.Zero ||
            copy.TurnTimeout <= TimeSpan.Zero || copy.TurnTimeout.TotalMilliseconds > int.MaxValue ||
            copy.StreamIdleTimeout.TotalMilliseconds > int.MaxValue || copy.MaxBackoff.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ChatResilienceOptions), "Resilience limits must be finite and positive; retries may be zero.");
        return copy;
    }
}

/// <summary>Per-turn budgets and protocol repair shared by streaming and XML/native dispatch.</summary>
public sealed class ChatTurnRecovery
{
    private readonly Dictionary<string, int> _retries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private int _requests;
    private int _toolCalls;
    public ChatResilienceOptions Options { get; }
    public int ConsecutiveMistakes { get; private set; }

    public ChatTurnRecovery(ChatResilienceOptions options) => Options = options.Snapshot();
    public bool TryRequest() => ++_requests <= Options.MaxModelRequests;
    public bool RecordMistake() => ++ConsecutiveMistakes < Options.MaxConsecutiveMistakes;
    public void RecordSuccess() => ConsecutiveMistakes = 0;

    public bool TryRetry(string kind, int limit, out int attempt)
    {
        _retries.TryGetValue(kind, out var count);
        attempt = count + 1;
        if (count >= limit) return false;
        _retries[kind] = attempt;
        return true;
    }

    public string? CheckToolCall(string name, IDictionary<string, object?> arguments)
    {
        if (++_toolCalls > Options.MaxToolCalls) return "The total tool-call budget was reached.";
        if (name == "read_command_output") return null; // Long-polling still consumes the total tool budget.
        var signature = name + ":" + Canonical(JsonSerializer.SerializeToElement(arguments));
        _calls.TryGetValue(signature, out var count);
        _calls[signature] = ++count;
        return count > Options.MaxIdenticalToolCalls
            ? "This exact tool call has already reached its per-turn limit. Use a different approach or ask the user for guidance."
            : null;
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText()
    };

    public static string Feedback(string code, string detail) => JsonSerializer.Serialize(new
    {
        type = "agent_recovery", code, detail,
        next_action = "Correct the error using the available tool schema. Do not repeat a failed action unchanged. If blocked, ask the user for guidance."
    });

    public TimeSpan RetryDelay(Exception? error, int attempt)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            foreach (var key in new[] { "Retry-After", "retry-after", "X-RateLimit-Reset", "x-ratelimit-reset", "RateLimit-Reset" })
            {
                var value = current.Data[key]?.ToString();
                if (current is System.ClientModel.ClientResultException cre &&
                    cre.GetRawResponse()?.Headers.TryGetValue(key, out var header) == true) value = header;
                if (current is Azure.RequestFailedException rfe &&
                    rfe.GetRawResponse()?.Headers.TryGetValue(key, out var azureHeader) == true) value = azureHeader;
                if (string.IsNullOrEmpty(value)) continue;
                double seconds;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                    !double.IsNaN(number) && !double.IsInfinity(number))
                {
                    seconds = key.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0
                        ? number - DateTimeOffset.UtcNow.ToUnixTimeSeconds() : number;
                }
                else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
                else continue;
                return TimeSpan.FromSeconds(Math.Max(0, Math.Min(Options.MaxBackoff.TotalSeconds, seconds)));
            }
        }
        return TimeSpan.FromMilliseconds(Math.Min(Options.MaxBackoff.TotalMilliseconds,
            Options.BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Min(30, Math.Max(0, attempt - 1)))));
    }

    public static bool IsTransient(Exception error)
    {
        if (LlmErrorClassifier.IsAuthError(error) || LlmErrorClassifier.IsContextWindowError(error)) return false;
        for (var current = error; current != null; current = current.InnerException)
        {
            int? status = current is System.ClientModel.ClientResultException cre ? cre.Status :
                current is Azure.RequestFailedException rfe ? rfe.Status : (int?)null;
            if (status.HasValue) return status == 408 || status == 429 || status >= 500;
            // net472 HttpRequestException has no StatusCode. Reject textual HTTP failures before
            // treating a status-less HttpRequestException as a disconnected transport.
            if (System.Text.RegularExpressions.Regex.IsMatch(current.Message, @"\b4\d\d\b")) return false;
        }
        for (var current = error; current != null; current = current.InnerException)
            if (current is HttpRequestException || current is System.IO.IOException ||
                current is System.Net.Sockets.SocketException || current is TimeoutException) return true;
        return false;
    }

    /// <summary>Remove whole older turns, retaining system, initial task and latest user turn with tool pairs.</summary>
    public static bool TrimHistory(List<ChatMessage> messages)
    {
        var firstUser = messages.FindIndex(m => m.Role == ChatRole.User);
        var lastUser = messages.FindLastIndex(m => m.Role == ChatRole.User && !IsRecoveryFeedback(m.Text));
        if (firstUser < 0) return false;
        var start = firstUser + 1;
        // Find a later complete user turn; never cut an assistant/tool-result group in half.
        var boundary = lastUser;
        if (boundary <= start) return false;
        messages.RemoveRange(start, boundary - start);
        return true;
    }

    private static bool IsRecoveryFeedback(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_recovery";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Ensure every call has exactly one matching result before another role follows.</summary>
    public static void RepairToolPairs(List<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var calls = messages[i].Contents.OfType<FunctionCallContent>().ToList();
            if (messages[i].Role != ChatRole.Assistant || calls.Count == 0) continue;
            var results = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
            while (i + 1 < messages.Count && messages[i + 1].Role == ChatRole.Tool)
            {
                foreach (var result in messages[i + 1].Contents.OfType<FunctionResultContent>())
                    results[result.CallId] = result;
                messages.RemoveAt(i + 1);
            }
            messages.Insert(++i, new ChatMessage(ChatRole.Tool, calls.Select(call => (AIContent)
                (results.TryGetValue(call.CallId, out var result) ? result : new FunctionResultContent(call.CallId,
                    Feedback("interrupted_tool", "Execution outcome is unknown. Inspect current state before retrying this action.")))).ToList()));
        }
    }

    public static string? ValidateArguments(AIFunction function, AIFunctionArguments arguments)
    {
        return Validate(function.JsonSchema, JsonSerializer.SerializeToElement(arguments), "arguments");
    }

    private static string? Validate(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (schema.TryGetProperty("type", out var type))
        {
            bool Matches(string? name) => name switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!(type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(t => Matches(t.GetString())) : Matches(type.GetString())))
                return path + " has the wrong type; expected " + type.GetRawText();
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _)) return path + "." + name.GetString() + " is required.";
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in value.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var child))
                        return path + "." + property.Name + " is not a declared parameter.";
                    var error = Validate(child, property.Value, path + "." + property.Name);
                    if (error != null) return error;
                }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray())
            {
                var error = Validate(items, item, path + "[]");
                if (error != null) return error;
            }
        return null;
    }

    /// <summary>Wait without racing DisposeAsync against an outstanding MoveNextAsync.</summary>
    public static async Task<T> WithCancellation<T>(Task<T> operation, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false) != operation)
            {
                _ = operation.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                ct.ThrowIfCancellationRequested();
            }
            return await operation.ConfigureAwait(false);
        }
    }
}
