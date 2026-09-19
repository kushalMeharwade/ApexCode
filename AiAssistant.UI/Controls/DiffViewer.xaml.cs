using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AiAssistant.Engine.Models;

namespace AiAssistant.UI.Controls;

/// <summary>
/// WPF control that displays a diff preview with syntax-colored lines
/// and Accept/Reject buttons for applying changes.
/// </summary>
public partial class DiffViewer : UserControl
{
    private DiffPreviewResult? _preview;

    public event EventHandler? Accepted;
    public event EventHandler? Rejected;

    public DiffViewer()
    {
        Helpers.ThemeManager.Initialize();
        
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        InitializeComponent();
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
        this.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary 
        { 
            Source = new Uri(isDark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
        });
        this.Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>
    /// Displays a diff preview.
    /// </summary>
    public void ShowDiff(DiffPreviewResult preview)
    {
        _preview = preview;
        FilePathText.Text = preview.FilePath;
        StatsText.Text = $"+{preview.LinesAdded} / -{preview.LinesRemoved} / ~{preview.LinesModified}  ({preview.LinesUnchanged} unchanged)";

        DiffContentPanel.Children.Clear();

        if (!preview.IsValid)
        {
            DiffContentPanel.Children.Add(CreateErrorText(preview.ErrorMessage ?? "Invalid diff"));
            return;
        }

        // Render each diff block
        foreach (var block in preview.Blocks)
        {
            // Block header
            DiffContentPanel.Children.Add(CreateBlockHeader(block));

            // Lines in the block
            foreach (var line in block.Lines)
            {
                DiffContentPanel.Children.Add(CreateDiffLine(line));
            }

            // Spacer between blocks
            DiffContentPanel.Children.Add(new Border { Height = 8 });
        }

        // If no blocks, show "no changes"
        if (preview.Blocks.Count == 0)
        {
            DiffContentPanel.Children.Add(CreateInfoText("No changes detected."));
        }
    }

    private TextBlock CreateBlockHeader(DiffBlock block)
    {
        var tb = new TextBlock
        {
            Text = $"@@ -{block.OriginalStartLine},{block.OriginalLineCount} +{block.NewStartLine},{block.NewLineCount} @@",
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 4, 0, 0)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "DiffHeaderFgBrush");
        tb.SetResourceReference(TextBlock.BackgroundProperty, "DiffHeaderBgBrush");
        return tb;
    }

    private Border CreateDiffLine(DiffLine line)
    {
        var prefix = line.Type switch
        {
            DiffLineType.Added => "+",
            DiffLineType.Removed => "-",
            _ => " "
        };

        var lineNum = line.Type switch
        {
            DiffLineType.Added => line.NewLineNumber?.ToString() ?? "",
            DiffLineType.Removed => line.OriginalLineNumber?.ToString() ?? "",
            _ => line.OriginalLineNumber?.ToString() ?? ""
        };

        var text = new TextBlock
        {
            Text = $"{prefix} {lineNum,4} │ {line.Content}",
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 12,
            Padding = new Thickness(4, 1, 4, 1),
            TextWrapping = TextWrapping.NoWrap
        };

        var border = new Border
        {
            Child = text,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0)
        };

        if (line.Type == DiffLineType.Added)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "DiffAddedFgBrush");
            border.SetResourceReference(Border.BackgroundProperty, "DiffAddedBgBrush");
        }
        else if (line.Type == DiffLineType.Removed)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "DiffRemovedFgBrush");
            border.SetResourceReference(Border.BackgroundProperty, "DiffRemovedBgBrush");
        }
        else
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            border.Background = Brushes.Transparent;
        }

        return border;
    }

    private TextBlock CreateErrorText(string message)
    {
        var tb = new TextBlock
        {
            Text = $"Error: {message}",
            FontSize = 12,
            Padding = new Thickness(8)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ErrorRedBrush");
        return tb;
    }

    private TextBlock CreateInfoText(string message)
    {
        var tb = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Padding = new Thickness(8),
            FontStyle = FontStyles.Italic
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "DiffInfoFgBrush");
        return tb;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        Rejected?.Invoke(this, EventArgs.Empty);
    }
}
