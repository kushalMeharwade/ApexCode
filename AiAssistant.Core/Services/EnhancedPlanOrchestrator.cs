using System;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.Core.Services;

public interface IEnhancedPlanOrchestrator
{
    Task ProcessStateTransitionAsync(Plan plan, CancellationToken cancellationToken);
}

public class EnhancedPlanOrchestrator : IEnhancedPlanOrchestrator
{
    private readonly IPlanService _planService;
    private readonly IChatService _chatService;
    private readonly ISettingsService _settingsService;

    public EnhancedPlanOrchestrator(IPlanService planService, IChatService chatService, ISettingsService settingsService)
    {
        _planService = planService;
        _chatService = chatService;
        _settingsService = settingsService;
    }

    public async Task ProcessStateTransitionAsync(Plan plan, CancellationToken cancellationToken)
    {
        if (!_settingsService.UseEnhancedPlanSystem) return;

        switch (plan.Status)
        {
            case PlanStatus.AwaitingReview:
                // Triggers the UI to display the PlanProposedCard
                _chatService.RaisePlanProposedEvent(plan);
                break;
                
            case PlanStatus.Approved:
                // Inject the system prompt and have the LLM automatically call generate_tasks
                await _chatService.TriggerSystemPromptInjectionAsync(cancellationToken);
                break;
                
            case PlanStatus.TasksGenerated:
                // Transition directly to InProgress when tasks are generated
                var sessionId = _chatService.ActiveSession?.Id;
                if (sessionId != null)
                {
                    await _planService.StartExecutionAsync(sessionId, plan.Id, cancellationToken);
                }
                break;
                
            case PlanStatus.InProgress:
                // Continue execution. The LLM will use start_task and complete_task
                await _chatService.TriggerAutoExecutionStepAsync(cancellationToken);
                break;
                
            case PlanStatus.AwaitingAnswers:
                // Pause execution and ask the user
                _chatService.RaiseQuestionAskedEvent(plan);
                break;
                
            case PlanStatus.Completed:
                // Done. Present walkthrough.
                _chatService.RaiseWalkthroughGeneratedEvent(plan);
                break;
        }
    }
}
