using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AiAssistant.Llm.Services
{
    public class LlmCompletionClient : ILlmCompletionClient
    {
        private readonly IChatClientFactory _clientFactory;

        public LlmCompletionClient(IChatClientFactory clientFactory)
        {
            _clientFactory = clientFactory;
        }

        public async Task<string> GetCompletionAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var client = _clientFactory.GetDefaultClient();
            var response = await client.GetResponseAsync(new List<ChatMessage> { new ChatMessage(ChatRole.User, prompt) }, null, cancellationToken);
            return response.Text ?? "";
        }
    }
}
