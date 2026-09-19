using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

public class UserPreferencesService : IUserPreferencesService
{
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiAssistant", "ui_preferences.json");

    private UICustomizationPreferences? _current;

    public event EventHandler<UICustomizationPreferences>? PreferencesChanged;

    public Task<UICustomizationPreferences> LoadAsync()
    {
        try
        {
            if (File.Exists(PreferencesPath))
            {
                var json = File.ReadAllText(PreferencesPath);
                var prefs = JsonSerializer.Deserialize<UICustomizationPreferences>(json);
                if (prefs != null)
                {
                    _current = prefs;
                    return Task.FromResult(prefs);
                }
            }
        }
        catch
        {
        }

        _current = new UICustomizationPreferences();
        return Task.FromResult(_current);
    }

    public Task SaveAsync(UICustomizationPreferences preferences)
    {
        if (preferences == null) return Task.CompletedTask;

        try
        {
            var dir = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
            AiAssistant.Storage.SafeFileWriter.WriteAllText(PreferencesPath, json);
            _current = preferences;
            PreferencesChanged?.Invoke(this, preferences);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }
}
