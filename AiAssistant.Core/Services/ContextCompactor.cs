using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using System.Linq;

namespace AiAssistant.Core.Services;

public class ContextCompactor : IContextCompactor
{
    private readonly Func<IChatClient> _clientResolver;

    private const int CompactionThreshold = 80; // Start compaction at 80% of max
    private const int SummaryMaxTokens = 500;   // Max tokens for the summary itself

    // FIX #9: Cache the tokenizer as a static field initialised once on a background thread.
    // TiktokenTokenizer.CreateForModel() makes a synchronous HTTP request to download the vocab
    // file on first call. By caching the instance we guarantee:
    //   • The HTTP call happens exactly once, on a background thread (never the UI thread).
    //   • All subsequent EstimateTokens() calls are pure in-memory operations (no I/O).
    // If the background warm-up hasn't finished yet, EstimateTokens() uses the char-based
    // heuristic fallback — no blocking, no crash.
    private static TiktokenTokenizer? _cachedTokenizer;
    private static readonly object _tokenizerLock = new();

    public ContextCompactor(Func<IChatClient> clientResolver)
    {
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));

        // Pre-warm the tokenizer on a background thread so it is ready before the first chat.
        Task.Run(() =>
        {
            try
            {
                var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
                lock (_tokenizerLock)
                {
                    _cachedTokenizer = tokenizer;
                }
            }
            catch { /* Ignore — the heuristic fallback in EstimateTokens() will be used */ }
        });
    }

    /// <summary>
    /// Summarizes a list of conversation turns using the LLM.
    /// Produces a concise summary preserving key decisions, code changes, and context.
    /// </summary>
    public async Task<string> SummarizeConversationAsync(IEnumerable<ConversationTurn> turns)
    {
        var turnsList = turns.ToList();
        if (turnsList.Count == 0)
            return "";

        if (turnsList.Count == 1)
            return $"Previous message ({turnsList[0].Role}): {turnsList[0].Content}";

        // Build the conversation text for summarization
        var conversationText = new System.Text.StringBuilder();
        foreach (var turn in turnsList)
        {
            conversationText.AppendLine($"[{turn.Role}] {turn.Content}");
            conversationText.AppendLine();
        }

        var prompt = $@"Summarize the following conversation concisely. Preserve:
- Key decisions made
- Code changes discussed or applied
- Important context about the project
- Any errors encountered and their fixes

Keep the summary under {SummaryMaxTokens} tokens. Output ONLY the summary, no preamble.

Conversation:
{conversationText}";

        try
        {
            var messages = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, "You are a conversation summarizer. Produce concise summaries that preserve key technical details, decisions, and code changes."),
                new(Microsoft.Extensions.AI.ChatRole.User, prompt)
            };

            var client = _clientResolver();
            var response = await client.GetResponseAsync(messages, new ChatOptions());
            // version. The correct accessor is .Text directly (in newer 10.x versions).
            // This matches the pattern used in LlmErrorFixer.cs and fixes the CS1061 build error that
            // was silently catching in the catch block, always returning the fallback summary.
            var summary = response.Text?.Trim();

            if (string.IsNullOrWhiteSpace(summary))
                return $"[Summary of {turnsList.Count} messages]";

            return summary!;
        }
        catch (Exception ex)
        {
            // If LLM call fails, produce a basic summary from the content
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Summarization failed: {ex.Message}");
            return GenerateFallbackSummary(turnsList);
        }
    }

    /// <summary>
    /// Compacts conversation context by summarizing older turns when the total
    /// token count exceeds the specified maximum.
    /// Returns the compacted context as a single string (for system prompt injection).
    /// </summary>
    public async Task<string> CompactContextAsync(IEnumerable<ConversationTurn> turns, int maxTokens)
    {
        var turnsList = turns.ToList();
        if (turnsList.Count == 0)
            return "";

        var totalTokens = turnsList.Sum(t => EstimateTokensForTurn(t));

        // If we're under the limit, no compaction needed
        if (totalTokens <= maxTokens)
            return string.Join("\n\n", turnsList.Select(t => $"[{t.Role}] {t.Content}"));

        // If we're over the threshold, summarize the older half
        var threshold = (int)(maxTokens * (CompactionThreshold / 100.0));

        if (totalTokens <= threshold)
            return string.Join("\n\n", turnsList.Select(t => $"[{t.Role}] {t.Content}"));

        // Split: keep the newest 50% of turns, summarize the oldest 50%
        var splitPoint = turnsList.Count / 2;
        var olderTurns = turnsList.Take(splitPoint).ToList();
        var newerTurns = turnsList.Skip(splitPoint).ToList();

        // Summarize the older turns
        var summary = await SummarizeConversationAsync(olderTurns);

        // Build the compacted context
        var result = new System.Text.StringBuilder();
        result.AppendLine("[Previous conversation summary]");
        result.AppendLine(summary);
        result.AppendLine();
        result.AppendLine("[Recent messages]");
        foreach (var turn in newerTurns)
        {
            result.AppendLine($"[{turn.Role}] {turn.Content}");
        }

        return result.ToString();
    }

    /// <summary>
    /// Prunes conversation history to fit within the specified token budget.
    /// Keeps the most recent turns that fit, with intelligent preservation of important context.
    /// Always preserves: the last turn (current user message) and tries to preserve the first
    /// user message (original task description) when possible.
    /// </summary>
    /// <remarks>
    /// IMPORTANT CONTRACT: This method operates on DB history turns only.
    /// The system prompt is prepended separately by ChatService and is NOT part of this list.
    /// </remarks>
    public async Task<IEnumerable<ConversationTurn>> PruneHistoryAsync(IEnumerable<ConversationTurn> turns, int maxTokens)
    {
        // FIX Feature 4: PruneHistoryCoreAsync only drops WHOLE older turns; it never shrinks the
        // content INSIDE a turn. A single turn — typically the current user message with large
        // @file content injected by the UI — can itself exceed the entire budget, in which case the
        // core would return it verbatim and the provider would reject the request with a 400 Bad
        // Request (payload exceeds the model context window). The final ClampTurnsToBudget pass
        // truncates the content of any oversized turn so the returned set always fits maxTokens.
        // This also covers the <=2-turn early-return and the extreme "last turn alone > budget" path.
        var core = (await PruneHistoryCoreAsync(turns, maxTokens)).ToList();
        return ClampTurnsToBudget(core, maxTokens);
    }

    private async Task<IEnumerable<ConversationTurn>> PruneHistoryCoreAsync(IEnumerable<ConversationTurn> turns, int maxTokens)
    {
        var turnsList = turns.ToList();
        if (turnsList.Count <= 2)
            return turnsList; // Over-budget single/pair turns are handled by ClampTurnsToBudget.

        var totalTokens = turnsList.Sum(t => EstimateTokensForTurn(t));

        // If we're under the limit, return as-is
        if (totalTokens <= maxTokens)
            return turnsList;

        // Strategy: Preserve BOTH the first user message (original task) and last turn (current question)
        // Fill the middle with as many recent turns as fit, summarizing what gets cut
        
        var lastTurn = turnsList.Last();
        var firstTurn = turnsList.First();
        
        // Calculate tokens for anchor turns
        var lastTurnTokens = EstimateTokensForTurn(lastTurn);
        var firstTurnTokens = EstimateTokensForTurn(firstTurn);
        
        // Reserve space for first and last turns
        var reservedTokens = lastTurnTokens + firstTurnTokens;
        
        // If first and last are the same (2-turn conversation), just keep both
        if (turnsList.Count == 2)
            return turnsList;
        
        // Check if we can afford to keep both anchors
        var availableTokens = maxTokens - reservedTokens;
        
        if (availableTokens <= 0)
        {
            // Can't fit both anchors - prioritize the last turn (current context is more important)
            availableTokens = maxTokens - lastTurnTokens;
            if (availableTokens <= 0)
            {
                // Extreme edge case: even last turn alone exceeds budget
                return new[] { lastTurn };
            }
            
            // Try to fit some recent turns without the first turn anchor
            var middleTurns = turnsList.Skip(1).Take(turnsList.Count - 2).ToList();
            var keptMiddle = new List<ConversationTurn>();
            var usedTokens = 0;
            
            for (int i = middleTurns.Count - 1; i >= 0; i--)
            {
                var turnTokens = EstimateTokensForTurn(middleTurns[i]);
                if (usedTokens + turnTokens <= availableTokens)
                {
                    keptMiddle.Insert(0, middleTurns[i]);
                    usedTokens += turnTokens;
                }
                else
                {
                    break;
                }
            }
            
            // Summarize discarded oldest turns
            var discardedCount = middleTurns.Count - keptMiddle.Count;
            if (discardedCount > 0)
            {
                var discarded = middleTurns.Take(discardedCount).ToList();
                var summary = await SummarizeConversationAsync(discarded);
                var summaryTurn = new ConversationTurn
                {
                    Id = "summary-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Role = "user",
                    Content = $"[Earlier conversation context]: {summary}",
                    Timestamp = discarded.Last().Timestamp
                };
                return new[] { summaryTurn }.Concat(keptMiddle).Concat(new[] { lastTurn });
            }
            
            return keptMiddle.Concat(new[] { lastTurn });
        }
        
        // We can fit both anchors - now fill the middle with recent turns
        var middleTurnsList = turnsList.Skip(1).Take(turnsList.Count - 2).ToList();
        var keptMiddleTurns = new List<ConversationTurn>();
        var usedMiddleTokens = 0;

        // Greedily keep the newest middle turns that fit (newest-first iteration)
        for (int i = middleTurnsList.Count - 1; i >= 0; i--)
        {
            var turnTokens = EstimateTokensForTurn(middleTurnsList[i]);
            if (usedMiddleTokens + turnTokens <= availableTokens)
            {
                keptMiddleTurns.Insert(0, middleTurnsList[i]); // Insert at front to preserve chronological order
                usedMiddleTokens += turnTokens;
            }
            else
            {
                break; // Can't fit any more without exceeding budget
            }
        }

        // Identify which middle turns were discarded
        var discardedMiddleTurns = middleTurnsList.Except(keptMiddleTurns).ToList();

        if (discardedMiddleTurns.Count > 0)
        {
            // Summarize the discarded portion and prepend after the first turn
            var summary = await SummarizeConversationAsync(discardedMiddleTurns);
            var summaryTurn = new ConversationTurn
            {
                Id = "summary-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Role = "user", // Must be "user" to avoid system role errors
                Content = $"[Context summary of earlier messages]: {summary}",
                Timestamp = discardedMiddleTurns.Last().Timestamp
            };
            // Result: [firstTurn, summaryTurn, keptMiddleTurns..., lastTurn]
            return new[] { firstTurn, summaryTurn }.Concat(keptMiddleTurns).Concat(new[] { lastTurn });
        }

        // All middle turns fit; return [firstTurn, middleTurns..., lastTurn]
        return new[] { firstTurn }.Concat(keptMiddleTurns).Concat(new[] { lastTurn });
    }

    /// <summary>
    /// FIX Feature 4: Guarantees the combined token count of <paramref name="turns"/> never exceeds
    /// <paramref name="maxTokens"/> by truncating the CONTENT of oversized turns (not just dropping
    /// whole turns). The last turn (current user question) is the highest priority to preserve, so it
    /// is kept — truncated to the whole budget if necessary — and earlier turns fill whatever remains.
    /// </summary>
    private static List<ConversationTurn> ClampTurnsToBudget(List<ConversationTurn> turns, int maxTokens)
    {
        if (turns.Count == 0)
            return turns;

        if (maxTokens <= 0)
        {
            // Degenerate budget — return just a hard-truncated last turn so we never send nothing
            // useful, but also never send an over-budget payload.
            return new List<ConversationTurn> { TruncateTurnToTokenBudget(turns[turns.Count - 1], 1) };
        }

        // Fast path: already within budget.
        if (turns.Sum(EstimateTokensForTurnStatic) <= maxTokens)
            return turns;

        // Preserve the last turn (the current question). It may consume the entire budget if it is
        // itself larger than maxTokens — in which case it is truncated down to fit.
        var last = TruncateTurnToTokenBudget(turns[turns.Count - 1], maxTokens);
        var lastTokens = EstimateTokensForTurnStatic(last);

        var remaining = maxTokens - lastTokens;
        var kept = new List<ConversationTurn>();
        var used = 0;

        // Walk earlier turns newest-first, keeping whole turns that fit and truncating the first one
        // that doesn't (older turns beyond that are dropped).
        for (int i = turns.Count - 2; i >= 0; i--)
        {
            if (remaining - used <= 0)
                break;

            var t = turns[i];
            var tks = EstimateTokensForTurnStatic(t);
            if (used + tks <= remaining)
            {
                kept.Insert(0, t);
                used += tks;
            }
            else
            {
                var budget = remaining - used;
                if (budget > 30) // enough headroom to keep a meaningful truncated version
                {
                    kept.Insert(0, TruncateTurnToTokenBudget(t, budget));
                }
                break; // stop — anything older is dropped
            }
        }

        kept.Add(last);
        return kept;
    }

    /// <summary>
    /// Returns a copy of <paramref name="turn"/> whose content fits within <paramref name="maxTokens"/>.
    /// Only plain text content can be safely shrunk, so any serialized content blocks are dropped and
    /// the (already flattened) Content field is truncated. Turns that already fit are returned unchanged.
    /// </summary>
    private static ConversationTurn TruncateTurnToTokenBudget(ConversationTurn turn, int maxTokens)
    {
        if (EstimateTokensForTurnStatic(turn) <= maxTokens)
            return turn;

        var truncated = TruncateContentToTokenBudget(turn.Content ?? string.Empty, maxTokens);
        // Drop serialized blocks: their tokens can't be reduced by truncating Content, and keeping
        // them would leave the turn over budget. The flattened Content is preserved (truncated).
        return turn with { Content = truncated, SerializedContentBlocks = null };
    }

    /// <summary>
    /// Truncates <paramref name="content"/> so its estimated token count is at or below
    /// <paramref name="maxTokens"/>. Keeps the head AND tail of the content (dropping the middle),
    /// because injected @file payloads put the file bodies in the middle and the user's actual
    /// question at the very end — preserving both ends keeps the request coherent.
    /// </summary>
    private static string TruncateContentToTokenBudget(string content, int maxTokens)
    {
        if (maxTokens <= 0) return string.Empty;
        if (string.IsNullOrEmpty(content)) return content;

        var currentTokens = EstimateTokens(content);
        if (currentTokens <= maxTokens)
            return content;

        const string marker = "\n\n[... content truncated to fit the model context window ...]\n\n";

        // Approximate the char budget from the observed chars-per-token ratio, then verify & shrink.
        double charsPerToken = (double)content.Length / Math.Max(currentTokens, 1);
        int charBudget = (int)(maxTokens * charsPerToken);

        if (charBudget <= marker.Length + 40)
        {
            // Budget too small for a head+tail split — hard-truncate the head only.
            var hardCap = Math.Max(0, charBudget - marker.Length);
            var head0 = content.Substring(0, Math.Min(hardCap, content.Length));
            return ShrinkToFit(head0 + marker, string.Empty, maxTokens, marker);
        }

        int usable = charBudget - marker.Length;
        int headLen = usable / 2;
        int tailLen = usable - headLen;

        var head = content.Substring(0, Math.Min(headLen, content.Length));
        var tail = content.Length > tailLen ? content.Substring(content.Length - tailLen) : string.Empty;

        return ShrinkToFit(head, tail, maxTokens, marker);
    }

    /// <summary>
    /// Iteratively shrinks head/tail slices until head+marker+tail fits the token budget.
    /// Guards against the char-per-token estimate under-counting (e.g. dense tokenization).
    /// </summary>
    private static string ShrinkToFit(string head, string tail, int maxTokens, string marker)
    {
        var result = head + marker + tail;
        int guard = 0;
        while (EstimateTokens(result) > maxTokens && guard++ < 12)
        {
            head = head.Length > 0 ? head.Substring(0, (int)(head.Length * 0.8)) : head;
            tail = tail.Length > 0 ? tail.Substring(tail.Length - (int)(tail.Length * 0.8)) : tail;
            if (head.Length + tail.Length < 16)
            {
                result = (head + tail).Length > 0 ? head + tail : marker.Trim();
                break;
            }
            result = head + marker + tail;
        }
        return result;
    }

    public int EstimateTokensForTurn(ConversationTurn turn)
    {
        return EstimateTokensForTurnStatic(turn);
    }

    /// <summary>
    /// Static version of EstimateTokensForTurn so callers that don't hold a ContextCompactor
    /// instance (e.g. ConversationHistory.GetTokenCountAsync) can use the same accurate logic.
    /// </summary>
    public static int EstimateTokensForTurnStatic(ConversationTurn turn)
    {
        if (!string.IsNullOrEmpty(turn.SerializedContentBlocks))
        {
            try
            {
                var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(turn.SerializedContentBlocks!);
                if (blocks != null)
                {
                    int tokens = 0;
                    foreach (var block in blocks)
                    {
                        if (block.Text != null) tokens += EstimateTokens(block.Text);
                        if (block.Arguments != null) tokens += EstimateTokens(System.Text.Json.JsonSerializer.Serialize(block.Arguments));
                        if (block.Result != null) tokens += EstimateTokens(block.Result);
                        tokens += 10; // Overhead per block
                    }
                    return tokens;
                }
            }
            catch { }
        }
        return EstimateTokens(turn.Content);
    }

    /// <summary>
    /// Estimates the number of tokens in a string.
    /// Uses the cached TiktokenTokenizer instance if available (warmed up in the constructor
    /// background task). Falls back to a character-based heuristic (~3.5 chars/token) if the
    /// tokenizer hasn't loaded yet or if it throws — ensuring this method NEVER blocks on I/O.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // FIX #9: Read from the cached instance — no synchronous HTTP, no UI-thread blocking.
        TiktokenTokenizer? tokenizer;
        lock (_tokenizerLock)
        {
            tokenizer = _cachedTokenizer;
        }

        if (tokenizer != null)
        {
            try
            {
                return tokenizer.CountTokens(text);
            }
            catch
            {
                // Fall through to heuristic
            }
        }

        // Heuristic fallback: ~3.5 chars per token (conservative estimate for English/code).
        // Used when the background warm-up hasn't completed yet.
        return (int)Math.Ceiling(text.Length / 3.5);
    }

    /// <summary>
    /// Generates a basic fallback summary when the LLM call fails.
    /// Extracts key information from the conversation turns.
    /// </summary>
    private static string GenerateFallbackSummary(List<ConversationTurn> turns)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Conversation with {turns.Count} messages:");

        // Extract key information
        var userMessages = turns.Where(t => t.Role == "user").ToList();
        var assistantMessages = turns.Where(t => t.Role == "assistant").ToList();

        if (userMessages.Count > 0)
        {
            sb.AppendLine($"- User asked about: {userMessages.First().Content.Substring(0, Math.Min(100, userMessages.First().Content.Length))}...");
        }

        if (assistantMessages.Count > 0)
        {
            var lastResponse = assistantMessages.Last();
            sb.AppendLine($"- Last assistant response: {lastResponse.Content.Substring(0, Math.Min(100, lastResponse.Content.Length))}...");
        }

        // Count code blocks mentioned
        var codeBlockCount = turns.Sum(t => (t.Content?.Split(new[] { "```" }, StringSplitOptions.None).Length - 1) / 2);
        if (codeBlockCount > 0)
            sb.AppendLine($"- {codeBlockCount} code block(s) discussed");

        return sb.ToString();
    }

    public static (bool IsOverBudget, int TotalTokens, int HistoryBudget) ApplyEvictionPolicy(
        List<ConversationTurn> history,
        int systemPromptTokens,
        int toolsTokens,
        string providerId,
        string modelId,
        int maxContextTokens,
        bool forceCompaction = false)
    {
        int effectiveContextLength = 8000; 
        if (providerId.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            if (modelId.IndexOf("ling-3.0-flash-fin:free", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                effectiveContextLength = 15000; 
            }
            else if (modelId.IndexOf(":free", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                effectiveContextLength = Math.Max(8000, maxContextTokens / 2); 
            }
            else
            {
                effectiveContextLength = maxContextTokens;
            }
        }
        else
        {
            effectiveContextLength = maxContextTokens;
        }

        const int outputReserve = 2000;
        const int safetyMargin = 2000;
        int historyBudget = effectiveContextLength - systemPromptTokens - toolsTokens - outputReserve - safetyMargin;

        if (forceCompaction)
        {
            historyBudget = Math.Max(0, historyBudget - 2000); // Force shedding of ~2000 tokens
        }

        var turnTokens = history.Select(t => EstimateTokensForTurnStatic(t)).ToList();
        int historyTokens = turnTokens.Sum();
        int totalTokens = systemPromptTokens + toolsTokens + historyTokens;

        double utilization = historyBudget > 0 ? (double)historyTokens / historyBudget : 1.0;

        if (utilization >= 0.85 || totalTokens > effectiveContextLength - outputReserve - safetyMargin)
        {
            int targetBudget = historyBudget;
            if (historyTokens <= historyBudget && utilization >= 0.85)
            {
                targetBudget = (int)(historyBudget * 0.75); // Target 75% utilization to free up space
            }

            int activeTurnIndex = history.Count - 1; 

            // Priority 1: Stub Oldest Turns
            int i = 0;
            while (historyTokens > targetBudget && i < activeTurnIndex)
            {
                var turnToStub = history[i];
                int oldTurnTokens = turnTokens[i];

                if (turnToStub.Role == "user")
                {
                    var cleanText = System.Text.RegularExpressions.Regex.Replace(turnToStub.Content ?? "", @"<file_context[^>]*>.*?</file_context>\s*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                    history[i] = turnToStub with { Content = $"[turn {i + 1}: user asked \"{cleanText}\"]", SerializedContentBlocks = null };
                }
                else if (turnToStub.Role == "assistant")
                {
                    string stubText = $"[turn {i + 1}: assistant responded";
                    if (!string.IsNullOrEmpty(turnToStub.SerializedContentBlocks))
                    {
                        try
                        {
                            var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(turnToStub.SerializedContentBlocks!);
                            var calls = blocks?.Where(b => !string.IsNullOrEmpty(b.Name)).Select(b => b.Name!).ToList() ?? new List<string>();
                            if (calls.Count > 0) stubText += $" and called {string.Join(", ", calls)}";
                        }
                        catch { }
                    }
                    stubText += "]";
                    history[i] = turnToStub with { Content = stubText, SerializedContentBlocks = null };
                }

                int newTokens = EstimateTokensForTurnStatic(history[i]);
                if (newTokens < oldTurnTokens)
                {
                    turnTokens[i] = newTokens;
                    historyTokens = historyTokens - oldTurnTokens + newTokens;
                    totalTokens = totalTokens - oldTurnTokens + newTokens;
                }
                else
                {
                    history[i] = turnToStub; 
                }
                i++;
            }

            // Priority 2: Compress Stale Tool Results Iteratively
            if (historyTokens > targetBudget)
            {
                for (int j = 0; j < history.Count; j++)
                {
                    if (j == activeTurnIndex) continue;
                    if (historyTokens <= targetBudget) break;

                    var turn = history[j];
                    if (turn.Role == "assistant" && !string.IsNullOrEmpty(turn.SerializedContentBlocks))
                    {
                        try
                        {
                            var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(turn.SerializedContentBlocks!);
                            bool modified = false;
                            if (blocks != null)
                            {
                                foreach (var b in blocks)
                                {
                                    if (b.Result != null && b.Result.Length > 200)
                                    {
                                        b.Result = $"[tool result compressed: originally {b.Result.Length} chars, already read]";
                                        modified = true;
                                    }
                                }
                            }
                            if (modified)
                            {
                                int oldTurnTokens = turnTokens[j];
                                history[j] = turn with { SerializedContentBlocks = System.Text.Json.JsonSerializer.Serialize(blocks) };
                                int newTokens = EstimateTokensForTurnStatic(history[j]);
                                turnTokens[j] = newTokens;
                                historyTokens = historyTokens - oldTurnTokens + newTokens;
                                totalTokens = totalTokens - oldTurnTokens + newTokens;
                            }
                        }
                        catch { }
                    }
                }
            }

            // Priority 3: Truncate Oldest Oversized Turn
            if (historyTokens > targetBudget)
            {
                for (int j = 0; j < history.Count; j++)
                {
                    if (j == activeTurnIndex) continue; // Skip active turn regardless of role
                    if (historyTokens <= targetBudget) break;

                    int oldTurnTokens = turnTokens[j];
                    if (oldTurnTokens > 1000)
                    {
                        var turn = history[j];
                        history[j] = turn with { Content = "[truncated]" + (turn.Content != null && turn.Content.Length > 500 ? turn.Content.Substring(0, 500) : turn.Content) };
                        int newTokens = EstimateTokensForTurnStatic(history[j]);
                        turnTokens[j] = newTokens;
                        historyTokens = historyTokens - oldTurnTokens + newTokens;
                        totalTokens = totalTokens - oldTurnTokens + newTokens;
                    }
                }
            }

            // Priority 4: Truncate Active Turn
            if (historyTokens > targetBudget && activeTurnIndex >= 0)
            {
                var activeTurn = history[activeTurnIndex];
                if (activeTurn.Role == "assistant" && !string.IsNullOrEmpty(activeTurn.SerializedContentBlocks))
                {
                    try
                    {
                        var blocks = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(activeTurn.SerializedContentBlocks!);
                        bool modified = false;
                        if (blocks != null)
                        {
                            foreach (var b in blocks)
                            {
                                if (b.Result != null && b.Result.Length > 1000)
                                {
                                    b.Result = $"[tool result truncated: originally {b.Result.Length} chars] " + b.Result.Substring(0, 1000);
                                    modified = true;
                                }
                            }
                        }
                        if (modified)
                        {
                            int oldTurnTokens = turnTokens[activeTurnIndex];
                            history[activeTurnIndex] = activeTurn with { SerializedContentBlocks = System.Text.Json.JsonSerializer.Serialize(blocks) };
                            int newTokens = EstimateTokensForTurnStatic(history[activeTurnIndex]);
                            turnTokens[activeTurnIndex] = newTokens;
                            historyTokens = historyTokens - oldTurnTokens + newTokens;
                            totalTokens = totalTokens - oldTurnTokens + newTokens;
                        }
                    }
                    catch { }
                }
            }
        }

        bool failFast = totalTokens > effectiveContextLength - outputReserve - safetyMargin;
        return (failFast, totalTokens, historyBudget);
    }
}
