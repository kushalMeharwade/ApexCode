using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Core.Services;

namespace AiAssistant.Core.Services;

/// <summary>
/// Service for managing extension settings stored in SQLite.
/// Handles provider profiles, system prompts, and user preferences.
/// </summary>
public interface ISettingsService
{
    event EventHandler? ProviderSettingsChanged;
    Task<ProviderProfile?> GetDefaultProviderAsync();
    Task<IReadOnlyList<ProviderProfile>> GetAllProvidersAsync();
    Task<ProviderProfile> SaveProviderAsync(ProviderProfile profile);
    Task DeleteProviderAsync(string id);
    Task<SystemPrompt?> GetDefaultPromptAsync();
    Task<IReadOnlyList<SystemPrompt>> GetAllPromptsAsync();
    Task<SystemPrompt> SavePromptAsync(SystemPrompt prompt);
    Task DeletePromptAsync(string id);
    bool EmbeddingIndexingEnabled { get; }
    Task SetEmbeddingIndexingEnabledAsync(bool value);
    bool AutoHealEnabled { get; }
    Task SetAutoHealEnabledAsync(bool value);
    bool AutoContinueOnInterrupt { get; set; }
    bool UseEnhancedPlanSystem { get; set; }
    string GetToolApprovalMode(string toolName);
    Task SetToolApprovalModeAsync(string toolName, string mode);
    IReadOnlyList<string> ExecuteCommandWhitelist { get; }
    Task SetExecuteCommandWhitelistAsync(IEnumerable<string> whitelist);
    bool EnableLogs { get; set; }
    string WorkspaceExtensions { get; }
    Task SetWorkspaceExtensionsAsync(string extensions);
    IUserPreferencesService UserPreferences { get; }
    bool DeveloperPersonaEnabled { get; set; }
    string DeveloperPersona { get; set; }
    string Language { get; }
    Task SetLanguageAsync(string value);
    string Country { get; }
    Task SetCountryAsync(string value);
    
    /// <summary>
    /// Gets a boolean setting with a default value if not set.
    /// Used for simple on/off flags like YoloMode.
    /// </summary>
    bool GetBoolSetting(string key, bool defaultValue);
    
    /// <summary>
    /// Sets a boolean setting.
    /// </summary>
    Task SetBoolSettingAsync(string key, bool value);

    // Agent Resilience Settings
    int MaxToolIterations { get; }
    Task SetMaxToolIterationsAsync(int value);
    int MaxConsecutiveMistakes { get; }
    Task SetMaxConsecutiveMistakesAsync(int value);
    int MaxAutoRetryAttempts { get; }
    Task SetMaxAutoRetryAttemptsAsync(int value);
    int MaxStreamRetries { get; }
    Task SetMaxStreamRetriesAsync(int value);
}

public class SettingsService : ISettingsService
{
    private readonly IProviderProfileRepository _providerRepo;
    private readonly ISystemPromptRepository _promptRepo;
    private readonly IUserPreferencesService _userPreferences;
    private readonly IAppSettingsRepository _appSettingsRepo;
    public event EventHandler? ProviderSettingsChanged;
    private bool _enableLogs;
    private bool _embeddingIndexingEnabled = true;
    private bool _autoHealEnabled = true;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _toolApprovalModes = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _executeCommandWhitelist = new();
    private string _workspaceExtensions = ".cs, .csx, .cshtml, .razor, .vb, .fs, .fsx, .csproj, .vbproj, .fsproj, .sln, .props, .targets, .config, .editorconfig, .resx, .settings, .manifest, .dll, .exe, .nuspec, .runtimeconfig.json, .js, .jsx, .ts, .tsx, .html, .htm, .css, .scss, .sass, .less, .vue, .json, .map, .mjs, .cjs, .vsixmanifest, .vsct, .pkgdef, .vstemplate, .vsix, .xaml, .xaml.cs, .resw, .sql, .dacpac, .bacpac, .edmx, .dbml, .mdf, .ldf, .xml, .yaml, .yml, .toml, .ini, .env, .appsettings.json, .csv, .md, .txt, .rst, .log, .py, .c, .cpp, .cc, .cxx, .h, .hpp, .java, .go, .rs, .php, .rb, .swift, .kt, .sh, .ps1, .bat, .cmd, .psm1, .gitignore, .gitattributes, .dockerfile, Dockerfile, .test.cs, .spec.ts, .testsettings, .runsettings";
    private string _language = "English";
    private string _country = "United States";
    
