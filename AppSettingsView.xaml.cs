using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Network;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Data;
using WindowsGSH.Data.Security;
using WindowsGSH.Discord;
using WindowsGSH.Services;

namespace WindowsGSH;

public partial class AppSettingsView : System.Windows.Controls.UserControl
{
    private readonly List<JavaRuntimeRow> _javaRuntimeRows = [];
    private readonly ManagedJavaStore _managedJavaStore = new();
    private readonly List<ManagedJavaRuntimeRow> _managedJavaRows = [];
    private CancellationTokenSource? _javaDiscoveryCancellation;
    private CancellationTokenSource? _managedJavaCancellation;
    private AppSettings? _settings;
    private SteamCredentialStore? _steamCredentialStore;
    private readonly DepotDownloaderSessionStore _depotDownloaderSessionStore =
        new(new DepotDownloaderToolManager().AccountConfigPath);
    private DiscordBotTokenStore? _discordTokenStore;
    private DiscordWebhookStore? _discordWebhookStore;
    private DiscordRepository? _discordRepository;
    private DiscordBotHost? _discordBotHost;
    private Func<bool, Task>? _startDiscordBot;
    private Func<Task>? _stopDiscordBot;
    private Func<Task>? _testDiscordWebhook;
    private Action? _showReadiness;
    private Action? _showOperationHistory;
    private Action<string>? _log;
    private Func<bool, string?>? _updateStartupRegistration;
    private Action? _desktopSettingsChanged;
    private Func<Task<IReadOnlyList<ServerHealthReport>>>? _getServerHealthReports;
    private InstalledServerLoader? _installedServerLoader;

    public AppSettingsView()
    {
        InitializeComponent();
    }

    public void Configure(
        AppSettings settings,
        SteamCredentialStore steamCredentialStore,
        DiscordBotTokenStore discordTokenStore,
        DiscordWebhookStore discordWebhookStore,
        DiscordRepository discordRepository,
        DiscordBotHost discordBotHost,
        Func<bool, Task> startDiscordBot,
        Func<Task> stopDiscordBot,
        Func<Task> testDiscordWebhook,
        Action showReadiness,
        Action showOperationHistory,
        Action<string> log,
        Func<bool, string?> updateStartupRegistration,
        Action desktopSettingsChanged)
    {
        Configure(
            settings,
            steamCredentialStore,
            discordTokenStore,
            discordWebhookStore,
            discordRepository,
            discordBotHost,
            startDiscordBot,
            stopDiscordBot,
            testDiscordWebhook,
            showReadiness,
            showOperationHistory,
            log,
            updateStartupRegistration,
            desktopSettingsChanged,
            new InstalledServerLoader(),
            () => Task.FromResult<IReadOnlyList<ServerHealthReport>>([]));
    }

    public void Configure(
        AppSettings settings,
        SteamCredentialStore steamCredentialStore,
        DiscordBotTokenStore discordTokenStore,
        DiscordWebhookStore discordWebhookStore,
        DiscordRepository discordRepository,
        DiscordBotHost discordBotHost,
        Func<bool, Task> startDiscordBot,
        Func<Task> stopDiscordBot,
        Func<Task> testDiscordWebhook,
        Action showReadiness,
        Action showOperationHistory,
        Action<string> log,
        Func<bool, string?> updateStartupRegistration,
        Action desktopSettingsChanged,
        Func<Task<IReadOnlyList<ServerHealthReport>>> getServerHealthReports)
    {
        Configure(
            settings,
            steamCredentialStore,
            discordTokenStore,
            discordWebhookStore,
            discordRepository,
            discordBotHost,
            startDiscordBot,
            stopDiscordBot,
            testDiscordWebhook,
            showReadiness,
            showOperationHistory,
            log,
            updateStartupRegistration,
            desktopSettingsChanged,
            new InstalledServerLoader(),
            getServerHealthReports);
    }

    public void Configure(
        AppSettings settings,
        SteamCredentialStore steamCredentialStore,
        DiscordBotTokenStore discordTokenStore,
        DiscordWebhookStore discordWebhookStore,
        DiscordRepository discordRepository,
        DiscordBotHost discordBotHost,
        Func<bool, Task> startDiscordBot,
        Func<Task> stopDiscordBot,
        Func<Task> testDiscordWebhook,
        Action showReadiness,
        Action showOperationHistory,
        Action<string> log,
        Func<bool, string?> updateStartupRegistration,
        Action desktopSettingsChanged,
        InstalledServerLoader installedServerLoader)
    {
        Configure(
            settings,
            steamCredentialStore,
            discordTokenStore,
            discordWebhookStore,
            discordRepository,
            discordBotHost,
            startDiscordBot,
            stopDiscordBot,
            testDiscordWebhook,
            showReadiness,
            showOperationHistory,
            log,
            updateStartupRegistration,
            desktopSettingsChanged,
            installedServerLoader,
            () => Task.FromResult<IReadOnlyList<ServerHealthReport>>([]));
    }

    public void Configure(
        AppSettings settings,
        SteamCredentialStore steamCredentialStore,
        DiscordBotTokenStore discordTokenStore,
        DiscordWebhookStore discordWebhookStore,
        DiscordRepository discordRepository,
        DiscordBotHost discordBotHost,
        Func<bool, Task> startDiscordBot,
        Func<Task> stopDiscordBot,
        Func<Task> testDiscordWebhook,
        Action showReadiness,
        Action showOperationHistory,
        Action<string> log,
        Func<bool, string?> updateStartupRegistration,
        Action desktopSettingsChanged,
        InstalledServerLoader installedServerLoader,
        Func<Task<IReadOnlyList<ServerHealthReport>>> getServerHealthReports)
    {
        _settings = settings;
        _steamCredentialStore = steamCredentialStore;
        _discordTokenStore = discordTokenStore;
        _discordWebhookStore = discordWebhookStore;
        _discordRepository = discordRepository;
        if (_discordBotHost != null)
        {
            _discordBotHost.StateChanged -= DiscordBotHost_StateChanged;
        }
        _discordBotHost = discordBotHost;
        _discordBotHost.StateChanged += DiscordBotHost_StateChanged;
        _startDiscordBot = startDiscordBot;
        _stopDiscordBot = stopDiscordBot;
        _testDiscordWebhook = testDiscordWebhook;
        _showReadiness = showReadiness;
        _showOperationHistory = showOperationHistory;
        _log = log;
        _updateStartupRegistration = updateStartupRegistration;
        _desktopSettingsChanged = desktopSettingsChanged;
        _getServerHealthReports = getServerHealthReports;
        _installedServerLoader = installedServerLoader;
        LoadControls();
    }

