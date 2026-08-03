using WindowsGSH.Core;
using WindowsGSH.Core.Api;
using WindowsGSH.Core.Automation;
using WindowsGSH.Core.Discord;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Network;
using WindowsGSH.Core.Network.Upnp;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using WindowsGSH.Core.Web.Api;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Scheduling;
using WindowsGSH.Core.Windows;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsGSH.Data;
using WindowsGSH.Data.Security;
using WindowsGSH.Discord;
using WindowsGSH.Services;

namespace WindowsGSH;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string ServersNavigationId = "servers";
    private const string LogsNavigationId = "logs";
    private const string ModulesNavigationId = "modules";
    private const string ConfigNavigationId = "config";
    private const string HostHealthNavigationId = "hosthealth";
    private static readonly TimeSpan WindowsSessionEndingStopTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan WindowsSessionEndingHandlerBudget = TimeSpan.FromSeconds(15);

    // Reserves the majority of WindowsSessionEndingStopTimeout above for the actual stop sequence
    // (CancelActiveOperationsForExitAsync + StopRunningServersAsync + the Discord bot stop) that
    // follows enumeration. Deliberately much shorter than the exit path's own ShutdownEnumerationTimeout
    // (10s) - a hung module during Windows session ending must not be allowed to consume nearly the
    // entire 12-second cooperative deadline, since that would leave almost no time to actually
    // gracefully stop the servers recovered from LastKnownGoodShutdownSnapshot (the whole point of that
    // fallback), even before accounting for any active-operation cancellation.
    private static readonly TimeSpan WindowsSessionEndingEnumerationTimeout = TimeSpan.FromSeconds(3);

    // Bounds each individual ServerProcessLocator.TryIsRunningAsync probe used while waiting for a
    // server to stop gracefully during Windows session ending (see StopServerForShutdownAsync). A
    // broken module's Runtime getter can hang this call the same way it can hang enumeration; the
    // outer cancellationToken (the Windows session-ending deadline) still bounds the total wait even
    // if every individual probe times out.
    private static readonly TimeSpan ProcessProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DiscordConsoleCommandTimeout = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SteamCredentialStore _steamCredentialStore = new();
    private readonly SteamBranchPasswordStore _steamBranchPasswordStore = new();
    private readonly DiscordBotTokenStore _discordTokenStore = new();
    private readonly DiscordWebhookStore _discordWebhookStore = new();
    private readonly DiscordRepository _discordRepository = new();
    private readonly ConcurrentDictionary<string, Task> _pendingDiscordConsoleWorkers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly InstalledServerLoader _installedServerLoader = new();
    private readonly ServerLifecycleService _lifecycleService;
    private readonly ServerInstallService _installService;
    private readonly ServerVerifyService _verifyService;
    private readonly IServerConsoleService _consoleService = ServerConsoleService.Shared;
    private readonly ServerBackupService _backupService = new();
    private readonly ServerDeleteService _deleteService;
    private readonly ServerOperationsController _serverOperations;
    private readonly ServerCrashDiagnosticsService _crashDiagnosticsService = new();
    // Shares MainWindow's own _installedServerLoader rather than letting ReadinessCheckService create
    // its own - InstalledServerLoader's hang-protection dedup dictionary is per-instance, so two
    // separate loaders inside the same running app double the number of independent stuck workers a
    // single permanently hung module could accumulate. Assigned in the constructor body, not a field
    // initializer - C# field initializers cannot reference another instance field (CS0236).
    private readonly ReadinessCheckService _readinessCheckService;
    private readonly FirstRunChecklistComposer _firstRunChecklistComposer = new();
    private readonly FirstRunReadinessRunner _firstRunReadinessRunner;
    private readonly WindowsFirewallService _firewallService = new();
    private readonly ModuleApiActionClient _apiActionClient = new();
    private readonly UpnpMappingLifecycleService _upnpMappingLifecycleService = new(new PortMappingRegistry(), log: AppLogService.Add);
    private readonly ServerOperationManager _operationManager = ServerOperationManager.Shared;
    private readonly ServerStatusComposer _serverStatusComposer = new(TimeSpan.FromMinutes(10));
    private readonly ServerListViewModel _serverListViewModel;
    private readonly ServerCardCollection _serverCards = new();
    private readonly StableItemCollection<ServerIssueRow> _serverIssues = new();
    private readonly StableItemCollection<FirstRunChecklistItem> _firstRunChecklist = new();
    private readonly ServerRuntimeTracker _runtimeTracker;
    private readonly ShutdownPlanner _shutdownPlanner = new();
    private readonly ApplicationExitPlanner _exitPlanner = new();
    private readonly ServerScheduleRunner _scheduleRunner = new();
    private readonly ExternalCommandRunner _externalCommandRunner = new();
    private readonly ServerAutomationService _automationService = new();
    private readonly ServerHealthService _serverHealthService = new();
    private readonly StartupRegistrationService _startupRegistrationService = new();
    private readonly RuntimeDiagnosticsService _runtimeDiagnostics;
    private readonly DiscordBotHost _discordBotHost;
    private readonly DiscordEventAlertService _discordEventAlertService;
    private readonly DiscordWebhookNotificationService _discordWebhookNotificationService;
    private readonly System.Collections.ObjectModel.ObservableCollection<NavigationItem> _navigationItems =
    [
        new(ServersNavigationId, "Servers"),
        new(LogsNavigationId, "Logs"),
        new(ModulesNavigationId, "Module Management"),
        new(ConfigNavigationId, "WindowsGSH Config"),
        new(HostHealthNavigationId, "Host Health")
    ];
    private readonly Dictionary<string, DateTimeOffset> _lastAutoUpdateChecks = [];
    private readonly Dictionary<string, DateTimeOffset> _lastAutomationChecks = [];
    private readonly HashSet<string> _manuallyStoppedServers = [];
    private readonly HashSet<string> _openConfigServerIds = [];
    private IReadOnlyList<ServerIssueRow> _healthIssueRows = [];
    private DateTimeOffset _lastHealthCheckAt;
    private bool _isClosing;
    private bool _closeDecisionInProgress;
    private bool _windowsSessionEnding;
    private bool _trayAvailable;
    private bool _serversViewIsEmpty;
    private IReadOnlyList<ReadinessCheckResult> _latestReadinessResults = [];
    private readonly SemaphoreSlim _trayStopAllGate = new(1, 1);
    private readonly SemaphoreSlim _firstRunReadinessGate = new(1, 1);
    private HwndSource? _windowSource;
    private bool _startupWorkQueued;
    private bool _bulkActionWindowOpen;
    private bool _bulkActionInProgress;
    private IDisposable? _crashEventSubscription;

    public MainWindow()
    {
        InitializeComponent();
        _runtimeDiagnostics = new RuntimeDiagnosticsService(Dispatcher);
        _readinessCheckService = new ReadinessCheckService(_installedServerLoader);

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        _firstRunReadinessRunner = new(
            () => _readinessCheckService.RunAsync(),
            ex => AppLogService.Add("SteamCMD setup failed: " + ex.Message));
        _appLogCollectionChangedHandler = (_, _) => QueueAppLogRefresh();
        MainNavigationItemsControl.ItemsSource = _navigationItems;
        InstalledServersItemsControl.ItemsSource = _serverCards.Items;
        ServerIssuesItemsControl.ItemsSource = _serverIssues.Items;
        FirstRunChecklistItemsControl.ItemsSource = _firstRunChecklist.Items;
        _lifecycleService = new ServerLifecycleService(() => _moduleRegistry.GetModules());
        var steamGuardProvider = new SteamGuardCodeProvider(this);
        var steamClient = new PersistentSteamClient(
            _steamCredentialStore,
            steamGuardProvider,
            () => _settings.PersistentAuthenticatedSteamUpdates);
        _installService = new ServerInstallService(steamClient, _operationManager);
        _verifyService = new ServerVerifyService(steamClient, _operationManager);
        _deleteService = new ServerDeleteService(_operationManager);
        _runtimeTracker = new ServerRuntimeTracker(
            _consoleService,
            _crashDiagnosticsService,
            _serverStatusComposer,
            () => _isClosing,
            AppLogService.Add,
            DispatchUnexpectedServerExitAsync,
            DispatchRefreshInstalledServersViewOnlyAsync);
        _serverListViewModel = new ServerListViewModel(
            _serverStatusComposer,
            _operationManager.Get,
            _runtimeTracker.GetLiveMonitoredProcessId);
        _serverOperations = new ServerOperationsController(
            _operationManager,
            _lifecycleService,
            _backupService,
            GetModule,
            StartServerAsync,
            (server, module, reason, skipIgnoredBuild, token) => UpdateServerAsync(server, module, reason, token, skipIgnoredBuild),
            CreateLifecycleStopOptions,
            GetConfiguredBackupPaths,
            () => _settings.BackupRetentionCount,
            CancelActiveServerOperationAsync,
            RefreshInstalledServersViewOnlyAsync,
            AppLogService.Add,
            SetOperationStatus,
            _runtimeTracker.StopLogTail,
            _runtimeTracker.StopAddonProcesses,
            MarkAutoUpdateChecked,
            MarkAutomationChecked,
            SetManuallyStopped);
        DiscordChannelBackfillService.Backfill(
            new DiscordRepository(),
            _settings.DiscordDashboardChannelId,
            _settings.DiscordNotificationsChannelId);
        _discordBotHost = new DiscordBotHost(
            GetDiscordServersAsync,
            ExecuteDiscordServerCommandAsync,
            message => AppLogService.Add(message),
            AppLogService.GetRecentText,
            _consoleService.GetRecentText);
        _discordBotHost.SetEnabled(_settings.DiscordBotEnabled);
        _discordWebhookNotificationService = new DiscordWebhookNotificationService(
            () => _settings.DiscordWebhookEnabled,
            _discordWebhookStore.LoadGlobal,
            ResolveServerWebhook,
            log: message => AppLogService.Add(message));
        // One shared alert service decides whether to fire and renders the message exactly once per
        // event, then fans out to both transports. Do not split this back into two independently
        // subscribed DiscordEventAlertServices - that previously caused every alert to be evaluated
        // and sent twice whenever both a bot Alert Channel and a webhook were configured.
        _discordEventAlertService = new DiscordEventAlertService(
            WindowsGshEventBus.Shared,
            ResolveDiscordAlertServer,
            _discordRepository.IsNotificationEnabled,
            SendDiscordAlertToAllTransportsAsync,
            log: message => AppLogService.Add(message));
        _discordEventAlertService.Start();
        _crashEventSubscription = WindowsGshEventBus.Shared.Subscribe<ServerCrashDetectedEvent>(OnServerCrashDetected);
        ConfigView.Configure(
            _settings,
            _steamCredentialStore,
            _discordTokenStore,
            _discordWebhookStore,
            _discordRepository,
            _discordBotHost,
            StartDiscordBotIfEnabledAsync,
            StopDiscordBotAsync,
            () => _discordWebhookNotificationService.SendTestAlertAsync(),
            ShowReadinessCheck,
            ShowOperationHistory,
            message => AppLogService.Add(message),
            UpdateStartupRegistration,
            ApplyDesktopSettings,
            _installedServerLoader,
            BuildSupportBundleHealthReportsAsync);
        AppLogService.Messages.CollectionChanged += _appLogCollectionChangedHandler;
        LogFilterComboBox.Items.Add(new ServerLogFilterItem("All logs", null));
        LogFilterComboBox.SelectedIndex = 0;
        RefreshAppLogText();
        ThemeSelector.SelectedItem = ThemeSelector.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), _settings.Theme, StringComparison.OrdinalIgnoreCase));
        NavigateTo(ServersNavigationId);
        ApplySelectedTheme();
        _themeSelectorReady = true;
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += MainWindow_StateChanged;
        WebServerState.SetServerMetrics(_installedServerLoader.MetricsService);
        InitializeWebServerControl();
        _serverRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _serverRefreshTimer.Tick += ServerRefreshTimer_Tick;
        _runtimeDiagnostics.SetEnabled(_settings.RuntimeDiagnosticsEnabled);
    }

    private async void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        QueueStartupWorkAfterFirstRender();
    }

    private void QueueStartupWorkAfterFirstRender()
    {
        if (_startupWorkQueued)
        {
            return;
        }

        _startupWorkQueued = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _serverRefreshTimer.Start();
                StartSystemMetricsSampling();
                _ = LoadInitialServerCardsAsync();
                _ = RunStartupReadinessAsync();
            }),
            // Render/input work still runs first, but do not wait for ApplicationIdle: animated
            // controls and other recurring dispatcher work can postpone that priority for several
            // seconds even though the window is already visible.
            DispatcherPriority.Background);
    }

    private async Task RunStartupReadinessAsync()
    {
        await StartDiscordBotIfEnabledAsync();
        await RefreshFirstRunReadinessAsync(
            "startup",
            ensureSteamCmd: true,
            logWarnings: true);
    }

    private async Task RefreshFirstRunReadinessAsync(
        string context,
        bool ensureSteamCmd = false,
        bool logWarnings = false)
    {
        await _firstRunReadinessGate.WaitAsync();
        try
        {
            Func<Task>? setup = null;
            if (ensureSteamCmd)
            {
                setup = async () =>
                {
                    AppLogService.Add("Checking SteamCMD.");
                    await new Core.Steam.SteamCmdManager(_steamCredentialStore)
                        .EnsureInstalledAsync(AppLogService.CreateProgress());
                };
            }

            var readiness = await _firstRunReadinessRunner.RunAsync(setup);
            _latestReadinessResults = readiness;
            UpdateFirstRunChecklist();
            if (logWarnings)
            {
                foreach (var issue in readiness.Where(check => check.Status is ReadinessStatus.Warning or ReadinessStatus.Fail))
                {
                    AppLogService.Add($"Readiness {issue.Status}: {issue.Name} - {issue.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Add($"{context} readiness refresh failed: {ex.Message}");
        }
        finally
        {
            _firstRunReadinessGate.Release();
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _crashEventSubscription?.Dispose();
        _crashEventSubscription = null;
        StateChanged -= MainWindow_StateChanged;
        StopSystemMetricsSampling();
        AppLogService.Messages.CollectionChanged -= _appLogCollectionChangedHandler;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _serverRefreshTimer.Stop();
        _runtimeDiagnostics.Dispose();
        _discordEventAlertService.Dispose();
        _discordWebhookNotificationService.Dispose();

        // Best-effort, bounded to a few seconds total: Discord logout and the final diagnostics/
        // metrics sample writes (all three cancelled just above) usually finish almost immediately,
        // but must never block window close indefinitely if one is stuck (e.g. a hung Discord REST
        // call) - the process is exiting either way once this handler returns.
        try
        {
            await Task.WhenAll(
                _discordBotHost.StopAsync(),
                _runtimeDiagnostics.WaitForStopAsync(),
                _systemMetricsTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            AppLogService.Add("Shutdown cleanup did not finish within the timeout; closing anyway.");
        }
        catch (Exception ex)
        {
            AppLogService.Add("Shutdown cleanup failed: " + ex.Message);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_trayAvailable &&
            _settings.MinimizeToTray &&
            WindowState == System.Windows.WindowState.Minimized)
        {
            Hide();
        }
    }

    internal void SetTrayAvailability(bool available)
    {
        _trayAvailable = available;
    }

    internal void ApplyInitialDesktopState()
    {
        if (!_settings.StartMinimized)
        {
            return;
        }

        WindowState = System.Windows.WindowState.Minimized;
        if (_trayAvailable && _settings.MinimizeToTray)
        {
            Hide();
        }
    }

    internal void RestoreAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }


    internal void RequestSafeExitFromTray()
    {
        RestoreAndActivate();
        Close();
    }

    internal async Task StopAllServersFromTrayAsync()
    {
        if (_bulkActionInProgress)
        {
            System.Windows.MessageBox.Show(
                this,
                "A bulk server action is already running.",
                "Bulk Action Running",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (_isClosing ||
            _windowsSessionEnding ||
            !_trayStopAllGate.Wait(0))
        {
            return;
        }

        try
        {
            await StopAllServersFromTrayCoreAsync();
        }
        finally
        {
            _trayStopAllGate.Release();
        }
    }

    private async Task StopAllServersFromTrayCoreAsync()
    {
        IReadOnlyList<InstalledServer> runningServers;
        try
        {
            runningServers = await LoadRunningServersForExitAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not enumerate servers for tray stop-all: " + ex.Message);
            System.Windows.MessageBox.Show(
                this,
                "WindowsGSH could not determine which servers are running. No stop requests were sent.",
                "Stop All Servers",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (runningServers.Count == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "No managed game servers are currently running.",
                "Stop All Servers",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        RestoreAndActivate();
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"Stop {runningServers.Count} managed game server(s)?\n\n" +
            "WindowsGSH will request graceful shutdown and force-stop a server only if it does not exit.",
            "Stop All Servers",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirmation != System.Windows.MessageBoxResult.Yes)
        {
            AppLogService.Add("Tray stop-all cancelled.");
            return;
        }

        IsEnabled = false;
        _serverRefreshTimer.Stop();
        try
        {
            await CancelActiveOperationsForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            runningServers = await LoadRunningServersForExitAsync();
            foreach (var server in runningServers)
            {
                SetManuallyStopped(server.Id, manuallyStopped: true);
            }

            AppLogService.Add($"Tray stop-all confirmed for {runningServers.Count} managed server(s).");
            await StopRunningServersAsync(runningServers, forceIfNeeded: true, CancellationToken.None);
            await RefreshDiscordPanelsForShutdownAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Add("Tray stop-all failed: " + ex.Message);
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Stop All Servers Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            _serverRefreshTimer.Start();
            await RefreshInstalledServersSafelyAsync("post stop-all refresh");
        }
    }

    private string? UpdateStartupRegistration(bool enabled)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return "WindowsGSH could not determine its executable path, so the startup setting was not changed.";
        }

        try
        {
            _startupRegistrationService.Reconcile(enabled, executablePath);
            return null;
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not update current-user startup registration: " + ex.Message);
            return "Could not update Windows startup registration: " + ex.Message;
        }
    }

    private void ApplyDesktopSettings()
    {
        AccessibilityVisualState.ApplyToApplication(_settings);
        _runtimeDiagnostics.SetEnabled(_settings.RuntimeDiagnosticsEnabled);
        if ((!_trayAvailable || !_settings.MinimizeToTray) && !IsVisible)
        {
            RestoreAndActivate();
        }

        UpdateFirstRunChecklist();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing || _windowsSessionEnding)
        {
            return;
        }

        e.Cancel = true;
        if (_closeDecisionInProgress)
        {
            return;
        }

        _closeDecisionInProgress = true;
        try
        {
            await HandleUserCloseAsync();
        }
        finally
        {
            _closeDecisionInProgress = false;
        }
    }

    private async Task HandleUserCloseAsync()
    {
        IReadOnlyList<InstalledServer> runningServers;
        var activeOperations = _operationManager.GetActive();
        _serverRefreshTimer.Stop();
        try
        {
            runningServers = await LoadRunningServersForExitAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not enumerate servers during exit: " + ex.Message);
            System.Windows.MessageBox.Show(
                "WindowsGSH could not determine which servers are running, so exit was cancelled.\n\n" +
                "Review the app log and try again.",
                "Exit Cancelled",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            _serverRefreshTimer.Start();
            return;
        }

        var initialPlan = _exitPlanner.PlanUserClose(runningServers.Count, activeOperations.Count);
        ApplicationExitChoice? choice = null;
        if (initialPlan.ShouldPrompt)
        {
            var dialog = new ExitDecisionWindow(runningServers.Count, activeOperations.Count)
            {
                Owner = this
            };
            dialog.ShowDialog();
            choice = dialog.Choice;
        }

        var plan = _exitPlanner.PlanUserClose(runningServers.Count, activeOperations.Count, choice);
        if (!plan.ShouldExit)
        {
            AppLogService.Add("WindowsGSH exit cancelled; managed servers remain running.");
            _serverRefreshTimer.Start();
            return;
        }

        _isClosing = true;
        StopSystemMetricsSampling();
        IsEnabled = false;
        if (plan.ShouldStopServers)
        {
            try
            {
                runningServers = await LoadRunningServersForExitAsync();
            }
            catch (Exception ex)
            {
                _isClosing = false;
                StartSystemMetricsSampling();
                IsEnabled = true;
                _serverRefreshTimer.Start();
                AppLogService.Add("Could not recheck running servers before exit: " + ex.Message);
                System.Windows.MessageBox.Show(
                    "WindowsGSH could not recheck running servers after your choice, so exit was cancelled.",
                    "Exit Cancelled",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
        }
        AppLogService.Add(plan.ShouldStopServers
            ? $"Exiting WindowsGSH after stopping {runningServers.Count} managed server(s)."
            : runningServers.Count > 0
                ? $"Exiting WindowsGSH and leaving {runningServers.Count} managed server(s) running."
                : "Exiting WindowsGSH; no managed servers are running.");

        try
        {
            await CancelActiveOperationsForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (plan.ShouldStopServers)
            {
                await StopRunningServersAsync(runningServers, forceIfNeeded: true, CancellationToken.None);
                await RefreshDiscordPanelsForShutdownAsync();
            }

            await _discordBotHost.StopAsync();
        }
        finally
        {
            IsEnabled = true;
            // The original Closing event remains active until this async handler returns.
            // Queue the confirmed close so WPF can finish unwinding that event first.
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
    }

    internal void HandleWindowsSessionEnding(System.Windows.ReasonSessionEnding reason)
    {
        if (_windowsSessionEnding || _isClosing)
        {
            return;
        }

        _windowsSessionEnding = true;
        _isClosing = true;
        StopSystemMetricsSampling();
        IsEnabled = false;
        _serverRefreshTimer.Stop();
        var windowHandle = new WindowInteropHelper(this).Handle;
        var shutdownReasonCreated = windowHandle != IntPtr.Zero &&
            ShutdownBlockReasonCreate(windowHandle, "Stopping managed game servers safely.");
        var source = reason == System.Windows.ReasonSessionEnding.Logoff
            ? ApplicationExitSource.WindowsLogoff
            : ApplicationExitSource.WindowsShutdown;
        AppLogService.Add(
            source == ApplicationExitSource.WindowsLogoff
                ? "Windows is signing out. Attempting bounded graceful shutdown of managed servers."
                : "Windows is shutting down or restarting. Attempting bounded graceful shutdown of managed servers.");

        try
        {
            using var cancellation = new CancellationTokenSource(WindowsSessionEndingStopTimeout);
            var shutdownTask = PrepareForWindowsSessionEndingAsync(reason, cancellation.Token);
            var completed = WaitWithDispatcher(shutdownTask, WindowsSessionEndingHandlerBudget);
            if (!completed)
            {
                cancellation.Cancel();
                AppLogService.Add(
                    $"Windows session-ending budget of {WindowsSessionEndingHandlerBudget.TotalSeconds:0} seconds expired. " +
                    "WindowsGSH will not force-kill remaining game server processes.");
                return;
            }

            try
            {
                shutdownTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                AppLogService.Add("Windows session-ending graceful shutdown reached its deadline.");
            }
            catch (Exception ex)
            {
                AppLogService.Add("Windows session-ending graceful shutdown failed: " + ex.Message);
            }
        }
        finally
        {
            if (shutdownReasonCreated)
            {
                ShutdownBlockReasonDestroy(windowHandle);
            }
        }
    }

    private async Task PrepareForWindowsSessionEndingAsync(
        System.Windows.ReasonSessionEnding reason,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledServer> runningServers;
        try
        {
            runningServers = await GetRunningServersForWindowsSessionEndingAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not enumerate servers during Windows session ending: " + ex.Message);
            return;
        }

        var plan = _exitPlanner.PlanWindowsSessionEnding(
            runningServers.Count,
            _operationManager.GetActive().Count,
            reason == System.Windows.ReasonSessionEnding.Logoff);
        AppLogService.Add(
            $"{plan.Source}: {runningServers.Count} managed server(s) selected for graceful shutdown.");

        await CancelActiveOperationsForExitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        if (plan.ShouldStopServers)
        {
            await StopRunningServersAsync(runningServers, forceIfNeeded: false, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _discordBotHost.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
    }

    // Generous but bounded: this is a rare, user-triggered/OS-triggered path (not the 3-second
    // refresh cadence), so a longer ceiling than the regular per-server load timeout is acceptable in
    // exchange for giving a genuinely-slow-but-working enumeration a real chance to finish before
    // falling back.
    private static readonly TimeSpan ShutdownEnumerationTimeout = TimeSpan.FromSeconds(10);

    private async Task<IReadOnlyList<InstalledServer>> LoadRunningServersForExitAsync()
    {
        // User-close and tray Stop All can be safely aborted and retried by the user - so ANY failed
        // fresh enumeration throws here, even if a last-known-good snapshot exists. A snapshot only
        // proves what was true when it was recorded: a server it shows offline may have started since,
        // a server may have been added since, or a server it shows running may have changed state
        // while its own module was hung - exactly the scenario that makes fresh enumeration
        // untrustworthy in the first place. The existing try/catch around every call site of this
        // method already reports the failure and cancels the exit/stop-all attempt.
        var servers = await GetServersForShutdownWithFallbackAsync("exit", allowStaleFallback: false, ShutdownEnumerationTimeout);
        return _shutdownPlanner.SelectStopCandidates(servers);
    }

    // Shared by both the user-close/tray-stop-all path above and the Windows-session-ending path
    // below - both need the identical bounded-enumeration behavior for the same reason. LoadForShutdown
    // deliberately avoids GetDisplayInfo/IsInstallValid, but ServerProcessLocator.IsRunning still reads
    // module.Runtime (an arbitrary property getter), so a broken module can still hang it - and since
    // these callers would otherwise call it synchronously (often on the UI thread), an unbounded hang
    // would freeze the whole app with no way to even cancel. What happens next on a failed fresh
    // enumeration differs sharply between the two paths - see ShutdownPlanner.ResolveShutdownServers
    // (Core, unit tested), which makes the actual decision based on allowStaleFallback. enumerationTimeout
    // is caller-supplied (rather than a single shared constant) because the two paths have very
    // different budgets available: the exit path has no overall deadline, but Windows session ending
    // must reserve most of its cooperative deadline for the actual stop sequence that follows.
    private async Task<IReadOnlyList<InstalledServer>> GetServersForShutdownWithFallbackAsync(
        string context,
        bool allowStaleFallback,
        TimeSpan enumerationTimeout)
    {
        var servers = await _installedServerLoader.TryLoadForShutdownAsync(enumerationTimeout);
        if (servers == null)
        {
            AppLogService.Add(
                $"Could not determine running servers before {context} within {enumerationTimeout.TotalSeconds:0}s (a module may be unresponsive).");
        }

        return _shutdownPlanner.ResolveShutdownServers(
            servers,
            _serverListViewModel.LastKnownGoodShutdownSnapshot,
            allowStaleFallback);
    }

    private async Task StopRunningServersAsync(
        IReadOnlyList<InstalledServer> runningServers,
        bool forceIfNeeded,
        CancellationToken cancellationToken)
    {
        var stopTasks = runningServers
            .Select(server => StopServerForShutdownAsync(server, forceIfNeeded, cancellationToken))
            .ToArray();
        await Task.WhenAll(stopTasks);
    }

    private async Task RefreshDiscordPanelsForShutdownAsync()
    {
        if (!_discordBotHost.IsRunning)
        {
            return;
        }

        try
        {
            var servers = await _installedServerLoader.LoadAsync();
            var offlineServers = servers.Select(_shutdownPlanner.CreateDiscordOfflineSnapshot).ToArray();
            await _discordBotHost.RefreshPanelsAsync(offlineServers);
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not update Discord panels during shutdown: " + ex.Message);
        }
    }

    private async Task StopServerForShutdownAsync(
        InstalledServer server,
        bool forceIfNeeded,
        CancellationToken cancellationToken)
    {
        try
        {
            var module = GetModule(server);
            var instance = ServerInstanceFactory.Load(server);
            AppLogService.Add($"Stopping {server.Name} before shutdown.", server.Id);
            _runtimeTracker.MarkExpectedProcessExits(module, instance);
            _runtimeTracker.CancelLogTail(server.Id);

            try
            {
                if (forceIfNeeded)
                {
                    await module.StopAsync(instance, cancellationToken);
                }
                else if (module is IModuleGracefulStopCapability gracefulStop)
                {
                    await gracefulStop.StopGracefullyAsync(instance, cancellationToken);
                }
                else
                {
                    AppLogService.Add(
                        $"{server.Name} module does not expose a guaranteed graceful-only stop path; no stop action was sent during Windows session ending.",
                        server.Id);
                }
            }
            catch (Exception ex)
            {
                AppLogService.Add($"Graceful shutdown failed for {server.Name}: {ex.Message}", server.Id);
            }

            if (forceIfNeeded)
            {
                _runtimeTracker.StopAddonProcesses(server.Id);
            }
            if (forceIfNeeded)
            {
                await ForceStopIfStillRunningAsync(module, instance, server.Name, server.Id, cancellationToken);
            }
            else
            {
                // Regression guard for a P1 finding: a stale-snapshot fallback candidate can be
                // exactly the server whose module hung during enumeration in the first place
                // (ServerProcessLocator.IsRunning reads the same arbitrary module.Runtime getter that
                // made fresh enumeration time out). A direct, unbounded IsRunning call here would hang
                // this method - and the whole Windows session-ending handler - indefinitely in the
                // exact module-failure scenario the fallback exists to survive. TryIsRunningAsync bounds
                // each probe independently; a timed-out/faulted probe (null) is treated the same as
                // "still running" rather than guessed as stopped, so the loop keeps polling until the
                // module answers cleanly or the outer cancellationToken (the Windows session-ending
                // deadline) expires - caught by the OperationCanceledException handler below.
                while (await ServerProcessLocator.TryIsRunningAsync(
                    module,
                    instance.InstallPath,
                    ProcessProbeTimeout,
                    cancellationToken) != false)
                {
                    await Task.Delay(250, cancellationToken);
                }

                AppLogService.Add($"{server.Name} stopped gracefully.", server.Id);
            }

            // This path (app exit / Windows session ending) never goes through
            // ServerLifecycleService.StopAsync/ForceStopAsync - it calls the module directly above -
            // so ServerLifecycleService's own AfterStopAsync hook (where this call also lives for a
            // normal stop, including that method's own process-exit confirmation) never fires here.
            // Without this, a server using MapOnStartRemoveOnStop would keep its router mapping in
            // place through an app/session shutdown, contrary to its own configured policy and
            // potentially exposing a later process that binds the same port. Isolated in its own
            // try/catch so a mapping-removal failure is never misreported as this server having
            // failed to stop.
            //
            // The graceful (non-force) branch above already polls until the process is confirmed
            // gone (or the deadline throws first), so this re-check is normally instant there; it
            // matters for the forceIfNeeded branch, where ForceStopIfStillRunningAsync can still
            // finish with the process alive (a genuinely stuck process surviving even a kill) -
            // removing forwarding for a server that might still be running would be wrong.
            if (await ServerProcessLocator.TryIsRunningAsync(
                    module, instance.InstallPath, ProcessProbeTimeout, cancellationToken) != false)
            {
                AppLogService.Add(
                    $"UPnP port mapping removal skipped for {server.Name}: the process could not be confirmed stopped.",
                    server.Id);
            }
            else
            {
                try
                {
                    await _upnpMappingLifecycleService.UnmapOnStopAsync(server, instance, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogService.Add($"UPnP port mapping removal failed for {server.Name}: {ex.Message}", server.Id);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLogService.Add(
                $"{server.Name} did not finish graceful shutdown before the Windows deadline; it was not force-killed.",
                server.Id);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Failed to stop {server.Name} during shutdown: {ex.Message}", server.Id);
        }
    }

    private async Task<IReadOnlyList<InstalledServer>> GetRunningServersForWindowsSessionEndingAsync()
    {
        // Windows session ending cannot be aborted/retried the way user-close can - proceed with
        // whatever confirmed candidates are available (even none) rather than throwing.
        var servers = await GetServersForShutdownWithFallbackAsync(
            "Windows session ending",
            allowStaleFallback: true,
            WindowsSessionEndingEnumerationTimeout);
        return _shutdownPlanner.SelectStopCandidates(servers);
    }

    private async Task CancelActiveOperationsForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var active = _operationManager.GetActive();
        foreach (var operation in active)
        {
            _operationManager.Cancel(operation.ServerId);
        }

        if (active.Count == 0)
        {
            return;
        }

        AppLogService.Add($"Cancellation requested for {active.Count} active operation(s) before exit.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_operationManager.IsAnyOperationRunning() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (_operationManager.IsAnyOperationRunning())
        {
            AppLogService.Add("One or more operations did not acknowledge cancellation before exit continued.");
        }
    }

    private static bool WaitWithDispatcher(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = timeout
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        _ = task.ContinueWith(
            _ => System.Windows.Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return task.IsCompleted;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmEndSession = 0x0016;
        if (message == WmEndSession && wParam == IntPtr.Zero && _windowsSessionEnding)
        {
            Dispatcher.BeginInvoke(new Action(() => _ = RestoreAfterCancelledWindowsSessionEndingAsync()));
        }

        return IntPtr.Zero;
    }

    private async Task RestoreAfterCancelledWindowsSessionEndingAsync()
    {
        if (!_windowsSessionEnding)
        {
            return;
        }

        _windowsSessionEnding = false;
        _isClosing = false;
        StartSystemMetricsSampling();
        IsEnabled = true;
        _serverRefreshTimer.Start();

        foreach (var server in await GetRunningServersForWindowsSessionEndingAsync())
        {
            try
            {
                _runtimeTracker.ClearExpectedProcessExits(GetModule(server), ServerInstanceFactory.Load(server));
            }
            catch
            {
            }
        }

        AppLogService.Add("Windows session ending was cancelled. WindowsGSH resumed normal operation.");
        _ = RefreshInstalledServersSafelyAsync("post-cancelled-shutdown refresh");
        _ = StartDiscordBotIfEnabledAsync();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    private static async Task ForceStopIfStillRunningAsync(
        IGameServerModule module,
        ServerInstance instance,
        string serverName,
        string serverId,
        CancellationToken cancellationToken)
    {
        await Task.Delay(750, cancellationToken);
        if (!ServerProcessLocator.IsRunning(module, instance.InstallPath))
        {
            AppLogService.Add($"{serverName} stopped.", serverId);
            return;
        }

        AppLogService.Add($"{serverName} is still running after graceful shutdown; force stopping.", serverId);
        await ServerForceStopper.KillAsync(
            module,
            instance,
            cancellationToken,
            new ServerForceStopOptions(serverId, serverName, GracefulStopAttempted: true, AppLogService.Add));
        await Task.Delay(250, cancellationToken);

        if (ServerProcessLocator.IsRunning(module, instance.InstallPath))
        {
            AppLogService.Add($"{serverName} may still be running after force stop.", serverId);
        }
        else
        {
            AppLogService.Add($"{serverName} force stopped.", serverId);
        }
    }

    private void HideButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        WindowState = System.Windows.WindowState.Minimized;
    }

    private void AddServerButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var window = new InstallServerWindow
        {
            Owner = this
        };

        window.ShowDialog();
        _ = RefreshInstalledServersViewOnlyAsync();
    }

    private void ImportExistingServerButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var window = new ExistingServerImportWindow
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _ = RefreshInstalledServersViewOnlyAsync();
        }
    }

    private void ImportWindowsGsmButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var window = new WindowsGsmServerImportWindow
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _ = RefreshInstalledServersViewOnlyAsync();
        }
    }

    private async void BulkActionsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_bulkActionWindowOpen || _bulkActionInProgress)
        {
            System.Windows.MessageBox.Show(
                this,
                "The Bulk Actions window is already open or a batch is running.",
                "Bulk Actions",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        _bulkActionWindowOpen = true;
        try
        {
            var servers = await LoadServersInBackgroundAsync();
            var window = new BulkActionsWindow(
                servers.Select(CreateBulkActionRow).ToArray(),
                ExecuteBulkServerActionAsync,
                CancelBulkServerActions,
                running => _bulkActionInProgress = running)
            {
                Owner = this
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogService.Add("Could not open Bulk Actions: " + ex.Message);
            System.Windows.MessageBox.Show(
                this,
                "Could not open Bulk Actions: " + ex.Message,
                "Bulk Actions",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _bulkActionWindowOpen = false;
            _bulkActionInProgress = false;
            await RefreshInstalledServersViewOnlyAsync();
        }
    }

    private BulkActionServerRow CreateBulkActionRow(InstalledServer server)
    {
        var autoStart = false;
        var configReady = File.Exists(server.ConfigPath);
        if (configReady)
        {
            try
            {
                autoStart = ServerConfigAppSettings
                    .FromConfigJson(File.ReadAllText(server.ConfigPath))
                    .Automation
                    .AutoStart;
            }
            catch
            {
                configReady = false;
            }
        }

        var supportsUpdate = false;
        var supportsBackups = false;
        try
        {
            var capabilities = ModuleDescriptor.GetEffectiveCapabilities(GetModule(server));
            supportsUpdate = capabilities.SupportsUpdate;
            supportsBackups = capabilities.SupportsBackups;
        }
        catch
        {
        }

        return new BulkActionServerRow
        {
            ServerId = server.Id,
            ServerName = server.Name,
            CurrentState = server.IsOperationRunning ? server.OperationText : server.CurrentStatusText,
            ConfigExists = configReady,
            CanStart = server.CanStart,
            CanStop = server.CanStop,
            IsBusy = server.IsOperationRunning || _operationManager.Get(server.Id)?.IsActive == true,
            AutoStartEnabled = autoStart,
            SupportsUpdate = supportsUpdate,
            SupportsBackups = supportsBackups,
            IsSelected = true
        };
    }

    private async Task<ServerActionExecutionOutcome> ExecuteBulkServerActionAsync(
        BulkServerAction action,
        string serverId,
        CancellationToken cancellationToken)
    {
        var server = await BulkServerActionTargetLoader.LoadAsync(
            serverId,
            async token =>
            {
                var servers = await LoadServersInBackgroundAsync();
                token.ThrowIfCancellationRequested();
                return servers;
            },
            target => target.Id,
            cancellationToken);
        if (server == null)
        {
            return ServerActionExecutionOutcome.Skipped("Server is no longer installed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = ModuleDescriptor.GetEffectiveCapabilities(GetModule(server));
        return action switch
        {
            BulkServerAction.StartSelected or BulkServerAction.StartAutoStart =>
                server.CanStart
                    ? await _serverOperations.StartManualAsync(server)
                    : ServerActionExecutionOutcome.Skipped("Server is no longer startable."),
            BulkServerAction.StopSelected or BulkServerAction.StopAllRunning =>
                server.CanStop
                    ? await _serverOperations.StopManualForBulkAsync(server)
                    : ServerActionExecutionOutcome.Skipped("Server is no longer running."),
            BulkServerAction.RestartSelected =>
                server.CanStop || server.CanStart
                    ? await _serverOperations.RestartManualAsync(server)
                    : ServerActionExecutionOutcome.Skipped("Server is no longer restartable."),
            BulkServerAction.UpdateSelectedOffline =>
                !capabilities.SupportsUpdate
                    ? ServerActionExecutionOutcome.Skipped("The module does not support updates.")
                    : server.CanStop
                        ? ServerActionExecutionOutcome.Skipped("Server was started after the preview; stop it before updating.")
                        : server.CanStart
                            ? await _serverOperations.UpdateManualAsync(server)
                            : ServerActionExecutionOutcome.Skipped("Server is no longer updateable."),
            BulkServerAction.BackupSelected =>
                !capabilities.SupportsBackups
                    ? ServerActionExecutionOutcome.Skipped("The module does not support backups.")
                    : server.CanStop
                        ? ServerActionExecutionOutcome.Skipped("Server was started after the preview; stop it before bulk backup.")
                        : await _serverOperations.BackupManualAsync(server),
            _ => ServerActionExecutionOutcome.Skipped("Unsupported bulk action.")
        };
    }

    private void CancelBulkServerActions(IReadOnlyList<string> serverIds)
    {
        foreach (var serverId in serverIds)
        {
            _operationManager.Cancel(serverId);
        }
    }

    private bool EnsureBulkActionNotRunning()
    {
        if (!_bulkActionInProgress)
        {
            return true;
        }

        System.Windows.MessageBox.Show(
            this,
            "Wait for the active bulk server action to finish or cancel it from the Bulk Actions window.",
            "Bulk Action Running",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
        return false;
    }

    private void FirstRunReadinessButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ShowReadinessCheck();
    }

    private void NavButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string navigationId })
        {
            NavigateTo(navigationId);
        }
    }

    private void NavigateTo(string navigationId)
    {
        foreach (var item in _navigationItems)
        {
            item.IsSelected = string.Equals(item.Id, navigationId, StringComparison.OrdinalIgnoreCase);
        }

        ServersView.Visibility = IsNavigationSelected(ServersNavigationId)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        LogsView.Visibility = IsNavigationSelected(LogsNavigationId)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        ConfigView.Visibility = IsNavigationSelected(ConfigNavigationId)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        ModuleManagementView.Visibility = IsNavigationSelected(ModulesNavigationId)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        HostHealthView.Visibility = IsNavigationSelected(HostHealthNavigationId)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        if (IsNavigationSelected(ConfigNavigationId))
        {
            ConfigView.LoadControls();
        }

        if (IsNavigationSelected(LogsNavigationId))
        {
            RefreshAppLogText();
        }

        if (IsNavigationSelected(ModulesNavigationId))
        {
            ModuleManagementView.RefreshModules();
        }

        if (IsNavigationSelected(HostHealthNavigationId))
        {
            HostHealthView.RefreshContext(
                _serverListViewModel.LastVisibleServers,
                _settings.LastKnownPublicIp);

            var history = _systemMetricHistory.GetHistory();
            if (history.Count > 0)
            {
                HostHealthView.UpdateMetrics(history[^1], history);
            }
        }
    }

    private bool IsNavigationSelected(string navigationId)
    {
        return _navigationItems.Any(item =>
            item.IsSelected &&
            string.Equals(item.Id, navigationId, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowReadinessCheck()
    {
        new ReadinessCheckWindow(_readinessCheckService, HandleReadinessNavigation) { Owner = this }.ShowDialog();
    }

    private void HandleReadinessNavigation(Core.Readiness.ReadinessAction action)
    {
        switch (action)
        {
            case Core.Readiness.ReadinessAction.OpenJavaSettings:
                NavigateTo(ConfigNavigationId);
                ConfigView.ShowJavaSettings();
                break;

            case Core.Readiness.ReadinessAction.OpenModuleManagement:
                NavigateTo(ModulesNavigationId);
                break;
        }
    }

    private void ShowOperationHistory()
    {
        new OperationHistoryWindow { Owner = this }.ShowDialog();
    }

    private void InfoButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InstalledServer server } || !server.CanShowInfo)
        {
            return;
        }

        try
        {
            var window = new ServerInfoWindow(
                server,
                _installedServerLoader,
                _upnpMappingLifecycleService,
                _runtimeTracker.HasLiveMonitoredProcess)
            {
                Owner = this
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            // ServerInfoWindow's constructor calls several module methods synchronously
            // (GetConsoleLogPath, GetAddonDefinitions/GetAddonStatus, Capabilities, GetSteamInstall,
            // etc.) with no isolation of its own - a misbehaving module can throw there. Uncaught,
            // that propagates out of this click handler to WPF's DispatcherUnhandledException
            // handler, which deliberately sets e.Handled = false and terminates the whole app over
            // one server's Info window failing to open. Matches the same catch-log-and-tell-the-user
            // pattern BulkActionsButton_Click already uses when a window fails to open.
            //
            // ex.Message is never surfaced here - those module calls receive this server's real
            // settings (instance.Settings), so a poorly written or malicious module's exception
            // message could embed a password/token/RCON value, the same "arbitrary module code is
            // exactly as untrusted as its exception text" reasoning ServerHealthService.cs's own
            // hardening already established for this codebase. A fixed message is used instead,
            // regardless of what the real exception says; the technical detail still goes to the
            // app log's structured entry via ex (not ex.Message) for support/debugging.
            AppLogService.Add($"Could not open Server Info for {server.Name} due to an internal error ({ex.GetType().Name}).", server.Id);
            System.Windows.MessageBox.Show(
                this,
                $"Could not open Server Info for {server.Name} due to an internal error. Check the app log for details.",
                "Server Info",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async void VerifyServerButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InstalledServer server } button)
        {
            return;
        }

        if (!EnsureBulkActionNotRunning())
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var module = GetModule(server);
            if (server.CanStop && !module.AllowsOnlineVerification())
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Stop the server before verifying its files.",
                    "Server Running",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var instance = ServerInstanceFactory.Load(server);
            var steam = ReadConfigObject(server.ConfigPath, "steam");
            var branch = ReadString(steam, "branch");
            var branchPassword = _steamBranchPasswordStore.Resolve(
                server.Id,
                ReadString(steam, "branchPassword"),
                ReadString(steam, "branchPasswordRef"));

            var result = await _verifyService.VerifyAsync(
                new ServerVerifyRequest(
                    module,
                    instance,
                    ServerIsRunning: server.CanStop,
                    SteamBranch: branch,
                    SteamBranchPassword: branchPassword,
                    Progress: AppLogService.CreateProgress(server.Id),
                    UseOperationCoordinator: true));

            AppLogService.Add(result.Message, server.Id);
            await RefreshInstalledServersSafelyAsync("post-verify refresh");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void DeleteServerButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InstalledServer server } button)
        {
            return;
        }

        if (server.CanStop)
        {
            System.Windows.MessageBox.Show(
                "Stop the server before deleting it.",
                "Server Running",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Are you sure you want to remove {server.Name}?\n\nThis will delete this server folder:\n{server.ServerFolder}",
            "Delete Server",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginServerOperation(server, ServerOperationKind.Delete, out operation))
            {
                return;
            }

            SetOperationStatus(server.Id, "Deleting");
            AppLogService.Add($"Deleting {server.Name}.", server.Id);
            _runtimeTracker.TryMarkExpectedProcessExits(server, GetModule);
            _runtimeTracker.StopLogTail(server.Id);
            _runtimeTracker.StopAddonProcesses(server.Id);
            var deleteResult = await _deleteService.DeleteAsync(
                new ServerDeleteRequest(
                    server,
                    new ServerDeleteOptions(RemoveFirewallRules: true, RequireStopped: true),
                    IsRunning: item => item.CanStop,
                    RemoveFirewallRules: RemoveFirewallRulesForDelete,
                    UseOperationCoordinator: false),
                operation!.CancellationToken);
            foreach (var step in deleteResult.Steps)
            {
                if (!string.IsNullOrWhiteSpace(step.Message))
                {
                    AppLogService.Add(step.Message, server.Id);
                }
            }

            if (!deleteResult.Success)
            {
                throw new InvalidOperationException(deleteResult.Message);
            }

            // The server's config (and whatever UpnpMappingPolicy it carried) is gone once deletion
            // succeeds, and MapOnStartAsync/UnmapOnStopAsync only ever run as part of that same
            // server starting or stopping - which can never happen again. Without this, a mapping
            // owned by a deleted server would survive on the router and in the ownership registry
            // indefinitely. Policy-independent and reads nothing from the (already-deleted) server
            // folder, so it runs regardless of deletion order; isolated in its own try/catch so a
            // removal failure is never reported as the delete itself having failed.
            try
            {
                await _upnpMappingLifecycleService.RemoveAllOwnedMappingsAsync(server, operation!.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogService.Add($"Could not remove UPnP port mapping(s) for deleted server {server.Name}: {ex.Message}", server.Id);
            }

            // Numeric server IDs are reused. Do not let this deleted server's in-session policy
            // override become the policy of a future, unrelated server installed with the same ID.
            _upnpMappingLifecycleService.ClearCurrentPolicy(server.Id);

            CleanupDeletedServerData(server);
            AppLogService.Add($"{server.Name} deleted.", server.Id);
            operation?.Dispose();
            await RefreshInstalledServersViewOnlyAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add($"Failed to delete {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }

            button.IsEnabled = true;
        }
    }

    private void CleanupDeletedServerData(InstalledServer server)
    {
        _consoleService.RemoveServerState(server.Id);
        // PruneStaleServerMetrics already reclaims this within ~5 minutes (a server that stops
        // being sampled falls behind its stale threshold on the next maintenance cycle), plus the
        // service's own 50-series cap - this is just for immediate cleanup on delete, not to close
        // a leak.
        _installedServerLoader.MetricsService.RemoveSeries(server.Id);

        try
        {
            _discordRepository.DeleteServerData(server.Id);
            _discordWebhookStore.ClearForServer(server.Id);
            AppLogService.Add($"Cleared saved Discord settings for deleted server {server.Name}.", server.Id);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Could not clear saved Discord settings for deleted server {server.Name}: {ex.Message}", server.Id);
        }
    }

    private int RemoveFirewallRulesForDelete(InstalledServer server)
    {
        var module = _moduleRegistry.GetModules()
            .FirstOrDefault(item => string.Equals(item.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (module == null)
        {
            AppLogService.Add($"Skipped firewall rule removal for {server.Name}: module not found ({server.ModuleId}).", server.Id);
            return 0;
        }

        return _firewallService.RemoveManagedRules(server, module);
    }

    private async Task RunCronSchedulesAsync(IReadOnlyList<InstalledServer> servers)
    {
        var serverById = servers.ToDictionary(server => server.Id, StringComparer.Ordinal);
        var plan = _scheduleRunner.PlanDueActions(new ServerScheduleRunRequest(
            _isClosing,
            servers.Select(CreateScheduleCandidate).ToArray()));
        foreach (var log in plan.LogMessages)
        {
            AppLogService.Add(log.Message, log.ServerId);
        }

        var dueActions = plan.DueActions
            .Where(action => serverById.ContainsKey(action.ServerId))
            .Select(action => new CronServerAction(
                serverById[action.ServerId],
                action.Action,
                action.Command,
                action.ParametersJson,
                action.WorkingDirectory,
                action.TimeoutSeconds))
            .ToArray();

        if (dueActions.Length == 0)
        {
            return;
        }

        // When parallel operations are enabled, dispatch without awaiting: CronSchedule.IsMatch only
        // ever asks "is it due right now," with no
        // catch-up, and this method used to await every due action to completion before returning.
        // Since RunServerMaintenanceAsync refuses to re-enter while a previous pass is still running,
        // one slow action (e.g. a 15-minute update) used to block every OTHER server's due actions
        // from even being evaluated - not just delayed, silently skipped forever, since the exact
        // matching minute had already passed by the time planning ran again. Firing the batch off in
        // the background lets planning happen on every tick regardless of how long the previous
        // batch's actions take. Per-server pile-up is already prevented independently: every
        // RunCronXAsync method behind RunCronActionAsync goes through a TryBegin guard before doing
        // any real work - ServerOperationsController's own for start/stop/update/backup/restart, and
        // this class's TryBeginServerOperation (backed by the same shared ServerOperationManager,
        // keyed by server id regardless of operation kind) for rcon/console/api/external - so a
        // second due attempt for a server that's still busy logs "another operation is active" and
        // returns immediately rather than piling up.
        if (_settings.ParallelServerOperations)
        {
            _ = RunDueCronActionsInBackgroundAsync(dueActions);
            return;
        }

        // In serial mode this must remain part of the maintenance await chain. Per-server guards
        // only serialize operations for the same server ID and cannot prevent cron work for one
        // server from overlapping auto-update or automation work for another server.
        await RunDueCronActionsInBackgroundAsync(dueActions);
    }

    private async Task RunDueCronActionsInBackgroundAsync(IReadOnlyList<CronServerAction> dueActions)
    {
        try
        {
            if (_settings.ParallelServerOperations)
            {
                await Task.WhenAll(dueActions.Select(RunCronActionSafelyAsync));
            }
            else
            {
                foreach (var action in dueActions)
                {
                    await RunCronActionSafelyAsync(action);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Cron schedule execution failed: {ex.Message}");
        }
    }

    private async Task RunCronActionSafelyAsync(CronServerAction action)
    {
        try
        {
            await RunCronActionAsync(action);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Cron {action.Action} failed for {action.Server.Name}: {ex.Message}", action.Server.Id);
        }
    }

    private ServerScheduleCandidate CreateScheduleCandidate(InstalledServer server)
    {
        var configExists = File.Exists(server.ConfigPath);
        ServerScheduleConfig schedules;
        if (!configExists)
        {
            schedules = ServerScheduleConfig.Empty;
        }
        else
        {
            try
            {
                schedules = ServerScheduleConfig.FromConfigJson(File.ReadAllText(server.ConfigPath));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                AppLogService.Add($"{server.Name} schedules could not be read: {ex.Message}", server.Id);
                schedules = ServerScheduleConfig.Empty;
            }
        }

        var operation = _operationManager.Get(server.Id);
        return new ServerScheduleCandidate(
            server.Id,
            server.Name,
            configExists,
            schedules,
            _openConfigServerIds.Contains(server.Id),
            server.CanStop,
            operation?.IsActive == true ? operation.DisplayText : null);
    }

    private async Task RunCronActionAsync(CronServerAction action)
    {
        switch (action.Action)
        {
            case "start":
                await _serverOperations.RunCronStartAsync(action.Server);
                break;
            case "stop":
                await _serverOperations.RunCronStopAsync(action.Server);
                break;
            case "update":
                await _serverOperations.RunCronUpdateAsync(action.Server);
                break;
            case "backup":
                await _serverOperations.RunCronBackupAsync(action.Server);
                break;
            case "restart":
                await _serverOperations.RunCronRestartAsync(action.Server);
                break;
            case "rcon":
                await RunCronRconAsync(action.Server, action.Command ?? string.Empty);
                break;
            case "console":
                await RunCronConsoleCommandAsync(action.Server, action.Command ?? string.Empty);
                break;
            case "api":
                await RunCronApiActionAsync(action.Server, action.Command ?? string.Empty, action.ParametersJson ?? "{}");
                break;
            case "external":
                await RunCronExternalCommandAsync(
                    action.Server,
                    action.Command ?? string.Empty,
                    action.ParametersJson ?? string.Empty,
                    action.WorkingDirectory ?? string.Empty,
                    action.TimeoutSeconds);
                break;
        }
    }

    private async Task RunCronExternalCommandAsync(
        InstalledServer server,
        string executablePath,
        string arguments,
        string workingDirectory,
        int timeoutSeconds)
    {
        var executableName = string.IsNullOrWhiteSpace(executablePath)
            ? "(missing executable)"
            : Path.GetFileName(executablePath);
        if (!_settings.ExternalScheduledCommandsEnabled)
        {
            AppLogService.Add(
                $"External command audit: skipped {executableName} for {server.Name}; external scheduled commands are disabled.",
                server.Id);
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        IReadOnlyList<string> redactedValues = [];
        try
        {
            if (!TryBeginServerOperation(server, ServerOperationKind.ExternalCommand, out operation, logBusy: false))
            {
                AppLogService.Add(
                    $"External command audit: skipped {executableName} for {server.Name}; another server operation is active.",
                    server.Id);
                return;
            }

            SetOperationStatus(server.Id, "Running external command");
            AppLogService.Add($"External command audit: starting {executableName} for {server.Name}.", server.Id);
            var configJson = File.Exists(server.ConfigPath) ? File.ReadAllText(server.ConfigPath) : "{}";
            redactedValues = ExternalCommandRunner.ExtractSecretLikeValues(configJson);
            var result = await _externalCommandRunner.RunAsync(
                new ExternalCommandRunRequest(
                    executablePath,
                    arguments,
                    workingDirectory,
                    timeoutSeconds <= 0 ? 300 : timeoutSeconds,
                    _settings.ExternalCommandAllowedPaths,
                    redactedValues,
                    line =>
                    {
                        AppLogService.Add($"External stdout: {line}", server.Id);
                        _consoleService.Add(server.Id, line);
                    },
                    line =>
                    {
                        AppLogService.Add($"External stderr: {line}", server.Id);
                        _consoleService.Add(server.Id, line);
                    }),
                operation!.CancellationToken);

            if (result.TimedOut)
            {
                throw new TimeoutException($"External command exceeded its {Math.Max(1, timeoutSeconds)} second timeout.");
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"External command exited with code {result.ExitCode}.");
            }

            AppLogService.Add(
                $"External command audit: completed {executableName} for {server.Name} with exit code 0.",
                server.Id);
        }
        catch (OperationCanceledException)
        {
            AppLogService.Add($"External command audit: cancelled {executableName} for {server.Name}.", server.Id);
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add(
                $"External command audit: failed {executableName} for {server.Name}: {ExternalCommandRunner.Redact(ex.Message, redactedValues)}",
                server.Id);
            _consoleService.Add(server.Id, "External command failed: " + ExternalCommandRunner.Redact(ex.Message, redactedValues));
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task RunCronRconAsync(InstalledServer server, string command)
    {
        if (!server.CanStop)
        {
            AppLogService.Add($"Cron RCON skipped for {server.Name}: server is offline.", server.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var module = GetModule(server);
        var instance = ServerInstanceFactory.Load(server);
        if (!ModuleRconAvailability.IsRconEnabled(module, instance.Settings))
        {
            AppLogService.Add($"Cron RCON skipped for {server.Name}: RCON is disabled.", server.Id);
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginServerOperation(server, ServerOperationKind.Rcon, out operation, logBusy: false))
            {
                return;
            }

            SetOperationStatus(server.Id, "Running RCON");
            AppLogService.Add($"Cron RCON triggered for {server.Name}: {command}", server.Id);
            _consoleService.Add(server.Id, $"> cron rcon {command}");
            var response = await module.ExecuteRconCommandAsync(
                instance,
                command,
                operation!.CancellationToken);
            foreach (var line in response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                AppLogService.Add($"RCON: {line}", server.Id);
                _consoleService.Add(server.Id, line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add($"Cron RCON failed for {server.Name}: {ex.Message}", server.Id);
            _consoleService.Add(server.Id, "Cron RCON failed: " + ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task RunCronConsoleCommandAsync(InstalledServer server, string command)
    {
        if (!server.CanStop)
        {
            AppLogService.Add($"Cron console command skipped for {server.Name}: server is offline.", server.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            var module = GetModule(server);
            if (!ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(module))
            {
                AppLogService.Add($"Cron console command skipped for {server.Name}: {ConsoleInputStrategyPolicy.GetCommandUnavailableMessage(module)}", server.Id);
                return;
            }

            if (!TryBeginServerOperation(server, ServerOperationKind.ConsoleCommand, out operation, logBusy: false))
            {
                return;
            }

            SetOperationStatus(server.Id, "Running console command");
            AppLogService.Add($"Cron console command triggered for {server.Name}: {command}", server.Id);
            _consoleService.Add(server.Id, $"> cron console {command}");
            var response = await _consoleService.ExecuteModuleCommandAsync(
                module,
                ServerInstanceFactory.Load(server),
                command,
                operation!.CancellationToken);
            foreach (var line in response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                AppLogService.Add($"Console: {line}", server.Id);
                _consoleService.Add(server.Id, line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add($"Cron console command failed for {server.Name}: {ex.Message}", server.Id);
            _consoleService.Add(server.Id, "Cron console command failed: " + ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task RunCronApiActionAsync(InstalledServer server, string actionKey, string parametersJson)
    {
        if (!server.CanStop)
        {
            AppLogService.Add($"Cron API skipped for {server.Name}: server is offline.", server.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            var module = GetModule(server);
            if (module is not IModuleApiActionCapability api)
            {
                AppLogService.Add($"Cron API skipped for {server.Name}: module does not support API actions.", server.Id);
                return;
            }

            var connection = api.GetApiConnection();
            var action = api.GetApiActions().FirstOrDefault(candidate => string.Equals(candidate.Key, actionKey, StringComparison.OrdinalIgnoreCase));
            if (connection == null || action == null)
            {
                AppLogService.Add($"Cron API skipped for {server.Name}: action '{actionKey}' is not available.", server.Id);
                return;
            }

            if (!TryBeginServerOperation(server, ServerOperationKind.ApiAction, out operation, logBusy: false))
            {
                return;
            }

            SetOperationStatus(server.Id, "Running API action");
            AppLogService.Add($"Cron API triggered for {server.Name}: {action.Label}", server.Id);
            _consoleService.Add(server.Id, $"> cron api {action.Key}");
            var response = await _apiActionClient.ExecuteAsync(
                ServerInstanceFactory.Load(server),
                connection,
                action,
                ParseParameterObject(parametersJson),
                operation!.CancellationToken);
            foreach (var line in response.Body.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                AppLogService.Add($"API: {line}", server.Id);
                _consoleService.Add(server.Id, line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add($"Cron API failed for {server.Name}: {ex.Message}", server.Id);
            _consoleService.Add(server.Id, "Cron API failed: " + ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> ParseParameterObject(string parametersJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("API action parameters must be a JSON object.");
        }

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static object? ToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object or JsonValueKind.Array => element.Clone(),
            _ => element.GetRawText()
        };
    }

    private Task RunScheduledAutoUpdatesAsync(IReadOnlyList<InstalledServer> servers)
    {
        if (_settings.AutoUpdateIntervalMinutes <= 0 || _isClosing)
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = servers
            .Select(CreateAutoUpdateCandidate)
            .ToArray();
        var request = new AutoUpdatePlanRequest(
            now,
            TimeSpan.FromMinutes(_settings.AutoUpdateIntervalMinutes),
            _isClosing,
            candidates);
        var dueServerIds = _automationService
            .PlanAutoUpdates(request)
            .ToHashSet(StringComparer.Ordinal);
        var dueServers = servers
            .Where(server => dueServerIds.Contains(server.Id))
            .ToArray();

        if (dueServers.Length == 0)
        {
            return Task.CompletedTask;
        }

        // Parallel mode keeps updates detached so one long SteamCMD run does not hold the whole
        // maintenance pass. Serial mode must await them: its UI promise is global serialization,
        // and per-server operation guards cannot prevent work for different servers overlapping.
        if (ShouldAwaitScheduledAutoUpdates(_settings.ParallelServerOperations))
        {
            return RunDueAutoUpdatesInBackgroundAsync(dueServers);
        }

        _ = RunDueAutoUpdatesInBackgroundAsync(dueServers);
        return Task.CompletedTask;
    }

    internal static bool ShouldAwaitScheduledAutoUpdates(bool parallelServerOperations) =>
        !parallelServerOperations;

    private async Task RunDueAutoUpdatesInBackgroundAsync(IReadOnlyList<InstalledServer> dueServers)
    {
        try
        {
            if (_settings.ParallelServerOperations)
            {
                await Task.WhenAll(dueServers.Select(_serverOperations.RunScheduledAutoUpdateAsync));
            }
            else
            {
                foreach (var server in dueServers)
                {
                    await _serverOperations.RunScheduledAutoUpdateAsync(server);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Scheduled auto-update execution failed: {ex.Message}");
        }
    }

    private AutoUpdateServerCandidate CreateAutoUpdateCandidate(InstalledServer server)
    {
        var configExists = File.Exists(server.ConfigPath);
        // Short-circuit before ever reading the config: a card without a usable module gets
        // skipped by the planner regardless of what automation.autoUpdate says, and - more
        // importantly - InstalledServerLoader's own problem/timeout cards (CanEditConfig == false)
        // are exactly the ones whose ServerConfig.json is missing or malformed. Reading it anyway
        // would risk the JsonException TryReadAutoUpdateEnabled itself guards against below, for a
        // result that was never going to matter.
        var hasUsableModule = HasUsableModuleForAutoUpdate(server.CanEditConfig);
        var autoUpdateEnabled = configExists && hasUsableModule && TryReadAutoUpdateEnabled(server.ConfigPath);

        _lastAutoUpdateChecks.TryGetValue(server.Id, out var lastCheck);
        // LastOperationError is deliberately not part of module availability: it can describe any
        // earlier start/stop/backup failure and is cleared only when another operation begins. Using
        // it here would permanently prevent this planner from starting the very update that clears
        // it.
        return new AutoUpdateServerCandidate(
            server.Id,
            configExists,
            hasUsableModule,
            server.CanStop,
            _operationManager.Get(server.Id)?.IsActive == true,
            autoUpdateEnabled,
            lastCheck == default ? null : lastCheck,
            server.RemoteBuildId,
            server.IgnoredBuildId);
    }

    // CanEditConfig is populated directly from module != null by InstalledServerLoader - it
    // correctly identifies whether a module was loaded, independent of what that module happens to
    // be named. Checking the module id string itself (e.g. rejecting "unknown") is unsound: nothing
    // reserves that id, so a real, validly-imported module could actually be named "unknown" and
    // would have CanEditConfig == true - the loader's own problem/timeout cards are the ones that
    // always set CanEditConfig to false, which is what this predicate actually needs to detect.
    internal static bool HasUsableModuleForAutoUpdate(bool canEditConfig) => canEditConfig;

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise this exact
    // defensive behaviour against a real malformed file, without instantiating MainWindow itself.
    // A single server's unreadable/malformed config must never throw out of candidate projection -
    // RunServerMaintenanceAsync's own try/catch would otherwise abort every later step in that pass
    // (auto-start/restart, public IP tracking, health refresh) for every other server too, not just
    // report a problem with this one.
    internal static bool TryReadAutoUpdateEnabled(string configPath)
    {
        try
        {
            var automation = ReadConfigObject(configPath, "automation");
            return ReadBoolean(automation, "autoUpdate");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task RunServerAutomationAsync(IReadOnlyList<InstalledServer> servers)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = _automationService.PlanStartup(new AutomationStartupPlanRequest(
            now,
            _isClosing,
            TimeSpan.FromMinutes(1),
            servers.Select(CreateAutomationStartupCandidate).ToArray()));

        if (_settings.ParallelServerOperations)
        {
            await Task.WhenAll(candidates.Select(candidate => _serverOperations.StartAutomaticallyAsync(
                servers.First(server => string.Equals(server.Id, candidate.ServerId, StringComparison.Ordinal)))));
            return;
        }

        foreach (var candidate in candidates)
        {
            var server = servers.First(item => string.Equals(item.Id, candidate.ServerId, StringComparison.Ordinal));
            await _serverOperations.StartAutomaticallyAsync(server);
        }
    }

    private AutomationStartupCandidate CreateAutomationStartupCandidate(InstalledServer server)
    {
        var configExists = File.Exists(server.ConfigPath);
        ServerAutomationSettings settings = ServerAutomationSettings.Default;
        if (configExists)
        {
            try
            {
                settings = ServerInstanceFactory.Load(server).AppSettings.Automation;
            }
            catch
            {
            }
        }

        _lastAutomationChecks.TryGetValue(server.Id, out var lastCheck);
        return new AutomationStartupCandidate(
            server.Id,
            server.Name,
            configExists,
            server.CanStart,
            _manuallyStoppedServers.Contains(server.Id),
            _openConfigServerIds.Contains(server.Id) && !server.CanStop,
            _operationManager.Get(server.Id)?.IsActive == true,
            lastCheck == default ? null : lastCheck,
            settings);
    }

    private void OpenConfigButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InstalledServer server })
        {
            return;
        }

        _openConfigServerIds.Add(server.Id);
        var window = new ServerConfigEditorWindow(
            server,
            null,
            _upnpMappingLifecycleService,
            _runtimeTracker.HasLiveMonitoredProcess)
        {
            Owner = this
        };
        window.Closed += (_, _) => _openConfigServerIds.Remove(server.Id);

        if (window.ShowDialog() == true)
        {
            _ = RefreshInstalledServersViewOnlyAsync();
        }
    }

    private async Task<string> ExecuteDiscordServerCommandAsync(string action, string serverId, string arguments)
    {
        if (_bulkActionInProgress)
        {
            return "A bulk server action is running; Discord server commands are temporarily unavailable.";
        }

        if (_closeDecisionInProgress || _isClosing || _windowsSessionEnding)
        {
            return "WindowsGSH is preparing to exit; remote server commands are temporarily unavailable.";
        }

        var servers = await LoadServersInBackgroundAsync();
        return await Dispatcher.InvokeAsync(async () =>
        {
            var server = servers.FirstOrDefault(item =>
                string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Name, serverId, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                return ServerActionMessageFormatter.ServerNotFound(serverId);
            }

            return action.ToLowerInvariant() switch
            {
                "start" => await DiscordStartServerAsync(server),
                "stop" => await DiscordStopServerAsync(server),
                "restart" => await DiscordRestartServerAsync(server),
                "update" => await DiscordUpdateServerAsync(server),
                "backup" => await DiscordBackupServerAsync(server),
                "send" => await DiscordSendConsoleAsync(server, arguments),
                "sendr" => await DiscordSendRconAsync(server, arguments),
                _ => ServerActionMessageFormatter.UnknownAction(action)
            };
        }).Task.Unwrap();
    }

    private async Task<string> DiscordStartServerAsync(InstalledServer server)
    {
        return await _serverOperations.DiscordStartAsync(server);
    }

    private async Task<string> DiscordStopServerAsync(InstalledServer server)
    {
        return await _serverOperations.DiscordStopAsync(server);
    }

    private async Task<string> DiscordRestartServerAsync(InstalledServer server)
    {
        return await _serverOperations.DiscordRestartAsync(server);
    }

    private async Task<string> DiscordUpdateServerAsync(InstalledServer server)
    {
        return await _serverOperations.DiscordUpdateAsync(server);
    }

    private async Task<string> DiscordBackupServerAsync(InstalledServer server)
    {
        return await _serverOperations.DiscordBackupAsync(server);
    }

    private async Task<string> DiscordSendRconAsync(InstalledServer server, string commandText)
    {
        if (!server.CanStop)
        {
            return ServerActionMessageFormatter.IsOffline(server.Name);
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return ServerActionMessageFormatter.SendUsage(_settings.DiscordBotPrefix, server.Id, "sendr");
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginServerOperation(server, ServerOperationKind.Rcon, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            SetOperationStatus(server.Id, "Running RCON");
            AppLogService.Add($"Discord RCON for {server.Name}: {commandText}", server.Id);
            _consoleService.Add(server.Id, $"> discord rcon {commandText}");
            var response = await GetModule(server).ExecuteRconCommandAsync(ServerInstanceFactory.Load(server), commandText, operation!.CancellationToken);
            foreach (var line in response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                _consoleService.Add(server.Id, line);
            }

            return string.IsNullOrWhiteSpace(response) ? ServerActionMessageFormatter.RconCommandSent(server.Name) : response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            AppLogService.Add($"Discord RCON failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.FailedToSendCommand(server.Name, ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task<string> DiscordSendConsoleAsync(InstalledServer server, string commandText)
    {
        if (!server.CanStop)
        {
            return ServerActionMessageFormatter.IsOffline(server.Name);
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return ServerActionMessageFormatter.SendUsage(_settings.DiscordBotPrefix, server.Id);
        }

        if (_pendingDiscordConsoleWorkers.TryGetValue(server.Id, out var pendingWorker))
        {
            if (!pendingWorker.IsCompleted)
            {
                return ServerActionMessageFormatter.ConsoleCommandStillRunning(server.Name);
            }

            RemovePendingDiscordConsoleWorkerIfCurrent(
                _pendingDiscordConsoleWorkers,
                server.Id,
                pendingWorker);
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBeginServerOperation(server, ServerOperationKind.ConsoleCommand, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            SetOperationStatus(server.Id, "Running console command");
            AppLogService.Add($"Discord console command for {server.Name}: {commandText}", server.Id);
            _consoleService.Add(server.Id, $"> discord console {commandText}");
            var result = await RunBoundedDiscordConsoleOperationAsync(
                async token =>
                {
                    var module = GetModule(server);
                    if (!ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(module))
                    {
                        return new DiscordConsoleExecutionResult(
                            false,
                            string.Empty,
                            ConsoleInputStrategyPolicy.GetCommandUnavailableMessage(module));
                    }

                    var response = await _consoleService.ExecuteModuleCommandAsync(
                        module,
                        ServerInstanceFactory.Load(server),
                        commandText,
                        token);
                    return new DiscordConsoleExecutionResult(true, response, null);
                },
                DiscordConsoleCommandTimeout,
                operation!.CancellationToken,
                workerTask =>
                {
                    _pendingDiscordConsoleWorkers[server.Id] = workerTask;
                    _ = workerTask.ContinueWith(
                        completed => RemovePendingDiscordConsoleWorkerIfCurrent(
                            _pendingDiscordConsoleWorkers,
                            server.Id,
                            completed),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                });
            if (!result.IsSupported)
            {
                failed = true;
                var unavailableMessage = result.UnavailableMessage ?? "Console command input is unavailable.";
                operation.Fail(new NotSupportedException(unavailableMessage));
                return ServerActionMessageFormatter.FailedToSendCommand(
                    server.Name,
                    unavailableMessage);
            }

            foreach (var line in result.Response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                _consoleService.Add(server.Id, line);
            }

            return ServerActionMessageFormatter.ConsoleCommandSent(server.Name);
        }
        catch (TimeoutException ex)
        {
            failed = true;
            operation?.Fail(ex);
            AppLogService.Add($"Discord console command timed out for {server.Name}.", server.Id);
            return ServerActionMessageFormatter.ConsoleCommandTimedOut(server.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            operation?.Fail(new InvalidOperationException("Console command failed due to an internal error."));
            AppLogService.Add($"Discord console command failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.ConsoleCommandFailed(server.Name);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    internal static async Task<T> RunBoundedDiscordConsoleOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<Task>? workerStarted = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var cancellationDispatch = new DiscordConsoleCancellationDispatch(cancellationToken);
        var operationTask = Task.Run(
            () => operation(cancellationDispatch.Token),
            CancellationToken.None);
        var lifetimeTask = ObserveDiscordConsoleLifetimeAsync(operationTask, cancellationDispatch);
        workerStarted?.Invoke(lifetimeTask);
        try
        {
            return await operationTask.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            if (!operationTask.IsCompleted)
            {
                _ = cancellationDispatch.QueueCancellation();
            }

            throw;
        }
    }

    private static async Task ObserveDiscordConsoleLifetimeAsync(
        Task operationTask,
        DiscordConsoleCancellationDispatch cancellationDispatch)
    {
        try
        {
            await operationTask.ConfigureAwait(false);
        }
        catch
        {
            // The caller has already received the operation failure or timeout.
        }

        await cancellationDispatch.CompleteAsync().ConfigureAwait(false);
    }

    internal static bool RemovePendingDiscordConsoleWorkerIfCurrent(
        ConcurrentDictionary<string, Task> pendingWorkers,
        string serverId,
        Task workerTask)
    {
        return ((ICollection<KeyValuePair<string, Task>>)pendingWorkers)
            .Remove(new KeyValuePair<string, Task>(serverId, workerTask));
    }

    private sealed record DiscordConsoleExecutionResult(
        bool IsSupported,
        string Response,
        string? UnavailableMessage);

    private sealed class DiscordConsoleCancellationDispatch
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _moduleCancellation = new();
        private readonly CancellationTokenRegistration _parentRegistration;
        private Task? _cancellationTask;
        private bool _closed;

        public DiscordConsoleCancellationDispatch(CancellationToken parentToken)
        {
            // Do not use CreateLinkedTokenSource here. Linked-token propagation calls Cancel
            // synchronously on the thread cancelling the parent; module callbacks would therefore
            // run while ServerOperationManager holds its lock on the WPF dispatcher. This callback
            // only queues cancellation and returns promptly.
            _parentRegistration = parentToken.Register(
                static state => ((DiscordConsoleCancellationDispatch)state!).QueueCancellation(),
                this);
        }

        public CancellationToken Token => _moduleCancellation.Token;

        public Task QueueCancellation()
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return Task.CompletedTask;
                }

                return _cancellationTask ??= Task.Run(_moduleCancellation.Cancel);
            }
        }

        public async Task CompleteAsync()
        {
            // Dispose only unregisters the small queueing callback; it never invokes module code.
            _parentRegistration.Dispose();

            Task cancellationTask;
            lock (_sync)
            {
                _closed = true;
                cancellationTask = _cancellationTask ?? Task.CompletedTask;
            }

            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch
            {
                // Cancellation callback failures are arbitrary module failures. Observe them so
                // they cannot surface later as an unobserved task exception.
            }

            _moduleCancellation.Dispose();
        }
    }

    private IReadOnlyList<string> GetConfiguredBackupPaths(InstalledServer server)
    {
        var backup = ReadConfigObject(server.ConfigPath, "backup");
        return ReadStringArray(backup, "paths");
    }

    private void MarkAutoUpdateChecked(string serverId)
    {
        _lastAutoUpdateChecks[serverId] = DateTimeOffset.UtcNow;
    }

    private void MarkAutomationChecked(string serverId)
    {
        _lastAutomationChecks[serverId] = DateTimeOffset.UtcNow;
    }

    private void SetManuallyStopped(string serverId, bool manuallyStopped)
    {
        if (manuallyStopped)
        {
            _manuallyStoppedServers.Add(serverId);
        }
        else
        {
            _manuallyStoppedServers.Remove(serverId);
        }
    }

    private bool TryBeginServerOperation(InstalledServer server, ServerOperationKind kind, out ServerOperationScope? operation, bool logBusy = true)
    {
        return _serverOperations.TryBegin(server, kind, out operation, logBusy);
    }

    private IReadOnlyList<InstalledServer> ApplyOperationStatuses(IReadOnlyList<InstalledServer> servers)
    {
        return _serverListViewModel.ApplyOperationStatuses(servers);
    }

    private void UpdateServerSummary(ServerListViewState state)
    {
        var issueRows = state.IssueRows
            .Concat(_healthIssueRows)
            .GroupBy(row => (row.Server.Id, row.Issue))
            .Select(group => group.First())
            .ToArray();
        var warningCount = issueRows.Select(row => row.Server.Id).Distinct(StringComparer.Ordinal).Count();
        UpdateSummaryPill(OfflineSummaryBorder, OfflineSummaryTextBlock, state.OfflineCount, "offline");
        UpdateSummaryPill(WarningSummaryButton, TransitionalSummaryTextBlock, warningCount, "warning");
        UpdateSummaryPill(OnlineSummaryBorder, OnlineSummaryTextBlock, state.OnlineCount, "online");

        _serverIssues.Update(issueRows);
        if (warningCount == 0)
        {
            ServerIssuesPopup.IsOpen = false;
        }
    }

    private void WarningSummaryButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ServerIssuesPopup.IsOpen = !ServerIssuesPopup.IsOpen;
    }

    private async void IgnoreServerBuildButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: InstalledServer server } ||
            !server.HasUpdateAvailable ||
            string.IsNullOrWhiteSpace(server.RemoteBuildId))
        {
            return;
        }

        try
        {
            IgnoreServerBuild(server);
            AppLogService.Add($"{server.Name} ignored Steam build {server.RemoteBuildId}.", server.Id);
            ServerIssuesPopup.IsOpen = false;
            await RefreshInstalledServersViewOnlyAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Ignore Build Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private static void UpdateSummaryPill(System.Windows.UIElement border, System.Windows.Controls.TextBlock textBlock, int count, string label)
    {
        border.Visibility = count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        textBlock.Text = $"{count} {Pluralize(label, count)}";
    }

    private static void IgnoreServerBuild(InstalledServer server)
    {
        var root = JsonNode.Parse(File.ReadAllText(server.ConfigPath))?.AsObject()
            ?? throw new InvalidOperationException("Server config is not a JSON object.");
        var steam = root["steam"] as JsonObject ?? [];
        root["steam"] = steam;
        steam["ignoredBuildId"] = server.RemoteBuildId;
        steam["ignoredBuildUtc"] = DateTimeOffset.UtcNow.ToString("O");
        AtomicFile.WriteAllText(server.ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Pluralize(string label, int count)
    {
        return count == 1 ? label : $"{label} servers";
    }

    private async Task<IReadOnlyList<InstalledServer>> GetDiscordServersAsync()
    {
        var servers = await LoadServersInBackgroundAsync();
        return await Dispatcher.InvokeAsync(() => ApplyOperationStatuses(servers));
    }

    private async Task RefreshServerHealthIssuesAsync(IReadOnlyList<InstalledServer> servers)
    {
        if (DateTimeOffset.UtcNow - _lastHealthCheckAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastHealthCheckAt = DateTimeOffset.UtcNow;
        var publicIpTrackingEnabled = _settings.PublicIpTrackingEnabled;
        var lastKnownPublicIp = _settings.LastKnownPublicIp;
        var lastPublicIpCheckedAt = _settings.LastPublicIpCheckedAt;
        var issues = await Task.Run(async () =>
        {
            var descriptors = _moduleRegistry.GetModuleDescriptors();
            var backgroundIssues = new List<ServerIssueRow>();
            foreach (var server in servers)
            {
                var descriptor = descriptors.FirstOrDefault(item =>
                    string.Equals(item.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
                IReadOnlyList<FirewallRuleStatus>? firewallRules = null;
                string? firewallError = null;
                if (descriptor != null)
                {
                    try
                    {
                        firewallRules = _firewallService.GetRuleStatuses(server, descriptor.Module);
                    }
                    catch (Exception ex)
                    {
                        firewallError = $"Firewall status could not be read: {ex.Message}";
                    }
                }

                IReadOnlyList<ServerOperationSnapshot>? recentOperations = null;
                string? recentOperationsError = null;
                try
                {
                    recentOperations = OperationHistoryRepository.GetRecentForServer(server.Id);
                }
                catch (Exception ex)
                {
                    recentOperationsError = $"Recent operation history could not be read: {ex.Message}";
                }

                var report = await _serverHealthService.EvaluateAsync(new ServerHealthRequest(
                    server,
                    descriptor,
                    servers,
                    firewallRules,
                    firewallError,
                    publicIpTrackingEnabled,
                    lastKnownPublicIp,
                    lastPublicIpCheckedAt,
                    descriptors,
                    recentOperations,
                    recentOperationsError));
                var important = report.Checks
                    .Where(check => check.Severity is ServerHealthSeverity.Fail or ServerHealthSeverity.Warning)
                    .Take(3)
                    .ToArray();
                if (important.Length > 0)
                {
                    backgroundIssues.Add(new ServerIssueRow(
                        server,
                        server.Name,
                        string.Join(Environment.NewLine, important.Select(check => $"{check.Category}: {check.Message}")),
                        CanIgnoreBuild: false));
                }
            }

            return (IReadOnlyList<ServerIssueRow>)backgroundIssues;
        }).ConfigureAwait(true);

        _healthIssueRows = issues;
        await Dispatcher.InvokeAsync(() =>
        {
            var selectedSource = (LogFilterComboBox.SelectedItem as ServerLogFilterItem)?.Source;
            UpdateServerSummary(_serverListViewModel.Update(servers, selectedSource));
        });
    }

    // Deliberately a fresh, on-demand evaluation for every installed server, not a reuse of
    // _healthIssueRows - that digest only keeps the top few Warning/Fail checks per server (see
    // RefreshServerHealthIssuesAsync above) and is also rate-limited to once a minute, which could
    // show a support bundle a materially stale picture right after something changed. Deliberately
    // NOT folded into RefreshServerHealthIssuesAsync itself despite the near-identical per-server
    // gather-then-evaluate shape - that method is an already-working, already-tested periodic
    // background path; duplicating its body here for the support-bundle case is a smaller risk than
    // reshaping it to serve two different callers with different rate-limiting/output needs.
    // Bounds how long a single server's health evaluation may take while building a support bundle.
    // EvaluateAsync's own module-readiness step (AddModuleReadinessChecksAsync) awaits arbitrary
    // compiled module code with no internal bound of its own - a module that never completes its
    // returned task (or simply ignores the cancellation token) would otherwise hang this per-server
    // loop, and therefore the whole export, forever. Matches DiscordBotHost.PanelRefreshTimeout's own
    // value - a previously-validated "generous but bounded" duration for a similar "don't let one
    // slow/hanging dependency block everything else" concern in this codebase.
    private static readonly TimeSpan SupportBundleHealthEvaluationTimeout = TimeSpan.FromSeconds(20);

    // Tracks, per server id, a health evaluation that is still running past its own timeout - the
    // timeout only lets THIS export's loop move on, it can't actually terminate a synchronously-
    // blocked module's thread-pool worker underneath it. Without this, every repeated export against
    // the same permanently-stuck server would start yet another Task.Run worker that also never
    // returns - a slow, unbounded thread-pool leak across a process that may run for a month or more.
    // Keyed by server id so a fresh export can skip starting a duplicate attempt while an earlier
    // one is still outstanding, and can try again once that entry clears (the evaluation eventually
    // completes, faults, or - if it never does - stays skipped forever, which is still strictly
    // better than accumulating one more stuck worker per export).
    private readonly ConcurrentDictionary<string, Task<ServerHealthReport>> _pendingSupportBundleHealthEvaluations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _supportBundleServerLoadSync = new();
    private Task<IReadOnlyList<InstalledServer>>? _pendingSupportBundleServerLoad;

    // LoadServersInBackgroundAsync ultimately calls InstalledServerLoader.LoadAsync, which runs
    // TryLoadAsync for every installed server via Task.WhenAll and calls a module's GetDisplayInfo/
    // IsInstallValid synchronously inside each one - a single server whose module hangs in either
    // call blocks the ENTIRE load (Task.WhenAll waits for every task, not just the slow one), not
    // just that one server's slot, well before this method's own per-server timeout logic is ever
    // reached. Racing the load itself against the same timeout and falling back to the server list
    // the normal periodic refresh already produced (_serverListViewModel.LastVisibleServers) lets the
    // export proceed with whatever was last known to be true instead of waiting forever - the
    // timed-out load keeps running, but _pendingSupportBundleServerLoad ensures later exports reuse
    // that exact task instead of accumulating one additional blocked worker per export.
    private async Task<IReadOnlyList<InstalledServer>> LoadServersForSupportBundleAsync()
    {
        Task<IReadOnlyList<InstalledServer>> loadTask;
        lock (_supportBundleServerLoadSync)
        {
            if (_pendingSupportBundleServerLoad is { } pending)
            {
                loadTask = pending;
            }
            else
            {
                loadTask = LoadServersInBackgroundAsync();
                _pendingSupportBundleServerLoad = loadTask;
                _ = ObserveSupportBundleServerLoadAsync(loadTask);
            }
        }

        var completed = await Task.WhenAny(loadTask, Task.Delay(SupportBundleHealthEvaluationTimeout))
            .ConfigureAwait(false);
        if (completed == loadTask)
        {
            return await loadTask.ConfigureAwait(false);
        }

        AppLogService.Add(
            $"Loading the installed server list timed out after {SupportBundleHealthEvaluationTimeout.TotalSeconds:0}s while building the support bundle; using the last known server list instead.");
        return _serverListViewModel.LastVisibleServers;
    }

    private async Task ObserveSupportBundleServerLoadAsync(Task<IReadOnlyList<InstalledServer>> loadTask)
    {
        try
        {
            await loadTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Installed server load, abandoned earlier for a support-bundle export, later failed: {ex}");
        }
        finally
        {
            lock (_supportBundleServerLoadSync)
            {
                if (ReferenceEquals(_pendingSupportBundleServerLoad, loadTask))
                {
                    _pendingSupportBundleServerLoad = null;
                }
            }
        }
    }

    private async Task<IReadOnlyList<ServerHealthReport>> BuildSupportBundleHealthReportsAsync()
    {
        var servers = await LoadServersForSupportBundleAsync().ConfigureAwait(false);
        var publicIpTrackingEnabled = _settings.PublicIpTrackingEnabled;
        var lastKnownPublicIp = _settings.LastKnownPublicIp;
        var lastPublicIpCheckedAt = _settings.LastPublicIpCheckedAt;

        return await Task.Run(async () =>
        {
            var descriptors = _moduleRegistry.GetModuleDescriptors();
            var reports = new List<ServerHealthReport>(servers.Count);
            foreach (var server in servers)
            {
                var serverFolderId = Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(server.ServerFolder));
                var descriptor = descriptors.FirstOrDefault(item =>
                    string.Equals(item.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));

                IReadOnlyList<ServerOperationSnapshot>? recentOperations = null;
                string? recentOperationsError = null;
                try
                {
                    recentOperations = OperationHistoryRepository.GetRecentForServer(server.Id);
                }
                catch (Exception ex)
                {
                    recentOperationsError = $"Recent operation history could not be read: {ex.Message}";
                }

                if (_pendingSupportBundleHealthEvaluations.ContainsKey(serverFolderId))
                {
                    // An earlier export's evaluation for this exact server never completed - its
                    // Task.Run worker may still be blocked, possibly forever, inside module code.
                    // Starting a second Task.Run for the same server here would leave two permanently
                    // stuck workers instead of one, growing without bound across repeated exports.
                    AppLogService.Add(
                        $"Health evaluation for {server.Name} is still stuck from an earlier support-bundle export; skipped starting another attempt.",
                        server.Id);
                    continue;
                }

                try
                {
                    // A linked, timeout-bound token is passed through in case EvaluateAsync's own
                    // internals (or a well-behaved module) actually observe it, but Task.WhenAny below
                    // is the real backstop: it does not require the awaited task to ever honour
                    // cancellation, only that this loop can move on once the timeout elapses either way.
                    using var timeoutCts = new CancellationTokenSource(SupportBundleHealthEvaluationTimeout);
                    // Calling EvaluateAsync (or GetRuleStatuses) directly would run synchronously on
                    // this thread up until the first genuine await point - if a module's synchronous
                    // plumbing (GetConfigFields/GetPorts/IsInstallValid, etc., reached either from
                    // firewall-rule collection below or from inside EvaluateAsync itself) blocks
                    // before that point, the call never returns a task to race in the first place,
                    // and the Task.WhenAny below is never reached at all. Task.Run schedules the
                    // whole thing - firewall probing included, not just the evaluation - on an
                    // independently running task immediately, so a synchronous hang anywhere inside
                    // still leaves a real, already-in-flight task here to race against the timeout.
                    var evaluateTask = Task.Run(
                        () =>
                        {
                            IReadOnlyList<FirewallRuleStatus>? firewallRules = null;
                            string? firewallError = null;
                            if (descriptor != null)
                            {
                                try
                                {
                                    firewallRules = _firewallService.GetRuleStatuses(server, descriptor.Module);
                                }
                                catch (Exception ex)
                                {
                                    // GetRuleStatuses reaches descriptor.Module.GetConfigFields() -
                                    // arbitrary compiled module code that could throw with an
                                    // unstructured value in its Exception.Message (e.g.
                                    // throw new Exception("hunter2")) that doesn't match any of
                                    // RedactText's recognised secret shapes (assignment/token/
                                    // webhook). firewallError below ends up in an exported health.json
                                    // check message, so the raw exception text must never reach it -
                                    // Debug.WriteLine (never exported, the same non-exported
                                    // diagnostic channel RuntimeDiagnosticsService already uses for
                                    // this exact class of concern) is the only place it goes.
                                    Debug.WriteLine($"Firewall status could not be read for {server.Name} while building the support bundle: {ex}");
                                    firewallError = "Firewall status could not be read due to an internal error.";
                                }
                            }

                            return _serverHealthService.EvaluateAsync(
                                new ServerHealthRequest(
                                    server,
                                    descriptor,
                                    servers,
                                    firewallRules,
                                    firewallError,
                                    publicIpTrackingEnabled,
                                    lastKnownPublicIp,
                                    lastPublicIpCheckedAt,
                                    descriptors,
                                    recentOperations,
                                    recentOperationsError),
                                timeoutCts.Token);
                        },
                        timeoutCts.Token);
                    _pendingSupportBundleHealthEvaluations[serverFolderId] = evaluateTask;
                    var completed = await Task.WhenAny(evaluateTask, Task.Delay(SupportBundleHealthEvaluationTimeout))
                        .ConfigureAwait(false);
                    if (completed != evaluateTask)
                    {
                        AppLogService.Add(
                            $"Health evaluation for {server.Name} timed out after {SupportBundleHealthEvaluationTimeout.TotalSeconds:0}s while building the support bundle; its results were skipped.",
                            server.Id);
                        // Task.WhenAny does not cancel or abandon evaluateTask - it may still complete
                        // (or fault) later. Observe it so a late fault becomes a log entry instead of an
                        // unobserved task exception, and so its entry above is eventually cleared once
                        // it actually finishes (letting a future export retry this server); deliberately
                        // not awaited here, since the whole point is to stop waiting on it now.
                        _ = ObserveLateHealthEvaluationAsync(evaluateTask, server, serverFolderId);
                        continue;
                    }

                    // Completed within the timeout window - no longer "in flight," regardless of
                    // whether awaiting its result below throws.
                    _pendingSupportBundleHealthEvaluations.TryRemove(serverFolderId, out _);
                    var report = await evaluateTask.ConfigureAwait(false);
                    reports.Add(report with { SourceFolderId = serverFolderId });
                }
                catch (Exception ex)
                {
                    // A support bundle must never fail to export just because one server's health
                    // check couldn't be evaluated (5.6's own "bundle generation can't fail because
                    // one source is unavailable" acceptance criterion) - that server's config-derived
                    // summary.json still gets written by SupportBundleService; it just has no
                    // accompanying health.json.
                    //
                    // ex.Message can originate from arbitrary module code reached inside EvaluateAsync
                    // and might contain an unstructured secret RedactText can't reliably catch (e.g. a
                    // bare password with no assignment shape). AppSettingsView snapshots
                    // AppLogService.Text into the exported logs/app.log AFTER this whole health-
                    // gathering pass completes, so anything logged here with AppLogService.Add ends up
                    // in that same export - Debug.WriteLine (never exported) is the only place the raw
                    // exception goes; the exported log gets a fixed, safe message instead.
                    Debug.WriteLine($"Could not evaluate health for {server.Name} while building the support bundle: {ex}");
                    AppLogService.Add(
                        $"Could not evaluate health for {server.Name} while building the support bundle due to an internal error.",
                        server.Id);
                }
            }

            return (IReadOnlyList<ServerHealthReport>)reports;
        }).ConfigureAwait(false);
    }

    private async Task ObserveLateHealthEvaluationAsync(
        Task<ServerHealthReport> evaluateTask,
        InstalledServer server,
        string serverFolderId)
    {
        try
        {
            await evaluateTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Same reasoning as the catch block above - this can carry an arbitrary module exception
            // message, and it runs in the background well after (or even during) a support-bundle
            // export, so the exported log must only ever see a fixed, safe message.
            Debug.WriteLine($"Health evaluation for {server.Name} completed after its support-bundle timeout with an error: {ex}");
            AppLogService.Add(
                $"Health evaluation for {server.Name} completed after its support-bundle timeout with an internal error.",
                server.Id);
        }
        finally
        {
            _pendingSupportBundleHealthEvaluations.TryRemove(serverFolderId, out _);
        }
    }

    private static JsonElement ReadConfigObject(string configPath, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value.Clone();
        }

        return default;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName, bool fallback = false)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
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
            // Ignore drag races when the mouse is released before DragMove starts.
        }
    }

}

public sealed record ModuleServerUsage(string Name, string ServerFolder);

public sealed record CronServerAction(
    InstalledServer Server,
    string Action,
    string? Command = null,
    string? ParametersJson = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 0);

public sealed class ServerRow : System.Windows.Controls.Control
{
    public static readonly System.Windows.DependencyProperty GameInitialProperty =
        System.Windows.DependencyProperty.Register(nameof(GameInitial), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty GameNameProperty =
        System.Windows.DependencyProperty.Register(nameof(GameName), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty DetailProperty =
        System.Windows.DependencyProperty.Register(nameof(Detail), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty BadgeProperty =
        System.Windows.DependencyProperty.Register(nameof(Badge), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty PlayersProperty =
        System.Windows.DependencyProperty.Register(nameof(Players), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty VersionProperty =
        System.Windows.DependencyProperty.Register(nameof(Version), typeof(string), typeof(ServerRow), new System.Windows.PropertyMetadata(""));

    public static readonly System.Windows.DependencyProperty AccentProperty =
        System.Windows.DependencyProperty.Register(nameof(Accent), typeof(System.Windows.Media.Brush), typeof(ServerRow), new System.Windows.PropertyMetadata(System.Windows.Media.Brushes.SteelBlue));

    public static readonly System.Windows.DependencyProperty WarningProperty =
        System.Windows.DependencyProperty.Register(nameof(Warning), typeof(bool), typeof(ServerRow), new System.Windows.PropertyMetadata(false));

    public static readonly System.Windows.DependencyProperty MutedProperty =
        System.Windows.DependencyProperty.Register(nameof(Muted), typeof(bool), typeof(ServerRow), new System.Windows.PropertyMetadata(false));

    static ServerRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ServerRow), new System.Windows.FrameworkPropertyMetadata(typeof(ServerRow)));
    }

    public string GameInitial
    {
        get => (string)GetValue(GameInitialProperty);
        set => SetValue(GameInitialProperty, value);
    }

    public string GameName
    {
        get => (string)GetValue(GameNameProperty);
        set => SetValue(GameNameProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public string Players
    {
        get => (string)GetValue(PlayersProperty);
        set => SetValue(PlayersProperty, value);
    }

    public string Version
    {
        get => (string)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public System.Windows.Media.Brush Accent
    {
        get => (System.Windows.Media.Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool Warning
    {
        get => (bool)GetValue(WarningProperty);
        set => SetValue(WarningProperty, value);
    }

    public bool Muted
    {
        get => (bool)GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }
}

