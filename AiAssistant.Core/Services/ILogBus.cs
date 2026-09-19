using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiAssistant.Core.Services;

public enum LogCategory
{
    Chat,
    Llm,
    Tool,
    Agent,
    Settings
}

public record LogEntry(
    DateTime Timestamp,
    LogCategory Category,
    string Message,
    string? Detail = null
);

public class LlmTransaction : INotifyPropertyChanged
{
    private string _status = "Running...";
    private string? _responsePayload;
    private string? _error;
    private TimeSpan? _duration;
    private int? _promptTokens;
    private int? _completionTokens;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public string Provider { get; }
    public string Model { get; }
    public string RequestPayload { get; set; }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    public string? ResponsePayload
    {
        get => _responsePayload;
        set { if (_responsePayload != value) { _responsePayload = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResponseOrError)); } }
    }

    public string? Error
    {
        get => _error;
        set { if (_error != value) { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResponseOrError)); } }
    }

    public string? ResponseOrError => !string.IsNullOrEmpty(_responsePayload) ? _responsePayload : _error;

    public TimeSpan? Duration
    {
        get => _duration;
        set { if (_duration != value) { _duration = value; OnPropertyChanged(); } }
    }
    
    public int? PromptTokens
    {
        get => _promptTokens;
        set { if (_promptTokens != value) { _promptTokens = value; OnPropertyChanged(); } }
    }

    public int? CompletionTokens
    {
        get => _completionTokens;
        set { if (_completionTokens != value) { _completionTokens = value; OnPropertyChanged(); } }
    }

    public LlmTransaction(string provider, string model, string? requestPayload = null)
    {
        Provider = provider;
        Model = model;
        RequestPayload = requestPayload ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public interface ILogBus
{
    event EventHandler<LogEntry>? EntryPublished;
    event EventHandler<LlmTransaction>? TransactionPublished;
    
    System.Collections.Generic.IReadOnlyList<LogEntry> History { get; }
    System.Collections.Generic.IReadOnlyList<LlmTransaction> Transactions { get; }

    void Publish(LogEntry entry);
    void Publish(LlmTransaction transaction);
    void ClearHistory();
    void ClearTransactions();
}
