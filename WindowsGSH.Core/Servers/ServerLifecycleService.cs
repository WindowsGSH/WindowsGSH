using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public sealed class ServerLifecycleService : IServerLifecycleService
{
    // Bounds each individual post-stop "did the process actually exit" probe below. A broken
    // module's Runtime getter (read inside ServerProcessLocator.FindProcesses) could otherwise hang
    // this indefinitely; matches ProcessProbeTimeout's reasoning in MainWindow's own
    // Windows-session-ending stop path.
    private static readonly TimeSpan AfterStopProcessExitConfirmationTimeout = TimeSpan.FromSeconds(3);

    // Total budget for the polling loop around that probe. module.StopAsync returning is not proof
    // of exit - a graceful stop strategy commonly still has a few hundred milliseconds of real save
    // work left when it returns - so a single-shot check right after would misreport a server that
    // is about to exit fine as "still running" and fail the whole stop. Polling for a few seconds
    // absorbs that ordinary timing slack; a process that is still running after this whole window
    // has genuinely not honored the stop command, which callers (see ServerOperationsController's
    // restart paths) can then legitimately escalate to a force stop instead of leaving it stopped.
    private static readonly TimeSpan AfterStopConfirmationGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AfterStopConfirmationPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<IReadOnlyList<IGameServerModule>> _getModules;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _monotonicNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<IGameServerModule, string, TimeSpan, CancellationToken, Task<bool?>> _tryIsRunningAsync;
    private readonly IProcessTuningService _processTuningService;
    private readonly JavaRuntimeManager _javaRuntimeManager;
    private readonly ManagedJavaStore _managedJavaStore;

    public ServerLifecycleService()
        : this(() => new ModuleRegistry().GetModules())
    {
    }

    public ServerLifecycleService(
        Func<IReadOnlyList<IGameServerModule>> getModules,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        IProcessTuningService? processTuningService = null,
        JavaRuntimeManager? javaRuntimeManager = null,
        ManagedJavaStore? managedJavaStore = null,
        Func<IGameServerModule, string, TimeSpan, CancellationToken, Task<bool?>>? tryIsRunningAsync = null,
        Func<TimeSpan>? monotonicNow = null)
    {
        _getModules = getModules;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _monotonicNow = monotonicNow ?? (() => Stopwatch.GetElapsedTime(0));
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
        _tryIsRunningAsync = tryIsRunningAsync ?? ServerProcessLocator.TryIsRunningAsync;
        _processTuningService = processTuningService ?? new ProcessTuningService();
        _javaRuntimeManager = javaRuntimeManager ?? new JavaRuntimeManager();
        _managedJavaStore = managedJavaStore ?? new ManagedJavaStore();
    }

    public Task<ServerOperationResult> StartAsync(ServerInstance instance, CancellationToken token)
    {
        return StartAsync(instance, ServerLifecycleStartOptions.Default, token);
    }

    public async Task<ServerOperationResult> StartAsync(ServerInstance instance, ServerLifecycleStartOptions options, CancellationToken token)
    {
        var startedAt = _utcNow();
        try
        {
            var module = ResolveModule(instance);
            options.SetStatus?.Invoke(instance.Id, "Booting");
            if (options.HasLiveMonitoredProcess?.Invoke(instance.Id) == true ||
                ServerProcessLocator.IsRunning(module, instance.InstallPath))
            {
                throw new InvalidOperationException($"{instance.Name} already has a running process.");
            }

            if (!module.IsInstallValid(instance))
            {
                throw new InvalidOperationException($"{instance.Name} install is not valid for module {module.Name}.");
            }

            ValidateJavaRuntime(module, instance);
            instance = WithResolvedJavaPath(module, instance);
            options.Log?.Invoke(options.Automatic ? $"Auto-starting {instance.Name}." : $"Starting {instance.Name}.", instance.Id);
            if (options.BeforeStartAsync != null)
            {
                await options.BeforeStartAsync(instance, module, options.Automatic, token).ConfigureAwait(false);
            }

            options.SetStatus?.Invoke(instance.Id, "Booting");
            options.MarkBooting?.Invoke(instance.Id);
            var process = await module.StartAsync(instance, token).ConfigureAwait(false);
            if (process != null)
            {
                var launchCommandLine = TryBuildLaunchCommandLineForLog(process);
                if (!string.IsNullOrWhiteSpace(launchCommandLine))
                {
                    options.Log?.Invoke(
                        $"{instance.Name} launched with command line: {launchCommandLine}",
                        instance.Id);
                }

                await DetectEarlyExitAsync(instance, process, token).ConfigureAwait(false);
                ApplyProcessTuning(instance, process, options);
                options.ProcessStarted?.Invoke(instance, module, process, false);
            }

            foreach (var addonProcess in await module.StartAddonProcessesAsync(instance, token).ConfigureAwait(false))
            {
                options.AddonProcessStarted?.Invoke(instance, module, addonProcess);
            }

            if (options.AfterStartAsync != null)
            {
                await options.AfterStartAsync(instance, module, token).ConfigureAwait(false);
            }

            options.ClearStatus?.Invoke(instance.Id);
            options.Log?.Invoke($"{instance.Name} start command sent.", instance.Id);
            return Success(ServerOperationKind.Start, instance, startedAt, options.Automatic ? "Auto start" : "Start");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.ClearBooting?.Invoke(instance.Id);
            options.StartFailed?.Invoke(instance.Id);
            options.Log?.Invoke($"Failed to start {instance.Name}: {ex.Message}", instance.Id);
            return Failure(ServerOperationKind.Start, instance, startedAt, ex, options.Automatic ? "Auto start" : "Start");
        }
        catch
        {
            options.ClearBooting?.Invoke(instance.Id);
            options.StartFailed?.Invoke(instance.Id);
            throw;
        }
    }

    public Task<ServerOperationResult> StopAsync(ServerInstance instance, CancellationToken token)
    {
        return StopAsync(instance, ServerLifecycleStopOptions.Default, token);
    }

    public async Task<ServerOperationResult> StopAsync(ServerInstance instance, ServerLifecycleStopOptions options, CancellationToken token)
    {
        var startedAt = _utcNow();
        try
        {
            var module = ResolveModule(instance);
            options.SetStatus?.Invoke(instance.Id, "Stopping");
            options.Log?.Invoke($"Stopping {instance.Name}.", instance.Id);
            options.MarkExpectedProcessExits?.Invoke(module, instance);
            options.StopLogTail?.Invoke(instance.Id);
            await module.StopAsync(instance, token).ConfigureAwait(false);
            options.StopAddonProcesses?.Invoke(instance.Id);
            if (options.AfterStopDelay > TimeSpan.Zero)
            {
                await _delayAsync(options.AfterStopDelay, token).ConfigureAwait(false);
            }

            if (options.AfterStopAsync != null)
            {
                // module.StopAsync returning does not guarantee the process actually exited - a
                // manifest stop strategy with KillAfterTimeout = false (or a custom module that only
                // dispatches a graceful command) can return while the process is still very much
                // alive. Running removal-style hooks (e.g. UPnP unmap) anyway would treat a server
                // that is merely slow to shut down - or that ignored the stop command entirely - as
                // if it had already stopped. A timed-out/faulted probe is treated the same as "still
                // running" rather than guessed as stopped, matching ServerProcessLocator's own
                // TryIsRunningAsync contract. Polled for AfterStopConfirmationGracePeriod rather than
                // checked once - the process may simply not have finished exiting yet.
                var gracePeriodStarted = _monotonicNow();
                bool? isStillRunning;
                while (true)
                {
                    isStillRunning = await _tryIsRunningAsync(
                        module, instance.InstallPath, AfterStopProcessExitConfirmationTimeout, token).ConfigureAwait(false);
                    if (isStillRunning != true ||
                        _monotonicNow() - gracePeriodStarted >= AfterStopConfirmationGracePeriod)
                    {
                        break;
                    }

                    await _delayAsync(AfterStopConfirmationPollInterval, token).ConfigureAwait(false);
                }

                if (isStillRunning == true)
                {
                    var error = $"{instance.Name} process is still running after the stop command completed.";
                    options.Log?.Invoke(error, instance.Id);
                    return Failure(ServerOperationKind.Stop, instance, startedAt, error, "Stop");
                }

                if (isStillRunning == null)
                {
                    options.Log?.Invoke(
                        $"{instance.Name}: could not confirm the process has exited; after-stop hooks were skipped.",
                        instance.Id);
                }
                else
                {
                    await options.AfterStopAsync(instance, module, token).ConfigureAwait(false);
                }
            }

            options.Log?.Invoke($"{instance.Name} stopped.", instance.Id);
            return Success(ServerOperationKind.Stop, instance, startedAt, "Stop");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log?.Invoke($"Failed to stop {instance.Name}: {ex.Message}", instance.Id);
            return Failure(ServerOperationKind.Stop, instance, startedAt, ex, "Stop");
        }
    }

    public Task<ServerOperationResult> RestartAsync(ServerInstance instance, CancellationToken token)
    {
        return RestartAsync(instance, ServerLifecycleStartOptions.Default, ServerLifecycleStopOptions.Default, token);
    }

    public async Task<ServerOperationResult> RestartAsync(
        ServerInstance instance,
        ServerLifecycleStartOptions startOptions,
        ServerLifecycleStopOptions stopOptions,
        CancellationToken token)
    {
        var startedAt = _utcNow();
        stopOptions.Log?.Invoke($"Restarting {instance.Name}.", instance.Id);
        var stop = await StopAsync(instance, stopOptions, token).ConfigureAwait(false);
        if (!stop.Success)
        {
            return Failure(ServerOperationKind.Restart, instance, startedAt, stop.LastError ?? "Stop failed during restart.", "Restart");
        }

        var start = await StartAsync(instance, startOptions, token).ConfigureAwait(false);
        if (!start.Success)
        {
            return Failure(ServerOperationKind.Restart, instance, startedAt, start.LastError ?? "Start failed during restart.", "Restart");
        }

        return Success(ServerOperationKind.Restart, instance, startedAt, "Restart");
    }

    public Task<ServerOperationResult> ForceStopAsync(ServerInstance instance, CancellationToken token)
    {
        return ForceStopAsync(instance, ServerLifecycleStopOptions.Default, token);
    }

    public async Task<ServerOperationResult> ForceStopAsync(ServerInstance instance, ServerLifecycleStopOptions options, CancellationToken token)
    {
        var startedAt = _utcNow();
        try
        {
            var module = ResolveModule(instance);
            options.SetStatus?.Invoke(instance.Id, "Force stopping");
            options.Log?.Invoke($"Force stopping {instance.Name}.", instance.Id);
            options.MarkExpectedProcessExits?.Invoke(module, instance);
            options.StopLogTail?.Invoke(instance.Id);
            options.StopAddonProcesses?.Invoke(instance.Id);
            await ServerForceStopper.KillAsync(
                module,
                instance,
                token,
                new ServerForceStopOptions(instance.Id, instance.Name, GracefulStopAttempted: false, options.Log)).ConfigureAwait(false);
            if (options.AfterStopDelay > TimeSpan.Zero)
            {
                await _delayAsync(options.AfterStopDelay, token).ConfigureAwait(false);
            }

            if (options.AfterStopAsync != null)
            {
                // ServerForceStopper.KillAsync already Kill()ed and WaitForExitAsync'd every process
                // it found, so a single check (not a retry loop - nothing further is going to
                // terminate a still-running process at this point) is enough here, unlike the
                // graceful StopAsync path above where the process may simply still be in the middle
                // of exiting. Still running after a real kill is a genuinely exceptional case (e.g. a
                // process that respawned or fell outside the killed tree), but running removal-style
                // hooks (e.g. UPnP unmap) against it would be exactly as wrong here as it is in
                // StopAsync.
                var isStillRunning = await _tryIsRunningAsync(
                    module, instance.InstallPath, AfterStopProcessExitConfirmationTimeout, token).ConfigureAwait(false);
                if (isStillRunning == true)
                {
                    var error = $"{instance.Name} process is still running after the force stop completed.";
                    options.Log?.Invoke(error, instance.Id);
                    return Failure(ServerOperationKind.ForceStop, instance, startedAt, error, "Force stop");
                }

                if (isStillRunning == null)
                {
                    var error = $"Could not confirm that {instance.Name} exited after the force stop; after-stop cleanup was skipped.";
                    options.Log?.Invoke(error, instance.Id);
                    return Failure(ServerOperationKind.ForceStop, instance, startedAt, error, "Force stop");
                }
                else
                {
                    await options.AfterStopAsync(instance, module, token).ConfigureAwait(false);
                }
            }

            options.Log?.Invoke($"{instance.Name} force stopped.", instance.Id);
            return Success(ServerOperationKind.ForceStop, instance, startedAt, "Force stop");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log?.Invoke($"Failed to force stop {instance.Name}: {ex.Message}", instance.Id);
            return Failure(ServerOperationKind.ForceStop, instance, startedAt, ex, "Force stop");
        }
    }

    public Task<bool> IsRunningAsync(ServerInstance instance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var module = ResolveModule(instance);
        return Task.FromResult(ServerProcessLocator.IsRunning(module, instance.InstallPath));
    }

    private async Task DetectEarlyExitAsync(ServerInstance instance, Process process, CancellationToken token)
    {
        await _delayAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
        try
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{instance.Name} exited immediately after start with code {process.ExitCode}.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Some process handles do not expose exit state reliably. Monitoring hooks will report later exits.
        }
    }

    private IGameServerModule ResolveModule(ServerInstance instance)
    {
        return _getModules()
            .FirstOrDefault(module => string.Equals(module.Id, instance.ModuleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Module '{instance.ModuleId}' is not loaded.");
    }

    private ServerInstance WithResolvedJavaPath(IGameServerModule module, ServerInstance instance)
    {
        if (!module.Capabilities.RequiresJava ||
            string.IsNullOrWhiteSpace(instance.AppSettings.Java.ManagedRuntimeId))
        {
            return instance;
        }

        var effectivePath = JavaRuntimeManager.ResolveEffectiveJavaPath(
            instance.AppSettings.Java, _managedJavaStore);

        if (string.IsNullOrWhiteSpace(effectivePath) ||
            string.Equals(effectivePath, instance.AppSettings.Java.RuntimePath, StringComparison.OrdinalIgnoreCase))
        {
            return instance;
        }

        var resolvedJava = instance.AppSettings.Java with { RuntimePath = effectivePath };
        return instance with { AppSettings = instance.AppSettings with { Java = resolvedJava } };
    }

    private void ValidateJavaRuntime(IGameServerModule module, ServerInstance instance)
    {
        if (!module.Capabilities.RequiresJava)
        {
            return;
        }

        var minimumMajor = module.Capabilities.MinimumJavaMajor ?? 1;
        var validation = _javaRuntimeManager.Validate(instance.AppSettings.Java, minimumMajor, _managedJavaStore);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }
    }

    private void ApplyProcessTuning(ServerInstance instance, Process process, ServerLifecycleStartOptions options)
    {
        var result = _processTuningService.Apply(process, instance.AppSettings.Runtime);
        foreach (var step in result.Steps)
        {
            var message = step.Success
                ? step.Message
                : $"{step.Message}{(string.IsNullOrWhiteSpace(step.Error) ? string.Empty : $" {step.Error}")}";
            options.Log?.Invoke($"{instance.Name}: {message}", instance.Id);
        }
    }

    private ServerOperationResult Success(ServerOperationKind kind, ServerInstance instance, DateTimeOffset startedAt, string description)
    {
        var completedAt = _utcNow();
        return new ServerOperationResult(true, kind, instance.Id, instance.Name, ServerOperationStatus.Completed, startedAt, completedAt, description);
    }

    private ServerOperationResult Failure(ServerOperationKind kind, ServerInstance instance, DateTimeOffset startedAt, Exception exception, string description)
    {
        return Failure(kind, instance, startedAt, exception.Message, description);
    }

    private ServerOperationResult Failure(ServerOperationKind kind, ServerInstance instance, DateTimeOffset startedAt, string error, string description)
    {
        var completedAt = _utcNow();
        return new ServerOperationResult(false, kind, instance.Id, instance.Name, ServerOperationStatus.Failed, startedAt, completedAt, description, error);
    }

    internal static string BuildLaunchCommandLineForLog(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(" ", startInfo.ArgumentList.Select(WindowsCommandLineEscaper.Quote))
            : startInfo.Arguments;
        return RedactLaunchCommandLine(
            string.IsNullOrWhiteSpace(arguments)
                ? WindowsCommandLineEscaper.Quote(startInfo.FileName)
                : $"{WindowsCommandLineEscaper.Quote(startInfo.FileName)} {arguments}");
    }

    private static string? TryBuildLaunchCommandLineForLog(Process process)
    {
        try
        {
            return BuildLaunchCommandLineForLog(process.StartInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string RedactLaunchCommandLine(string commandLine)
    {
        var redacted = Regex.Replace(
            commandLine,
            @"(?i)(?<prefix>(?:--?|/)(?:[\w.-]*password|token|secret|api[_-]?key|gslt)\s+)(?:""[^""]*""|\S+)",
            "${prefix}<redacted>");
        return Regex.Replace(
            redacted,
            @"(?i)(?<prefix>(?:^|[\s?&;])(?:[\w.-]*password|token|secret|api[_-]?key|gslt)\s*[:=])(?:""[^""]*""|[^\s?&;]+)",
            "${prefix}<redacted>");
    }
}

public sealed record ServerLifecycleStartOptions(
    bool Automatic = false,
    Action<string, string?>? Log = null,
    Action<string, string>? SetStatus = null,
    Action<string>? ClearStatus = null,
    Action<string>? MarkBooting = null,
    Action<string>? ClearBooting = null,
    Action<string>? StartFailed = null,
    Func<string, bool>? HasLiveMonitoredProcess = null,
    Func<ServerInstance, IGameServerModule, bool, CancellationToken, Task>? BeforeStartAsync = null,
    Action<ServerInstance, IGameServerModule, Process, bool>? ProcessStarted = null,
    Action<ServerInstance, IGameServerModule, Process>? AddonProcessStarted = null,
    Func<ServerInstance, IGameServerModule, CancellationToken, Task>? AfterStartAsync = null)
{
    public static ServerLifecycleStartOptions Default { get; } = new();
}

public sealed record ServerLifecycleStopOptions(
    Action<string, string?>? Log = null,
    Action<string, string>? SetStatus = null,
    Action<IGameServerModule, ServerInstance>? MarkExpectedProcessExits = null,
    Action<string>? StopLogTail = null,
    Action<string>? StopAddonProcesses = null,
    TimeSpan? StopDelay = null,
    Func<ServerInstance, IGameServerModule, CancellationToken, Task>? AfterStopAsync = null)
{
    public static ServerLifecycleStopOptions Default { get; } = new();

    public TimeSpan AfterStopDelay => StopDelay ?? TimeSpan.FromMilliseconds(750);
}
