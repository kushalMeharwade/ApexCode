using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AiAssistant.Core.Models;
using AiAssistant.UI.Helpers;

namespace AiAssistant.UI.ViewModels;

public class PlanProposedCardViewModel : IMessageElement
{
    public EnhancedPlanReviewPanelViewModel ReviewPanelViewModel { get; }
    public string PlanId { get; }

    public PlanProposedCardViewModel(Plan plan, Func<System.Threading.Tasks.Task<bool>> onApprove, Action<string> onRequestChanges)
    {
        PlanId = plan.Id;
        ReviewPanelViewModel = new EnhancedPlanReviewPanelViewModel(plan, onApprove, onRequestChanges);
    }
}

public class WalkthroughCardViewModel : IMessageElement
{
    public Plan Plan { get; }
    public ICommand OpenWalkthroughCommand { get; }
    
    public WalkthroughCardViewModel(Plan plan)
    {
        Plan = plan;
        OpenWalkthroughCommand = new RelayCommand(() => { /* Logic to open walkthrough */ });
    }
}


public class QuestionCardViewModel : IMessageElement, INotifyPropertyChanged
{
    public AgentQuestion Question { get; }
    public string TaskTitle { get; }
    public ObservableCollection<OptionItemViewModel> Options { get; } = new();

    private bool _isCustomSelected;
    public bool IsCustomSelected
    {
        get => _isCustomSelected;
        set
        {
            if (_isCustomSelected != value)
            {
                _isCustomSelected = value;
                OnPropertyChanged();
                if (value)
                {
                    // Deselect other options
                    foreach (var opt in Options) opt.IsSelected = false;
                }
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string _customAnswerText = string.Empty;
    public string CustomAnswerText
    {
        get => _customAnswerText;
        set
        {
            if (_customAnswerText != value)
            {
                _customAnswerText = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly != value)
            {
                _isReadOnly = value;
                OnPropertyChanged();
            }
        }
    }

    public string SubmitLabel => !string.IsNullOrEmpty(Question.SubmitLabel) ? Question.SubmitLabel : "Submit answer";
    
    // For rendering the final read-only state
    public string FinalAnswerText => Question.State == QuestionState.Cancelled ? "Cancelled" : (Question.Answer ?? string.Empty);

    public ICommand SubmitCommand { get; }
    public ICommand CancelCommand { get; }

    public QuestionCardViewModel(AgentQuestion question, string taskTitle, Action<AgentQuestionResult> onResult)
    {
        Question = question;
        TaskTitle = taskTitle;
        IsReadOnly = question.State != QuestionState.Pending;

        foreach (var optText in question.Options ?? new List<string>())
        {
            var opt = new OptionItemViewModel { Text = optText };
            opt.PropertyChanged += (s, e) => 
            {
                if (e.PropertyName == nameof(OptionItemViewModel.IsSelected) && opt.IsSelected)
                {
                    IsCustomSelected = false;
                    foreach (var other in Options.Where(x => x != opt)) other.IsSelected = false;
                    CommandManager.InvalidateRequerySuggested();
                }
            };
            Options.Add(opt);
        }

        SubmitCommand = new RelayCommand(
            execute: () => 
            {
                IsReadOnly = true;
                var selectedText = IsCustomSelected ? CustomAnswerText : Options.FirstOrDefault(o => o.IsSelected)?.Text;
                Question.Answer = selectedText;
                
                var result = new AgentQuestionResult
                {
                    WasCancelled = false,
                    SelectedOption = IsCustomSelected ? null : selectedText,
                    CustomText = IsCustomSelected ? CustomAnswerText : null
                };
                onResult(result);
                OnPropertyChanged(nameof(FinalAnswerText));
            },
            canExecute: () => !IsReadOnly && (Options.Any(o => o.IsSelected) || (IsCustomSelected && !string.IsNullOrWhiteSpace(CustomAnswerText)))
        );

        CancelCommand = new RelayCommand(
            execute: () => 
            {
                IsReadOnly = true;
                var result = new AgentQuestionResult { WasCancelled = true };
                onResult(result);
                OnPropertyChanged(nameof(FinalAnswerText));
            },
            canExecute: () => !IsReadOnly
        );
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for the execution summary card shown at the end of a parallel execution batch.
/// Displays a breakdown of completed / failed tasks and exposes retry/skip commands per failed task.
/// </summary>
public class ExecutionSummaryCardViewModel : IMessageElement
{
    public Plan Plan { get; }
    public int TotalTasks { get; }
    public int CompletedTasks { get; }
    public int FailedTasks { get; }
    public bool HasFailures => FailedTasks > 0;
    public bool IsFullySuccessful => FailedTasks == 0;

    /// <summary>Subset of tasks that ended in a Failed state after all retries.</summary>
    public IReadOnlyList<PlanTask> FailedTaskList { get; }

    public ExecutionSummaryCardViewModel(Plan plan)
    {
        Plan = plan;
        var tasks = plan.Tasks ?? new System.Collections.Generic.List<PlanTask>();
        TotalTasks = tasks.Count;
        CompletedTasks = tasks.Count(t => t.Status == TaskStatus.Completed);
        FailedTasks = tasks.Count(t => t.Status == TaskStatus.Failed);
        FailedTaskList = tasks.Where(t => t.Status == TaskStatus.Failed).ToList();
    }
}
