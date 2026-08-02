using System.Windows.Threading;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Metrics;
using WindowsGSH.Core.Network;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web.Api;

namespace WindowsGSH;

public partial class MainWindow
{
    private readonly DispatcherTimer _serverRefreshTimer = new();
    private readonly PublicIpService _publicIpService = new();
    private readonly SystemMetricsService _systemMetricsService = new();
    private readonly SystemMetricHistory _systemMetricHistory = new();
    private readonly NetworkBandwidthService _networkBandwidthService = new();
    private bool _refreshingServers;
    private bool _runningServerMaintenance;
    private CancellationTokenSource? _systemMetricsCancellation;
    private Task? _systemMetricsTask;
    private static readonly TimeSpan SystemMetricsFailureReminderInterval = TimeSpan.FromMinutes(15);

    private async void ServerRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshInstalledServersSafelyAsync("server refresh").ConfigureAwait(true);
    }

    private async Task RefreshInstalledServersSafelyAsync(string context)
    {
        try
        {
            await RefreshInstalledServersAsync();
        }
        catch (Exception ex)
        {
            AppLogService.Add($"{context} failed: {ex.Message}");
        }
    }

    private void StartSystemMetricsSampling()
    {
        if (_systemMetricsTask is { IsCompleted: false } &&
            _systemMetricsCancellation?.IsCancellationRequested != true)
        {
            return;
        }

        var previousCancellation = _systemMetricsCancellation;
        var previousTask = _systemMetricsTask;
        _systemMetricsCancellation = new CancellationTokenSource();
        _systemMetricsTask = RunSystemMetricsSamplingAsync(_systemMetricsCancellation.Token);
        if (previousCancellation != null)
        {
            _ = (previousTask ?? Task.CompletedTask).ContinueWith(
                _ => previousCancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void StopSystemMetricsSampling()
    {
        _systemMetricsCancellation?.Cancel();
    }

    private async Task RunSystemMetricsSamplingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        string? lastFailureKey = null;
        var lastFailureLoggedAt = default(DateTimeOffset);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _systemMetricsService.SampleAsync(cancellationToken).ConfigureAwait(false);
                    var (upload, download) = _networkBandwidthService.Sample();
                    var fullSnapshot = snapshot with
                    {
                        UploadBytesPerSecond = upload,
                        DownloadBytesPerSecond = download
                    };
                    _systemMetricHistory.Add(fullSnapshot);
                    WebServerState.UpdateHostMetrics(fullSnapshot);
                    await Dispatcher.InvokeAsync(() => UpdateSystemMetricsView(fullSnapshot));

                    if (lastFailureKey != null)
                    {
                        AppLogService.Add("System metrics sampling recovered.");
                        lastFailureKey = null;
                        lastFailureLoggedAt = default;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A single transient failure (a slow WMI query, a locked drive handle, etc.)
                    // must not permanently stop host metrics sampling for the rest of the app's
                    // lifetime - nothing else ever restarts this loop once it returns. Log and
                    // retry on the next tick instead. ex.Message is safe here - SystemMetricsService
                    // only ever wraps internal, app-owned host telemetry (CPU/RAM/disk/network),
                    // never module or settings data.
                    var now = DateTimeOffset.UtcNow;
                    var failureKey = $"{ex.GetType().FullName}:{ex.Message}";
                    if (!string.Equals(lastFailureKey, failureKey, StringComparison.Ordinal) ||
                        now - lastFailureLoggedAt >= SystemMetricsFailureReminderInterval)
                    {
                        AppLogService.Add("System metrics sampling failed: " + ex.Message);
                        lastFailureKey = failureKey;
                        lastFailureLoggedAt = now;
                    }
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Safety net for anything outside the inner try (e.g. the timer itself) - the inner
            // catch above already handles the common "one sampling call failed" case without
            // stopping the loop.
            AppLogService.Add("System metrics sampling stopped: " + ex.Message);
        }
    }

    private void UpdateSystemMetricsView(SystemMetricsSnapshot snapshot)
    {
        HostCpuUsageTextBlock.Text = snapshot.TotalCpuText;
        HostCpuDetailTextBlock.Text =
            $"{snapshot.LogicalProcessorCount} logical processor(s) · {snapshot.CpuModel}";
        HostMemoryTextBlock.Text = snapshot.MemoryText;
        HostDriveTextBlock.Text = snapshot.DriveText;
        HostDriveDetailTextBlock.Text =
            $"{snapshot.DriveRoot} · {snapshot.DriveFormat} · {snapshot.DriveUsageText}";
        HostAppProcessTextBlock.Text = snapshot.ProcessText;
        SystemMetricsUpdatedTextBlock.Text = snapshot.Error == null
            ? $"Updated {snapshot.SampledAt.ToLocalTime():HH:mm:ss}"
            : $"Unavailable: {snapshot.Error}";

        if (HostHealthView.Visibility == System.Windows.Visibility.Visible)
        {
            HostHealthView.UpdateMetrics(snapshot, _systemMetricHistory.GetHistory());
        }
    }

    private async Task RefreshInstalledServersAsync()
    {
        if (_refreshingServers)
        {
            return;
        }

        _refreshingServers = true;
        try
        {
            var servers = await LoadServersInBackgroundAsync();
            UpdateInstalledServersView(servers);
            _ = _runtimeTracker.AttachRunningServerProcessesAsync(servers, GetModule);
            _ = RunServerMaintenanceAsync(servers);
        }
        finally
        {
            _refreshingServers = false;
        }
    }

    private async Task LoadInitialServerCardsAsync()
    {
        try
        {
            // The full loader waits for live queries and metrics before returning any servers.
            // Render a lightweight local snapshot first so startup never presents an empty server
            // list while one or more running servers consume their query timeout.
            var servers = await Task.Run(_installedServerLoader.LoadInitialCards);
            UpdateInstalledServersView(servers);
        }
        catch (Exception ex)
        {
            AppLogService.Add(
                $"Initial server cards could not be loaded due to an internal error ({ex.GetType().Name}).");
        }

        // Replace the lightweight cards with the existing full status/query/metrics result.
        await RefreshInstalledServersSafelyAsync("initial server refresh");
    }

    private async Task RunServerMaintenanceAsync(IReadOnlyList<InstalledServer> servers)
    {
        if (_runningServerMaintenance || _bulkActionInProgress)
        {
            return;
        }

        _runningServerMaintenance = true;
        try
        {
            await RunCronSchedulesAsync(servers);
            await RunScheduledAutoUpdatesAsync(servers);
            await RunServerAutomationAsync(servers);
            await RunPublicIpTrackingAsync(servers);
            await _upnpMappingLifecycleService.ReconcileOrphanedMappingsAsync(
                servers.Select(server => server.Id).ToHashSet(StringComparer.Ordinal),
                CancellationToken.None);
            await RefreshServerHealthIssuesAsync(servers);
            PruneStaleServerMetrics(servers);
        }
        catch (Exception ex)
        {
            AppLogService.Add("Server automation failed: " + ex.Message);
        }
        finally
        {
            _runningServerMaintenance = false;
        }
    }

    private void PruneStaleServerMetrics(IReadOnlyList<InstalledServer> servers)
    {
        var staleThreshold = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        _installedServerLoader.MetricsService.PruneStale(staleThreshold);
    }

    private async Task RunPublicIpTrackingAsync(IReadOnlyList<InstalledServer> servers)
    {
        if (!_settings.PublicIpTrackingEnabled)
        {
            return;
        }

        var endpointText = string.IsNullOrWhiteSpace(_settings.PublicIpEndpoint) ? "https://api.ipify.org" : _settings.PublicIpEndpoint;
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            AppLogService.Add("Public IP tracking skipped: endpoint is invalid.");
            return;
        }

        if (!await PublicIpEndpointPolicy.IsAllowedEndpointAsync(endpoint))
        {
            // Enforced here too, not just on Settings save (P2 follow-up to P3-04): an endpoint
            // saved before this validation existed, or hand-edited in the settings file, must
            // not keep sending plaintext/private-network requests indefinitely just because the
            // Settings page was never re-saved.
            AppLogService.Add("Public IP tracking skipped: configured endpoint must be HTTPS and not a loopback/private/link-local address. Update it on the Settings page.");
            return;
        }

        var result = await _publicIpService.CheckAsync(new PublicIpCheckRequest(
            Enabled: true,
            Endpoint: endpoint,
            CheckInterval: TimeSpan.FromMinutes(Math.Max(1, _settings.PublicIpCheckIntervalMinutes)),
            LastKnownIp: _settings.LastKnownPublicIp,
            LastCheckedAt: _settings.LastPublicIpCheckedAt,
            ServerConfigPaths: servers.Select(server => server.ConfigPath).ToArray()));
        if (!result.Checked)
        {
            return;
        }

        _settings.LastPublicIpCheckedAt = result.CheckedAt;
        if (result.Success && !string.IsNullOrWhiteSpace(result.CurrentIp))
        {
            _settings.LastKnownPublicIp = result.CurrentIp;
        }

        _settings.Save();
        if (!result.Success)
        {
            AppLogService.Add(result.Message);
        }
    }

    private Task DispatchRefreshInstalledServersViewOnlyAsync()
    {
        return Dispatcher.InvokeAsync(RefreshInstalledServersViewOnlyAsync).Task.Unwrap();
    }

    private async Task RefreshInstalledServersViewOnlyAsync()
    {
        try
        {
            var servers = await LoadServersInBackgroundAsync();
            await Dispatcher.InvokeAsync(() => UpdateInstalledServersView(servers));
        }
        catch
        {
            // The normal refresh loop will report state again shortly.
        }
    }

    private Task<IReadOnlyList<InstalledServer>> LoadServersInBackgroundAsync()
    {
        return Task.Run(() => _installedServerLoader.LoadAsync());
    }

    private void UpdateInstalledServersView(IReadOnlyList<InstalledServer> servers)
    {
        _discordBotHost.UpdateServerSnapshot(servers);
        var selectedSource = (LogFilterComboBox.SelectedItem as ServerLogFilterItem)?.Source;
        var state = _serverListViewModel.Update(servers, selectedSource);
        WebServerState.UpdateServers(_serverListViewModel.LastVisibleServers);
        UpdateServerSummary(state);
        _serversViewIsEmpty = state.IsEmpty;
        EmptyServersPanel.Visibility = state.IsEmpty
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        UpdateFirstRunChecklist();

        var serversWithTrends = ApplyMetricTrends(state.VisibleServers);
        InstalledServersItemsControl.ItemsSource = serversWithTrends;

        if (state.Changed)
        {
            RefreshLogFilters(state);
        }

        if (HostHealthView.Visibility == System.Windows.Visibility.Visible)
        {
            HostHealthView.RefreshContext(_serverListViewModel.LastVisibleServers, _settings.LastKnownPublicIp);
        }
    }

    private IReadOnlyList<InstalledServer> ApplyMetricTrends(IReadOnlyList<InstalledServer> servers)
    {
        var metricsService = _installedServerLoader.MetricsService;
        return servers
            .Select(server =>
            {
                var history = metricsService.GetHistory(server.Id);
                if (history.Count < 3)
                {
                    return server;
                }

                var cpuTrend = ComputeCpuTrend(history);
                var memTrend = ComputeMemoryTrend(history);
                if (cpuTrend == server.CpuTrend && memTrend == server.MemoryTrend)
                {
                    return server;
                }

                return server with { CpuTrend = cpuTrend, MemoryTrend = memTrend };
            })
            .ToArray();
    }

    private static string ComputeCpuTrend(IReadOnlyList<ServerMetricsSnapshot> history)
    {
        var values = history
            .TakeLast(5)
            .Select(s => s.CpuPercent)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
        if (values.Length < 2)
        {
            return "";
        }

        var delta = values[^1] - values[0];
        return delta > 3.0 ? " ↑" : delta < -3.0 ? " ↓" : "";
    }

    private static string ComputeMemoryTrend(IReadOnlyList<ServerMetricsSnapshot> history)
    {
        var values = history
            .TakeLast(5)
            .Select(s => s.MemoryBytes)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
        if (values.Length < 2)
        {
            return "";
        }

        var delta = values[^1] - values[0];
        const long threshold = 50 * 1024 * 1024; // 50 MB
        return delta > threshold ? " ↑" : delta < -threshold ? " ↓" : "";
    }
}
