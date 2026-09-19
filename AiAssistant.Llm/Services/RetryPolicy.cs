using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiAssistant.Llm.Services;

/// <summary>
/// Error categories that influence retry behaviour.
/// Mirrors cline's ClineErrorType enum (rateLimit, auth, network, balance).
/// </summary>
public enum LlmErrorType
{
    Unknown,
    RateLimit,
    Auth,
    Network,
    ServerError,
    BadRequest,
}

/// <summary>
/// Reusable retry policy for LLM API calls.
/// Replaces the scattered inline retry logic that previously lived inside ChatService.
/// </summary>
public class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    public bool RetryAllErrors { get; init; } = false;
    public ILogger? Logger { get; init; }

    private static readonly Regex RateLimitRegex = new(
        @"\b(429|rate limit|too many requests|quota exceeded|resource exhausted|rate_limit_exceeded)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RetryAfterRegex = new(
        @"retry[^,\s]*\s*after[^,\s]*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly RetryPolicy Default = new();

    /// <summary>
    /// Classify an exception into a known error type for retry decisions.
    /// Walks the InnerException chain to find the root cause.
    /// </summary>
    public static LlmErrorType ClassifyError(Exception? ex)
    {
        if (ex == null) return LlmErrorType.Unknown;

        // Walk the exception chain
        var current = ex;
        while (current != null)
        {
            // ClientResultException (OpenAI SDK, Azure SDK)
            if (current is System.ClientModel.ClientResultException cre)
            {
                if (cre.Status == 429) return LlmErrorType.RateLimit;
                if (cre.Status is 401 or 403) return LlmErrorType.Auth;
                if (cre.Status >= 500) return LlmErrorType.ServerError;
                if (cre.Status == 400) return LlmErrorType.BadRequest;
            }

            // Azure.RequestFailedException
            if (current is Azure.RequestFailedException rfe)
            {
                if (rfe.Status == 429) return LlmErrorType.RateLimit;
                if (rfe.Status is 401 or 403) return LlmErrorType.Auth;
                if (rfe.Status >= 500) return LlmErrorType.ServerError;
                if (rfe.Status == 400) return LlmErrorType.BadRequest;
            }

            current = current.InnerException;
        }

        // Fall back to message-based heuristics (some providers embed status in messages)
        var msg = ex.Message ?? "";
        if (RateLimitRegex.IsMatch(msg)) return LlmErrorType.RateLimit;
        if (msg.Contains("401") || msg.Contains("Unauthorized") || msg.Contains("invalid_api_key") || msg.Contains("Incorrect API key"))
            return LlmErrorType.Auth;
        if (msg.Contains("500") || msg.Contains("502") || msg.Contains("503") || msg.Contains("504"))
            return LlmErrorType.ServerError;
        if (msg.Contains("400") || msg.Contains("Bad Request"))
            return LlmErrorType.BadRequest;

        // Network-level checks
        if (IsNetworkException(ex)) return LlmErrorType.Network;

        return LlmErrorType.Unknown;
    }

    private static bool IsNetworkException(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is System.Net.Sockets.SocketException ||
               ex is IOException ||
               ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested ||
               ex.InnerException is HttpRequestException ||
               ex.InnerException is System.Net.Sockets.SocketException;
    }

    /// <summary>
    /// Determine whether an exception should be retried.
    /// </summary>
    public bool ShouldRetry(Exception ex, int attempt)
    {
        if (attempt >= MaxRetries) return false;
        if (RetryAllErrors) return true;

        var type = ClassifyError(ex);
        return type is LlmErrorType.RateLimit or LlmErrorType.Network or LlmErrorType.ServerError;
    }

    /// <summary>
    /// Calculate the retry delay. Prefers Retry-After / X-RateLimit-Reset headers,
    /// falls back to exponential backoff with jitter.
    /// </summary>
    public TimeSpan GetRetryDelay(Exception ex, int attempt)
    {
        // Try to extract a delay from headers embedded in the exception
        var headerDelay = GetRetryAfterFromException(ex);
        if (headerDelay.HasValue) return headerDelay.Value;

        // Exponential backoff with jitter: 2s, 4s, 8s, 16s, ...
        var exponential = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(new Random().Next(0, 500));
        var delay = exponential + jitter;
        return delay > MaxDelay ? MaxDelay : delay;
    }

    /// <summary>
    /// Attempt to parse Retry-After, X-RateLimit-Reset, or ratelimit-reset from
    /// exception properties or message text.
    /// </summary>
    private static TimeSpan? GetRetryAfterFromException(Exception ex)
    {
        // Try ClientResultException headers
        if (ex is System.ClientModel.ClientResultException cre)
        {
            var raw = cre.GetRawResponse();
            if (raw?.Headers != null)
            {
                foreach (var headerName in new[] { "Retry-After", "x-ratelimit-reset", "ratelimit-reset" })
                {
                    if (raw.Headers.TryGetValues(headerName, out var values) && values != null)
                    {
                        foreach (var val in values)
                        {
                            if (TryParseDelay(val, out var delay))
                                return delay;
                        }
                    }
                }
            }
        }

        // Try Azure.RequestFailedException
        if (ex is Azure.RequestFailedException rfe)
        {
            var headers = rfe.GetRawResponse()?.Headers;
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    foreach (var headerName in new[] { "Retry-After", "x-ratelimit-reset", "ratelimit-reset" })
                    {
                        if (string.Equals(header.Name, headerName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryParseDelay(header.Value, out var delay))
                                return delay;
                        }
                    }
                }
            }
        }

        // Fall back to regex on the message (some providers embed "retry after N")
        var match = RetryAfterRegex.Match(ex.Message);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        // Try to find "retry-after" in the message itself
        if (RetryAfterRegex.IsMatch(ex.Message) && int.TryParse(match.Groups[1].Value, out seconds))
            return TimeSpan.FromSeconds(seconds);

        return null;
    }

    private static bool TryParseDelay(string value, out TimeSpan delay)
    {
        delay = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Delta-seconds format
        if (int.TryParse(value, out var seconds))
        {
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // Unix timestamp format
        if (long.TryParse(value, out var unixTimestamp) && unixTimestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            var target = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
            delay = target - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) return true;
        }

        // HTTP-date format
        if (DateTimeOffset.TryParse(value, out var date))
        {
            delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) return true;
        }

        return false;
    }

    /// <summary>
    /// Execute an async streaming operation with retry support.
    /// The onRetry callback is invoked before each retry attempt (for UI feedback).
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, int, TimeSpan, Task>? onRetryAsync = null,
        CancellationToken ct = default)
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                var errorType = ClassifyError(ex);

                // Never retry auth errors or bad request (400) errors
                if (errorType is LlmErrorType.Auth or LlmErrorType.BadRequest)
                    throw;

                if (attempt >= MaxRetries) throw;

                var delay = GetRetryDelay(ex, attempt);

                Logger?.LogWarning(
                    "Retry {Attempt}/{MaxRetries} after {Delay}ms (error type: {ErrorType}): {Message}",
                    attempt + 1, MaxRetries, delay.TotalMilliseconds, errorType, ex.Message);

                var retryTask = onRetryAsync?.Invoke(ex, attempt + 1, delay);
                if (retryTask != null)
                    await retryTask;
                await Task.Delay(delay, ct);
            }
        }

        throw lastError ?? new InvalidOperationException("Retry policy exhausted without error");
    }
}

