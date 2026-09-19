using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Media;

namespace AiAssistant.UI.ViewModels;

// ---------------------------------------------------------------------------
// Typed union — the element stream is ObservableCollection<IMessageElement>.
// WPF resolves implicit DataTemplates against the concrete runtime type, so
// TextElement and ToolCallViewModel each get their own template automatically.
// ---------------------------------------------------------------------------

/// <summary>
/// Marker interface for items that live in <see cref="ChatMessageViewModel.Elements"/>.
/// Adding a new element kind (citations, images, thinking blocks…) only requires
/// implementing this interface and adding a DataTemplate — no ViewModel surgery.
/// </summary>
public interface IMessageElement { }

// ---------------------------------------------------------------------------
// ChatMessageViewModel
// ---------------------------------------------------------------------------

public class ChatMessageViewModel : INotifyPropertyChanged
{
    // ── Raw accumulated content kept for copy-all and Content binding ────────
    private string _content = "";
    private bool _isStreaming;

    /// <summary>
    /// Flat accumulated content (used for copy-all and Content binding).
    /// Setting this does NOT rebuild Elements — use AppendTextDelta / AppendTool
    /// for live stream mutations.
    /// </summary>
    public string Content
    {
        get => _content;
        set
        {
            if (string.Equals(_content, value, StringComparison.Ordinal)) return;
            _content = value;
            OnPropertyChanged();
        }
    }

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Role { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool IsToolExecuting => Elements.OfType<ToolCallViewModel>().Any(t => t.IsRunning);

    public string ToolExecutionStatus
    {
        get
        {
            var runningTool = Elements.OfType<ToolCallViewModel>().LastOrDefault(t => t.IsRunning);
            return runningTool != null ? $"Running {runningTool.ToolName}" : string.Empty;
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            // Propagate to every TextElement in the unified stream.
            foreach (var el in Elements.OfType<TextElement>())
                el.IsStreaming = value;
            OnPropertyChanged();
        }
    }

    private bool _hasCheckpoint;
    public bool HasCheckpoint
    {
        get => _hasCheckpoint;
        set { _hasCheckpoint = value; OnPropertyChanged(); }
    }

    private string? _checkpointId;
    public string? CheckpointId
    {
        get => _checkpointId;
        set { _checkpointId = value; OnPropertyChanged(); }
    }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    // ── Unified element stream ───────────────────────────────────────────────
    /// <summary>
    /// Single chronological sequence of text segments and tool cards in exactly
    /// the order the LLM produced them.  WPF picks the correct DataTemplate for
    /// each item based on its concrete runtime type (TextElement or ToolCallViewModel).
    /// </summary>
    public ObservableCollection<IMessageElement> Elements { get; } = new();

    /// <summary>
    /// O(1) lookup of tool cards by their call-ID.  Kept in sync with Elements so
    /// tool completions never need a linear scan.
    /// </summary>
    internal readonly Dictionary<string, ToolCallViewModel> _toolIndex = new(StringComparer.Ordinal);

    // ── Streaming state machine ──────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="delta"/> to the current text run, or starts a new
    /// <see cref="TextElement"/> when the last item is a tool card (or the stream
    /// is just beginning).  Mutates the existing TextElement in place — raises
    /// PropertyChanged, NOT CollectionChanged — so the UI does not rebuild the
    /// item for every token.
    /// </summary>
    public void AppendTextDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;

        Content += delta;

        if (Elements.LastOrDefault() is TextElement last)
        {
            // Rule 1 — extend the current text run in-place (no collection event).
            last.AppendRaw(delta);
        }
        else
        {
            // Rule 2 — a tool card is the last item, or the collection is empty.
            // Start a fresh text segment.
            var el = new TextElement { IsStreaming = true };
            el.AppendRaw(delta);
            Elements.Add(el);
        }
    }

