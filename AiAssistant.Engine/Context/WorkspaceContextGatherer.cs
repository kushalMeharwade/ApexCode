using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;
using AiAssistant.Llm.Models;
using AiAssistant.Engine.SkeletonEngine;

namespace AiAssistant.Engine.Context
{
    public class WorkspaceContextGatherer : IWorkspaceContextGatherer
    {
        private readonly CSharpParser _csharpParser;

        public WorkspaceContextGatherer()
        {
            _csharpParser = new CSharpParser();
        }

        public async Task<PromptContext> GatherContextAsync(
            EditorContextData editorData,
            string userRequest,
            CancellationToken ct)
        {
            var context = new PromptContext
            {
                UserRequest = userRequest,
                ActiveFilePath = editorData.FilePath,
                ActiveFileContent = editorData.FileContent,
                SelectedText = editorData.SelectedText,
                CursorPosition = editorData.CursorPosition,
                CursorLine = editorData.CursorLine,
                TargetFramework = editorData.TargetFramework,
                NuGetPackages = editorData.NuGetPackages,
                ExistingErrors = editorData.ActiveDiagnostics
            };

            // Skeletonize active file if it's C#
            if (editorData.FilePath.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
            {
                var skeleton = _csharpParser.Parse(editorData.FileContent, editorData.FilePath);
                context.ContextFiles.Add(new ContextFile 
                { 
                    FilePath = editorData.FilePath, 
                    Content = skeleton.Outline
                });
            }

            // We can also attach the resolved symbol definitions directly into the prompt context
            if (!string.IsNullOrEmpty(editorData.SymbolDefinition))
            {
                context.ContextFiles.Add(new ContextFile
                {
                    FilePath = $"Symbol: {editorData.SymbolName}",
                    Content = editorData.SymbolDefinition
                });
            }

            return await Task.FromResult(context);
        }
    }
}

            // Skeletonize files - add active file skeletonized if needed, or other files
            // For Phase 3, we add the skeletonized version of the active file to ContextFiles?
            // Usually ContextFiles are OTHER files, but since ActiveFile is included whole, 