    // Resilience settings
    private int _maxToolIterations = 350;
    private int _maxConsecutiveMistakes = 12;
    private int _maxAutoRetryAttempts = 3;
    private int _maxStreamRetries = 3;
    
    public bool DeveloperPersonaEnabled { get; set; } = false;
    public string DeveloperPersona { get; set; } = string.Empty;

    public bool EmbeddingIndexingEnabled => _embeddingIndexingEnabled;
    public bool AutoHealEnabled => _autoHealEnabled;
    public bool AutoContinueOnInterrupt { get; set; } = true;
    public bool UseEnhancedPlanSystem { get; set; } = true; // Hidden feature flag
    public IReadOnlyList<string> ExecuteCommandWhitelist => _executeCommandWhitelist;
    public string WorkspaceExtensions => _workspaceExtensions;
    public string Language => _language;
    public string Country => _country;
    
    public int MaxToolIterations => _maxToolIterations;
    public int MaxConsecutiveMistakes => _maxConsecutiveMistakes;
    public int MaxAutoRetryAttempts => _maxAutoRetryAttempts;
    public int MaxStreamRetries => _maxStreamRetries;
    
    public bool EnableLogs 
    { 
        get => _enableLogs; 
        set 
        {
            _enableLogs = value;
            SaveEnableLogsPreference(value);
        }
    }

    public IUserPreferencesService UserPreferences { get; }

    public SettingsService(
        IProviderProfileRepository providerRepo,
        ISystemPromptRepository promptRepo,
        IUserPreferencesService userPreferences,
        IAppSettingsRepository appSettingsRepo)
    {
        _providerRepo = providerRepo ?? throw new ArgumentNullException(nameof(providerRepo));
        _promptRepo = promptRepo ?? throw new ArgumentNullException(nameof(promptRepo));
        _userPreferences = userPreferences ?? throw new ArgumentNullException(nameof(userPreferences));
        _appSettingsRepo = appSettingsRepo ?? throw new ArgumentNullException(nameof(appSettingsRepo));
        UserPreferences = _userPreferences;
        LoadEnableLogsPreference();
        _ = LoadAppSettingsAsync();
    }

