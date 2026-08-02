using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using WindowsGSH.Core.Windows;
using WindowsGSH.Data.Security;
using WindowsGSH.Services;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WindowsGSH;

public partial class InstallServerWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int MaxInstallLogCharacters = 120_000;
    private const int MaxLogLinesPerFlush = 150;

    private readonly ModuleRegistry _moduleRegistry = new();
    private readonly SteamCmdManager _steamCmdManager;
    private readonly AppSettings _appSettings = AppSettings.Load();

    private readonly ServerInstallService _installService;
    private readonly WindowsFirewallService _firewallService = new();
    private readonly Dictionary<string, FrameworkElement> _fieldControls = [];
    private readonly Queue<string> _pendingLogLines = [];
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _branchLoadCancellation;
    private bool _isLoadingBranches;
    private bool _renderingFields;
    private bool _conditionalRefreshPending;
    private IReadOnlySet<string> _visibilityControllerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int _branchLoadVersion;
    private string? _renderedModuleId;
    private string? _loggedInstallModeModuleId;
    private bool _modulesLoaded;

    private IGameServerModule? SelectedModule => ModuleComboBox.SelectedItem as IGameServerModule;

    public InstallServerWindow()
    {
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        var credentialStore = new SteamCredentialStore();
        var steamGuardProvider = new SteamGuardCodeProvider(this);
        _steamCmdManager = new SteamCmdManager(credentialStore, steamGuardProvider);
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();
        _installService = new ServerInstallService(
            new PersistentSteamClient(
                credentialStore,
                steamGuardProvider,
                () => _appSettings.PersistentAuthenticatedSteamUpdates),
            ServerOperationManager.Shared);
        ModuleComboBox.IsEnabled = false;
        InstallButton.IsEnabled = false;
        RefreshBranchesButton.IsEnabled = false;
        StatusTextBlock.Text = "Loading modules...";
        AppendLog("Loading modules...");
        ResetBranchComboBox();
        Loaded += InstallServerWindow_Loaded;
    }

    private async void InstallServerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= InstallServerWindow_Loaded;
        await LoadModulesAsync();
    }

    private async Task LoadModulesAsync()
    {
        try
        {
            var modules = await Task.Run(() => _moduleRegistry.GetModules()).ConfigureAwait(true);
            _modulesLoaded = true;
            ModuleComboBox.ItemsSource = modules;
            ModuleComboBox.IsEnabled = modules.Count > 0;
            if (modules.Count > 0)
            {
                ModuleComboBox.SelectedIndex = 0;
            }
            else
            {
                StatusTextBlock.Text = "No modules installed.";
                AppendLog("No modules are installed. Add modules through Module Management before installing a server.");
            }

            ResetBranchComboBox();
            LogSelectedInstallMode();
        }
        catch (Exception ex)
        {
            var path = WriteInstallCrashLog(ex, "Install module list failed to load");
            StatusTextBlock.Text = "Modules failed to load.";
            AppendLog($"ERROR: Modules failed to load. Crash log written: {path}");
            ModuleComboBox.IsEnabled = false;
            InstallButton.IsEnabled = false;
            RefreshBranchesButton.IsEnabled = false;
        }
    }

    private void ModuleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            InvalidateBranchLoad();
            RenderFields();
            ResetBranchComboBox();
            LogSelectedInstallMode();
        }
        catch (Exception ex)
        {
            var moduleName = SelectedModule?.Name ?? "selected module";
            var path = WriteInstallCrashLog(ex, $"Install module selection failed for {moduleName}");
            FieldsPanel.Children.Clear();
            _fieldControls.Clear();
            InstallButton.IsEnabled = false;
            RefreshBranchesButton.IsEnabled = false;
            SteamBranchComboBox.IsEnabled = false;
            StatusTextBlock.Text = "Module failed to load.";
            AppendLog($"ERROR: {moduleName} failed to load. Crash log written: {path}");
        }
    }

    private static string WriteInstallCrashLog(Exception exception, string context)
    {
        return new AppCrashLogService().Write(new AppCrashLogRequest(
            context,
            IsFatal: false,
            Exception: exception,
            RecentAppLog: AppLogService.GetRecentText(160))).Path;
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
        var module = SelectedModule;
        var currentValues = module != null && string.Equals(_renderedModuleId, module.Id, StringComparison.OrdinalIgnoreCase)
            ? _fieldControls.Keys.ToDictionary(key => key, GetFieldValue, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _fieldControls.Clear();
        FieldsPanel.Children.Clear();

        if (module == null)
        {
            _visibilityControllerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _renderedModuleId = null;
            return;
        }

        AddReadOnlyRow("Install folder", GetPreviewInstallPath());

        var fields = module.GetConfigFields().ToArray();
        _visibilityControllerKeys = ModuleFieldFormService.GetVisibilityControllerKeys(fields);
        _renderedModuleId = module.Id;
        string? currentGroup = null;
        foreach (var field in fields.Where(IsFieldVisible))
        {
            currentGroup = AddGroupHeaderIfNeeded(currentGroup, field.Group);
            var fieldToRender = field;
            if (currentValues.TryGetValue(field.Key, out var currentValue))
            {
                fieldToRender = field with { DefaultValue = currentValue };
            }

            AddField(fieldToRender);
        }

        var steamInstall = module.GetSteamInstall();
        if (steamInstall != null)
        {
            AddPasswordRow("Steam branch password", "steam.branchPassword", string.Empty);
            if (!string.IsNullOrWhiteSpace(steamInstall.CustomArguments))
            {
                AddCustomArgumentsPreviewRow(steamInstall.CustomArguments);
            }
        }
        }
        finally
        {
            _renderingFields = false;
        }
    }

    private void InvalidateBranchLoad()
    {
        _branchLoadVersion++;
        _branchLoadCancellation = null;
    }

    private void ResetBranchComboBox()
    {
        _isLoadingBranches = false;
        var selectedModule = SelectedModule;
        var usesSteam = selectedModule?.GetSteamInstall() != null;
        InstallButton.IsEnabled = _modulesLoaded && CanInstall(selectedModule);
        SteamBranchLabel.Visibility = usesSteam ? Visibility.Visible : Visibility.Collapsed;
        SteamBranchPanel.Visibility = usesSteam ? Visibility.Visible : Visibility.Collapsed;
        SteamBranchComboBox.Items.Clear();
        SteamBranchComboBox.Items.Add("public");
        SteamBranchComboBox.SelectedIndex = 0;
        SteamBranchComboBox.IsEnabled = usesSteam;
        RefreshBranchesButton.IsEnabled = _modulesLoaded && usesSteam;
        InstallDescriptionTextBlock.Text = GetInstallDescription(selectedModule);

        if (!_modulesLoaded)
        {
            StatusTextBlock.Text = "Loading modules...";
        }
        else if (selectedModule == null)
        {
            StatusTextBlock.Text = "No modules installed.";
        }
        else if (!CanInstall(selectedModule))
        {
            StatusTextBlock.Text = "Selected module does not define an install or update path.";
        }
        else if (string.IsNullOrWhiteSpace(StatusTextBlock.Text) ||
                 StatusTextBlock.Text is "No modules installed." or "Selected module does not define a SteamCMD install." or "Selected module does not define an install or update path.")
        {
            StatusTextBlock.Text = string.Empty;
        }
    }

    private static bool CanInstall(IGameServerModule? module)
    {
        return module != null && (module.GetSteamInstall() != null || module is IModuleUpdateCapability);
    }

    private static string GetInstallDescription(IGameServerModule? module)
    {
        if (module?.GetSteamInstall() != null)
        {
            return "Choose a module, review its fields, then install through SteamCMD.";
        }

        if (module is IModuleUpdateCapability)
        {
            return "Choose a module, review its fields, then install through the module downloader.";
        }

        return "Choose a module, review its fields, then install.";
    }

    private void LogSelectedInstallMode()
    {
        var module = SelectedModule;
        if (module == null || string.Equals(_loggedInstallModeModuleId, module.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loggedInstallModeModuleId = module.Id;
        if (module.GetSteamInstall() != null)
        {
            AppendLog("SteamCMD will be downloaded automatically into the application folder on first use.");
        }
        else if (module is IModuleUpdateCapability)
        {
            AppendLog($"{module.Name} installs through its module updater. SteamCMD is not used.");
        }
        else
        {
            AppendLog($"{module.Name} does not define an install or update path.");
        }
    }

    private void RefreshBranchesButton_Click(object sender, RoutedEventArgs e)
    {
        InvalidateBranchLoad();
        var module = SelectedModule;
        var version = ++_branchLoadVersion;
        var cancellation = new CancellationTokenSource();
        _branchLoadCancellation = cancellation;
        _ = LoadBranchesAsync(module, version, cancellation);
    }

    private async Task LoadBranchesAsync(IGameServerModule? module, int version, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        _isLoadingBranches = true;
        InstallButton.IsEnabled = false;
        RefreshBranchesButton.IsEnabled = false;
        SteamBranchComboBox.Items.Clear();
        SteamBranchComboBox.Items.Add("Loading...");
        SteamBranchComboBox.SelectedIndex = 0;
        SteamBranchComboBox.IsEnabled = false;

        var steamInstall = module?.GetSteamInstall();
        if (module == null || steamInstall == null)
        {
            if (IsCurrentBranchLoad(version, cancellationToken))
            {
                _isLoadingBranches = false;
                ResetBranchComboBox();
            }

            return;
        }

        try
        {
            if (!steamInstall.LoginAnonymous)
            {
                AppendLog("Authenticated Steam branch discovery is limited to the public branch. Enter a private branch manually if required.");
                SteamBranchComboBox.Items.Clear();
                SteamBranchComboBox.Items.Add("public");
                SteamBranchComboBox.SelectedIndex = 0;
                SteamBranchComboBox.IsEnabled = true;
                return;
            }

            AppendLog($"Refreshing Steam branches for {module.Name}...");
            var branches = await _steamCmdManager.GetBranchesAsync(steamInstall, CreateProgress(), cancellationToken, forceRefresh: true);
            if (!IsCurrentBranchLoad(version, cancellationToken))
            {
                return;
            }

            SteamBranchComboBox.Items.Clear();
            foreach (var branch in branches)
            {
                SteamBranchComboBox.Items.Add(branch);
            }

            SteamBranchComboBox.SelectedItem = branches.Contains("public") ? "public" : branches.FirstOrDefault();
            SteamBranchComboBox.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentBranchLoad(version, cancellationToken))
            {
                return;
            }

            AppendLog("Could not refresh Steam branches. You can still enter a branch manually.");
            AppendLog(ex.Message);
            SteamBranchComboBox.Items.Clear();
            SteamBranchComboBox.Items.Add("public");
            SteamBranchComboBox.SelectedIndex = 0;
            SteamBranchComboBox.IsEnabled = true;
            RefreshBranchesButton.IsEnabled = module?.GetSteamInstall() != null;
        }
        finally
        {
            if (IsCurrentBranchLoad(version, cancellationToken))
            {
                _isLoadingBranches = false;
                InstallButton.IsEnabled = CanInstall(SelectedModule);
                RefreshBranchesButton.IsEnabled = module?.GetSteamInstall()?.LoginAnonymous == true;
            }

            cancellation.Dispose();
        }
    }

    private bool IsCurrentBranchLoad(int version, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
            version == _branchLoadVersion &&
            _branchLoadCancellation?.Token.Equals(cancellationToken) == true;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        StatusTextBlock.Text = "Installing...";

        try
        {
            await InstallAsync();
            StatusTextBlock.Text = "Install complete.";
            AppendLog("Install complete.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Install failed.";
            AppendLog("ERROR: " + ex.Message);
        }
        finally
        {
            InstallButton.IsEnabled = CanInstall(SelectedModule);
        }
    }

    private async Task InstallAsync()
    {
        var module = SelectedModule ?? throw new InvalidOperationException("Select a module.");
        var usesSteam = module.GetSteamInstall() != null;
        if (usesSteam && _isLoadingBranches)
        {
            throw new InvalidOperationException("Wait for Steam branches to finish loading before installing.");
        }

        var settings = ReadSettings(module);
        ModuleFieldFormService.ValidateSettings(module.GetConfigFields(), settings);
        var serverName = module.GetServerName(settings);
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new InvalidOperationException("Server Name is required.");
        }

        var serverId = GetNextServerId();
        var installPath = GetInstallPath(module, serverId);
        var serverFolder = Directory.GetParent(installPath)?.FullName ?? installPath;
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        var instance = new ServerInstance(serverId, serverName, module.Id, serverFolder, installPath, configPath, settings);
        ValidatePortAvailability(module, instance);

        if (usesSteam)
        {
            var branch = NormalizeSteamBranch(SteamBranchComboBox.Text);
            var branchPassword = GetFieldValue("steam.branchPassword")?.ToString() ?? string.Empty;
            var lastSteamOutputUtc = DateTimeOffset.UtcNow;
            using var heartbeatCancellation = new CancellationTokenSource();
            var heartbeatTask = ReportSteamCmdHeartbeatAsync(() => lastSteamOutputUtc, heartbeatCancellation.Token);
            ServerInstallResult result;
            try
            {
                result = await _installService.InstallAsync(
                    new ServerInstallRequest(
                        module,
                        instance,
                        branch,
                        branchPassword,
                        Progress: CreateProgress(_ => lastSteamOutputUtc = DateTimeOffset.UtcNow)));
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.LastError ?? result.Message);
                }
            }
            finally
            {
                heartbeatCancellation.Cancel();
                try
                {
                    await heartbeatTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        else if (module is IModuleUpdateCapability)
        {
            ServerInstallResult result;
            try
            {
                result = await _installService.InstallAsync(
                    new ServerInstallRequest(
                        module,
                        instance,
                        string.Empty,
                        string.Empty,
                        Progress: CreateProgress()));
            }
            catch (Exception ex)
            {
                AppendLog($"Module update failed. The server config remains at {configPath}; fix the settings and run Update from the installed server.");
                throw new InvalidOperationException($"{module.Name} module update failed: {FormatException(ex)}", ex);
            }

            if (!result.Success)
            {
                AppendLog($"Module update failed. The server config remains at {configPath}; fix the settings and run Update from the installed server.");
                throw new InvalidOperationException($"{module.Name} module update failed: {result.LastError ?? result.Message}");
            }
        }
        else
        {
            throw new InvalidOperationException($"{module.Name} does not define an install or update path.");
        }

        CreateFirewallRulesIfRequested(module, instance);
    }

    private void CreateFirewallRulesIfRequested(IGameServerModule module, ServerInstance instance)
    {
        if (CreateFirewallRulesCheckBox.IsChecked != true)
        {
            return;
        }

        if (!IsRunningAsAdministrator())
        {
            AppendLog("WARNING: Windows Firewall rules require administrator rights. Restart WindowsGSH as administrator and create the rules from install.");
            return;
        }

        try
        {
            var result = _firewallService.CreateOrUpdateRules(new FirewallRuleApplyRequest(instance, module));
            foreach (var failed in result.Steps.Where(step => !step.Success))
            {
                AppendLog($"WARNING: {failed.Message} {failed.Error}");
            }

            AppendLog(result.CreatedRules.Count == 0
                ? "No firewall rules needed to be created."
                : $"Created {result.CreatedRules.Count} Windows Firewall rule(s).");
        }
        catch (Exception ex)
        {
            AppendLog("WARNING: Could not create Windows Firewall rules: " + FormatException(ex));
        }
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

    private async Task ReportSteamCmdHeartbeatAsync(Func<DateTimeOffset> getLastOutputUtc, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            var silence = DateTimeOffset.UtcNow - getLastOutputUtc();
            if (silence >= TimeSpan.FromSeconds(15))
            {
                AppendLog($"SteamCMD still running... no output for {Math.Round(silence.TotalSeconds)}s.");
            }
        }
    }

    private static string NormalizeSteamBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            string.Equals(branch.Trim(), "Loading...", StringComparison.OrdinalIgnoreCase))
        {
            return "public";
        }

        return branch.Trim();
    }

    private Dictionary<string, object?> ReadSettings(IGameServerModule module)
    {
        var settings = new Dictionary<string, object?>();
        foreach (var field in module.GetConfigFields())
        {
            settings[field.Key] = _fieldControls.ContainsKey(field.Key)
                ? GetFieldValue(field.Key)
                : field.DefaultValue;
        }

        return settings;
    }

    private object? GetFieldValue(string key) => ModuleFieldFormService.GetControlValue(_fieldControls, key);

    private void AddField(ConfigFieldDefinition field)
    {
        switch (field.Type)
        {
            case ConfigFieldType.Boolean:
                AddCheckBoxRow(field);
                break;
            case ConfigFieldType.Password:
                AddPasswordRow(field.Label, field.Key, field.DefaultValue?.ToString() ?? string.Empty);
                break;
            case ConfigFieldType.Select:
                AddComboRow(field);
                break;
            default:
                AddTextRow(field);
                break;
        }
    }

    private void AddTextRow(ConfigFieldDefinition field)
    {
        var textBox = new WpfTextBox
        {
            Height = 32,
            Text = field.DefaultValue?.ToString() ?? string.Empty,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = (System.Windows.Media.Brush)FindResource("PanelAltBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft")
        };
        if (ControlsFieldVisibility(field.Key))
        {
            textBox.TextChanged += (_, _) => RefreshConditionalFields();
        }

        _fieldControls[field.Key] = textBox;
        AddRow(GetFieldLabel(field), textBox, field.Description);
    }

    private void AddPasswordRow(string label, string key, string defaultValue)
    {
        var passwordBox = new PasswordBox
        {
            Height = 32,
            Password = defaultValue,
            Background = (System.Windows.Media.Brush)FindResource("PanelAltBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft")
        };
        _fieldControls[key] = passwordBox;
        AddRow(label, passwordBox, null);
    }

    private void AddComboRow(ConfigFieldDefinition field)
    {
        var comboBox = new WpfComboBox
        {
            Height = 32,
            IsEditable = true,
            Background = (System.Windows.Media.Brush)FindResource("PanelAltBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft")
        };

        foreach (var option in field.Options ?? [])
        {
            comboBox.Items.Add(option);
        }

        var defaultText = field.DefaultValue?.ToString() ?? string.Empty;
        comboBox.SelectedItem = comboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), defaultText, StringComparison.OrdinalIgnoreCase));
        if (comboBox.SelectedItem == null)
        {
            comboBox.Text = defaultText;
        }

        if (ControlsFieldVisibility(field.Key))
        {
            comboBox.SelectionChanged += (_, _) => RefreshConditionalFields();
            comboBox.AddHandler(
                System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, _) => RefreshConditionalFields()));
        }

        _fieldControls[field.Key] = comboBox;
        AddRow(GetFieldLabel(field), comboBox, field.Description);
    }

    private void AddCheckBoxRow(ConfigFieldDefinition field)
    {
        var checkBox = new WpfCheckBox
        {
            IsChecked = field.DefaultValue is bool value && value,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("AppCheckBox")
        };
        if (ControlsFieldVisibility(field.Key))
        {
            checkBox.Checked += (_, _) => RefreshConditionalFields();
            checkBox.Unchecked += (_, _) => RefreshConditionalFields();
        }

        _fieldControls[field.Key] = checkBox;
        AddRow(GetFieldLabel(field), checkBox, field.Description);
    }

    private bool ControlsFieldVisibility(string key) => _visibilityControllerKeys.Contains(key);

    private void RefreshConditionalFields()
    {
        if (_renderingFields || _conditionalRefreshPending)
        {
            return;
        }

        _conditionalRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _conditionalRefreshPending = false;
            RenderFields();
        });
    }

    private string? AddGroupHeaderIfNeeded(string? currentGroup, string? group)
    {
        if (string.IsNullOrWhiteSpace(group) || string.Equals(currentGroup, group, StringComparison.OrdinalIgnoreCase))
        {
            return currentGroup;
        }

        var header = new TextBlock
        {
            Text = group,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 8)
        };
        FieldsPanel.Children.Add(header);
        return group;
    }

    private bool IsFieldVisible(ConfigFieldDefinition field)
    {
        return ModuleFieldFormService.IsFieldVisible(field, GetFieldValue);
    }

    private static string GetFieldLabel(ConfigFieldDefinition field)
    {
        return field.RestartRequired ? $"{field.Label} (restart)" : field.Label;
    }

    // P3-06: SteamCmdPolicy.BuildInstallArguments splices this verbatim into the SteamCMD command
    // line for both the initial install and every subsequent update - shown here so it's reviewed
    // before the user commits to installing this module for the first time. This is NOT the only
    // place it's surfaced: ServerInfoWindow shows the same value persistently for the life of the
    // server, since updates afterward can be triggered manually, on a schedule, from Discord, or
    // from the web UI, none of which come back through this install-time preview.
    private void AddCustomArgumentsPreviewRow(string customArguments)
    {
        var textBlock = new TextBlock
        {
            Text = customArguments,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = (System.Windows.Media.Brush)FindResource("WarnMetricText")
        };
        AddRow(
            "SteamCMD custom arguments",
            textBlock,
            "Appended verbatim to the SteamCMD command line on every install and update, with no escaping or validation - repeated on each of the 4 app_update calls SteamCMD makes for App ID 90. Only continue if you trust where this module came from.");
    }

    private void AddReadOnlyRow(string label, string value)
    {
        var textBlock = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        AddRow(label, textBlock, "Servers are installed below the application folder.");
    }

    private void AddRow(string label, FrameworkElement control, string? description)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var stack = new StackPanel();
        stack.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        FieldsPanel.Children.Add(grid);
    }

    private void ValidatePortAvailability(IGameServerModule module, ServerInstance candidate)
    {
        var candidatePorts = GetConfiguredPorts(module, candidate).ToArray();
        if (candidatePorts.Length == 0)
        {
            return;
        }

        var serversPath = AppPaths.GetPath("servers");
        if (!Directory.Exists(serversPath))
        {
            return;
        }

        foreach (var serverFolder in Directory.EnumerateDirectories(serversPath))
        {
            var configPath = Path.Combine(serverFolder, "ServerConfig.json");
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                var existingModuleId = ReadJsonString(root, "moduleId", "");
                var existingModule = _moduleRegistry.GetModules()
                    .FirstOrDefault(item => string.Equals(item.Id, existingModuleId, StringComparison.OrdinalIgnoreCase));
                if (existingModule == null)
                {
                    continue;
                }

                var settings = root.TryGetProperty("settings", out var settingsElement) && settingsElement.ValueKind == JsonValueKind.Object
                    ? ReadJsonSettings(settingsElement)
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var existingId = ReadJsonString(root, "id", Path.GetFileName(serverFolder));
                var existingName = ReadJsonString(root, "name", $"Server {existingId}");
                var relativeInstallPath = ReadJsonString(root, "installPath", "files");
                var installPath = Path.GetFullPath(Path.Combine(serverFolder, relativeInstallPath));
                var existing = new ServerInstance(existingId, existingName, existingModuleId, serverFolder, installPath, configPath, settings);
                var existingPorts = GetConfiguredPorts(existingModule, existing).ToArray();
                foreach (var candidatePort in candidatePorts)
                {
                    var conflict = existingPorts.FirstOrDefault(existingPort => existingPort.Port == candidatePort.Port);
                    if (conflict != null)
                    {
                        throw new InvalidOperationException(
                            $"{candidatePort.Label} {candidatePort.Port} is already used by {existingName} ({existingModule.Name}) as {conflict.Label}. Choose a different port before installing.");
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static IReadOnlyList<ConfiguredPort> GetConfiguredPorts(IGameServerModule module, ServerInstance instance)
    {
        return module.GetConfigFields()
            .Where(field => field.Type == ConfigFieldType.Port)
            .Select(field => TryCreateConfiguredPort(field, instance.Settings))
            .OfType<ConfiguredPort>()
            .GroupBy(port => port.Port)
            .Select(group => group.First())
            .ToArray();
    }

    private static ConfiguredPort? TryCreateConfiguredPort(ConfigFieldDefinition field, IReadOnlyDictionary<string, object?> settings)
    {
        if (!settings.TryGetValue(field.Key, out var value) || !TryParsePort(value?.ToString(), out var port))
        {
            return null;
        }

        return new ConfiguredPort(field.Key, field.Label, port);
    }

    private static bool TryParsePort(string? value, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var separatorIndex = text.LastIndexOf(':');
        if (separatorIndex >= 0)
        {
            text = text[(separatorIndex + 1)..];
        }

        return int.TryParse(text, out port) && port is >= 1 and <= 65535;
    }

    private sealed record ConfiguredPort(string Key, string Label, int Port);

    private static string ReadJsonString(JsonElement element, string propertyName, string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static Dictionary<string, object?> ReadJsonSettings(JsonElement settingsElement)
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in settingsElement.EnumerateObject())
        {
            settings[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when property.Value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when property.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => property.Value.ToString()
            };
        }

        return settings;
    }

    private static string GetPreviewInstallPath()
    {
        return AppPaths.GetPath("servers", GetNextServerId(), "files");
    }

    private static string GetInstallPath(IGameServerModule module, string serverId)
    {
        return AppPaths.GetPath("servers", serverId, "files");
    }

    private static string GetNextServerId()
    {
        var serversPath = AppPaths.GetPath("servers");
        Directory.CreateDirectory(serversPath);

        var usedIds = Directory.EnumerateDirectories(serversPath)
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        var nextId = 1;
        while (usedIds.Contains(nextId))
        {
            nextId++;
        }

        return nextId.ToString();
    }

    private Progress<string> CreateProgress(Action<string>? afterLog = null)
    {
        return new Progress<string>(text =>
        {
            afterLog?.Invoke(text);
            QueueLog(text);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AppendLog(string text)
    {
        AppendLogLine(text);
    }

    private void QueueLog(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => QueueLog(text)));
            return;
        }

        _pendingLogLines.Enqueue(text);
        if (!_logFlushTimer.IsEnabled)
        {
            _logFlushTimer.Start();
        }
    }

    private void FlushPendingLogLines()
    {
        var count = 0;
        while (_pendingLogLines.Count > 0 && count < MaxLogLinesPerFlush)
        {
            AppendLogLine(_pendingLogLines.Dequeue());
            count++;
        }

        if (_pendingLogLines.Count == 0)
        {
            _logFlushTimer.Stop();
        }
    }

    private void AppendLogLine(string text)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        TrimLogTextBox();
        LogTextBox.ScrollToEnd();
    }

    private void TrimLogTextBox()
    {
        if (LogTextBox.Text.Length <= MaxInstallLogCharacters)
        {
            return;
        }

        var removeCount = LogTextBox.Text.Length - MaxInstallLogCharacters;
        var newlineIndex = LogTextBox.Text.IndexOf(Environment.NewLine, removeCount, StringComparison.Ordinal);
        if (newlineIndex >= 0)
        {
            removeCount = newlineIndex + Environment.NewLine.Length;
        }

        LogTextBox.Text = LogTextBox.Text[removeCount..];
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }

    private static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();

        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private void ContentGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || WpfInteractionHelper.IsInteractiveElement(e.OriginalSource as System.Windows.DependencyObject))
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

}
