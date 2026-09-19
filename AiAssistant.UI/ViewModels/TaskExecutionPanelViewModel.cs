using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.UI.ViewModels
{
    public class TaskExecutionPanelViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private readonly IPlanService _planService;
        private readonly IChatService _chatService;
        
        private Plan? _plan;
        public Plan? Plan
        {
            get => _plan;
            set
            {
                if (_plan != value)
                {
                    if (_plan != null) _plan.PropertyChanged -= Plan_PropertyChanged;
                    _plan = value;
                    if (_plan != null) _plan.PropertyChanged += Plan_PropertyChanged;
                    
                }
                // Plan updates may reuse the same instance after mutating its task list.
                OnPropertyChanged();
                RefreshExecutionState();
            }
        }

        public bool HasActiveExecution => _plan?.Tasks?.Count > 0 &&
            (_plan.Status == PlanStatus.TasksGenerated ||
             _plan.Status == PlanStatus.InProgress ||
             _plan.Status == PlanStatus.AwaitingAnswers ||
             _plan.Status == PlanStatus.Completed);

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        private void Plan_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(Plan.Status) ||
                e.PropertyName == nameof(Plan.Tasks) || e.PropertyName == nameof(Plan.TaskProgressValue))
            {
                RefreshExecutionState();
            }
        }

        private void RefreshExecutionState()
        {
            OnPropertyChanged(nameof(Tasks));
            OnPropertyChanged(nameof(TaskProgressValue));
            OnPropertyChanged(nameof(HasFailedTask));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(HasActiveExecution));
            OnPropertyChanged(nameof(CurrentTaskTitle));
            OnPropertyChanged(nameof(CurrentTaskSummary));
        }

        public System.Collections.Generic.IEnumerable<PlanTask>? Tasks => _plan?.Tasks;
        public double TaskProgressValue => _plan?.TaskProgressValue ?? 0;
        public bool HasFailedTask => _plan?.Tasks?.Any(t => t.Status == AiAssistant.Core.Models.TaskStatus.Failed || t.Status == AiAssistant.Core.Models.TaskStatus.Interrupted) ?? false;
        public bool IsRunning => _plan?.Tasks?.Any(t => t.Status == AiAssistant.Core.Models.TaskStatus.InProgress) ?? false;

        public string CurrentTaskTitle
        {
            get
            {
                if (_plan?.Tasks == null || _plan.Tasks.Count == 0) return "No Tasks";
                var tasks = _plan.Tasks;
                var currentIndex = tasks.FindIndex(t => t.Status != AiAssistant.Core.Models.TaskStatus.Completed && t.Status != AiAssistant.Core.Models.TaskStatus.Skipped);
                if (currentIndex == -1) return "All Tasks Completed";
                return $"Task {currentIndex + 1} of {tasks.Count}";
            }
        }

        public string CurrentTaskSummary
        {
            get
            {
                if (_plan?.Tasks == null || _plan.Tasks.Count == 0) return string.Empty;
                var tasks = _plan.Tasks;
                var currentTask = tasks.FirstOrDefault(t => t.Status != AiAssistant.Core.Models.TaskStatus.Completed && t.Status != AiAssistant.Core.Models.TaskStatus.Skipped);
                return currentTask?.Title ?? "Done";
            }
        }

        public ICommand RetryTaskCommand { get; }
        public ICommand SkipTaskCommand { get; }
        public ICommand AbortPlanCommand { get; }
        public ICommand ViewPlanCommand { get; }

        public TaskExecutionPanelViewModel(IPlanService planService, IChatService chatService)
        {
            _planService = planService;
            _chatService = chatService;

            RetryTaskCommand = new AiAssistant.UI.Helpers.RelayCommand(p => ExecuteRetryTask(p as string));
            SkipTaskCommand = new AiAssistant.UI.Helpers.RelayCommand(p => ExecuteSkipTask(p as string));
            AbortPlanCommand = new AiAssistant.UI.Helpers.RelayCommand(_ => ExecuteAbortPlan());
            ViewPlanCommand = new AiAssistant.UI.Helpers.RelayCommand(_ => ExecuteViewPlan());
        }

        private async void ExecuteRetryTask(string? taskId)
        {
            if (_plan == null || string.IsNullOrEmpty(taskId)) return;
            var task = _plan.Tasks.Find(t => t.Id == taskId);
            if (task == null) return;

            task.Status = AiAssistant.Core.Models.TaskStatus.Pending;
            task.FailureMessage = null;
            task.AttemptNumber++;
            
            var sessionId = _chatService.ActiveSession?.Id ?? "default";
            // We should ideally have an UpdateTask method in IPlanService, but for now we can just use the memory reference since PlanWorkspaceService shares the instance if memory is alive.
            
            await _chatService.SendMessageAsync(sessionId, 
                $"[System] The user chose to RETRY task '{task.Title}'. Attempt {task.AttemptNumber}. Re-evaluate the file state and use start_task to begin.", 
                null, CancellationToken.None);
        }

        private async void ExecuteSkipTask(string? taskId)
        {
            if (_plan == null || string.IsNullOrEmpty(taskId)) return;
            var task = _plan.Tasks.Find(t => t.Id == taskId);
            if (task == null) return;

            task.Status = AiAssistant.Core.Models.TaskStatus.Skipped;
            task.FailureMessage = null;

            var sessionId = _chatService.ActiveSession?.Id ?? "default";
            
            await _chatService.SendMessageAsync(sessionId, 
                $"[System] The user chose to SKIP task '{task.Title}'. Any partial edits remain as-is. Proceed to the next task in the plan.", 
                null, CancellationToken.None);
        }

        public async System.Threading.Tasks.Task AbortPlanAsync()
        {
            if (_plan == null) return;
            
            var failedTask = _plan.Tasks?.FirstOrDefault(t => t.Status == AiAssistant.Core.Models.TaskStatus.Failed || t.Status == AiAssistant.Core.Models.TaskStatus.Interrupted);
            string taskContext = failedTask != null ? $" Task '{failedTask.Title}' was interrupted/failed and may have left partial edits." : "";
            
            // Mark all Pending/Running tasks as Skipped/Aborted
            var sessionId = _chatService.ActiveSession?.Id ?? "default";
            
            await _chatService.SendMessageAsync(sessionId, 
                $"[System] The user chose to ABORT the remaining tasks in the plan.{taskContext} Provide a summary of what was completed and warn the user about any files that may have been left in a broken state.", 
                null, CancellationToken.None);
        }

        private async void ExecuteAbortPlan()
        {
            await AbortPlanAsync();
        }

        private void ExecuteViewPlan()
        {
            // Triggers the UI to display the plan overview inline
            if (_plan != null)
            {
                _chatService.RaiseScrollToPlanEvent(_plan.Id);
            }
        }
    }
}