    private async Task LoadAppSettingsAsync()
    {
        try
        {
            var semantic = await _appSettingsRepo.GetValueAsync("semantic_search_enabled");
            if (semantic != null) _embeddingIndexingEnabled = semantic == "true";

            var autoHeal = await _appSettingsRepo.GetValueAsync("auto_heal_enabled");
            if (autoHeal != null) _autoHealEnabled = autoHeal == "true";

            var tools = new[] { "read_files", "create_file", "replace_in_file", "execute_command", "execute_query", "get_database_schema" };
            foreach (var tool in tools)
            {
                var mode = await _appSettingsRepo.GetValueAsync($"approval_mode_{tool}");
                if (mode != null)
                {
                    _toolApprovalModes[tool] = mode;
                }
            }

            var whitelistStr = await _appSettingsRepo.GetValueAsync("execute_command_whitelist");
            if (whitelistStr != null)
            {
                _executeCommandWhitelist = whitelistStr.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                _executeCommandWhitelist = new List<string> {
                    "dir", "dir /a /s /b", "cd", "type", "more", "findstr", "tree /f", "where", "whoami", "hostname", "ver", "vol", "echo", "set",
                    "Get-ChildItem", "Get-Content", "Select-String", "Get-Location", "Test-Path", "Resolve-Path", "Get-Item",
                    "git status", "git log", "git log --oneline --graph", "git diff", "git diff --staged", "git show", "git branch",
                    "git branch -a", "git remote -v", "git blame", "git stash list", "git tag", "git describe", "git rev-parse HEAD",
                    "git fetch --dry-run",
                    "npm list", "npm outdated", "npm view", "npm ls",
                    "dotnet --version", "dotnet --list-sdks", "dotnet --list-runtimes", "dotnet list package", "dotnet list package --outdated", "dotnet --info",
                    "pip list", "pip show", "pip freeze", "nuget list",
                    "msbuild -version", "node -v", "npm -v", "python --version",
                    "tasklist", "tasklist /fi", "systeminfo",
                    "dotnet build", "dotnet test", "dotnet run", "dotnet publish", "msbuild",
                    "npm run build", "npm run dev", "npx",
                    "mkdir", "copy", "xcopy", "move", "ren",
                    "curl", "curl -o", "Invoke-WebRequest", "ping"
                };
            }

            var extensions = await _appSettingsRepo.GetValueAsync("workspace_extensions");
            if (extensions != null) _workspaceExtensions = extensions;

            var devPersonaEnabled = await _appSettingsRepo.GetValueAsync("dev_persona_enabled");
            if (devPersonaEnabled != null) DeveloperPersonaEnabled = devPersonaEnabled == "true";

            var devPersona = await _appSettingsRepo.GetValueAsync("dev_persona");
            if (devPersona != null) DeveloperPersona = devPersona;

            var language = await _appSettingsRepo.GetValueAsync("language");
            if (language != null) _language = language;

            var country = await _appSettingsRepo.GetValueAsync("country");
            if (country != null) _country = country;

            var maxTools = await _appSettingsRepo.GetValueAsync("resilience_max_tool_iterations");
            if (maxTools != null && int.TryParse(maxTools, out var maxToolsVal)) _maxToolIterations = maxToolsVal;

            var maxMistakes = await _appSettingsRepo.GetValueAsync("resilience_max_consecutive_mistakes");
            if (maxMistakes != null && int.TryParse(maxMistakes, out var maxMistakesVal)) _maxConsecutiveMistakes = maxMistakesVal;

            var maxRetries = await _appSettingsRepo.GetValueAsync("resilience_max_auto_retries");
            if (maxRetries != null && int.TryParse(maxRetries, out var maxRetriesVal)) _maxAutoRetryAttempts = maxRetriesVal;

            var maxStream = await _appSettingsRepo.GetValueAsync("resilience_max_stream_retries");
            if (maxStream != null && int.TryParse(maxStream, out var maxStreamVal)) _maxStreamRetries = maxStreamVal;
        }
        catch { }
    }

    public async Task SetEmbeddingIndexingEnabledAsync(bool value)
    {
        _embeddingIndexingEnabled = value;
        await _appSettingsRepo.SetValueAsync("semantic_search_enabled", value ? "true" : "false");
    }

    public async Task SetAutoHealEnabledAsync(bool value)
    {
        _autoHealEnabled = value;
        await _appSettingsRepo.SetValueAsync("auto_heal_enabled", value ? "true" : "false");
    }

    public string GetToolApprovalMode(string toolName)
    {
        // In the enhanced plan system, tool approval modes are still respected per-tool.
        // A per-tool setting takes precedence; fall back to "prompt" for mutation tools
        // and "allowall" for read-only tools if not explicitly configured.
        if (_toolApprovalModes.TryGetValue(toolName, out var mode))
            return mode;
        
        if (toolName == "read_files" || toolName == "execute_query" || toolName == "get_database_schema") return "allowall";
        return "prompt";
    }

    public async Task SetToolApprovalModeAsync(string toolName, string mode)
    {
        _toolApprovalModes[toolName] = mode;
        await _appSettingsRepo.SetValueAsync($"approval_mode_{toolName}", mode);
    }

    public async Task SetExecuteCommandWhitelistAsync(IEnumerable<string> whitelist)
    {
        _executeCommandWhitelist = whitelist.ToList();
        var joined = string.Join("\n", _executeCommandWhitelist);
        await _appSettingsRepo.SetValueAsync("execute_command_whitelist", joined);
    }

