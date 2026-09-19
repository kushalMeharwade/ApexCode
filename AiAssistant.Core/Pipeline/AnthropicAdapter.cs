using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Core.Pipeline;

public sealed class AnthropicAdapter : IProviderAdapter
{
    public string ProviderName => "anthropic";

    public object BuildRequest(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, ChatOptions options)
    {
        var messages = PureHistory(history).Select(message => (object)new { role = message.Role, content = message.Content }).ToList();
        messages.Add(new { role = "user", content = userMessage });
        
        if (options.Tools != null && options.Tools.Count > 0)
        {
            var tools = options.Tools.OfType<Microsoft.Extensions.AI.AIFunction>().Select(t => new 
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.JsonSchema
            }).ToList();
            
            return new { system = systemPrompt, messages, tools };
        }

        return new { system = systemPrompt, messages };
    }

    private static IEnumerable<ChatMessage> PureHistory(IEnumerable<ChatMessage> history) =>
        history.Where(message => message.Role is "user" or "assistant");
}
