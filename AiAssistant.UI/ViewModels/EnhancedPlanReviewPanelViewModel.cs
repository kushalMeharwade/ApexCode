using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using AiAssistant.Core.Models;

namespace AiAssistant.UI.ViewModels;

// A simple ICommand implementation
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
    public void Execute(object parameter) => _execute();
}

public class EnhancedPlanReviewPanelViewModel : ViewModelBase
{
    private readonly Plan _plan;
    private readonly Func<System.Threading.Tasks.Task<bool>> _onApprove;
    private readonly Action<string> _onRequestChanges;
    private string _generalFeedback;

    public EnhancedPlanReviewPanelViewModel(Plan plan, Func<System.Threading.Tasks.Task<bool>> onApprove, Action<string> onRequestChanges)
    {
        _plan = plan;
        _onApprove = onApprove;
        _onRequestChanges = onRequestChanges;

        Title = plan.Title;
        Summary = plan.Summary;
        
        // Status formatting
        if (plan.Status == PlanStatus.AwaitingReview)
        {
            StatusText = "[Initial Proposal] Awaiting your review";
            BadgeText = "Initial Proposal";
            StatusMessage = "Awaiting your review";
            StatusBrush = (Brush)Application.Current.TryFindResource("InfoBrush") ?? Brushes.CornflowerBlue;
        }
        else if (plan.Status == PlanStatus.Approved || plan.Status == PlanStatus.InProgress || plan.Status == PlanStatus.Completed)
        {
            StatusText = $"[{plan.Status}] Plan approved";
            BadgeText = plan.Status.ToString();
            StatusMessage = "Plan approved";
            StatusBrush = (Brush)Application.Current.TryFindResource("SuccessBrush") ?? Brushes.MediumSeaGreen;
            IsReadOnly = true;
            IsCollapsed = true;
        }
        else if (plan.Status == PlanStatus.Superseded)
        {
            SetSuperseded();
        }
        else
        {
            StatusText = $"[{plan.Status}]";
            BadgeText = plan.Status.ToString();
            StatusMessage = string.Empty;
            StatusBrush = (Brush)Application.Current.TryFindResource("WarningBrush") ?? Brushes.Goldenrod;
        }

        if (plan.Revision > 1)
        {
            HasRevision = true;
            RevisionLabel = $"Revision {plan.Revision}";
            if (plan.Status == PlanStatus.AwaitingReview)
            {
                StatusText = $"[Revision {plan.Revision}] Awaiting your review";
                BadgeText = $"Revision {plan.Revision}";
            }
        }
        else
        {
            HasRevision = false;
            RevisionLabel = string.Empty;
        }

        ImportantChanges = new ObservableCollection<PlanSectionRowViewModel>(
            plan.ImportantChanges.Select((x, i) => new PlanSectionRowViewModel { Text = x, IndexText = $"{i + 1}." }));

        ModifiedFiles = new ObservableCollection<PlanFileRowViewModel>(
            plan.ModifiedFiles.Select((x, i) => new PlanFileRowViewModel 
            { 
                Action = $"[{x.Action.ToUpper()}]", 
                Path = x.Path, 
                Description = x.Description,
                ActionBadgeColor = GetBadgeColor(x.Action),
                IndexText = $"{i + 1}."
            }));

        Risks = new ObservableCollection<PlanSectionRowViewModel>(
            plan.Risks.Select((x, i) => new PlanSectionRowViewModel { Text = x, IndexText = $"{i + 1}." }));

        Decisions = new ObservableCollection<PlanSectionRowViewModel>(
            plan.Decisions.Select((x, i) => new PlanSectionRowViewModel { Text = x, IndexText = $"{i + 1}." }));

        OpenQuestions = new ObservableCollection<PlanSectionRowViewModel>(
            plan.OpenQuestions.Select((x, i) => new PlanSectionRowViewModel { Text = x.Question, IndexText = $"{i + 1}." }));

        // Commands
        ProceedCommand = new RelayCommand(OnProceed);
        RequestChangesCommand = new RelayCommand(OnRequestChanges, CanRequestChanges);
        CopyCommand = new RelayCommand(OnCopy);
        DownloadCommand = new RelayCommand(OnDownload);
    }

    private Brush GetBadgeColor(string action)
    {
        return action?.ToLower() switch
        {
            "create" => (Brush)Application.Current.TryFindResource("SuccessBrush") ?? Brushes.MediumSeaGreen,
            "modify" => (Brush)Application.Current.TryFindResource("InfoBrush") ?? Brushes.CornflowerBlue,
            "delete" => (Brush)Application.Current.TryFindResource("ErrorBrush") ?? Brushes.IndianRed,
            _ => (Brush)Application.Current.TryFindResource("TextSecondaryBrush") ?? Brushes.Gray
        };
    }

    public string Title { get; }
    public string Summary { get; }

    private string _statusText;
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

    private string _badgeText;
    public string BadgeText { get => _badgeText; set { _badgeText = value; OnPropertyChanged(); } }

    private string _statusMessage;
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

