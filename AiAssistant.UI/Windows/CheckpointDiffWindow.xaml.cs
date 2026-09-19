using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiAssistant.Core.Models;
using AiAssistant.Core.Services;

namespace AiAssistant.UI.Windows;

public partial class CheckpointDiffWindow : Window
{
    private readonly ICheckpointService _checkpointService;
    private readonly IVisualStudioEnvironmentService _vsEnvService;
    private readonly string _checkpointId;
    private readonly string _commitHash;

    public CheckpointDiffWindow(
        IReadOnlyList<CheckpointFileDiff> diffs,
        ICheckpointService checkpointService,
        IVisualStudioEnvironmentService vsEnvService,
        string checkpointId,
        string commitHash)
    {
        Helpers.ThemeManager.Initialize();
        
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        InitializeComponent();

        _checkpointService = checkpointService;
        _vsEnvService = vsEnvService;
        _checkpointId = checkpointId;
        _commitHash = commitHash;

        FilesListView.ItemsSource = diffs;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var dict = Helpers.ThemeManager.GetThemeDictionaryContainer();
        bool isDark = Helpers.ThemeManager.IsDarkTheme;
        for (int i = this.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var current = this.Resources.MergedDictionaries[i];
            if (current.Contains("AiAssistantThemeMarker") || (current.Source != null && current.Source.ToString().Contains("Theme.xaml")))
            {
                this.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
        this.Resources.MergedDictionaries.Add(new ResourceDictionary 
        { 
            Source = new Uri(isDark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
        });
        this.Resources.MergedDictionaries.Add(dict);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is CheckpointFileDiff diff)
        {
            try
            {
                var content = await _checkpointService.GetFileContentAsync(_commitHash, diff.FilePath);
                if (content == null)
                {
                    MessageBox.Show("Could not retrieve file content from checkpoint.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Save old content to temp file
                var tempFile = Path.GetTempFileName();
                AiAssistant.Storage.SafeFileWriter.WriteAllText(tempFile, content);

                var workspacePath = await _vsEnvService.GetWorkspaceRootAsync();
                if (string.IsNullOrEmpty(workspacePath))
                {
                    MessageBox.Show("Active solution directory not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var rightFilePath = Path.Combine(workspacePath, diff.FilePath);
                
                await _vsEnvService.OpenDiffViewerAsync(tempFile, rightFilePath, $"Checkpoint Compare: {Path.GetFileName(diff.FilePath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening diff viewer: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
