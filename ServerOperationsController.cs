using System.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public sealed class ServerOperationsController
{
    private static readonly TimeSpan PersistentAutoStartFailureCooldown = TimeSpan.FromMinutes(30);
    private readonly ServerOperationManager _operationManager;
    private readonly ServerLifecycleService _lifecycleService;
    private readonly ServerBackupService _backupService;
    private readonly Func<InstalledServer, IGameServerModule> _getModule;
    private readonly Func<InstalledServer, bool, CancellationToken, Task> _startServerAsync;
    private readonly Func<InstalledServer, IGameServerModule, string, bool, CancellationToken, Task> _updateServerAsync;
    private readonly Func<InstalledServer, TimeSpan?, ServerLifecycleStopOptions> _createStopOptions;
    private readonly Func<InstalledServer, IReadOnlyList<string>> _getConfiguredBackupPaths;
    private readonly Func<int> _getBackupRetentionCount;
    private readonly Func<InstalledServer, Task> _cancelActiveOperationAsync;
    private readonly Func<Task> _refreshServersAsync;
    private readonly Action<string, string?> _log;
    private readonly Action<string, string> _setOperationStatus;
    private readonly Action<string> _stopLogTail;
    private readonly Action<string> _stopAddonProcesses;
    private readonly Action<string> _markAutoUpdateChecked;
    private readonly Action<string> _markAutomationChecked;
    private readonly Action<string, bool> _setManuallyStopped;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _serversRoot;
    private readonly object _startAttemptLock = new();
    private readonly HashSet<string> _autoStartingServers = [];
    private readonly Dictionary<string, DateTimeOffset> _lastStartAttempts = [];
    private readonly Dictionary<string, DateTimeOffset> _automaticStartSuppressedUntil = [];

    public ServerOperationsController(
        ServerOperationManager operationManager,
        ServerLifecycleService lifecycleService,
        ServerBackupService backupService,
        Func<InstalledServer, IGameServerModule> getModule,
        Func<InstalledServer, bool, CancellationToken, Task> startServerAsync,
        Func<InstalledServer, IGameServerModule, string, bool, CancellationToken, Task> updateServerAsync,
        Func<InstalledServer, TimeSpan?, ServerLifecycleStopOptions> createStopOptions,
        Func<InstalledServer, IReadOnlyList<string>> getConfiguredBackupPaths,
        Func<int> getBackupRetentionCount,
        Func<InstalledServer, Task> cancelActiveOperationAsync,
        Func<Task> refreshServersAsync,
        Action<string, string?> log,
        Action<string, string> setOperationStatus,
        Action<string> stopLogTail,
        Action<string> stopAddonProcesses,
        Action<string> markAutoUpdateChecked,
        Action<string> markAutomationChecked,
        Action<string, bool> setManuallyStopped,
        Func<DateTimeOffset>? utcNow = null,
        string? serversRoot = null)
    {
        _operationManager = operationManager;
        _lifecycleService = lifecycleService;
        _backupService = backupService;
        _getModule = getModule;
        _startServerAsync = startServerAsync;
        _updateServerAsync = updateServerAsync;
        _createStopOptions = createStopOptions;
        _getConfiguredBackupPaths = getConfiguredBackupPaths;
        _getBackupRetentionCount = getBackupRetentionCount;
        _cancelActiveOperationAsync = cancelActiveOperationAsync;
        _refreshServersAsync = refreshServersAsync;
        _log = log;
        _setOperationStatus = setOperationStatus;
        _stopLogTail = stopLogTail;
        _stopAddonProcesses = stopAddonProcesses;
        _markAutoUpdateChecked = markAutoUpdateChecked;
        _markAutomationChecked = markAutomationChecked;
        _setManuallyStopped = setManuallyStopped;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _serversRoot = Path.GetFullPath(serversRoot ?? Core.AppPaths.GetPath("servers"));
    }

    public async Task<ServerActionExecutionOutcome> StartManualAsync(InstalledServer server, string? description = null)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Start, out operation, description: description))
            {
                return ServerActionExecutionOutcome.Skipped("Another operation or recent start request is blocking this start.");
            }

            await _startServerAsync(server, false, operation!.CancellationToken);
            ClearAutomaticStartSuppression(server.Id);
            _setManuallyStopped(server.Id, false);
            return ServerActionExecutionOutcome.Succeeded($"{server.Name} started.");
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} start stopped.", server.Id);
            _stopLogTail(server.Id);
            _stopAddonProcesses(server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Cancelled($"{server.Name} start was cancelled.");
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to start {server.Name}: {ex.Message}", server.Id);
            return ServerActionExecutionOutcome.Failed(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<ServerActionExecutionOutcome> UpdateManualAsync(InstalledServer server, string? description = null)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Update, out operation, description: description))
            {
                return ServerActionExecutionOutcome.Skipped("Another operation is already running.");
            }

            _markAutoUpdateChecked(server.Id);
            _setOperationStatus(server.Id, "Updating");
            await _updateServerAsync(server, _getModule(server), "manually", false, operation!.CancellationToken);
            _log($"{server.Name} manual update completed.", server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Succeeded($"{server.Name} updated.");
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} update stopped.", server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Cancelled($"{server.Name} update was cancelled.");
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Manual update failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionExecutionOutcome.Failed(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task StopManualAsync(InstalledServer server)
    {
        if (server.IsOperationRunning)
        {
            await _cancelActiveOperationAsync(server);
            return;
        }

        await StopAsync(
            server,
            busyMessage: null,
            requestedLog: null,
            successMessage: null,
            failureLogPrefix: $"Failed to stop {server.Name}",
            failureFormatter: error => $"Failed to stop {server.Name}: {error}");
    }

    public async Task<ServerActionExecutionOutcome> StopManualForBulkAsync(InstalledServer server, string? description = null)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Stop, out operation, description: description))
            {
                return ServerActionExecutionOutcome.Skipped("Another operation is already running.");
            }

            _setManuallyStopped(server.Id, true);
            var result = await _lifecycleService.StopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, null),
                operation!.CancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.LastError ?? $"Failed to stop {server.Name}.");
            }

            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Succeeded($"{server.Name} stopped.");
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} stop command cancelled.", server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Cancelled($"{server.Name} stop was cancelled.");
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to stop {server.Name}: {ex.Message}", server.Id);
            return ServerActionExecutionOutcome.Failed(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<ServerActionExecutionOutcome> RestartManualAsync(InstalledServer server, string? description = null)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Restart, out operation, description: description))
            {
                return ServerActionExecutionOutcome.Skipped("Another operation is already running.");
            }

            var instance = ServerInstanceFactory.Load(server);
            if (server.CanStop)
            {
                await StopForRestartAsync(server, instance, operation!.CancellationToken);
            }

            await _startServerAsync(server, false, operation!.CancellationToken);
            _setManuallyStopped(server.Id, false);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Succeeded($"{server.Name} restarted.");
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} restart stopped.", server.Id);
            _stopLogTail(server.Id);
            _stopAddonProcesses(server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Cancelled($"{server.Name} restart was cancelled.");
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to restart {server.Name}: {ex.Message}", server.Id);
            return ServerActionExecutionOutcome.Failed(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task ForceStopAsync(InstalledServer server, string? description = null)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.ForceStop, out operation, description: description))
            {
                return;
            }

            _setManuallyStopped(server.Id, true);
            var result = await _lifecycleService.ForceStopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, null),
                operation!.CancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.LastError ?? $"Failed to force stop {server.Name}.");
            }

            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to force stop {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<ServerActionExecutionOutcome> BackupManualAsync(InstalledServer server)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            var module = _getModule(server);
            if (!module.Capabilities.SupportsBackups)
            {
                return ServerActionExecutionOutcome.Skipped("The module does not support backups.");
            }

            if (!TryBegin(server, ServerOperationKind.Backup, out operation))
            {
                return ServerActionExecutionOutcome.Skipped("Another operation is already running.");
            }

            _setOperationStatus(server.Id, "Backing up");
            var paths = _getConfiguredBackupPaths(server);
            if (paths.Count == 0)
            {
                paths = module.GetBackupTargets().Select(target => target.RelativePath).ToArray();
            }

            var backupResult = await _backupService.CreateBackupAsync(
                ServerInstanceFactory.Load(server),
                module,
                paths,
                _getBackupRetentionCount(),
                operation!.CancellationToken);
            var backupPath = backupResult.BackupPath ?? throw new InvalidOperationException(backupResult.Message);
            var backupFileName = Path.GetFileName(backupPath) + backupResult.BuildWarningSuffix();
            _log($"Manual backup created for {server.Name}: {backupFileName}", server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Succeeded($"Backup created: {backupFileName}");
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} backup was cancelled.", server.Id);
            await _refreshServersAsync();
            return ServerActionExecutionOutcome.Cancelled($"{server.Name} backup was cancelled.");
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Manual backup failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionExecutionOutcome.Failed(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<string> DiscordStartAsync(InstalledServer server)
    {
        if (!server.CanStart)
        {
            return ServerActionMessageFormatter.CannotStart(server.Name, server.CurrentStatusText);
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Start, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            _setManuallyStopped(server.Id, false);
            _log($"Discord start requested for {server.Name}.", server.Id);
            await _startServerAsync(server, false, operation!.CancellationToken);
            return ServerActionMessageFormatter.StartCommandSent(server.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Discord start failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.FailedToStart(server.Name, ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public Task<string> DiscordStopAsync(InstalledServer server)
    {
        if (!server.CanStop)
        {
            return Task.FromResult(ServerActionMessageFormatter.AlreadyOffline(server.Name));
        }

        return StopAsync(
            server,
            busyMessage: ServerActionMessageFormatter.AlreadyBusy(server.Name),
            requestedLog: $"Discord stop requested for {server.Name}.",
            successMessage: ServerActionMessageFormatter.Stopped(server.Name),
            failureLogPrefix: $"Discord stop failed for {server.Name}",
            failureFormatter: error => ServerActionMessageFormatter.FailedToStop(server.Name, error));
    }

    public async Task<string> DiscordRestartAsync(InstalledServer server)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Restart, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            _setManuallyStopped(server.Id, false);
            var instance = ServerInstanceFactory.Load(server);
            _log($"Discord restart requested for {server.Name}.", server.Id);
            if (server.CanStop)
            {
                await StopForRestartAsync(server, instance, operation!.CancellationToken);
            }

            await _startServerAsync(server, false, operation!.CancellationToken);
            return ServerActionMessageFormatter.RestartCommandSent(server.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Discord restart failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.FailedToRestart(server.Name, ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<string> DiscordUpdateAsync(InstalledServer server)
    {
        if (server.CanStop)
        {
            return ServerActionMessageFormatter.StopBeforeUpdating(server.Name);
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Update, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            _markAutoUpdateChecked(server.Id);
            _setOperationStatus(server.Id, "Updating");
            _log($"Discord update requested for {server.Name}.", server.Id);
            await _updateServerAsync(server, _getModule(server), "from Discord", false, operation!.CancellationToken);
            await _refreshServersAsync();
            return ServerActionMessageFormatter.UpdateCompleted(server.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Discord update failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.FailedToUpdate(server.Name, ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task<string> DiscordBackupAsync(InstalledServer server)
    {
        if (server.CanStop)
        {
            return ServerActionMessageFormatter.StopBeforeBackup(server.Name);
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            var module = _getModule(server);
            if (!module.Capabilities.SupportsBackups)
            {
                return ServerActionMessageFormatter.BackupsUnsupported(server.Name);
            }

            if (!TryBegin(server, ServerOperationKind.Backup, out operation))
            {
                return ServerActionMessageFormatter.AlreadyBusy(server.Name);
            }

            _setOperationStatus(server.Id, "Backing up");
            var paths = _getConfiguredBackupPaths(server);
            if (paths.Count == 0)
            {
                paths = module.GetBackupTargets().Select(target => target.RelativePath).ToArray();
            }

            var backupResult = await _backupService.CreateBackupAsync(
                ServerInstanceFactory.Load(server),
                module,
                paths,
                _getBackupRetentionCount(),
                operation!.CancellationToken);
            var backupPath = backupResult.BackupPath ?? throw new InvalidOperationException(backupResult.Message);
            var backupFileName = Path.GetFileName(backupPath) + backupResult.BuildWarningSuffix();
            _log($"Discord backup created for {server.Name}: {backupFileName}", server.Id);
            await _refreshServersAsync();
            return ServerActionMessageFormatter.BackupCreated(server.Name, backupFileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Discord backup failed for {server.Name}: {ex.Message}", server.Id);
            return ServerActionMessageFormatter.FailedToBackUp(server.Name, ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public async Task RunCronStartAsync(InstalledServer server)
    {
        if (!server.CanStart)
        {
            return;
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Start, out operation, logBusy: false))
            {
                return;
            }

            _setManuallyStopped(server.Id, false);
            _log($"Cron start triggered for {server.Name}.", server.Id);
            await _startServerAsync(server, true, operation!.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Cron start failed for {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public Task RunCronStopAsync(InstalledServer server)
    {
        if (!server.CanStop)
        {
            return Task.CompletedTask;
        }

        return StopWithLogAsync(
            server,
            ServerOperationKind.Stop,
            TimeSpan.Zero,
            $"Cron stop triggered for {server.Name}.",
            $"Cron stop failed for {server.Name}",
            setManualStop: true);
    }

    public Task RunCronUpdateAsync(InstalledServer server)
    {
        if (server.CanStop)
        {
            _log($"Cron update skipped for {server.Name}: server is running.", server.Id);
            return Task.CompletedTask;
        }

        return UpdateWithLogAsync(
            server,
            "from cron",
            skipIgnoredBuild: false,
            startedLog: $"Cron update triggered for {server.Name}.",
            cancelledLog: null,
            failedLogPrefix: $"Cron update failed for {server.Name}",
            markAutoUpdateChecked: false);
    }

    public Task RunCronBackupAsync(InstalledServer server)
    {
        if (server.CanStop)
        {
            _log($"Cron backup skipped for {server.Name}: server is running.", server.Id);
            return Task.CompletedTask;
        }

        return BackupWithLogAsync(
            server,
            startedLog: null,
            successLogPrefix: $"Cron backup created for {server.Name}",
            failedLogPrefix: $"Cron backup failed for {server.Name}",
            skipUnsupportedLog: true);
    }

    public async Task RunCronRestartAsync(InstalledServer server)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Restart, out operation, logBusy: false))
            {
                return;
            }

            _setManuallyStopped(server.Id, false);
            var instance = ServerInstanceFactory.Load(server);
            _log($"Cron restart triggered for {server.Name}.", server.Id);
            if (server.CanStop)
            {
                await StopForRestartAsync(server, instance, operation!.CancellationToken);
            }

            await _startServerAsync(server, true, operation!.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Cron restart failed for {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    public Task RunScheduledAutoUpdateAsync(InstalledServer server)
    {
        if (!HasValidConfigPath(server))
        {
            return Task.CompletedTask;
        }

        return UpdateWithLogAsync(
            server,
            "on schedule",
            skipIgnoredBuild: true,
            startedLog: $"Scheduled auto-update check for {server.Name}.",
            cancelledLog: $"{server.Name} scheduled update stopped.",
            failedLogPrefix: $"Scheduled auto-update failed for {server.Name}",
            markAutoUpdateChecked: true);
    }

    public async Task StartAutomaticallyAsync(InstalledServer server)
    {
        if (!HasValidConfigPath(server) ||
            _operationManager.Get(server.Id)?.IsActive == true)
        {
            return;
        }

        if (IsAutomaticStartSuppressed(server.Id))
        {
            return;
        }

        lock (_autoStartingServers)
        {
            if (!_autoStartingServers.Add(server.Id))
            {
                return;
            }
        }

        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Start, out operation, logBusy: false))
            {
                return;
            }

            _markAutomationChecked(server.Id);
            await _startServerAsync(server, true, operation!.CancellationToken);
            ClearAutomaticStartSuppression(server.Id);
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} auto-start stopped.", server.Id);
            _stopLogTail(server.Id);
            _stopAddonProcesses(server.Id);
            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Auto-start failed for {server.Name}: {ex.Message}", server.Id);
            if (IsPersistentAutoStartFailure(ex.Message))
            {
                SuppressAutomaticStart(server.Id, server.Name, PersistentAutoStartFailureCooldown);
            }
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }

            lock (_autoStartingServers)
            {
                _autoStartingServers.Remove(server.Id);
            }
        }
    }

    private bool HasValidConfigPath(InstalledServer server)
    {
        // SECURITY: the stored ConfigPath is never used for I/O here. The
        // policy derives a fixed ServerConfig.json path from a canonical
        // ServerFolder beneath _serversRoot and verifies stored-path equality.
        if (!InstalledServerPathPolicy.TryGetExpectedConfigPath(server, _serversRoot, out var configPath, out var error))
        {
            _log($"Automation skipped for {server.Name}: {error}", server.Id);
            return false;
        }

        var serversRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_serversRoot));
        if (!configPath.StartsWith(serversRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _log($"Automation skipped for {server.Name}: resolved configuration path escaped the servers directory.", server.Id);
            return false;
        }

        return File.Exists(configPath);
    }

    public bool TryBegin(InstalledServer server, ServerOperationKind kind, out ServerOperationScope? operation, bool logBusy = true, string? description = null)
    {
        if (kind == ServerOperationKind.Start && !TryReserveStartAttempt(server, out var wait))
        {
            operation = null;
            if (logBusy)
            {
                _log($"{server.Name} start ignored: a start was already requested. Try again in {Math.Ceiling(wait.TotalSeconds)}s.", server.Id);
            }

            _ = _refreshServersAsync();
            return false;
        }

        if (_operationManager.TryBegin(server.Id, server.Name, kind, out operation, out var existing, description))
        {
            return true;
        }

        if (kind == ServerOperationKind.Start)
        {
            ReleaseStartAttempt(server.Id);
        }

        if (logBusy)
        {
            _log($"{server.Name} is already busy: {existing?.DisplayText ?? "operation running"}.", server.Id);
        }

        _ = _refreshServersAsync();
        return false;
    }

    public void ReleaseStartAttempt(string serverId)
    {
        lock (_startAttemptLock)
        {
            _lastStartAttempts.Remove(serverId);
        }
    }

    // Run operation work using a pre-acquired scope so the caller can return HTTP 202 atomically.
    // The scope was obtained via TryBegin before the HTTP response was sent; do NOT call TryBegin again.

    internal async Task StartWithScopeAsync(InstalledServer server, ServerOperationScope scope)
    {
        var failed = false;
        try
        {
            await _startServerAsync(server, false, scope.CancellationToken);
            ClearAutomaticStartSuppression(server.Id);
            _setManuallyStopped(server.Id, false);
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} start stopped.", server.Id);
            await _cancelActiveOperationAsync(server); // marks stopped, kills any live process
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to start {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
                scope.Dispose();
        }
    }

    internal async Task StopWithScopeAsync(InstalledServer server, ServerOperationScope scope)
    {
        var failed = false;
        try
        {
            _setManuallyStopped(server.Id, true);
            var result = await _lifecycleService.StopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, null),
                scope.CancellationToken);
            if (!result.Success)
                throw new InvalidOperationException(result.LastError ?? $"Failed to stop {server.Name}.");
            await _refreshServersAsync();
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} stop command cancelled.", server.Id);
            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to stop {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
                scope.Dispose();
        }
    }

    internal async Task RestartWithScopeAsync(InstalledServer server, ServerOperationScope scope)
    {
        var failed = false;
        try
        {
            var instance = ServerInstanceFactory.Load(server);
            if (server.CanStop)
            {
                var stopResult = await _lifecycleService.StopAsync(instance, _createStopOptions(server, null), scope.CancellationToken);
                if (!stopResult.Success)
                    throw new InvalidOperationException(stopResult.LastError ?? $"Failed to stop {server.Name}.");
            }

            await _startServerAsync(server, false, scope.CancellationToken);
            _setManuallyStopped(server.Id, false);
            await _refreshServersAsync();
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} restart stopped.", server.Id);
            await _cancelActiveOperationAsync(server); // marks stopped, kills any live process
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to restart {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
                scope.Dispose();
        }
    }

    internal async Task ForceStopWithScopeAsync(InstalledServer server, ServerOperationScope scope)
    {
        var failed = false;
        try
        {
            _setManuallyStopped(server.Id, true);
            var result = await _lifecycleService.ForceStopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, null),
                scope.CancellationToken);
            if (!result.Success)
                throw new InvalidOperationException(result.LastError ?? $"Failed to force stop {server.Name}.");
            await _refreshServersAsync();
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} force stop cancelled.", server.Id);
            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Failed to force stop {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
                scope.Dispose();
        }
    }

    internal async Task UpdateWithScopeAsync(InstalledServer server, ServerOperationScope scope)
    {
        var failed = false;
        try
        {
            _markAutoUpdateChecked(server.Id);
            _setOperationStatus(server.Id, "Updating");
            await _updateServerAsync(server, _getModule(server), "manually", false, scope.CancellationToken);
            _log($"{server.Name} manual update completed.", server.Id);
            await _refreshServersAsync();
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} update stopped.", server.Id);
            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"Manual update failed for {server.Name}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
                scope.Dispose();
        }
    }

    private async Task<string> StopAsync(
        InstalledServer server,
        string? busyMessage,
        string? requestedLog,
        string? successMessage,
        string failureLogPrefix,
        Func<string, string> failureFormatter)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Stop, out operation))
            {
                return busyMessage ?? string.Empty;
            }

            _setManuallyStopped(server.Id, true);
            if (!string.IsNullOrWhiteSpace(requestedLog))
            {
                _log(requestedLog, server.Id);
            }

            var result = await _lifecycleService.StopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, null),
                operation!.CancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.LastError ?? $"Failed to stop {server.Name}.");
            }

            await _refreshServersAsync();
            return successMessage ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            _log($"{server.Name} stop command cancelled.", server.Id);
            await _refreshServersAsync();
            return string.Empty;
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"{failureLogPrefix}: {ex.Message}", server.Id);
            return failureFormatter(ex.Message);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task StopWithLogAsync(
        InstalledServer server,
        ServerOperationKind kind,
        TimeSpan? stopDelay,
        string startedLog,
        string failedLogPrefix,
        bool setManualStop)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, kind, out operation, logBusy: false))
            {
                return;
            }

            if (setManualStop)
            {
                _setManuallyStopped(server.Id, true);
            }

            _log(startedLog, server.Id);
            var result = await _lifecycleService.StopAsync(
                ServerInstanceFactory.Load(server),
                _createStopOptions(server, stopDelay),
                operation!.CancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.LastError ?? $"Failed to stop {server.Name}.");
            }

            await _refreshServersAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"{failedLogPrefix}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    // Shared by every restart path (manual, Discord, cron). A graceful stop that couldn't confirm
    // the process had exited must not leave the server stopped-but-down with nothing to bring it
    // back - each restart call site used to throw right here and abort before ever attempting the
    // start half, silently taking the server offline until a human noticed (e.g. a nightly cron
    // restart against a slow-shutdown server). Escalating to a force stop instead gives the restart
    // a real chance to still complete; only if the force stop also fails is there genuinely nothing
    // more this method can do.
    //
    // Known trade-off: ForceStopAsync's Kill(entireProcessTree: true) does not consult the module's
    // stop strategy, so this overrides a manifest's own killAfterTimeout: false (a module author's
    // explicit "never force-kill this, to protect save integrity" declaration) once the grace period
    // in StopAsync's own exit-confirmation probe has passed. No module in the current catalogue sets
    // killAfterTimeout: false today, so this is not yet a live conflict - flagged loudly in the log
    // below rather than silently, so it is noticed if/when one does.
    private async Task StopForRestartAsync(InstalledServer server, ServerInstance instance, CancellationToken cancellationToken)
    {
        var stopResult = await _lifecycleService.StopAsync(instance, _createStopOptions(server, null), cancellationToken);
        if (stopResult.Success)
        {
            return;
        }

        _log(
            $"{server.Name}: graceful stop did not confirm the process exited ({stopResult.LastError}); force stopping before restart. " +
            "This overrides any module stop-strategy preference against force-killing (killAfterTimeout: false).",
            server.Id);
        var forceStopResult = await _lifecycleService.ForceStopAsync(instance, _createStopOptions(server, null), cancellationToken);
        if (!forceStopResult.Success)
        {
            throw new InvalidOperationException(forceStopResult.LastError ?? $"Failed to stop {server.Name}.");
        }
    }

    private async Task UpdateWithLogAsync(
        InstalledServer server,
        string reason,
        bool skipIgnoredBuild,
        string startedLog,
        string? cancelledLog,
        string failedLogPrefix,
        bool markAutoUpdateChecked)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            if (!TryBegin(server, ServerOperationKind.Update, out operation, logBusy: false))
            {
                return;
            }

            if (markAutoUpdateChecked)
            {
                _markAutoUpdateChecked(server.Id);
            }

            _setOperationStatus(server.Id, "Updating");
            _log(startedLog, server.Id);
            await _updateServerAsync(server, _getModule(server), reason, skipIgnoredBuild, operation!.CancellationToken);
            await _refreshServersAsync();
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(cancelledLog))
            {
                _log(cancelledLog, server.Id);
            }

            await _refreshServersAsync();
        }
        catch (Exception ex)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"{failedLogPrefix}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private async Task BackupWithLogAsync(
        InstalledServer server,
        string? startedLog,
        string successLogPrefix,
        string failedLogPrefix,
        bool skipUnsupportedLog)
    {
        ServerOperationScope? operation = null;
        var failed = false;
        try
        {
            var module = _getModule(server);
            if (!module.Capabilities.SupportsBackups)
            {
                if (!skipUnsupportedLog)
                {
                    _log($"{server.Name} does not support backups.", server.Id);
                }

                return;
            }

            if (!TryBegin(server, ServerOperationKind.Backup, out operation, logBusy: false))
            {
                return;
            }

            _setOperationStatus(server.Id, "Backing up");
            if (!string.IsNullOrWhiteSpace(startedLog))
            {
                _log(startedLog, server.Id);
            }

            var paths = _getConfiguredBackupPaths(server);
            if (paths.Count == 0)
            {
                paths = module.GetBackupTargets().Select(target => target.RelativePath).ToArray();
            }

            var backupResult = await _backupService.CreateBackupAsync(
                ServerInstanceFactory.Load(server),
                module,
                paths,
                _getBackupRetentionCount(),
                operation!.CancellationToken);
            var backupPath = backupResult.BackupPath ?? throw new InvalidOperationException(backupResult.Message);
            _log($"{successLogPrefix}: {Path.GetFileName(backupPath) + backupResult.BuildWarningSuffix()}", server.Id);
            await _refreshServersAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            _operationManager.Fail(server.Id, ex);
            _log($"{failedLogPrefix}: {ex.Message}", server.Id);
        }
        finally
        {
            if (!failed)
            {
                operation?.Dispose();
            }
        }
    }

    private bool TryReserveStartAttempt(InstalledServer server, out TimeSpan wait)
    {
        var now = _utcNow();
        var cooldown = TimeSpan.FromSeconds(30);
        lock (_startAttemptLock)
        {
            if (_lastStartAttempts.TryGetValue(server.Id, out var lastAttempt))
            {
                var elapsed = now - lastAttempt;
                if (elapsed < cooldown)
                {
                    wait = cooldown - elapsed;
                    return false;
                }
            }

            _lastStartAttempts[server.Id] = now;
        }

        wait = TimeSpan.Zero;
        return true;
    }

    private bool IsAutomaticStartSuppressed(string serverId)
    {
        var now = _utcNow();
        lock (_startAttemptLock)
        {
            if (!_automaticStartSuppressedUntil.TryGetValue(serverId, out var until))
            {
                return false;
            }

            if (now < until)
            {
                return true;
            }

            _automaticStartSuppressedUntil.Remove(serverId);
            return false;
        }
    }

    private void SuppressAutomaticStart(string serverId, string serverName, TimeSpan duration)
    {
        var until = _utcNow().Add(duration);
        lock (_startAttemptLock)
        {
            _automaticStartSuppressedUntil[serverId] = until;
        }

        _log(
            $"Auto-start for {serverName} will retry after {duration.TotalMinutes:0} minute(s) because the last failure looks persistent.",
            serverId);
    }

    private void ClearAutomaticStartSuppression(string serverId)
    {
        lock (_startAttemptLock)
        {
            _automaticStartSuppressedUntil.Remove(serverId);
        }
    }

    private static bool IsPersistentAutoStartFailure(string message)
    {
        return message.Contains("port ", StringComparison.OrdinalIgnoreCase) &&
               message.Contains("already active on this machine", StringComparison.OrdinalIgnoreCase);
    }

}

public sealed record ServerActionExecutionOutcome(
    ServerActionExecutionStatus Status,
    string Message)
{
    public static ServerActionExecutionOutcome Succeeded(string message) =>
        new(ServerActionExecutionStatus.Succeeded, message);

    public static ServerActionExecutionOutcome Failed(string message) =>
        new(ServerActionExecutionStatus.Failed, message);

    public static ServerActionExecutionOutcome Cancelled(string message) =>
        new(ServerActionExecutionStatus.Cancelled, message);

    public static ServerActionExecutionOutcome Skipped(string message) =>
        new(ServerActionExecutionStatus.Skipped, message);
}

public enum ServerActionExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}
