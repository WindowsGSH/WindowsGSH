using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using Xunit;
using System.IO;

namespace WindowsGSH.Tests;

/// <summary>
/// Unit tests for the *WithScopeAsync methods on ServerOperationsController.
/// These test the production code path that the web dispatch uses after pre-acquiring
/// a ServerOperationScope: they verify scope disposal (→ Completed), failure (→ Failed),
/// and cancellation (→ Cancelled) without needing WPF or a web host.
/// </summary>
public sealed class ServerOperationsControllerScopeTests
{
    // Minimal stub server — no real InstallPath/config needed for delegate-based tests.
    private static readonly InstalledServer StubServer = new InstalledServer(
        Id: "srv-x", Name: "Stub Server", ModuleId: "Stub",
        Runtime: "", ServerFolder: "", InstallPath: @"C:\stub",
        ConfigPath: "", IpAddress: "127.0.0.1", Port: "0",
        SteamAppId: "", SteamBranch: "", MaxPlayers: "0",
        ProcessId: "", CpuUsage: "", MemoryUsage: "",
        PlayerCount: "", CurrentStatusText: "", Uptime: "",
        IsOperationRunning: false, OperationText: "", LastOperationError: null,
        IsInstalled: true, Status: ServerRuntimeStatus.Offline, StatusText: "Offline",
        StatusBrushKey: "", HasUpdateAvailable: false,
        LocalBuildId: "", RemoteBuildId: "", IgnoredBuildId: "",
        CanShowInfo: false, CanEditConfig: false, CanStart: true, CanStop: false);

    private static ServerOperationsController MakeController(
        ServerOperationManager mgr,
        Func<InstalledServer, bool, CancellationToken, Task>? startServerAsync = null,
        Func<InstalledServer, IGameServerModule, string, bool, CancellationToken, Task>? updateServerAsync = null,
        ServerLifecycleService? lifecycleService = null,
        List<string>? log = null)
        => new ServerOperationsController(
            mgr,
            lifecycleService ?? new ServerLifecycleService(),
            new ServerBackupService(),
            _ => null!,
            startServerAsync ?? ((_, _, _) => Task.CompletedTask),
            updateServerAsync ?? ((_, _, _, _, _) => Task.CompletedTask),
            (_, _) => new ServerLifecycleStopOptions(),
            _ => [],
            () => 0,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            (msg, _) => log?.Add(msg),
            (_, _) => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            (_, _) => { });

    private static ServerOperationScope AcquireScope(ServerOperationManager mgr,
        ServerOperationKind kind = ServerOperationKind.Start)
    {
        mgr.TryBegin(StubServer.Id, StubServer.Name, kind,
            out var scope, out _, "Initiated via web by operator");
        return scope!;
    }

    // --- StartWithScopeAsync ---

