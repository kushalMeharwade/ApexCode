using System;
using System.IO;
using System.Windows;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.UI.Controls;

namespace AiAssistant.UI.Helpers
{
    public static class ThemeManager
    {
        private static ResourceDictionary _themeDictionaryContainer;
        private static bool _initialized = false;
        public static bool IsDarkTheme { get; private set; } = true;
        public static event EventHandler ThemeChanged;
        public static event EventHandler<UICustomizationPreferences> PreferencesChanged;

        public static void OnPreferencesChanged(UICustomizationPreferences prefs)
        {
            PreferencesChanged?.Invoke(null, prefs);
        }

        public static void Initialize()
        {
            if (_initialized) return;

            _themeDictionaryContainer = new ResourceDictionary();
            _themeDictionaryContainer.Add("AiAssistantThemeMarker", true);
            
            // Determine user preference to load correct theme initially
            bool isDark = true;
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var themeFile = Path.Combine(localAppData, AiAssistantPackage.PackageGuidString, "theme.txt");
                if (File.Exists(themeFile))
                {
                    var theme = File.ReadAllText(themeFile).Trim();
                    if (theme == "light")
                    {
                        isDark = false;
                    }
                }
            }
            catch { }
            
            IsDarkTheme = isDark;

            // Add the appropriate theme
            var themeUri = isDark 
                ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" 
                : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml";
            
            // Add control styles and shared resources FIRST, so Theme overrides them
            _themeDictionaryContainer.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AiAssistant.UI;component/Themes/ControlStyles.xaml", UriKind.Absolute) });
            _themeDictionaryContainer.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) });

            _initialized = true;
        }

        public static ResourceDictionary GetThemeDictionaryContainer()
        {
            Initialize();
            return _themeDictionaryContainer;
        }

        public static void ApplyTheme(bool isDark)
        {
            Initialize();
            IsDarkTheme = isDark;
            
            // Create a completely new container instance to force WPF to recognize the reference change
            _themeDictionaryContainer = new ResourceDictionary();
            _themeDictionaryContainer.Add("AiAssistantThemeMarker", true);
            
            var themeUri = isDark 
                ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" 
                : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml";
            
            _themeDictionaryContainer.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AiAssistant.UI;component/Themes/ControlStyles.xaml", UriKind.Absolute) });
            _themeDictionaryContainer.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(themeUri, UriKind.Absolute) });

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
