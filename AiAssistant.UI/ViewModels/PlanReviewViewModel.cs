using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AiAssistant.Core.Models;

namespace AiAssistant.UI.ViewModels;

/// <summary>
/// One reviewable step of a proposed plan.
/// </summary>
/// <remarks>
/// This exists because reviewer comments used to live only in the visual tree: the overlay scraped
/// <c>TextBox.Text</c> out of the item template, which meant a comment was lost whenever the
/// template was re-realised and was skipped entirely by the approval path. Binding the comment to a
/// view model makes it the source of truth, independent of what is currently rendered.
/// </remarks>
public sealed class PlanItemViewModel : INotifyPropertyChanged
{
    private string _comment = string.Empty;
    private bool _isCommentVisible;
    private bool _isReadOnly;

    public int StepNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string StepLabel => StepNumber.ToString(CultureInfo.CurrentCulture);

    /// <summary>Reviewer feedback for this step. Bound two-way to the inline comment editor.</summary>
    public string Comment
    {
        get => _comment;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_comment, next, StringComparison.Ordinal)) return;
            _comment = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComment));
            UserInputChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasComment => !string.IsNullOrWhiteSpace(_comment);

    /// <summary>Whether the inline comment editor is expanded. Collapsing never clears the text.</summary>
    public bool IsCommentVisible
    {
        get => _isCommentVisible;
        set
        {
            if (_isCommentVisible == value) return;
            _isCommentVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Set once the plan has been decided, so a past plan can be read but not re-edited.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            if (_isReadOnly == value) return;
            _isReadOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEditable));
        }
    }

    public bool IsEditable => !_isReadOnly;

    /// <summary>Raised when the reviewer edits the comment, so the panel can persist the change.</summary>
    public event EventHandler? UserInputChanged;

    public PlanItem ToModel() => new PlanItem
    {
        Title = Title,
        Description = Description,
        Comment = HasComment ? _comment.Trim() : null,
    };

    public static PlanItemViewModel FromModel(PlanItem item, int stepNumber)
    {
        var comment = item?.Comment ?? string.Empty;
        return new PlanItemViewModel
        {
            StepNumber = stepNumber,
            Title = string.IsNullOrWhiteSpace(item?.Title) ? $"Step {stepNumber}" : item!.Title.Trim(),
            Description = item?.Description?.Trim() ?? string.Empty,
            _comment = comment,
            // An existing comment is shown expanded, otherwise the reviewer would have to hunt for
            // feedback they already wrote in an earlier visit to the overlay.
            _isCommentVisible = !string.IsNullOrWhiteSpace(comment),
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// State of the plan review overlay: the current proposal, where it is in its lifecycle, and what
/// the reviewer is allowed to do with it right now.
/// </summary>
public sealed class PlanReviewViewModel : INotifyPropertyChanged
{
    private PlanReviewStatus _status = PlanReviewStatus.None;
    private int _revision;
    private string _generalComment = string.Empty;
    private bool _isBusy;
    private string _validationMessage = string.Empty;

    public ObservableCollection<PlanItemViewModel> Items { get; } = new();

    /// <summary>Session the current plan belongs to, used to guard against cross-session leakage.</summary>
    public string? SessionId { get; set; }

    public DateTime? ProposedAtUtc { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>Raised whenever the reviewer changes a comment or the general instructions.</summary>
    public event EventHandler? UserInputChanged;

    public PlanReviewStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            foreach (var item in Items) item.IsReadOnly = IsReadOnly;
            OnPropertyChanged();
            RaiseDerived();
        }
    }

    /// <summary>1-based proposal round; 0 means no plan has been proposed yet.</summary>
    public int Revision
    {
        get => _revision;
        set
        {
            if (_revision == value) return;
            _revision = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RevisionLabel));
        }
    }

    /// <summary>Reviewer instructions that are not tied to a specific step.</summary>
    public string GeneralComment
    {
        get => _generalComment;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_generalComment, next, StringComparison.Ordinal)) return;
            _generalComment = next;
            ValidationMessage = string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUserInput));
            UserInputChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Problem to report inside the overlay. The panel's error banner sits underneath this overlay
    /// in the z-order, so a validation message raised there would never be seen.
    /// </summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_validationMessage, next, StringComparison.Ordinal)) return;
            _validationMessage = next;
            OnPropertyChanged();
        }
    }

    /// <summary>True while a turn is streaming, so the reviewer cannot fire a second turn.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            RaiseDerived();
        }
    }

    public bool HasPlan => Items.Count > 0;

    /// <summary>A decided plan is kept for reference but can no longer be edited or re-submitted.</summary>
    public bool IsReadOnly => _status == PlanReviewStatus.Approved || _status == PlanReviewStatus.Archived;

    public bool IsActionable =>
        HasPlan && (_status == PlanReviewStatus.AwaitingReview || _status == PlanReviewStatus.RevisionRequested);

    /// <summary>Drives button enablement: actionable and not already running a turn.</summary>
    public bool CanInteract => IsActionable && !_isBusy;

    public bool HasUserInput =>
        !string.IsNullOrWhiteSpace(_generalComment) || Items.Any(item => item.HasComment);

    public string RevisionLabel => _revision <= 1
        ? "Initial proposal"
        : string.Format(CultureInfo.CurrentCulture, "Revision {0}", _revision);

    public bool HasRevision => _revision > 1;

    public string StatusText => _status switch
    {
        PlanReviewStatus.AwaitingReview => "Awaiting your review",
        PlanReviewStatus.RevisionRequested => "Revision requested",
        PlanReviewStatus.Approved => "Approved — executing in Act mode",
        PlanReviewStatus.Archived => "Reviewed",
        _ => "No plan",
    };

    public Brush StatusBrush => _status switch
    {
        PlanReviewStatus.Approved => Resource("SuccessGreenBrush"),
        PlanReviewStatus.RevisionRequested => Resource("AccentPrimaryBrush"),
        PlanReviewStatus.AwaitingReview => Resource("AccentPrimaryBrush"),
        _ => Resource("TextTertiaryBrush"),
    };

    public string GuidanceText => _status switch
    {
        PlanReviewStatus.Approved =>
            "This plan was approved and handed to Act mode. It is kept here for reference — switch back to Plan mode and ask for a new plan if the approach needs to change.",
        PlanReviewStatus.Archived =>
            "This plan was recovered from the conversation history. It is read-only; ask for a new plan in Plan mode to make changes.",
        PlanReviewStatus.RevisionRequested =>
            "Your feedback was sent. A revised plan will appear here shortly.",
        _ =>
            "Comment on any step to tell the agent what to change. \"Approve & Execute\" switches to Act mode and sends the plan plus your notes; \"Request Revision\" sends your notes back for a new plan without touching any files.",
    };

    /// <summary>Replaces the current proposal with <paramref name="items"/>.</summary>
    public void Load(IReadOnlyList<PlanItem>? items, int revision, PlanReviewStatus status)
    {
        DetachItems();
        Items.Clear();

        if (items != null)
        {
            int step = 1;
            foreach (var item in items)
            {
                if (item == null) continue;
                var vm = PlanItemViewModel.FromModel(item, step++);
                vm.IsReadOnly = status == PlanReviewStatus.Approved || status == PlanReviewStatus.Archived;
                vm.UserInputChanged += OnItemUserInputChanged;
                Items.Add(vm);
            }
        }

        _revision = revision < 1 ? 1 : revision;
        _status = Items.Count == 0 ? PlanReviewStatus.None : status;
        _generalComment = string.Empty;
        _validationMessage = string.Empty;

        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(RevisionLabel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(GeneralComment));
        OnPropertyChanged(nameof(ValidationMessage));
        RaiseDerived();
    }

    public void Clear()
    {
        DetachItems();
        Items.Clear();
        _revision = 0;
        _status = PlanReviewStatus.None;
        _generalComment = string.Empty;
        _validationMessage = string.Empty;
        _isBusy = false;
        SessionId = null;
        ProposedAtUtc = null;
        DecidedAtUtc = null;

        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(RevisionLabel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(GeneralComment));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsBusy));
        RaiseDerived();
    }

    public IReadOnlyList<PlanItem> ToModel() => Items.Select(item => item.ToModel()).ToList();

    /// <summary>Expands the first comment editor, used to point the reviewer at what to fill in.</summary>
    public PlanItemViewModel? RevealFirstCommentEditor()
    {
        var target = Items.FirstOrDefault(item => item.HasComment) ?? Items.FirstOrDefault();
        if (target != null) target.IsCommentVisible = true;
        return target;
    }

    private void DetachItems()
    {
        foreach (var item in Items) item.UserInputChanged -= OnItemUserInputChanged;
    }

    private void OnItemUserInputChanged(object? sender, EventArgs e)
    {
        ValidationMessage = string.Empty;
        OnPropertyChanged(nameof(HasUserInput));
        UserInputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsActionable));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(HasUserInput));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(GuidanceText));
    }

    private static Brush Resource(string key)
    {
        var app = System.Windows.Application.Current;
        var found = app?.TryFindResource(key);
        return found as Brush ?? Brushes.Gray;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
