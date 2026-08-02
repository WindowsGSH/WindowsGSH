using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Network.Upnp;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Scheduling;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using WindowsGSH.Data;
using WindowsGSH.Discord;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;

namespace WindowsGSH;

public partial class ServerConfigEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly InstalledServer _server;
    private readonly IGameServerModule? _module;
    private readonly string? _selectedAddonId;
    private readonly Dictionary<string, FrameworkElement> _baseFieldControls = [];
    private readonly Dictionary<string, Dictionary<string, FrameworkElement>> _addonFieldControls = [];
    private readonly Dictionary<string, WpfCheckBox> _automationControls = [];
    private readonly Dictionary<string, WpfCheckBox> _scheduleEnabledControls = [];
    private readonly Dictionary<string, WpfCheckBox> _discordCardControls = [];
    private readonly Dictionary<string, WpfTextBox> _scheduleCronControls = [];
    private readonly List<RconScheduleControls> _rconScheduleControls = [];
    private readonly List<ConsoleScheduleControls> _consoleScheduleControls = [];
    private readonly List<ApiScheduleControls> _apiScheduleControls = [];
    private readonly List<ExternalCommandScheduleControls> _externalCommandScheduleControls = [];
    private readonly ServerBackupService _backupService = new();
    private readonly ServerOperationManager _operationManager = ServerOperationManager.Shared;
    private readonly WindowsFirewallService _firewallService = new();
    private readonly PortMappingRegistry _portMappingRegistry = new();
    private readonly UpnpMappingLifecycleService _upnpMappingLifecycleService;
    private readonly Func<string, bool>? _hasLiveMonitoredProcess;
    private readonly DiscordRepository _discordRepository = new();
    private readonly DiscordWebhookStore _discordWebhookStore = new();
    private readonly JsonObject _config;
    private readonly bool _isReadOnly;
    private UpnpMappingPolicy _persistedUpnpPolicy;
    private WpfCheckBox? _backupOnStartCheckBox;
    private WpfCheckBox? _createFirewallRulesCheckBox;
    private WpfTextBox? _backupPathsTextBox;
    private WpfTextBox? _backupDestinationTextBox;
    private WpfComboBox? _backupComboBox;
    private WpfTextBox? _discordCardChannelTextBox;
    private WpfTextBox? _discordAlertChannelTextBox;
    private WpfTextBox? _discordDashboardChannelTextBox;
    private WpfCheckBox? _discordUseWebhookOverrideCheckBox;
    private WpfPasswordBox? _discordWebhookUrlBox;
    private StackPanel? _rconScheduleRowsPanel;
    private StackPanel? _consoleScheduleRowsPanel;
    private StackPanel? _apiScheduleRowsPanel;
    private StackPanel? _externalCommandScheduleRowsPanel;
    private WpfTextBox? _resolvedLaunchArgsTextBox;
    private WpfTextBox? _changedLaunchFieldsTextBox;
    private WpfCheckBox? _applyPriorityOnStartCheckBox;
    private WpfComboBox? _processPriorityComboBox;
    private WpfCheckBox? _applyAffinityOnStartCheckBox;
    private WpfTextBox? _cpuAffinityMaskTextBox;
    private WpfComboBox? _upnpMappingPolicyComboBox;
    private WpfComboBox? _javaRuntimeComboBox;
    private WpfComboBox? _managedJavaRuntimeComboBox;
    private bool _syncingJavaRuntimeSelection;
    private WpfComboBox? _javaMemoryPresetComboBox;
    private WpfTextBox? _javaInitialMemoryTextBox;
    private WpfTextBox? _javaMaximumMemoryTextBox;
    private WpfTextBox? _javaAdditionalArgumentsTextBox;
    private string? _savedConfigSnapshot;
    private bool _suppressUnsavedPrompt;
    private bool _renderingFields;
    private bool _hasConditionalFields;

    public ServerConfigEditorWindow(InstalledServer server, string? selectedAddonId = null)
        : this(
            server,
            selectedAddonId,
            new UpnpMappingLifecycleService(new PortMappingRegistry(), log: AppLogService.Add),
            hasLiveMonitoredProcess: null)
    {
    }

    internal ServerConfigEditorWindow(
        InstalledServer server,
        string? selectedAddonId,
        UpnpMappingLifecycleService upnpMappingLifecycleService,
        Func<string, bool>? hasLiveMonitoredProcess = null)
    {
        InitializeComponent();
        _upnpMappingLifecycleService = upnpMappingLifecycleService ?? throw new ArgumentNullException(nameof(upnpMappingLifecycleService));
        _hasLiveMonitoredProcess = hasLiveMonitoredProcess;

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        _server = server;
        _selectedAddonId = selectedAddonId;
        _module = new ModuleRegistry().GetModules().FirstOrDefault(module => string.Equals(module.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
        _config = JsonNode.Parse(File.ReadAllText(server.ConfigPath))?.AsObject() ?? [];
        _persistedUpnpPolicy = ReadConfiguredUpnpPolicy();
        if (_module != null)
        {
            new ServerSecretStore().ResolveConfigSecrets(_module, server.ServerFolder, _config);
        }
        _isReadOnly = _module != null && ServerProcessLocator.IsRunning(_module, server.InstallPath);

        Title = $"Edit {server.Name}";
        ConfigEditorTitleBar.Title = Title;
        TitleTextBlock.Text = server.Name;
        SubtitleTextBlock.Text = _module == null
            ? "No module definition found. Raw config editing is not available yet."
            : $"Module: {_module.Name}";
        if (_isReadOnly)
        {
            StatusTextBlock.Text = "Server is running. Stop the server before making config changes.";
            SaveButton.IsEnabled = false;
        }

        Loaded += ServerConfigEditorWindow_Loaded;
        Closing += ServerConfigEditorWindow_Closing;
    }

    private async void ServerConfigEditorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ServerConfigEditorWindow_Loaded;
        await RefreshSettingsFromConfigFilesAsync();
        RenderFields();
        await RefreshJavaRuntimeChoicesAsync();
        SaveCurrentValuesToConfigObject();
        _savedConfigSnapshot = SerializeConfig();
    }

    private async Task RefreshSettingsFromConfigFilesAsync()
    {
        if (_module == null)
        {
            return;
        }

        try
        {
            var instance = CreateServerInstance();
            var fileSettings = await _module.ReadConfigFileSettingsAsync(instance, CancellationToken.None);
            if (fileSettings.Count == 0)
            {
                return;
            }

            var settings = _config["settings"] as JsonObject ?? [];
            _config["settings"] = settings;
            foreach (var (key, value) in fileSettings)
            {
                var field = _module.GetConfigFields().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                {
                    continue;
                }

                settings[key] = ModuleFieldFormService.ToJsonValue(field, value);
            }

            _config["name"] = _module.GetServerName(ReadSettings(settings));
            StatusTextBlock.Text = $"Loaded current values from {_module.Name} config files.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not load module config files: {ex.Message}";
        }
    }

    private void RenderFields()
    {
        if (_renderingFields)
        {
            return;
        }

        _renderingFields = true;
        try
        {
        // Conditional fields rebuild this entire form. Preserve the user's unsaved UPnP choice in
        // the in-memory config before the old ComboBox is discarded, just like base-field values.
        SaveNetworkToConfigObject();
        var currentBaseValues = _baseFieldControls.Keys
            .ToDictionary(key => key, key => GetFieldValue(_baseFieldControls, key), StringComparer.OrdinalIgnoreCase);
        ConfigTabControl.Items.Clear();
        _baseFieldControls.Clear();
        _addonFieldControls.Clear();
        _automationControls.Clear();
        _scheduleEnabledControls.Clear();
        _scheduleCronControls.Clear();
        _rconScheduleControls.Clear();
        _consoleScheduleControls.Clear();
        _apiScheduleControls.Clear();
        _backupOnStartCheckBox = null;
        _createFirewallRulesCheckBox = null;
        _backupPathsTextBox = null;
        _backupDestinationTextBox = null;
        _backupComboBox = null;
        _discordCardChannelTextBox = null;
        _discordAlertChannelTextBox = null;
        _discordDashboardChannelTextBox = null;
        _rconScheduleRowsPanel = null;
        _consoleScheduleRowsPanel = null;
        _apiScheduleRowsPanel = null;
        _resolvedLaunchArgsTextBox = null;
        _changedLaunchFieldsTextBox = null;
        _applyPriorityOnStartCheckBox = null;
        _processPriorityComboBox = null;
        _applyAffinityOnStartCheckBox = null;
        _cpuAffinityMaskTextBox = null;
        _upnpMappingPolicyComboBox = null;
        _javaRuntimeComboBox = null;
        _managedJavaRuntimeComboBox = null;
        _javaMemoryPresetComboBox = null;
        _javaInitialMemoryTextBox = null;
        _javaMaximumMemoryTextBox = null;
        _javaAdditionalArgumentsTextBox = null;

        if (_module == null)
        {
            StatusTextBlock.Text = "Unable to edit: module not found.";
            return;
        }

        var baseFields = _module.GetConfigFields().ToArray();
        _hasConditionalFields = baseFields.Any(field => field.VisibleWhen != null);
        var basePanel = new StackPanel();
        string? currentGroup = null;
        foreach (var field in baseFields.Where(field => IsBaseFieldVisible(field, currentBaseValues)))
        {
            currentGroup = AddSectionHeaderIfNeeded(basePanel, currentGroup, field.Group);
            var value = currentBaseValues.TryGetValue(field.Key, out var currentValue)
                ? currentValue
                : GetSettingValue(field.Key, field.DefaultValue);
            AddField(basePanel, _baseFieldControls, field, value);
        }

        AddLaunchArgumentPreviewFields(basePanel);
        AddJavaRuntimeFields(basePanel);
        AddRuntimeTuningFields(basePanel);
        AddAutomationFields(basePanel);
        AddNetworkFields(basePanel);
        AddFirewallFields(basePanel);

        ConfigTabControl.Items.Add(new TabItem
        {
            Header = "Base Game",
            Content = CreateTabScrollViewer(basePanel)
        });

        AddBackupsTab();
        AddSchedulesTab();
        AddDiscordTab();

        foreach (var addon in _module.GetAddonDefinitions())
        {
            var status = _module.GetAddonStatus(CreateServerInstance(), addon.Id);
            if (!status.IsInstalled)
            {
                continue;
            }

            var controls = new Dictionary<string, FrameworkElement>();
            _addonFieldControls[addon.Id] = controls;
            var panel = new StackPanel();
            foreach (var field in addon.ConfigFields)
            {
                AddField(panel, controls, field, GetAddonSettingValue(addon.Id, field.Key, field.DefaultValue));
            }

            var tab = new TabItem
            {
                Header = addon.Name,
                Content = CreateTabScrollViewer(panel)
            };
            ConfigTabControl.Items.Add(tab);
            if (string.Equals(addon.Id, _selectedAddonId, StringComparison.OrdinalIgnoreCase))
            {
                ConfigTabControl.SelectedItem = tab;
            }
        }

        AddAddonManagementTab();

        ConfigTabControl.SelectedIndex = Math.Max(0, ConfigTabControl.SelectedIndex);
        ApplyReadOnlyState();
        }
        finally
        {
            _renderingFields = false;
        }
    }

    private void AddAddonManagementTab()
    {
        if (_module == null)
        {
            return;
        }

        var addons = _module.GetAddonDefinitions();
        if (addons.Count == 0)
        {
            return;
        }

        var panel = new StackPanel();
        foreach (var addon in addons)
        {
            var status = _module.GetAddonStatus(CreateServerInstance(), addon.Id);
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(CreateTextBlock(addon.Name, "TextBrush", FontWeights.SemiBold));
            text.Children.Add(CreateTextBlock(status.StatusText, "MutedTextBrush", FontWeights.Normal, new Thickness(0, 3, 0, 0)));
            if (status.IsInstalled && (!string.IsNullOrWhiteSpace(status.SourceName) || !string.IsNullOrWhiteSpace(status.SourceVersion)))
            {
                var sourceText = string.Join(
                    " ",
                    new[] { status.SourceName, status.SourceVersion }.Where(value => !string.IsNullOrWhiteSpace(value)));
                text.Children.Add(CreateTextBlock($"Source: {sourceText}", "MutedTextBrush", FontWeights.Normal, new Thickness(0, 3, 0, 0)));
            }
            if (status.IsInstalled && !string.IsNullOrWhiteSpace(status.Sha256))
            {
                text.Children.Add(CreateTextBlock($"SHA-256: {status.Sha256}", "MutedTextBrush", FontWeights.Normal, new Thickness(0, 3, 0, 0), wrap: true));
            }
            text.Children.Add(CreateTextBlock(addon.Description, "MutedTextBrush", FontWeights.Normal, new Thickness(0, 3, 0, 0), wrap: true));
            if (!string.IsNullOrWhiteSpace(addon.InstallInstructions))
            {
                text.Children.Add(CreateTextBlock(addon.InstallInstructions, "MutedTextBrush", FontWeights.Normal, new Thickness(0, 6, 0, 0), wrap: true));
            }
            row.Children.Add(text);

            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var primaryButton = new WpfButton
        {
            Content = status.IsInstalled
                ? status.IsEnabled ? "Remove" : "Repair"
                : addon.Package != null ? "Install" : "Download",
            MinWidth = 92,
            MinHeight = 32,
            Tag = addon,
            IsEnabled = !_isReadOnly
        };
            primaryButton.Style = (Style)FindResource(status.IsInstalled ? "DialogSecondaryButton" : "DialogPrimaryButton");
            primaryButton.Click += addon.Package != null || status.IsInstalled
                ? ToggleAddonButton_Click
                : DownloadAddonButton_Click;
            buttons.Children.Add(primaryButton);

            if (status.IsInstalled)
            {
                var configButton = new WpfButton
                {
                    Content = "Config",
                    MinWidth = 92,
                    MinHeight = 32,
                    Margin = new Thickness(8, 0, 0, 0),
                    Tag = addon.Id,
                    IsEnabled = !_isReadOnly
                };
                configButton.Style = (Style)FindResource("DialogSecondaryButton");
                configButton.Click += SelectAddonConfigButton_Click;
                buttons.Children.Add(configButton);
            }

            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            panel.Children.Add(row);
        }

        ConfigTabControl.Items.Add(new TabItem
        {
            Header = "Addons",
            Content = CreateTabScrollViewer(panel)
        });
    }

    private void AddAutomationFields(StackPanel panel)
    {
        panel.Children.Add(CreateSectionHeader("Automation"));
        AddAutomationCheckBox(panel, "updateOnStart", "Update on Start", "Run the module update method before starting this server.", defaultValue: false);
        AddAutomationCheckBox(panel, "autoStart", "Auto Start", "Start this server when WindowsGSH opens.", defaultValue: false);
        AddAutomationCheckBox(panel, "autoRestart", "Auto Restart", "Start this server again if WindowsGSH sees it offline.", defaultValue: false);
        AddAutomationCheckBox(panel, "autoUpdate", "Auto Update", "Run an update before automatic starts or restarts.", defaultValue: false);
    }

    private void AddRuntimeTuningFields(StackPanel panel)
    {
        panel.Children.Add(CreateSectionHeader("Process Tuning"));

        _applyPriorityOnStartCheckBox = new WpfCheckBox
        {
            IsChecked = GetRuntimeBoolean("applyPriorityOnStart", defaultValue: false),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        _applyPriorityOnStartCheckBox.SetResourceReference(ForegroundProperty, "TextBrush");
        _applyPriorityOnStartCheckBox.SetResourceReference(StyleProperty, "AppCheckBox");
        AddRow(panel, "Apply Priority", _applyPriorityOnStartCheckBox, "Set the game server process priority after it starts.");

        _processPriorityComboBox = new WpfComboBox
        {
            ItemsSource = new[] { "", "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "RealTime" },
            SelectedItem = GetRuntimeString("processPriorityClass"),
            IsEnabled = !_isReadOnly,
            MinHeight = 32
        };
        ApplyInputTheme(_processPriorityComboBox);
        AddRow(panel, "Priority Class", _processPriorityComboBox, "Use Normal or AboveNormal for most servers. RealTime can make Windows unresponsive.");

        _applyAffinityOnStartCheckBox = new WpfCheckBox
        {
            IsChecked = GetRuntimeBoolean("applyAffinityOnStart", defaultValue: false),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        _applyAffinityOnStartCheckBox.SetResourceReference(ForegroundProperty, "TextBrush");
        _applyAffinityOnStartCheckBox.SetResourceReference(StyleProperty, "AppCheckBox");
        AddRow(panel, "Apply CPU Affinity", _applyAffinityOnStartCheckBox, "Pin the game server process to specific CPU cores after it starts.");

        _cpuAffinityMaskTextBox = new WpfTextBox
        {
            Text = GetRuntimeString("cpuAffinityMask"),
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_cpuAffinityMaskTextBox);
        AddRow(panel, "CPU Affinity Mask", _cpuAffinityMaskTextBox, "Decimal or hex mask. Example: 0x3 uses CPU 0 and CPU 1.");
    }

    private void AddJavaRuntimeFields(StackPanel panel)
    {
        if (_module?.Capabilities.RequiresJava != true)
        {
            return;
        }

        var java = ServerConfigAppSettings.FromConfigJson(_config.ToJsonString()).Java;
        var managedStore = new ManagedJavaStore();
        var installedManagedRuntimes = managedStore.GetInstalledRuntimes();
        var discovered = AppSettings.Load().KnownJavaRuntimePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new JavaRuntimeChoice(path, $"Cached runtime · {path}", string.Empty))
            .ToList();
        discovered.AddRange(installedManagedRuntimes.Select(runtime =>
            new JavaRuntimeChoice(
                runtime.JavaExecutablePath,
                $"Managed · {runtime.DisplayText} · {runtime.JavaExecutablePath}",
                runtime.Release.Id)));
        if (!string.IsNullOrWhiteSpace(java.RuntimePath) &&
            discovered.All(choice => !string.Equals(choice.ExecutablePath, java.RuntimePath, StringComparison.OrdinalIgnoreCase)))
        {
            discovered.Add(new JavaRuntimeChoice(java.RuntimePath, $"Configured runtime · {java.RuntimePath}", string.Empty));
        }

        panel.Children.Add(CreateSectionHeader("Java Runtime"));

        _javaRuntimeComboBox = new WpfComboBox
        {
            ItemsSource = discovered,
            DisplayMemberPath = nameof(JavaRuntimeChoice.DisplayText),
            SelectedValuePath = nameof(JavaRuntimeChoice.ExecutablePath),
            SelectedValue = !string.IsNullOrWhiteSpace(java.ManagedRuntimeId)
                ? installedManagedRuntimes.FirstOrDefault(runtime =>
                    string.Equals(runtime.Release.Id, java.ManagedRuntimeId, StringComparison.OrdinalIgnoreCase))?.JavaExecutablePath
                : java.RuntimePath,
            IsEditable = true,
            IsEnabled = !_isReadOnly,
            MinHeight = 32
        };
        ApplyInputTheme(_javaRuntimeComboBox);
        AddRow(
            panel,
            "Java Runtime",
            _javaRuntimeComboBox,
            $"Choose a detected java.exe or enter a path, or leave blank to use a managed runtime below. Requires Java {_module.Capabilities.MinimumJavaMajor ?? 1}+.");

        var managedChoices = installedManagedRuntimes
            .Select(r => new ManagedJavaRuntimeChoice(r.Release.Id, r.DisplayText))
            .Prepend(new ManagedJavaRuntimeChoice(string.Empty, "None (use system / custom path above)"))
            .ToList();
        if (!string.IsNullOrWhiteSpace(java.ManagedRuntimeId) &&
            managedChoices.All(choice =>
                !string.Equals(choice.RuntimeId, java.ManagedRuntimeId, StringComparison.OrdinalIgnoreCase)))
        {
            managedChoices.Add(new ManagedJavaRuntimeChoice(
                java.ManagedRuntimeId,
                $"Missing managed runtime · {java.ManagedRuntimeId}"));
        }
        _managedJavaRuntimeComboBox = new WpfComboBox
        {
            ItemsSource = managedChoices,
            DisplayMemberPath = nameof(ManagedJavaRuntimeChoice.DisplayText),
            SelectedValuePath = nameof(ManagedJavaRuntimeChoice.RuntimeId),
            SelectedValue = java.ManagedRuntimeId,
            IsEnabled = !_isReadOnly,
            MinHeight = 32
        };
        if (_managedJavaRuntimeComboBox.SelectedIndex < 0)
        {
            _managedJavaRuntimeComboBox.SelectedValue = java.ManagedRuntimeId;
        }

        ApplyInputTheme(_managedJavaRuntimeComboBox);
        _javaRuntimeComboBox.SelectionChanged += JavaRuntimeComboBox_SelectionChanged;
        _managedJavaRuntimeComboBox.SelectionChanged += ManagedJavaRuntimeComboBox_SelectionChanged;
        var managedNote = !string.IsNullOrWhiteSpace(java.ManagedRuntimeId) &&
                          installedManagedRuntimes.All(runtime =>
                              !string.Equals(runtime.Release.Id, java.ManagedRuntimeId, StringComparison.OrdinalIgnoreCase))
            ? "The configured managed runtime is missing. Its selection is preserved until you install it or explicitly choose another runtime."
            : installedManagedRuntimes.Count == 0
            ? "No managed runtimes installed. Install one in App Settings → Java Runtimes."
            : "Select a managed runtime installed by WindowsGSH, or 'None' to use the path above.";
        AddRow(panel, "Managed Runtime", _managedJavaRuntimeComboBox, managedNote);

        _javaMemoryPresetComboBox = new WpfComboBox
        {
            ItemsSource = new[] { "2 GB", "4 GB", "8 GB", "16 GB", ServerJavaSettings.CustomPreset },
            SelectedItem = java.MemoryPreset,
            IsEnabled = !_isReadOnly,
            MinHeight = 32
        };
        ApplyInputTheme(_javaMemoryPresetComboBox);
        _javaMemoryPresetComboBox.SelectionChanged += JavaMemoryPresetComboBox_SelectionChanged;
        AddRow(panel, "Memory Preset", _javaMemoryPresetComboBox, "Applies safe initial/maximum memory pairs. Choose Custom to enter your own MB values.");

        _javaInitialMemoryTextBox = new WpfTextBox
        {
            Text = java.InitialMemoryMb.ToString(),
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_javaInitialMemoryTextBox);
        AddRow(panel, "Initial Memory (MB)", _javaInitialMemoryTextBox, "Java -Xms value. Minimum 256 MB.");

        _javaMaximumMemoryTextBox = new WpfTextBox
        {
            Text = java.MaximumMemoryMb.ToString(),
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_javaMaximumMemoryTextBox);
        AddRow(panel, "Maximum Memory (MB)", _javaMaximumMemoryTextBox, "Java -Xmx value. Must be at least the initial memory.");

        _javaAdditionalArgumentsTextBox = new WpfTextBox
        {
            Text = java.AdditionalJvmArguments,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_javaAdditionalArgumentsTextBox);
        AddRow(panel, "Additional JVM Args", _javaAdditionalArgumentsTextBox, "Optional advanced JVM flags. WindowsGSH adds the memory arguments.");
    }

    private void JavaMemoryPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_javaInitialMemoryTextBox == null || _javaMaximumMemoryTextBox == null)
        {
            return;
        }

        var values = _javaMemoryPresetComboBox?.SelectedItem?.ToString() switch
        {
            "2 GB" => (Initial: 1024, Maximum: 2048),
            "4 GB" => (Initial: 1024, Maximum: 4096),
            "8 GB" => (Initial: 2048, Maximum: 8192),
            "16 GB" => (Initial: 4096, Maximum: 16384),
            _ => (Initial: 0, Maximum: 0)
        };
        if (values.Initial > 0)
        {
            _javaInitialMemoryTextBox.Text = values.Initial.ToString();
            _javaMaximumMemoryTextBox.Text = values.Maximum.ToString();
        }
    }

    private async Task RefreshJavaRuntimeChoicesAsync()
    {
        if (_module?.Capabilities.RequiresJava != true || _javaRuntimeComboBox == null)
        {
            return;
        }

        var selectedManagedRuntimeId = GetSelectedManagedRuntimeId();
        var managedRuntimes = new ManagedJavaStore().GetInstalledRuntimes();
        var selectedManagedRuntime = managedRuntimes.FirstOrDefault(runtime =>
            string.Equals(
                runtime.Release.Id,
                selectedManagedRuntimeId,
                StringComparison.OrdinalIgnoreCase));
        var selectedPath = selectedManagedRuntime?.JavaExecutablePath ?? GetSelectedJavaRuntimePath();
        var knownPaths = AppSettings.Load().KnownJavaRuntimePaths
            .Append(selectedPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        StatusTextBlock.Text = "Detecting Java runtimes...";
        var runtimes = await new JavaRuntimeManager().DiscoverAsync(knownPaths);
        var choices = runtimes
            .Where(runtime => runtime.Found)
            .GroupBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(runtime => runtime.MajorVersion)
            .ThenBy(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(CreateJavaRuntimeChoice)
            .ToList();
        foreach (var runtime in managedRuntimes)
        {
            choices.RemoveAll(choice =>
                string.Equals(
                    choice.ExecutablePath,
                    runtime.JavaExecutablePath,
                    StringComparison.OrdinalIgnoreCase));
            choices.Add(new JavaRuntimeChoice(
                runtime.JavaExecutablePath,
                $"Managed · {runtime.DisplayText} · {runtime.JavaExecutablePath}",
                runtime.Release.Id));
        }

        if (!string.IsNullOrWhiteSpace(selectedPath) &&
            choices.All(choice => !string.Equals(choice.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new JavaRuntimeChoice(selectedPath, $"Unavailable · {selectedPath}"));
        }

        _javaRuntimeComboBox.ItemsSource = choices;
        _javaRuntimeComboBox.SelectedItem = !string.IsNullOrWhiteSpace(selectedManagedRuntimeId)
            ? choices.FirstOrDefault(choice =>
                string.Equals(
                    choice.ManagedRuntimeId,
                    selectedManagedRuntimeId,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        if (_javaRuntimeComboBox.SelectedItem == null)
        {
            _javaRuntimeComboBox.SelectedValue = selectedPath;
        }

        StatusTextBlock.Text = choices.Count == 0
            ? "No Java runtimes detected. Enter a java.exe path or configure JAVA_HOME/PATH."
            : $"Detected {choices.Count(choice => !choice.DisplayText.StartsWith("Unavailable", StringComparison.Ordinal))} Java runtime(s).";
    }

    private void AddAutomationCheckBox(StackPanel panel, string key, string label, string description, bool defaultValue)
    {
        var checkBox = new WpfCheckBox
        {
            IsChecked = GetAutomationBoolean(key, defaultValue),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        checkBox.SetResourceReference(ForegroundProperty, "TextBrush");
        checkBox.SetResourceReference(StyleProperty, "AppCheckBox");
        _automationControls[key] = checkBox;
        AddRow(panel, label, checkBox, description);
    }

    private void AddNetworkFields(StackPanel panel)
    {
        bool hasExternallyEligiblePort;
        try
        {
            hasExternallyEligiblePort = _module?.GetPorts().Any(port => port.OpenExternally) == true;
        }
        catch (Exception ex)
        {
            AppLogService.Add(
                $"UPnP settings unavailable for {_server.Name}: module port enumeration failed ({ex.GetType().Name}).",
                _server.Id);
            panel.Children.Add(CreateSectionHeader("Automatic Port Forwarding (UPnP)"));
            panel.Children.Add(CreateTextBlock(
                "Automatic port forwarding is unavailable because this module's port declarations could not be read.",
                "WarnBrush",
                FontWeights.Normal,
                new Thickness(0, 0, 0, 14),
                wrap: true));
            return;
        }

        var currentValue = GetNetworkString("upnpMappingPolicy");
        if (!hasExternallyEligiblePort && _persistedUpnpPolicy == UpnpMappingPolicy.Manual)
        {
            return;
        }

        UpnpPolicyChoice[] choices = hasExternallyEligiblePort
            ?
            [
                new UpnpPolicyChoice(nameof(UpnpMappingPolicy.Manual), "Manual (show instructions only)"),
                new UpnpPolicyChoice(nameof(UpnpMappingPolicy.MapOnStart), "Automatic - map on start"),
                new UpnpPolicyChoice(nameof(UpnpMappingPolicy.MapOnStartRemoveOnStop), "Automatic - map on start, remove on stop"),
            ]
            :
            [
                new UpnpPolicyChoice(nameof(UpnpMappingPolicy.Manual), "Manual (no externally eligible ports)"),
            ];
        var selected = choices.FirstOrDefault(choice => string.Equals(choice.Value, currentValue, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        panel.Children.Add(CreateSectionHeader("Automatic Port Forwarding (UPnP)"));
        panel.Children.Add(CreateTextBlock(
            "Requires a router with UPnP enabled. Only this server's ports marked \"open externally\" are affected - " +
            "WindowsGSH never touches a mapping it did not create itself. See this server's Networking tab for manual " +
            "instructions, or to check whether a UPnP gateway was found on this network.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 14),
            wrap: true));

        _upnpMappingPolicyComboBox = new WpfComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(UpnpPolicyChoice.DisplayText),
            SelectedValuePath = nameof(UpnpPolicyChoice.Value),
            SelectedValue = selected.Value,
            IsEnabled = !_isReadOnly,
            MinHeight = 32
        };
        ApplyInputTheme(_upnpMappingPolicyComboBox);
        AddRow(
            panel,
            "Port Mapping",
            _upnpMappingPolicyComboBox,
            "\"Map on start\" leaves the forwarding in place after the server stops (recreated/reconciled on each start). " +
            "\"Remove on stop\" also removes it again once the server stops.");
    }

    private void AddFirewallFields(StackPanel panel)
    {
        if (_module == null)
        {
            return;
        }

        IReadOnlyList<FirewallRuleStatus> rules;
        try
        {
            rules = _firewallService.GetRuleStatuses(_server, _module);
        }
        catch (Exception ex)
        {
            panel.Children.Add(CreateSectionHeader("Windows Firewall"));
            panel.Children.Add(CreateTextBlock(
                $"Could not read Windows Firewall rules: {FormatException(ex)}",
                "MutedTextBrush",
                FontWeights.Normal,
                new Thickness(0, 0, 0, 14),
                wrap: true));
            return;
        }

        if (rules.Count == 0)
        {
            return;
        }

        var allRulesExist = rules.All(rule => rule.Exists);
        _createFirewallRulesCheckBox = new WpfCheckBox
        {
            IsChecked = allRulesExist,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly && !allRulesExist
        };
        _createFirewallRulesCheckBox.SetResourceReference(ForegroundProperty, "TextBrush");
        _createFirewallRulesCheckBox.SetResourceReference(StyleProperty, "AppCheckBox");

        panel.Children.Add(CreateSectionHeader("Windows Firewall"));
        AddRow(
            panel,
            "Create Firewall Rules",
            _createFirewallRulesCheckBox,
            allRulesExist
                ? "WindowsGSH managed firewall rules already exist. This is read only because unchecking it will not remove rules."
                : "Create missing inbound Windows Firewall rules for the configured server ports when saving.");
    }

    private void AddBackupsTab()
    {
        if (_module == null || !_module.Capabilities.SupportsBackups)
        {
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(CreateSectionHeader("Backup Settings"));

        _backupOnStartCheckBox = new WpfCheckBox
        {
            IsChecked = GetBackupBoolean("backupOnStart", defaultValue: false),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        _backupOnStartCheckBox.SetResourceReference(ForegroundProperty, "TextBrush");
        _backupOnStartCheckBox.SetResourceReference(StyleProperty, "AppCheckBox");
        AddRow(panel, "Backup on Start", _backupOnStartCheckBox, "Create a backup before starting this server.");

        var destinationPanel = new Grid();
        destinationPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destinationPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _backupDestinationTextBox = new WpfTextBox
        {
            Text = GetBackupString("destination"),
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_backupDestinationTextBox);
        destinationPanel.Children.Add(_backupDestinationTextBox);
        var browseDestinationButton = new WpfButton
        {
            Content = "Browse...",
            MinWidth = 90,
            MinHeight = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = !_isReadOnly,
            Style = (Style)FindResource("DialogSecondaryButton")
        };
        browseDestinationButton.Click += BrowseBackupDestinationButton_Click;
        Grid.SetColumn(browseDestinationButton, 1);
        destinationPanel.Children.Add(browseDestinationButton);
        AddRow(
            panel,
            "Backup Destination",
            destinationPanel,
            $"Optional. Leave blank to use the default: {Path.Combine(_server.ServerFolder, "backups")}");

        _backupPathsTextBox = new WpfTextBox
        {
            Text = GetBackupPathsText(),
            MinHeight = 120,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            IsReadOnly = _isReadOnly,
            // Opts out of ui:ControlsDictionary's implicit TextBox style, which silently replaces
            // this control's real ScrollViewer with Wpf.Ui.Controls.PassiveScrollViewer - a type
            // with no ScrollBar UI at all (confirmed this tier, Sub-chunk 16/17's ServerInfoWindow/
            // MainWindow fixes). A plain, scrollable, multi-line TextBox like this one needs its
            // real default template back; local property values above are unaffected by this.
            Style = null
        };
        ApplyInputTheme(_backupPathsTextBox);
        AddRow(panel, "Additional Targets", _backupPathsTextBox, "Optional. Add one file or folder path per line, relative to the server files folder. Module defaults are always included.");

        var moduleTargetsPanel = new StackPanel { Margin = new Thickness(210, 0, 0, 14) };
        moduleTargetsPanel.Children.Add(CreateTextBlock("Default module targets", "MutedTextBrush", FontWeights.SemiBold, wrap: true));
        foreach (var target in _module.GetBackupTargets())
        {
            var type = target.IsDirectory ? "Folder" : "File";
            var required = target.IsRequired ? " · required" : string.Empty;
            moduleTargetsPanel.Children.Add(CreateTextBlock($"[{type}{required}] {target.Label}: {target.RelativePath}", "MutedTextBrush", FontWeights.Normal, new Thickness(0, 3, 0, 0), wrap: true));
        }

        panel.Children.Add(moduleTargetsPanel);

        panel.Children.Add(CreateSectionHeader("Restore"));
        var restoreGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        restoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        restoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var restoreLabel = new TextBlock { Text = "Previous Backups", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        restoreLabel.SetResourceReference(ForegroundProperty, "TextBrush");
        restoreGrid.Children.Add(restoreLabel);

        // WrapPanel, not a fixed Auto/Star Grid: the combo box plus four buttons need at least
        // ~698 DIP including margins, and at the default 980-DIP window width there is not always
        // that much room left in this column once the label column and the scroll-viewer's own
        // padding are subtracted - a Grid would either clip the last button or need horizontal
        // scrolling (which the tab's ScrollViewer does not enable). Wrapping degrades gracefully at
        // any window width instead of depending on an exact pixel budget that a later tweak could
        // silently break again.
        var restorePanel = new System.Windows.Controls.WrapPanel();

        _backupComboBox = new WpfComboBox { MinHeight = 34, MinWidth = 260, Margin = new Thickness(0, 0, 8, 8) };
        ApplyInputTheme(_backupComboBox);
        RefreshBackupComboBox();
        restorePanel.Children.Add(_backupComboBox);

        var backupNowButton = new WpfButton { Content = "Backup Now", MinWidth = 110, Margin = new Thickness(0, 0, 8, 8), Style = (Style)FindResource("DialogSecondaryButton"), IsEnabled = !_isReadOnly };
        backupNowButton.Click += BackupNowButton_Click;
        restorePanel.Children.Add(backupNowButton);

        var previewButton = new WpfButton { Content = "Preview", MinWidth = 92, Margin = new Thickness(0, 0, 8, 8), Style = (Style)FindResource("DialogSecondaryButton") };
        previewButton.Click += PreviewBackupButton_Click;
        restorePanel.Children.Add(previewButton);

        var openFolderButton = new WpfButton { Content = "Open Folder", MinWidth = 110, Margin = new Thickness(0, 0, 8, 8), Style = (Style)FindResource("DialogSecondaryButton") };
        openFolderButton.Click += OpenBackupFolderButton_Click;
        restorePanel.Children.Add(openFolderButton);

        var restoreButton = new WpfButton { Content = "Restore", MinWidth = 92, Margin = new Thickness(0, 0, 8, 8), Style = (Style)FindResource("DialogPrimaryButton"), IsEnabled = !_isReadOnly };
        restoreButton.Click += RestoreBackupButton_Click;
        restorePanel.Children.Add(restoreButton);

        Grid.SetColumn(restorePanel, 1);
        restoreGrid.Children.Add(restorePanel);
        panel.Children.Add(restoreGrid);

        ConfigTabControl.Items.Add(new TabItem
        {
            Header = "Backups",
            Content = CreateTabScrollViewer(panel)
        });
    }

    private void AddSchedulesTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(CreateSectionHeader("Cron Schedules"));
        panel.Children.Add(CreateTextBlock(
            "Cron schedules are stored per server for scheduled actions. Use five fields: minute hour day month weekday.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 14),
            wrap: true));
        var helpButton = new WpfButton
        {
            Content = "Cron Help",
            MinWidth = 110,
            Margin = new Thickness(210, 0, 0, 14),
            Style = (Style)FindResource("DialogSecondaryButton")
        };
        helpButton.Click += CronHelpButton_Click;
        panel.Children.Add(helpButton);

        AddScheduleRow(panel, "start", "Start", "0 9 * * *", "Start this server on a cron schedule.");
        AddScheduleRow(panel, "stop", "Stop", "0 2 * * *", "Stop this server on a cron schedule.");
        AddScheduleRow(panel, "update", "Update", "0 3 * * *", "Update this server while it is offline on a cron schedule.");
        AddScheduleRow(panel, "restart", "Restart", "0 5 * * *", "Restart this server on a cron schedule.");
        if (_module?.Capabilities.SupportsBackups == true)
        {
            AddScheduleRow(panel, "backup", "Backup", "0 4 * * *", "Create a backup on a cron schedule.");
        }

        if (ShouldShowRconSchedules())
        {
            AddRconSchedulesSection(panel);
        }

        if (SupportsConsoleCommands())
        {
            AddConsoleSchedulesSection(panel);
        }

        if (SupportsApiActions())
        {
            AddApiSchedulesSection(panel);
        }

        var appSettings = AppSettings.Load();
        if (appSettings.ExternalScheduledCommandsEnabled || ReadExternalCommandSchedules().Count > 0)
        {
            AddExternalCommandSchedulesSection(panel, appSettings.ExternalScheduledCommandsEnabled);
        }

        ConfigTabControl.Items.Add(new TabItem
        {
            Header = "Schedules",
            Content = CreateTabScrollViewer(panel)
        });
    }

    private void AddDiscordTab()
    {
        var settings = AppSettings.Load();
        if (!settings.DiscordBotEnabled && !settings.DiscordWebhookEnabled)
        {
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(CreateSectionHeader("Server Discord"));
        panel.Children.Add(CreateTextBlock(
            "Each channel below is optional and independent. Prefer a numeric Discord channel ID over a channel name — IDs are stable across renames and can't collide across servers in different guilds.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 14),
            wrap: true));

        _discordCardChannelTextBox = new WpfTextBox
        {
            Text = GetInitialCardChannelValue(),
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_discordCardChannelTextBox);
        AddRow(panel, "Card Channel", _discordCardChannelTextBox, "Where this server's individual status card is posted and kept updated. Can be shared across servers - each tracks its own card. Leave blank for no individual card.");

        _discordAlertChannelTextBox = new WpfTextBox
        {
            Text = _discordRepository.GetServerSettings(_server.Id)?.AlertChannelId ?? string.Empty,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_discordAlertChannelTextBox);
        AddRow(panel, "Alert Channel", _discordAlertChannelTextBox, "Where this server's alerts (started/stopped/crashed/restarted/backup/update) are posted, and where bot commands for this server are accepted from. Leave blank to skip alerts for this server.");

        _discordDashboardChannelTextBox = new WpfTextBox
        {
            Text = _discordRepository.GetServerSettings(_server.Id)?.DashboardChannelId ?? string.Empty,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        ApplyInputTheme(_discordDashboardChannelTextBox);
        AddRow(panel, "Dashboard Channel", _discordDashboardChannelTextBox, "Grouping key for the combined dashboard. Servers sharing this channel get one combined dashboard card there. Leave blank to exclude this server from any dashboard.");

        panel.Children.Add(CreateSectionHeader("Server Card Fields"));
        panel.Children.Add(CreateTextBlock(
            "Choose the optional fields shown on this server's Discord status card. Status and game-server identity are always shown.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 10),
            wrap: true));
        AddDiscordCardOption(panel, "showServerAddress", "Server IP and port", "Show the configured server address.");
        AddDiscordCardOption(panel, "showPlayerCount", "Player count", "Show online and maximum player totals.");
        AddDiscordCardOption(panel, "showUptime", "Uptime", "Show process uptime.");
        AddDiscordCardOption(panel, "showResourceUsage", "CPU and RAM", "Show current process CPU and memory usage.");
        AddDiscordCardOption(panel, "showMap", "Map", "Show the queried map when available.");
        AddDiscordCardOption(panel, "showGameVersion", "Game and version", "Show queried game/build/version details.");
        AddDiscordCardOption(panel, "showQuerySummary", "Query protocol and duration", "Show the query protocol and total query workflow duration.");
        AddDiscordCardOption(panel, "showPlayerList", "Player list", "Show up to 12 queried player names with score/ping where available.");
        AddDiscordCardOption(panel, "showQueryDiagnostics", "Query diagnostics", "Show query failure details when the server is in warning state.");

        _discordUseWebhookOverrideCheckBox = new WpfCheckBox
        {
            IsChecked = GetDiscordBoolean("useWebhookOverride", defaultValue: false),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        _discordUseWebhookOverrideCheckBox.SetResourceReference(ForegroundProperty, "TextBrush");
        _discordUseWebhookOverrideCheckBox.SetResourceReference(StyleProperty, "AppCheckBox");
        AddRow(panel, "Webhook Override", _discordUseWebhookOverrideCheckBox, "Send this server's alerts to its own webhook instead of the global webhook.");

        _discordWebhookUrlBox = new WpfPasswordBox
        {
            MinHeight = 32,
            IsEnabled = !_isReadOnly
        };
        ApplyInputTheme(_discordWebhookUrlBox);
        AddRow(
            panel,
            "Webhook URL",
            _discordWebhookUrlBox,
            _discordWebhookStore.HasServerWebhook(_server.Id)
                ? "A server webhook is saved with Windows user encryption. Leave blank unless changing it."
                : "Paste a Discord webhook URL. It is stored separately with Windows user encryption.");

        var clearWebhookButton = new WpfButton
        {
            Content = "Clear Server Webhook",
            MinWidth = 160,
            IsEnabled = !_isReadOnly,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        clearWebhookButton.SetResourceReference(StyleProperty, "DialogSecondaryButton");
        clearWebhookButton.Click += (_, _) =>
        {
            _discordWebhookStore.ClearForServer(_server.Id);
            if (_discordWebhookUrlBox != null)
            {
                _discordWebhookUrlBox.Password = string.Empty;
            }

            StatusTextBlock.Text = "Server Discord webhook cleared.";
        };
        AddRow(panel, string.Empty, clearWebhookButton, string.Empty);

        ConfigTabControl.Items.Add(new TabItem
        {
            Header = "Discord",
            Content = CreateTabScrollViewer(panel)
        });
    }

    private void AddDiscordCardOption(StackPanel panel, string key, string label, string helpText)
    {
        var checkBox = new WpfCheckBox
        {
            IsChecked = GetDiscordBoolean(key, defaultValue: true),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_isReadOnly
        };
        checkBox.SetResourceReference(ForegroundProperty, "TextBrush");
        checkBox.SetResourceReference(StyleProperty, "AppCheckBox");
        _discordCardControls[key] = checkBox;
        AddRow(panel, label, checkBox, helpText);
    }

    private bool ShouldShowRconSchedules()
    {
        return SupportsRcon() && (IsRconEnabled() || ReadRconSchedules().Count > 0);
    }

    private bool SupportsRcon()
    {
        return ModuleRconAvailability.HasRconSupport(_module, CreateServerInstance());
    }

    private bool IsRconEnabled()
    {
        if (!ModuleRconAvailability.HasRconEnabledField(_module))
        {
            return true;
        }

        var value = _baseFieldControls.Count > 0
            ? GetFieldValue(_baseFieldControls, "rcon.enabled")
            : GetSettingValue("rcon.enabled", false);
        return ModuleRconAvailability.IsTruthy(value);
    }

    private bool SupportsConsoleCommands()
    {
        return ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(_module);
    }

    private void AddRconSchedulesSection(StackPanel panel)
    {
        panel.Children.Add(CreateSectionHeader("Scheduled RCON Commands"));
        panel.Children.Add(CreateTextBlock(
            "Add any number of RCON commands and run each one on its own cron schedule.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 12),
            wrap: true));
        if (!IsRconEnabled())
        {
            panel.Children.Add(CreateTextBlock(
                "RCON is currently disabled, so scheduled RCON commands will not run. Existing rows are shown so you can disable, edit, or delete them.",
                "MutedTextBrush",
                FontWeights.Normal,
                new Thickness(210, 0, 0, 12),
                wrap: true));
        }

        _rconScheduleRowsPanel = new StackPanel();
        panel.Children.Add(_rconScheduleRowsPanel);

        foreach (var schedule in ReadRconSchedules())
        {
            AddRconScheduleRow(schedule.Enabled, schedule.Cron, schedule.Command);
        }

        var addButton = new WpfButton
        {
            Content = "+ Add RCON Command",
            MinWidth = 150,
            Margin = new Thickness(210, 0, 0, 14),
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        addButton.Click += (_, _) => AddRconScheduleRow(false, "*/30 * * * *", "");
        panel.Children.Add(addButton);
    }

    private void AddRconScheduleRow(bool enabledValue, string cronValue, string commandValue)
    {
        if (_rconScheduleRowsPanel == null)
        {
            return;
        }

        var border = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(210, 0, 0, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        border.SetResourceReference(BackgroundProperty, "PanelAltBrush");
        border.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new WpfCheckBox
        {
            Content = "Enabled",
            IsChecked = enabledValue,
            IsEnabled = !_isReadOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        enabled.SetResourceReference(ForegroundProperty, "TextBrush");
        enabled.SetResourceReference(StyleProperty, "AppCheckBox");
        grid.Children.Add(enabled);

        var cron = new WpfTextBox
        {
            MinHeight = 32,
            Text = string.IsNullOrWhiteSpace(cronValue) ? "*/30 * * * *" : cronValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(cron);
        Grid.SetColumn(cron, 1);
        grid.Children.Add(cron);

        var command = new WpfTextBox
        {
            MinHeight = 32,
            Text = commandValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);

        var deleteButton = new WpfButton
        {
            Content = "Delete",
            MinWidth = 82,
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        Grid.SetColumn(deleteButton, 3);
        grid.Children.Add(deleteButton);

        border.Child = grid;
        var controls = new RconScheduleControls(border, enabled, cron, command);
        deleteButton.Click += (_, _) =>
        {
            _rconScheduleControls.Remove(controls);
            _rconScheduleRowsPanel.Children.Remove(border);
        };

        _rconScheduleControls.Add(controls);
        _rconScheduleRowsPanel.Children.Add(border);
    }

    private void AddConsoleSchedulesSection(StackPanel panel)
    {
        panel.Children.Add(CreateSectionHeader("Scheduled Console Commands"));
        panel.Children.Add(CreateTextBlock(
            "Add game server console commands and run each one on its own cron schedule.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 12),
            wrap: true));

        _consoleScheduleRowsPanel = new StackPanel();
        panel.Children.Add(_consoleScheduleRowsPanel);

        foreach (var schedule in ReadConsoleSchedules())
        {
            AddConsoleScheduleRow(schedule.Enabled, schedule.Cron, schedule.Command);
        }

        var addButton = new WpfButton
        {
            Content = "+ Add Console Command",
            MinWidth = 170,
            Margin = new Thickness(210, 0, 0, 14),
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        addButton.Click += (_, _) => AddConsoleScheduleRow(false, "*/30 * * * *", "say Scheduled message from WindowsGSH");
        panel.Children.Add(addButton);
    }

    private void AddConsoleScheduleRow(bool enabledValue, string cronValue, string commandValue)
    {
        if (_consoleScheduleRowsPanel == null)
        {
            return;
        }

        var border = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(210, 0, 0, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        border.SetResourceReference(BackgroundProperty, "PanelAltBrush");
        border.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new WpfCheckBox
        {
            Content = "Enabled",
            IsChecked = enabledValue,
            IsEnabled = !_isReadOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        enabled.SetResourceReference(ForegroundProperty, "TextBrush");
        enabled.SetResourceReference(StyleProperty, "AppCheckBox");
        grid.Children.Add(enabled);

        var cron = new WpfTextBox
        {
            MinHeight = 32,
            Text = string.IsNullOrWhiteSpace(cronValue) ? "*/30 * * * *" : cronValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(cron);
        Grid.SetColumn(cron, 1);
        grid.Children.Add(cron);

        var command = new WpfTextBox
        {
            MinHeight = 32,
            Text = commandValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);

        var deleteButton = new WpfButton
        {
            Content = "Delete",
            MinWidth = 82,
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        Grid.SetColumn(deleteButton, 3);
        grid.Children.Add(deleteButton);

        border.Child = grid;
        var controls = new ConsoleScheduleControls(border, enabled, cron, command);
        deleteButton.Click += (_, _) =>
        {
            _consoleScheduleControls.Remove(controls);
            _consoleScheduleRowsPanel.Children.Remove(border);
        };

        _consoleScheduleControls.Add(controls);
        _consoleScheduleRowsPanel.Children.Add(border);
    }

    private void AddApiSchedulesSection(StackPanel panel)
    {
        panel.Children.Add(CreateSectionHeader("Scheduled API Actions"));
        panel.Children.Add(CreateTextBlock(
            "Run module-defined API actions on cron schedules. Parameters use JSON, for example { \"message\": \"Restart soon\" }.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 12),
            wrap: true));

        _apiScheduleRowsPanel = new StackPanel();
        panel.Children.Add(_apiScheduleRowsPanel);

        foreach (var schedule in ReadApiSchedules())
        {
            AddApiScheduleRow(schedule.Enabled, schedule.Cron, schedule.ActionKey, schedule.ParametersJson);
        }

        var addButton = new WpfButton
        {
            Content = "+ Add API Action",
            MinWidth = 150,
            Margin = new Thickness(210, 0, 0, 14),
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        addButton.Click += (_, _) =>
        {
            var firstAction = _module is IModuleApiActionCapability api
                ? api.GetApiActions().FirstOrDefault()?.Key
                : null;
            AddApiScheduleRow(false, "*/30 * * * *", firstAction ?? string.Empty, "{}");
        };
        panel.Children.Add(addButton);
    }

    private void AddApiScheduleRow(bool enabledValue, string cronValue, string actionKeyValue, string parametersJsonValue)
    {
        if (_apiScheduleRowsPanel == null || _module is not IModuleApiActionCapability api)
        {
            return;
        }

        var actions = api.GetApiActions();

        var border = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(210, 0, 0, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        border.SetResourceReference(BackgroundProperty, "PanelAltBrush");
        border.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new WpfCheckBox
        {
            Content = "Enabled",
            IsChecked = enabledValue,
            IsEnabled = !_isReadOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        enabled.SetResourceReference(ForegroundProperty, "TextBrush");
        enabled.SetResourceReference(StyleProperty, "AppCheckBox");
        grid.Children.Add(enabled);

        var cron = new WpfTextBox
        {
            MinHeight = 32,
            Text = string.IsNullOrWhiteSpace(cronValue) ? "*/30 * * * *" : cronValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(cron);
        Grid.SetColumn(cron, 1);
        grid.Children.Add(cron);

        var action = new WpfComboBox
        {
            ItemsSource = actions,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Key",
            SelectedValue = string.IsNullOrWhiteSpace(actionKeyValue) ? actions.FirstOrDefault()?.Key : actionKeyValue,
            MinHeight = 32,
            IsEnabled = !_isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        var parameters = new WpfTextBox
        {
            MinHeight = 32,
            Text = string.IsNullOrWhiteSpace(parametersJsonValue) ? "{}" : parametersJsonValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(parameters);
        Grid.SetColumn(parameters, 3);
        grid.Children.Add(parameters);

        var deleteButton = new WpfButton
        {
            Content = "Delete",
            MinWidth = 82,
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        Grid.SetColumn(deleteButton, 4);
        grid.Children.Add(deleteButton);

        border.Child = grid;
        var controls = new ApiScheduleControls(border, enabled, cron, action, parameters);
        deleteButton.Click += (_, _) =>
        {
            _apiScheduleControls.Remove(controls);
            _apiScheduleRowsPanel.Children.Remove(border);
        };

        _apiScheduleControls.Add(controls);
        _apiScheduleRowsPanel.Children.Add(border);
    }

    private void AddExternalCommandSchedulesSection(StackPanel panel, bool globallyEnabled)
    {
        panel.Children.Add(CreateSectionHeader("Scheduled External Commands"));
        panel.Children.Add(CreateTextBlock(
            globallyEnabled
                ? "Run an explicitly allowlisted executable without a shell. Top row: Enabled, cron, executable, timeout seconds. Lower rows: arguments, then optional working directory. Output and audit results are written to logs."
                : "External commands are globally disabled. Existing rows are shown so they can be reviewed, disabled, or removed.",
            "MutedTextBrush",
            FontWeights.Normal,
            new Thickness(0, 0, 0, 12),
            wrap: true));

        _externalCommandScheduleRowsPanel = new StackPanel();
        panel.Children.Add(_externalCommandScheduleRowsPanel);
        foreach (var schedule in ReadExternalCommandSchedules())
        {
            AddExternalCommandScheduleRow(
                schedule.Enabled,
                schedule.Cron,
                schedule.ExecutablePath,
                schedule.Arguments,
                schedule.WorkingDirectory,
                schedule.TimeoutSeconds);
        }

        var addButton = new WpfButton
        {
            Content = "+ Add External Command",
            MinWidth = 180,
            Margin = new Thickness(210, 0, 0, 14),
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly && globallyEnabled
        };
        addButton.Click += (_, _) => AddExternalCommandScheduleRow(false, "0 * * * *", "", "", "", 300);
        panel.Children.Add(addButton);
    }

    private void AddExternalCommandScheduleRow(
        bool enabledValue,
        string cronValue,
        string executableValue,
        string argumentsValue,
        string workingDirectoryValue,
        int timeoutSecondsValue)
    {
        if (_externalCommandScheduleRowsPanel == null)
        {
            return;
        }

        var border = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(210, 0, 0, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        border.SetResourceReference(BackgroundProperty, "PanelAltBrush");
        border.SetResourceReference(BorderBrushProperty, "BorderBrushSoft");

        var panel = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new WpfCheckBox
        {
            Content = "Enabled",
            IsChecked = enabledValue,
            IsEnabled = !_isReadOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        enabled.SetResourceReference(ForegroundProperty, "TextBrush");
        enabled.SetResourceReference(StyleProperty, "AppCheckBox");
        top.Children.Add(enabled);

        var cron = CreateScheduleTextBox(string.IsNullOrWhiteSpace(cronValue) ? "0 * * * *" : cronValue);
        Grid.SetColumn(cron, 1);
        top.Children.Add(cron);

        var executable = CreateScheduleTextBox(executableValue);
        Grid.SetColumn(executable, 2);
        top.Children.Add(executable);

        var timeout = CreateScheduleTextBox(Math.Max(1, timeoutSecondsValue).ToString());
        Grid.SetColumn(timeout, 3);
        top.Children.Add(timeout);

        var deleteButton = new WpfButton
        {
            Content = "Delete",
            MinWidth = 82,
            Style = (Style)FindResource("DialogSecondaryButton"),
            IsEnabled = !_isReadOnly
        };
        Grid.SetColumn(deleteButton, 4);
        top.Children.Add(deleteButton);
        panel.Children.Add(top);

        var arguments = CreateScheduleTextBox(argumentsValue);
        arguments.Margin = new Thickness(0, 8, 0, 0);
        arguments.ToolTip = "Arguments are passed directly to the executable. WindowsGSH does not invoke a shell.";
        panel.Children.Add(arguments);

        var workingDirectory = CreateScheduleTextBox(workingDirectoryValue);
        workingDirectory.Margin = new Thickness(0, 8, 0, 0);
        workingDirectory.ToolTip = "Optional working directory. Defaults to the executable folder.";
        panel.Children.Add(workingDirectory);

        border.Child = panel;
        var controls = new ExternalCommandScheduleControls(
            border,
            enabled,
            cron,
            executable,
            arguments,
            workingDirectory,
            timeout);
        deleteButton.Click += (_, _) =>
        {
            _externalCommandScheduleControls.Remove(controls);
            _externalCommandScheduleRowsPanel.Children.Remove(border);
        };

        _externalCommandScheduleControls.Add(controls);
        _externalCommandScheduleRowsPanel.Children.Add(border);
    }

    private WpfTextBox CreateScheduleTextBox(string text)
    {
        var textBox = new WpfTextBox
        {
            MinHeight = 32,
            Text = text,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly,
            Margin = new Thickness(0, 0, 10, 0)
        };
        ApplyInputTheme(textBox);
        return textBox;
    }

    private void AddScheduleRow(StackPanel panel, string key, string label, string placeholder, string description)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 240, MaxWidth = 380 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 0, 12, 0)
        };
        labelBlock.SetResourceReference(ForegroundProperty, "TextBrush");
        grid.Children.Add(labelBlock);

        var rowPanel = new Grid();
        rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var enabled = new WpfCheckBox
        {
            Content = "Enabled",
            IsChecked = GetScheduleBoolean(key, "enabled", defaultValue: false),
            IsEnabled = !_isReadOnly,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        enabled.SetResourceReference(ForegroundProperty, "TextBrush");
        enabled.SetResourceReference(StyleProperty, "AppCheckBox");
        _scheduleEnabledControls[key] = enabled;
        rowPanel.Children.Add(enabled);

        var cronTextBox = new WpfTextBox
        {
            MinHeight = 32,
            Text = GetScheduleString(key, "cron"),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsReadOnly = _isReadOnly
        };
        if (string.IsNullOrWhiteSpace(cronTextBox.Text))
        {
            cronTextBox.Text = placeholder;
        }

        ApplyInputTheme(cronTextBox);
        _scheduleCronControls[key] = cronTextBox;
        Grid.SetColumn(cronTextBox, 1);
        rowPanel.Children.Add(cronTextBox);

        var stack = new StackPanel();
        stack.Children.Add(rowPanel);
        var descriptionBlock = new TextBlock
        {
            Text = description,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        descriptionBlock.SetResourceReference(ForegroundProperty, "MutedTextBrush");
        stack.Children.Add(descriptionBlock);

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        panel.Children.Add(grid);
    }

    private void CronHelpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://crontab.guru/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not open cron help: {ex.Message}";
        }
    }

    private static ScrollViewer CreateTabScrollViewer(UIElement content)
    {
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            // Extra right-side padding beyond the other three sides: without it, a field's input
            // control (TextBox/ComboBox/etc.) sits flush against the vertical scrollbar track, with
            // no visual breathing room between the two.
            Padding = new Thickness(14, 14, 26, 14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        scrollViewer.SetResourceReference(BackgroundProperty, "PanelBrush");
        return scrollViewer;
    }

    private static TextBlock CreateTextBlock(string text, string foregroundResource, FontWeight weight, Thickness? margin = null, bool wrap = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontWeight = weight,
            Margin = margin ?? new Thickness(0),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        textBlock.SetResourceReference(ForegroundProperty, foregroundResource);
        return textBlock;
    }

    private static TextBlock CreateSectionHeader(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 14)
        };
        textBlock.SetResourceReference(ForegroundProperty, "TextBrush");
        return textBlock;
    }

    private bool GetAutomationBoolean(string key, bool defaultValue)
    {
        return GetBooleanFromObject(_config["automation"] as JsonObject, key, defaultValue);
    }

    private bool GetBackupBoolean(string key, bool defaultValue)
    {
        return GetBooleanFromObject(_config["backup"] as JsonObject, key, defaultValue);
    }

    private bool GetRuntimeBoolean(string key, bool defaultValue)
    {
        return GetBooleanFromObject(_config["runtimeSettings"] as JsonObject, key, defaultValue);
    }

    private string GetRuntimeString(string key)
    {
        return GetStringFromObject(_config["runtimeSettings"] as JsonObject ?? [], key);
    }

    private string GetNetworkString(string key)
    {
        return GetStringFromObject(_config["network"] as JsonObject ?? [], key);
    }

    private bool GetDiscordBoolean(string key, bool defaultValue)
    {
        return GetBooleanFromObject(_config["discord"] as JsonObject, key, defaultValue);
    }

    /// <summary>
    /// Initial value for the Card Channel field. Prefers <c>CardChannelId</c> (set by the v9
    /// migration's backfill, or by a previous save through this same UI); falls back to the
    /// legacy DB <c>ChannelName</c> (a row saved through the pre-Chunk-3 editor after the
    /// migration ran keeps this current but not <c>CardChannelId</c>, which that editor never
    /// touched); falls back to the server's own JSON <c>discord.channel</c> for servers that
    /// never had a <c>discord_server_settings</c> row at all. This is the "populate Card Channel
    /// from old discord.channel on first load" behavior this chunk's plan calls for.
    /// </summary>
    private string GetInitialCardChannelValue()
    {
        var saved = _discordRepository.GetServerSettings(_server.Id);
        if (!string.IsNullOrWhiteSpace(saved?.CardChannelId))
        {
            return saved.CardChannelId;
        }

        if (!string.IsNullOrWhiteSpace(saved?.ChannelName))
        {
            return saved.ChannelName;
        }

        return GetStringFromObject(_config["discord"] as JsonObject ?? [], "channel");
    }

    private bool GetScheduleBoolean(string scheduleKey, string propertyName, bool defaultValue)
    {
        return GetBooleanFromObject(GetScheduleObject(scheduleKey), propertyName, defaultValue);
    }

    private string GetScheduleString(string scheduleKey, string propertyName)
    {
        var schedule = GetScheduleObject(scheduleKey);
        if (schedule == null ||
            !schedule.TryGetPropertyValue(propertyName, out var value) ||
            value is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var stringValue))
        {
            return string.Empty;
        }

        return stringValue;
    }

    private JsonObject? GetScheduleObject(string scheduleKey)
    {
        return _config["schedules"] is JsonObject schedules &&
            schedules[scheduleKey] is JsonObject schedule
            ? schedule
            : null;
    }

    private IReadOnlyList<RconScheduleValue> ReadRconSchedules()
    {
        if (_config["schedules"] is not JsonObject schedules ||
            schedules["rconCommands"] is not JsonArray commands)
        {
            return [];
        }

        return commands
            .OfType<JsonObject>()
            .Select(command => new RconScheduleValue(
                GetBooleanFromObject(command, "enabled", defaultValue: false),
                GetStringFromObject(command, "cron"),
                GetStringFromObject(command, "command")))
            .Where(command => !string.IsNullOrWhiteSpace(command.Cron) || !string.IsNullOrWhiteSpace(command.Command))
            .ToArray();
    }

    private IReadOnlyList<ConsoleScheduleValue> ReadConsoleSchedules()
    {
        if (_config["schedules"] is not JsonObject schedules ||
            schedules["consoleCommands"] is not JsonArray commands)
        {
            return [];
        }

        return commands
            .OfType<JsonObject>()
            .Select(command => new ConsoleScheduleValue(
                GetBooleanFromObject(command, "enabled", defaultValue: false),
                GetStringFromObject(command, "cron"),
                GetStringFromObject(command, "command")))
            .Where(command => !string.IsNullOrWhiteSpace(command.Cron) || !string.IsNullOrWhiteSpace(command.Command))
            .ToArray();
    }

    private IReadOnlyList<ApiScheduleValue> ReadApiSchedules()
    {
        if (_config["schedules"] is not JsonObject schedules ||
            schedules["apiActions"] is not JsonArray actions)
        {
            return [];
        }

        return actions
            .OfType<JsonObject>()
            .Select(action => new ApiScheduleValue(
                GetBooleanFromObject(action, "enabled", defaultValue: false),
                GetStringFromObject(action, "cron"),
                GetStringFromObject(action, "action"),
                action.TryGetPropertyValue("parameters", out var parameters) && parameters is JsonObject parameterObject
                    ? parameterObject.ToJsonString()
                    : "{}"))
            .Where(action => !string.IsNullOrWhiteSpace(action.Cron) || !string.IsNullOrWhiteSpace(action.ActionKey))
            .ToArray();
    }

    private IReadOnlyList<ExternalCommandScheduleValue> ReadExternalCommandSchedules()
    {
        if (_config["schedules"] is not JsonObject schedules ||
            schedules["externalCommands"] is not JsonArray commands)
        {
            return [];
        }

        return commands
            .OfType<JsonObject>()
            .Select(command => new ExternalCommandScheduleValue(
                GetBooleanFromObject(command, "enabled", defaultValue: false),
                GetStringFromObject(command, "cron"),
                GetStringFromObject(command, "executable"),
                GetStringFromObject(command, "arguments"),
                GetStringFromObject(command, "workingDirectory"),
                GetInt32FromObject(command, "timeoutSeconds", 300)))
            .Where(command => !string.IsNullOrWhiteSpace(command.Cron) || !string.IsNullOrWhiteSpace(command.ExecutablePath))
            .ToArray();
    }

    private static int GetInt32FromObject(JsonObject parent, string key, int defaultValue)
    {
        return parent.TryGetPropertyValue(key, out var value) &&
            value is JsonValue jsonValue &&
            jsonValue.TryGetValue<int>(out var intValue)
            ? intValue
            : defaultValue;
    }

    private static string GetStringFromObject(JsonObject parent, string key)
    {
        return parent.TryGetPropertyValue(key, out var value) &&
            value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var stringValue)
            ? stringValue
            : string.Empty;
    }

    private static bool GetBooleanFromObject(JsonObject? parent, string key, bool defaultValue)
    {
        if (parent == null ||
            !parent.TryGetPropertyValue(key, out var value) ||
            value is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<bool>(out var boolValue))
        {
            return defaultValue;
        }

        return boolValue;
    }

    private string GetBackupPathsText()
    {
        if (_config["backup"] is JsonObject backup &&
            backup["paths"] is JsonArray paths &&
            paths.Count > 0)
        {
            return string.Join(Environment.NewLine, paths.Select(path => path?.GetValue<string>()).Where(path => !string.IsNullOrWhiteSpace(path)));
        }

        return string.Empty;
    }

    private string GetBackupString(string key)
    {
        return GetStringFromObject(_config["backup"] as JsonObject ?? [], key);
    }

    private IReadOnlyList<string> ReadBackupPathsFromTextBox()
    {
        return (_backupPathsTextBox?.Text ?? string.Empty)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RefreshBackupComboBox()
    {
        if (_backupComboBox == null)
        {
            return;
        }

        _backupComboBox.Items.Clear();
        foreach (var backup in _backupService.ListBackupInfo(CreateServerInstance()))
        {
            _backupComboBox.Items.Add(new BackupChoice(backup.BackupPath, $"{backup.FileName} · {FormatBackupSize(backup.SizeBytes)}"));
        }

        if (_backupComboBox.Items.Count > 0)
        {
            _backupComboBox.SelectedIndex = 0;
        }
    }

    private static string FormatBackupSize(long bytes)
    {
        return bytes >= 1024L * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }

    private object? GetSettingValue(string key, object? fallback)
    {
        if (_config["settings"] is not JsonObject settings || !settings.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolValue) => boolValue,
            JsonValue jsonValue when jsonValue.TryGetValue<int>(out var intValue) => intValue,
            _ => fallback
        };
    }

    private object? GetAddonSettingValue(string addonId, string key, object? fallback)
    {
        if (_config["addons"] is not JsonObject addons ||
            addons[addonId] is not JsonObject addon ||
            addon["settings"] is not JsonObject settings)
        {
            return fallback;
        }

        return GetSettingValue(settings, key, fallback);
    }

    private void AddField(StackPanel panel, Dictionary<string, FrameworkElement> controls, ConfigFieldDefinition field, object? value)
    {
        switch (field.Type)
        {
            case ConfigFieldType.Boolean:
                AddCheckBoxRow(panel, controls, field, value is bool boolValue && boolValue);
                break;
            case ConfigFieldType.Password:
                AddPasswordRow(panel, controls, field, value?.ToString() ?? string.Empty);
                break;
            case ConfigFieldType.Select:
                AddComboRow(panel, controls, field, value?.ToString() ?? string.Empty);
                break;
            default:
                AddTextRow(panel, controls, field, value?.ToString() ?? string.Empty);
                break;
        }
    }

    private void AddTextRow(StackPanel panel, Dictionary<string, FrameworkElement> controls, ConfigFieldDefinition field, string value)
    {
        var textBox = new WpfTextBox { MinHeight = 32, Text = value, VerticalContentAlignment = VerticalAlignment.Center, IsReadOnly = _isReadOnly };
        ApplyInputTheme(textBox);
        textBox.TextChanged += (_, _) => RefreshResolvedLaunchArguments();
        controls[field.Key] = textBox;
        AddRow(panel, GetFieldLabel(field), textBox, field.Description);
    }

    private void AddPasswordRow(StackPanel panel, Dictionary<string, FrameworkElement> controls, ConfigFieldDefinition field, string value)
    {
        var passwordBox = new PasswordBox { MinHeight = 32, Password = value, IsEnabled = !_isReadOnly };
        ApplyInputTheme(passwordBox);
        passwordBox.PasswordChanged += (_, _) => RefreshResolvedLaunchArguments();
        controls[field.Key] = passwordBox;
        AddRow(panel, GetFieldLabel(field), passwordBox, field.Description);
    }

    private void AddComboRow(StackPanel panel, Dictionary<string, FrameworkElement> controls, ConfigFieldDefinition field, string value)
    {
        var comboBox = new WpfComboBox { MinHeight = 32, IsEditable = true, IsEnabled = !_isReadOnly };
        ApplyInputTheme(comboBox);
        foreach (var option in field.Options ?? [])
        {
            comboBox.Items.Add(option);
        }

        comboBox.Text = value;
        comboBox.SelectionChanged += (_, _) => RefreshResolvedLaunchArguments();
        comboBox.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => RefreshResolvedLaunchArguments()));
        if (_hasConditionalFields)
        {
            comboBox.SelectionChanged += (_, _) => { if (!_renderingFields) RenderFields(); };
        }

        controls[field.Key] = comboBox;
        AddRow(panel, GetFieldLabel(field), comboBox, field.Description);
    }

    private void AddCheckBoxRow(StackPanel panel, Dictionary<string, FrameworkElement> controls, ConfigFieldDefinition field, bool value)
    {
        var checkBox = new WpfCheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_isReadOnly };
        checkBox.SetResourceReference(ForegroundProperty, "TextBrush");
        checkBox.SetResourceReference(StyleProperty, "AppCheckBox");
        checkBox.Checked += (_, _) => RefreshResolvedLaunchArguments();
        checkBox.Unchecked += (_, _) => RefreshResolvedLaunchArguments();
        if (_hasConditionalFields)
        {
            checkBox.Checked += (_, _) => { if (!_renderingFields) RenderFields(); };
            checkBox.Unchecked += (_, _) => { if (!_renderingFields) RenderFields(); };
        }

        controls[field.Key] = checkBox;
        AddRow(panel, GetFieldLabel(field), checkBox, field.Description);
    }

    private string? AddSectionHeaderIfNeeded(StackPanel panel, string? currentGroup, string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || string.Equals(currentGroup, group, StringComparison.OrdinalIgnoreCase))
        {
            return currentGroup;
        }

        panel.Children.Add(CreateSectionHeader(group));
        return group;
    }

    private bool IsBaseFieldVisible(ConfigFieldDefinition field, IReadOnlyDictionary<string, object?>? currentValues = null)
    {
        return ModuleFieldFormService.IsFieldVisible(
            field,
            key => currentValues?.TryGetValue(key, out var currentValue) == true
                ? currentValue
                : _baseFieldControls.Count > 0
                    ? GetFieldValue(_baseFieldControls, key)
                    : GetSettingValue(key, null));
    }

    private static string GetFieldLabel(ConfigFieldDefinition field)
    {
        return field.RestartRequired ? $"{field.Label} (restart)" : field.Label;
    }

    private void AddLaunchArgumentPreviewFields(StackPanel panel)
    {
        var template = GetLaunchArgumentsTemplate();

        AddReadOnlyCommandRow(
            panel,
            "Launch Args Template",
            string.IsNullOrWhiteSpace(template) ? "Module does not declare runtime.defaultArguments." : template,
            string.IsNullOrWhiteSpace(template)
                ? "This module may build launch arguments in C# code. Add runtime.defaultArguments to the module manifest for an exact preview."
                : "Imported from the module runtime.defaultArguments value.");

        _resolvedLaunchArgsTextBox = AddReadOnlyCommandRow(
            panel,
            "Resolved Launch Args",
            BuildResolvedLaunchArguments(template, ReadSettingsFromControls()),
            "Preview of the arguments WindowsGSH will pass to the server executable.");

        _changedLaunchFieldsTextBox = AddReadOnlyCommandRow(
            panel,
            "Changed Launch Fields",
            BuildChangedLaunchFields(template, ReadSettingsFromControls()),
            "Fields used by launch arguments that differ from the saved config.");
    }

    private WpfTextBox AddReadOnlyCommandRow(StackPanel panel, string label, string value, string description)
    {
        var textBox = new WpfTextBox
        {
            MinHeight = 58,
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsReadOnly = true,
            // See _backupPathsTextBox above - same PassiveScrollViewer defect, same fix. This
            // helper backs three fields (the raw command preview plus resolved/changed launch
            // arguments), all plain scrollable multi-line TextBoxes.
            Style = null
        };
        ApplyInputTheme(textBox);
        AddRow(panel, label, textBox, description);
        return textBox;
    }

    private void RefreshResolvedLaunchArguments()
    {
        if (_resolvedLaunchArgsTextBox == null)
        {
            return;
        }

        _resolvedLaunchArgsTextBox.Text = BuildResolvedLaunchArguments(GetLaunchArgumentsTemplate(), ReadSettingsFromControls());
        if (_changedLaunchFieldsTextBox != null)
        {
            _changedLaunchFieldsTextBox.Text = BuildChangedLaunchFields(GetLaunchArgumentsTemplate(), ReadSettingsFromControls());
        }
    }

    private string GetLaunchArgumentsTemplate()
    {
        var moduleJsonPath = Path.Combine(ModuleStoragePaths.InstalledModules, _server.ModuleId, "module.json");
        if (!File.Exists(moduleJsonPath))
        {
            return string.Empty;
        }

        try
        {
            return ModuleManifest.Load(moduleJsonPath).GetDefaultArguments();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildResolvedLaunchArguments(string template, IReadOnlyDictionary<string, object?> settings)
    {
        return CollapseCommandLineWhitespace(ModuleLaunchArgumentBuilder.Build(template, settings));
    }

    private bool SupportsApiActions()
    {
        return _module?.Capabilities.SupportsApiActions == true &&
            _module is IModuleApiActionCapability api &&
            api.GetApiConnection() is { } connection &&
            api.GetApiActions().Count > 0 &&
            IsApiEnabled(
                connection.EnabledKey,
                GetFieldValue(_baseFieldControls, connection.EnabledKey) ?? GetSettingValue(connection.EnabledKey, null));
    }

    private string BuildChangedLaunchFields(string template, IReadOnlyDictionary<string, object?> currentSettings)
    {
        var keys = Regex.Matches(template, "\\{quote:(?<key>[^}]+)\\}|\\{\\??(?<key>[^}:}]+)(?::[^}]*)?\\}")
            .Select(match => match.Groups["key"].Value)
            .Where(key => !string.IsNullOrWhiteSpace(key) && !key.StartsWith("quote:", StringComparison.OrdinalIgnoreCase))
            .Append("server.additionalArguments")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var settingsObject = _config["settings"] as JsonObject ?? [];
        var changed = new List<string>();
        foreach (var key in keys)
        {
            currentSettings.TryGetValue(key, out var currentValue);
            var savedValue = GetSettingValue(settingsObject, key, null);
            if (!string.Equals(currentValue?.ToString() ?? string.Empty, savedValue?.ToString() ?? string.Empty, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }

        return changed.Count == 0 ? "None" : string.Join(", ", changed);
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            _ => value != null
        };
    }

    private static bool IsApiEnabled(string enabledKey, object? value)
    {
        return string.IsNullOrWhiteSpace(enabledKey) || IsTruthy(value);
    }

    private static string CollapseCommandLineWhitespace(string text)
    {
        var collapsed = new System.Text.StringBuilder();
        var inQuotes = false;
        var pendingSpace = false;
        foreach (var ch in text.Trim())
        {
            if (ch == '"')
            {
                if (pendingSpace && collapsed.Length > 0)
                {
                    collapsed.Append(' ');
                }

                pendingSpace = false;
                inQuotes = !inQuotes;
                collapsed.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && collapsed.Length > 0)
            {
                collapsed.Append(' ');
            }

            pendingSpace = false;
            collapsed.Append(ch);
        }

        return collapsed.ToString();
    }

    private static void ApplyInputTheme(System.Windows.Controls.Control control)
    {
        control.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelAltBrush");
        control.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush");
        control.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "BorderBrushSoft");
    }

    private void AddRow(StackPanel panel, string label, FrameworkElement control, string? description)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0)
        };
        labelBlock.SetResourceReference(ForegroundProperty, "TextBrush");
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var stack = new StackPanel();
        stack.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionBlock = new TextBlock { Text = description, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            descriptionBlock.SetResourceReference(ForegroundProperty, "MutedTextBrush");
            stack.Children.Add(descriptionBlock);
        }

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        panel.Children.Add(grid);
    }

    private void ApplyReadOnlyState()
    {
        if (!_isReadOnly)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        StatusTextBlock.Text = "Server is running. Stop the server before making config changes.";
    }

    private void ContentGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || WpfInteractionHelper.IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly)
        {
            StatusTextBlock.Text = "Server is running. Stop the server before making config changes.";
            return;
        }

        ConfigTabControl.IsEnabled = false;
        SaveButton.IsEnabled = false;
        try
        {
        var previousUpnpPolicy = _persistedUpnpPolicy;
        var selectedUpnpPolicy = ReadSelectedUpnpPolicy();
        var policyDoesNotRetainStoppedMappings = selectedUpnpPolicy != UpnpMappingPolicy.MapOnStart;
        var shouldCleanUpMappings = previousUpnpPolicy == UpnpMappingPolicy.MapOnStart &&
            policyDoesNotRetainStoppedMappings;
        Exception? registryReadFailure = null;
        if (!shouldCleanUpMappings && policyDoesNotRetainStoppedMappings)
        {
            try
            {
                var ownedMappings = await _portMappingRegistry.GetAllAsync(CancellationToken.None);
                shouldCleanUpMappings = ownedMappings.Any(mapping =>
                    string.Equals(mapping.ServerId, _server.Id, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                // Mapping ownership is ancillary to the rest of this editor. A malformed row for
                // another server must not prevent unrelated game/runtime settings from being saved.
                registryReadFailure = ex;
                AppLogService.Add(
                    $"Could not inspect UPnP mapping ownership while saving {_server.Name}: {ex.Message}",
                    _server.Id);
            }
        }

        if (shouldCleanUpMappings)
        {
            var confirmation = System.Windows.MessageBox.Show(
                $"Apply the new port-forwarding policy for {_server.Name}?{Environment.NewLine}{Environment.NewLine}" +
                "Because this server is currently stopped, WindowsGSH will remove any retained router mappings it previously created. " +
                "Mappings created by you or another application will not be changed.",
                "Update Automatic Port Forwarding",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmation != System.Windows.MessageBoxResult.Yes)
            {
                StatusTextBlock.Text = "Save cancelled; automatic port forwarding was not changed.";
                return;
            }
        }

        var settings = _config["settings"] as JsonObject ?? [];
        _config["settings"] = settings;
        var baseSettings = ReadSettingsFromControls();
        ModuleFieldFormService.ValidateSettings(_module?.GetConfigFields() ?? [], baseSettings);

        foreach (var field in _module?.GetConfigFields() ?? [])
        {
            if (baseSettings.TryGetValue(field.Key, out var value))
            {
                settings[field.Key] = ModuleFieldFormService.ToJsonValue(field, value);
            }
        }

        SaveAutomationToConfigObject();
        SaveJavaToConfigObject();
        ValidateSelectedJavaRuntime();
        SaveRuntimeToConfigObject();
        SaveNetworkToConfigObject();
        SaveBackupToConfigObject();
        if (_backupDestinationTextBox != null)
        {
            _backupService.ValidateBackupDestination(CreateServerInstance());
        }
        SaveSchedulesToConfigObject(validateExternalCommands: true);
        SaveDiscordToConfigObject(persist: true);

        foreach (var addon in _module?.GetAddonDefinitions() ?? [])
        {
            if (!_addonFieldControls.TryGetValue(addon.Id, out var controls))
            {
                continue;
            }

            var addons = _config["addons"] as JsonObject ?? [];
            _config["addons"] = addons;
            var addonObject = addons[addon.Id] as JsonObject ?? [];
            addons[addon.Id] = addonObject;
            addonObject["enabled"] ??= true;
            var addonSettings = addonObject["settings"] as JsonObject ?? [];
            addonObject["settings"] = addonSettings;
            var values = addon.ConfigFields.ToDictionary(field => field.Key, field => GetFieldValue(controls, field.Key), StringComparer.OrdinalIgnoreCase);
            ModuleFieldFormService.ValidateSettings(addon.ConfigFields, values);
            foreach (var field in addon.ConfigFields)
            {
                addonSettings[field.Key] = ModuleFieldFormService.ToJsonValue(field, values[field.Key]);
            }
        }

        if (_module != null)
        {
            _config["name"] = _module.GetServerName(baseSettings);
        }

            if (_module != null)
            {
                await _module.WriteConfigFileSettingsAsync(CreateServerInstance(), CancellationToken.None);
                new ServerSecretStore().ProtectConfigSecrets(_module, _server.ServerFolder, _config);
            }

            File.WriteAllText(_server.ConfigPath, _config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _savedConfigSnapshot = SerializeConfig();
            _persistedUpnpPolicy = selectedUpnpPolicy;
            _upnpMappingLifecycleService.SetCurrentPolicy(_server.Id, selectedUpnpPolicy);
            var enabledAutomaticMapping = previousUpnpPolicy == UpnpMappingPolicy.Manual &&
                selectedUpnpPolicy != UpnpMappingPolicy.Manual;
            if (enabledAutomaticMapping && IsServerRunningForUpnpReconcile())
            {
                // A Discord start may already have passed the old Manual policy before the editor
                // published this one. Re-enter the shared lifecycle gate and reconcile now; if the
                // start has not reached its hook yet, the override above makes that later hook map.
                await _upnpMappingLifecycleService.MapOnStartAsync(
                    _server,
                    _module!,
                    CreateServerInstance(),
                    CancellationToken.None);
            }

            if (shouldCleanUpMappings)
            {
                try
                {
                    await _upnpMappingLifecycleService.RemoveAllOwnedMappingsAsync(_server, CancellationToken.None);
                    var remainingOwnedMappings = (await _portMappingRegistry.GetAllAsync(CancellationToken.None))
                        .Count(mapping => string.Equals(mapping.ServerId, _server.Id, StringComparison.Ordinal));
                    if (remainingOwnedMappings > 0)
                    {
                        StatusTextBlock.Text =
                            $"Saved, but {remainingOwnedMappings} WindowsGSH-owned router mapping(s) could not be " +
                            "confirmed removed. Check the application log and your router.";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogService.Add($"UPnP cleanup could not be verified for {_server.Name}: {ex.Message}", _server.Id);
                    StatusTextBlock.Text =
                        "Saved, but WindowsGSH could not inspect or remove its router mappings. " +
                        "Check the application log and your router.";
                    return;
                }
            }
            else if (registryReadFailure != null)
            {
                StatusTextBlock.Text =
                    "Saved. WindowsGSH could not inspect existing UPnP ownership records; other settings were not blocked. " +
                    "Check the application log.";
                return;
            }

            if (!CreateFirewallRulesIfRequested())
            {
                return;
            }

            _suppressUnsavedPrompt = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            if (!_suppressUnsavedPrompt)
            {
                ConfigTabControl.IsEnabled = !_isReadOnly;
                SaveButton.IsEnabled = !_isReadOnly;
            }
        }
    }

    private bool CreateFirewallRulesIfRequested()
    {
        if (_module == null ||
            _createFirewallRulesCheckBox?.IsChecked != true ||
            _createFirewallRulesCheckBox.IsEnabled == false)
        {
            return true;
        }

        if (!IsRunningAsAdministrator())
        {
            const string message = "Saved. Firewall rules require administrator rights. Restart WindowsGSH as administrator and tick Create Firewall Rules again.";
            StatusTextBlock.Text = message;
            LogServerFirewallMessage(message);
            return false;
        }

        var result = _firewallService.CreateOrUpdateRules(new FirewallRuleApplyRequest(CreateServerInstance(), _module));
        if (!result.Success)
        {
            var error = result.Steps.FirstOrDefault(step => !step.Success);
            var message = $"Saved. Could not create Windows Firewall rules: {error?.Error ?? result.Message}";
            StatusTextBlock.Text = message;
            LogServerFirewallMessage(message);
            return false;
        }

        _createFirewallRulesCheckBox.IsChecked = true;
        _createFirewallRulesCheckBox.IsEnabled = false;
        var resultMessage = result.CreatedRules.Count == 0
            ? "Saved. Firewall rules already existed."
            : $"Saved. Created {result.CreatedRules.Count} Windows Firewall rule(s).";
        StatusTextBlock.Text = resultMessage;
        LogServerFirewallMessage(result.CreatedRules.Count == 0
            ? "No Windows Firewall rules needed to be created."
            : $"Created {result.CreatedRules.Count} Windows Firewall rule(s).");
        return true;
    }

    private void LogServerFirewallMessage(string message)
    {
        ServerConsoleService.Add(_server.Id, message);
        AppLogService.Add(message, _server.Id);
    }

    private void DownloadAddonButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ServerAddonDefinition addon } ||
            string.IsNullOrWhiteSpace(addon.DownloadUrl))
        {
            StatusTextBlock.Text = "This addon does not provide a download page.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = addon.DownloadUrl,
                UseShellExecute = true
            });
            StatusTextBlock.Text = $"Opened {addon.Name} download page. Follow the install instructions here, then reopen CFG.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not open addon download page: {ex.Message}";
        }
    }

    private async void ToggleAddonButton_Click(object sender, RoutedEventArgs e)
    {
        if (_module == null || sender is not WpfButton { Tag: ServerAddonDefinition addon })
        {
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginOperation(ServerOperationKind.Addon, out operation))
            {
                return;
            }

            _operationManager.UpdateStatus(_server.Id, "Updating addon");
            SaveCurrentValuesToConfigObject();
            var instance = CreateServerInstance();
            var status = _module.GetAddonStatus(instance, addon.Id);
            if (status.IsInstalled && status.IsEnabled)
            {
                await _module.RemoveAddonAsync(instance, addon.Id, operation!.CancellationToken);
            }
            else
            {
                await _module.InstallAddonAsync(instance, addon.Id, operation!.CancellationToken);
            }

            ReloadConfig();
            RenderFields();
            if (!status.IsInstalled || !status.IsEnabled)
            {
                SelectAddonTab(addon.Id);
            }

            StatusTextBlock.Text = status.IsInstalled && status.IsEnabled
                ? $"{addon.Name} removed."
                : status.IsInstalled
                    ? $"{addon.Name} repaired."
                    : $"{addon.Name} installed.";
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(_server.Id, ex);
            StatusTextBlock.Text = $"Addon update failed: {ex.Message}";
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private void SelectAddonConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string addonId })
        {
            SelectAddonTab(addonId);
        }
    }

    private async void BackupNowButton_Click(object sender, RoutedEventArgs e)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginOperation(ServerOperationKind.Backup, out operation))
            {
                return;
            }

            _operationManager.UpdateStatus(_server.Id, "Backing up");
            SaveCurrentValuesToConfigObject();
            var backupResult = await _backupService.CreateBackupAsync(
                CreateServerInstance(),
                _module,
                ReadBackupPathsFromTextBox(),
                AppSettings.Load().BackupRetentionCount,
                operation!.CancellationToken);
            var backupPath = backupResult.BackupPath ?? throw new InvalidOperationException(backupResult.Message);
            RefreshBackupComboBox();
            StatusTextBlock.Text = $"Backup created: {backupPath}{backupResult.BuildWarningSuffix()}";
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(_server.Id, ex);
            StatusTextBlock.Text = $"Backup failed: {ex.Message}";
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private void PreviewBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupComboBox?.SelectedItem is not BackupChoice backup)
        {
            StatusTextBlock.Text = "Select a backup to preview.";
            return;
        }

        try
        {
            var preview = _backupService.GetBackupPreview(CreateServerInstance(), backup.Path);
            new BackupPreviewWindow(preview)
            {
                Owner = this
            }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Backup preview failed: {ex}", nameof(PreviewBackupButton_Click));
            var detail = ex.InnerException?.Message ?? ex.Message;
            StatusTextBlock.Text = $"Preview failed: {detail}";
        }
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentValuesToConfigObject();
        var folder = _backupService.ResolveBackupFolder(CreateServerInstance());
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder}\"",
            UseShellExecute = true
        });
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupComboBox?.SelectedItem is not BackupChoice backup)
        {
            StatusTextBlock.Text = "Select a backup to restore.";
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginOperation(ServerOperationKind.Restore, out operation))
            {
                return;
            }

            _operationManager.UpdateStatus(_server.Id, "Restoring");
            if (_module != null && ServerProcessLocator.IsRunning(_module, _server.InstallPath))
            {
                StatusTextBlock.Text = "Stop the server before restoring a backup.";
                return;
            }

            var preview = _backupService.GetBackupPreview(CreateServerInstance(), backup.Path);
            var confirm = System.Windows.MessageBox.Show(
                $"Restore {backup.DisplayName} to {_server.Name}?{Environment.NewLine}{Environment.NewLine}" +
                $"{preview.ExistingFileCount} existing files will be overwritten and {preview.NewFileCount} files will be created.",
                "Restore Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var restore = await _backupService.RestoreBackupAsync(
                CreateServerInstance(),
                backup.Path,
                _module,
                confirmedWhileRunning: false,
                operation!.CancellationToken);
            if (!restore.Success)
            {
                throw new InvalidOperationException(restore.Message);
            }

            StatusTextBlock.Text = $"Restored backup: {Path.GetFileName(backup.Path)}";
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(_server.Id, ex);
            StatusTextBlock.Text = $"Restore failed: {ex.Message}";
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private bool TryBeginOperation(ServerOperationKind kind, out ServerOperationScope? operation)
    {
        if (_operationManager.TryBegin(_server.Id, _server.Name, kind, out operation, out var existing))
        {
            return true;
        }

        StatusTextBlock.Text = $"Server is busy: {existing?.DisplayText ?? "operation running"}.";
        return false;
    }

    private void SelectAddonTab(string addonId)
    {
        if (_module == null)
        {
            return;
        }

        var addon = _module.GetAddonDefinitions().FirstOrDefault(item => string.Equals(item.Id, addonId, StringComparison.OrdinalIgnoreCase));
        if (addon == null)
        {
            return;
        }

        foreach (var item in ConfigTabControl.Items.OfType<TabItem>())
        {
            if (string.Equals(item.Header?.ToString(), addon.Name, StringComparison.OrdinalIgnoreCase))
            {
                ConfigTabControl.SelectedItem = item;
                return;
            }
        }
    }

    private void ReloadConfig()
    {
        _config.Clear();
        var latest = JsonNode.Parse(File.ReadAllText(_server.ConfigPath))?.AsObject() ?? [];
        if (_module != null)
        {
            new ServerSecretStore().ResolveConfigSecrets(_module, _server.ServerFolder, latest);
        }

        foreach (var property in latest)
        {
            _config[property.Key] = property.Value?.DeepClone();
        }

        _persistedUpnpPolicy = ReadConfiguredUpnpPolicy();
    }

    private void SaveCurrentValuesToConfigObject()
    {
        var settings = _config["settings"] as JsonObject ?? [];
        _config["settings"] = settings;

        foreach (var field in _module?.GetConfigFields() ?? [])
        {
            if (_baseFieldControls.ContainsKey(field.Key))
            {
                settings[field.Key] = ModuleFieldFormService.ToJsonValue(field, GetFieldValue(_baseFieldControls, field.Key));
            }
        }

        SaveAutomationToConfigObject();
        SaveRuntimeToConfigObject();
        SaveNetworkToConfigObject();
        SaveBackupToConfigObject();
        SaveSchedulesToConfigObject();
        SaveDiscordToConfigObject(persist: false);

        foreach (var addon in _module?.GetAddonDefinitions() ?? [])
        {
            if (!_addonFieldControls.TryGetValue(addon.Id, out var controls))
            {
                continue;
            }

            var addons = _config["addons"] as JsonObject ?? [];
            _config["addons"] = addons;
            var addonObject = addons[addon.Id] as JsonObject ?? [];
            addons[addon.Id] = addonObject;
            var addonSettings = addonObject["settings"] as JsonObject ?? [];
            addonObject["settings"] = addonSettings;
            foreach (var field in addon.ConfigFields)
            {
                addonSettings[field.Key] = ModuleFieldFormService.ToJsonValue(field, GetFieldValue(controls, field.Key));
            }
        }
    }

    private void SaveAutomationToConfigObject()
    {
        var automation = _config["automation"] as JsonObject ?? [];
        _config["automation"] = automation;
        foreach (var (key, checkBox) in _automationControls)
        {
            automation[key] = checkBox.IsChecked == true;
        }
    }

    private void SaveRuntimeToConfigObject()
    {
        if (_applyPriorityOnStartCheckBox == null &&
            _processPriorityComboBox == null &&
            _applyAffinityOnStartCheckBox == null &&
            _cpuAffinityMaskTextBox == null)
        {
            return;
        }

        var runtime = _config["runtimeSettings"] as JsonObject ?? [];
        _config["runtimeSettings"] = runtime;
        runtime["applyPriorityOnStart"] = _applyPriorityOnStartCheckBox?.IsChecked == true;
        runtime["processPriorityClass"] = _processPriorityComboBox?.Text.Trim() ?? string.Empty;
        runtime["applyAffinityOnStart"] = _applyAffinityOnStartCheckBox?.IsChecked == true;
        runtime["cpuAffinityMask"] = _cpuAffinityMaskTextBox?.Text.Trim() ?? string.Empty;
        ValidateRuntimeTuning(runtime);
    }

    private void SaveNetworkToConfigObject()
    {
        if (_upnpMappingPolicyComboBox == null)
        {
            return;
        }

        var network = _config["network"] as JsonObject ?? [];
        _config["network"] = network;
        network["upnpMappingPolicy"] = (_upnpMappingPolicyComboBox.SelectedValue as string) ?? nameof(UpnpMappingPolicy.Manual);
    }

    private UpnpMappingPolicy ReadConfiguredUpnpPolicy()
    {
        var value = GetNetworkString("upnpMappingPolicy");
        return Enum.TryParse<UpnpMappingPolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)
            ? policy
            : UpnpMappingPolicy.Manual;
    }

    private UpnpMappingPolicy ReadSelectedUpnpPolicy()
    {
        if (_upnpMappingPolicyComboBox == null)
        {
            // Conditional rerenders preserve an unsaved selection in _config before discarding the
            // old control. A transient GetPorts failure can prevent reconstruction, so the in-memory
            // config—not the older on-disk policy—is the authoritative selection in that state.
            return ReadConfiguredUpnpPolicy();
        }

        var value = _upnpMappingPolicyComboBox?.SelectedValue as string;
        return Enum.TryParse<UpnpMappingPolicy>(value, ignoreCase: true, out var policy) && Enum.IsDefined(policy)
            ? policy
            : UpnpMappingPolicy.Manual;
    }

    private bool IsServerRunningForUpnpReconcile()
    {
        if (_module == null)
        {
            return false;
        }

        if (_hasLiveMonitoredProcess?.Invoke(_server.Id) == true)
        {
            return true;
        }

        try
        {
            return ServerProcessLocator.IsRunning(_module, _server.InstallPath);
        }
        catch (Exception ex)
        {
            AppLogService.Add(
                $"Could not determine whether {_server.Name} needs immediate UPnP reconciliation: {ex.GetType().Name}.",
                _server.Id);
            return false;
        }
    }

    private void SaveJavaToConfigObject()
    {
        if (_module?.Capabilities.RequiresJava != true || _javaRuntimeComboBox == null)
        {
            return;
        }

        if (!int.TryParse(_javaInitialMemoryTextBox?.Text, out var initialMemoryMb) ||
            !int.TryParse(_javaMaximumMemoryTextBox?.Text, out var maximumMemoryMb))
        {
            throw new InvalidOperationException("Java memory values must be whole numbers in MB.");
        }

        var javaSettings = new ServerJavaSettings(
            GetSelectedJavaRuntimePath(),
            GetSelectedManagedRuntimeId(),
            _javaMemoryPresetComboBox?.Text.Trim() ?? ServerJavaSettings.CustomPreset,
            initialMemoryMb,
            maximumMemoryMb,
            _javaAdditionalArgumentsTextBox?.Text.Trim() ?? string.Empty);
        JavaRuntimeManager.ValidateMemory(javaSettings);
        _config["java"] = javaSettings.ToJsonObject();

    }

    private void ValidateSelectedJavaRuntime()
    {
        if (_module?.Capabilities.RequiresJava != true || _javaRuntimeComboBox == null)
        {
            return;
        }

        var settings = new ServerJavaSettings(
            GetSelectedJavaRuntimePath(),
            GetSelectedManagedRuntimeId(),
            ServerJavaSettings.Default.MemoryPreset,
            ServerJavaSettings.Default.InitialMemoryMb,
            ServerJavaSettings.Default.MaximumMemoryMb,
            string.Empty);

        if (string.IsNullOrWhiteSpace(settings.RuntimePath) &&
            string.IsNullOrWhiteSpace(settings.ManagedRuntimeId))
        {
            return;
        }

        var validation = new JavaRuntimeManager().Validate(
            settings,
            _module.Capabilities.MinimumJavaMajor ?? 1,
            new ManagedJavaStore());
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }
    }

    private string GetSelectedJavaRuntimePath()
    {
        if (_javaRuntimeComboBox?.SelectedItem is JavaRuntimeChoice { ManagedRuntimeId.Length: > 0 })
        {
            return string.Empty;
        }

        return _javaRuntimeComboBox?.SelectedItem is JavaRuntimeChoice selected &&
            string.Equals(_javaRuntimeComboBox.Text, selected.DisplayText, StringComparison.Ordinal)
            ? selected.ExecutablePath
            : _javaRuntimeComboBox?.Text.Trim() ?? string.Empty;
    }

    private string GetSelectedManagedRuntimeId()
    {
        if (_javaRuntimeComboBox?.SelectedItem is JavaRuntimeChoice { ManagedRuntimeId.Length: > 0 } managedChoice)
        {
            return managedChoice.ManagedRuntimeId;
        }

        return _managedJavaRuntimeComboBox?.SelectedItem is ManagedJavaRuntimeChoice choice
            ? choice.RuntimeId
            : string.Empty;
    }

    private void JavaRuntimeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingJavaRuntimeSelection ||
            _javaRuntimeComboBox?.SelectedItem is not JavaRuntimeChoice selected)
        {
            return;
        }

        _syncingJavaRuntimeSelection = true;
        try
        {
            _managedJavaRuntimeComboBox!.SelectedValue = selected.ManagedRuntimeId;
        }
        finally
        {
            _syncingJavaRuntimeSelection = false;
        }
    }

    private void ManagedJavaRuntimeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingJavaRuntimeSelection ||
            _managedJavaRuntimeComboBox?.SelectedItem is not ManagedJavaRuntimeChoice selected)
        {
            return;
        }

        _syncingJavaRuntimeSelection = true;
        try
        {
            if (string.IsNullOrWhiteSpace(selected.RuntimeId))
            {
                if (_javaRuntimeComboBox?.SelectedItem is JavaRuntimeChoice { ManagedRuntimeId.Length: > 0 })
                {
                    _javaRuntimeComboBox.SelectedItem = null;
                    _javaRuntimeComboBox.Text = string.Empty;
                }

                return;
            }

            var matching = _javaRuntimeComboBox?.Items
                .OfType<JavaRuntimeChoice>()
                .FirstOrDefault(choice =>
                    string.Equals(choice.ManagedRuntimeId, selected.RuntimeId, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
            {
                _javaRuntimeComboBox!.SelectedItem = matching;
            }
        }
        finally
        {
            _syncingJavaRuntimeSelection = false;
        }
    }

    private static JavaRuntimeChoice CreateJavaRuntimeChoice(JavaRuntimeInfo runtime)
    {
        var version = runtime.MajorVersion.HasValue ? $"Java {runtime.MajorVersion}" : "Java ?";
        var vendor = string.IsNullOrWhiteSpace(runtime.Vendor) ? "Unknown vendor" : runtime.Vendor;
        var path = string.IsNullOrWhiteSpace(runtime.ExecutablePath) ? "Unknown path" : runtime.ExecutablePath;
        var status = runtime.Found ? string.Empty : " · unavailable";
        return new JavaRuntimeChoice(path, $"{version} · {vendor} · {path}{status}");
    }

    private static void ValidateRuntimeTuning(JsonObject runtime)
    {
        if (GetBooleanFromObject(runtime, "applyPriorityOnStart", defaultValue: false) &&
            !ProcessTuningService.TryParsePriority(GetStringFromObject(runtime, "processPriorityClass"), out _, out var priorityError))
        {
            throw new InvalidOperationException(priorityError);
        }

        if (GetBooleanFromObject(runtime, "applyAffinityOnStart", defaultValue: false) &&
            !ProcessTuningService.TryParseAffinityMask(GetStringFromObject(runtime, "cpuAffinityMask"), out _, out var affinityError))
        {
            throw new InvalidOperationException(affinityError);
        }
    }

    private void SaveBackupToConfigObject()
    {
        if (_backupOnStartCheckBox == null && _backupPathsTextBox == null && _backupDestinationTextBox == null)
        {
            return;
        }

        var backup = _config["backup"] as JsonObject ?? [];
        _config["backup"] = backup;
        if (_backupOnStartCheckBox != null)
        {
            backup["backupOnStart"] = _backupOnStartCheckBox.IsChecked == true;
        }

        if (_backupPathsTextBox != null)
        {
            var paths = new JsonArray();
            foreach (var path in ReadBackupPathsFromTextBox())
            {
                paths.Add(path);
            }

            backup["paths"] = paths;
        }

        if (_backupDestinationTextBox != null)
        {
            backup["destination"] = _backupDestinationTextBox.Text.Trim();
        }
    }

    private void BrowseBackupDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where this server's backup ZIP files should be stored.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = GetInitialBackupDestination()
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && _backupDestinationTextBox != null)
        {
            _backupDestinationTextBox.Text = dialog.SelectedPath;
        }
    }

    private string GetInitialBackupDestination()
    {
        try
        {
            SaveCurrentValuesToConfigObject();
            return _backupService.ResolveBackupFolder(CreateServerInstance());
        }
        catch
        {
            return Path.Combine(_server.ServerFolder, "backups");
        }
    }

    private void SaveSchedulesToConfigObject(bool validateExternalCommands = false)
    {
        if (_scheduleEnabledControls.Count == 0 && _scheduleCronControls.Count == 0)
        {
            if (_rconScheduleControls.Count == 0 &&
                _consoleScheduleControls.Count == 0 &&
                _apiScheduleControls.Count == 0 &&
                _externalCommandScheduleControls.Count == 0)
            {
                return;
            }
        }

        var schedules = _config["schedules"] as JsonObject ?? [];
        _config["schedules"] = schedules;
        foreach (var key in _scheduleCronControls.Keys)
        {
            var schedule = schedules[key] as JsonObject ?? [];
            schedules[key] = schedule;
            schedule["enabled"] = _scheduleEnabledControls.TryGetValue(key, out var enabled) && enabled.IsChecked == true;
            var cron = _scheduleCronControls[key].Text.Trim();
            ValidateCronIfEnabled(enabled?.IsChecked == true, cron, $"{key} schedule");
            schedule["cron"] = cron;
        }

        if (_rconScheduleRowsPanel != null)
        {
            var rconCommands = new JsonArray();
            foreach (var controls in _rconScheduleControls)
            {
                var command = controls.Command.Text.Trim();
                var cron = controls.Cron.Text.Trim();
                if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(cron))
                {
                    continue;
                }

                ValidateCronIfEnabled(controls.Enabled.IsChecked == true, cron, "Scheduled RCON command");
                rconCommands.Add(new JsonObject
                {
                    ["enabled"] = controls.Enabled.IsChecked == true,
                    ["cron"] = cron,
                    ["command"] = command
                });
            }

            schedules["rconCommands"] = rconCommands;
        }

        if (_consoleScheduleRowsPanel != null)
        {
            var consoleCommands = new JsonArray();
            foreach (var controls in _consoleScheduleControls)
            {
                var command = controls.Command.Text.Trim();
                var cron = controls.Cron.Text.Trim();
                if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(cron))
                {
                    continue;
                }

                ValidateCronIfEnabled(controls.Enabled.IsChecked == true, cron, "Scheduled console command");
                consoleCommands.Add(new JsonObject
                {
                    ["enabled"] = controls.Enabled.IsChecked == true,
                    ["cron"] = cron,
                    ["command"] = command
                });
            }

            schedules["consoleCommands"] = consoleCommands;
        }

        if (_apiScheduleRowsPanel != null)
        {
            var apiActions = new JsonArray();
            foreach (var controls in _apiScheduleControls)
            {
                var cron = controls.Cron.Text.Trim();
                var action = controls.Action.SelectedValue?.ToString() ?? string.Empty;
                var parametersJson = controls.ParametersJson.Text.Trim();
                if (string.IsNullOrWhiteSpace(action) && string.IsNullOrWhiteSpace(cron))
                {
                    continue;
                }

                ValidateCronIfEnabled(controls.Enabled.IsChecked == true, cron, "Scheduled API action");
                JsonObject parameters;
                try
                {
                    parameters = string.IsNullOrWhiteSpace(parametersJson)
                        ? []
                        : JsonNode.Parse(parametersJson)?.AsObject() ?? [];
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Scheduled API action parameters must be a JSON object: {ex.Message}");
                }

                apiActions.Add(new JsonObject
                {
                    ["enabled"] = controls.Enabled.IsChecked == true,
                    ["cron"] = cron,
                    ["action"] = action,
                    ["parameters"] = parameters
                });
            }

            schedules["apiActions"] = apiActions;
        }

        if (_externalCommandScheduleRowsPanel != null)
        {
            var appSettings = AppSettings.Load();
            var externalCommands = new JsonArray();
            foreach (var controls in _externalCommandScheduleControls)
            {
                var executable = controls.Executable.Text.Trim();
                var cron = controls.Cron.Text.Trim();
                if (string.IsNullOrWhiteSpace(executable) && string.IsNullOrWhiteSpace(cron))
                {
                    continue;
                }

                ValidateCronIfEnabled(controls.Enabled.IsChecked == true, cron, "Scheduled external command");
                if (!int.TryParse(controls.TimeoutSeconds.Text, out var timeoutSeconds) ||
                    timeoutSeconds is < 1 or > 86400)
                {
                    throw new InvalidOperationException("Scheduled external command timeout must be between 1 and 86400 seconds.");
                }

                if (validateExternalCommands &&
                    controls.Enabled.IsChecked == true &&
                    appSettings.ExternalScheduledCommandsEnabled)
                {
                    ExternalCommandRunner.ValidateExecutable(executable, appSettings.ExternalCommandAllowedPaths);
                    var workingDirectory = controls.WorkingDirectory.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(Path.GetFullPath(workingDirectory)))
                    {
                        throw new InvalidOperationException($"Scheduled external command working directory does not exist: {workingDirectory}");
                    }
                }

                externalCommands.Add(new JsonObject
                {
                    ["enabled"] = controls.Enabled.IsChecked == true,
                    ["cron"] = cron,
                    ["executable"] = executable,
                    ["arguments"] = controls.Arguments.Text,
                    ["workingDirectory"] = controls.WorkingDirectory.Text.Trim(),
                    ["timeoutSeconds"] = timeoutSeconds
                });
            }

            schedules["externalCommands"] = externalCommands;
        }
    }

    private static void ValidateCronIfEnabled(bool enabled, string cron, string label)
    {
        if (!enabled || string.IsNullOrWhiteSpace(cron))
        {
            return;
        }

        try
        {
            CronSchedule.Parse(cron);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{label} cron is invalid: {ex.Message}");
        }
    }

    private void SaveDiscordToConfigObject(bool persist)
    {
        if (_discordCardChannelTextBox == null &&
            _discordAlertChannelTextBox == null &&
            _discordDashboardChannelTextBox == null &&
            _discordUseWebhookOverrideCheckBox == null)
        {
            return;
        }

        // All four controls above are always created together in AddDiscordTab (there's no
        // partial-creation path), so past the guard above they're guaranteed non-null.
        var cardChannelId = _discordCardChannelTextBox?.Text.Trim() ?? string.Empty;
        var alertChannelId = _discordAlertChannelTextBox?.Text.Trim() ?? string.Empty;
        var dashboardChannelId = _discordDashboardChannelTextBox?.Text.Trim() ?? string.Empty;
        var includeOnDashboard = !string.IsNullOrWhiteSpace(dashboardChannelId);

        var discord = _config["discord"] as JsonObject ?? [];
        _config["discord"] = discord;
        // Keep the legacy JSON discord.channel/includeOnDashboard keys in sync with the new Card
        // Channel/Dashboard Channel values - ServerDiscordSettings (ServerSettingsModels.cs) and
        // any other reader of this JSON model still expect these keys to mean something. Retiring
        // the JSON model itself is a separate concern, outside this chunk's file scope.
        discord["channel"] = cardChannelId;
        discord["includeOnDashboard"] = includeOnDashboard;

        if (_discordUseWebhookOverrideCheckBox != null)
        {
            discord["useWebhookOverride"] = _discordUseWebhookOverrideCheckBox.IsChecked == true;
        }

        foreach (var (key, checkBox) in _discordCardControls)
        {
            discord[key] = checkBox.IsChecked == true;
        }

        if (persist)
        {
            if (_discordWebhookUrlBox != null && !string.IsNullOrWhiteSpace(_discordWebhookUrlBox.Password))
            {
                _discordWebhookStore.SaveForServer(_server.Id, _discordWebhookUrlBox.Password);
                _discordWebhookUrlBox.Password = string.Empty;
            }

            // ChannelName mirrors Card Channel so DiscordBotHost/DiscordPanelService (which still
            // route off ChannelName until Chunk 4/5 land) keep working unchanged through this UI
            // change - from the routing code's perspective, nothing has changed yet.
            _discordRepository.SaveServerSettings(new DiscordServerSettings(
                _server.Id,
                cardChannelId,
                includeOnDashboard,
                string.IsNullOrWhiteSpace(cardChannelId) ? null : cardChannelId,
                string.IsNullOrWhiteSpace(alertChannelId) ? null : alertChannelId,
                string.IsNullOrWhiteSpace(dashboardChannelId) ? null : dashboardChannelId));
        }
    }

    private Dictionary<string, object?> ReadSettingsFromControls()
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _module?.GetConfigFields() ?? [])
        {
            settings[field.Key] = _baseFieldControls.ContainsKey(field.Key)
                ? GetFieldValue(_baseFieldControls, field.Key)
                : GetSettingValue(field.Key, field.DefaultValue);
        }

        return settings;
    }

    private Dictionary<string, object?> ReadSettings(JsonObject settingsObject)
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _module?.GetConfigFields() ?? [])
        {
            settings[field.Key] = GetSettingValue(settingsObject, field.Key, field.DefaultValue);
        }

        return settings;
    }

    private ServerInstance CreateServerInstance()
    {
        var moduleSettings = new ServerModuleSettings(ReadSettings(_config["settings"] as JsonObject ?? []));
        return new ServerInstance(
            _server.Id,
            _server.Name,
            _server.ModuleId,
            _server.ServerFolder,
            _server.InstallPath,
            _server.ConfigPath,
            moduleSettings.Values,
            ServerConfigAppSettings.FromConfigJson(_config.ToJsonString()),
            moduleSettings);
    }

    private static object? GetSettingValue(JsonObject settings, string key, object? fallback)
    {
        return ModuleFieldFormService.GetJsonSettingValue(settings, key, fallback);
    }

    private static object? GetFieldValue(Dictionary<string, FrameworkElement> controls, string key)
    {
        return ModuleFieldFormService.GetControlValue(controls, key);
    }

    private void ServerConfigEditorWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_suppressUnsavedPrompt || !HasUnsavedChanges())
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Changes will not be saved.",
            "Close Config",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            e.Cancel = true;
        }
    }

    private bool HasUnsavedChanges()
    {
        if (_isReadOnly || _savedConfigSnapshot == null)
        {
            return false;
        }

        if (_createFirewallRulesCheckBox?.IsEnabled == true &&
            _createFirewallRulesCheckBox.IsChecked == true)
        {
            return true;
        }

        SaveCurrentValuesToConfigObject();
        return !string.Equals(_savedConfigSnapshot, SerializeConfig(), StringComparison.Ordinal);
    }

    private string SerializeConfig()
    {
        return _config.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed record BackupChoice(string Path, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record JavaRuntimeChoice(
        string ExecutablePath,
        string DisplayText,
        string ManagedRuntimeId = "");

    private sealed record ManagedJavaRuntimeChoice(string RuntimeId, string DisplayText);

    private sealed record UpnpPolicyChoice(string Value, string DisplayText);
}

public sealed record RconScheduleControls(Border Container, WpfCheckBox Enabled, WpfTextBox Cron, WpfTextBox Command);

public sealed record RconScheduleValue(bool Enabled, string Cron, string Command);

public sealed record ConsoleScheduleControls(Border Container, WpfCheckBox Enabled, WpfTextBox Cron, WpfTextBox Command);

public sealed record ConsoleScheduleValue(bool Enabled, string Cron, string Command);

public sealed record ApiScheduleControls(Border Container, WpfCheckBox Enabled, WpfTextBox Cron, WpfComboBox Action, WpfTextBox ParametersJson);

public sealed record ApiScheduleValue(bool Enabled, string Cron, string ActionKey, string ParametersJson);

public sealed record ExternalCommandScheduleControls(
    Border Container,
    WpfCheckBox Enabled,
    WpfTextBox Cron,
    WpfTextBox Executable,
    WpfTextBox Arguments,
    WpfTextBox WorkingDirectory,
    WpfTextBox TimeoutSeconds);

public sealed record ExternalCommandScheduleValue(
    bool Enabled,
    string Cron,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    int TimeoutSeconds);