    public void LoadControls()
    {
        var settings = RequireSettings();
        ParallelServerOperationsCheckBox.IsChecked = settings.ParallelServerOperations;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        RestartAppOnCrashCheckBox.IsChecked = settings.RestartAppOnCrash;
        AppCrashRestartMaxAttemptsTextBox.Text = Math.Max(1, settings.AppCrashRestartMaxAttempts).ToString();
        AppCrashRestartWindowTextBox.Text = Math.Max(1, settings.AppCrashRestartWindowMinutes).ToString();
        BackupRetentionTextBox.Text = settings.BackupRetentionCount.ToString();
        AutoUpdateIntervalTextBox.Text = settings.AutoUpdateIntervalMinutes.ToString();
        PersistentAuthenticatedSteamUpdatesCheckBox.IsChecked = settings.PersistentAuthenticatedSteamUpdates;
        PublicIpTrackingEnabledCheckBox.IsChecked = settings.PublicIpTrackingEnabled;
        PublicIpEndpointTextBox.Text = string.IsNullOrWhiteSpace(settings.PublicIpEndpoint) ? "https://api.ipify.org" : settings.PublicIpEndpoint;
        PublicIpIntervalTextBox.Text = Math.Max(1, settings.PublicIpCheckIntervalMinutes).ToString();
        ExternalReachabilityChecksEnabledCheckBox.IsChecked = settings.ExternalReachabilityChecksEnabled;
        DiscordBotEnabledCheckBox.IsChecked = settings.DiscordBotEnabled;
        DiscordBotPrefixTextBox.Text = string.IsNullOrWhiteSpace(settings.DiscordBotPrefix) ? "!" : settings.DiscordBotPrefix;
        DiscordAllowDestructiveCommandsCheckBox.IsChecked = settings.DiscordAllowDestructiveCommands;
        DiscordNotificationsChannelTextBox.Text = settings.DiscordNotificationsChannelId;
        DiscordBotTokenBox.Password = string.Empty;
        DiscordWebhookEnabledCheckBox.IsChecked = settings.DiscordWebhookEnabled;
        DiscordWebhookUrlBox.Password = string.Empty;
        ExternalScheduledCommandsEnabledCheckBox.IsChecked = settings.ExternalScheduledCommandsEnabled;
        ExternalCommandAllowedPathsTextBox.Text = string.Join(Environment.NewLine, settings.ExternalCommandAllowedPaths);
        ReducedMotionCheckBox.IsChecked = settings.ReducedMotion;
        SoftwareRenderingCheckBox.IsChecked = settings.SoftwareRendering;
        RuntimeDiagnosticsEnabledCheckBox.IsChecked = settings.RuntimeDiagnosticsEnabled;
        CurrentRenderingModeTextBlock.Text = GetCurrentRenderingModeText();
        WebEnabledCheckBox.IsChecked = settings.WebEnabled;
        WebPortTextBox.Text = settings.WebPort.ToString();
        WebBindAddressTextBox.Text = settings.WebBindAddress;
        WebTrustForwardedHeadersCheckBox.IsChecked = settings.WebTrustForwardedHeaders;
        AllowLegacyWebSocketQueryStringAuthCheckBox.IsChecked = settings.AllowLegacyWebSocketQueryStringAuth;
        UpdateWebServerStatusText(settings.WebEnabled);
        UpdateWebBindAddressWarning(settings.WebBindAddress);
        _ = RefreshWebUsersListAsync();
        LoadJavaRuntimeRowsWithoutProbing(settings.KnownJavaRuntimePaths);
        RefreshManagedJavaRows();
        VersionInfoTextBlock.Text = $"WindowsGSH {AppVersionInfo.DisplayVersion}  •  Module API {ModuleCompatibility.CurrentModuleApiVersion}";
        DiscordAdminIdsTextBox.Text = string.Join(Environment.NewLine, RequireDiscordRepository().GetAdmins().Select(admin => $"{admin.DiscordUserId} {admin.ServerIds}"));
        DiscordBotHintTextBlock.Text = RequireDiscordTokenStore().HasToken
            ? "A Discord bot token is saved for this Windows user. Leave token blank unless changing it."
            : "Create a Discord app/bot, paste the bot token here, then add Discord user IDs as admins.";
        DiscordWebhookHintTextBlock.Text = RequireDiscordWebhookStore().HasGlobalWebhook
            ? "A global webhook is saved with Windows user encryption. Leave the URL blank unless changing it."
            : "Optional. Create a Discord channel webhook and paste its URL here. Server CFGs can override this destination.";
        LoadSteamCredentialControls();
        UpdateDiscordState(_discordBotHost!.State);
        SetStatus(string.Empty);
    }

    public void SetStatus(string message)
    {
        ConfigStatusTextBlock.Text = message;
    }

    public void ShowDiscordSettings()
    {
        SettingsTabControl.SelectedItem = DiscordTabItem;
        if (_discordBotHost != null)
        {
            UpdateDiscordState(_discordBotHost.State);
        }
    }

    public void ShowJavaSettings()
    {
        SettingsTabControl.SelectedItem = JavaTabItem;
    }

    private void DiscordBotHost_StateChanged(object? sender, DiscordHostStateSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(new Action(() => UpdateDiscordState(snapshot)));
    }

