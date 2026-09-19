using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using System.Windows;
using System.Windows.Controls;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using AiAssistant.Storage.Services;
using Microsoft.Extensions.AI;

namespace AiAssistant.UI.Controls;

public partial class SettingsView : UserControl, INotifyPropertyChanged
{
    private IProviderProfileRepository? _providerRepo;
    private ISystemPromptRepository? _promptRepo;
    private IPersonaService? _personaService;
    private IDatabaseConnectionService? _dbService;
    private IModelCacheRepository? _modelCacheRepo;
    private AiAssistant.Llm.Services.IChatClientFactory? _chatClientFactory;
    private ITelemetryService? _telemetryService;
    private ISettingsService? _settingsService;
    private IOutputLogger? _outputLogger;
    private IProviderChangeNotifier? _providerChangeNotifier;
    private IModelFetchService? _modelFetchService;
    private IUserPreferencesService? _userPreferences;
    private UICustomizationPreferences? _uiPrefs;
    private List<string> _allDiscoveredServers = new();
    private bool _isLoadingProfile = false;
    private string _errorMessage = "";
    private ProviderProfile? _editingProviderProfile;
    private DatabaseConnection? _editingDbConnection;
    private AiAssistant.Storage.Models.SystemPrompt? _editingPersona;
    private System.Collections.ObjectModel.ObservableCollection<string> _workspaceExtensionsList = new();
    private System.Collections.ObjectModel.ObservableCollection<string> _commandWhitelist = new();

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    private bool _servicesInitialized = false;
    private bool _isLoaded = false;

    public SettingsView() : this(null, null, null, null, null, null, null, null, null, null, null) { }

    public SettingsView(IProviderProfileRepository? providerRepo, ISystemPromptRepository? promptRepo,
        IPersonaService? personaService, IDatabaseConnectionService? dbService,
        IModelCacheRepository? modelCacheRepo, AiAssistant.Llm.Services.IChatClientFactory? chatClientFactory,
        ITelemetryService? telemetryService, ISettingsService? settingsService, IOutputLogger? outputLogger,
        IProviderChangeNotifier? providerChangeNotifier, IModelFetchService? modelFetchService)
    {
        _providerRepo = providerRepo;
        _promptRepo = promptRepo;
        _personaService = personaService;
        _dbService = dbService;
        _modelCacheRepo = modelCacheRepo;
        _chatClientFactory = chatClientFactory;
        _telemetryService = telemetryService;
        _settingsService = settingsService;
        _outputLogger = outputLogger;
        _providerChangeNotifier = providerChangeNotifier;
        _modelFetchService = modelFetchService;
        _userPreferences = _settingsService?.UserPreferences;
        _servicesInitialized = true;
        Helpers.ThemeManager.Initialize();

        // Load theme resources BEFORE parsing XAML so StaticResource lookups succeed
        Helpers.ThemeManager.ThemeChanged += OnThemeChanged;
        if (_userPreferences != null)
        {
            _userPreferences.PreferencesChanged += OnPreferencesChanged;
        }
        OnThemeChanged(null, EventArgs.Empty);

        InitializeComponent();
        DataContext = this;

        Loaded += SettingsView_Loaded;
    }

    /// <summary>
    /// Sets services after construction when used as an in-panel overlay.
    /// </summary>
    public void SetServices(IProviderProfileRepository? providerRepo, ISystemPromptRepository? promptRepo,
        IPersonaService? personaService, IDatabaseConnectionService? dbService,
        IModelCacheRepository? modelCacheRepo, AiAssistant.Llm.Services.IChatClientFactory? chatClientFactory,
        ITelemetryService? telemetryService, ISettingsService? settingsService, IOutputLogger? outputLogger,
        IProviderChangeNotifier? providerChangeNotifier, IModelFetchService? modelFetchService)
    {
        _providerRepo = providerRepo;
        _promptRepo = promptRepo;
        _personaService = personaService;
        _dbService = dbService;
        _modelCacheRepo = modelCacheRepo;
        _chatClientFactory = chatClientFactory;
        _telemetryService = telemetryService;
        _settingsService = settingsService;
        _outputLogger = outputLogger;
        _providerChangeNotifier = providerChangeNotifier;
        _modelFetchService = modelFetchService;
        _userPreferences = _settingsService?.UserPreferences;
        _servicesInitialized = true;

        // If already loaded (overlay was created before services were set), trigger data load
        if (_isLoaded)
        {
            _ = SettingsView_LoadedAsync();
        }
    }

    public void SelectProviderTab()
    {
        if (MainTabControl != null)
        {
            MainTabControl.SelectedIndex = 1;
        }
    }

