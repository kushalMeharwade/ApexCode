using System;
using System.Windows;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Repositories;

namespace AiAssistant.UI.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow(
        IProviderProfileRepository providerRepo,
        ISystemPromptRepository promptRepo,
        IPersonaService personaService,
        IDatabaseConnectionService dbService,
        IModelCacheRepository modelCacheRepo,
        AiAssistant.Llm.Services.IChatClientFactory chatClientFactory,
        ISettingsService settingsService,
        IProviderChangeNotifier providerChangeNotifier,
        IModelFetchService modelFetchService)
    {
        Helpers.ThemeManager.Initialize();
        
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        OnThemeChanged(null, EventArgs.Empty);

        InitializeComponent();
        
        // Re-initialize SettingsView with the provided dependencies
        MainSettingsView = new Controls.SettingsView(providerRepo, promptRepo, personaService, dbService, modelCacheRepo, chatClientFactory, null, settingsService, null, providerChangeNotifier, modelFetchService);
        Content = MainSettingsView; // Replace the existing content with the new one
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