    public async Task SetWorkspaceExtensionsAsync(string extensions)
    {
        _workspaceExtensions = extensions;
        await _appSettingsRepo.SetValueAsync("workspace_extensions", extensions);
        ProviderSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLanguageAsync(string value)
    {
        _language = value;
        await _appSettingsRepo.SetValueAsync("language", value);
    }

    public async Task SetCountryAsync(string value)
    {
        _country = value;
        await _appSettingsRepo.SetValueAsync("country", value);
    }

    public async Task SetMaxToolIterationsAsync(int value)
    {
        _maxToolIterations = value;
        await _appSettingsRepo.SetValueAsync("resilience_max_tool_iterations", value.ToString());
    }

    public async Task SetMaxConsecutiveMistakesAsync(int value)
    {
        _maxConsecutiveMistakes = value;
        await _appSettingsRepo.SetValueAsync("resilience_max_consecutive_mistakes", value.ToString());
    }

    public async Task SetMaxAutoRetryAttemptsAsync(int value)
    {
        _maxAutoRetryAttempts = value;
        await _appSettingsRepo.SetValueAsync("resilience_max_auto_retries", value.ToString());
    }

    public async Task SetMaxStreamRetriesAsync(int value)
    {
        _maxStreamRetries = value;
        await _appSettingsRepo.SetValueAsync("resilience_max_stream_retries", value.ToString());
    }

    private void LoadEnableLogsPreference()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logPrefFile = Path.Combine(localAppData, "AiAssistant", "enable_logs.txt");
            if (File.Exists(logPrefFile))
            {
                var content = File.ReadAllText(logPrefFile).Trim();
                _enableLogs = content == "true";
            }
        }
        catch { }
    }

    private void SaveEnableLogsPreference(bool enabled)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "AiAssistant");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            AiAssistant.Storage.SafeFileWriter.WriteAllText(Path.Combine(dir, "enable_logs.txt"), enabled ? "true" : "false");
        }
        catch { }
    }

    public async Task<ProviderProfile?> GetDefaultProviderAsync()
    {
        var providers = await _providerRepo.GetAllAsync();
        return providers.FirstOrDefault(p => p.IsEnabled);
    }

    public async Task<IReadOnlyList<ProviderProfile>> GetAllProvidersAsync()
    {
        return (await _providerRepo.GetAllAsync()).ToList();
    }

    public async Task<ProviderProfile> SaveProviderAsync(ProviderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            profile = profile with { Id = Guid.NewGuid().ToString() };

        var existing = await _providerRepo.GetByIdAsync(profile.Id);
        ProviderProfile result;
        if (existing != null)
            result = await _providerRepo.UpdateAsync(profile);
        else
            result = await _providerRepo.AddAsync(profile);

        ProviderSettingsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task DeleteProviderAsync(string id)
    {
        await _providerRepo.DeleteAsync(id);
        ProviderSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SystemPrompt?> GetDefaultPromptAsync()
    {
        return await _promptRepo.GetDefaultAsync();
    }

    public async Task<IReadOnlyList<SystemPrompt>> GetAllPromptsAsync()
    {
        return (await _promptRepo.GetAllAsync()).ToList();
    }

    public async Task<SystemPrompt> SavePromptAsync(SystemPrompt prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt.Id))
            prompt = prompt with { Id = Guid.NewGuid().ToString() };

        var existing = await _promptRepo.GetByIdAsync(prompt.Id);
        if (existing != null)
            return await _promptRepo.UpdateAsync(prompt);

        return await _promptRepo.AddAsync(prompt);
    }

    public async Task DeletePromptAsync(string id)
    {
        await _promptRepo.DeleteAsync(id);
    }

    public bool GetBoolSetting(string key, bool defaultValue)
    {
        try
        {
            var value = _appSettingsRepo.GetValueAsync(key).GetAwaiter().GetResult();
            return value == "true";
        }
        catch
        {
            return defaultValue;
        }
    }

    public async Task SetBoolSettingAsync(string key, bool value)
    {
        await _appSettingsRepo.SetValueAsync(key, value ? "true" : "false");
    }
}
