using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using AiAssistant.Engine.Context;
using AiAssistant.Engine.Services;
using AiAssistant.Llm.Services;
using AiAssistant.Core.Services;
using AiAssistant.Core.Models;
using AiAssistant.Engine.Models;

namespace AiAssistant.Engine.Pipeline
{
    public class DeterministicPipelineOrchestrator
    {
        private readonly IWorkspaceContextGatherer _contextGatherer;
        private readonly TokenBudgetManager _tokenBudgetManager;
        private readonly SystemPromptBuilder _promptBuilder;
        private readonly ILlmCompletionClient _llmClient;
        private readonly EditIntentParser _intentParser;
        private readonly IEditApplicationService _editService;

        public DeterministicPipelineOrchestrator(
            IWorkspaceContextGatherer contextGatherer,
            TokenBudgetManager tokenBudgetManager,
            SystemPromptBuilder promptBuilder,
            ILlmCompletionClient llmClient,
            EditIntentParser intentParser,
            IEditApplicationService editService)
        {
            _contextGatherer = contextGatherer;
            _tokenBudgetManager = tokenBudgetManager;
            _promptBuilder = promptBuilder;
            _llmClient = llmClient;
            _intentParser = intentParser;
            _editService = editService;
        }

        public async Task<AiAssistant.Core.Models.EditResult> ExecuteRequestAsync(
            EditorContextData editorData,
            string userRequest,
            CancellationToken ct)
        {
            System.Diagnostics.Debug.WriteLine("\n[PIPELINE] === Starting Pipeline Execution ===");
            
            // 1. Capture State for concurrency and undo
            var preEditState = new EditSessionState(editorData.VersionNumber, editorData.FilePath, System.Array.Empty<Diagnostic>());

            // 2. Gather Context
            var context = await _contextGatherer.GatherContextAsync(editorData, userRequest, ct);
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 1. Context Gathered - ActiveFile: {context.ActiveFilePath}, CursorLine: {context.CursorLine}, Fragments: {context.ContextFiles?.Count ?? 0}");

            // 3. Optimize Tokens
            var trimmedContext = _tokenBudgetManager.TrimToBudget(context, maxTokens: 100000); // Or configured max
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 2. Token Budgeting Complete - Fragments included: {trimmedContext.ContextFiles?.Count ?? 0}");

            // 4. Build Prompt
            var prompt = _promptBuilder.Build(trimmedContext);
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 3. Prompt Built - Total Length: {prompt.Length} characters");

            // 5. Invoke LLM
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var llmResponse = await _llmClient.GetCompletionAsync(prompt, ct);
            sw.Stop();
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 4. LLM Call Complete - Response Length: {llmResponse.Length} characters, Time: {sw.ElapsedMilliseconds} ms");

            // 6. Parse Output
            var parseResult = _intentParser.Parse(llmResponse);
            if (!parseResult.Success)
            {
                System.Diagnostics.Debug.WriteLine($"[PIPELINE] 5. Parse Failed - Error: {parseResult.ErrorMessage}");
                // Abort instantly on parse failure. No retries.
                var errResult = new AiAssistant.Core.Models.EditResult { Status = EditResultStatus.ParseError };
                errResult.FailedEdits.Add(new FailedEdit("Unknown", "Parse", $"Parse failure: {parseResult.ErrorMessage}", null));
                return errResult;
            }
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 5. Parse Success - Edits in intent: {parseResult.Intent.Edits?.Count ?? 0}");

            // 7. Apply Edits
            var editResult = await _editService.ApplyEditsAsync(parseResult.Intent, preEditState, ct);
            System.Diagnostics.Debug.WriteLine($"[PIPELINE] 7. Edit Application Complete - Applied: {editResult.AppliedEdits.Count}, Failed: {editResult.FailedEdits.Count}");

            // 8. Retry Logic (Exactly ONE retry on application failure)
            if (editResult.Status == EditResultStatus.RetryableError)
            {
                // Build retry prompt explicitly pointing out the search miss
                var retryPrompt = prompt + "\n\n" + 
                                  "SYSTEM FEEDBACK: Your previous edit failed because the `search` text was not found in the file. " +
                                  "Your search text was not found in the source code. Copy the search text character-for-character from the source.";
                
                var retryResponse = await _llmClient.GetCompletionAsync(retryPrompt, ct);
                
                var retryParse = _intentParser.Parse(retryResponse);
                if (!retryParse.Success)
                {
                    var errResult = new AiAssistant.Core.Models.EditResult { Status = EditResultStatus.ParseError };
                    errResult.FailedEdits.Add(new FailedEdit("Unknown", "Parse", $"Parse failure on retry: {retryParse.ErrorMessage}", null));
                    return errResult;
                }

                // Apply retry
                editResult = await _editService.ApplyEditsAsync(retryParse.Intent, preEditState, ct);
                if (editResult.Status == EditResultStatus.RetryableError)
                {
                    // Convert second failure into a hard error
                    editResult.Status = EditResultStatus.MatchNotFound;
                }
            }

            return editResult;
        }
    }
}
