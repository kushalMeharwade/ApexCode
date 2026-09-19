using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Services;
using AiAssistant.UI.ViewModels;

namespace AiAssistant.UI.Controls;

public partial class HistoryPanel : UserControl, INotifyPropertyChanged
{
    private IChatService? _chatService;
    private ITelemetryService? _telemetryService;
    private System.ComponentModel.ICollectionView? _groupedItems;
    private List<HistoryItemViewModel> _allItems = new();
    private string _searchQuery = "";

    public System.ComponentModel.ICollectionView? GroupedItems
    {
        get => _groupedItems;
        private set { _groupedItems = value; OnPropertyChanged(); }
    }

    public HistoryPanel() : this(null, null) { }

    public HistoryPanel(IChatService? chatService, ITelemetryService? telemetryService)
    {
        _chatService = chatService;
        _telemetryService = telemetryService;

        InitializeComponent();

        // FIX: this panel never listened for theme changes, so it stayed on whatever
        // ResourceDictionary was hardcoded in its own XAML (DarkTheme.xaml) regardless
        // of the user's selected theme. ChatPanel already subscribes to this same event
        // right after InitializeComponent (the XAML-defined <UserControl.Resources>
        // replaces `this.Resources` wholesale during InitializeComponent, so this call
        // has to come after it, not before) — History just never did.
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        DataContext = this;
        Loaded += HistoryPanel_Loaded;
        Unloaded += HistoryPanel_Unloaded;
    }

    private void HistoryPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        Helpers.ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>
    /// Swaps the Dark/Light theme dictionary the same way ChatPanel and SettingsView do,
    /// and re-merges the shared ThemeDictionaryContainer afterward so live font-size
    /// overrides (set from the Settings > General sliders) apply here too.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var container = Helpers.ThemeManager.GetThemeDictionaryContainer();
        bool isDark = Helpers.ThemeManager.IsDarkTheme;

        for (int i = this.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var current = this.Resources.MergedDictionaries[i];
            if (current.Contains("AiAssistantThemeMarker") || (current.Source != null && current.Source.ToString().Contains("Theme.xaml")))
            {
                this.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        this.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri(isDark
                ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml"
                : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute)
        });