    private void UpdateDiscordState(DiscordHostStateSnapshot snapshot)
    {
        DiscordConnectionStateTextBlock.Text = snapshot.State.ToString();
        DiscordConnectionIdentityTextBlock.Text = snapshot.ApplicationId.HasValue
            ? $"{snapshot.BotUsername ?? "Discord bot"} · Application ID {snapshot.ApplicationId}"
            : snapshot.BotUsername ?? "Bot identity is not available.";
        DiscordConnectionDetailTextBlock.Text = snapshot.LastFailure == null
            ? $"Changed {snapshot.ChangedAt.ToLocalTime():g}"
            : $"{snapshot.LastFailure} · {snapshot.ChangedAt.ToLocalTime():g}";
        var inviteLink = DiscordInviteLink.Create(snapshot.ApplicationId);
        DiscordInviteLinkTextBox.Text = inviteLink ?? string.Empty;
        CopyDiscordInviteButton.IsEnabled = inviteLink != null;
        OpenDiscordInviteButton.IsEnabled = inviteLink != null;
        StartDiscordBotButton.IsEnabled = snapshot.CanStart && !snapshot.IsTransitional;
        StopDiscordBotButton.IsEnabled = snapshot.CanStop && snapshot.State != DiscordHostState.Stopping;
    }

    private void LoadSteamCredentialControls()
    {
        var store = RequireSteamCredentialStore();
        SteamUsernameTextBox.Text = store.LoadUsername() ?? string.Empty;
        SteamPasswordBox.Password = string.Empty;
        SteamCredentialHintTextBlock.Text = store.HasCredentials
            ? "A Steam login is saved for this Windows user. Leave password blank unless changing it."
            : "Optional. Saved locally with Windows user encryption and used instead of anonymous SteamCMD login.";
        PersistentSteamSessionStatusTextBlock.Text = _depotDownloaderSessionStore.HasProtectedSession
            ? "A reusable Steam session is saved with Windows user encryption."
            : "No reusable Steam session is saved yet.";
    }

