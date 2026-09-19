using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Llm.Services
{
    public interface ILlmCompletionClient
    {
        Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default);
    }
}