        this.Resources.MergedDictionaries.Add(container);
    }

    public void SetServices(IChatService? chatService, ITelemetryService? telemetryService)
    {
        _chatService = chatService;
        _telemetryService = telemetryService;
    }

    private bool _isLoaded;

    private async void HistoryPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;

        if (_chatService != null)
        {
            await LoadHistoryAsync();
        }
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (SearchBox != null) SearchBox.Focus();
        }, DispatcherPriority.Input);
    }

    public async Task LoadHistoryAsync()
    {
        if (_chatService == null) return;

        try
        {
            LoadingStatePanel.Visibility = Visibility.Visible;
            
            var rotateAnim = new System.Windows.Media.Animation.DoubleAnimation {
                From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1000),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever 
            };
            LoadingRingTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotateAnim);

            var textAnim = new System.Windows.Media.Animation.DoubleAnimation {
                From = 1.0, To = 0.5, Duration = TimeSpan.FromMilliseconds(800), 
                AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever 
            };
            LoadingTextBlock.BeginAnimation(UIElement.OpacityProperty, textAnim);

            HistoryGroupsList.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            var items = await Task.Run(async () =>
            {
                var sessions = await _chatService.GetSessionSummariesAsync();
                var result = new List<HistoryItemViewModel>();

                foreach (var session in sessions)
                {
                    result.Add(new HistoryItemViewModel
                    {
                        Id = session.Id,
                        Name = session.Name,
                        FirstPrompt = ExtractUserPrompt(session.FirstPrompt, session.Name),
                        UpdatedAt = session.UpdatedAt.Kind == DateTimeKind.Local
                            ? session.UpdatedAt
                            : DateTime.SpecifyKind(session.UpdatedAt, DateTimeKind.Utc).ToLocalTime()
                    });
                }
                return result;
            });

            _allItems = items.OrderByDescending(i => i.UpdatedAt).ToList();
            ApplyFiltersAndGroup();
            
            LoadingRingTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            LoadingTextBlock.BeginAnimation(UIElement.OpacityProperty, null);
            
            LoadingStatePanel.Visibility = Visibility.Collapsed;
            HistoryGroupsList.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to load history: {ex.Message}");
            LoadingRingTransform.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            LoadingTextBlock.BeginAnimation(UIElement.OpacityProperty, null);

            LoadingStatePanel.Visibility = Visibility.Collapsed;
            HistoryGroupsList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Strips the hidden [CONTEXT: …] injection block that the system prepends to user
    /// messages when files are referenced, so only the actual user-typed text is shown
    /// in the history card preview.
    /// </summary>
    internal static string ExtractUserPrompt(string? rawContent, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
            return string.IsNullOrWhiteSpace(fallback) ? "(no messages)" : fallback;

        var content = rawContent.TrimStart();

        // If the message starts with a [CONTEXT: block, find where it ends.
        // The block header is a single line: "[CONTEXT: ...]"
        // Everything after the closing bracket line (and any blank lines) is the real prompt.
        if (content.StartsWith("[CONTEXT:", StringComparison.OrdinalIgnoreCase))
        {
            // 1. Try to find the modern marker added by ChatPanel
            var marker = "\nUser's question: ";
            var markerIdx = content.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                var extracted = content.Substring(markerIdx + marker.Length).Trim();
                if (!string.IsNullOrWhiteSpace(extracted))
                    return extracted;
            }

            // 2. Try to find the '---' separator
            var sepMarker = "\n---";
            var sepIdx = content.LastIndexOf(sepMarker, StringComparison.Ordinal);
            if (sepIdx >= 0)
            {
                var extracted = content.Substring(sepIdx + sepMarker.Length).Trim();
                if (!string.IsNullOrWhiteSpace(extracted))
                    return extracted;
            }

            // 3. Fallback for legacy format
            var closingBracket = content.IndexOf(']');
            if (closingBracket >= 0)
            {
                var afterHeader = content.Substring(closingBracket + 1).TrimStart('\r', '\n');

                // Try to skip past the last code block closing
                var lastCodeBlockEnd = afterHeader.LastIndexOf("\n```", StringComparison.Ordinal);
                if (lastCodeBlockEnd >= 0)
                {
                    var extracted = afterHeader.Substring(lastCodeBlockEnd + 4).Trim();
                    if (!string.IsNullOrWhiteSpace(extracted))
                        return extracted;
                }

                // Absolute fallback
                var lines = afterHeader.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var userLines = lines
                    .SkipWhile(l => l.TrimStart().StartsWith("###")
                                 || l.TrimStart().StartsWith("```")
                                 || l.TrimStart().StartsWith("//")
                                 || l.TrimStart().StartsWith("using ")
                                 || l.TrimStart().StartsWith("<"))
                    .ToArray();

                var extracted2 = string.Join(" ", userLines).Trim();
                if (!string.IsNullOrWhiteSpace(extracted2))
                    return extracted2;
            }

            // Could not isolate the user text — fall back to session name
            return string.IsNullOrWhiteSpace(fallback) ? "(no messages)" : fallback;
        }

        return content;
    }

    public Task RefreshHistoryAsync()
    {
        return LoadHistoryAsync();
    }

    private void ApplyFiltersAndGroup()
    {
        var filtered = FilterByDate(_allItems);
        filtered = FilterBySearch(filtered);
        
        AssignGroupNames(filtered);

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(filtered);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("GroupName"));
        
        GroupedItems = view;

        EmptyStatePanel.Visibility = !filtered.Any()
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<HistoryItemViewModel> FilterByDate(List<HistoryItemViewModel> items)
    {
        var fromDate = FromDatePicker.SelectedDate;
        var toDate = ToDatePicker.SelectedDate;

        if (fromDate == null && toDate == null) return items;

        return items.Where(i =>
        {
            if (fromDate.HasValue && i.UpdatedAt.Date < fromDate.Value.Date) return false;
            if (toDate.HasValue && i.UpdatedAt.Date > toDate.Value.Date) return false;
            return true;
        }).ToList();
    }

    private List<HistoryItemViewModel> FilterBySearch(List<HistoryItemViewModel> items)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery)) return items;

        var query = _searchQuery.Trim().ToLowerInvariant();
        return items.Where(i =>
            (i.Name?.ToLowerInvariant().Contains(query) ?? false) ||
            (i.FirstPrompt?.ToLowerInvariant().Contains(query) ?? false)
        ).ToList();
    }

    private static void AssignGroupNames(List<HistoryItemViewModel> items)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekStart = today.AddDays(-(int)now.DayOfWeek);
        var monthStart = new DateTime(now.Year, now.Month, 1);

        foreach (var item in items)
        {
            var date = item.UpdatedAt.Date;
            if (date == today) item.GroupName = "Today";
            else if (date == yesterday) item.GroupName = "Yesterday";
            else if (date >= weekStart) item.GroupName = "This Week";
            else if (date >= monthStart) item.GroupName = "This Month";
            else item.GroupName = "Older";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_searchQuery)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFiltersAndGroup();
    }

    private void FilterToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        DateFiltersPanel.Visibility = DateFiltersPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        ApplyFiltersAndGroup();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        FromDatePicker.SelectedDate = null;
        ToDatePicker.SelectedDate = null;
        ApplyFiltersAndGroup();
    }


    private void ItemText_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is HistoryItemViewModel item)
        {
            // ChatPanel owns the loading indicator and the complete activation/load operation.
            ReturnToChat?.Invoke(this, item.Id);
        }
    }

    private void RenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not HistoryItemViewModel item) return;

        item.IsEditing = true;

        // The rename TextBox is a sibling of the button within the same row Grid.
        // Walk up to that Grid and find it directly instead of searching the whole tree.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (FindAncestor<Grid>(btn) is Grid rowGrid)
            {
                var textBox = rowGrid.Children.OfType<TextBox>()
                    .FirstOrDefault(tb => tb.Name == "RenameTextBox");
                if (textBox != null)
                {
                    textBox.Focus();
                    textBox.SelectAll();
                }
            }
        }, DispatcherPriority.Input);
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return parent as T;
    }

    private async void CommitRename(TextBox textBox)
    {
        if (textBox.Tag is not HistoryItemViewModel item) return;

        item.IsEditing = false;

        var newName = textBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        try
        {
            if (_chatService != null)
            {
                await _chatService.RenameSessionAsync(item.Id, newName);
                item.Name = newName;
                _telemetryService?.TrackEventAsync("history_session_renamed");
            }
        }
        catch (Exception ex)
        {
            await ConfirmOverlay.ShowInfoAsync("Error", $"Failed to rename conversation: {ex.Message}");
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(textBox);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (textBox.Tag is HistoryItemViewModel item)
                item.IsEditing = false;
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is HistoryItemViewModel item && item.IsEditing)
        {
            CommitRename(textBox);
        }
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItemViewModel item)
        {
            var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                "Delete Conversation",
                $"Delete conversation \"{item.Name}\"? This cannot be undone.",
                confirmText: "Delete",
                isDestructive: true);

            if (!confirmed) return;

            try
            {
                if (_chatService != null)
                {
                    await _chatService.DeleteSessionAsync(item.Id);
                    _allItems.Remove(item);
                    ApplyFiltersAndGroup();
                    _telemetryService?.TrackEventAsync("history_session_deleted");
                }
            }
            catch (Exception ex)
            {
                await ConfirmOverlay.ShowInfoAsync("Error", $"Failed to delete conversation: {ex.Message}");
            }
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ReturnToChat?.Invoke(this, null);
    }

    private void NewChatFromEmptyState_Click(object sender, RoutedEventArgs e)
    {
        // FIX (History empty state quick action): skip the extra "Back" then "New Chat"
        // click sequence when there's no history to show yet.
        NewChatRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<string?>? ReturnToChat;

    /// <summary>
    /// Raised when the user clicks "Start a new chat" from the empty-history state.
    /// Distinct from <see cref="ReturnToChat"/> (which just closes the history overlay)
    /// because this should also trigger ChatPanel's new-session flow.
    /// </summary>
    public event EventHandler? NewChatRequested;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
