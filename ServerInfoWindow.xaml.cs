using System.Collections.Specialized;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Network;
using WindowsGSH.Core.Network.Upnp;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using WindowsGSH.Data;
using WindowsGSH.Services;

namespace WindowsGSH;

public partial class ServerInfoWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int MaxSamples = 80;
    private static readonly TimeSpan ModuleQueryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RconCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConsoleCommandTimeout = TimeSpan.FromSeconds(15);
    private const int MaxPendingConsoleRemovals = 500;

    private readonly InstalledServer _server;
    private readonly InstalledServerLoader _serverLoader;
    private readonly UpnpMappingLifecycleService _upnpMappingLifecycleService;
    private readonly Func<string, bool>? _hasLiveMonitoredProcess;
    private readonly IGameServerModule? _module;
    private readonly ServerInstance _instance;
    private readonly ServerTelemetryService _telemetryService = new();
    private readonly IServerConsoleService _consoleService = ServerConsoleService.Shared;
    private readonly DispatcherTimer _timer = new();
    private readonly Queue<ChartSample> _samples = [];
    private readonly int? _maxPlayers;
    private readonly CancellationTokenSource _refreshCancellation = new();
    private readonly CancellationTokenSource _logTailCancellation = new();
    private readonly ServerHealthService _healthService = new();
    private readonly ConsoleViewState _consoleViewState = new();
    private readonly DispatcherTimer _consoleRefreshTimer = new(DispatcherPriority.Background);
    private int _pendingRemoveCount;
    private int _consoleDirty;
    private NotifyCollectionChangedEventHandler? _consoleLogHandler;
    private ServerHealthRequest? _healthRequest;
    private ServerHealthReport? _healthReport;
    private IReadOnlyList<ServerHealthCheck> _externalReachabilityChecks = [];
    private IReadOnlyList<ServerHealthCheck> _portForwardingChecks = [];
    private bool _externalReachabilityCheckInFlight;
    private bool _healthRefreshInFlight;
    private bool _portForwardingRefreshInFlight;
    private ChartMetric _chartMetric = ChartMetric.Cpu;
    private bool _refreshInFlight;
    private string? _lastTelemetryError;
    private Task<TelemetryDiscoveryResult>? _telemetryDiscoveryTask;

    // Preserves the original public constructor's own signature/metadata (see the same reasoning
    // recorded on ReadinessCheckWindow's constructors) rather than folding the new dependency into
    // an optional parameter on it.
    public ServerInfoWindow(InstalledServer server)
        : this(
            server,
            new InstalledServerLoader(),
            new UpnpMappingLifecycleService(new PortMappingRegistry(), log: AppLogService.Add),
            hasLiveMonitoredProcess: null)
    {
    }

    // Accepts an existing loader so a caller that already owns one (MainWindow) can share it -
    // InstalledServerLoader's hang-protection dedup dictionary is scoped to the loader instance, not
    // app-wide. Without this, every time this window is (re)opened, or its "Refresh" action runs, a
    // fresh loader with no memory of any earlier stuck attempt would start yet another Task.Run
    // worker for the same permanently hung server. Internal rather than public - MainWindow is the
    // only real caller with a shared instance to pass in, and it's in the same assembly.
    internal ServerInfoWindow(InstalledServer server, InstalledServerLoader serverLoader)
        : this(
            server,
            serverLoader,
            new UpnpMappingLifecycleService(new PortMappingRegistry(), log: AppLogService.Add),
            hasLiveMonitoredProcess: null)
    {
    }

    internal ServerInfoWindow(
        InstalledServer server,
        InstalledServerLoader serverLoader,
        UpnpMappingLifecycleService upnpMappingLifecycleService,
        Func<string, bool>? hasLiveMonitoredProcess = null)
    {
        _serverLoader = serverLoader;
        _upnpMappingLifecycleService = upnpMappingLifecycleService ?? throw new ArgumentNullException(nameof(upnpMappingLifecycleService));
        _hasLiveMonitoredProcess = hasLiveMonitoredProcess;
        InitializeComponent();

        var capabilities = WindowsVisualCapabilities.Current;
        WindowCornerPreference = capabilities.SupportsRoundedCorners
            ? Wpf.Ui.Controls.WindowCornerPreference.Round
            : Wpf.Ui.Controls.WindowCornerPreference.DoNotRound;
        // See ExitDecisionWindow.xaml.cs for why Mica stays off for now.
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        _server = server;
        try
        {
            _instance = ServerInstanceFactory.Load(server);
            _module = new ModuleRegistry().GetModules()
                .FirstOrDefault(module => string.Equals(module.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
            _maxPlayers = int.TryParse(server.MaxPlayers, out var maxPlayers) ? maxPlayers : null;

            Title = $"{server.Name} Info";
            ServerInfoTitleBar.Title = Title;
            TitleTextBlock.Text = server.Name;
            SubtitleTextBlock.Text = $"{server.IpAddress}:{server.Port}  |  {server.InstallPath}";
            SteamAppTextBlock.Text = string.IsNullOrWhiteSpace(server.SteamAppId) ? "--" : server.SteamAppId;
            BranchTextBlock.Text = string.IsNullOrWhiteSpace(server.SteamBranch) ? "public" : server.SteamBranch;

            // P3-06: unlike InstallServerWindow's preview (shown once, only when a new server is
            // first installed), this is visible every time Server Info is opened for this server -
            // covers imported servers, servers installed before this preview existed, and a module
            // that adds/changes CustomArguments after the server was already installed. Applies to
            // every subsequent update regardless of trigger (manual, scheduled, Discord, web), since
            // SteamCmdPolicy.BuildInstallArguments uses the same value for install and update.
            var customArguments = _module?.GetSteamInstall()?.CustomArguments;
            if (!string.IsNullOrWhiteSpace(customArguments))
            {
                SteamCustomArgumentsWarningTextBlock.Text =
                    $"This module appends custom SteamCMD arguments on every install and update: {customArguments}";
                SteamCustomArgumentsWarningTextBlock.Visibility = Visibility.Visible;
            }
            ApplyPresentation(ServerInfoPresentationModel.FromServer(
                server,
                _module?.Capabilities.SupportsQuery == true));
            ConsoleTextBox.Text = _consoleViewState.BuildVisibleText(_consoleService.GetLogSnapshot(server.Id));
            _consoleLogHandler = (_, args) => OnConsoleChanged_Background(args);
            _consoleService.GetLog(server.Id).CollectionChanged += _consoleLogHandler;
            var consoleLogPath = _module?.GetConsoleLogPath(_instance);
            var hasConsoleLog = !string.IsNullOrWhiteSpace(consoleLogPath);
            var hasConsole = (_module != null && ConsoleInputStrategyPolicy.UsesRedirectedStreams(_module.Runtime)) ||
                ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(_module) ||
                ModuleRconAvailability.HasRconSupport(_module, _instance) ||
                hasConsoleLog;
            ConsoleTabButton.Visibility = hasConsole ? Visibility.Visible : Visibility.Collapsed;
            ApiActionsButton.Visibility = SupportsApiActions()
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (hasConsole && string.IsNullOrWhiteSpace(ConsoleTextBox.Text))
            {
                var message = _module != null && ConsoleInputStrategyPolicy.UsesRedirectedStreams(_module.Runtime)
                    ? "Redirected console capture is enabled. No output has been captured yet."
                    : hasConsoleLog
                        ? "Log capture is enabled. No log output has been captured yet."
                        : ConsoleInputStrategyPolicy.PrefersRcon(_module)
                            ? "This module prefers RCON for commands."
                            : "This module does not expose captured console output.";
                _consoleService.Add(server.Id, message);
            }

            if (hasConsoleLog && !string.IsNullOrWhiteSpace(consoleLogPath))
            {
                _consoleService.AttachLogFile(server.Id, consoleLogPath, _logTailCancellation.Token);
            }

            RconPanel.Visibility = ModuleRconAvailability.CanUseRcon(_module, _instance)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConsoleCommandPanel.Visibility = ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(_module)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateConsoleInputState();
            RefreshAddons();

            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += async (_, _) => await RefreshTelemetryAsync();
            _consoleRefreshTimer.Interval = TimeSpan.FromMilliseconds(150);
            _consoleRefreshTimer.Tick += (_, _) => FlushConsolePending();
            Closed += (_, _) => Cleanup();

            _ = RefreshTelemetryAsync();
            ShowInfoView();
            UpdateChartMode();
            _timer.Start();
            _consoleRefreshTimer.Start();
        }
        catch
        {
            // Several calls above are arbitrary module code (GetSteamInstall, GetConsoleLogPath,
            // Capabilities via SupportsApiActions/ModuleRconAvailability, GetAddonDefinitions/
            // GetAddonStatus via RefreshAddons) that can throw partway through construction - after
            // the console log CollectionChanged subscription above has already been registered, but
            // before Closed (which normally removes it) exists to ever fire. MainWindow's
            // InfoButton_Click now catches a failed construction so the app doesn't crash, but
            // without this, that subscription (and, if AttachLogFile already ran, its background
            // tail loop) would leak for as long as the app runs, rooted via the shared,
            // static-lifetime ServerConsoleService holding a reference to this half-built window.
            // Cleanup() mirrors the Closed handler's own teardown exactly, since a half-built window
            // is never shown and Closed will never fire for it otherwise.
            Cleanup();
            throw;
        }
    }

    private void Cleanup()
    {
        _timer.Stop();
        _consoleRefreshTimer.Stop();
        _logTailCancellation.Cancel();
        _logTailCancellation.Dispose();
        _refreshCancellation.Cancel();
        _refreshCancellation.Dispose();
        if (_consoleLogHandler != null)
        {
            _consoleService.GetLog(_server.Id).CollectionChanged -= _consoleLogHandler;
            _consoleLogHandler = null;
        }
    }

    private void RefreshAddons()
    {
        if (_module == null)
        {
            AddonsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var addonRows = _module.GetAddonDefinitions()
            .Select(definition =>
            {
                var status = _module.GetAddonStatus(_instance, definition.Id);
                return new AddonRow(
                    definition.Id,
                    definition.Name,
                    definition.Description,
                    status.IsInstalled,
                    status.IsEnabled,
                    status.StatusText);
            })
            .ToArray();

        AddonsItemsControl.ItemsSource = addonRows;
        AddonsPanel.Visibility = addonRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool SupportsApiActions()
    {
        return _module?.Capabilities.SupportsApiActions == true &&
            _module is IModuleApiActionCapability api &&
            api.GetApiConnection() is { } connection &&
            api.GetApiActions().Count > 0 &&
            IsApiEnabled(connection.EnabledKey, GetSetting(_instance.Settings, connection.EnabledKey));
    }

    private async Task RefreshTelemetryAsync()
    {
        if (_refreshInFlight)
        {
            return;
        }

        _refreshInFlight = true;
        try
        {
            await RefreshTelemetryCoreAsync(_refreshCancellation.Token);
            _lastTelemetryError = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // RefreshTelemetryCoreAsync does more than the module's own QueryAsync call (which
            // already has its own local catch) - GetSnapshot, chart rendering, and the various
            // TextBlock updates all run unguarded. This method is invoked from the timer's
            // async void Tick handler, so anything that escapes here reaches WPF's
            // DispatcherUnhandledException handler, which deliberately sets e.Handled = false and
            // terminates the app. Matches the same catch-and-log-instead-of-crash pattern
            // MainWindow's own periodic timer work already uses (see
            // RefreshInstalledServersSafelyAsync in MainWindow.Metrics.cs).
            //
            // A persistent failure would otherwise log the identical message every 3 seconds
            // forever for as long as Server Info stays open - AppLogService.Add does a synchronous
            // DB write plus UI work on every call, so that's not just log noise but a real,
            // recurring cost. Only logs when the message actually changes from the last failure
            // (reset to null on the next successful refresh, so recovery is logged too and a
            // renewed failure after recovery logs again).
            var errorKey = ex.GetType().FullName ?? ex.GetType().Name;
            if (!string.Equals(_lastTelemetryError, errorKey, StringComparison.Ordinal))
            {
                _lastTelemetryError = errorKey;
                // Metrics discovery can invoke compiled module code with access to the server's
                // real settings. Never persist its arbitrary exception message, which could echo
                // a password/token/RCON value; the exception type is sufficient to correlate the
                // failure without leaking module-controlled text.
                AppLogService.Add($"Server Info telemetry refresh failed due to an internal error ({ex.GetType().Name}).", _server.Id);
            }
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task RefreshTelemetryCoreAsync(CancellationToken cancellationToken)
    {
        var telemetry = await RunTelemetryDiscoveryAsync(cancellationToken);
        var snapshot = telemetry.Snapshot;
        ServerInfoPresentationModel presentation;
        if (telemetry.SupportsQuery && _module != null)
        {
            try
            {
                var query = await RunBoundedModuleOperationAsync(
                    token => _module.QueryAsync(_instance, token),
                    ModuleQueryTimeout,
                    cancellationToken);
                presentation = ServerInfoPresentationModel.FromQuery(
                    query,
                    _maxPlayers,
                    supportsQuery: true);
            }
            catch (TimeoutException)
            {
                presentation = ServerInfoPresentationModel.FromQuery(
                    query: null,
                    _maxPlayers,
                    supportsQuery: true,
                    failureMessage: "Query timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // module.QueryAsync(_instance, ...) is arbitrary compiled module code called with
                // this server's real settings - its exception message could echo a password/
                // token/RCON value. failureMessage flows straight into QueryDiagnosticTextBlock.Text
                // (a user-visible panel), so this can't surface ex.Message any more than the
                // logging call sites elsewhere in this file can - same "arbitrary module code is
                // exactly as untrusted as its exception text" policy applied everywhere else.
                presentation = ServerInfoPresentationModel.FromQuery(
                    query: null,
                    _maxPlayers,
                    supportsQuery: true,
                    failureMessage: $"Query failed due to an internal error ({ex.GetType().Name}).");
            }
        }
        else
        {
            presentation = ServerInfoPresentationModel.FromQuery(
                query: null,
                _maxPlayers,
                supportsQuery: false);
        }

        PidTextBlock.Text = snapshot.ProcessIdText;
        CpuTextBlock.Text = snapshot.CpuText;
        MemoryTextBlock.Text = snapshot.MemoryText;
        ApplyPresentation(presentation);

        _samples.Enqueue(new ChartSample(
            snapshot.CpuPercent ?? 0,
            snapshot.MemoryBytes.HasValue ? snapshot.MemoryBytes.Value / 1024d / 1024d : 0));

        while (_samples.Count > MaxSamples)
        {
            _samples.Dequeue();
        }

        DrawChart();
    }

    private async Task<TelemetryDiscoveryResult> RunTelemetryDiscoveryAsync(
        CancellationToken cancellationToken)
    {
        // If synchronous module discovery hangs, retain that one outstanding task instead of
        // consuming another thread-pool worker on every telemetry timer tick.
        if (_telemetryDiscoveryTask == null || _telemetryDiscoveryTask.IsCompleted)
        {
            _telemetryDiscoveryTask = Task.Run(() => new TelemetryDiscoveryResult(
                _telemetryService.GetSnapshot(_module, _server.InstallPath, _maxPlayers),
                _module?.Capabilities.SupportsQuery == true));
        }

        var discoveryTask = _telemetryDiscoveryTask;
        try
        {
            return await discoveryTask.WaitAsync(ModuleQueryTimeout, cancellationToken);
        }
        catch
        {
            if (!discoveryTask.IsCompleted)
            {
                _ = discoveryTask.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            throw;
        }
    }

    private void ApplyPresentation(ServerInfoPresentationModel presentation)
    {
        PlayersTextBlock.Text = presentation.PlayersText;
        QueryMapTextBlock.Text = presentation.MapText;
        QueryGameVersionTextBlock.Text = presentation.GameVersionText;
        QueryProtocolTextBlock.Text = presentation.ProtocolText;
        QueryDurationTextBlock.Text = presentation.QueryDurationText;
        QueryStateTextBlock.Text = presentation.QueryStateText;
        QueryDiagnosticTextBlock.Text = presentation.DiagnosticText;
        QueryDiagnosticPanel.Visibility = presentation.HasDiagnostic
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlayerListStateTextBlock.Text = presentation.PlayerListStateText;
        PlayersItemsControl.ItemsSource = presentation.Players;
    }

    private void DrawChart()
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || _samples.Count == 0)
        {
            return;
        }

        var samples = _samples.ToArray();
        var values = _chartMetric == ChartMetric.Cpu
            ? samples.Select(sample => sample.CpuPercent).ToArray()
            : samples.Select(sample => sample.MemoryMb).ToArray();
        var maxValue = _chartMetric == ChartMetric.Cpu
            ? GetCpuScaleMax(values)
            : GetMemoryScaleMax(values);

        MetricLine.Points = BuildPoints(values, maxValue, width, height);
        MetricLine.Stroke = new SolidColorBrush(_chartMetric == ChartMetric.Cpu
            ? System.Windows.Media.Color.FromRgb(87, 184, 230)
            : System.Windows.Media.Color.FromRgb(242, 170, 46));

        UpdateChartScale(maxValue);
        UpdateChartCounters(values);
    }

    private static double GetCpuScaleMax(IReadOnlyCollection<double> values)
    {
        var observed = values.Count == 0 ? 10 : values.Max();
        return Math.Min(100, GetNiceScaleMax(Math.Max(10, observed)));
    }

    private static double GetMemoryScaleMax(IReadOnlyCollection<double> values)
    {
        var observed = values.Count == 0 ? 64 : values.Max();
        return GetNiceScaleMax(Math.Max(64, observed));
    }

    private static double GetNiceScaleMax(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 :
            normalized <= 2 ? 2 :
            normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private void UpdateChartScale(double maxValue)
    {
        ChartScaleMaxTextBlock.Text = FormatChartValue(maxValue);
        ChartScaleMidTextBlock.Text = FormatChartValue(maxValue / 2d);
        ChartScaleMinTextBlock.Text = FormatChartValue(0);
    }


    private void UpdateChartCounters(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            ChartCurrentTextBlock.Text = "--";
            ChartAverageTextBlock.Text = "--";
            ChartPeakTextBlock.Text = "--";
            return;
        }

        ChartCurrentTextBlock.Text = FormatChartValue(values.Last());
        ChartAverageTextBlock.Text = FormatChartValue(values.Average());
        ChartPeakTextBlock.Text = FormatChartValue(values.Max());
    }

    private string FormatChartValue(double value)
    {
        return _chartMetric == ChartMetric.Cpu
            ? $"{value:0.0}%"
            : FormatMemoryMb(value);
    }

    private static string FormatMemoryMb(double value)
    {
        return value >= 1024
            ? $"{value / 1024d:0.0} GB"
            : $"{value:0} MB";
    }

    private void CpuChartButton_Click(object sender, RoutedEventArgs e)
    {
        _chartMetric = ChartMetric.Cpu;
        UpdateChartMode();
        DrawChart();
    }

    private void MemoryChartButton_Click(object sender, RoutedEventArgs e)
    {
        _chartMetric = ChartMetric.Memory;
        UpdateChartMode();
        DrawChart();
    }

    private void UpdateChartMode()
    {
        ChartTitleTextBlock.Text = _chartMetric == ChartMetric.Cpu ? "CPU Over Time" : "Memory Over Time";
        CpuChartButton.FontWeight = _chartMetric == ChartMetric.Cpu ? FontWeights.SemiBold : FontWeights.Normal;
        MemoryChartButton.FontWeight = _chartMetric == ChartMetric.Memory ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static PointCollection BuildPoints(IEnumerable<double> values, double maxValue, double width, double height)
    {
        var list = values.ToArray();
        var points = new PointCollection();
        if (list.Length == 1)
        {
            var y = height - (Math.Clamp(list[0], 0, maxValue) / maxValue * height);
            points.Add(new System.Windows.Point(0, y));
            points.Add(new System.Windows.Point(width, y));
            return points;
        }

        for (var index = 0; index < list.Length; index++)
        {
            var x = index / (double)(list.Length - 1) * width;
            var y = height - (Math.Clamp(list[index], 0, maxValue) / maxValue * height);
            points.Add(new System.Windows.Point(x, y));
        }

        return points;
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawChart();
    }

    private void InfoTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowInfoView();
    }

    private void ConsoleTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowConsoleView();
    }

    private void ApiActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_module == null || _module is not IModuleApiActionCapability)
        {
            return;
        }

        var window = new ApiActionsWindow(_server, _module, _instance)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void HealthTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHealthView();
        await RefreshHealthAsync();
    }

    private async void NetworkingTabButton_Click(object sender, RoutedEventArgs e)
    {
        ShowNetworkingView();
        await RefreshPortForwardingAsync();
        await RefreshHealthAsync();
    }

    private async void RefreshNetworkingButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPortForwardingAsync();
        await RefreshHealthAsync();
    }

    private async void RefreshHealthButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshHealthAsync();
    }

    private void CopyHealthSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_healthRequest == null || _healthReport == null)
        {
            return;
        }

        System.Windows.Clipboard.SetText(_healthService.BuildSupportSummary(_healthRequest, _healthReport));
        HealthSummaryTextBlock.Text = $"{HealthSummaryTextBlock.Text} Support summary copied.";
    }

    private void CopyNetworkingSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        var networkChecks = _healthReport?.Checks.Where(IsNetworkingCheck).ToArray() ?? [];
        if (networkChecks.Length == 0)
        {
            NetworkingSummaryTextBlock.Text = "No networking results are available to copy.";
            return;
        }

        System.Windows.Clipboard.SetText(BuildNetworkingSummary(_server.Name, networkChecks));
        NetworkingSummaryTextBlock.Text = $"{BuildNetworkingStatus(networkChecks)} Networking summary copied.";
    }

    private void ShowInfoView()
    {
        InfoView.Visibility = Visibility.Visible;
        ConsoleView.Visibility = Visibility.Collapsed;
        NetworkingView.Visibility = Visibility.Collapsed;
        HealthView.Visibility = Visibility.Collapsed;
        InfoTabButton.FontWeight = FontWeights.SemiBold;
        ConsoleTabButton.FontWeight = FontWeights.Normal;
        NetworkingTabButton.FontWeight = FontWeights.Normal;
        HealthTabButton.FontWeight = FontWeights.Normal;
        ApiActionsButton.FontWeight = FontWeights.Normal;
    }

    private void ShowConsoleView()
    {
        UpdateConsoleInputState();
        InfoView.Visibility = Visibility.Collapsed;
        ConsoleView.Visibility = Visibility.Visible;
        NetworkingView.Visibility = Visibility.Collapsed;
        HealthView.Visibility = Visibility.Collapsed;
        InfoTabButton.FontWeight = FontWeights.Normal;
        ConsoleTabButton.FontWeight = FontWeights.SemiBold;
        NetworkingTabButton.FontWeight = FontWeights.Normal;
        HealthTabButton.FontWeight = FontWeights.Normal;
        ApiActionsButton.FontWeight = FontWeights.Normal;
        var consoleWasDirty = Volatile.Read(ref _consoleDirty) != 0;
        FlushConsolePending();
        if (!consoleWasDirty)
        {
            RebuildConsoleText();
        }
        ConsoleTextBox.Focus();
        if (_consoleViewState.AutoScrollEnabled)
        {
            Dispatcher.BeginInvoke(ScrollConsoleToEnd, DispatcherPriority.Background);
        }
    }

    private void ShowHealthView()
    {
        InfoView.Visibility = Visibility.Collapsed;
        ConsoleView.Visibility = Visibility.Collapsed;
        NetworkingView.Visibility = Visibility.Collapsed;
        HealthView.Visibility = Visibility.Visible;
        InfoTabButton.FontWeight = FontWeights.Normal;
        ConsoleTabButton.FontWeight = FontWeights.Normal;
        NetworkingTabButton.FontWeight = FontWeights.Normal;
        HealthTabButton.FontWeight = FontWeights.SemiBold;
        ApiActionsButton.FontWeight = FontWeights.Normal;
    }

    private void ShowNetworkingView()
    {
        InfoView.Visibility = Visibility.Collapsed;
        ConsoleView.Visibility = Visibility.Collapsed;
        NetworkingView.Visibility = Visibility.Visible;
        HealthView.Visibility = Visibility.Collapsed;
        InfoTabButton.FontWeight = FontWeights.Normal;
        ConsoleTabButton.FontWeight = FontWeights.Normal;
        NetworkingTabButton.FontWeight = FontWeights.SemiBold;
        HealthTabButton.FontWeight = FontWeights.Normal;
        ApiActionsButton.FontWeight = FontWeights.Normal;

        // Re-checked every time this tab is shown (not just once at window construction) so a
        // setting change made elsewhere while this window stays open is picked up without
        // requiring the user to close and reopen it.
        UpdateExternalReachabilityButtonState();
    }

    // Called from CollectionChanged on a background thread — must not touch WPF objects.
    // Accumulates removals and sets a dirty flag; the DispatcherTimer renders the batch.
    private void OnConsoleChanged_Background(NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Remove)
            AddPendingConsoleRemovals(args.OldItems?.Count ?? 1);
        Interlocked.Exchange(ref _consoleDirty, 1);
    }

    private void AddPendingConsoleRemovals(int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingRemoveCount);
            var updated = Math.Min(MaxPendingConsoleRemovals, current + Math.Max(0, count));
            if (Interlocked.CompareExchange(ref _pendingRemoveCount, updated, current) == current)
            {
                return;
            }
        }
    }

    private void FlushConsolePending()
    {
        if (ConsoleView.Visibility != Visibility.Visible)
        {
            return;
        }

        if (Interlocked.Exchange(ref _consoleDirty, 0) == 0) return;
        var removals = Interlocked.Exchange(ref _pendingRemoveCount, 0);
        if (removals > 0)
            _consoleViewState.OnLinesRemoved(removals);
        RebuildConsoleText();
    }

    private void RebuildConsoleText()
    {
        var shouldFollow = _consoleViewState.AutoScrollEnabled && IsConsoleAtBottom();
        ConsoleTextBox.Text = _consoleViewState.BuildVisibleText(_consoleService.GetLogSnapshot(_server.Id));
        if (shouldFollow) ScrollConsoleToEnd();
    }

    private void PauseConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        _consoleViewState.ToggleAutoScroll();
        PauseConsoleButton.Content = _consoleViewState.AutoScrollEnabled
            ? "Pause Auto-scroll"
            : "Resume Auto-scroll";
        if (_consoleViewState.AutoScrollEnabled)
        {
            ScrollConsoleToEnd();
        }
    }

    private void CopyConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ConsoleTextBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                _consoleService.Add(_server.Id, $"Could not copy to clipboard: {ex.Message}");
            }
        }
    }

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        // Drain accumulated prune events before taking the clear snapshot.
        // Those removals are already absent from the snapshot we are about to take;
        // if we let them flow through OnLinesRemoved on the next tick they would shift
        // the hidden-line marker backwards and expose pre-clear lines.
        Interlocked.Exchange(ref _pendingRemoveCount, 0);
        _consoleViewState.ClearView(_consoleService.GetLogSnapshot(_server.Id).Count);
        RebuildConsoleText();
    }

    private void ConsoleSearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _consoleViewState.SetSearch(ConsoleSearchTextBox.Text);
        var shouldFollow = _consoleViewState.AutoScrollEnabled && !_consoleViewState.IsSearchActive;
        ConsoleTextBox.Text = _consoleViewState.BuildVisibleText(_consoleService.GetLogSnapshot(_server.Id));
        if (shouldFollow)
        {
            ScrollConsoleToEnd();
        }
    }

    private bool IsConsoleAtBottom()
    {
        var viewer = FindScrollViewer(ConsoleTextBox);
        if (viewer == null)
        {
            return true;
        }

        if (viewer.ScrollableHeight <= 0)
        {
            return true;
        }

        return viewer.VerticalOffset >= viewer.ScrollableHeight - 12;
    }

    private void ScrollConsoleToEnd()
    {
        ConsoleTextBox.CaretIndex = ConsoleTextBox.Text.Length;
        ConsoleTextBox.ScrollToEnd();
        ConsoleTextBox.Dispatcher.BeginInvoke(() =>
        {
            ConsoleTextBox.CaretIndex = ConsoleTextBox.Text.Length;
            ConsoleTextBox.ScrollToEnd();
        }, DispatcherPriority.ContextIdle);
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(System.Windows.DependencyObject source)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is System.Windows.Controls.ScrollViewer viewer)
            {
                return viewer;
            }

            var found = FindScrollViewer(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private async Task RefreshHealthAsync()
    {
        if (_healthRefreshInFlight || _portForwardingRefreshInFlight)
        {
            // A refresh is already running (e.g. the Health tab and Refresh button were both
            // clicked in quick succession) - ignore the overlapping trigger instead of starting
            // a second concurrent evaluation.
            return;
        }

        _healthRefreshInFlight = true;
        UpdateRefreshButtonState();
        HealthSummaryTextBlock.Text = "Checking server health...";
        NetworkingSummaryTextBlock.Text = "Refreshing network status...";
        try
        {
            var cancellationToken = _refreshCancellation.Token;

            // Module discovery, firewall inspection, settings loading, and the health evaluation
            // itself all run synchronously (EvaluateAsync only actually awaits at its very last
            // step) - on a compiled module doing slow I/O, a large server collection, or a slow
            // firewall/COM query, running this directly on the calling (UI) thread would freeze
            // the whole window. Task.Run moves all of it to a background thread; only the final
            // field assignment and UI updates below run back on the UI thread, via the ordinary
            // async/await continuation - no manual Dispatcher marshalling needed.
            var (request, report) = await Task.Run(async () =>
            {
                var descriptors = new ModuleRegistry().GetModuleDescriptors();
                var descriptor = descriptors.FirstOrDefault(item =>
                    string.Equals(item.Id, _server.ModuleId, StringComparison.OrdinalIgnoreCase));
                var servers = await _serverLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<FirewallRuleStatus>? firewallRules = null;
                string? firewallError = null;
                if (descriptor != null)
                {
                    try
                    {
                        firewallRules = new WindowsFirewallService().GetRuleStatuses(_server, descriptor.Module);
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
                    recentOperations = OperationHistoryRepository.GetRecentForServer(_server.Id);
                }
                catch (Exception ex)
                {
                    recentOperationsError = $"Recent operation history could not be read: {ex.Message}";
                }

                var appSettings = AppSettings.Load();
                var builtRequest = new ServerHealthRequest(
                    _server,
                    descriptor,
                    servers,
                    firewallRules,
                    firewallError,
                    appSettings.PublicIpTrackingEnabled,
                    appSettings.LastKnownPublicIp,
                    appSettings.LastPublicIpCheckedAt,
                    descriptors,
                    recentOperations,
                    recentOperationsError);
                var builtReport = await _healthService.EvaluateAsync(builtRequest, cancellationToken).ConfigureAwait(false);
                return (builtRequest, builtReport);
            }, cancellationToken);

            _healthRequest = request;
            _healthReport = report with
            {
                Checks = MergePortForwardingChecks(
                    MergeExternalReachabilityChecks(report.Checks, _externalReachabilityChecks),
                    _portForwardingChecks)
            };
            UpdateHealthPresentation();
            UpdateNetworkingPresentation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Health evaluation can call compiled module code with access to real settings.
            // Preserve a useful correlation type without displaying arbitrary exception text.
            HealthSummaryTextBlock.Text = $"Health check failed due to an internal error ({ex.GetType().Name}).";
            NetworkingSummaryTextBlock.Text = $"Network status refresh failed due to an internal error ({ex.GetType().Name}). Port forwarding guidance shown below may still be available.";
        }
        finally
        {
            _healthRefreshInFlight = false;
            UpdateRefreshButtonState();
        }
    }

    private async Task RefreshPortForwardingAsync()
    {
        if (_portForwardingRefreshInFlight)
        {
            return;
        }

        _portForwardingRefreshInFlight = true;
        UpdateRefreshButtonState();
        NetworkingSummaryTextBlock.Text = "Generating local port forwarding guidance...";
        try
        {
            var checks = await Task.Run(ComputePortForwardingChecks, _refreshCancellation.Token);
            _portForwardingChecks = checks;
            var mergedChecks = MergePortForwardingChecks(_healthReport?.Checks ?? [], checks);
            _healthReport = (_healthReport ?? new ServerHealthReport(_server.Id, _server.Name, [])) with
            {
                Checks = mergedChecks
            };
            UpdateHealthPresentation();
            UpdateNetworkingPresentation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NetworkingSummaryTextBlock.Text =
                $"Port forwarding guidance failed due to an internal error ({ex.GetType().Name}).";
        }
        finally
        {
            _portForwardingRefreshInFlight = false;
            UpdateRefreshButtonState();
        }
    }

    private void UpdateRefreshButtonState()
    {
        var enabled = !_healthRefreshInFlight && !_portForwardingRefreshInFlight;
        RefreshHealthButton.IsEnabled = enabled;
        RefreshNetworkingButton.IsEnabled = enabled;
    }

    private async void TestExternalReachabilityButton_Click(object sender, RoutedEventArgs e)
    {
        await TestExternalReachabilityAsync();
    }

    // Deliberately separate from RefreshHealthAsync/EvaluateAsync's automatic pipeline - this is a
    // real, opt-in, user-triggered network call to an external service (see AppSettings.
    // ExternalReachabilityChecksEnabled), never something that should run as part of an ordinary
    // health refresh. Ports are resolved fresh on every click from the immutable declaration
    // snapshot captured when the module manifest was validated, rather than invoking arbitrary
    // compiled GetPorts() code or reusing a stale health report.
    private async Task TestExternalReachabilityAsync()
    {
        if (_externalReachabilityCheckInFlight || !TestExternalReachabilityButton.IsEnabled)
        {
            return;
        }

        _externalReachabilityCheckInFlight = true;
        TestExternalReachabilityButton.IsEnabled = false;
        TestExternalReachabilityButton.ToolTip = "An external reachability check is already running.";
        try
        {
            var cancellationToken = _refreshCancellation.Token;
            var settings = AppSettings.Load();
            if (!settings.ExternalReachabilityChecksEnabled ||
                !settings.ExternalReachabilityConsentAcknowledged)
            {
                ReplaceExternalReachabilityChecks([new ServerHealthCheck(
                    "Network",
                    "External reachability",
                    ServerHealthSeverity.Info,
                    "External reachability checks are disabled or have not been acknowledged. Enable them in Settings > Diagnostics to use this.")]);
                return;
            }

            var server = _server;
            var module = _module;
            if (module == null)
            {
                ReplaceExternalReachabilityChecks([new ServerHealthCheck(
                    "Network",
                    "External reachability",
                    ServerHealthSeverity.Info,
                    "This server's module could not be identified, so no ports could be resolved to test.")]);
                return;
            }

            if (!ModulePortSnapshotStore.TryGet(module, out var declaredPorts))
            {
                ReplaceExternalReachabilityChecks([new ServerHealthCheck(
                    "Network",
                    "External reachability",
                    ServerHealthSeverity.Info,
                    "Validated port metadata is unavailable for this module, so no external request was sent.")]);
                return;
            }

            // Resolve only the immutable declarations captured from the validated manifest at
            // module-load time. This path deliberately never invokes compiled module.GetPorts(),
            // so an untrusted module cannot occupy a worker indefinitely during a probe.
            var instance = ServerInstanceFactory.Load(server);
            var resolvedPorts = new ServerPortResolver().Resolve(declaredPorts, instance.Settings);
            var portSelection = BuildExternalReachabilityPortSelection(resolvedPorts);

            if (portSelection.TcpPorts.Length == 0)
            {
                ReplaceExternalReachabilityChecks([
                    new ServerHealthCheck(
                        "Network",
                        "External reachability",
                        ServerHealthSeverity.Info,
                        "No configured TCP ports were available to test."),
                    .. BuildExternalPortSelectionNotes(portSelection)]);
                return;
            }

            const int maxTestedPorts = 5;
            var testedPorts = portSelection.TcpPorts.Take(maxTestedPorts).ToArray();
            var truncated = portSelection.TcpPorts.Length > testedPorts.Length;

            var result = await new ExternalReachabilityService().CheckAsync(
                new ExternalReachabilityCheckRequest(true, testedPorts),
                cancellationToken);

            ReplaceExternalReachabilityChecks([
                .. BuildExternalReachabilityChecks(result, truncated, portSelection.TcpPorts.Length),
                .. BuildExternalPortSelectionNotes(portSelection)]);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Same discipline as RefreshHealthAsync's own catch below: never surface arbitrary
            // exception text (server settings and declarative port resolution are still untrusted
            // persisted inputs).
            ReplaceExternalReachabilityChecks([new ServerHealthCheck(
                "Network",
                "External reachability",
                ServerHealthSeverity.Info,
                $"External reachability check failed due to an internal error ({ex.GetType().Name}).")]);
        }
        finally
        {
            _externalReachabilityCheckInFlight = false;
            UpdateExternalReachabilityButtonState();
        }
    }

    internal static ExternalReachabilityPortSelection BuildExternalReachabilityPortSelection(
        IReadOnlyList<ResolvedPort> resolvedPorts)
    {
        var tcpPorts = new Dictionary<int, bool>();
        var udpDeclarations = 0;
        var unknownTransportDeclarations = 0;
        var unresolvedDeclarations = 0;
        var blockedPorts = 0;

        foreach (var port in resolvedPorts)
        {
            if (!port.OpenExternally)
            {
                continue;
            }

            if (port.Status != ResolvedPortStatus.Resolved || !port.Port.HasValue)
            {
                unresolvedDeclarations++;
                continue;
            }

            if (port.Protocol is PortProtocol.Udp or PortProtocol.Both)
            {
                udpDeclarations++;
            }

            if (port.Protocol == PortProtocol.Either)
            {
                unknownTransportDeclarations++;
                continue;
            }

            if (port.Protocol is not (PortProtocol.Tcp or PortProtocol.Both))
            {
                continue;
            }

            var start = port.Port.Value;
            var end = (long)start + Math.Max(1, port.RangeSize) - 1L;
            if (start is < 1 or > 65535 || end < start || end > 65535)
            {
                unresolvedDeclarations++;
                continue;
            }

            for (var candidate = start; candidate <= end; candidate++)
            {
                if (candidate == 25)
                {
                    blockedPorts++;
                }
                else
                {
                    tcpPorts[candidate] = port.Required ||
                        (tcpPorts.TryGetValue(candidate, out var alreadyRequired) && alreadyRequired);
                }
            }
        }

        return new ExternalReachabilityPortSelection(
            tcpPorts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => pair.Key)
                .ToArray(),
            udpDeclarations,
            unknownTransportDeclarations,
            unresolvedDeclarations,
            blockedPorts);
    }

    internal static IReadOnlyList<ServerHealthCheck> BuildExternalPortSelectionNotes(
        ExternalReachabilityPortSelection selection)
    {
        var checks = new List<ServerHealthCheck>();
        if (selection.UdpDeclarations > 0)
        {
            checks.Add(new ServerHealthCheck(
                "Network",
                "External reachability: UDP",
                ServerHealthSeverity.Info,
                $"Generic UDP reachability cannot be reliably determined, so {selection.UdpDeclarations} UDP port declaration(s) were not externally tested."));
        }

        if (selection.UnknownTransportDeclarations > 0)
        {
            checks.Add(new ServerHealthCheck(
                "Network",
                "External reachability: unknown transport",
                ServerHealthSeverity.Info,
                $"{selection.UnknownTransportDeclarations} port declaration(s) use an unknown/either transport and were not assumed to be TCP."));
        }

        if (selection.UnresolvedDeclarations > 0)
        {
            checks.Add(new ServerHealthCheck(
                "Network",
                "External reachability: incomplete port resolution",
                ServerHealthSeverity.Info,
                $"{selection.UnresolvedDeclarations} port declaration(s) could not be resolved, so this external inspection is incomplete."));
        }

        if (selection.BlockedPorts > 0)
        {
            checks.Add(new ServerHealthCheck(
                "Network",
                "External reachability: blocked port",
                ServerHealthSeverity.Info,
                $"Port 25 cannot be checked by the external service and was excluded from {selection.BlockedPorts} resolved TCP endpoint(s)."));
        }

        return checks;
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo, declared on this project's
    // own AssemblyInfo.cs) can exercise the outcome->severity/message mapping directly without
    // instantiating this WPF window.
    internal static IReadOnlyList<ServerHealthCheck> BuildExternalReachabilityChecks(
        ExternalReachabilityCheckResult result,
        bool truncated,
        int totalEligiblePorts)
    {
        const string category = "Network";
        const string name = "External reachability";

        if (result.Outcome != ExternalReachabilityOutcome.Success)
        {
            var message = result.Outcome == ExternalReachabilityOutcome.RateLimited
                ? result.Message + (result.RetryAfter.HasValue
                    ? $" Try again in about {Math.Max(1, (int)result.RetryAfter.Value.TotalSeconds)}s."
                    : " Try again shortly.")
                : result.Message;
            return [new ServerHealthCheck(category, name, ServerHealthSeverity.Info, message)];
        }

        var addressFamilyText = string.IsNullOrWhiteSpace(result.AddressFamily)
            ? "an unknown address family"
            : result.AddressFamily.ToUpperInvariant();

        var checks = (result.Results ?? [])
            .Select(port =>
            {
                var (severity, description) = port.Status switch
                {
                    ExternalPortReachability.Reachable =>
                        (ServerHealthSeverity.Pass, "reachable from outside your network"),
                    ExternalPortReachability.Refused =>
                        (ServerHealthSeverity.Warning, "reached, but nothing is accepting connections there - it may not be forwarded, or the server may not be listening"),
                    ExternalPortReachability.TimedOut =>
                        (ServerHealthSeverity.Warning, "did not respond - this is inconclusive, and may mean the port is blocked, not forwarded, or firewalled"),
                    _ => (ServerHealthSeverity.Info, "could not be determined for this port")
                };
                return new ServerHealthCheck(
                    category,
                    $"{name}: port {port.Port}",
                    severity,
                    $"Tested over {addressFamilyText}: {description}.");
            })
            .ToList();

        if (truncated)
        {
            checks.Add(new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Info,
                $"Only the first {checks.Count} of {totalEligiblePorts} configured TCP ports were tested (external service limit of 5)."));
        }

        return checks;
    }

    private void ReplaceExternalReachabilityChecks(IReadOnlyList<ServerHealthCheck> checks)
    {
        _externalReachabilityChecks = checks.ToArray();
        var mergedChecks = MergeExternalReachabilityChecks(_healthReport?.Checks ?? [], checks);
        _healthReport = (_healthReport ?? new ServerHealthReport(_server.Id, _server.Name, [])) with
        {
            Checks = mergedChecks
        };
        UpdateHealthPresentation();
        UpdateNetworkingPresentation();
    }

    private void UpdateExternalReachabilityButtonState()
    {
        var settings = AppSettings.Load();
        var enabled = CanEnableExternalReachabilityButton(
            settings.ExternalReachabilityChecksEnabled,
            settings.ExternalReachabilityConsentAcknowledged,
            _externalReachabilityCheckInFlight);
        TestExternalReachabilityButton.IsEnabled = enabled;
        TestExternalReachabilityButton.ToolTip = _externalReachabilityCheckInFlight
            ? "An external reachability check is already running."
            : enabled
                ? "Test configured TCP ports from outside your network."
                : "Enable external reachability checks in Settings > Diagnostics to use this.";
    }

    internal static bool CanEnableExternalReachabilityButton(
        bool enabledInSettings,
        bool consentAcknowledged,
        bool checkInFlight) =>
        enabledInSettings && consentAcknowledged && !checkInFlight;

    internal static IReadOnlyList<ServerHealthCheck> MergeExternalReachabilityChecks(
        IReadOnlyList<ServerHealthCheck> existingChecks,
        IReadOnlyList<ServerHealthCheck> newExternalChecks)
    {
        var retainedChecks = existingChecks
            .Where(check => !(string.Equals(check.Category, "Network", StringComparison.Ordinal) &&
                check.Name.StartsWith("External reachability", StringComparison.Ordinal)))
            .ToArray();
        return [.. retainedChecks, .. newExternalChecks];
    }

    internal sealed record ExternalReachabilityPortSelection(
        int[] TcpPorts,
        int UdpDeclarations,
        int UnknownTransportDeclarations,
        int UnresolvedDeclarations,
        int BlockedPorts);

    // Entirely local: no external network call, unlike TestExternalReachabilityAsync - so it needs
    // no settings gate, consent, or re-entrancy guard, and runs as part of the ordinary automatic
    // health/networking refresh (RefreshHealthAsync) rather than needing a separate user-triggered
    // button. It only reads already-resolved port declarations, local network interfaces, and
    // (indirectly, via ResolvedPort.Error) this server's own same-server port-overlap detection.
    // Called from RefreshHealthAsync's Task.Run background block, so this must not touch any UI
    // element directly - it only returns data for the caller to apply back on the UI thread.
    private IReadOnlyList<ServerHealthCheck> ComputePortForwardingChecks()
    {
        try
        {
            var module = _module;
            if (module == null)
            {
                return [new ServerHealthCheck(
                    "Network",
                    "Port forwarding",
                    ServerHealthSeverity.Info,
                    "This server's module could not be identified, so no port forwarding guidance could be generated.")];
            }

            if (!ModulePortSnapshotStore.TryGet(module, out var declaredPorts))
            {
                return [new ServerHealthCheck(
                    "Network",
                    "Port forwarding",
                    ServerHealthSeverity.Info,
                    "Validated port metadata is unavailable for this module, so no port forwarding guidance could be generated.")];
            }

            // Resolved fresh on every refresh, same discipline as TestExternalReachabilityAsync - a
            // stale _healthReport shouldn't decide what guidance is shown now.
            var instance = ServerInstanceFactory.Load(_server);
            var resolvedPorts = new ServerPortResolver().Resolve(declaredPorts, instance.Settings);
            return BuildPortForwardingChecks(resolvedPorts, GetLocalIPv4());
        }
        catch (Exception ex)
        {
            // Same discipline as the other health-adjacent catch blocks in this file: never surface
            // arbitrary exception text (server settings and declarative port resolution are still
            // untrusted persisted inputs).
            return [new ServerHealthCheck(
                "Network",
                "Port forwarding",
                ServerHealthSeverity.Info,
                $"Port forwarding guidance could not be generated due to an internal error ({ex.GetType().Name}).")];
        }
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise the
    // instruction-building logic directly. localIp is supplied by the caller (GetLocalIPv4(): a
    // real, un-fakeable OS call) rather than resolved in here, so this method itself stays pure and
    // testable against any address.
    internal static IReadOnlyList<ServerHealthCheck> BuildPortForwardingChecks(
        IReadOnlyList<ResolvedPort> resolvedPorts,
        string localIp)
    {
        const string category = "Network";
        var checks = new List<ServerHealthCheck>();
        var hasUsableDestination = TryNormalizePrivateLanIPv4(localIp, out var destinationIp);
        var needsDestination = resolvedPorts.Any(port =>
            port.OpenExternally &&
            port.Status == ResolvedPortStatus.Resolved &&
            port.Port.HasValue &&
            port.Protocol != PortProtocol.Either);

        if (needsDestination && !hasUsableDestination)
        {
            checks.Add(new ServerHealthCheck(
                category,
                "Port forwarding: destination address",
                ServerHealthSeverity.Warning,
                "A usable private LAN IPv4 address could not be identified for this computer, so no copyable forwarding instructions were generated. Check ipconfig or your router's connected-device list, then use this computer's 10.x.x.x, 172.16-31.x.x, or 192.168.x.x address."));
        }

        foreach (var port in resolvedPorts)
        {
            checks.Add(BuildPortForwardingCheck(
                port,
                hasUsableDestination ? destinationIp : null));
        }

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: dynamic IP addresses",
            ServerHealthSeverity.Info,
            hasUsableDestination
                ? $"This computer's detected local network address ({destinationIp}) can change if your router reassigns it (DHCP). Verify it against ipconfig or your router's connected-device list, then consider a DHCP reservation so the mapping keeps working."
                : "Local addresses can change when a router reassigns them (DHCP). After identifying the correct address, consider a DHCP reservation so the mapping keeps working."));

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: CGNAT and double NAT",
            ServerHealthSeverity.Info,
            "If forwarding still doesn't work after your router and firewall are configured correctly, your ISP may be using Carrier-Grade NAT, or you may have a double-NAT setup (e.g. an ISP-provided modem plus your own router). This is not checked automatically here - see this server's own Host Health view for a public/local address comparison, or contact your ISP."));

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: VPN adapters",
            ServerHealthSeverity.Info,
            "If a VPN is active on this computer, the detected local address above may belong to the VPN's virtual adapter rather than your real network, and forwarding instructions based on it will not work. Disable full-tunnel VPN software on this machine while setting up port forwarding."));

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: RCON and admin interfaces",
            ServerHealthSeverity.Info,
            "Never forward or otherwise expose RCON, web admin panels, or other management ports directly to the internet. Ports declared as management-only are marked accordingly above and excluded from the forwarding instructions."));

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: WindowsGSH's own web dashboard",
            ServerHealthSeverity.Info,
            "If you use WindowsGSH's own web dashboard/API, do not forward its port directly to the internet either - put it behind a reverse proxy (e.g. Caddy, nginx) or a private tunnel/VPN with its own authentication in front of it instead."));

        checks.Add(new ServerHealthCheck(
            category,
            "Port forwarding: other configured servers",
            ServerHealthSeverity.Info,
            "Conflicts with ports used by other servers configured on this machine are checked separately - see this tab's own \"Port conflicts\" result after Refresh."));

        return checks;
    }

    private static ServerHealthCheck BuildPortForwardingCheck(ResolvedPort port, string? localIp)
    {
        const string category = "Network";
        var name = $"Port forwarding: {port.Name}";

        if (!port.OpenExternally)
        {
            return new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Warning,
                $"{port.Name} is marked by the module as not intended for external exposure and should not be forwarded by default.");
        }

        if (port.Status == ResolvedPortStatus.Invalid)
        {
            if (port.FailureReason == PortResolutionFailureReason.Overlap)
            {
                return new ServerHealthCheck(
                    category,
                    name,
                    ServerHealthSeverity.Warning,
                    $"{port.Name} overlaps another port declared for this server on a shared protocol. Review the server's port settings before creating router mappings.");
            }

            return new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Warning,
                port.Required
                    ? $"{port.Name} is required, but a valid value could not be resolved. Check this server's configuration and the module's declaration."
                    : $"{port.Name}'s configured value or declaration is invalid. Check this server's configuration and the module's declaration.");
        }

        if (port.Status != ResolvedPortStatus.Resolved || !port.Port.HasValue)
        {
            return new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Info,
                $"{port.Name} is not currently configured, so no forwarding is needed for it yet.");
        }

        if (port.Protocol == PortProtocol.Either)
        {
            return new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Warning,
                $"{port.Name} may use TCP or UDP; its transport is unknown. Check the server's documentation and forward only the protocol it actually requires; WindowsGSH will not recommend exposing both protocols by default.");
        }

        if (localIp == null)
        {
            return new ServerHealthCheck(
                category,
                name,
                ServerHealthSeverity.Warning,
                $"{port.Name} resolves to port {(port.RangeSize > 1 ? $"{port.Port.Value}-{port.Port.Value + port.RangeSize - 1}" : port.Port.Value)}, but no forwarding instruction can be generated until this computer's private LAN IPv4 address is identified.");
        }

        var range = port.RangeSize > 1
            ? $"{port.Port.Value}-{port.Port.Value + port.RangeSize - 1}"
            : port.Port.Value.ToString();
        var protocolLabel = port.Protocol switch
        {
            PortProtocol.Tcp => "TCP",
            PortProtocol.Udp => "UDP",
            PortProtocol.Both => "TCP+UDP",
            _ => throw new InvalidOperationException("Unexpected port protocol.")
        };
        var destination = port.RangeSize > 1
            ? $"{localIp} (ports {range})"
            : $"{localIp}:{range}";

        return new ServerHealthCheck(
            category,
            name,
            ServerHealthSeverity.Info,
            $"Forward {protocolLabel} {(port.RangeSize > 1 ? "ports " : "")}{range} from your router to {destination}. Purpose: {port.Name}.");
    }

    internal static bool TryNormalizePrivateLanIPv4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!IPAddress.TryParse(value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var isPrivate =
            bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
        if (!isPrivate)
        {
            return false;
        }

        normalized = address.ToString();
        return true;
    }

    private void UpdateHealthPresentation()
    {
        var healthChecks = SelectHealthTabChecks(_healthReport?.Checks ?? []);
        HealthItemsControl.ItemsSource = healthChecks.Select(check => new HealthRow(
            check.Severity.ToString().ToUpperInvariant(),
            $"{check.Category} / {check.Name}",
            check.Message));

        var failures = healthChecks.Count(check => check.Severity == ServerHealthSeverity.Fail);
        var warnings = healthChecks.Count(check => check.Severity == ServerHealthSeverity.Warning);
        HealthSummaryTextBlock.Text = healthChecks.Count == 0
            ? "No non-network health results are available. See Networking for port and connectivity results."
            : $"{failures} failure(s), {warnings} warning(s), {healthChecks.Count} health result(s). Network results are shown on the Networking tab.";
    }

    private void UpdateNetworkingPresentation()
    {
        var networkChecks = _healthReport?.Checks
            .Where(IsNetworkingCheck)
            .ToArray() ?? [];
        NetworkingItemsControl.ItemsSource = networkChecks.Select(check => new HealthRow(
            check.Severity.ToString().ToUpperInvariant(),
            check.Name,
            check.Message));

        NetworkingSummaryTextBlock.Text = networkChecks.Length == 0
            ? "No networking results are available yet."
            : BuildNetworkingStatus(networkChecks);
    }

    private static string BuildNetworkingStatus(IReadOnlyList<ServerHealthCheck> networkChecks)
    {
        var failures = networkChecks.Count(check => check.Severity == ServerHealthSeverity.Fail);
        var warnings = networkChecks.Count(check => check.Severity == ServerHealthSeverity.Warning);
        return $"{failures} failure(s), {warnings} warning(s), {networkChecks.Count} network result(s).";
    }

    internal static string BuildNetworkingSummary(
        string serverName,
        IReadOnlyList<ServerHealthCheck> networkChecks)
    {
        var lines = new List<string>
        {
            $"WindowsGSH Networking Summary - {serverName}",
            $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            string.Empty
        };
        lines.AddRange(networkChecks.Select(check =>
            $"[{check.Severity.ToString().ToUpperInvariant()}] {check.Name}: {check.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyList<ServerHealthCheck> SelectHealthTabChecks(
        IReadOnlyList<ServerHealthCheck> checks) =>
        checks.Where(check => !IsNetworkingCheck(check)).ToArray();

    private static bool IsNetworkingCheck(ServerHealthCheck check) =>
        string.Equals(check.Category, "Network", StringComparison.Ordinal);

    internal static IReadOnlyList<ServerHealthCheck> MergePortForwardingChecks(
        IReadOnlyList<ServerHealthCheck> existingChecks,
        IReadOnlyList<ServerHealthCheck> newPortForwardingChecks)
    {
        var retainedChecks = existingChecks
            .Where(check => !(string.Equals(check.Category, "Network", StringComparison.Ordinal) &&
                check.Name.StartsWith("Port forwarding", StringComparison.Ordinal)))
            .ToArray();
        return [.. retainedChecks, .. newPortForwardingChecks];
    }

    // Mirrors HostHealthView's own local-IP detection exactly (kept as a separate copy rather than
    // extracted into a shared helper, to avoid touching that unrelated, already-working file for
    // this change). Primary: a UDP "connect" lets the OS pick the routing interface without
    // actually sending any traffic. Fallback: enumerate physical adapters for offline/LAN-only
    // hosts, preferring Ethernet then WiFi.
    private static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            var ip = ((IPEndPoint)socket.LocalEndPoint!).Address;
            if (!IPAddress.IsLoopback(ip))
            {
                return ip.ToString();
            }
        }
        catch
        {
        }

        try
        {
            string? wireless = null;
            string? other = null;

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Slip)
                {
                    continue;
                }

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(addr.Address))
                    {
                        continue;
                    }

                    var ipText = addr.Address.ToString();
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                    {
                        return ipText;
                    }

                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        wireless ??= ipText;
                    }
                    else
                    {
                        other ??= ipText;
                    }
                }
            }

            return wireless ?? other ?? "Unknown";
        }
        catch
        {
        }

        return "Unknown";
    }

    private async void SendRconButton_Click(object sender, RoutedEventArgs e)
    {
        var sendButton = sender as System.Windows.Controls.Button;
        var command = RconCommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            if (_module == null)
            {
                _consoleService.Add(_server.Id, "Module is not available.");
                return;
            }

            _consoleService.Add(_server.Id, $"> rcon {command}");
            if (sendButton != null)
            {
                sendButton.IsEnabled = false;
            }
            var response = await RunBoundedModuleOperationAsync(
                token => _module.ExecuteRconCommandAsync(_instance, command, token),
                RconCommandTimeout,
                _refreshCancellation.Token);
            foreach (var line in response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                _consoleService.Add(_server.Id, line);
            }

            RconCommandTextBox.Clear();
        }
        catch (TimeoutException)
        {
            _consoleService.Add(_server.Id, "RCON command timed out.");
        }
        catch (OperationCanceledException)
        {
            // Window closure cancels in-flight work; no error needs to be appended to a closing UI.
        }
        catch (Exception ex)
        {
            // RCON module code receives the real instance/settings; never echo its arbitrary
            // exception message into the user-visible console.
            _consoleService.Add(_server.Id, $"RCON failed due to an internal error ({ex.GetType().Name}).");
        }
        finally
        {
            if (sendButton != null)
            {
                sendButton.IsEnabled = true;
            }
        }
    }

    private void SendConsoleCommandButton_Click(object sender, RoutedEventArgs e)
    {
        SendConsoleCommand();
    }

    private void ConsoleCommandTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SendConsoleCommand();
    }

    private async void SendConsoleCommand()
    {
        var command = ConsoleCommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (_module == null)
        {
            _consoleService.Add(_server.Id, "Console command failed: module is not loaded.");
            return;
        }

        try
        {
            _consoleService.Add(_server.Id, $"> {command}");
            var response = await RunBoundedModuleOperationAsync(
                token => _consoleService.ExecuteModuleCommandAsync(
                    _module,
                    _instance,
                    command,
                    token),
                ConsoleCommandTimeout,
                _refreshCancellation.Token);
            foreach (var line in response.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                _consoleService.Add(_server.Id, line);
            }
            ConsoleCommandTextBox.Clear();
        }
        catch (TimeoutException)
        {
            _consoleService.Add(_server.Id, "Console command timed out.");
        }
        catch (OperationCanceledException)
        {
            // Window closure cancels in-flight work.
        }
        catch (Exception ex)
        {
            // ExecuteModuleCommandAsync's non-Redirected fallback calls into arbitrary module code
            // (IModuleConsoleCommandCapability.ExecuteConsoleCommandAsync) with this server's real
            // instance/settings - its exception message could echo a password/token/RCON value.
            // The Redirected-strategy path's own exceptions are already fixed, generic text (see
            // ServerConsoleService.SendCommand), but this catch can't tell which source an
            // exception came from, so it never surfaces ex.Message either way - same policy as
            // every other arbitrary-module-code catch in this file.
            _consoleService.Add(_server.Id, $"Console command failed due to an internal error ({ex.GetType().Name}).");
        }
        finally
        {
            UpdateConsoleInputState();
        }
    }

    private void UpdateConsoleInputState()
    {
        var isDisabled = _consoleService.IsConsoleInputDisabled(_server.Id);
        ConsoleCommandInputPanel.Visibility = isDisabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        ConsoleInputDisabledPanel.Visibility = isDisabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void RestartForConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            $"Restart {_server.Name} to restore console input?",
            "Restore Console Input",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes || Owner is not MainWindow mainWindow)
        {
            return;
        }

        RestartForConsoleButton.IsEnabled = false;
        try
        {
            await mainWindow.RestartServerFromInfoAsync(_server);
            UpdateConsoleInputState();
        }
        finally
        {
            RestartForConsoleButton.IsEnabled = true;
        }
    }

    private static async Task<T> RunBoundedModuleOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task.Run isolates synchronous work performed before a nominally-async module method
        // returns its Task. WaitAsync supplies the hard deadline even if the module ignores the
        // cancellation token entirely.
        var operationTask = Task.Run(
            () => operation(operationCancellation.Token),
            CancellationToken.None);
        try
        {
            return await operationTask.WaitAsync(timeout, cancellationToken);
        }
        catch
        {
            operationCancellation.Cancel();
            _ = operationTask.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private sealed record TelemetryDiscoveryResult(
        ServerTelemetrySnapshot Snapshot,
        bool SupportsQuery);

    private void AddonConfigButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: AddonRow addon })
        {
            return;
        }

        var window = new ServerConfigEditorWindow(
            _server,
            addon.Id,
            _upnpMappingLifecycleService,
            _hasLiveMonitoredProcess)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void ForceStopButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            $"Force stop {_server.Name}?\n\nThis immediately kills the server process tree and should only be used when normal Stop is stuck.",
            "Force Stop Server",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        ForceStopButton.IsEnabled = false;
        try
        {
            if (Owner is MainWindow mainWindow)
            {
                await mainWindow.ForceStopServerAsync(_server);
            }
        }
        finally
        {
            ForceStopButton.IsEnabled = true;
        }
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

    private static object? GetSetting(IReadOnlyDictionary<string, object?> settings, string key)
    {
        return !string.IsNullOrWhiteSpace(key) && settings.TryGetValue(key, out var value) ? value : null;
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            int number => number != 0,
            long number => number != 0,
            double number => Math.Abs(number) > double.Epsilon,
            _ => value != null
        };
    }

    private static bool IsApiEnabled(string enabledKey, object? value)
    {
        return string.IsNullOrWhiteSpace(enabledKey) || IsTruthy(value);
    }

    private sealed record ChartSample(double CpuPercent, double MemoryMb);

    private enum ChartMetric
    {
        Cpu,
        Memory
    }

    private sealed record AddonRow(
        string Id,
        string Name,
        string Description,
        bool IsInstalled,
        bool IsEnabled,
        string StatusText);

    private sealed record HealthRow(string SeverityText, string Heading, string Message);
}