    private async void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveConfig())
        {
            await StartDiscordBotAsync(force: false);
        }
    }

    private async Task<bool> SaveConfig()
    {
        var currentSettings = RequireSettings();
        var settings = currentSettings.CreateCopy();
        settings.ParallelServerOperations = ParallelServerOperationsCheckBox.IsChecked == true;
        settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        settings.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        settings.RestartAppOnCrash = RestartAppOnCrashCheckBox.IsChecked == true;
        if (!int.TryParse(AppCrashRestartMaxAttemptsTextBox.Text, out var crashRestartMaxAttempts) || crashRestartMaxAttempts < 1)
        {
            SetStatus("Crash restart max attempts must be 1 or higher.");
            return false;
        }

        if (!int.TryParse(AppCrashRestartWindowTextBox.Text, out var crashRestartWindowMinutes) || crashRestartWindowMinutes < 1)
        {
            SetStatus("Crash restart window must be 1 minute or higher.");
            return false;
        }

        settings.AppCrashRestartMaxAttempts = crashRestartMaxAttempts;
        settings.AppCrashRestartWindowMinutes = crashRestartWindowMinutes;
        if (!int.TryParse(BackupRetentionTextBox.Text, out var retentionCount) || retentionCount < 0)
        {
            SetStatus("Backups to keep must be 0 or higher.");
            return false;
        }

        settings.BackupRetentionCount = retentionCount;

        if (!int.TryParse(AutoUpdateIntervalTextBox.Text, out var autoUpdateIntervalMinutes) || autoUpdateIntervalMinutes < 0)
        {
            SetStatus("Auto update interval must be 0 or higher.");
            return false;
        }

        settings.AutoUpdateIntervalMinutes = autoUpdateIntervalMinutes;
        settings.PersistentAuthenticatedSteamUpdates = PersistentAuthenticatedSteamUpdatesCheckBox.IsChecked == true;
        if (!int.TryParse(PublicIpIntervalTextBox.Text, out var publicIpIntervalMinutes) || publicIpIntervalMinutes < 1)
        {
            SetStatus("Public IP interval must be 1 or higher.");
            return false;
        }

        if (!Uri.TryCreate(PublicIpEndpointTextBox.Text.Trim(), UriKind.Absolute, out var publicIpEndpoint) ||
            publicIpEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            SetStatus("Public IP endpoint must be an absolute HTTPS URL.");
            return false;
        }

        var publicIpTrackingEnabled = PublicIpTrackingEnabledCheckBox.IsChecked == true;
        if (publicIpTrackingEnabled && !await PublicIpEndpointPolicy.IsAllowedEndpointAsync(publicIpEndpoint))
        {
            SetStatus("Public IP endpoint could not be validated: its host resolves to a loopback/private/link-local address, or the DNS lookup failed or timed out. Use a public endpoint such as https://api.ipify.org.");
            return false;
        }

        settings.PublicIpTrackingEnabled = publicIpTrackingEnabled;
        settings.PublicIpEndpoint = publicIpEndpoint.ToString();
        settings.PublicIpCheckIntervalMinutes = publicIpIntervalMinutes;

        var enableExternalReachabilityChecks = ExternalReachabilityChecksEnabledCheckBox.IsChecked == true;
        var externalReachabilityConsent = ResolveExternalReachabilityConsent(
            enableExternalReachabilityChecks,
            currentSettings.ExternalReachabilityConsentAcknowledged,
            () =>
            {
                var consent = System.Windows.MessageBox.Show(
                    "Enable external reachability checks? This feature runs only when you click Test External Reachability in a server's Health tab, and sends only the port numbers you are testing to probe.windowsgsh.com. WindowsGSH does not send server names, module/game names, credentials, or your public address to the service. The service observes your public address from the HTTPS connection, and Cloudflare or UKHost4U may retain ordinary infrastructure access logs.",
                    "External reachability",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                return consent == MessageBoxResult.Yes;
            });
        settings.ExternalReachabilityChecksEnabled = externalReachabilityConsent.Enabled;
        settings.ExternalReachabilityConsentAcknowledged = externalReachabilityConsent.Acknowledged;
        ExternalReachabilityChecksEnabledCheckBox.IsChecked = externalReachabilityConsent.Enabled;
        settings.DiscordBotEnabled = DiscordBotEnabledCheckBox.IsChecked == true;
        settings.DiscordBotPrefix = string.IsNullOrWhiteSpace(DiscordBotPrefixTextBox.Text) ? "!" : DiscordBotPrefixTextBox.Text.Trim();
        settings.DiscordAllowDestructiveCommands = DiscordAllowDestructiveCommandsCheckBox.IsChecked == true;
        settings.DiscordNotificationsChannelId = DiscordNotificationsChannelTextBox.Text.Trim();
        settings.DiscordWebhookEnabled = DiscordWebhookEnabledCheckBox.IsChecked == true;
        settings.KnownJavaRuntimePaths = ReadJavaRuntimePaths();
        List<string> externalAllowedPaths;
        try
        {
            externalAllowedPaths = ExternalCommandAllowedPathsTextBox.Text
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStatus($"External command allowlist contains an invalid path: {ex.Message}");
            return false;
        }
        var invalidExternalPath = externalAllowedPaths.FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path));
        if (invalidExternalPath != null)
        {
            SetStatus($"External command allowlist path does not exist: {invalidExternalPath}");
            return false;
        }

        settings.ExternalScheduledCommandsEnabled = ExternalScheduledCommandsEnabledCheckBox.IsChecked == true;
        settings.ExternalCommandAllowedPaths = externalAllowedPaths;
        settings.ReducedMotion = ReducedMotionCheckBox.IsChecked == true;
        settings.SoftwareRendering = SoftwareRenderingCheckBox.IsChecked == true;
        settings.RuntimeDiagnosticsEnabled = RuntimeDiagnosticsEnabledCheckBox.IsChecked == true;

        if (!int.TryParse(WebPortTextBox.Text.Trim(), out var webPort) || webPort < 1024 || webPort > 65535)
        {
            SetStatus("Web port must be a number between 1024 and 65535.");
            return false;
        }
        var webBindAddress = WebBindAddressTextBox.Text.Trim();
        if (!WebHostService.IsValidBindAddress(webBindAddress))
        {
            SetStatus("Bind address must be a valid IP address such as 127.0.0.1 or 0.0.0.0. Wildcards and URLs are not accepted.");
            return false;
        }

        var enablingWebServer = WebEnabledCheckBox.IsChecked == true && !currentSettings.WebEnabled;
        if (!WebHostService.IsLoopbackAddress(webBindAddress) &&
            (webBindAddress != currentSettings.WebBindAddress || enablingWebServer))
        {
            var confirmedNonLoopback = System.Windows.MessageBox.Show(
                $"Binding the web server to {webBindAddress} exposes it beyond this PC. WindowsGSH's web API and console " +
                "carry credentials, bearer tokens, and console commands over plain HTTP unless you put it behind HTTPS " +
                "yourself.\n\nFor remote access, the recommended setup is to keep the bind address at 127.0.0.1 and use a " +
                $"reverse proxy or trusted tunnel (e.g. Cloudflare Tunnel) pointed at http://127.0.0.1:{webPort}.\n\n" +
                "Continue binding to a non-loopback address anyway?",
                "Non-Loopback Web Binding",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmedNonLoopback)
            {
                SetStatus("Web bind address was not changed. Non-loopback binding requires confirmation.");
                return false;
            }
        }

        settings.WebEnabled = WebEnabledCheckBox.IsChecked == true;
        settings.WebPort = webPort;
        settings.WebBindAddress = webBindAddress;
        settings.WebTrustForwardedHeaders = WebTrustForwardedHeadersCheckBox.IsChecked == true;
        settings.AllowLegacyWebSocketQueryStringAuth = AllowLegacyWebSocketQueryStringAuthCheckBox.IsChecked == true;

        IReadOnlyList<DiscordAdminBinding> discordAdmins;
        try
        {
            discordAdmins = ReadDiscordAdmins();
        }
        catch (Exception ex)
        {
            SetStatus("Discord admin settings are invalid: " + ex.Message);
            return false;
        }

        try
        {
            RequireDiscordRepository().SaveAdmins(discordAdmins);
            if (!string.IsNullOrWhiteSpace(DiscordBotTokenBox.Password))
            {
                RequireDiscordTokenStore().Save(DiscordBotTokenBox.Password);
            }

            if (!string.IsNullOrWhiteSpace(DiscordWebhookUrlBox.Password))
            {
                RequireDiscordWebhookStore().SaveGlobal(DiscordWebhookUrlBox.Password);
            }

            settings.Save();
        }
        catch (Exception ex)
        {
            SetStatus("Could not save WindowsGSH settings: " + ex.Message);
            return false;
        }

        var startupError = _updateStartupRegistration?.Invoke(settings.StartWithWindows);
        if (!string.IsNullOrWhiteSpace(startupError))
        {
            var rollbackSucceeded = false;
            try
            {
                currentSettings.Save();
                _ = _updateStartupRegistration?.Invoke(currentSettings.StartWithWindows);
                rollbackSucceeded = true;
            }
            catch (Exception rollbackEx)
            {
                _log?.Invoke("Could not roll back settings after startup registration failed: " + rollbackEx.Message);
            }

            SetStatus(
                rollbackSucceeded
                    ? startupError + " The previous settings were restored."
                    : startupError + " The previous settings could not be restored; review the app log.");
            return false;
        }

        DiscordBotTokenBox.Password = string.Empty;
        DiscordWebhookUrlBox.Password = string.Empty;
        currentSettings.ApplyFrom(settings);
        _desktopSettingsChanged?.Invoke();
        SetStatus("Saved.");
        return true;
    }

    private static string GetCurrentRenderingModeText()
    {
        return RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly
            ? "Current mode: Software rendering"
            : "Current mode: Hardware rendering";
    }

    private void UpdateWebServerStatusText(bool savedEnabled)
    {
        if (WebHostService.IsRunning)
            WebServerStatusTextBlock.Text = $"Web server is running on port {WebHostService.ActivePort}.";
        else if (WebHostService.LastStartError != null)
            WebServerStatusTextBlock.Text = $"Web server is not running. {WebHostService.LastStartError}";
        else
            WebServerStatusTextBlock.Text = "Web server is not running.";

        var pendingEnabled = WebEnabledCheckBox.IsChecked == true;
        var bindAddressChanged = WebHostService.IsRunning &&
                                 WebBindAddressTextBox.Text.Trim() != WebHostService.ActiveBindAddress;
        var portChanged = WebHostService.IsRunning &&
                          int.TryParse(WebPortTextBox.Text.Trim(), out var p) &&
                          p != WebHostService.ActivePort;
        var restartNeeded = pendingEnabled != WebHostService.IsRunning || portChanged || bindAddressChanged;
        WebRestartRequiredTextBlock.Visibility = restartNeeded
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void WebEnabledCheckBox_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateWebServerStatusText(WebEnabledCheckBox.IsChecked == true);
    }

    private void WebPortTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateWebServerStatusText(WebEnabledCheckBox.IsChecked == true);
    }

    private void WebBindAddressTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateWebBindAddressWarning(WebBindAddressTextBox.Text.Trim());
        UpdateWebServerStatusText(WebEnabledCheckBox.IsChecked == true);
    }

    private void UpdateWebBindAddressWarning(string address)
    {
        WebLanWarningTextBlock.Visibility = WebHostService.IsLoopbackAddress(address)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    private async Task RefreshWebUsersListAsync()
    {
        try
        {
            var users = await new WebUserRepository().ListAsync();
            WebUsersListBox.Items.Clear();
            foreach (var user in users)
            {
                var status = user.Enabled ? string.Empty : " [disabled]";
                var forceChange = user.ForcePasswordChange ? " [must change password]" : string.Empty;
                WebUsersListBox.Items.Add($"{user.Username}  •  {user.Role}{status}{forceChange}");
            }
            if (WebUsersListBox.Items.Count == 0)
                WebUsersListBox.Items.Add("No web users yet.");
        }
        catch (Exception ex)
        {
            WebUserFeedbackTextBlock.Text = $"Could not load users: {ex.Message}";
        }
    }

    private async void CreateWebUserButton_Click(object sender, RoutedEventArgs e)
    {
        var username = NewWebUsernameTextBox.Text.Trim();
        var password = NewWebPasswordBox.Password;
        var roleText = (NewWebRoleComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Viewer";

        if (string.IsNullOrWhiteSpace(username))
        {
            WebUserFeedbackTextBlock.Text = "Username is required.";
            return;
        }
        if (!WebPasswordPolicy.IsValid(password))
        {
            WebUserFeedbackTextBlock.Text = $"Password must be at least {WebPasswordPolicy.MinimumLength} characters.";
            return;
        }
        if (!Enum.TryParse<WebRole>(roleText, out var role))
        {
            WebUserFeedbackTextBlock.Text = "Unknown role.";
            return;
        }

        try
        {
            CreateWebUserButton.IsEnabled = false;
            var store = new WebUserRepository();
            var existing = await store.FindByUsernameAsync(username);
            if (existing != null)
            {
                WebUserFeedbackTextBlock.Text = $"A user named '{username}' already exists.";
                return;
            }

            var (hash, salt) = PasswordHasher.Hash(password);
            var user = new WebUser(0, username, hash, salt, role, DateTimeOffset.UtcNow, null, true, false);
            await store.CreateAsync(user);

            NewWebUsernameTextBox.Text = string.Empty;
            NewWebPasswordBox.Clear();
            WebUserFeedbackTextBlock.Text = $"User '{username}' created with role '{role}'.";
            await RefreshWebUsersListAsync();
        }
        catch (Exception ex)
        {
            WebUserFeedbackTextBlock.Text = $"Error: {ex.Message}";
        }
        finally
        {
            CreateWebUserButton.IsEnabled = true;
        }
    }

    private async void DeleteWebUserButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = WebUsersListBox.SelectedIndex;
        if (selectedIndex < 0)
        {
            WebUserFeedbackTextBlock.Text = "Select a user to delete.";
            return;
        }

        try
        {
            var users = await new WebUserRepository().ListAsync();
            if (selectedIndex >= users.Count)
                return;

            var user = users[selectedIndex];
            var confirm = System.Windows.MessageBox.Show(
                $"Delete web user '{user.Username}'? This cannot be undone.",
                "Delete web user",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            await new WebUserRepository().DeleteAsync(user.Id);
            WebUserFeedbackTextBlock.Text = $"User '{user.Username}' deleted.";
            await RefreshWebUsersListAsync();
        }
        catch (Exception ex)
        {
            WebUserFeedbackTextBlock.Text = $"Error: {ex.Message}";
        }
    }

    private async void ResetWebUserPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = WebUsersListBox.SelectedIndex;
        if (selectedIndex < 0)
        {
            WebUserFeedbackTextBlock.Text = "Select a user to reset their password.";
            return;
        }

        try
        {
            var users = await new WebUserRepository().ListAsync();
            if (selectedIndex >= users.Count)
                return;

            var user = users[selectedIndex];
            var newPassword = GenerateTemporaryPassword();
            var (hash, salt) = PasswordHasher.Hash(newPassword);
            var updated = user with { PasswordHash = hash, Salt = salt, ForcePasswordChange = true };
            await new WebUserRepository().UpdateAsync(updated);
            await new WebUserRepository().RevokeAllRefreshTokensForUserAsync(user.Id);

            System.Windows.MessageBox.Show(
                $"Password for '{user.Username}' reset to:\n\n{newPassword}\n\nThey must change it on next login.",
                "Password reset",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            WebUserFeedbackTextBlock.Text = $"Password reset for '{user.Username}'.";
            await RefreshWebUsersListAsync();
        }
        catch (Exception ex)
        {
            WebUserFeedbackTextBlock.Text = $"Error: {ex.Message}";
        }
    }

    private static string GenerateTemporaryPassword()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
    }

    private void CancelConfigButton_Click(object sender, RoutedEventArgs e)
    {
        LoadControls();
    }

    private void SaveSteamCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var store = RequireSteamCredentialStore();
            var previousUsername = store.LoadUsername();
            store.Save(SteamUsernameTextBox.Text, SteamPasswordBox.Password);
            if (!string.Equals(previousUsername, SteamUsernameTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _depotDownloaderSessionStore.Clear();
            }

            SteamPasswordBox.Password = string.Empty;
            SteamCredentialHintTextBlock.Text = "Steam login saved for this Windows user.";
            PersistentSteamSessionStatusTextBlock.Text = _depotDownloaderSessionStore.HasProtectedSession
                ? "A reusable Steam session is saved with Windows user encryption."
                : "No reusable Steam session is saved yet.";
            SetStatus("Steam login saved.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void ClearSteamCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        RequireSteamCredentialStore().Clear();
        _depotDownloaderSessionStore.Clear();
        LoadSteamCredentialControls();
        SetStatus("Steam login and remembered Steam session cleared.");
    }

    private async void DetectJavaRuntimesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunJavaDiscoveryAsync(async cancellationToken =>
        {
            var knownPaths = ReadJavaRuntimePaths();
            var manager = new JavaRuntimeManager();
            var runtimes = await manager.DiscoverAsync(knownPaths, cancellationToken);
            var mergedPaths = knownPaths
                .Concat(runtimes.Where(runtime => runtime.Found).Select(runtime => runtime.ExecutablePath))
                .Select(JavaRuntimeLocator.NormalizeExecutablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var mergedRuntimes = await manager.DiscoverAsync(mergedPaths, cancellationToken);
            LoadJavaRuntimeRows(mergedRuntimes, mergedPaths);
            var foundCount = _javaRuntimeRows.Count(row => row.IsAvailable);
            SetStatus(foundCount == 0 ? "No Java runtimes detected." : $"Detected {foundCount} working Java runtime(s). Save to cache them.");
        });
    }

    private void BrowseJavaRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Java Runtime",
            Filter = "Java executable (java.exe)|java.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            JavaRuntimePathTextBox.Text = dialog.FileName;
        }
    }

    private async void AddJavaRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        var path = JavaRuntimePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("Choose or enter a Java runtime path first.");
            return;
        }

        await RunJavaDiscoveryAsync(async cancellationToken =>
        {
            var paths = ReadJavaRuntimePaths()
                .Append(path)
                .Select(JavaRuntimeLocator.NormalizeExecutablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var runtimes = await new JavaRuntimeManager().DiscoverAsync(paths, cancellationToken);
            LoadJavaRuntimeRows(runtimes, paths);
            JavaRuntimePathTextBox.Text = string.Empty;
            SetStatus("Java runtime path added. Save to cache it.");
        });
    }

    private void RemoveJavaRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (KnownJavaRuntimesDataGrid.SelectedItem is not JavaRuntimeRow selected)
        {
            SetStatus("Select a Java runtime row to remove.");
            return;
        }

        if (selected.IsManaged)
        {
            SetStatus("Managed runtimes must be removed from the Managed Java runtimes section below.");
            return;
        }

        LoadJavaRuntimeRowsWithoutProbing(ReadJavaRuntimePaths().Where(path =>
            !string.Equals(path, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
        SetStatus("Java runtime removed. Save to persist this change.");
    }

    private void ClearJavaRuntimesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadJavaRuntimeRowsWithoutProbing([]);
        JavaRuntimeHintTextBlock.Text = "Java runtime cache cleared. Save to persist this change.";
    }

    private async void RefreshManagedJavaButton_Click(object sender, RoutedEventArgs e)
    {
        await RunManagedJavaOperationAsync(async cancellationToken =>
        {
            await _managedJavaStore.Catalogue.RefreshFromApiAsync(
                new AdoptiumApiClient(),
                new Progress<string>(message => ManagedJavaRuntimeHintTextBlock.Text = message),
                cancellationToken);
            RefreshManagedJavaRows();
            SetStatus("Managed Java catalogue refreshed.");
        });
    }

    private async void InstallManagedJavaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedJavaRuntimesDataGrid.SelectedItem is not ManagedJavaRuntimeRow selected)
        {
            SetStatus("Select a managed Java runtime first.");
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            $"Download and install {selected.Version} from Eclipse Adoptium?\n\n" +
            $"Source: {selected.Release.SourceUrl}\nLicense: {selected.Release.LicenseUrl}",
            "Install Managed Java Runtime",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        await RunManagedJavaOperationAsync(async cancellationToken =>
        {
            await _managedJavaStore.InstallService.InstallAsync(
                selected.Release,
                new Progress<string>(message => ManagedJavaRuntimeHintTextBlock.Text = message),
                cancellationToken);
            RefreshManagedJavaRows();
            RefreshJavaRuntimeGrid();
            SetStatus($"{selected.Version} installed.");
        });
    }

    private async void RepairManagedJavaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedJavaRuntimesDataGrid.SelectedItem is not ManagedJavaRuntimeRow selected ||
            !_managedJavaStore.InstallService.IsInstalled(selected.Release))
        {
            SetStatus("Select an installed managed Java runtime to repair.");
            return;
        }

        await RunManagedJavaOperationAsync(async cancellationToken =>
        {
            await _managedJavaStore.InstallService.RepairAsync(
                selected.Release,
                new Progress<string>(message => ManagedJavaRuntimeHintTextBlock.Text = message),
                cancellationToken);
            RefreshManagedJavaRows();
            RefreshJavaRuntimeGrid();
            SetStatus($"{selected.Version} repaired.");
        });
    }

    private async void RemoveManagedJavaButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedJavaRuntimesDataGrid.SelectedItem is not ManagedJavaRuntimeRow selected ||
            !_managedJavaStore.InstallService.IsInstalled(selected.Release))
        {
            SetStatus("Select an installed managed Java runtime to remove.");
            return;
        }

        await RunManagedJavaOperationAsync(async cancellationToken =>
        {
            var servers = await RequireInstalledServerLoader().LoadAsync(cancellationToken);
            var references = _managedJavaStore.GetReferencingServers(selected.Release.Id, servers);
            if (references.Count > 0)
            {
                SetStatus($"Cannot remove {selected.Version}; used by: {string.Join(", ", references)}.");
                return;
            }

            if (System.Windows.MessageBox.Show(
                    $"Remove {selected.Version} from WindowsGSH?",
                    "Remove Managed Java Runtime",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _managedJavaStore.InstallService.Remove(selected.Release);
            RefreshManagedJavaRows();
            RefreshJavaRuntimeGrid();
            SetStatus($"{selected.Version} removed.");
        });
    }

    private void CancelManagedJavaButton_Click(object sender, RoutedEventArgs e)
    {
        CancelManagedJavaButton.IsEnabled = false;
        ManagedJavaRuntimeHintTextBlock.Text = "Cancelling managed Java operation...";
        _managedJavaCancellation?.Cancel();
    }

    private async void StartDiscordBotButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveConfig())
        {
            await StartDiscordBotAsync(force: true);
        }
    }

    private async void StopDiscordBotButton_Click(object sender, RoutedEventArgs e)
    {
        await StopDiscordBotAsync();
    }

    private void CopyDiscordInviteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiscordInviteLinkTextBox.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(DiscordInviteLinkTextBox.Text);
            SetStatus("Discord invite link copied.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy Discord invite link: " + ex.Message);
        }
    }

    private void OpenDiscordInviteButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiscordInviteLinkTextBox.Text))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DiscordInviteLinkTextBox.Text,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open Discord invite link: " + ex.Message);
        }
    }

    private void ClearDiscordTokenButton_Click(object sender, RoutedEventArgs e)
    {
        RequireDiscordTokenStore().Clear();
        DiscordBotTokenBox.Password = string.Empty;
        DiscordBotHintTextBlock.Text = "Discord bot token cleared.";
        SetStatus("Discord bot token cleared.");
    }

    private async void TestDiscordWebhookButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveConfig() || _testDiscordWebhook == null)
        {
            return;
        }

        await _testDiscordWebhook();
        SetStatus("Discord webhook test requested.");
    }

    private void ClearDiscordWebhookButton_Click(object sender, RoutedEventArgs e)
    {
        RequireDiscordWebhookStore().ClearGlobal();
        DiscordWebhookUrlBox.Password = string.Empty;
        DiscordWebhookHintTextBlock.Text = "Global Discord webhook cleared.";
        SetStatus("Global Discord webhook cleared.");
    }

    private void ReadinessCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _showReadiness?.Invoke();
    }

    private void OperationHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _showOperationHistory?.Invoke();
    }

    private async void ExportSupportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export WindowsGSH Support Bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"WindowsGSH-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ExportSupportBundleButton.IsEnabled = false;
        SetStatus("Evaluating server health...");
        IReadOnlyList<ServerHealthReport> healthReports = [];
        try
        {
            if (_getServerHealthReports != null)
            {
                healthReports = await _getServerHealthReports();
            }
        }
        catch (Exception ex)
        {
            // Fresh Server Doctor results are a nice-to-have addition to the bundle, not a
            // requirement for it - a failure gathering them must not block the export itself.
            AppLogService.Add($"Could not gather server health results for the support bundle: {ex.Message}", "Support");
        }

        SetStatus("Creating redacted support bundle...");
        try
        {
            var request = new SupportBundleRequest(
                dialog.FileName,
                AppPaths.AppDirectory,
                AppVersionInfo.DisplayVersion,
                ModuleCompatibility.CurrentModuleApiVersion,
                AppLogService.Text,
                OperationHistoryRepository.GetRecent(500),
                RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly ? "Software" : "Hardware",
                RequireSettings().ReducedMotion,
                healthReports);
            var result = await Task.Run(() => new SupportBundleService().Export(request));
            AppLogService.Add($"Support bundle exported: {result.OutputPath}", "Support");
            SetStatus($"Support bundle created with {result.EntryCount} file(s): {result.OutputPath}");
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Support bundle export failed: {ex.Message}", "Support");
            SetStatus($"Support bundle export failed: {ex.Message}");
        }
        finally
        {
            ExportSupportBundleButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<DiscordAdminBinding> ReadDiscordAdmins()
    {
        return DiscordAdminIdsTextBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => new DiscordAdminBinding(
                parts[0],
                parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "0",
                null))
            .ToArray();
    }

    private List<string> ReadJavaRuntimePaths()
    {
        return _javaRuntimeRows
            .Where(row => !row.IsManaged)
            .Select(row => row.ExecutablePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadJavaRuntimeRowsWithoutProbing(IEnumerable<string> paths)
    {
        _javaRuntimeRows.Clear();
        foreach (var path in paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(JavaRuntimeLocator.NormalizeExecutablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _javaRuntimeRows.Add(new JavaRuntimeRow(
                path,
                "--",
                "--",
                "Not checked",
                false));
        }

        RefreshJavaRuntimeGrid();
    }

    private void LoadJavaRuntimeRows(
        IReadOnlyList<JavaRuntimeInfo> runtimes,
        IEnumerable<string> requestedPaths)
    {
        var byPath = runtimes.ToDictionary(runtime => runtime.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        _javaRuntimeRows.Clear();
        foreach (var path in requestedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(JavaRuntimeLocator.NormalizeExecutablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byPath.TryGetValue(path, out var runtime))
            {
                runtime = JavaRuntimeInfo.Missing("Unavailable") with { ExecutablePath = path };
            }

            _javaRuntimeRows.Add(new JavaRuntimeRow(
                path,
                runtime.MajorVersion.HasValue ? $"Java {runtime.MajorVersion}" : "--",
                string.IsNullOrWhiteSpace(runtime.Vendor) ? "--" : runtime.Vendor,
                runtime.Found ? "Ready" : "Unavailable",
                runtime.Found));
        }

        RefreshJavaRuntimeGrid();
    }

    private void RefreshJavaRuntimeGrid()
    {
        _javaRuntimeRows.RemoveAll(row => row.IsManaged);
        foreach (var runtime in _managedJavaStore.GetInstalledRuntimes())
        {
            _javaRuntimeRows.Add(new JavaRuntimeRow(
                runtime.JavaExecutablePath,
                $"Java {runtime.Release.MajorVersion}",
                runtime.Release.Vendor,
                "Managed · Ready",
                true,
                true));
        }

        _javaRuntimeRows.Sort((left, right) =>
        {
            var versionCompare = string.Compare(right.Version, left.Version, StringComparison.OrdinalIgnoreCase);
            return versionCompare != 0
                ? versionCompare
                : string.Compare(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        });
        KnownJavaRuntimesDataGrid.ItemsSource = null;
        KnownJavaRuntimesDataGrid.ItemsSource = _javaRuntimeRows;
        var availableCount = _javaRuntimeRows.Count(row => row.IsAvailable);
        var managedCount = _javaRuntimeRows.Count(row => row.IsManaged);
        JavaRuntimeHintTextBlock.Text = _javaRuntimeRows.Count == 0
            ? "No Java runtimes are cached yet."
            : $"{_javaRuntimeRows.Count} runtime(s) shown; {availableCount} available, including {managedCount} managed.";
    }

    private void RefreshManagedJavaRows()
    {
        _managedJavaRows.Clear();
        foreach (var release in _managedJavaStore.Catalogue.GetReleases()
                     .OrderByDescending(release => release.MajorVersion))
        {
            _managedJavaRows.Add(new ManagedJavaRuntimeRow(
                release,
                $"{release.Vendor} Java {release.MajorVersion} ({release.ReleaseVersion})",
                release.Architecture,
                _managedJavaStore.InstallService.IsInstalled(release) ? "Installed" : "Available"));
        }

        ManagedJavaRuntimesDataGrid.ItemsSource = null;
        ManagedJavaRuntimesDataGrid.ItemsSource = _managedJavaRows;
        ManagedJavaRuntimeHintTextBlock.Text = _managedJavaRows.Count == 0
            ? "Catalogue is empty. Click Refresh Catalogue to load trusted Adoptium releases."
            : $"{_managedJavaRows.Count} managed runtime release(s) available.";
    }

    private async Task RunManagedJavaOperationAsync(Func<CancellationToken, Task> action)
    {
        var previous = _managedJavaCancellation;
        var operationCancellation = new CancellationTokenSource();
        _managedJavaCancellation = operationCancellation;
        previous?.Cancel();
        previous?.Dispose();
        SetManagedJavaButtonsEnabled(false);
        try
        {
            await action(operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ManagedJavaRuntimeHintTextBlock.Text = "Managed Java operation cancelled.";
        }
        catch (Exception ex)
        {
            ManagedJavaRuntimeHintTextBlock.Text = $"Managed Java operation failed: {ex.Message}";
            SetStatus("Managed Java operation failed.");
        }
        finally
        {
            if (ReferenceEquals(_managedJavaCancellation, operationCancellation))
            {
                _managedJavaCancellation = null;
            }

            operationCancellation.Dispose();
            SetManagedJavaButtonsEnabled(true);
        }
    }

    private void SetManagedJavaButtonsEnabled(bool enabled)
    {
        RefreshManagedJavaButton.IsEnabled = enabled;
        InstallManagedJavaButton.IsEnabled = enabled;
        RepairManagedJavaButton.IsEnabled = enabled;
        RemoveManagedJavaButton.IsEnabled = enabled;
        CancelManagedJavaButton.IsEnabled = !enabled;
    }

    private async Task RunJavaDiscoveryAsync(Func<CancellationToken, Task> action)
    {
        _javaDiscoveryCancellation?.Cancel();
        _javaDiscoveryCancellation?.Dispose();
        _javaDiscoveryCancellation = new CancellationTokenSource();
        DetectJavaRuntimesButton.IsEnabled = false;
        AddJavaRuntimeButton.IsEnabled = false;
        JavaRuntimeHintTextBlock.Text = "Detecting Java runtimes...";
        try
        {
            await action(_javaDiscoveryCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            JavaRuntimeHintTextBlock.Text = "Java runtime detection cancelled.";
        }
        catch (Exception ex)
        {
            JavaRuntimeHintTextBlock.Text = $"Java runtime detection failed: {ex.Message}";
            SetStatus("Java runtime detection failed.");
        }
        finally
        {
            DetectJavaRuntimesButton.IsEnabled = true;
            AddJavaRuntimeButton.IsEnabled = true;
        }
    }

    private async Task StartDiscordBotAsync(bool force = false)
    {
        if (_startDiscordBot == null)
        {
            return;
        }

        await _startDiscordBot(force);
    }

    private async Task StopDiscordBotAsync()
    {
        if (_stopDiscordBot == null)
        {
            return;
        }

        await _stopDiscordBot();
        SetStatus("Discord bot stopped.");
        _log?.Invoke("Discord bot stopped.");
    }

    private AppSettings RequireSettings()
    {
        return _settings ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    internal static (bool Enabled, bool Acknowledged) ResolveExternalReachabilityConsent(
        bool requestedEnable,
        bool acknowledged,
        Func<bool> confirm)
    {
        if (!requestedEnable)
        {
            return (false, acknowledged);
        }

        if (acknowledged)
        {
            return (true, true);
        }

        return confirm()
            ? (true, true)
            : (false, false);
    }

    private InstalledServerLoader RequireInstalledServerLoader()
    {
        return _installedServerLoader ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    private SteamCredentialStore RequireSteamCredentialStore()
    {
        return _steamCredentialStore ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    private DiscordBotTokenStore RequireDiscordTokenStore()
    {
        return _discordTokenStore ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    private DiscordRepository RequireDiscordRepository()
    {
        return _discordRepository ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    private DiscordWebhookStore RequireDiscordWebhookStore()
    {
        return _discordWebhookStore ?? throw new InvalidOperationException("App settings view has not been configured.");
    }

    private sealed record JavaRuntimeRow(
        string ExecutablePath,
        string Version,
        string Vendor,
        string Status,
        bool IsAvailable,
        bool IsManaged = false);

    private sealed record ManagedJavaRuntimeRow(
        ManagedJavaRelease Release,
        string Version,
        string Architecture,
        string Status);
}