    /// <summary>
    /// Appends a new tool card to the stream and registers it in the identity index.
    /// Returns the view model so the caller can bind approval commands immediately.
    /// </summary>
    public ToolCallViewModel AppendTool(string id, string toolName, IDictionary<string, object?>? arguments)
    {
        var vm = ToolCallViewModel.Create(id, toolName, arguments);
        _toolIndex[id] = vm;
        Elements.Add(vm);
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ToolCallViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(IsToolExecuting));
                OnPropertyChanged(nameof(ToolExecutionStatus));
            }
        };
        OnPropertyChanged(nameof(IsToolExecuting));
        OnPropertyChanged(nameof(ToolExecutionStatus));
        return vm;
    }

    /// <summary>
    /// Completes the tool call identified by <paramref name="id"/> using O(1) dictionary
    /// lookup.  Returns false if the ID is not found (e.g. a race from the log bus).
    /// </summary>
    public bool CompleteTool(string id, string result, string status)
    {
        if (!_toolIndex.TryGetValue(id, out var vm)) return false;
        vm.Complete(result, status);
        return true;
    }

    /// <summary>
    /// Finds the last in-flight (no result yet) tool card with the given name.
    /// Used by the log bus which does not carry a structured call ID.
    /// </summary>
    public ToolCallViewModel? FindInflightTool(string toolName) =>
        Elements.OfType<ToolCallViewModel>()
                .LastOrDefault(t => string.Equals(t.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
                                    && !t.HasResult);

    /// <summary>
    /// Seals all open text segments at the end of a streaming turn.  Triggers
    /// the final Markdown render for each TextElement.
    /// </summary>
    public void FinalizeStreaming()
    {
        IsStreaming = false;
        for (int i = 0; i < Elements.Count; i++)
        {
            if (Elements[i] is TextElement el && !el.IsCode)
            {
                if (el.Text.Contains("```"))
                {
                    var parts = SplitMarkdown(el.Text);
                    if (parts.Count > 1 || parts.Any(part => part.IsCode))
                    {
                        Elements.RemoveAt(i);
                        foreach (var part in parts)
                        {
                            var newEl = new TextElement { IsStreaming = true };
                            if (part.IsCode)
                            {
                                newEl.IsCode = true;
                                newEl.Language = part.Language;
                                newEl.AppendRaw(part.Content);
                            }
                            else
                            {
                                newEl.AppendRaw(part.Content);
                            }
                            newEl.IsStreaming = false;
                            Elements.Insert(i++, newEl);
                        }
                        i--; 
                        continue;
                    }
                }
                el.IsStreaming = false;
            }
        }
    }

    // ── Historical restore ───────────────────────────────────────────────────

    /// <summary>
    /// Appends a finished text segment directly to Elements (used when restoring
    /// a persisted message — not during live streaming).
    /// </summary>
    public void AppendTextBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var parts = SplitMarkdown(text);
        foreach (var part in parts)
        {
            var el = new TextElement { IsStreaming = true };
            if (part.IsCode)
            {
                el.IsCode = true;
                el.Language = part.Language;
                el.AppendRaw(part.Content);
            }
            else
            {
                el.AppendRaw(part.Content);
            }
            el.IsStreaming = false;
            Elements.Add(el);
        }
    }

    private static List<(bool IsCode, string Language, string Content)> SplitMarkdown(string text)
    {
        var result = new List<(bool, string, string)>();
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        
        bool inCode = false;
        string currentLanguage = "";
        var currentContent = new System.Text.StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode)
                {
                    result.Add((true, currentLanguage, currentContent.ToString().TrimEnd('\r', '\n')));
                    currentContent.Clear();
                    inCode = false;
                }
                else
                {
                    if (currentContent.Length > 0)
                    {
                        result.Add((false, "", currentContent.ToString().TrimEnd('\r', '\n')));
                        currentContent.Clear();
                    }
                    inCode = true;
                    currentLanguage = line.TrimStart().Substring(3).Trim();
                }
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        if (currentContent.Length > 0)
        {
            result.Add((inCode, currentLanguage, currentContent.ToString().TrimEnd('\r', '\n')));
        }

        return result;
    }

    /// <summary>
    /// Appends a finished (historical) tool card and registers it in the index.
    /// </summary>
    public ToolCallViewModel AppendHistoricalTool(string id, string toolName, IDictionary<string, object?>? arguments)
    {
        var vm = ToolCallViewModel.Create(id, toolName, arguments);
        if (!string.IsNullOrEmpty(id))
            _toolIndex[id] = vm;
        Elements.Add(vm);
        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ToolCallViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(IsToolExecuting));
                OnPropertyChanged(nameof(ToolExecutionStatus));
            }
        };
        OnPropertyChanged(nameof(IsToolExecuting));
        OnPropertyChanged(nameof(ToolExecutionStatus));
        return vm;
    }

    // ── Font refresh (called when user changes font/size in Settings) ─────────
    public void RefreshFontSize()
    {
        foreach (var el in Elements.OfType<TextElement>())
            el.RefreshDocumentFontSize();
    }

    // ── Display helpers ──────────────────────────────────────────────────────
    public string RoleInitial => Role switch
    {
        "user"      => "U",
        "assistant" => "AI",
        "system"    => "S",
        _           => "?"
    };

    public string RoleName => Role switch
    {
        "user"      => "You",
        "assistant" => "ApexCode",
        "system"    => "System",
        _           => Role
    };

    public Brush RoleBrush => Role switch
    {
        "user"      => (Brush)(System.Windows.Application.Current.TryFindResource("TextPrimaryBrush")  ?? Brushes.Gray),
        "assistant" => (Brush)(System.Windows.Application.Current.TryFindResource("AccentPrimaryBrush") ?? Brushes.Gray),
        _           => (Brush)(System.Windows.Application.Current.TryFindResource("TextSecondaryBrush") ?? Brushes.Gray)
    };

    public Brush BackgroundBrush => Role switch
    {
        "user"      => (Brush)(System.Windows.Application.Current.TryFindResource("UserBubbleBgBrush")      ?? Brushes.Transparent),
        "assistant" => (Brush)(System.Windows.Application.Current.TryFindResource("AssistantBubbleBgBrush") ?? Brushes.Transparent),
        _           => Brushes.Transparent
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ---------------------------------------------------------------------------
// TextElement  — replaces MessagePart, now the first concrete IMessageElement.
// ---------------------------------------------------------------------------

/// <summary>
/// A prose segment in the unified element stream.  Mutated in-place during
/// streaming (no CollectionChanged events per token). The viewer renders Markdown
/// and owns the document. FinalizeStreaming extracts fenced code into separate
/// elements for the existing code editor template.
/// </summary>
public class TextElement : IMessageElement, INotifyPropertyChanged
{
    private readonly System.Text.StringBuilder _rawBuffer = new();
    private bool _isStreaming;
    private bool _isCode;
    private string _language = "";

    // ── Raw text (accumulated during streaming) ──────────────────────────────
    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal)) return;
            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasVisibleContent));
        }
    }

    public bool HasVisibleContent => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Appends <paramref name="delta"/> to the raw buffer and updates Text.</summary>
    internal void AppendRaw(string delta)
    {
        _rawBuffer.Append(delta);
        Text = _rawBuffer.ToString();
    }

    // ── Code-block support (mirrors old MessagePart) ─────────────────────────
    public bool IsCode
    {
        get => _isCode;
        set
        {
            if (_isCode == value) return;
            _isCode = value;
            OnPropertyChanged();
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            if (string.Equals(_language, value, StringComparison.Ordinal)) return;
            _language = value;
            OnPropertyChanged();
        }
    }

    // ── Streaming flag ───────────────────────────────────────────────────────
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            OnPropertyChanged();
        }
    }

    // Rendering and document ownership belong to the viewer. These refresh hooks
    // remain for existing tab/font refresh callers; theme values are dynamic resources.
    public void RefreshDocument() => OnPropertyChanged(nameof(Text));
    public void RefreshDocumentFontSize() => OnPropertyChanged(nameof(Text));
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
