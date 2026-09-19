using System;
using System.Windows;

namespace AiAssistant.UI.Windows;

public partial class ApprovalDialog : Window
{
    public bool Approved { get; private set; }
    public bool AlwaysAllow { get; private set; }

    public ApprovalDialog(string toolName, string description)
    {
        Helpers.ThemeManager.Initialize();
        
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        InitializeComponent();
        Title = $"Approve Tool: {toolName}";
        DescriptionText.Text = description;
    }

    private void ApproveBtn_Click(object sender, RoutedEventArgs e)
    {
        Approved = true;
        DialogResult = true;
        Close();
    }

    private void RejectBtn_Click(object sender, RoutedEventArgs e)
    {
        Approved = false;
        DialogResult = false;
        Close();
    }

    private void AlwaysAllowBtn_Click(object sender, RoutedEventArgs e)
    {
        Approved = true;
        AlwaysAllow = true;
        DialogResult = true;
        Close();
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
}
