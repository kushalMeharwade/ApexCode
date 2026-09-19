using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.Tools.Functions.Act;

public class AskQuestionTool : CustomAIFunction
{
    private readonly IPlanService _planService;
    private readonly IServiceProvider _serviceProvider;

    public AskQuestionTool(IPlanService planService, IServiceProvider serviceProvider) : base(
        name: "ask_question",
        description: "Halts execution and asks the user a question. Use this if you are blocked or need clarification.",
        jsonSchemaString: """
        {
            "type": "object",
            "properties": {
                "taskId": { "type": "string", "description": "Optional. The ID of the task you are currently blocked on, if any." },
                "question": { "type": "string", "description": "The question to ask the user." },
                "options": { "type": "array", "items": { "type": "string" }, "description": "Optional list of predefined choices for the user to select from." },
                "allowCustomAnswer": { "type": "boolean", "description": "Whether to allow the user to type a custom answer. Defaults to true." },
                "submitLabel": { "type": "string", "description": "Optional custom text for the submit button. Defaults to 'Submit answer'." }
            },
            "required": ["question"],
            "additionalProperties": false
        }
        """)
    {
        _planService = planService;
        _serviceProvider = serviceProvider;
    }

    protected override async Task<object?> InvokeCoreImplAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var chatService = (IChatService)_serviceProvider.GetService(typeof(IChatService))!;
        var sessionId = chatService.ActiveSession?.Id ?? "default";

        var plan = await _planService.GetActivePlanAsync(sessionId, cancellationToken);
        var planId = plan?.Id ?? "active";

        var taskId = arguments.TryGetValue("taskId", out var t) && t != null ? t.ToString() : string.Empty;
        var questionText = arguments.TryGetValue("question", out var q) && q != null ? q.ToString() : string.Empty;
        
        if (string.IsNullOrEmpty(questionText)) return "Error: question is required.";

        List<string> optionsList = new();
        if (arguments.TryGetValue("options", out var opts) && opts != null)
        {
            if (opts is IEnumerable<object> list)
            {
                optionsList = new List<string>(System.Linq.Enumerable.Select(list, o => o.ToString()!));
            }
            else if (opts is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in je.EnumerateArray()) optionsList.Add(item.GetString() ?? item.ToString());
                }
                else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var strVal = je.GetString();
                    if (!string.IsNullOrWhiteSpace(strVal))
                    {
                        try {
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(strVal);
                            if (parsed != null) optionsList.AddRange(parsed);
                        } catch { }
                    }
                }
            }
            else if (opts is string strOpts)
            {
                try {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(strOpts);
                    if (parsed != null) optionsList.AddRange(parsed);
                } catch { }
            }
        }

        bool allowCustom = true;
        if (arguments.TryGetValue("allowCustomAnswer", out var aca) && aca != null)
        {
            if (aca is bool b) allowCustom = b;
            else if (aca is string sAca && bool.TryParse(sAca, out var parsedS)) allowCustom = parsedS;
            else if (aca is System.Text.Json.JsonElement jeBool)
            {
                if (jeBool.ValueKind == System.Text.Json.JsonValueKind.True || jeBool.ValueKind == System.Text.Json.JsonValueKind.False)
                    allowCustom = jeBool.GetBoolean();
                else if (jeBool.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(jeBool.GetString(), out var parsedB))
                    allowCustom = parsedB;
            }
        }

        string submitLbl = "Submit answer";
        if (arguments.TryGetValue("submitLabel", out var sl) && sl != null)
        {
            if (sl is string s) submitLbl = s;
            else if (sl is System.Text.Json.JsonElement jeString && jeString.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                submitLbl = jeString.GetString() ?? "Submit answer";
            }
        }

        var agentQuestion = new AgentQuestion
        {
            TaskId = taskId,
            Question = questionText,
            Options = optionsList,
            AllowCustomAnswer = allowCustom,
            SubmitLabel = submitLbl
        };

        var result = await _planService.AskQuestionAsync(sessionId, planId, agentQuestion, cancellationToken);
        
        if (result.WasCancelled)
        {
            return "The user cancelled the question. You should reconsider your approach or try something else.";
        }
        
        return $"The user responded with: {result.SelectedOption ?? result.CustomText}";
    }
}


