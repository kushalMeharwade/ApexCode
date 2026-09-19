using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiAssistant.UI.ViewModels;

public class HistoryItemViewModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _firstPrompt = "";
    private DateTime _updatedAt;
    private bool _isEditing;

    public string Id { get; init; } = "";
    
    public string GroupName { get; set; } = "";

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether this item is currently showing an inline rename TextBox instead of
    /// its static name TextBlock.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The first user prompt sent to the AI agent in this conversation.
    /// </summary>
    public string FirstPrompt
    {
        get => _firstPrompt;
        set { _firstPrompt = value; OnPropertyChanged(); }
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { _updatedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(RelativeTime)); }
    }

    public string RelativeTime => GetRelativeTime(_updatedAt);

    private static string GetRelativeTime(DateTime time)
    {
        var localTime = time.Kind == DateTimeKind.Local ? time : 
            DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime();
        var span = DateTime.Now - localTime;

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 2) return "Yesterday";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        return $"{(int)(span.TotalDays / 30)}mo ago";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
