using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerOperationsControllerTests
{
    [Fact]
    public async Task StartManualAsync_Begins_operation_and_clears_manual_stop_flag()
    {
        var started = false;
        var manuallyStopped = true;
        var controller = CreateController(
            startServerAsync: (server, automatic, token) =>
            {
                started = true;
                return Task.CompletedTask;
            },
            setManuallyStopped: (_, value) => manuallyStopped = value);

        await controller.StartManualAsync(CreateServer(canStart: true));

        Assert.True(started);
        Assert.False(manuallyStopped);
    }

    [Fact]
    public async Task StartManualAsync_Blocks_duplicate_start_until_attempt_is_released()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var startCount = 0;
        var logs = new List<string>();
        var controller = CreateController(
            utcNow: () => now,
            startServerAsync: (_, _, _) =>
            {
                startCount++;
                return Task.CompletedTask;
            },
            log: (message, _) => logs.Add(message));
        var server = CreateServer(canStart: true);

        await controller.StartManualAsync(server);
        await controller.StartManualAsync(server);
        controller.ReleaseStartAttempt(server.Id);
        await controller.StartManualAsync(server);

        Assert.Equal(2, startCount);
        Assert.Contains(logs, message => message.Contains("start ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartManualAsync_keeps_manual_stop_flag_when_start_fails()
    {
        var manuallyStopped = true;
        var controller = CreateController(
            startServerAsync: (_, _, _) => throw new InvalidOperationException("start failed"),
            setManuallyStopped: (_, value) => manuallyStopped = value);

        var result = await controller.StartManualAsync(CreateServer(canStart: true));

        Assert.Equal(ServerActionExecutionStatus.Failed, result.Status);
        Assert.True(manuallyStopped);
    }

    [Fact]
    public async Task DiscordStartAsync_Returns_busy_message_when_operation_is_running()
    {
        var manager = new ServerOperationManager(_ => { });
        Assert.True(manager.TryBegin("server-1", "Test Server", ServerOperationKind.Update, out var scope, out _));
        var controller = CreateController(operationManager: manager);

        var message = await controller.DiscordStartAsync(CreateServer(canStart: true));

        Assert.Equal(ServerActionMessageFormatter.AlreadyBusy("Test Server"), message);
        scope!.Dispose();
    }

    [Fact]
    public async Task RunCronStartAsync_Starts_as_automatic_and_logs_trigger()
    {
        var automaticValues = new List<bool>();
        var logs = new List<string>();
        var controller = CreateController(
            startServerAsync: (_, automatic, _) =>
            {
                automaticValues.Add(automatic);
                return Task.CompletedTask;
            },
            log: (message, _) => logs.Add(message));

        await controller.RunCronStartAsync(CreateServer(canStart: true));

        Assert.Equal([true], automaticValues);
        Assert.Contains(logs, message => message.Contains("Cron start triggered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAutomaticallyAsync_Requires_config_and_marks_automation_check()
    {
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ServerConfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{}");
        var startCount = 0;
        var markedServerIds = new List<string>();
        var controller = CreateController(
            startServerAsync: (_, automatic, _) =>
            {
                if (automatic)
                {
                    startCount++;
                }

                return Task.CompletedTask;
            },
            markAutomationChecked: markedServerIds.Add);

        await controller.StartAutomaticallyAsync(CreateServer(canStart: true, configPath: configPath));
        await controller.StartAutomaticallyAsync(CreateServer(canStart: true, configPath: configPath + ".missing"));

        Assert.Equal(1, startCount);
        Assert.Equal(["server-1"], markedServerIds);
    }

    [Fact]
    public async Task StartAutomaticallyAsync_BacksOffAfterPersistentPortFailure()
    {
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ServerConfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{}");
        var now = new DateTimeOffset(2026, 6, 28, 4, 30, 0, TimeSpan.Zero);
        var startCount = 0;
        var logs = new List<string>();
        var controller = CreateController(
            startServerAsync: (_, _, _) =>
            {
                startCount++;
                throw new InvalidOperationException("Test Server: port 7777 is already active on this machine.");
            },
            log: (message, _) => logs.Add(message),
            utcNow: () => now);
        var server = CreateServer(canStart: true, configPath: configPath);

        await controller.StartAutomaticallyAsync(server);
        await controller.StartAutomaticallyAsync(server);
        now = now.AddMinutes(29);
        await controller.StartAutomaticallyAsync(server);
        now = now.AddMinutes(2);
        await controller.StartAutomaticallyAsync(server);

        Assert.Equal(2, startCount);
        Assert.Contains(logs, message => message.Contains("will retry after 30 minute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automation_refuses_invalid_config_paths_without_throwing()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var logs = new List<string>();
        var starts = 0;
        var controller = CreateController(
            startServerAsync: (_, _, _) => { starts++; return Task.CompletedTask; },
            log: (message, _) => logs.Add(message));
        var server = CreateServer(canStart: true, configPath: Path.Combine(folder, "..", "outside.json"));

        await controller.StartAutomaticallyAsync(server);
        await controller.RunScheduledAutoUpdateAsync(server);

        Assert.Equal(0, starts);
        Assert.Contains(logs, message => message.Contains("Automation skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartManualAsync_escalates_to_force_stop_instead_of_aborting_when_graceful_stop_cannot_confirm_exit()
    {
        // Regression test for the actual A1 bug: a graceful stop that can't confirm the process
        // exited used to make every restart path throw and abort before ever attempting to start
        // again, silently leaving the server down. This proves the fix end-to-end against a real
        // process: ServerLifecycleService.StopAsync reports failure (module.StopAsync is a no-op
        // here, simulating a stop strategy that never actually asked the process to exit), the
        // controller escalates to a real ForceStopAsync (which genuinely kills the process), and the
        // restart still completes instead of aborting.
        const string powerShellPath = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe";
        if (!File.Exists(powerShellPath))
        {
            return;
        }

        var configPath = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"), "ServerConfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "{}");
        var installPath = Path.Combine(Path.GetDirectoryName(configPath)!, "install");
        Directory.CreateDirectory(installPath);

        using var stuckProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 60\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        stuckProcess.Start();
        try
        {
            // Use the executable path we launched instead of reading MainModule back from the
            // process. That property can be unavailable on a restricted GitHub-hosted runner,
            // which made runtime.json contain an empty executable; the locator then correctly
            // rejected the unrelated module StartPath and the test never exercised force-stop.
            WriteRuntimeState(installPath, stuckProcess.Id, powerShellPath);

            var module = new TestModule();
            var currentTime = DateTimeOffset.UtcNow;
            var monotonicTime = TimeSpan.Zero;
            var lifecycleService = new ServerLifecycleService(
                () => [module],
                utcNow: () => currentTime,
                delayAsync: (delay, _) =>
                {
                    currentTime += delay;
                    monotonicTime += delay;
                    return Task.CompletedTask;
                },
                monotonicNow: () => monotonicTime);

            var started = false;
            var controller = new ServerOperationsController(
                new ServerOperationManager(_ => { }),
                lifecycleService,
                new ServerBackupService(),
                getModule: _ => module,
                startServerAsync: (_, _, _) => { started = true; return Task.CompletedTask; },
                updateServerAsync: (_, _, _, _, _) => Task.CompletedTask,
                // A non-null AfterStopAsync is what activates the post-stop exit-confirmation probe
                // in ServerLifecycleService (this is how MainWindow's real CreateLifecycleStopOptions
                // always wires it, via the UPnP hook, regardless of mapping policy) - without one
                // here, StopAsync would report success unconditionally and never even attempt to
                // notice the process is still running, which would defeat this test's whole premise.
                createStopOptions: (_, delay) => new ServerLifecycleStopOptions(
                    StopDelay: delay ?? TimeSpan.Zero,
                    AfterStopAsync: (_, _, _) => Task.CompletedTask),
                getConfiguredBackupPaths: _ => [],
                getBackupRetentionCount: () => 10,
                cancelActiveOperationAsync: _ => Task.CompletedTask,
                refreshServersAsync: () => Task.CompletedTask,
                log: (_, _) => { },
                setOperationStatus: (_, _) => { },
                stopLogTail: _ => { },
                stopAddonProcesses: _ => { },
                markAutoUpdateChecked: _ => { },
                markAutomationChecked: _ => { },
                setManuallyStopped: (_, _) => { });

            var server = CreateServer(canStart: true, canStop: true, configPath: configPath, installPath: installPath);

            var result = await controller.RestartManualAsync(server);

            Assert.Equal(ServerActionExecutionStatus.Succeeded, result.Status);
            Assert.True(started);
            stuckProcess.Refresh();
            // ForceStopAsync already awaited WaitForExitAsync for this exact process before this
            // method returned Succeeded above - this is just giving the OS a moment to reflect that
            // in a fresh Process handle under whatever CPU contention the full suite is under, not
            // waiting for the kill itself to still be in progress.
            Assert.True(stuckProcess.WaitForExit(15000) || stuckProcess.HasExited);
        }
        finally
        {
            if (!stuckProcess.HasExited)
            {
                stuckProcess.Kill(entireProcessTree: true);
            }
        }
    }

    private static void WriteRuntimeState(string installPath, int pid, string? executable)
    {
        var serverFolder = Directory.GetParent(installPath)!.FullName;
        var state = new Dictionary<string, object?>
        {
            ["pid"] = pid,
            ["installPath"] = installPath,
            ["executable"] = executable ?? string.Empty,
            ["updatedUtc"] = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(serverFolder, "runtime.json"), System.Text.Json.JsonSerializer.Serialize(state));
    }

    private sealed class TestModule : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0.0";
        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);
        public ModuleRuntimeDefinition Runtime => new("definitely-not-running.exe", ["definitely-not-running"]);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, IsInstalled: false, IsEnabled: false, StatusText: "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static ServerOperationsController CreateController(
        ServerOperationManager? operationManager = null,
        Func<InstalledServer, bool, CancellationToken, Task>? startServerAsync = null,
        Action<string, bool>? setManuallyStopped = null,
        Action<string, string?>? log = null,
        Action<string>? markAutomationChecked = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var manager = operationManager ?? new ServerOperationManager(_ => { });
        return new ServerOperationsController(
            manager,
            new ServerLifecycleService(() => []),
            new ServerBackupService(),
            _ => throw new InvalidOperationException("Module should not be requested."),
            startServerAsync ?? ((_, _, _) => Task.CompletedTask),
            (_, _, _, _, _) => Task.CompletedTask,
            (_, delay) => new ServerLifecycleStopOptions(StopDelay: delay),
            _ => [],
            () => 10,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            log ?? ((_, _) => { }),
            (_, _) => { },
            _ => { },
            _ => { },
            _ => { },
            markAutomationChecked ?? (_ => { }),
            setManuallyStopped ?? ((_, _) => { }),
            utcNow,
            Path.GetTempPath());
    }

    private static InstalledServer CreateServer(bool canStart = false, bool canStop = false, string? configPath = null, string? installPath = null)
    {
        var serverFolder = configPath == null
            ? installPath ?? @"C:\servers\test"
            : Path.GetDirectoryName(configPath)!;
        return new InstalledServer(
            "server-1",
            "Test Server",
            "test",
            "Runtime",
            serverFolder,
            installPath ?? @"C:\servers\test",
            configPath ?? @"C:\servers\test\ServerConfig.json",
            "127.0.0.1",
            "27015",
            "",
            "",
            "0",
            "",
            "",
            "",
            "",
            "Offline",
            "",
            false,
            "",
            null,
            true,
            ServerRuntimeStatus.Offline,
            "Offline",
            "ServerStatusOfflineBrush",
            false,
            "",
            "",
            "",
            true,
            true,
            canStart,
            canStop);
    }
}
