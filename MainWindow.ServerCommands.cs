using System.Windows;
using WindowsGSH.Core.Automation;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public partial class MainWindow
{
    private async void StartServerButton_Click(object sender, RoutedEventArgs e)
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
            await _serverOperations.StartManualAsync(server);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void UpdateServerButton_Click(object sender, RoutedEventArgs e)
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
            if (server.CanStop)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Stop the server before updating it.",
                    "Server Running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            await _serverOperations.UpdateManualAsync(server);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void StopServerButton_Click(object sender, RoutedEventArgs e)
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
            await _serverOperations.StopManualAsync(server);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task CancelActiveServerOperationAsync(InstalledServer server)
    {
        if (!_operationManager.Cancel(server.Id))
        {
            AppLogService.Add($"{server.Name} does not have an active operation to stop.", server.Id);
            await RefreshInstalledServersViewOnlyAsync();
            return;
        }

        _manuallyStoppedServers.Add(server.Id);
        _lastAutomationChecks[server.Id] = DateTimeOffset.UtcNow;
        AppLogService.Add($"Stopping active operation for {server.Name}.", server.Id);
        _runtimeTracker.StopLogTail(server.Id);
        _runtimeTracker.StopAddonProcesses(server.Id);
        try
        {
            await ServerForceStopper.KillAsync(
                GetModule(server),
                ServerInstanceFactory.Load(server),
                CancellationToken.None,
                new ServerForceStopOptions(server.Id, server.Name, GracefulStopAttempted: false, AppLogService.Add));
        }
        catch
        {
            // Some active operations, such as SteamCMD updates, do not have a game process yet.
        }

        await RefreshInstalledServersViewOnlyAsync();
    }

    private async void RestartServerButton_Click(object sender, RoutedEventArgs e)
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
            await _serverOperations.RestartManualAsync(server);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    internal async Task RestartServerFromInfoAsync(InstalledServer server)
    {
        await _serverOperations.RestartManualAsync(
            server,
            description: "Restarted from Server Info to restore console input.");
    }

    private async Task StartServerAsync(InstalledServer server, bool automatic, CancellationToken cancellationToken)
    {
        var instance = ServerInstanceFactory.Load(server);
        var startOptions = CreateLifecycleStartOptions(server, automatic);
        ServerOperationResult result;
        try
        {
            result = await Task.Run(
                () => _lifecycleService.StartAsync(
                    instance,
                    startOptions,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseStartAttempt(server.Id);
            throw;
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(result.LastError ?? $"Failed to start {server.Name}.");
        }

        await RefreshInstalledServersViewOnlyAsync();
    }

    private ServerLifecycleStartOptions CreateLifecycleStartOptions(InstalledServer server, bool automatic)
    {
        return new ServerLifecycleStartOptions(
            Automatic: automatic,
            Log: AppLogService.Add,
            SetStatus: (_, status) => SetOperationStatus(server.Id, status),
            ClearStatus: _ => ClearOperationStatus(server.Id),
            MarkBooting: _ => _serverStatusComposer.MarkBooting(server.Id),
            ClearBooting: _ => _serverStatusComposer.ClearBooting(server.Id),
            StartFailed: _ => ReleaseStartAttempt(server.Id),
            HasLiveMonitoredProcess: _ => _runtimeTracker.HasLiveMonitoredProcess(server.Id),
            BeforeStartAsync: async (instance, module, isAutomatic, token) =>
            {
                var readinessIssues = _readinessCheckService.CheckServerStartSafety(server)
                    .Where(check => check.Status == ReadinessStatus.Fail)
                    .ToArray();
                if (readinessIssues.Length > 0)
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, readinessIssues.Select(issue => issue.Message)));
                }

                await PrepareServerStartAsync(server, module, instance, isAutomatic, token);
            },
            ProcessStarted: (instance, module, process, attached) =>
            {
                _runtimeTracker.AttachStartedProcess(server, module, instance, process, attached);
            },
            AddonProcessStarted: (_, _, addonProcess) => _runtimeTracker.AttachAddonProcess(server.Id, addonProcess),
            AfterStartAsync: (instance, module, token) => RunNonCriticalLifecycleHookAsync(
                hookToken => _upnpMappingLifecycleService.MapOnStartAsync(server, module, instance, hookToken),
                token,
                message => AppLogService.Add(message, server.Id),
                "UPnP port mapping was cancelled; the game server is still running.",
                "UPnP port mapping failed"));
    }

    private ServerLifecycleStopOptions CreateLifecycleStopOptions(InstalledServer server, TimeSpan? stopDelay = null)
    {
        return new ServerLifecycleStopOptions(
            Log: AppLogService.Add,
            SetStatus: (_, status) => SetOperationStatus(server.Id, status),
            MarkExpectedProcessExits: _runtimeTracker.MarkExpectedProcessExits,
            StopLogTail: _runtimeTracker.StopLogTail,
            StopAddonProcesses: serverId => _runtimeTracker.StopAddonProcesses(serverId),
            StopDelay: stopDelay,
            AfterStopAsync: (instance, _, token) => RunNonCriticalLifecycleHookAsync(
                hookToken => _upnpMappingLifecycleService.UnmapOnStopAsync(server, instance, hookToken),
                token,
                message => AppLogService.Add(message, server.Id),
                "UPnP port mapping removal was cancelled; the game server is already stopped.",
                "UPnP port mapping removal failed"));
    }

    internal static async Task RunNonCriticalLifecycleHookAsync(
        Func<CancellationToken, Task> hook,
        CancellationToken cancellationToken,
        Action<string> log,
        string cancellationMessage,
        string failurePrefix)
    {
        try
        {
            await hook(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process transition has already completed before an AfterStart/AfterStop hook
            // runs. Cancellation should stop optional router work, not rewrite that completed
            // server operation as cancelled.
            log(cancellationMessage);
        }
        catch (Exception ex)
        {
            log($"{failurePrefix}: {ex.Message}");
        }
    }

    private async Task PrepareServerStartAsync(InstalledServer server, IGameServerModule module, ServerInstance instance, bool automatic, CancellationToken cancellationToken)
    {
        var backup = ReadConfigObject(server.ConfigPath, "backup");
        var actions = _automationService.PlanBeforeStart(instance.AppSettings.Automation, automatic);

        if (actions.Contains(AutomationBeforeStartAction.Backup) && module.Capabilities.SupportsBackups)
        {
            SetOperationStatus(server.Id, "Backing up");
            var paths = ReadStringArray(backup, "paths");
            if (paths.Count == 0)
            {
                paths = module.GetBackupTargets().Select(target => target.RelativePath).ToArray();
            }

            var backupResult = await _backupService.CreateBackupAsync(instance, module, paths, _settings.BackupRetentionCount, cancellationToken);
            var backupPath = backupResult.BackupPath ?? throw new InvalidOperationException(backupResult.Message);
            AppLogService.Add(
                $"Backup created before start for {server.Name}: {System.IO.Path.GetFileName(backupPath) + backupResult.BuildWarningSuffix()}",
                server.Id);
        }

        if (actions.Contains(AutomationBeforeStartAction.Update))
        {
            if (WasUpdatedRecently(server.ConfigPath, TimeSpan.FromMinutes(5)))
            {
                AppLogService.Add($"Skipping before-start update for {server.Name}: updated recently.", server.Id);
                return;
            }

            SetOperationStatus(server.Id, "Updating");
            await UpdateServerAsync(server, module, "before start", cancellationToken);
        }
    }

    private async Task UpdateServerAsync(
        InstalledServer server,
        IGameServerModule module,
        string reason,
        CancellationToken cancellationToken,
        bool skipIgnoredBuild = false)
    {
        var instance = ServerInstanceFactory.Load(server);
        var steam = ReadConfigObject(server.ConfigPath, "steam");
        var branch = ReadString(steam, "branch");
        var branchPassword = _steamBranchPasswordStore.Resolve(
            server.Id,
            ReadString(steam, "branchPassword"),
            ReadString(steam, "branchPasswordRef"));
        var result = await _installService.UpdateAsync(
            new ServerUpdateRequest(
                module,
                instance,
                reason,
                branch,
                branchPassword,
                skipIgnoredBuild,
                server.IgnoredBuildId,
                UseOperationCoordinator: false,
                Progress: AppLogService.CreateProgress(server.Id)),
            cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.LastError ?? result.Message);
        }
    }

    private static bool WasUpdatedRecently(string configPath, TimeSpan maxAge)
    {
        var steam = ReadConfigObject(configPath, "steam");
        var lastUpdateUtc = ReadString(steam, "lastUpdateUtc");
        return DateTimeOffset.TryParse(lastUpdateUtc, out var lastUpdate) &&
            DateTimeOffset.UtcNow - lastUpdate.ToUniversalTime() <= maxAge;
    }

    public async Task ForceStopServerAsync(InstalledServer server)
    {
        await _serverOperations.ForceStopAsync(server);
    }

    private void ReleaseStartAttempt(string serverId)
    {
        _serverOperations.ReleaseStartAttempt(serverId);
    }

    private void SetOperationStatus(string serverId, string status)
    {
        _operationManager.UpdateStatus(serverId, status);
        _ = RefreshInstalledServersViewOnlyAsync();
        _ = _discordBotHost.RefreshPanelsAsync();
    }

    private void ClearOperationStatus(string serverId)
    {
        _operationManager.Complete(serverId);
        _ = _discordBotHost.RefreshPanelsAsync();
    }
}
