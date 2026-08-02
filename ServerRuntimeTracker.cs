using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

internal sealed class ServerRuntimeTracker
{
    private readonly IServerConsoleService _consoleService;
    private readonly ServerCrashDiagnosticsService _crashDiagnosticsService;
    private readonly ServerStatusComposer _serverStatusComposer;
    private readonly Func<bool> _isClosing;
    private readonly Action<string, string?> _log;
    private readonly Func<InstalledServer, ServerInstance, Task> _handleUnexpectedExitAsync;
    private readonly Func<Task> _refreshServersAsync;
    private readonly Dictionary<string, CancellationTokenSource> _logTailCancellations = [];
    private readonly Dictionary<string, List<Process>> _addonProcesses = [];
    private readonly Dictionary<int, (DateTime StartTimeUtc, DateTime MarkedAtUtc)> _expectedProcessExits = [];
    private static readonly TimeSpan ExpectedProcessExitTtl = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, HashSet<int>> _monitoredServerProcesses = [];
    private readonly object _logTailCancellationsLock = new();
    private readonly object _expectedProcessExitLock = new();
    private readonly object _monitoredServerProcessesLock = new();
    private readonly object _addonProcessesLock = new();
    private readonly ConcurrentDictionary<string, object> _runtimeStateLocks = new(StringComparer.Ordinal);
    private int _reattachingRunningProcesses;

    public ServerRuntimeTracker(
        IServerConsoleService consoleService,
        ServerCrashDiagnosticsService crashDiagnosticsService,
        ServerStatusComposer serverStatusComposer,
        Func<bool> isClosing,
        Action<string, string?> log,
        Func<InstalledServer, ServerInstance, Task> handleUnexpectedExitAsync,
        Func<Task> refreshServersAsync)
    {
        _consoleService = consoleService;
        _crashDiagnosticsService = crashDiagnosticsService;
        _serverStatusComposer = serverStatusComposer;
        _isClosing = isClosing;
        _log = log;
        _handleUnexpectedExitAsync = handleUnexpectedExitAsync;
        _refreshServersAsync = refreshServersAsync;
    }

