using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Llm.Models;

namespace AiAssistant.Engine.Context
{
    public interface IWorkspaceContextGatherer
    {
        Task<PromptContext> GatherContextAsync(EditorContextData editorData, string userRequest, CancellationToken ct = default);
    }
}
