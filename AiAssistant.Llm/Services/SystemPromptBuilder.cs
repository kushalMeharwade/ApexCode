using System.Text;
using AiAssistant.Llm.Models;

namespace AiAssistant.Llm.Services
{
    public class SystemPromptBuilder
    {
        public string Build(PromptContext context)
        {
            var sb = new StringBuilder();

            // 1. Output Format & Rules
            sb.AppendLine("You are an elite AI coding assistant operating within Visual Studio.");
            sb.AppendLine("You MUST return your response as a valid JSON object matching this exact schema:");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"edits\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"file_path\": \"path/to/file.cs\",");
            sb.AppendLine("      \"search\": \"exact text from source to replace\",");
            sb.AppendLine("      \"replace\": \"new text to insert\"");
            sb.AppendLine("    }");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"explanation\": \"brief explanation of changes\"");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("1. The `search` field MUST be an exact substring copied from the source code provided below. Copy it character-for-character. Do not paraphrase, reformat, or approximate.");
            sb.AppendLine("2. Prefer the SMALLEST possible search scope. Target a single method, statement, or block. Never replace an entire class if only one method needs changing.");
            sb.AppendLine("3. The `file_path` must match one of the file paths provided in the context (use forward slashes).");
            sb.AppendLine("4. The `replace` field can be empty if you are deleting code.");
            sb.AppendLine("5. To edit multiple locations or files, include multiple entries in the `edits` array.");
            sb.AppendLine("6. If you cannot fulfill the request, return an empty `edits` array with an explanation.");
            sb.AppendLine("7. Do NOT output markdown, code fences, explanatory text, or anything outside the JSON object.");
            sb.AppendLine("8. Do NOT add comments like \"// new code\" or \"// added by AI\" in the replacement text.");
            sb.AppendLine("9. Preserve existing code style: indentation, naming conventions, and spacing.");
            sb.AppendLine("10. If adding a new using directive, include it by targeting the existing using block in the search/replace.");
            sb.AppendLine();

            // 2. Environment Context
            if (!string.IsNullOrEmpty(context.TargetFramework))
            {
                sb.AppendLine($"Target Framework: {context.TargetFramework}");
            }
            if (context.NuGetPackages != null && context.NuGetPackages.Count > 0)
            {
                sb.AppendLine("Available NuGet Packages (do not hallucinate others):");
                foreach (var pkg in context.NuGetPackages)
                {
                    sb.AppendLine($"- {pkg}");
                }
                sb.AppendLine();
            }

            // 3. Existing Errors
            if (context.ExistingErrors != null && context.ExistingErrors.Count > 0)
            {
                sb.AppendLine("Existing Compiler Errors (before your edits):");
                foreach (var err in context.ExistingErrors)
                {
                    sb.AppendLine($"- {err}");
                }
                sb.AppendLine();
            }

            // 4. Cursor and Selection
            if (!string.IsNullOrEmpty(context.SelectedText))
            {
                sb.AppendLine("User's Selected Text:");
                sb.AppendLine("```");
                sb.AppendLine(context.SelectedText);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (context.CursorLine > 0)
            {
                sb.AppendLine($"User's Cursor is currently near line {context.CursorLine} in the active file.");
                sb.AppendLine();
            }

            // 5. Context Files (Tier 2 & 3)
            if (context.ContextFiles != null && context.ContextFiles.Count > 0)
            {
                sb.AppendLine("--- CONTEXT FILES (For Reference) ---");
                foreach (var cf in context.ContextFiles)
                {
                    sb.AppendLine($"File: {cf.FilePath}");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(cf.Content);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }

            // 6. Source Code (Active File)
            sb.AppendLine("--- ACTIVE FILE (Target for Edits) ---");
            sb.AppendLine($"File: {context.ActiveFilePath}");
            sb.AppendLine("```csharp");
            sb.AppendLine(context.ActiveFileContent);
            sb.AppendLine("```");
            sb.AppendLine();

            // 7. User Request
            sb.AppendLine("--- USER REQUEST ---");
            sb.AppendLine(context.UserRequest);
            
            return sb.ToString();
        }

        public string BuildRetryPrompt(string originalSource, string errorMessage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Your previous edit was REJECTED.");
            sb.AppendLine($"Reason: {errorMessage}");
            sb.AppendLine();
            sb.AppendLine("You MUST return your response as a valid JSON object matching this exact schema:");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            sb.AppendLine("  \"edits\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"file_path\": \"path/to/file.cs\",");
            sb.AppendLine("      \"search\": \"exact text from source to replace\",");
            sb.AppendLine("      \"replace\": \"new text to insert\"");
            sb.AppendLine("    }");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"explanation\": \"brief explanation of changes\"");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("1. The `search` field MUST be an exact substring copied from the source code provided below.");
            sb.AppendLine("2. Do NOT output anything outside the JSON object.");
            sb.AppendLine();
            sb.AppendLine("--- ACTIVE FILE ---");
            sb.AppendLine("```csharp");
            sb.AppendLine(originalSource);
            sb.AppendLine("```");
            
            return sb.ToString();
        }
    }
}