    public void StopAddonProcesses(string serverId)
    {
        List<Process>? processes;
        lock (_addonProcessesLock)
        {
            if (!_addonProcesses.Remove(serverId, out processes))
            {
                return;
            }
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            }
        }
    }

    public void StopLogTail(string serverId)
    {
        if (TryRemoveLogTailCancellation(serverId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    public void CancelLogTail(string serverId)
    {
        if (TryRemoveLogTailCancellation(serverId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    public void MarkExpectedProcessExits(IGameServerModule module, ServerInstance instance)
    {
        foreach (var process in ServerProcessLocator.FindProcesses(module, instance.InstallPath))
        {
            using (process)
            {
                try
                {
                    var startTimeUtc = process.StartTime.ToUniversalTime();
                    var processId = process.Id;
                    lock (_expectedProcessExitLock)
                    {
                        PruneExpiredExpectedExits();
                        _expectedProcessExits[processId] = (startTimeUtc, DateTime.UtcNow);
                    }
                }
                catch
                {
                }
            }
        }
    }

    public void ClearExpectedProcessExits(IGameServerModule module, ServerInstance instance)
    {
        foreach (var process in ServerProcessLocator.FindProcesses(module, instance.InstallPath))
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    lock (_expectedProcessExitLock)
                    {
                        _expectedProcessExits.Remove(processId);
                    }
                }
                catch
                {
                }
            }
        }
    }

    public void TryMarkExpectedProcessExits(InstalledServer server, Func<InstalledServer, IGameServerModule> getModule)
    {
        try
        {
            MarkExpectedProcessExits(getModule(server), ServerInstanceFactory.Load(server));
        }
        catch
        {
            // Broken or partial installs may not have enough metadata to locate processes.
        }
    }

    public void AttachAddonProcess(string serverId, Process addonProcess)
    {
        lock (_addonProcessesLock)
        {
            if (!_addonProcesses.TryGetValue(serverId, out var processes))
            {
                processes = [];
                _addonProcesses[serverId] = processes;
            }

            processes.Add(addonProcess);
        }

        _consoleService.Add(serverId, $"Attached addon helper process {addonProcess.Id}.");
        // Must subscribe its own Exited handler (it always does, unconditionally) before ours
        // below - otherwise disposing the process in our handler would leave its handler reading
        // process.Id on an already-disposed Process, which throws InvalidOperationException even
        // though Id was already cached (confirmed empirically: Process.Id checks Associated/
        // disposed state on every read, it does not just return the cached int).
        _consoleService.Attach(serverId, addonProcess);

        addonProcess.Exited += (_, _) =>
        {
            // Only dispose if we actually removed it here: StopAddonProcesses may have
            // already taken ownership (removed + is killing/disposing it) concurrently.
            var removedHere = false;
            lock (_addonProcessesLock)
            {
                if (_addonProcesses.TryGetValue(serverId, out var processes) && processes.Remove(addonProcess))
                {
                    removedHere = true;
                    if (processes.Count == 0)
                    {
                        _addonProcesses.Remove(serverId);
                    }
                }
            }

            if (removedHere)
            {
                DrainRedirectedOutputBeforeUse(addonProcess);
                addonProcess.Dispose();
            }
        };

        // Subscribe both consumers before enabling events. If the helper exits between being
        // returned by the module and this attachment completing, setting EnableRaisingEvents
        // after the subscriptions still raises Exited; enabling it first left a narrow window in
        // which the event fired with no cleanup subscriber and the Process remained retained.
        addonProcess.EnableRaisingEvents = true;
    }

    public void AttachStartedProcess(InstalledServer server, IGameServerModule module, ServerInstance instance, Process process, bool attached)
    {
        WriteRuntimeState(server, process, attached);
        // AttachServerConsole must subscribe its Exited handler (if any) before
        // MonitorServerProcess, so MonitorServerProcess's handler - which disposes the
        // process at the end - is always the last one to run on exit.
        AttachServerConsole(server, module, instance, process);
        MonitorServerProcess(server, module, instance, process);
    }

    public async Task AttachRunningServerProcessesAsync(
        IReadOnlyList<InstalledServer> servers,
        Func<InstalledServer, IGameServerModule> getModule)
    {
        if (Interlocked.Exchange(ref _reattachingRunningProcesses, 1) == 1)
        {
            return;
        }

        try
        {
            await Task.Run(() => AttachRunningServerProcesses(servers, getModule));
        }
        catch (Exception ex)
        {
            _log("Running process reattach failed: " + ex.Message, null);
        }
        finally
        {
            Interlocked.Exchange(ref _reattachingRunningProcesses, 0);
        }
    }

    public bool HasLiveMonitoredProcess(string serverId)
    {
        int[] processIds;
        lock (_monitoredServerProcessesLock)
        {
            if (!_monitoredServerProcesses.TryGetValue(serverId, out var processes) || processes.Count == 0)
            {
                return false;
            }

            processIds = processes.ToArray();
        }

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    return true;
                }

                UnmarkProcessMonitored(serverId, processId);
            }
            catch
            {
                UnmarkProcessMonitored(serverId, processId);
            }
        }

        return false;
    }

    public int? GetLiveMonitoredProcessId(string serverId)
    {
        int[] processIds;
        lock (_monitoredServerProcessesLock)
        {
            if (!_monitoredServerProcesses.TryGetValue(serverId, out var processes) || processes.Count == 0)
            {
                return null;
            }

            processIds = processes.ToArray();
        }

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    return processId;
                }

                UnmarkProcessMonitored(serverId, processId);
            }
            catch
            {
                UnmarkProcessMonitored(serverId, processId);
            }
        }

        return null;
    }

    internal void MarkProcessMonitored(string serverId, int processId)
    {
        lock (_monitoredServerProcessesLock)
        {
            if (!_monitoredServerProcesses.TryGetValue(serverId, out var processes))
            {
                processes = [];
                _monitoredServerProcesses[serverId] = processes;
            }

            processes.Add(processId);
        }
    }

    internal bool IsProcessMonitored(string serverId, int processId)
    {
        lock (_monitoredServerProcessesLock)
        {
            return _monitoredServerProcesses.TryGetValue(serverId, out var processes) && processes.Contains(processId);
        }
    }

    private bool TryRemoveLogTailCancellation(string serverId, out CancellationTokenSource cancellation)
    {
        lock (_logTailCancellationsLock)
        {
            return _logTailCancellations.Remove(serverId, out cancellation!);
        }
    }

    private void ReplaceLogTailCancellation(string serverId, CancellationTokenSource replacement, out CancellationTokenSource? previous)
    {
        lock (_logTailCancellationsLock)
        {
            _logTailCancellations.Remove(serverId, out previous);
            _logTailCancellations[serverId] = replacement;
        }
    }

    private void MonitorServerProcess(InstalledServer server, IGameServerModule module, ServerInstance instance, Process process)
    {
        if (SafeGetProcessId(process) is { } processId)
        {
            MarkProcessMonitored(server.Id, processId);
        }

        var commandLine = FormatCommandLine(process);
        process.Exited += (_, _) =>
        {
            // AttachServerConsole (if it attached a console) subscribes before this handler,
            // so this always runs last for a given process - safe to dispose at the end,
            // after every consumer below has read what it needs (exit code, PID, etc).
            try
            {
                DrainRedirectedOutputBeforeUse(process);

                if (SafeGetProcessId(process) is { } exitedProcessId)
                {
                    UnmarkProcessMonitored(server.Id, exitedProcessId);
                    ClearRuntimeStateIfOwned(server, exitedProcessId);
                }

                var decision = ServerProcessExitClassifier.Classify(
                    IsExpectedProcessExit(process),
                    _isClosing(),
                    TryGetExitCode(process),
                    _serverStatusComposer.IsBooting(server.Id));
                _serverStatusComposer.ClearBooting(server.Id);

                if (decision == ServerProcessExitDecision.Ignore)
                {
                    return;
                }

                if (decision == ServerProcessExitDecision.CleanExit)
                {
                    _log($"{server.Name} process exited cleanly.", server.Id);
                    ObserveFireAndForget(_refreshServersAsync(), "Refreshing servers after clean exit", server.Id);
                    return;
                }

                try
                {
                    var recentConsoleOutput = _consoleService.GetRecentText(server.Id, 120);
                    var gameLog = _crashDiagnosticsService.ReadGameLogExcerpt(module, instance);
                    var reportPath = _crashDiagnosticsService.WriteUnexpectedExitReport(server, module, instance, process, commandLine, recentConsoleOutput, gameLog);
                    var summary = _crashDiagnosticsService.BuildSummary(server, module, instance, process, recentConsoleOutput, reportPath, gameLog);
                    _log($"{server.Name} exited unexpectedly. Crash report written: {reportPath}", server.Id);
                    WindowsGshEventBus.Shared.Publish(new ServerCrashDetectedEvent(
                        DateTimeOffset.UtcNow,
                        server.Id,
                        server.ModuleId,
                        server.Name,
                        SafeGetProcessId(process),
                        reportPath,
                        $"{server.Name} exited unexpectedly.")
                    {
                        Summary = summary
                    });
                    ObserveFireAndForget(_handleUnexpectedExitAsync(server, instance), "Unexpected-exit recovery", server.Id);
                    ObserveFireAndForget(_refreshServersAsync(), "Refreshing servers after unexpected exit", server.Id);
                }
                catch (Exception ex)
                {
                    _log($"{server.Name} exited unexpectedly, but crash report failed: {ex.Message}", server.Id);
                }
            }
            finally
            {
                process.Dispose();
            }
        };
    }

    private bool IsExpectedProcessExit(Process process)
    {
        var processId = SafeGetProcessId(process);
        if (!processId.HasValue)
        {
            return false;
        }

        // Best-effort: if we can't read the exited process's start time, fall back to
        // PID-only matching below rather than refusing to ever classify it as expected.
        DateTime? startTimeUtc = null;
        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
        }

        lock (_expectedProcessExitLock)
        {
            PruneExpiredExpectedExits();

            if (!_expectedProcessExits.TryGetValue(processId.Value, out var expected))
            {
                return false;
            }

            // Windows reuses PIDs. Only treat this as the exit we marked if the start
            // time still matches what we recorded when marking it, so a genuine crash
            // of a different, later process reusing the same PID isn't swallowed.
            var isSameProcess = !startTimeUtc.HasValue
                || (expected.StartTimeUtc - startTimeUtc.Value).Duration() < TimeSpan.FromSeconds(2);

            if (!isSameProcess)
            {
                return false;
            }

            _expectedProcessExits.Remove(processId.Value);
            return true;
        }
    }

    private void PruneExpiredExpectedExits()
    {
        if (_expectedProcessExits.Count == 0)
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow - ExpectedProcessExitTtl;
        List<int>? expiredKeys = null;
        foreach (var (processId, expected) in _expectedProcessExits)
        {
            if (expected.MarkedAtUtc < cutoffUtc)
            {
                (expiredKeys ??= []).Add(processId);
            }
        }

        if (expiredKeys != null)
        {
            foreach (var processId in expiredKeys)
            {
                _expectedProcessExits.Remove(processId);
            }
        }
    }

    private void AttachServerConsole(InstalledServer server, IGameServerModule module, ServerInstance instance, Process process)
    {
        if (ConsoleInputStrategyPolicy.UsesRedirectedStreams(module.Runtime) && HasRedirectedConsoleOutput(process))
        {
            _consoleService.Attach(server.Id, process);
        }

        var consoleLogPath = module.GetConsoleLogPath(instance);
        if (string.IsNullOrWhiteSpace(consoleLogPath))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        ReplaceLogTailCancellation(server.Id, cancellation, out var existing);
        if (existing != null)
        {
            existing.Cancel();
            existing.Dispose();
        }

        _consoleService.AttachLogFile(server.Id, consoleLogPath, cancellation.Token);
    }

    // Process.Exited can fire before the asynchronous OutputDataReceived/ErrorDataReceived pump
    // (started by ServerConsoleService.Attach's BeginOutputReadLine/BeginErrorReadLine) has
    // drained the last buffered lines - they're two independent notifications, not ordered
    // relative to each other. Reading console output (crash report excerpts) or disposing the
    // process before that pump reaches EOF can drop the final, often most diagnostically useful,
    // lines. The parameterless WaitForExit() overload is the documented way to block until that
    // async drain has actually completed; run it via Task.Run with a bounded wait since it isn't
    // itself cancellable and a child process that inherited the redirected handle could in rare
    // cases keep the pipe open indefinitely.
    private static void DrainRedirectedOutputBeforeUse(Process process)
    {
        try
        {
            Task.Run(process.WaitForExit).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
    }

    // A failed fire-and-forget continuation (e.g. _handleUnexpectedExitAsync/_refreshServersAsync
    // called from the process Exited handler below) would otherwise only ever surface through
    // TaskScheduler.UnobservedTaskException - typically during a later GC, with no link back to
    // which operation actually failed. Observing it here reports the failure at the point it
    // happened instead.
    private void ObserveFireAndForget(Task task, string failureContext, string? serverId)
    {
        _ = task.ContinueWith(
            completed => _log($"{failureContext} failed: {completed.Exception?.GetBaseException().Message}", serverId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void AttachRunningServerProcesses(
        IReadOnlyList<InstalledServer> servers,
        Func<InstalledServer, IGameServerModule> getModule)
    {
        foreach (var server in servers.Where(server => server.Status == ServerRuntimeStatus.Running))
        {
            try
            {
                var module = getModule(server);
                var instance = ServerInstanceFactory.Load(server);
                foreach (var process in ServerProcessLocator.FindProcesses(module, instance.InstallPath))
                {
                    var processId = SafeGetProcessId(process);
                    if (!processId.HasValue || IsProcessMonitored(server.Id, processId.Value))
                    {
                        process.Dispose();
                        continue;
                    }

                    process.EnableRaisingEvents = true;
                    MarkProcessMonitored(server.Id, processId.Value);
                    AttachServerConsole(server, module, instance, process);
                    MonitorServerProcess(server, module, instance, process);
                    WriteRuntimeState(server, process, attached: true);
                    _consoleService.Add(server.Id, $"Reattached running process {processId.Value}.");
                    _log($"Reattached {server.Name} to running process {processId.Value}.", server.Id);
                }
            }
            catch (Exception ex)
            {
                _log($"Could not reattach {server.Name}: {ex.Message}", server.Id);
            }
        }
    }

    private void UnmarkProcessMonitored(string serverId, int processId)
    {
        lock (_monitoredServerProcessesLock)
        {
            if (!_monitoredServerProcesses.TryGetValue(serverId, out var processes))
            {
                return;
            }

            processes.Remove(processId);
            if (processes.Count == 0)
            {
                _monitoredServerProcesses.Remove(serverId);
            }
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static int? SafeGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatCommandLine(Process process)
    {
        try
        {
            return FormatCommandLine(process.StartInfo);
        }
        catch (InvalidOperationException)
        {
            var executablePath = TryGetProcessPath(process);
            return string.IsNullOrWhiteSpace(executablePath)
                ? "(attached process command line unavailable)"
                : QuoteIfNeeded(executablePath);
        }
    }

    private static string FormatCommandLine(ProcessStartInfo startInfo)
    {
        var executable = string.IsNullOrWhiteSpace(startInfo.FileName)
            ? "(unknown)"
            : QuoteIfNeeded(startInfo.FileName);
        return string.IsNullOrWhiteSpace(startInfo.Arguments)
            ? executable
            : $"{executable} {startInfo.Arguments}";
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(" ", StringComparison.Ordinal) && !value.StartsWith('"')
            ? $"\"{value}\""
            : value;
    }

    private void WriteRuntimeState(InstalledServer server, Process process, bool attached)
    {
        var runtimePath = Path.Combine(server.ServerFolder, "runtime.json");
        var state = new
        {
            pid = process.Id,
            executable = TryGetProcessPath(process),
            installPath = server.InstallPath,
            attached,
            sessionId = Environment.ProcessId,
            updatedUtc = DateTimeOffset.UtcNow
        };
        var contents = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

        lock (_runtimeStateLocks.GetOrAdd(server.Id, static _ => new object()))
        {
            try
            {
                AtomicFile.WriteAllText(runtimePath, contents);
            }
            catch (Exception atomicException)
            {
                // runtime.json is recoverable process identity rather than durable user configuration.
                // If an existing file cannot be atomically replaced (the live PaperMC report exposed
                // this on one machine), prefer a direct rewrite over leaving a known-stale PID behind.
                try
                {
                    File.WriteAllText(runtimePath, contents);
                    _log(
                        $"Runtime process identity for {server.Name} required a non-atomic rewrite after the atomic update failed: {atomicException.Message}",
                        server.Id);
                }
                catch (Exception fallbackException)
                {
                    _log(
                        $"Could not persist runtime process identity for {server.Name}: {fallbackException.Message}",
                        server.Id);
                }
            }
        }
    }

    private void ClearRuntimeStateIfOwned(InstalledServer server, int processId)
    {
        var runtimePath = Path.Combine(server.ServerFolder, "runtime.json");
        lock (_runtimeStateLocks.GetOrAdd(server.Id, static _ => new object()))
        {
            try
            {
                if (!File.Exists(runtimePath))
                {
                    return;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(runtimePath));
                if (document.RootElement.TryGetProperty("pid", out var pidElement) &&
                    pidElement.TryGetInt32(out var persistedProcessId) &&
                    persistedProcessId == processId)
                {
                    File.Delete(runtimePath);
                }
            }
            catch (Exception ex)
            {
                _log($"Could not clear runtime process identity for {server.Name}: {ex.Message}", server.Id);
            }
        }
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool HasRedirectedConsoleOutput(Process process)
    {
        try
        {
            return process.StartInfo.RedirectStandardOutput || process.StartInfo.RedirectStandardError;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