/// <summary>
/// Exception that carries retry metadata (status code, retry-after delay).
/// Mirrors cline's RetriableError.
/// </summary>
public class RetriableLlmException : Exception
{
    public int Status { get; }
    public int? RetryAfterSeconds { get; }

    public RetriableLlmException(string message, int status = 429, int? retryAfterSeconds = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// Static helper methods for error classification and retry-after extraction.
/// </summary>
public static class LlmErrorClassifier
{
    private static readonly Regex RateLimitRegex = new(
        @"\b(429|rate limit|too many requests|quota exceeded|resource exhausted|rate_limit_exceeded)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContextWindowRegex = new(
        @"\b(context\s*(?:length|window)|input\s*is\s*too\s*long|too\s*many\s*tokens|maximum\s*context|token.*exceed|input.*token.*exceed)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsRateLimitError(Exception? ex)
    {
        if (ex == null) return false;
        var current = ex;
        while (current != null)
        {
            if (current is System.ClientModel.ClientResultException cre && cre.Status == 429) return true;
            if (current is Azure.RequestFailedException rfe && rfe.Status == 429) return true;
            if (RateLimitRegex.IsMatch(current.Message)) return true;
            current = current.InnerException;
        }
        return false;
    }

    public static bool IsContextWindowError(Exception? ex)
    {
        if (ex == null) return false;
        var current = ex;
        while (current != null)
        {
            if (current is System.ClientModel.ClientResultException cre && cre.Status == 400 && ContextWindowRegex.IsMatch(cre.Message))
                return true;
            if (current is Azure.RequestFailedException rfe && rfe.Status == 400 && ContextWindowRegex.IsMatch(rfe.Message))
                return true;
            if (current.Message.Contains("context length") || current.Message.Contains("input is too long"))
                return true;
            current = current.InnerException;
        }
        return false;
    }

    public static bool IsAuthError(Exception? ex)
    {
        if (ex == null) return false;
        var current = ex;
        while (current != null)
        {
            if (current is System.ClientModel.ClientResultException cre && (cre.Status == 401 || cre.Status == 403))
                return true;
            if (current is Azure.RequestFailedException rfe && (rfe.Status == 401 || rfe.Status == 403))
                return true;
            if (current.Message.Contains("401") || current.Message.Contains("Unauthorized") ||
                current.Message.Contains("invalid_api_key") || current.Message.Contains("Incorrect API key"))
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