    [Fact]
    public async Task StartWithScopeAsync_success_disposes_scope_as_completed()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr);
        var scope = AcquireScope(mgr);

        await ctrl.StartWithScopeAsync(StubServer, scope);

        Assert.Single(history);
        Assert.Equal("Completed", history[0].Status);
        // Manager keeps the terminal snapshot so polling can read it.
        var finalOp = mgr.Get(StubServer.Id);
        Assert.NotNull(finalOp);
        Assert.Equal("Completed", finalOp!.Status);
        Assert.False(finalOp.IsActive);
    }

    [Fact]
    public async Task StartWithScopeAsync_exception_calls_fail_on_manager()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr, startServerAsync: (_, _, _) =>
            Task.FromException(new InvalidOperationException("Disk full")));
        var scope = AcquireScope(mgr);

        await ctrl.StartWithScopeAsync(StubServer, scope);

        Assert.Single(history);
        Assert.Equal("Failed", history[0].Status);
        Assert.Equal("Disk full", history[0].LastError);
    }

    [Fact]
    public async Task StartWithScopeAsync_cancellation_disposes_scope_as_cancelled()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr, startServerAsync: (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        mgr.TryBegin(StubServer.Id, StubServer.Name, ServerOperationKind.Start,
            out var scope, out _, "web");
        mgr.TryCancelWithAudit(StubServer.Id, "Cancelled via web by operator");

        await ctrl.StartWithScopeAsync(StubServer, scope!);

        Assert.Single(history);
        Assert.Equal("Cancelled", history[0].Status);
    }

    [Fact]
    public async Task StartWithScopeAsync_exception_does_not_double_dispose_scope()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr, startServerAsync: (_, _, _) =>
            Task.FromException(new IOException("Write error")));
        var scope = AcquireScope(mgr);

        await ctrl.StartWithScopeAsync(StubServer, scope);

        Assert.Single(history);
    }

    // --- UpdateWithScopeAsync ---

    [Fact]
    public async Task UpdateWithScopeAsync_success_disposes_scope_as_completed()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr);
        var scope = AcquireScope(mgr, ServerOperationKind.Update);

        await ctrl.UpdateWithScopeAsync(StubServer, scope);

        Assert.Single(history);
        Assert.Equal("Completed", history[0].Status);
    }

    [Fact]
    public async Task UpdateWithScopeAsync_exception_calls_fail_on_manager()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr, updateServerAsync:
            (_, _, _, _, _) => Task.FromException(new TimeoutException("SteamCMD timeout")));
        var scope = AcquireScope(mgr, ServerOperationKind.Update);

        await ctrl.UpdateWithScopeAsync(StubServer, scope);

        Assert.Single(history);
        Assert.Equal("Failed", history[0].Status);
        Assert.Equal("SteamCMD timeout", history[0].LastError);
    }

    // --- Scope description preserved through completion ---

    [Fact]
    public async Task StartWithScopeAsync_completed_history_row_preserves_web_description()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr);
        var scope = AcquireScope(mgr);

        await ctrl.StartWithScopeAsync(StubServer, scope);

        Assert.Single(history);
        Assert.Equal("Initiated via web by operator", history[0].Description);
    }

    [Fact]
    public async Task StartWithScopeAsync_cancel_history_row_has_combined_description()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);
        var ctrl = MakeController(mgr, startServerAsync: (_, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        mgr.TryBegin(StubServer.Id, StubServer.Name, ServerOperationKind.Start,
            out var scope, out _, "Initiated via web by operator");
        mgr.TryCancelWithAudit(StubServer.Id, "Cancelled via web by operator");

        await ctrl.StartWithScopeAsync(StubServer, scope!);

        Assert.Single(history);
        Assert.Equal("Cancelled", history[0].Status);
        Assert.Contains("Initiated via web by operator", history[0].Description);
        Assert.Contains("Cancelled via web by operator", history[0].Description);
    }

    // --- ForceStopWithScopeAsync cancellation ---

    [Fact]
    public async Task ForceStopWithScopeAsync_cancellation_records_cancelled_not_failed()
    {
        // Uses a lifecycle service that can find the stub module by ID (so ResolveModule succeeds),
        // with a process name that won't be running (so KillAsync exits immediately with 0 processes),
        // then the post-stop delay is cancelled by the pre-cancelled CT → OCE propagates up.
        var tmpConfig = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmpConfig, """{"settings": {}}""");
            var server = StubServer with { ConfigPath = tmpConfig };

            var history = new List<ServerOperationSnapshot>();
            var mgr = new ServerOperationManager(history.Add);
            var stubModule = new StubModule();
            var lifecycleService = new ServerLifecycleService(getModules: () => [stubModule]);
            var ctrl = MakeController(mgr, lifecycleService: lifecycleService);

            mgr.TryBegin(server.Id, server.Name, ServerOperationKind.ForceStop,
                out var scope, out _, "Initiated via web by operator");
            mgr.TryCancelWithAudit(server.Id, "Cancelled via web by operator");

            await ctrl.ForceStopWithScopeAsync(server, scope!);

            Assert.Single(history);
            Assert.Equal("Cancelled", history[0].Status);
        }
        finally
        {
            System.IO.File.Delete(tmpConfig);
        }
    }

    private sealed class StubModule : IGameServerModule
    {
        public string Id => "Stub";
        public string Name => "Stub";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false, SupportsUpdate: false, SupportsQuery: false,
            SupportsRcon: false, SupportsConsoleCommands: false, SupportsApiActions: false,
            SupportsBackups: false, SupportsDirectConnection: false,
            RequiresJava: false, MinimumJavaMajor: null);
        public ModuleRuntimeDefinition Runtime =>
            new("definitely-not-running.exe", ["definitely-not-running"]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Stub";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) =>
            new("127.0.0.1", "0", "0");
        public Task<System.Diagnostics.ProcessStartInfo> CreateStartInfoAsync(
            ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<System.Diagnostics.Process?> StartAsync(
            ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public bool IsInstallValid(ServerInstance instance) => false;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}
