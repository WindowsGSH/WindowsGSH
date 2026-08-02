using System.Diagnostics;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerLifecycleServiceTests
{
    [Fact]
    public async Task StartAsync_fails_when_install_is_invalid()
    {
        var module = new TestModule { InstallValid = false };
        var service = CreateService(module);

        var result = await service.StartAsync(CreateInstance(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServerOperationKind.Start, result.Kind);
        Assert.Equal(ServerOperationStatus.Failed, result.Status);
        Assert.Contains("install is not valid", result.LastError);
        Assert.False(module.StartCalled);
    }

    [Fact]
    public async Task StartAsync_runs_before_start_marks_booting_and_clears_status()
    {
        var module = new TestModule { InstallValid = true };
        var service = CreateService(module);
        var beforeStartCalled = false;
        var markedBooting = false;
        var clearedStatus = false;

        var result = await service.StartAsync(
            CreateInstance(),
            new ServerLifecycleStartOptions(
                BeforeStartAsync: (_, _, _, _) =>
                {
                    beforeStartCalled = true;
                    return Task.CompletedTask;
                },
                MarkBooting: _ => markedBooting = true,
                ClearStatus: _ => clearedStatus = true),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(module.StartCalled);
        Assert.True(beforeStartCalled);
        Assert.True(markedBooting);
        Assert.True(clearedStatus);
    }

    [Fact]
    public async Task StartAsync_awaits_after_start_before_reporting_success()
    {
        var module = new TestModule { InstallValid = true };
        var service = CreateService(module);
        var callbackCompleted = false;

        var result = await service.StartAsync(
            CreateInstance(),
            new ServerLifecycleStartOptions(
                AfterStartAsync: async (_, _, _) =>
                {
                    await Task.Yield();
                    callbackCompleted = true;
                }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(callbackCompleted);
    }

    [Fact]
    public async Task StartAsync_passes_the_real_caller_token_to_after_start_async()
    {
        // Regression guard: AfterStartAsync used to always receive CancellationToken.None,
        // regardless of what the caller passed to StartAsync itself - meaning a hook like UPnP
        // mapping (which can enumerate for up to 30 seconds per port) had no way to observe an
        // actual cancellation of the surrounding start/stop operation.
        var module = new TestModule { InstallValid = true };
        var service = CreateService(module);
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;

        var result = await service.StartAsync(
            CreateInstance(),
            new ServerLifecycleStartOptions(
                AfterStartAsync: (_, _, token) =>
                {
                    observedToken = token;
                    return Task.CompletedTask;
                }),
            cts.Token);

        Assert.True(result.Success);
        Assert.Equal(cts.Token, observedToken);
    }

    [Fact]
    public async Task StartAsync_remains_successful_when_noncritical_after_start_work_is_cancelled()
    {
        var module = new TestModule { InstallValid = true };
        var service = CreateService(module);
        using var cts = new CancellationTokenSource();
        var logs = new List<string>();

        var result = await service.StartAsync(
            CreateInstance(),
            new ServerLifecycleStartOptions(
                AfterStartAsync: (_, _, token) => MainWindow.RunNonCriticalLifecycleHookAsync(
                    _ =>
                    {
                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    },
                    token,
                    logs.Add,
                    "mapping cancelled",
                    "mapping failed")),
            cts.Token);

        Assert.True(result.Success);
        Assert.Contains("mapping cancelled", logs);
    }

    [Fact]
    public async Task StartAsync_applies_process_tuning_after_start()
    {
        var module = new TestModule { InstallValid = true, Process = Process.GetCurrentProcess() };
        var tuning = new RecordingProcessTuningService();
        var service = CreateService(module, tuning);
        var logs = new List<string>();
        var instance = CreateInstance() with
        {
            AppSettings = ServerConfigAppSettings.Empty with
            {
                Runtime = ServerRuntimeSettings.Default with
                {
                    ApplyPriorityOnStart = true,
                    ProcessPriorityClass = "AboveNormal"
                }
            }
        };

        var result = await service.StartAsync(
            instance,
            new ServerLifecycleStartOptions(Log: (message, _) => logs.Add(message)),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(instance.AppSettings.Runtime, tuning.Settings);
        Assert.Contains(logs, message => message.Contains("Applied process priority", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLaunchCommandLineForLog_QuotesExecutableAndRedactsSecrets()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = @"C:\Game Servers\Ark\ArkAscendedServer.exe",
            Arguments = "TheIsland_WP?listen?SessionName=\"Test Server\"?ServerAdminPassword=super-secret -clusterid=ClusterOne -GSLT token-value --password \"alpha beta\""
        };

        var commandLine = ServerLifecycleService.BuildLaunchCommandLineForLog(startInfo);

        Assert.StartsWith("\"C:\\Game Servers\\Ark\\ArkAscendedServer.exe\"", commandLine);
        Assert.Contains("SessionName=\"Test Server\"", commandLine);
        Assert.Contains("ServerAdminPassword=<redacted>", commandLine);
        Assert.Contains("-GSLT <redacted>", commandLine);
        Assert.Contains("--password <redacted>", commandLine);
        Assert.DoesNotContain("super-secret", commandLine);
        Assert.DoesNotContain("token-value", commandLine);
        Assert.DoesNotContain("alpha beta", commandLine);
    }

    [Fact]
    public async Task StartAsync_blocks_java_module_when_version_cannot_be_parsed()
    {
        var module = new TestModule { InstallValid = true, RequiresJava = true, MinimumJavaMajor = 21 };
        var javaRuntimeManager = new JavaRuntimeManager(new JavaRuntimeLocator(
            fileExists: _ => true,
            getEnvironmentVariable: _ => null,
            runVersionCommand: _ => "unexpected java output"));
        var service = CreateService(module, javaRuntimeManager: javaRuntimeManager);
        var instance = CreateInstance() with
        {
            AppSettings = ServerConfigAppSettings.Empty with
            {
                Java = ServerJavaSettings.Default with { RuntimePath = @"C:\Java\java.exe" }
            }
        };

        var result = await service.StartAsync(instance, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not be parsed", result.LastError);
        Assert.False(module.StartCalled);
    }

    [Fact]
    public async Task StopAsync_marks_expected_exits_stops_log_tail_and_addons()
    {
        var module = new TestModule();
        var service = CreateService(module);
        var markedExpected = false;
        var stoppedLogTail = false;
        var stoppedAddons = false;

        var result = await service.StopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                MarkExpectedProcessExits: (_, _) => markedExpected = true,
                StopLogTail: _ => stoppedLogTail = true,
                StopAddonProcesses: _ => stoppedAddons = true,
                StopDelay: TimeSpan.Zero),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(module.StopCalled);
        Assert.True(markedExpected);
        Assert.True(stoppedLogTail);
        Assert.True(stoppedAddons);
    }

    [Fact]
    public async Task StopAsync_invokes_after_stop_only_after_module_stop_and_addon_cleanup()
    {
        var module = new TestModule();
        var service = CreateService(module);
        var addonsStopped = false;
        var callbackObservedCompletedStop = false;

        var result = await service.StopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopAddonProcesses: _ => addonsStopped = true,
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) =>
                {
                    callbackObservedCompletedStop = module.StopCalled && addonsStopped;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(callbackObservedCompletedStop);
    }

    [Fact]
    public async Task StopAsync_passes_the_real_caller_token_to_after_stop_async()
    {
        // Regression guard: AfterStopAsync used to always receive CancellationToken.None,
        // regardless of what the caller passed to StopAsync itself - meaning a hook like UPnP
        // mapping removal had no way to observe an actual cancellation of the surrounding
        // stop operation.
        var module = new TestModule();
        var service = CreateService(module);
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;

        var result = await service.StopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, token) =>
                {
                    observedToken = token;
                    return Task.CompletedTask;
                }),
            cts.Token);

        Assert.True(result.Success);
        Assert.Equal(cts.Token, observedToken);
    }

    [Fact]
    public async Task StopAsync_fails_and_skips_after_stop_hooks_when_the_process_is_still_running_for_the_whole_grace_period()
    {
        // Regression guard: module.StopAsync returning is not proof the process actually exited - a
        // manifest stop strategy with KillAfterTimeout = false (or a custom module that only sends a
        // graceful command) can return while the process is still alive. Running a removal-style
        // AfterStopAsync hook (e.g. UPnP unmap) anyway would treat a server that ignored the stop
        // command, or is just slow to shut down, as if it had already stopped. A runtime.json
        // pointing at this test process itself (the same mechanism ServerProcessLocatorTests uses)
        // simulates "the managed process is still running" - it never exits for the rest of this
        // test, so the grace-period poll must exhaust its whole budget before giving up.
        //
        // Uses a virtual clock (advanced only when the stubbed delayAsync is invoked) rather than
        // CreateService's instant-delay stub, so this test proves the polling loop exhausts its real
        // 8-second grace period without this test itself taking 8 real seconds of busy-polling.
        var module = new TestModule();
        var currentTime = DateTimeOffset.UtcNow;
        var monotonicTime = TimeSpan.Zero;
        var service = new ServerLifecycleService(
            () => [module],
            utcNow: () => currentTime,
            delayAsync: (delay, _) =>
            {
                currentTime += delay;
                monotonicTime += delay;
                return Task.CompletedTask;
            },
            monotonicNow: () => monotonicTime);
        var instance = CreateInstance();
        WriteRuntimeState(instance.InstallPath);
        var afterStopInvoked = false;

        var result = await service.StopAsync(
            instance,
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) =>
                {
                    afterStopInvoked = true;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("still running", result.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(afterStopInvoked);
    }

    [Fact]
    public async Task StopAsync_succeeds_when_the_process_exits_partway_through_the_grace_period()
    {
        // Complements the test above: a process that is still finishing its own graceful shutdown
        // when module.StopAsync returns - the ordinary case the grace-period poll exists for - must
        // not be reported as a failed stop just because it hadn't quite exited at the first check.
        var module = new TestModule();
        var currentTime = DateTimeOffset.UtcNow;
        var monotonicTime = TimeSpan.Zero;
        var probeCount = 0;
        var service = new ServerLifecycleService(
            () => [module],
            utcNow: () => currentTime,
            delayAsync: (delay, _) =>
            {
                currentTime += delay;
                monotonicTime += delay;
                return Task.CompletedTask;
            },
            tryIsRunningAsync: (_, _, _, _) => Task.FromResult<bool?>(++probeCount == 1),
            monotonicNow: () => monotonicTime);
        var instance = CreateInstance();
        var afterStopInvoked = false;

        var result = await service.StopAsync(
            instance,
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) =>
                {
                    afterStopInvoked = true;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(afterStopInvoked);
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task StopAsync_grace_period_is_not_extended_when_the_wall_clock_moves_backward()
    {
        var module = new TestModule();
        var wallClock = DateTimeOffset.UtcNow;
        var monotonicTime = TimeSpan.Zero;
        var probeCount = 0;
        var service = new ServerLifecycleService(
            () => [module],
            utcNow: () => wallClock,
            delayAsync: (delay, _) =>
            {
                wallClock -= TimeSpan.FromMinutes(1);
                monotonicTime += delay;
                return Task.CompletedTask;
            },
            tryIsRunningAsync: (_, _, _, _) =>
            {
                probeCount++;
                return Task.FromResult<bool?>(true);
            },
            monotonicNow: () => monotonicTime);

        var result = await service.StopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) => Task.CompletedTask),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("still running", result.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(probeCount, 2, 40);
    }

    private static void WriteRuntimeState(string installPath, int? pid = null, string? executable = null)
    {
        var serverFolder = Directory.GetParent(installPath)!.FullName;
        Directory.CreateDirectory(serverFolder);
        var state = new Dictionary<string, object?>
        {
            ["pid"] = pid ?? Environment.ProcessId,
            ["installPath"] = installPath,
            ["executable"] = executable ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty,
            ["updatedUtc"] = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(serverFolder, "runtime.json"), System.Text.Json.JsonSerializer.Serialize(state));
    }

    [Fact]
    public async Task StopAsync_remains_successful_when_noncritical_after_stop_work_is_cancelled()
    {
        var module = new TestModule();
        var service = CreateService(module);
        using var cts = new CancellationTokenSource();
        var logs = new List<string>();

        var result = await service.StopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, token) => MainWindow.RunNonCriticalLifecycleHookAsync(
                    _ =>
                    {
                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    },
                    token,
                    logs.Add,
                    "removal cancelled",
                    "removal failed")),
            cts.Token);

        Assert.True(result.Success);
        Assert.Contains("removal cancelled", logs);
    }

    [Fact]
    public async Task RestartAsync_runs_stop_then_start()
    {
        var module = new TestModule { InstallValid = true };
        var service = CreateService(module);

        var result = await service.RestartAsync(
            CreateInstance(),
            ServerLifecycleStartOptions.Default,
            new ServerLifecycleStopOptions(StopDelay: TimeSpan.Zero),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(module.StopCalled);
        Assert.True(module.StartCalled);
        Assert.True(module.StopOrder < module.StartOrder);
    }

    [Fact]
    public async Task ForceStopAsync_succeeds_when_no_processes_match()
    {
        var module = new TestModule();
        var service = CreateService(module);
        var stoppedLogTail = false;
        var stoppedAddons = false;

        var result = await service.ForceStopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopLogTail: _ => stoppedLogTail = true,
                StopAddonProcesses: _ => stoppedAddons = true,
                StopDelay: TimeSpan.Zero),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(stoppedLogTail);
        Assert.True(stoppedAddons);
    }

    [Fact]
    public async Task ForceStopAsync_invokes_after_stop_only_after_addon_cleanup()
    {
        // ForceStopAsync is the third of three call sites that thread AfterStopAsync through
        // (StopAsync and ForceStopAsync share the same shape) - covered separately from StopAsync's
        // own equivalent test since it goes through ServerForceStopper.KillAsync instead of
        // module.StopAsync, not just a copy-paste of the same path.
        var module = new TestModule();
        var service = CreateService(module);
        var stoppedAddons = false;
        var callbackObservedAddonsStopped = false;

        var result = await service.ForceStopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopAddonProcesses: _ => stoppedAddons = true,
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) =>
                {
                    callbackObservedAddonsStopped = stoppedAddons;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(callbackObservedAddonsStopped);
    }

    [Fact]
    public async Task ForceStopAsync_fails_and_skips_after_stop_when_exit_probe_is_indeterminate()
    {
        var module = new TestModule();
        var service = new ServerLifecycleService(
            () => [module],
            delayAsync: (_, _) => Task.CompletedTask,
            tryIsRunningAsync: (_, _, _, _) => Task.FromResult<bool?>(null));
        var afterStopInvoked = false;

        var result = await service.ForceStopAsync(
            CreateInstance(),
            new ServerLifecycleStopOptions(
                StopDelay: TimeSpan.Zero,
                AfterStopAsync: (_, _, _) =>
                {
                    afterStopInvoked = true;
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not confirm", result.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(afterStopInvoked);
    }

    private static ServerLifecycleService CreateService(
        TestModule module,
        IProcessTuningService? tuning = null,
        JavaRuntimeManager? javaRuntimeManager = null)
    {
        return new ServerLifecycleService(
            () => [module],
            utcNow: () => DateTimeOffset.UtcNow,
            delayAsync: (_, _) => Task.CompletedTask,
            processTuningService: tuning,
            javaRuntimeManager: javaRuntimeManager);
    }

    private static ServerInstance CreateInstance()
    {
        var installPath = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installPath);
        return new ServerInstance(
            "server-1",
            "Server",
            "test",
            Path.GetDirectoryName(installPath)!,
            installPath,
            Path.Combine(Path.GetDirectoryName(installPath)!, "ServerConfig.json"),
            new Dictionary<string, object?>());
    }

    private sealed class TestModule : IGameServerModule
    {
        private int _order;

        public bool InstallValid { get; set; } = true;
        public Process? Process { get; set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool RequiresJava { get; set; }
        public int? MinimumJavaMajor { get; set; }
        public int StartOrder { get; private set; }
        public int StopOrder { get; private set; }
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
            SupportsDirectConnection: false,
            RequiresJava: RequiresJava,
            MinimumJavaMajor: MinimumJavaMajor);
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
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            StartCalled = true;
            StartOrder = ++_order;
            return Task.FromResult(Process);
        }

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            StopCalled = true;
            StopOrder = ++_order;
            return Task.CompletedTask;
        }

        public bool IsInstallValid(ServerInstance instance) => InstallValid;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingProcessTuningService : IProcessTuningService
    {
        public ServerRuntimeSettings? Settings { get; private set; }

        public ProcessTuningResult Apply(Process process, ServerRuntimeSettings settings)
        {
            Settings = settings;
            return new ProcessTuningResult(
                true,
                ProcessPriorityClass.AboveNormal,
                null,
                [new ProcessTuningStepResult(ProcessTuningTarget.Priority, true, "Applied process priority AboveNormal.")]);
        }
    }
}
