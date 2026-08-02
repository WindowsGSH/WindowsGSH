using System.Diagnostics;
using WindowsGSH.Core.Modules;
using System.Text.Json;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerProcessLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void IsRunning_uses_runtime_pid_when_install_path_matches()
    {
        var installPath = Path.Combine(_root, "server", "files");
        WriteRuntimeState(installPath, DateTimeOffset.UtcNow);

        var running = ServerProcessLocator.IsRunning(CreateModule(), installPath);

        Assert.True(running);
    }

    [Fact]
    public void IsRunning_ignores_runtime_pid_when_process_started_after_runtime_timestamp()
    {
        var installPath = Path.Combine(_root, "server", "files");
        WriteRuntimeState(installPath, DateTimeOffset.Parse("2000-01-01T00:00:00+00:00"));

        var running = ServerProcessLocator.IsRunning(CreateModule(), installPath);

        Assert.False(running);
    }

    [Fact]
    public void IsRunning_ignores_runtime_pid_when_install_path_does_not_match()
    {
        var installPath = Path.Combine(_root, "server", "files");
        var otherInstallPath = Path.Combine(_root, "other", "files");
        WriteRuntimeState(otherInstallPath, DateTimeOffset.UtcNow);

        var running = ServerProcessLocator.IsRunning(CreateModule(), installPath);

        Assert.False(running);
    }

    [Fact]
    public void IsRunning_ignores_runtime_pid_when_executable_does_not_match()
    {
        var installPath = Path.Combine(_root, "server", "files");
        WriteRuntimeState(installPath, DateTimeOffset.UtcNow, executable: Path.Combine(_root, "not-the-process.exe"));

        var running = ServerProcessLocator.IsRunning(CreateModule(), installPath);

        Assert.False(running);
    }

    [Fact]
    public void IsRunning_ignores_runtime_pid_when_executable_is_missing_and_process_path_is_unrelated()
    {
        var installPath = Path.Combine(_root, "server", "files");
        var logs = new List<string>();
        WriteRuntimeState(installPath, DateTimeOffset.UtcNow, includeExecutable: false);

        var processes = ServerProcessLocator.FindProcesses(CreateModule(), installPath, logs.Add);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        Assert.Empty(processes);
        Assert.Contains(logs, line => line.Contains("Runtime PID", StringComparison.Ordinal));
    }

    [Fact]
    public void IsRunning_ignores_runtime_pid_when_executable_is_blank_and_process_path_is_unrelated()
    {
        var installPath = Path.Combine(_root, "server", "files");
        WriteRuntimeState(installPath, DateTimeOffset.UtcNow, executable: " ");

        var running = ServerProcessLocator.IsRunning(CreateModule(), installPath);

        Assert.False(running);
    }

    [Fact]
    public void MatchesRuntimeExecutable_without_executable_rejects_unrelated_process_path()
    {
        using var document = JsonDocument.Parse("{}");
        var installPath = Path.Combine(_root, "server", "files");
        var startPath = Path.Combine(installPath, "server.exe");
        var unrelatedProcessPath = Path.Combine(_root, "other", "server.exe");

        var matches = ServerProcessLocator.MatchesRuntimeExecutable(
            document.RootElement,
            unrelatedProcessPath,
            startPath,
            installPath);

        Assert.False(matches);
    }

    [Fact]
    public void MatchesRuntimeExecutable_without_executable_accepts_install_contained_process_path()
    {
        using var document = JsonDocument.Parse("{}");
        var installPath = Path.Combine(_root, "server", "files");
        var startPath = Path.Combine(installPath, "server.exe");
        var containedProcessPath = Path.Combine(installPath, "bin", "wrapper.exe");

        var matches = ServerProcessLocator.MatchesRuntimeExecutable(
            document.RootElement,
            containedProcessPath,
            startPath,
            installPath);

        Assert.True(matches);
    }

    [Fact]
    public async Task TryIsRunningAsync_returns_null_within_the_timeout_when_the_modules_Runtime_getter_hangs()
    {
        // Regression guard for a P1 finding: StopServerForShutdownAsync's non-force path (used during
        // Windows session ending) called the synchronous IsRunning directly and unbounded - since
        // IsRunning reads module.Runtime (an arbitrary property getter) via FindProcesses, a broken
        // module could hang it forever with no way to interrupt via cancellation, defeating the whole
        // point of the stale-snapshot shutdown fallback that exists specifically to survive this exact
        // class of module failure. TryIsRunningAsync must return null (not hang, not throw) once its
        // own timeout elapses, instead of depending on the underlying synchronous call ever returning.
        var installPath = Path.Combine(_root, "server", "files");
        using var releaseGate = new ManualResetEventSlim(false);
        var module = new HangingRuntimeModule(releaseGate);

        var result = await ServerProcessLocator.TryIsRunningAsync(module, installPath, TimeSpan.FromMilliseconds(300));

        Assert.Null(result);

        // Release the blocked worker so it doesn't leak a thread-pool thread past the end of this test.
        releaseGate.Set();
    }

    [Fact]
    public async Task TryIsRunningAsync_reuses_one_pending_probe_across_repeated_timeouts()
    {
        var installPath = Path.Combine(_root, "server", "files");
        using var releaseGate = new ManualResetEventSlim(false);
        var module = new HangingRuntimeModule(releaseGate);

        var first = await ServerProcessLocator.TryIsRunningAsync(
            module,
            installPath,
            TimeSpan.FromMilliseconds(200));
        var firstProbe = ServerProcessLocator.GetPendingProbeForTests(module, installPath);

        var second = await ServerProcessLocator.TryIsRunningAsync(
            module,
            installPath,
            TimeSpan.FromMilliseconds(200));
        var secondProbe = ServerProcessLocator.GetPendingProbeForTests(module, installPath);

        Assert.Null(first);
        Assert.Null(second);
        Assert.NotNull(firstProbe);
        Assert.Same(firstProbe, secondProbe);

        releaseGate.Set();
        await firstProbe!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TryIsRunningAsync_tracks_separate_probes_for_different_modules_at_the_same_install_path()
    {
        // Regression guard: the dedup key must incorporate module identity, not just installPath -
        // otherwise a second, DIFFERENT module probed against the same install path would silently
        // share (and be handed the result of) the first module's own probe.
        var installPath = Path.Combine(_root, "server", "files");
        using var releaseGateA = new ManualResetEventSlim(false);
        using var releaseGateB = new ManualResetEventSlim(false);
        var moduleA = new HangingRuntimeModule(releaseGateA, id: "module-a");
        var moduleB = new HangingRuntimeModule(releaseGateB, id: "module-b");

        var probeATask = ServerProcessLocator.TryIsRunningAsync(moduleA, installPath, TimeSpan.FromSeconds(5));

        var pendingForModuleA = ServerProcessLocator.GetPendingProbeForTests(moduleA, installPath);
        Assert.NotNull(pendingForModuleA);
        // Module B's own key must show no in-flight probe at all - if the dedup key ignored module
        // identity, this would incorrectly return module A's still-pending probe instead.
        Assert.Null(ServerProcessLocator.GetPendingProbeForTests(moduleB, installPath));

        releaseGateA.Set();
        await probeATask;
        releaseGateB.Set();
    }

    [Fact]
    public async Task TryIsRunningAsync_returns_the_real_result_when_the_module_answers_within_the_timeout()
    {
        var installPath = Path.Combine(_root, "server", "files");
        WriteRuntimeState(installPath, DateTimeOffset.UtcNow);

        var result = await ServerProcessLocator.TryIsRunningAsync(CreateModule(), installPath, TimeSpan.FromSeconds(5));

        Assert.True(result);
    }

    private void WriteRuntimeState(
        string installPath,
        DateTimeOffset updatedUtc,
        string? executable = null,
        bool includeExecutable = true)
    {
        var serverFolder = Directory.GetParent(installPath)!.FullName;
        Directory.CreateDirectory(serverFolder);
        var state = new Dictionary<string, object?>
        {
            ["pid"] = Environment.ProcessId,
            ["installPath"] = installPath,
            ["attached"] = false,
            ["sessionId"] = Environment.ProcessId,
            ["updatedUtc"] = updatedUtc
        };

        if (includeExecutable)
        {
            state["executable"] = executable ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }

        File.WriteAllText(Path.Combine(serverFolder, "runtime.json"), JsonSerializer.Serialize(state));
    }

    private static IGameServerModule CreateModule()
    {
        return new TestModule();
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
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["definitely-not-running-process-name"]);
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
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Simulates a broken module whose Runtime getter hangs - the exact hazard TryIsRunningAsync exists
    // to bound. Blocks the calling thread until releaseGate is signaled, with its own generous internal
    // safety-net bound so a forgotten Set() call can't block a thread-pool thread forever even if a test
    // fails before releasing it.
    private sealed class HangingRuntimeModule : IGameServerModule
    {
        private readonly ManualResetEventSlim _releaseGate;

        public HangingRuntimeModule(ManualResetEventSlim releaseGate, string id = "test")
        {
            _releaseGate = releaseGate;
            Id = id;
        }

        public string Id { get; }
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
        public ModuleRuntimeDefinition Runtime
        {
            get
            {
                _releaseGate.Wait(TimeSpan.FromSeconds(10));
                return new ModuleRuntimeDefinition("server.exe", ["definitely-not-running-process-name"]);
            }
        }
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
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