    private void ListView_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            e.Handled = true;
            var eventArg = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            var parent = ((Control)sender).Parent as UIElement;
            parent?.RaiseEvent(eventArg);
        }
    }
    private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) return;
        _isLoaded = true;

        if (!_servicesInitialized) return;
        await SettingsView_LoadedAsync();
    }

    private async System.Threading.Tasks.Task SettingsView_LoadedAsync()
    {
        // Load theme preference
        LoadThemePreference();

        // Load telemetry preference
        LoadTelemetryPreference();

        await LoadProvidersAsync();

        await LoadPersonasAsync();
        await LoadDatabaseConnectionsAsync();
        
        if (_settingsService != null)
        {
            SemanticSearchCheck.IsChecked = _settingsService.EmbeddingIndexingEnabled;
            
            /*
            if (!string.IsNullOrEmpty(_settingsService.Language))
            {
                foreach (ComboBoxItem item in LanguageCombo.Items)
                {
                    if (item.Content.ToString() == _settingsService.Language)
                    {
                        LanguageCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            
            if (!string.IsNullOrEmpty(_settingsService.Country))
            {
                foreach (ComboBoxItem item in CountryCombo.Items)
                {
                    if (item.Content.ToString() == _settingsService.Country)
                    {
                        CountryCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            */
            AutoHealCheck.IsChecked = _settingsService.AutoHealEnabled;
            
            MaxToolIterationsBox.Text = _settingsService.MaxToolIterations.ToString();
            MaxConsecutiveMistakesBox.Text = _settingsService.MaxConsecutiveMistakes.ToString();
            MaxAutoRetryBox.Text = _settingsService.MaxAutoRetryAttempts.ToString();
            MaxStreamRetriesBox.Text = _settingsService.MaxStreamRetries.ToString();
            
            _workspaceExtensionsList.Clear();
            var extensionsStr = _settingsService.WorkspaceExtensions;
            if (!string.IsNullOrWhiteSpace(extensionsStr))
            {
                var parts = extensionsStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    _workspaceExtensionsList.Add(p);
                }
            }
            ExtensionsListBox.ItemsSource = _workspaceExtensionsList;
            
            SetComboItem(ReadFilesApprovalCombo, _settingsService.GetToolApprovalMode("read_files"));
            SetComboItem(CreateFileApprovalCombo, _settingsService.GetToolApprovalMode("create_file"));
            SetComboItem(ReplaceFileApprovalCombo, _settingsService.GetToolApprovalMode("replace_in_file"));
            SetComboItem(ExecuteCommandApprovalCombo, _settingsService.GetToolApprovalMode("execute_command"));
            SetComboItem(DatabaseQueryApprovalCombo, _settingsService.GetToolApprovalMode("execute_query"));
            SetComboItem(DatabaseSchemaApprovalCombo, _settingsService.GetToolApprovalMode("get_database_schema"));

            _commandWhitelist.Clear();
            var whitelist = _settingsService.ExecuteCommandWhitelist;
            if (whitelist != null)
            {
                foreach (var cmd in whitelist) _commandWhitelist.Add(cmd);
            }
            CommandsListBox.ItemsSource = _commandWhitelist;
            CommandWhitelistPanel.Visibility = (_settingsService.GetToolApprovalMode("execute_command") == "allowall") ? Visibility.Visible : Visibility.Collapsed;
        }

        await LoadUiPreferencesAsync();
        UpdateFontPreviewLabels();
        CheckModelFiles();
        _ = CheckGitStatusAsync();

    }

    private async Task CheckGitStatusAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                await Task.Run(() => process.WaitForExit());
                if (process.ExitCode == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        GitStatusIcon.Text = "✅";
                        GitStatusText.Text = "Git is installed and available. AI checkpoints are enabled.";
                        GitStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    });
                    return;
                }
            }
        }
        catch { }

        await Dispatcher.InvokeAsync(() =>
        {
            GitStatusIcon.Text = "❌";
            GitStatusText.Text = "Git is not installed or not in PATH. AI checkpoints feature is disabled. Please install Git to use checkpoints.";
            GitStatusText.SetResourceReference(TextBlock.ForegroundProperty, "ErrorRedBrush");
        });
    }

    /// <summary>
    /// Raised when the user clicks the Back button in the in-panel settings overlay.
    /// </summary>
    public event EventHandler? ReturnToChat;

    /// <summary>
    /// Gets or sets whether the back button header is visible.
    /// Used when SettingsView is hosted as an in-panel overlay.
    /// </summary>
    public bool ShowBackButton
    {
        get => SettingsHeaderGrid?.Visibility == Visibility.Visible;
        set
        {
            if (SettingsHeaderGrid != null)
                SettingsHeaderGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ReturnToChat?.Invoke(this, EventArgs.Empty);
    }

    private void FullScreenBtn_Click(object sender, RoutedEventArgs e)
    {
        // Open SettingsWindow in full screen
        var window = new AiAssistant.UI.Windows.SettingsWindow(
            _providerRepo!, _promptRepo!, _personaService!, _dbService!,
            _modelCacheRepo!, _chatClientFactory!, _settingsService!,
            _providerChangeNotifier!, _modelFetchService!);
            
        window.WindowState = WindowState.Maximized;
        window.ShowDialog();
    }

    private void LoadThemePreference()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var themeFile = Path.Combine(localAppData, AiAssistantPackage.PackageGuidString, "theme.txt");
            if (File.Exists(themeFile))
            {
                var theme = File.ReadAllText(themeFile).Trim();
                if (theme == "light")
                {
                    ThemeLightPill.IsChecked = true;
                }
                else if (theme == "dark")
                {
                    ThemeDarkPill.IsChecked = true;
                }
                else
                {
                    ThemeAutoPill.IsChecked = true;
                }
            }

            if (_settingsService != null)
            {
                EnableLogsCheck.IsChecked = _settingsService.EnableLogs;
            }
        }
        catch { }
    }

    private void LoadTelemetryPreference()
    {
        try
        {
            if (_telemetryService != null)
            {
                // Use async fire-and-forget since we're in a sync event handler
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var enabled = await _telemetryService.IsEnabledAsync();
                        // Telemetry check removed from UI
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Telemetry check failed: {ex.Message}");
                    }
                });
            }
        }
        catch { }
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            if (_providerRepo == null) return;
            var providers = await _providerRepo.GetAllAsync();
            ProvidersList.ItemsSource = providers.ToList();

            // Select the first enabled provider
            var defaultProvider = providers.FirstOrDefault(p => p.IsEnabled);
            if (defaultProvider != null)
            {
                ProviderTypeCombo.SelectedIndex = defaultProvider.ProviderType == "azureopenai" ? 1 : 0;
                ProviderNameText.Text = defaultProvider.Name;
                ApiEndpointText.Text = defaultProvider.ApiEndpoint ?? "";
                ModelFetchEndpointText.Text = defaultProvider.ModelFetchEndpoint ?? "";
                if (!string.IsNullOrEmpty(defaultProvider.DefaultModel))
                {
                    ModelPicker.SelectedModel = new AiAssistant.Core.Services.ModelInfo(defaultProvider.DefaultModel, defaultProvider.DefaultModel, 0, null, null, false);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load providers: {ex.Message}";
        }
    }

    private void ProvidersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProvidersList.SelectedItem is ProviderProfile profile)
        {
            _isLoadingProfile = true;
            ProviderNameText.Text = profile.Name;
            ProviderTypeCombo.SelectedIndex = profile.ProviderType switch
            {
                "azureopenai" => 1,
                "openrouter" => 2,
                "ollama" => 3,
                _ => 0
            };
            ApiEndpointText.Text = profile.ApiEndpoint ?? "";
            ModelFetchEndpointText.Text = profile.ModelFetchEndpoint ?? "";
            ApiKeyBox.Password = profile.ApiKey ?? ""; // Pre-load API key
            
            if (!string.IsNullOrEmpty(profile.DefaultModel))
            {
                ModelPicker.SelectedModel = new AiAssistant.Core.Services.ModelInfo(profile.DefaultModel, profile.DefaultModel, 0, null, null, false);
            }
            
            _isLoadingProfile = false;
            _ = AutoFetchModelsAsync();
        }
    }

    private void ProviderOptionsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void DeleteProviderFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderProfile profile)
        {
            await DeleteProviderProfileAsync(profile);
        }
    }

    
    private void AddProviderBtn_Click(object sender, RoutedEventArgs e)
    {
        ProviderEditTitle.Text = "Add provider";
        ProviderNameText.Text = "";
        ProviderTypeCombo.SelectedIndex = 0;
        ApiKeyBox.Password = "";
        ApiEndpointText.Text = "https://api.openai.com/v1";
        ModelFetchEndpointText.Text = "https://api.openai.com/v1/models";
        ProvidersList.SelectedItem = null;
        _editingProviderProfile = null;
        
        // Ensure fields are editable for custom providers
        ProviderNameText.IsEnabled = true;
        ApiEndpointText.IsEnabled = true;
        ModelFetchEndpointText.IsEnabled = true;
        ProviderTypeCombo.IsEnabled = true;

        RemoveProviderBtn.Visibility = Visibility.Collapsed;
        ProviderErrorTextBlock.Visibility = Visibility.Collapsed;
        ProviderNameErrorText.Visibility = Visibility.Collapsed;
        ApiKeyErrorText.Visibility = Visibility.Collapsed;
        ApiEndpointErrorText.Visibility = Visibility.Collapsed;
        ModelFetchEndpointErrorText.Visibility = Visibility.Collapsed;
        ProviderEditOverlay.Visibility = Visibility.Visible;
    }

    private void CloseProviderEditBtn_Click(object sender, RoutedEventArgs e)
    {
        ProviderEditOverlay.Visibility = Visibility.Collapsed;
    }

    private void ProviderEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            var profile = ((IEnumerable<ProviderProfile>)ProvidersList.ItemsSource).FirstOrDefault(p => p.Id == id);
            if (profile != null)
            {
                _editingProviderProfile = profile;
                ProvidersList.SelectedItem = null;
                ProviderEditTitle.Text = profile.IsBuiltIn ? "Connect built-in provider" : "Edit provider";
                
                // Populate fields with the selected provider's info
                _isLoadingProfile = true;
                ProviderNameText.Text = profile.Name;
                ProviderTypeCombo.SelectedIndex = profile.ProviderType switch
                {
                    "azureopenai" => 1,
                    "openrouter" => 2,
                    "ollama" => 3,
                    _ => 0
                };
                ApiEndpointText.Text = profile.ApiEndpoint ?? "";
                ModelFetchEndpointText.Text = profile.ModelFetchEndpoint ?? "";
                ApiKeyBox.Password = profile.ApiKey ?? ""; // Pre-load API key
                
                if (!string.IsNullOrEmpty(profile.DefaultModel))
                {
                    ModelPicker.SelectedModel = new AiAssistant.Core.Services.ModelInfo(profile.DefaultModel, profile.DefaultModel, 0, null, null, false);
                }
                
                _isLoadingProfile = false;
                _ = AutoFetchModelsAsync();

                // Lock fields if built-in
                bool isCustom = !profile.IsBuiltIn;
                ProviderNameText.IsEnabled = isCustom;
                ApiEndpointText.IsEnabled = isCustom;
                ModelFetchEndpointText.IsEnabled = isCustom;
                ProviderTypeCombo.IsEnabled = isCustom;
                
                RemoveProviderBtn.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                ProviderErrorTextBlock.Visibility = Visibility.Collapsed;
                ProviderNameErrorText.Visibility = Visibility.Collapsed;
                ApiKeyErrorText.Visibility = Visibility.Collapsed;
                ApiEndpointErrorText.Visibility = Visibility.Collapsed;
                ModelFetchEndpointErrorText.Visibility = Visibility.Collapsed;
                ProviderEditOverlay.Visibility = Visibility.Visible;
            }
        }
    }

    private async void RemoveProviderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersList.SelectedItem is ProviderProfile profile)
        {
            await DeleteProviderProfileAsync(profile);
            ProviderEditOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task DeleteProviderProfileAsync(ProviderProfile profile)
    {
        try
        {
            ErrorMessage = "";
            if (_providerRepo == null) { ErrorMessage = "Provider repository not available."; return; }

                var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                    "Delete Provider", $"Delete provider '{profile.Name}'?",
                    confirmText: "Delete", isDestructive: true);
                if (confirmed)
                {
                    await _providerRepo.DeleteAsync(profile.Id);
                    await LoadProvidersAsync();
                    _providerChangeNotifier?.NotifyProvidersChanged();
                    
                    if (ProvidersList.SelectedItem == profile || ProvidersList.SelectedItem == null)
                    {
                        ProvidersList.SelectedItem = null;
                        ProviderNameText.Text = "OpenAI";
                        ApiKeyBox.Password = "";
                        ApiEndpointText.Text = "https://api.openai.com/v1";
                    }
                }
            }
        catch (Exception ex) { ErrorMessage = $"Failed to delete provider: {ex.Message}"; }

    }
    


    private void ProviderTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProfile) return;
        if (ProviderTypeCombo.SelectedItem is not ComboBoxItem item) return;
        if (ProviderNameText == null || ApiEndpointText == null || ModelPicker == null) return;
        var type = item.Tag?.ToString();
        ProviderNameText.Text = type switch
        {
            "azureopenai" => "Azure OpenAI",
            "openrouter" => "OpenRouter",
            "ollama" => "Ollama",
            _ => "OpenAI"
        };
        ApiEndpointText.Text = type switch
        {
            "azureopenai" => "",
            "openrouter" => "https://openrouter.ai/api/v1",
            "ollama" => "http://localhost:11434",
            _ => "https://api.openai.com/v1"
        };
        ModelFetchEndpointText.Text = type switch
        {
            "azureopenai" => "",
            "openrouter" => "https://openrouter.ai/api/v1/models",
            "ollama" => "http://localhost:11434/api/tags",
            _ => "https://api.openai.com/v1/models"
        };

        _ = AutoFetchModelsAsync();
    }

    private async void FetchModelsBtn_Click(object sender, RoutedEventArgs e)
    {
        await AutoFetchModelsAsync();
    }

    private void ModelPicker_RetryClicked(object sender, EventArgs e)
    {
        _ = AutoFetchModelsAsync();
    }

    private async Task AutoFetchModelsAsync()
    {
        if (ModelPicker == null) return;
        
        if (_modelFetchService == null)
        {
            ModelPicker.ShowError("Error: Internal ModelFetchService is not available.");
            return;
        }

        var type = ProviderTypeCombo.SelectedItem is ComboBoxItem item ? item.Tag?.ToString() : null;
        if (string.IsNullOrEmpty(type))
        {
            ModelPicker.ShowError("Error: No provider type selected.");
            return;
        }

        var apiKey = ApiKeyBox.Password;
        var endpoint = ApiEndpointText.Text.Trim();
        var fetchEndpoint = ModelFetchEndpointText.Text.Trim();

        _outputLogger?.Log($"AutoFetchModelsAsync triggered for provider '{type}' with endpoint '{endpoint}' and fetchEndpoint '{fetchEndpoint}'");

        try
        {
            ModelPicker.ShowLoading();
            var models = await _modelFetchService.FetchModelsAsync(type, apiKey, endpoint, fetchEndpoint);
            _outputLogger?.Log($"FetchModelsAsync returned {models.Count} models.");
            if (models.Count == 0)
            {
                ModelPicker.ShowError("No models found or authentication failed.");
            }
            else
            {
                ModelPicker.SetModels(models);
            }
        }
        catch (Exception ex)
        {
            ModelPicker.ShowError($"Failed to fetch: {ex.Message}");
        }
    }

    private async void SaveProviderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            ProviderErrorTextBlock.Visibility = Visibility.Collapsed;
            ProviderNameErrorText.Visibility = Visibility.Collapsed;
            ApiKeyErrorText.Visibility = Visibility.Collapsed;
            ApiEndpointErrorText.Visibility = Visibility.Collapsed;
            ModelFetchEndpointErrorText.Visibility = Visibility.Collapsed;
            
            bool hasError = false;

            if (_providerRepo == null)
            {
                ProviderErrorTextBlock.Text = "Provider repository not available.";
                ProviderErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }

            var name = ProviderNameText.Text.Trim();
            if (string.IsNullOrEmpty(name)) 
            { 
                ProviderNameErrorText.Text = "Provider name is required."; 
                ProviderNameErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            var providerTypeTag = ProviderTypeCombo.SelectedItem is ComboBoxItem item
                ? (item.Tag?.ToString() ?? "openai") : "openai";

            var apiKey = ApiKeyBox.Password;
            var endpoint = ApiEndpointText.Text.Trim();
            var modelFetchEndpoint = ModelFetchEndpointText.Text.Trim();

            if (string.IsNullOrEmpty(apiKey))
            {
                ApiKeyErrorText.Text = "API key is required.";
                ApiKeyErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            if (string.IsNullOrEmpty(endpoint))
            {
                ApiEndpointErrorText.Text = "API endpoint is required.";
                ApiEndpointErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            if (string.IsNullOrEmpty(modelFetchEndpoint))
            {
                ModelFetchEndpointErrorText.Text = "Model fetch endpoint is required.";
                ModelFetchEndpointErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            if (hasError) return;

            var editingProvider = _editingProviderProfile;
            var allProviders = await _providerRepo.GetAllAsync();
            
            if (allProviders.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (editingProvider == null || p.Id != editingProvider.Id)))
            {
                ProviderNameErrorText.Text = $"A provider with the name '{name}' already exists."; 
                ProviderNameErrorText.Visibility = Visibility.Visible;
                return;
            }

            var existing = editingProvider;
            var profile = (existing ?? new ProviderProfile { Id = Guid.NewGuid().ToString() }) with
            {
                Name = existing?.IsBuiltIn == true ? existing.Name : name,
                ProviderType = existing?.IsBuiltIn == true ? existing.ProviderType : providerTypeTag,
                ApiEndpoint = existing?.IsBuiltIn == true ? existing.ApiEndpoint : endpoint,
                ModelFetchEndpoint = existing?.IsBuiltIn == true ? existing.ModelFetchEndpoint : modelFetchEndpoint,
                DefaultModel = ModelPicker.SelectedModel?.ModelId ?? (existing?.DefaultModel ?? "gpt-4o"),
                IsEnabled = true
            };

            if (ModelPicker.SelectedModel != null && ModelPicker.SelectedModel.ContextWindowTokens > 0)
            {
                profile = profile with { ContextWindowTokens = ModelPicker.SelectedModel.ContextWindowTokens };
            }

            if (!string.IsNullOrEmpty(apiKey) || existing == null)
            {
                profile = profile with { ApiKey = apiKey };
            }
            else if (string.IsNullOrEmpty(apiKey) && existing != null && existing.IsBuiltIn)
            {
                profile = profile with { ApiKey = null }; // allow clearing api key for built-in
            }

            if (existing != null)
                await _providerRepo.UpdateAsync(profile);
            else
                await _providerRepo.AddAsync(profile);
            
            if (_modelCacheRepo != null && !string.IsNullOrEmpty(profile.ApiKey))
            {
                await _modelCacheRepo.RefreshFromApiAsync(profile.ProviderType, profile.ApiKey);
            }
            
            await LoadProvidersAsync();
            _providerChangeNotifier?.NotifyProvidersChanged();
            await ConfirmOverlay.ShowInfoAsync("Settings", "Provider saved successfully!");
            ProviderEditOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save provider: {ex.Message}";
        }
    }

    private async void TestConnectionBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            ProviderErrorTextBlock.Visibility = Visibility.Collapsed;
            ProviderNameErrorText.Visibility = Visibility.Collapsed;
            ApiKeyErrorText.Visibility = Visibility.Collapsed;
            ApiEndpointErrorText.Visibility = Visibility.Collapsed;
            ModelFetchEndpointErrorText.Visibility = Visibility.Collapsed;
            
            bool hasError = false;

            if (_chatClientFactory == null)
            {
                ProviderErrorTextBlock.Text = "Chat client factory not available.";
                ProviderErrorTextBlock.Visibility = Visibility.Visible;
                return;
            }

            var providerType = ProviderTypeCombo.SelectedItem is ComboBoxItem item
                ? (item.Tag?.ToString() ?? "openai") : "openai";
            var apiKey = ApiKeyBox.Password;
            var apiEndpoint = ApiEndpointText.Text.Trim();
            var model = ModelPicker.SelectedModel?.ModelId ?? "gpt-4o";

            if (string.IsNullOrEmpty(apiKey))
            {
                ApiKeyErrorText.Text = "Please enter an API key before testing.";
                ApiKeyErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            if (string.IsNullOrEmpty(apiEndpoint))
            {
                ApiEndpointErrorText.Text = "Please enter an API endpoint before testing.";
                ApiEndpointErrorText.Visibility = Visibility.Visible;
                hasError = true;
            }

            if (hasError) return;

            // Show testing indicator
            var originalContent = TestConnectionBtn.Content;
            TestConnectionBtn.Content = "⏳ Testing...";
            TestConnectionBtn.IsEnabled = false;

            try
            {
                var client = _chatClientFactory.CreateClient(providerType, model, apiKey, apiEndpoint);

                // Send a simple "Hello" prompt and collect the response
                var response = await client.GetResponseAsync("Hello");
                var responseText = response.Text ?? "(empty response)";

                // Truncate long responses for display
                if (responseText.Length > 200)
                    responseText = responseText.Substring(0, 200) + "...";

                await ConfirmOverlay.ShowInfoAsync(
                    "Test Connection",
                    $"✅ Connection successful!\n\nProvider: {providerType}\nModel: {model}\nResponse: {responseText}");
            }
            finally
            {
                TestConnectionBtn.Content = originalContent;
                TestConnectionBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _ = ConfirmOverlay.ShowInfoAsync("Connection Failed", $"Connection failed.\n\nDetails:\n{ex.Message}");
        }
    }



    // ===== PERSONA MANAGEMENT =====

    private async Task LoadPersonasAsync()
    {
        try
        {
            if (_personaService == null) return;
            var personas = await _personaService.GetAllPersonasAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                PersonasList.ItemsSource = personas.ToList();
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load personas: {ex.Message}";
        }
    }



    private void PersonaEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            var persona = ((IEnumerable<SystemPrompt>)PersonasList.ItemsSource).FirstOrDefault(p => p.Id == id);
            if (persona != null)
            {
                _editingPersona = persona;
                PersonasList.SelectedItem = null; // Clear to prevent SelectionChanged from firing again unnecessarily
                PersonaEditTitle.Text = "Edit persona";
                PersonaNameText.Text = persona.Name;
                PersonaDescText.Text = persona.Content;
                PersonaEditErrorText.Visibility = Visibility.Collapsed;
                PersonaNameErrorText.Visibility = Visibility.Collapsed;
                PersonaDescErrorText.Visibility = Visibility.Collapsed;
                DeletePersonaBtn.Visibility = persona.IsBuiltIn ? Visibility.Collapsed : Visibility.Visible;
                PersonaEditOverlay.Visibility = Visibility.Visible;
            }
        }
    }

    private void AddPersonaBtn_Click(object sender, RoutedEventArgs e)
    {
        _editingPersona = null;
        PersonaEditTitle.Text = "Add persona";
        PersonaNameText.Text = "";
        PersonaDescText.Text = "";
        PersonaEditErrorText.Visibility = Visibility.Collapsed;
        PersonaNameErrorText.Visibility = Visibility.Collapsed;
        PersonaDescErrorText.Visibility = Visibility.Collapsed;
        PersonasList.SelectedItem = null;
        DeletePersonaBtn.Visibility = Visibility.Collapsed;
        PersonaEditOverlay.Visibility = Visibility.Visible;
    }

    private void ClosePersonaEditBtn_Click(object sender, RoutedEventArgs e)
    {
        PersonaEditOverlay.Visibility = Visibility.Collapsed;
    }

    private async void DeletePersonaFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            var persona = ((IEnumerable<SystemPrompt>)PersonasList.ItemsSource).FirstOrDefault(p => p.Id == id);
            if (persona != null) await DeletePersonaInternalAsync(persona);
        }
    }

    private async void DeletePersonaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPersona != null)
        {
            await DeletePersonaInternalAsync(_editingPersona);
            PersonaEditOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async Task DeletePersonaInternalAsync(SystemPrompt persona)
    {
        try
        {
            ErrorMessage = "";
            if (_personaService == null) { ErrorMessage = "Persona service not available."; return; }
            
            var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                "Delete Persona", $"Delete persona '{persona.Name}'?",
                confirmText: "Delete", isDestructive: true);
            if (confirmed)
            {
                var wasDefault = persona.IsDefault;
                await _personaService.DeletePersonaAsync(persona.Id);
                
                if (wasDefault)
                {
                    var remaining = await _personaService.GetAllPersonasAsync();
                    var newDefault = remaining.FirstOrDefault();
                    if (newDefault != null)
                    {
                        await _personaService.SetDefaultPersonaAsync(newDefault.Id);
                    }
                }

                await LoadPersonasAsync();
            }
        }
        catch (Exception ex) { ErrorMessage = $"Failed to delete persona: {ex.Message}"; }
    }

    private async void UpdatePersonaBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersonaEditErrorText.Visibility = Visibility.Collapsed;
            PersonaNameErrorText.Visibility = Visibility.Collapsed;
            PersonaDescErrorText.Visibility = Visibility.Collapsed;
            if (_personaService == null) { PersonaEditErrorText.Text = "Persona service not available."; PersonaEditErrorText.Visibility = Visibility.Visible; return; }
            
            var name = PersonaNameText.Text.Trim();
            var desc = PersonaDescText.Text.Trim();
            bool hasError = false;
            
            if (string.IsNullOrEmpty(name)) 
            { 
                PersonaNameErrorText.Text = "Persona name is required."; 
                PersonaNameErrorText.Visibility = Visibility.Visible; 
                hasError = true;
            }
            if (string.IsNullOrEmpty(desc)) 
            { 
                PersonaDescErrorText.Text = "Persona description is required."; 
                PersonaDescErrorText.Visibility = Visibility.Visible; 
                hasError = true;
            }
            if (hasError) return;

            var allPersonas = await _personaService.GetAllPersonasAsync();
            if (allPersonas.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (_editingPersona == null || p.Id != _editingPersona.Id)))
            {
                PersonaNameErrorText.Text = $"A persona with the name '{name}' already exists."; 
                PersonaNameErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (_editingPersona != null)
            {
                var updated = _editingPersona with { Name = name, Content = desc };
                await _personaService.UpdatePersonaAsync(updated);
                await ConfirmOverlay.ShowInfoAsync("Persona", "Persona updated!");
            }
            else
            {
                await _personaService.CreatePersonaAsync(name, desc);
                await ConfirmOverlay.ShowInfoAsync("Persona", "Persona created!");
            }
            
            await LoadPersonasAsync();
            PersonaEditOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { PersonaEditErrorText.Text = $"Failed to save persona: {ex.Message}"; PersonaEditErrorText.Visibility = Visibility.Visible; }
    }

    private async void SetDefaultPersonaBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PersonaEditErrorText.Visibility = Visibility.Collapsed;
            if (_personaService == null) { PersonaEditErrorText.Text = "Persona service not available."; PersonaEditErrorText.Visibility = Visibility.Visible; return; }
            if (_editingPersona == null) { PersonaEditErrorText.Text = "Select a persona to set as default."; PersonaEditErrorText.Visibility = Visibility.Visible; return; }

            await _personaService.SetDefaultPersonaAsync(_editingPersona.Id);
            await LoadPersonasAsync();
            await ConfirmOverlay.ShowInfoAsync("Persona", $"'{_editingPersona.Name}' is now the default persona.");
            PersonaEditOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { PersonaEditErrorText.Text = $"Failed to set default: {ex.Message}"; PersonaEditErrorText.Visibility = Visibility.Visible; }
    }

    // ===== DATABASE CONNECTIONS =====

    private async Task LoadDatabaseConnectionsAsync()
    {
        try
        {
            if (_dbService == null) return;
            var connections = await _dbService.GetAllConnectionsAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                var connList = connections.ToList();
                DatabaseConnectionsList.ItemsSource = connList;
                if (DbEmptyState != null)
                {
                    DbEmptyState.Visibility = connList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load database connections: {ex.Message}";
        }
    }



    private void DbAuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbCredentialsGrid != null)
        {
            DbCredentialsGrid.Visibility = DbAuthCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    
    private void AddDbBtn_Click(object sender, RoutedEventArgs e)
    {
        DbEditTitle.Text = "Add connection";
        DbNameText.Text = "";
        DbServerText.Text = "";
        DbDatabaseText.Text = "";
        DbUsernameText.Text = "";
        DbPasswordBox.Password = "";
        DbAuthCombo.SelectedIndex = 0;
        DbPermissionCombo.SelectedIndex = 0;
        DbRequireApprovalCheck.IsChecked = true;
        DatabaseConnectionsList.SelectedItem = null;
        _editingDbConnection = null;

        DbEditOverlay.Visibility = Visibility.Visible;
        
        _ = DiscoverSqlServersAsync();
    }

    private void CloseDbEditBtn_Click(object sender, RoutedEventArgs e)
    {
        DbEditOverlay.Visibility = Visibility.Collapsed;
    }

    private void DbEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            var conn = ((IEnumerable<DatabaseConnection>)DatabaseConnectionsList.ItemsSource).FirstOrDefault(c => c.Id == id);
            if (conn != null)
            {
                _editingDbConnection = conn;
                DatabaseConnectionsList.SelectedItem = null;
                
                DbNameText.Text = conn.Name;
                DbServerText.Text = conn.ServerAddress;
                DbDatabaseText.Text = conn.DatabaseName;
                DbAuthCombo.SelectedIndex = conn.Authentication == AuthenticationType.SqlAuth ? 1 : 0;
                DbUsernameText.Text = conn.Username ?? "";
                DbPasswordBox.Password = "";
                DbPermissionCombo.SelectedIndex = (int)conn.Permission;
                DbRequireApprovalCheck.IsChecked = conn.RequireApproval;
                
                if (DbCredentialsGrid != null)
                    DbCredentialsGrid.Visibility = conn.Authentication == AuthenticationType.SqlAuth ? Visibility.Visible : Visibility.Collapsed;
                    
                DbEditTitle.Text = "Edit connection";
                DbEditOverlay.Visibility = Visibility.Visible;
                
                _ = DiscoverSqlServersAsync();
            }
        }
    }

    private async void DeleteDbFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DatabaseConnection conn)
        {
            await DeleteDatabaseConnectionAsync(conn);
        }
    }
    
    private async Task DeleteDatabaseConnectionAsync(DatabaseConnection conn)
    {
        try
        {
            ErrorMessage = "";
            if (_dbService == null) { ErrorMessage = "Database service not available."; return; }

            var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                "Delete Connection", $"Delete connection '{conn.Name}'?",
                confirmText: "Delete", isDestructive: true);
            if (confirmed)
            {
                await _dbService.DeleteConnectionAsync(conn.Id);
                await LoadDatabaseConnectionsAsync();
            }
        }
        catch (Exception ex) { ErrorMessage = $"Failed to delete connection: {ex.Message}"; }
    }

    private async void SaveDbBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            if (_dbService == null) { ErrorMessage = "Database service not available."; return; }

            var name = DbNameText.Text.Trim();
            if (string.IsNullOrEmpty(name)) { await ConfirmOverlay.ShowInfoAsync("Validation Error", "Connection name is required."); return; }
            if (string.IsNullOrEmpty(DbServerText.Text.Trim())) { await ConfirmOverlay.ShowInfoAsync("Validation Error", "Server address is required."); return; }
            if (string.IsNullOrEmpty(DbDatabaseText.Text.Trim())) { await ConfirmOverlay.ShowInfoAsync("Validation Error", "Database name is required."); return; }

            var authType = DbAuthCombo.SelectedIndex == 1 ? AuthenticationType.SqlAuth : AuthenticationType.WindowsAuth;
            if (authType == AuthenticationType.SqlAuth) 
            { 
                if (string.IsNullOrEmpty(DbUsernameText.Text.Trim()))
                {
                    await ConfirmOverlay.ShowInfoAsync("Validation Error", "Username is required for SQL Authentication."); 
                    return; 
                }
                
                var editingConnAuth = _editingDbConnection;
                var allConnsAuth = await _dbService.GetAllConnectionsAsync();
                
                if (allConnsAuth.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (editingConnAuth == null || c.Id != editingConnAuth.Id)))
                {
                    await ConfirmOverlay.ShowInfoAsync("Validation Error", $"A database connection with the name '{name}' already exists."); 
                    return; 
                }

                var existingForAuth = allConnsAuth.FirstOrDefault(c => c.Id == editingConnAuth?.Id);
                if (existingForAuth == null && string.IsNullOrEmpty(DbPasswordBox.Password)) // only throw if new connection has no password
                {
                    await ConfirmOverlay.ShowInfoAsync("Validation Error", "Password is required for SQL Authentication."); 
                    return; 
                }
            }
            else
            {
                // We still need to check duplicate names even for Windows Auth
                var editingConnAuth = _editingDbConnection;
                var allConnsAuth = await _dbService.GetAllConnectionsAsync();
                
                if (allConnsAuth.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (editingConnAuth == null || c.Id != editingConnAuth.Id)))
                {
                    await ConfirmOverlay.ShowInfoAsync("Validation Error", $"A database connection with the name '{name}' already exists."); 
                    return; 
                }
            }

            var editingConn = _editingDbConnection;

            var conn = (editingConn ?? new DatabaseConnection { Id = Guid.NewGuid().ToString() }) with
            {
                Name = name,
                ServerAddress = DbServerText.Text.Trim(),
                DatabaseName = DbDatabaseText.Text.Trim(),
                Authentication = authType,
                Username = DbUsernameText.Text.Trim(),
                Permission = (PermissionLevel)DbPermissionCombo.SelectedIndex,
                RequireApproval = DbRequireApprovalCheck.IsChecked == true
            };

            // Encrypt password if SQL auth
            if (conn.Authentication == AuthenticationType.SqlAuth && !string.IsNullOrEmpty(DbPasswordBox.Password))
            {
                conn = conn.WithEncryptedPassword(DbPasswordBox.Password);
            }

            // Test connection before saving
            DbTestProgress.Visibility = Visibility.Visible;
            SaveDbBtn.IsEnabled = false;
            TestDbBtn.IsEnabled = false;
            
            try
            {
                var testResult = await _dbService.TestConnectionAsync(conn);
                if (!testResult.Success)
                {
                    await ConfirmOverlay.ShowInfoAsync("Connection Failed", $"Connection test failed.\n\nDetails:\n{testResult.Message}");
                    return;
                }
            }
            catch (Exception testEx)
            {
                await ConfirmOverlay.ShowInfoAsync("Connection Failed", $"Connection failed.\n\nDetails:\n{testEx.Message}");
                return;
            }
            finally
            {
                DbTestProgress.Visibility = Visibility.Collapsed;
                SaveDbBtn.IsEnabled = true;
                TestDbBtn.IsEnabled = true;
            }

            await _dbService.SaveConnectionAsync(conn);
            await LoadDatabaseConnectionsAsync();
            await ConfirmOverlay.ShowInfoAsync("Database", "Database connection saved!");
            DbEditOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ErrorMessage = $"Failed to save connection: {ex.Message}"; }
    }

    private async void TestDbBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            if (_dbService == null) { ErrorMessage = "Database service not available."; return; }

            if (string.IsNullOrEmpty(DbServerText.Text.Trim())) { await ConfirmOverlay.ShowInfoAsync("Validation Error", "Server address is required."); return; }
            if (string.IsNullOrEmpty(DbDatabaseText.Text.Trim())) { await ConfirmOverlay.ShowInfoAsync("Validation Error", "Database name is required."); return; }

            var authType = DbAuthCombo.SelectedIndex == 1 ? AuthenticationType.SqlAuth : AuthenticationType.WindowsAuth;
            if (authType == AuthenticationType.SqlAuth)
            {
                if (string.IsNullOrEmpty(DbUsernameText.Text.Trim()))
                {
                    await ConfirmOverlay.ShowInfoAsync("Validation Error", "Username is required for SQL Authentication."); 
                    return; 
                }
                if (string.IsNullOrEmpty(DbPasswordBox.Password))
                {
                    // For test connection, if password is empty, maybe they rely on existing saved password?
                    // But Test connection on a new unsaved one needs password.
                    // Let's enforce password if they are typing it in the UI.
                    var name = DbNameText.Text.Trim();
                    var existingConns1 = await _dbService.GetAllConnectionsAsync();
                    var exists = existingConns1.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (exists == null)
                    {
                        await ConfirmOverlay.ShowInfoAsync("Validation Error", "Password is required to test a new SQL Authentication connection."); 
                        return; 
                    }
                }
            }

            var conn = new DatabaseConnection
            {
                ServerAddress = DbServerText.Text.Trim(),
                DatabaseName = DbDatabaseText.Text.Trim(),
                Authentication = authType,
                Username = DbUsernameText.Text.Trim(),
                Permission = PermissionLevel.ReadOnly,
                RequireApproval = false
            };

            if (conn.Authentication == AuthenticationType.SqlAuth && !string.IsNullOrEmpty(DbPasswordBox.Password))
            {
                conn = conn.WithEncryptedPassword(DbPasswordBox.Password);
            }

            DbTestProgress.Visibility = Visibility.Visible;
            TestDbBtn.IsEnabled = false;

            try
            {
                var result = await _dbService.TestConnectionAsync(conn);
                await ConfirmOverlay.ShowInfoAsync(
                    "Test Connection",
                    result.Success ? "✅ Connection successful!" : $"Connection test failed.\n\nDetails:\n{result.Message}");
            }
            catch (Exception testEx)
            {
                await ConfirmOverlay.ShowInfoAsync("Test Connection Failed", $"Connection failed.\n\nDetails:\n{testEx.Message}");
            }
            finally
            {
                DbTestProgress.Visibility = Visibility.Collapsed;
                TestDbBtn.IsEnabled = true;
            }
        }
        catch (Exception ex) { ErrorMessage = $"Connection test failed: {ex.Message}"; }
    }

    private async Task DiscoverSqlServersAsync()
    {
        try
        {
            DbServerScanProgress.Visibility = Visibility.Visible;
            
            // 1. Get Local instances via Registry first (fast)
            var localInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, 
                    Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Registry32))
                {
                    using (var instanceKey = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL", false))
                    {
                        if (instanceKey != null)
                        {
                            foreach (var instanceName in instanceKey.GetValueNames())
                            {
                                localInstances.Add(instanceName.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) 
                                    ? Environment.MachineName 
                                    : $"{Environment.MachineName}\\{instanceName}");
                            }
                        }
                    }
                }
            }
            catch { /* Ignore registry errors */ }
            
            // Populate fast local ones
            if (localInstances.Any())
            {
                _allDiscoveredServers = localInstances.OrderBy(x => x).ToList();
                DbServerList.ItemsSource = _allDiscoveredServers;
            }

            // 2. Discover network instances asynchronously
            await Task.Run(() =>
            {
                try
                {
                    var instance = System.Data.Sql.SqlDataSourceEnumerator.Instance;
                    var table = instance.GetDataSources();
                    
                    var networkInstances = new List<string>();
                    foreach (System.Data.DataRow row in table.Rows)
                    {
                        string serverName = row["ServerName"].ToString();
                        string instanceName = row["InstanceName"].ToString();
                        
                        string fullInstance = string.IsNullOrEmpty(instanceName) 
                            ? serverName 
                            : $"{serverName}\\{instanceName}";
                            
                        if (!string.IsNullOrEmpty(fullInstance))
                        {
                            networkInstances.Add(fullInstance);
                        }
                    }
                    
                    Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var ni in networkInstances)
                        {
                            localInstances.Add(ni);
                        }
                        _allDiscoveredServers = localInstances.OrderBy(x => x).ToList();
                        DbServerList.ItemsSource = _allDiscoveredServers;
                        DbServerScanProgress.Visibility = Visibility.Collapsed;
                    });
                }
                catch
                {
                    Dispatcher.InvokeAsync(() => DbServerScanProgress.Visibility = Visibility.Collapsed);
                }
            });
        }
        catch
        {
            DbServerScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void DbServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DbServerList.SelectedItem != null)
        {
            DbServerText.Text = DbServerList.SelectedItem.ToString();
            DbServerPopup.IsOpen = false;
            DbServerList.SelectedItem = null;
        }
    }

    private void DbServerText_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DbServerList.HasItems)
        {
            DbServerPopup.IsOpen = true;
        }
    }
    
    private void DbServerText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allDiscoveredServers.Count > 0)
        {
            var filter = DbServerText.Text.Trim();
            var filtered = _allDiscoveredServers.Where(s => s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            DbServerList.ItemsSource = filtered;
            DbServerPopup.IsOpen = filtered.Count > 0 && DbServerText.IsFocused;
        }
    }

    private async void DeleteDbBtn_Click_Old(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            if (_dbService == null) { ErrorMessage = "Database service not available."; return; }
            if (DatabaseConnectionsList.SelectedItem is not DatabaseConnection conn) { ErrorMessage = "Select a connection to delete."; return; }

            var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                "Delete Connection", $"Delete connection '{conn.Name}'?",
                confirmText: "Delete", isDestructive: true);
            if (confirmed)
            {
                await _dbService.DeleteConnectionAsync(conn.Id);
                await LoadDatabaseConnectionsAsync();
                DbNameText.Text = "";
                DbServerText.Text = "localhost";
                DbDatabaseText.Text = "";
                DbUsernameText.Text = "";
                DbPasswordBox.Password = "";
            }
        }
        catch (Exception ex) { ErrorMessage = $"Failed to delete connection: {ex.Message}"; }
    }

    private async void ThemePill_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag == null) return;
        var theme = rb.Tag.ToString();
        var isDark = theme != "light";

        // Sync hidden combo box to prevent code behind logic breakages
        if (ThemeCombo != null)
        {
            ThemeCombo.SelectedIndex = theme == "light" ? 1 : 0;
        }

        // Apply theme immediately for live preview
        Helpers.ThemeManager.ApplyTheme(isDark);
        
        // Persist immediately so it survives restart
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var themeDir = Path.Combine(localAppData, AiAssistantPackage.PackageGuidString);
            if (!Directory.Exists(themeDir)) Directory.CreateDirectory(themeDir);
            AiAssistant.Storage.SafeFileWriter.WriteAllText(Path.Combine(themeDir, "theme.txt"), theme);
        }
        catch { }
    }

    private static bool TrySetResource(ResourceDictionary root, string key, object? value)
    {
        if (root == null) return false;
        root[key] = value;
        return true;
    }

    private void OnPreferencesChanged(object? sender, UICustomizationPreferences e)
    {
        _uiPrefs = e;
        double appFont   = GetBaseFontSize("BaseAppFontSize")     + e.AppFontSizeOffset;
        double msgFont   = GetBaseFontSize("BaseMessageFontSize") + e.MessageFontSizeOffset;
        double inputFont = GetBaseFontSize("BaseInputFontSize")   + e.InputFontSizeOffset;
        double codeFont  = GetBaseFontSize("BaseCodeFontSize")    + e.CodeFontSizeOffset;
        double smallFont = GetBaseFontSize("BaseSmallFontSize")   + e.MessageFontSizeOffset;

        // Modifying Application.Current.Resources inside a VSIX pollutes the Visual Studio global environment.
        // Instead, we update the shared ThemeDictionaryContainer which updates all tool windows.

        var container = Helpers.ThemeManager.GetThemeDictionaryContainer();
        TrySetResource(container, "AppFontSizeOverride",    appFont);
        TrySetResource(container, "MessageFontSizeOverride", msgFont);
        TrySetResource(container, "InputFontSizeOverride",  inputFont);
        TrySetResource(container, "CodeFontSizeOverride",   codeFont);
        TrySetResource(container, "SmallFontSizeOverride",  smallFont);
        if (!string.IsNullOrEmpty(e.AppFontFamily))
            TrySetResource(container, "AppFont", new System.Windows.Media.FontFamily(e.AppFontFamily));
        if (!string.IsNullOrEmpty(e.CodeFontFamily))
            TrySetResource(container, "CodeFont", new System.Windows.Media.FontFamily(e.CodeFontFamily));
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var dict = Helpers.ThemeManager.GetThemeDictionaryContainer();
        bool isDark = Helpers.ThemeManager.IsDarkTheme;
        
        if (_uiPrefs != null)
        {
            OnPreferencesChanged(sender, _uiPrefs);
        }
        
        for (int i = this.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var current = this.Resources.MergedDictionaries[i];
            if (current.Contains("AiAssistantThemeMarker") || (current.Source != null && current.Source.ToString().Contains("Theme.xaml")))
            {
                this.Resources.MergedDictionaries.RemoveAt(i);
            }
        }
        
        // Add the direct theme dictionary to force WPF DynamicResource invalidation
        this.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary 
        { 
            Source = new Uri(isDark ? "pack://application:,,,/AiAssistant.UI;component/Themes/DarkTheme.xaml" : "pack://application:,,,/AiAssistant.UI;component/Themes/LightTheme.xaml", UriKind.Absolute) 
        });
        
        this.Resources.MergedDictionaries.Add(dict);
    }

    private static double GetBaseFontSize(string key)
    {
        try
        {
            var container = Helpers.ThemeManager.GetThemeDictionaryContainer();
            foreach (System.Windows.ResourceDictionary dict in container.MergedDictionaries)
            {
                if (dict.Contains(key) && dict[key] is double d)
                    return d;
            }
        }
        catch { }
        return key switch
        {
            "BaseAppFontSize" => 13,
            "BaseMessageFontSize" => 13,
            "BaseInputFontSize" => 13,
            "BaseCodeFontSize" => 13,
            "BaseSmallFontSize" => 11,
            _ => 13
        };
    }

    private async Task LoadUiPreferencesAsync()
    {
        if (_userPreferences == null) return;

        _uiPrefs = await _userPreferences.LoadAsync();

        await Dispatcher.InvokeAsync(() =>
        {
            AppFontSlider.Value = _uiPrefs.AppFontSizeOffset;
            MessageFontSlider.Value = _uiPrefs.MessageFontSizeOffset;
            InputFontSlider.Value = _uiPrefs.InputFontSizeOffset;
            CodeFontSlider.Value = _uiPrefs.CodeFontSizeOffset;

            if (!string.IsNullOrEmpty(_uiPrefs.AppFontFamily))
            {
                foreach (ComboBoxItem item in AppFontFamilyCombo.Items)
                {
                    if (item.Tag?.ToString() == _uiPrefs.AppFontFamily)
                    {
                        AppFontFamilyCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_uiPrefs.CodeFontFamily))
            {
                foreach (ComboBoxItem item in CodeFontFamilyCombo.Items)
                {
                    if (item.Tag?.ToString() == _uiPrefs.CodeFontFamily)
                    {
                        CodeFontFamilyCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            OnPreferencesChanged(null, _uiPrefs);
            UpdateFontPreviewLabels();
        });
    }

    private void UpdateFontPreviewLabels()
    {
        AppFontSizeInput.Text = $"{GetBaseFontSize("BaseAppFontSize") + AppFontSlider.Value:0}";
        MessageFontSizeInput.Text = $"{GetBaseFontSize("BaseMessageFontSize") + MessageFontSlider.Value:0}";
        InputFontSizeInput.Text = $"{GetBaseFontSize("BaseInputFontSize") + InputFontSlider.Value:0}";
        CodeFontSizeInput.Text = $"{GetBaseFontSize("BaseCodeFontSize") + CodeFontSlider.Value:0}";
    }

    private void NumericInput_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void FontSizeInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        ApplyFontSizeFromInput(tb);
    }

    private void FontSizeInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (sender is not TextBox tb) return;
            ApplyFontSizeFromInput(tb);
            e.Handled = true;
        }
    }

    private void ApplyFontSizeFromInput(TextBox input)
    {
        if (_userPreferences == null || _uiPrefs == null) return;

        double baseSize = input.Name switch
        {
            nameof(AppFontSizeInput) => GetBaseFontSize("BaseAppFontSize"),
            nameof(MessageFontSizeInput) => GetBaseFontSize("BaseMessageFontSize"),
            nameof(InputFontSizeInput) => GetBaseFontSize("BaseInputFontSize"),
            nameof(CodeFontSizeInput) => GetBaseFontSize("BaseCodeFontSize"),
            _ => 11
        };

        if (!int.TryParse(input.Text, out int effectiveSize))
        {
            input.Text = $"{baseSize:0}";
            return;
        }

        int offset = effectiveSize - (int)baseSize;
        if (offset < -4) offset = -4;
        if (offset > 29) offset = 29;

        switch (input.Name)
        {
            case nameof(AppFontSizeInput):
                _uiPrefs = _uiPrefs with { AppFontSizeOffset = offset };
                AppFontSlider.Value = offset;
                break;
            case nameof(MessageFontSizeInput):
                _uiPrefs = _uiPrefs with { MessageFontSizeOffset = offset };
                MessageFontSlider.Value = offset;
                break;
            case nameof(InputFontSizeInput):
                _uiPrefs = _uiPrefs with { InputFontSizeOffset = offset };
                InputFontSlider.Value = offset;
                break;
            case nameof(CodeFontSizeInput):
                _uiPrefs = _uiPrefs with { CodeFontSizeOffset = offset };
                CodeFontSlider.Value = offset;
                break;
        }

        OnPreferencesChanged(this, _uiPrefs);
        UpdateFontPreviewLabels();
    }

    private void AppFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        _uiPrefs = _uiPrefs with { AppFontSizeOffset = (int)AppFontSlider.Value };
        OnPreferencesChanged(this, _uiPrefs);
        UpdateFontPreviewLabels();
    }

    private void MessageFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        _uiPrefs = _uiPrefs with { MessageFontSizeOffset = (int)MessageFontSlider.Value };
        OnPreferencesChanged(this, _uiPrefs);
        UpdateFontPreviewLabels();
    }

    private void InputFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        _uiPrefs = _uiPrefs with { InputFontSizeOffset = (int)InputFontSlider.Value };
        OnPreferencesChanged(this, _uiPrefs);
        UpdateFontPreviewLabels();
    }

    private void CodeFontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        _uiPrefs = _uiPrefs with { CodeFontSizeOffset = (int)CodeFontSlider.Value };
        OnPreferencesChanged(this, _uiPrefs);
        UpdateFontPreviewLabels();
    }

    private void AppFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        if (AppFontFamilyCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _uiPrefs = _uiPrefs with { AppFontFamily = tag };
            OnPreferencesChanged(this, _uiPrefs);
        }
    }

    /*
    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _settingsService == null || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        var lang = item.Content.ToString();
        if (!string.IsNullOrEmpty(lang))
        {
            await _settingsService.SetLanguageAsync(lang);
        }
    }

    private async void CountryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _settingsService == null || CountryCombo.SelectedItem is not ComboBoxItem item) return;
        var country = item.Content.ToString();
        if (!string.IsNullOrEmpty(country))
        {
            await _settingsService.SetCountryAsync(country);
        }
    }
    */

    private void CodeFontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_userPreferences == null || _uiPrefs == null) return;
        if (CodeFontFamilyCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _uiPrefs = _uiPrefs with { CodeFontFamily = tag };
            OnPreferencesChanged(this, _uiPrefs);
        }
    }

    private async void SaveThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        var isDark = ThemeDarkPill.IsChecked == true;
        var theme = isDark ? "dark" : "light";
        if (ThemeAutoPill.IsChecked == true) theme = "auto";
        
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var themeFile = Path.Combine(localAppData, AiAssistantPackage.PackageGuidString, "theme.txt");
            var dir = Path.GetDirectoryName(themeFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            AiAssistant.Storage.SafeFileWriter.WriteAllText(themeFile, theme);
            
            await ConfirmOverlay.ShowInfoAsync("ApexCode AI Settings", "Theme settings saved successfully.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save theme: {ex.Message}";
        }

        if (_settingsService != null)
        {
            _settingsService.EnableLogs = EnableLogsCheck.IsChecked == true;
        }

        if (_userPreferences != null && _uiPrefs != null)
        {
            await _userPreferences.SaveAsync(_uiPrefs);
        }

        // Application-level theme switching is handled by ThemeManager
        Helpers.ThemeManager.ApplyTheme(isDark);

        _telemetryService?.TrackEventAsync("theme_changed", theme);
    }

    // TelemetryCheck_Changed removed because the checkbox was removed from the UI.

    private async void SemanticSearchCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsService != null)
        {
            await _settingsService.SetEmbeddingIndexingEnabledAsync(SemanticSearchCheck.IsChecked == true);
        }
        CheckModelFiles();
    }

    private async void ResetDefaultsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            var confirmed = await ConfirmOverlay.ShowConfirmAsync(
                "Reset", "Reset all settings to defaults?",
                confirmText: "Reset", isDestructive: true);
            if (confirmed)
            {
                ProviderTypeCombo.SelectedIndex = 0;
                ProviderNameText.Text = "OpenAI";
                ApiKeyBox.Password = "";
                ApiEndpointText.Text = "https://api.openai.com/v1";
                ModelFetchEndpointText.Text = "https://api.openai.com/v1/models";
                ModelPicker.SelectedModel = null;
                ReadFilesApprovalCombo.SelectedIndex = 1;
                CreateFileApprovalCombo.SelectedIndex = 1;
                ReplaceFileApprovalCombo.SelectedIndex = 1;
                ExecuteCommandApprovalCombo.SelectedIndex = 1;
                AutoHealCheck.IsChecked = true;


                if (_userPreferences != null)
                {
                    var defaults = new UICustomizationPreferences();
                    _uiPrefs = defaults;
                    await _userPreferences.SaveAsync(defaults);

                    AppFontSlider.Value = 0;
                    MessageFontSlider.Value = 0;
                    InputFontSlider.Value = 0;
                    CodeFontSlider.Value = 0;

                    AppFontFamilyCombo.SelectedIndex = 0;
                    CodeFontFamilyCombo.SelectedIndex = 0;

                    UpdateFontPreviewLabels();
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to reset: {ex.Message}";
        }
    }

    public event EventHandler? TriggerManualIndexing;
    public event EventHandler? StopManualIndexing;
    public event EventHandler? DownloadModelsRequested;

    private void CheckModelFiles()
    {
        if (ModelDownloadPanel == null) return;

        if (SemanticSearchCheck.IsChecked != true)
        {
            ModelDownloadPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var outputDirectory = System.IO.Path.Combine(appData, "AiAssistant", "EmbeddingModel");
        var onnxExists = System.IO.File.Exists(System.IO.Path.Combine(outputDirectory, "model_quantized.onnx"));
        var jsonExists = System.IO.File.Exists(System.IO.Path.Combine(outputDirectory, "tokenizer.json"));

        if (!onnxExists || !jsonExists)
        {
            ModelDownloadPanel.Visibility = Visibility.Visible;
            DownloadModelsBtn.Visibility = Visibility.Visible;
            ModelDownloadProgress.Visibility = Visibility.Collapsed;
            ModelDownloadStatusText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelDownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void DownloadModelsBtn_Click(object sender, RoutedEventArgs e)
    {
        DownloadModelsBtn.Visibility = Visibility.Collapsed;
        ModelDownloadProgress.Visibility = Visibility.Visible;
        ModelDownloadProgress.Value = 0;
        ModelDownloadStatusText.Visibility = Visibility.Visible;
        ModelDownloadStatusText.Text = "Downloading models... 0%";
        DownloadModelsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateDownloadProgress(double percent)
    {
        ModelDownloadProgress.Value = percent;
        ModelDownloadStatusText.Text = $"Downloading models... {percent:F1}%";
    }

    public void OnDownloadComplete()
    {
        ModelDownloadStatusText.Text = "Models downloaded successfully!";
        ModelDownloadStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessGreenBrush");
        ModelDownloadProgress.Visibility = Visibility.Collapsed;
        
        // Hide the whole panel after a few seconds
        _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
        {
            Dispatcher.InvokeAsync(() => ModelDownloadPanel.Visibility = Visibility.Collapsed);
        });
    }

    public void OnDownloadFailed(string error)
    {
        ModelDownloadStatusText.Text = $"Download failed: {error}";
        ModelDownloadStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorRedBrush");
        ModelDownloadProgress.Visibility = Visibility.Collapsed;
        DownloadModelsBtn.Visibility = Visibility.Visible;
    }

    private void TriggerIndexBtn_Click(object sender, RoutedEventArgs e)
    {
        IndexLogsText.Text = ""; // Clear logs
        TriggerIndexBtn.Visibility = Visibility.Collapsed;
        StopIndexBtn.Visibility = Visibility.Visible;
        TriggerManualIndexing?.Invoke(this, EventArgs.Empty);
        IndexProgress.Visibility = Visibility.Visible;
    }

    private void StopIndexBtn_Click(object sender, RoutedEventArgs e)
    {
        StopManualIndexing?.Invoke(this, EventArgs.Empty);
    }

    private void AdvancedIndexBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedIndexPanel.Visibility == Visibility.Collapsed)
        {
            AdvancedIndexBtn.Content = "Advanced ▲";
            AdvancedIndexPanel.Visibility = Visibility.Visible;
        }
        else
        {
            AdvancedIndexBtn.Content = "Advanced ▼";
            AdvancedIndexPanel.Visibility = Visibility.Collapsed;
        }
    }

    public void StopIndexProgress()
    {
        IndexProgress.Visibility = Visibility.Collapsed;
        StopIndexBtn.Visibility = Visibility.Collapsed;
        TriggerIndexBtn.Visibility = Visibility.Visible;
    }

    public void AppendIndexLog(string message)
    {
        IndexLogsText.AppendText(message + Environment.NewLine);
        AdvancedIndexScroll.ScrollToBottom();
    }


    private async void SaveBehaviorBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorMessage = "";
            if (_settingsService != null)
            {
                await _settingsService.SetEmbeddingIndexingEnabledAsync(SemanticSearchCheck.IsChecked == true);
                await _settingsService.SetAutoHealEnabledAsync(AutoHealCheck.IsChecked == true);
                
                if (int.TryParse(MaxToolIterationsBox.Text, out var maxTools) && maxTools > 0 && maxTools <= 1000)
                    await _settingsService.SetMaxToolIterationsAsync(maxTools);
                
                if (int.TryParse(MaxConsecutiveMistakesBox.Text, out var maxMistakes) && maxMistakes > 0 && maxMistakes <= 20)
                    await _settingsService.SetMaxConsecutiveMistakesAsync(maxMistakes);
                
                if (int.TryParse(MaxAutoRetryBox.Text, out var maxRetries) && maxRetries >= 0 && maxRetries <= 10)
                    await _settingsService.SetMaxAutoRetryAttemptsAsync(maxRetries);
                
                if (int.TryParse(MaxStreamRetriesBox.Text, out var maxStream) && maxStream >= 0 && maxStream <= 10)
                    await _settingsService.SetMaxStreamRetriesAsync(maxStream);

                
                if (ReadFilesApprovalCombo.SelectedItem is ComboBoxItem readItem) await _settingsService.SetToolApprovalModeAsync("read_files", readItem.Tag?.ToString() ?? "prompt");
                if (CreateFileApprovalCombo.SelectedItem is ComboBoxItem createItem) await _settingsService.SetToolApprovalModeAsync("create_file", createItem.Tag?.ToString() ?? "prompt");
                if (ReplaceFileApprovalCombo.SelectedItem is ComboBoxItem replaceItem) await _settingsService.SetToolApprovalModeAsync("replace_in_file", replaceItem.Tag?.ToString() ?? "prompt");
                if (ExecuteCommandApprovalCombo.SelectedItem is ComboBoxItem execItem) await _settingsService.SetToolApprovalModeAsync("execute_command", execItem.Tag?.ToString() ?? "prompt");
                if (DatabaseQueryApprovalCombo.SelectedItem is ComboBoxItem queryItem) await _settingsService.SetToolApprovalModeAsync("execute_query", queryItem.Tag?.ToString() ?? "prompt");
                if (DatabaseSchemaApprovalCombo.SelectedItem is ComboBoxItem schemaItem) await _settingsService.SetToolApprovalModeAsync("get_database_schema", schemaItem.Tag?.ToString() ?? "prompt");
                
                await _settingsService.SetExecuteCommandWhitelistAsync(_commandWhitelist.ToList());
            }
            await ConfirmOverlay.ShowInfoAsync("Settings", "Behavior settings saved successfully!");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save behavior settings: {ex.Message}";
        }
    }

    private async void AddExtensionBtn_Click(object sender, RoutedEventArgs e)
    {
        var ext = NewExtensionTextBox.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !ext.StartsWith("."))
        {
            ExtensionValidationText.Text = "Extension must start with a dot (.) and cannot be empty.";
            ExtensionValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (_workspaceExtensionsList.Contains(ext))
        {
            ExtensionValidationText.Text = "Extension already exists in the list.";
            ExtensionValidationText.Visibility = Visibility.Visible;
            return;
        }

        ExtensionValidationText.Visibility = Visibility.Collapsed;
        _workspaceExtensionsList.Add(ext);
        NewExtensionTextBox.Text = string.Empty;
        NewExtensionTextBox.Focus();
        
        await SaveExtensionsSilentAsync();
    }

    private async void RemoveExtensionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ext)
        {
            _workspaceExtensionsList.Remove(ext);
            await SaveExtensionsSilentAsync();
        }
    }

    private async Task SaveExtensionsSilentAsync()
    {
        if (_settingsService == null) return;
        var extString = string.Join(", ", _workspaceExtensionsList);
        await _settingsService.SetWorkspaceExtensionsAsync(extString);
    }

    private void SetComboItem(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                break;
            }
        }
    }

    private void ExecuteCommandApprovalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandWhitelistPanel == null) return;
        if (ExecuteCommandApprovalCombo.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "allowall")
        {
            CommandWhitelistPanel.Visibility = Visibility.Visible;
        }
        else
        {
            CommandWhitelistPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void AddCommandBtn_Click(object sender, RoutedEventArgs e)
    {
        var cmd = NewCommandTextBox.Text.Trim();
        if (string.IsNullOrEmpty(cmd))
        {
            CommandValidationText.Text = "Command cannot be empty.";
            CommandValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (_commandWhitelist.Contains(cmd))
        {
            CommandValidationText.Text = "Command already exists in the list.";
            CommandValidationText.Visibility = Visibility.Visible;
            return;
        }

        CommandValidationText.Visibility = Visibility.Collapsed;
        _commandWhitelist.Add(cmd);
        NewCommandTextBox.Text = string.Empty;
        NewCommandTextBox.Focus();
    }

    private void RemoveCommandBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string cmd)
        {
            _commandWhitelist.Remove(cmd);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
