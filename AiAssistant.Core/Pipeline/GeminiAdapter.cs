using AiAssistant.Core.Models;
using Microsoft.Extensions.AI;
using ChatMessage = AiAssistant.Core.Models.ChatMessage;

namespace AiAssistant.Core.Pipeline;

public sealed class GeminiAdapter : IProviderAdapter
{
    public string ProviderName => "gemini";

    public object BuildRequest(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, ChatOptions options)
    {
        var contents = PureHistory(history).Select(message => (object)new 
        { 
            role = message.Role == "assistant" ? "model" : "user", 
            parts = new[] { new { text = message.Content } } 
        }).ToList();
        
        contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

        if (options.Tools != null && options.Tools.Count > 0)
        {
            var functionDeclarations = options.Tools.OfType<Microsoft.Extensions.AI.AIFunction>().Select(t => new 
            {
                name = t.Name,
                description = t.Description,
                parameters = t.JsonSchema
            }).ToList();
            
            return new 
            { 
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents,
                tools = new[] { new { function_declarations = functionDeclarations } }
            };
        }

        return new 
        { 
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents
        };
    }

    private static IEnumerable<ChatMessage> PureHistory(IEnumerable<ChatMessage> history) =>
        history.Where(message => message.Role is "user" or "assistant");
}
