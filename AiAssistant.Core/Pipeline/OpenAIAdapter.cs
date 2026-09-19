using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Core.Pipeline;

public sealed class OpenAIAdapter : IProviderAdapter
{
    public string ProviderName => "openai";

    public object BuildRequest(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, ChatOptions options)
    {
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(PureHistory(history).Select(message => (object)new { role = message.Role, content = message.Content }));
        messages.Add(new { role = "user", content = userMessage });
        
        if (options.Tools != null && options.Tools.Count > 0)
        {
            var tools = options.Tools.OfType<Microsoft.Extensions.AI.AIFunction>().Select(t => new 
            {
                type = "function",
                function = new 
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.JsonSchema
                }
            }).ToList();
            
            return new { messages, tools };
        }

        return new { messages };
    }

    private static IEnumerable<ChatMessage> PureHistory(IEnumerable<ChatMessage> history) =>
        history.Where(message => message.Role is "user" or "assistant");
}