    private Brush _statusBrush;
    public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; OnPropertyChanged(); } }

    private bool _isReadOnly;
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { _isReadOnly = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsInteractive)); }
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsInteractive)); }
    }

    public bool IsInteractive => !IsReadOnly && !IsProcessing;

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { _isCollapsed = value; OnPropertyChanged(); }
    }

    public bool IsAwaitingReview => _plan.Status == PlanStatus.AwaitingReview && !IsReadOnly;

    private bool _hasRevision;
    public bool HasRevision
    {
        get => _hasRevision;
        set { _hasRevision = value; OnPropertyChanged(); }
    }

    private string _revisionLabel;
    public string RevisionLabel
    {
        get => _revisionLabel;
        set { _revisionLabel = value; OnPropertyChanged(); }
    }

    public void SetSuperseded()
    {
        IsReadOnly = true;
        StatusText = "[Superseded]";
        BadgeText = "Superseded";
        StatusMessage = "Superseded — see latest plan below";
        StatusBrush = (Brush)Application.Current.TryFindResource("TextSecondaryBrush") ?? Brushes.Gray;
    }

    public ObservableCollection<PlanSectionRowViewModel> ImportantChanges { get; }
    public ObservableCollection<PlanFileRowViewModel> ModifiedFiles { get; }
    public ObservableCollection<PlanSectionRowViewModel> Risks { get; }
    public ObservableCollection<PlanSectionRowViewModel> Decisions { get; }
    public ObservableCollection<PlanSectionRowViewModel> OpenQuestions { get; }

    public bool HasImportantChanges => ImportantChanges.Count > 0;
    public bool HasModifiedFiles => ModifiedFiles.Count > 0;
    public bool HasRisks => Risks.Count > 0;
    public bool HasDecisions => Decisions.Count > 0;
    public bool HasOpenQuestions => OpenQuestions.Count > 0;

    public string GeneralFeedback
    {
        get => _generalFeedback;
        set { _generalFeedback = value; OnPropertyChanged(); }
    }

    private string _copyButtonText = "Copy";
    public string CopyButtonText
    {
        get => _copyButtonText;
        set { _copyButtonText = value; OnPropertyChanged(); }
    }

    private string _errorMessage;
    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public ICommand ProceedCommand { get; }
    public ICommand RequestChangesCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand DownloadCommand { get; }

    private async void OnProceed()
    {
        try
        {
            IsProcessing = true;
            if (_onApprove != null)
            {
                bool success = await _onApprove.Invoke();
                if (success)
                {
                    IsReadOnly = true;
                    IsCollapsed = true;
                    StatusText = $"[Approved] Plan approved";
                    BadgeText = "Approved";
                    StatusMessage = "Plan approved";
                    StatusBrush = (Brush)Application.Current.TryFindResource("SuccessBrush") ?? Brushes.MediumSeaGreen;
                }
                else
                {
                    ErrorMessage = "Failed to approve plan. This plan may no longer be active.";
                }
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private bool CanRequestChanges()
    {
        bool hasGeneral = !string.IsNullOrWhiteSpace(GeneralFeedback);
        bool hasRowComments = ImportantChanges.Any(x => !string.IsNullOrWhiteSpace(x.Comment)) ||
                              ModifiedFiles.Any(x => !string.IsNullOrWhiteSpace(x.Comment)) ||
                              Risks.Any(x => !string.IsNullOrWhiteSpace(x.Comment)) ||
                              Decisions.Any(x => !string.IsNullOrWhiteSpace(x.Comment));
        return hasGeneral || hasRowComments;
    }

    private void OnRequestChanges()
    {
        var feedbackBuilder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(GeneralFeedback))
        {
            feedbackBuilder.AppendLine($"General Feedback: {GeneralFeedback}");
        }

        void AppendSectionFeedback(string sectionName, System.Collections.IEnumerable items)
        {
            bool headerAdded = false;
            foreach (var item in items)
            {
                if (item is PlanSectionRowViewModel vm && !string.IsNullOrWhiteSpace(vm.Comment))
                {
                    if (!headerAdded)
                    {
                        feedbackBuilder.AppendLine($"\\nFeedback on {sectionName}:");
                        headerAdded = true;
                    }
                    if (vm is PlanFileRowViewModel fileVm)
                    {
                        feedbackBuilder.AppendLine($"- File '{fileVm.Path}': {vm.Comment}");
                    }
                    else
                    {
                        feedbackBuilder.AppendLine($"- Regarding '{vm.Text}': {vm.Comment}");
                    }
                    vm.IsSent = true;
                }
            }
        }

        AppendSectionFeedback("Important Changes", ImportantChanges);
        AppendSectionFeedback("Modified Files", ModifiedFiles);
        AppendSectionFeedback("Risks", Risks);
        AppendSectionFeedback("Decisions", Decisions);

        var finalFeedback = feedbackBuilder.ToString();
        _onRequestChanges?.Invoke(finalFeedback);
    }

    private async void OnCopy()
    {
        Clipboard.SetText(Helpers.PlanMarkdownHelper.GenerateMarkdown(_plan));
        CopyButtonText = "Copied!";
        await System.Threading.Tasks.Task.Delay(2000);
        CopyButtonText = "Copy";
    }

    private void OnDownload()
    {
        string safeTitle = string.IsNullOrWhiteSpace(_plan.Title) ? "plan" : string.Join("_", _plan.Title.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"plan_{safeTitle}.md",
            DefaultExt = ".md",
            Filter = "Markdown documents (.md)|*.md"
        };

        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, Helpers.PlanMarkdownHelper.GenerateMarkdown(_plan));
        }
    }
}
