using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Text;
using System.Text.Json;
using System.IO;
using Microsoft.Win32;
using System.Windows.Media;
using System.Globalization;
using AiAssistant.Core.Services;

namespace AiAssistant.UI.Controls;

public class LogEntryViewModel
{
    public DateTime Timestamp { get; }
    public LogCategory Category { get; }
    public string Message { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public LogEntryViewModel(LogEntry entry)
    {
        Timestamp = entry.Timestamp;
        Category = entry.Category;
        Message = entry.Message;
        Detail = entry.Detail;
    }
}

public class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LogCategory category)
        {
            return category switch
            {
                LogCategory.Chat => Brushes.DodgerBlue,
                LogCategory.Llm => Brushes.MediumOrchid,
                LogCategory.Tool => Brushes.MediumSeaGreen,
                LogCategory.Settings => Brushes.Goldenrod,
                _ => Brushes.LightCoral
            };
        }
        return Brushes.White;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class TruncateTextConverter : IValueConverter
{
    public int MaxLength { get; set; } = 15000;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            if (text.Length > MaxLength)
            {
                return text.Substring(0, MaxLength) + $"\r\n\r\n... [TRUNCATED ({text.Length} chars total) - Use 'Open as File', 'Copy' or 'Export' to view full payload] ...";
            }
            return text;
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public partial class LogPanel : UserControl
{
    private readonly ILogBus _logBus;
    private readonly ObservableCollection<LogEntryViewModel> _allHistoryEntries = new();
    private readonly object _historySync = new();
    
    public ObservableCollection<LlmTransaction> TransactionEntries { get; } = new();
    private readonly object _transactionSync = new();

    private readonly CollectionViewSource _filteredHistorySource = new();

    public LogPanel(ILogBus logBus)
    {
        InitializeComponent();
        _logBus = logBus ?? throw new ArgumentNullException(nameof(logBus));
        
        BindingOperations.EnableCollectionSynchronization(_allHistoryEntries, _historySync);
        BindingOperations.EnableCollectionSynchronization(TransactionEntries, _transactionSync);
        
        _filteredHistorySource.Source = _allHistoryEntries;
        _filteredHistorySource.Filter += OnHistoryFilter;
        
        HistoryListView.ItemsSource = _filteredHistorySource.View;
        DataContext = this;

        this.Loaded += LogPanel_Loaded;
        this.Unloaded += LogPanel_Unloaded;
    }

    private void LogPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_logBus != null)
        {
            lock (_historySync)
            {
                _allHistoryEntries.Clear();
                foreach (var entry in _logBus.History)
                {
                    _allHistoryEntries.Add(new LogEntryViewModel(entry));
                }
            }

            lock (_transactionSync)
            {
                TransactionEntries.Clear();
                foreach (var tx in _logBus.Transactions)
                {
                    TransactionEntries.Add(tx);
                }
            }

            _logBus.EntryPublished -= OnLogEntryPublished;
            _logBus.TransactionPublished -= OnTransactionPublished;

            _logBus.EntryPublished += OnLogEntryPublished;
            _logBus.TransactionPublished += OnTransactionPublished;
        }
    }

    private void LogPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_logBus != null)
        {
            _logBus.EntryPublished -= OnLogEntryPublished;
            _logBus.TransactionPublished -= OnTransactionPublished;
        }
    }

    private void OnLogEntryPublished(object sender, LogEntry entry)
    {
        lock (_historySync)
        {
            _allHistoryEntries.Add(new LogEntryViewModel(entry));
            if (_allHistoryEntries.Count > 1000)
            {
                _allHistoryEntries.RemoveAt(0);
            }
        }
        
        // Auto-scroll if at bottom (TC-B06)
        Dispatcher.InvokeAsync(() =>
        {
            if (VisualTreeHelper.GetChild(HistoryListView, 0) is Decorator border && border.Child is ScrollViewer scrollViewer)
            {
                if (scrollViewer.VerticalOffset + scrollViewer.ViewportHeight >= scrollViewer.ExtentHeight - 10)
                {
                    scrollViewer.ScrollToEnd();
                }
            }
        });
    }

    private void OnTransactionPublished(object sender, LlmTransaction transaction)
    {
        lock (_transactionSync)
        {
            TransactionEntries.Add(transaction);
            if (TransactionEntries.Count > 200)
            {
                TransactionEntries.RemoveAt(0);
            }
        }
    }

    private void OnHistoryFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is LogEntryViewModel entry)
        {
            e.Accepted = ShouldShow(entry.Category);
        }
    }

    private bool ShouldShow(LogCategory category)
    {
        if (!IsLoaded) return true;
        return category switch
        {
            LogCategory.Chat => FilterChat.IsChecked == true,
            LogCategory.Llm => FilterLlm.IsChecked == true,
            LogCategory.Tool => FilterTool.IsChecked == true,
            LogCategory.Agent => FilterAgent.IsChecked == true,
            LogCategory.Settings => FilterSettings.IsChecked == true,
            _ => true
        };
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_filteredHistorySource.View != null)
        {
            _filteredHistorySource.View.Refresh();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _logBus.ClearHistory();
        lock (_historySync) { _allHistoryEntries.Clear(); }
    }
    
    private void ClearTransactions_Click(object sender, RoutedEventArgs e)
    {
        _logBus.ClearTransactions();
        lock (_transactionSync) { TransactionEntries.Clear(); }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var markdown = GenerateMarkdown();
            Clipboard.SetDataObject(markdown, true);
            MessageBox.Show("Log history copied to clipboard.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"AiAssistant_Log_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                lock (_historySync)
                {
                    var json = JsonSerializer.Serialize(_allHistoryEntries, options);
                    File.WriteAllText(saveFileDialog.FileName, json);
                }
                MessageBox.Show("Logs exported to JSON successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export JSON: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ExportTextButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"AiAssistant_Log_{DateTime.Now:yyyyMMdd_HHmmss}.md"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var markdown = GenerateMarkdown();
                File.WriteAllText(saveFileDialog.FileName, markdown);
                MessageBox.Show("Logs exported to Markdown successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export Text: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private string GenerateMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ApexCode Log History");
        sb.AppendLine();

        lock (_historySync)
        {
            foreach (var entry in _allHistoryEntries)
            {
                sb.AppendLine($"## [{entry.Timestamp:HH:mm:ss.fff}] [{entry.Category}]");
                sb.AppendLine($"**Message:** {entry.Message}");
                if (!string.IsNullOrEmpty(entry.Detail))
                {
                    sb.AppendLine("**Details:**");
                    sb.AppendLine("```");
                    sb.AppendLine(entry.Detail);
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private void RawCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionsListView.SelectedItem is LlmTransaction selected)
        {
            try
            {
                Clipboard.SetDataObject(selected.ResponsePayload ?? selected.Error ?? "", true);
                MessageBox.Show("Response copied to clipboard.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
    }

    private void RawCopyReqButton_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionsListView.SelectedItem is LlmTransaction selected)
        {
            try
            {
                Clipboard.SetDataObject(selected.RequestPayload ?? "", true);
                MessageBox.Show("Request copied to clipboard.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
    }

#pragma warning disable VSTHRD100
    private async void RawOpenAsFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionsListView.SelectedItem is LlmTransaction selected)
        {
            try
            {
                var tmpPath = Path.Combine(Path.GetTempPath(), $"LlmRequest_{selected.Id}.json");
                var obj = new { Request = selected.RequestPayload, Response = selected.ResponsePayload, Error = selected.Error };
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
                
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Microsoft.VisualStudio.Shell.VsShellUtilities.OpenDocument(Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider, tmpPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open as file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
#pragma warning restore VSTHRD100

    private void RawExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (TransactionsListView.SelectedItem is LlmTransaction selected)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                FileName = $"LlmTransaction_{selected.Id}.json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    var obj = new { Request = selected.RequestPayload, Response = selected.ResponsePayload, Error = selected.Error };
                    File.WriteAllText(saveFileDialog.FileName, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
                    MessageBox.Show("Transaction exported successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void RawExportAllJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"AiAssistant_Transactions_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                lock (_transactionSync)
                {
                    var json = JsonSerializer.Serialize(TransactionEntries, options);
                    File.WriteAllText(saveFileDialog.FileName, json);
                }
                MessageBox.Show("Transactions exported to JSON successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export JSON: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RawExportAllTextButton_Click(object sender, RoutedEventArgs e)
    {
        var saveFileDialog = new SaveFileDialog
        {
            Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            FileName = $"AiAssistant_Transactions_{DateTime.Now:yyyyMMdd_HHmmss}.md"
        };

        if (saveFileDialog.ShowDialog() == true)
        {
            try
            {
                var markdown = GenerateTransactionsMarkdown();
                File.WriteAllText(saveFileDialog.FileName, markdown);
                MessageBox.Show("Transactions exported to Markdown successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export Text: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private string GenerateTransactionsMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ApexCode Transactions History");
        sb.AppendLine();

        lock (_transactionSync)
        {
            foreach (var tx in TransactionEntries)
            {
                sb.AppendLine($"## [{tx.StartTime:HH:mm:ss}] [{tx.Provider}] - {tx.Status} ({(tx.Duration?.TotalSeconds ?? 0):F3}s)");
                
                if (!string.IsNullOrEmpty(tx.RequestPayload))
                {
                    sb.AppendLine("**Request:**");
                    sb.AppendLine("```json");
                    sb.AppendLine(tx.RequestPayload);
                    sb.AppendLine("```");
                }
                
                if (!string.IsNullOrEmpty(tx.ResponsePayload))
                {
                    sb.AppendLine("**Response:**");
                    sb.AppendLine("```json");
                    sb.AppendLine(tx.ResponsePayload);
                    sb.AppendLine("```");
                }
                
                if (!string.IsNullOrEmpty(tx.Error))
                {
                    sb.AppendLine("**Error:**");
                    sb.AppendLine("```");
                    sb.AppendLine(tx.Error);
                    sb.AppendLine("```");
                }
                
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
