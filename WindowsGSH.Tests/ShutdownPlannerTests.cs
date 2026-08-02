using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ShutdownPlannerTests
{
    [Fact]
    public void SelectStopCandidates_returns_running_servers_only()
    {
        var running = Server("running", canStop: true);
        var offline = Server("offline", canStop: false);

        var result = new ShutdownPlanner().SelectStopCandidates([running, offline]);

        var candidate = Assert.Single(result);
        Assert.Equal("running", candidate.Id);
    }

    [Fact]
    public void CreateDiscordOfflineSnapshot_marks_server_offline_and_non_actionable()
    {
        var snapshot = new ShutdownPlanner().CreateDiscordOfflineSnapshot(Server(
            "running",
            canStop: true,
            processId: "1234",
            hasUpdateAvailable: true,
            lastOperationError: "Previous failure"));

        Assert.Equal("Offline", snapshot.CurrentStatusText);
        Assert.Equal(ServerRuntimeStatus.Offline, snapshot.Status);
        Assert.Equal("Offline", snapshot.StatusText);
        Assert.Equal("BadBrush", snapshot.StatusBrushKey);
        Assert.False(snapshot.CanStart);
        Assert.False(snapshot.CanStop);
        Assert.False(snapshot.IsOperationRunning);
        Assert.False(snapshot.HasUpdateAvailable);
        Assert.Null(snapshot.LastOperationError);
    }

    [Fact]
    public void CreateDiscordOfflineSnapshot_clears_runtime_fields()
    {
        var snapshot = new ShutdownPlanner().CreateDiscordOfflineSnapshot(Server(
            "running",
            processId: "1234",
            cpuUsage: "10%",
            memoryUsage: "256 MB",
            playerCount: "3/10",
            uptime: "01:02:03"));

        Assert.Equal("--", snapshot.ProcessId);
        Assert.Equal("--", snapshot.CpuUsage);
        Assert.Equal("--", snapshot.MemoryUsage);
        Assert.Equal("--", snapshot.PlayerCount);
        Assert.Equal("--", snapshot.Uptime);
        Assert.Equal("WindowsGSH closed", snapshot.OperationText);
    }

    [Fact]
    public void CreateDiscordOfflineSnapshot_preserves_identity_and_display_fields()
    {
        var server = Server("server-1", name: "Icarus", moduleId: "icarus", installPath: @"D:\Servers\Icarus");

        var snapshot = new ShutdownPlanner().CreateDiscordOfflineSnapshot(server);

        Assert.Equal(server.Id, snapshot.Id);
        Assert.Equal(server.Name, snapshot.Name);
        Assert.Equal(server.ModuleId, snapshot.ModuleId);
        Assert.Equal(server.InstallPath, snapshot.InstallPath);
        Assert.Equal(server.ConfigPath, snapshot.ConfigPath);
    }

    [Fact]
    public void ResolveShutdownServers_prefers_fresh_servers_when_available()
    {
        IReadOnlyList<InstalledServer> fresh = [Server("fresh")];
        IReadOnlyList<InstalledServer> lastKnownGood = [Server("stale")];

        var result = new ShutdownPlanner().ResolveShutdownServers(fresh, lastKnownGood, allowStaleFallback: false);

        Assert.Same(fresh, result);
    }

    [Fact]
    public void ResolveShutdownServers_throws_for_an_abortable_user_action_even_when_a_stale_snapshot_exists()
    {
        // Regression guard for a P1 finding: an earlier version of this method fell back to the
        // last-known-good snapshot whenever fresh enumeration failed, regardless of allowStaleFallback
        // - but a snapshot only proves what was true when it was recorded. A server it shows offline
        // may have started since; a server may have been added since; a server it shows running may
        // have changed state while its own module was hung (exactly the scenario that makes fresh
        // enumeration untrustworthy in the first place). For an abortable user action (user close, tray
        // Stop All), ANY failed fresh enumeration must abort - a stale snapshot must never substitute
        // for a live result here, even though one is available.
        IReadOnlyList<InstalledServer> lastKnownGood = [Server("stale")];

        Assert.Throws<InvalidOperationException>(
            () => new ShutdownPlanner().ResolveShutdownServers(null, lastKnownGood, allowStaleFallback: false));
    }

    [Fact]
    public void ResolveShutdownServers_throws_for_an_abortable_user_action_when_no_trustworthy_data_exists_at_all()
    {
        // Same abort requirement as above, for the case with no snapshot at all (e.g. shortly after
        // app start, before the first clean refresh) - falling back to an empty list here would let
        // user-close/tray-stop-all conclude "confirmed zero servers running" and exit while a real,
        // running game server process survives unmanaged.
        Assert.Throws<InvalidOperationException>(
            () => new ShutdownPlanner().ResolveShutdownServers(null, null, allowStaleFallback: false));
    }

    [Fact]
    public void ResolveShutdownServers_falls_back_to_the_last_known_good_snapshot_when_stale_fallback_is_allowed()
    {
        // The Windows-session-ending path cannot block the OS shutdown to demand a retry - it must
        // proceed with the best available information instead of throwing.
        IReadOnlyList<InstalledServer> lastKnownGood = [Server("stale")];

        var result = new ShutdownPlanner().ResolveShutdownServers(null, lastKnownGood, allowStaleFallback: true);

        Assert.Same(lastKnownGood, result);
    }

    [Fact]
    public void ResolveShutdownServers_returns_an_empty_list_when_stale_fallback_is_allowed_but_no_snapshot_exists()
    {
        var result = new ShutdownPlanner().ResolveShutdownServers(null, null, allowStaleFallback: true);

        Assert.Empty(result);
    }

    private static InstalledServer Server(
        string id,
        string name = "Test Server",
        string moduleId = "test",
        string installPath = @"D:\Servers\Test",
        bool canStop = false,
        string processId = "--",
        string cpuUsage = "--",
        string memoryUsage = "--",
        string playerCount = "--",
        string uptime = "--",
        bool hasUpdateAvailable = false,
        string? lastOperationError = null)
    {
        return new InstalledServer(
            Id: id,
            Name: name,
            ModuleId: moduleId,
            Runtime: "native",
            ServerFolder: installPath,
            InstallPath: installPath,
            ConfigPath: Path.Combine(installPath, "ServerConfig.json"),
            IpAddress: "127.0.0.1",
            Port: "27015",
            SteamAppId: "",
            SteamBranch: "",
            MaxPlayers: "10",
            ProcessId: processId,
            CpuUsage: cpuUsage,
            MemoryUsage: memoryUsage,
            PlayerCount: playerCount,
            CurrentStatusText: canStop ? "Online" : "Offline",
            Uptime: uptime,
            IsOperationRunning: canStop,
            OperationText: canStop ? "Running" : "",
            LastOperationError: lastOperationError,
            IsInstalled: true,
            Status: canStop ? ServerRuntimeStatus.Running : ServerRuntimeStatus.Offline,
            StatusText: canStop ? "Online" : "Offline",
            StatusBrushKey: canStop ? "GoodBrush" : "BadBrush",
            HasUpdateAvailable: hasUpdateAvailable,
            LocalBuildId: "1",
            RemoteBuildId: hasUpdateAvailable ? "2" : "1",
            IgnoredBuildId: "",
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: !canStop,
            CanStop: canStop);
    }
}
