using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;

namespace AiAssistant.Core.Services;

public interface IUserPreferencesService
{
    Task<UICustomizationPreferences> LoadAsync();
    Task SaveAsync(UICustomizationPreferences preferences);
    event EventHandler<UICustomizationPreferences> PreferencesChanged;
}
