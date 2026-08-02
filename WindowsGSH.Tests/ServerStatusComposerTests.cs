using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerStatusComposerTests
{
    [Fact]
    public void Apply_overlays_active_operation_status_and_controls()
    {
        var server = CreateServer(canStart: true, canStop: false);
        var operation = new ServerOperationSnapshot(
            server.Id,
            server.Name,
            ServerOperationKind.Update,
            "Updating",
            DateTimeOffset.UtcNow,
            LastError: null,
            IsActive: true);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        var result = Assert.Single(composer.Apply([server], _ => operation));

        Assert.Equal("Updating", result.CurrentStatusText);
        Assert.True(result.IsOperationRunning);
        Assert.Equal(operation.DisplayText, result.OperationText);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void Apply_overlays_failed_operation_error_without_replacing_status()
    {
        var server = CreateServer(currentStatusText: "Offline", status: ServerRuntimeStatus.Offline);
        var operation = new ServerOperationSnapshot(
            server.Id,
            server.Name,
            ServerOperationKind.Backup,
            "Failed",
            DateTimeOffset.UtcNow,
            "backup path missing",
            IsActive: false,
            FinishedAt: DateTimeOffset.UtcNow);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        var result = Assert.Single(composer.Apply([server], _ => operation));

        Assert.Equal("Offline", result.CurrentStatusText);
        Assert.Equal("backup path missing", result.LastOperationError);
        Assert.Equal(operation.DisplayText, result.OperationText);
    }

    [Fact]
    public void Apply_keeps_warning_server_as_booting_during_grace_period()
    {
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        var server = CreateServer(currentStatusText: "Offline", status: ServerRuntimeStatus.Warning, statusText: "Warning", statusBrushKey: "WarnBrush");
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        composer.MarkBooting(server.Id, now);

        var result = Assert.Single(composer.Apply([server], _ => null, now.AddMinutes(5)));

        Assert.Equal("Booting", result.CurrentStatusText);
        Assert.Equal(ServerRuntimeStatus.Running, result.Status);
        Assert.Equal("Booting", result.StatusText);
        Assert.Equal("GoodBrush", result.StatusBrushKey);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void IsBooting_tracks_boot_grace_period_and_clear()
    {
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        composer.MarkBooting("server-1", now);

        Assert.True(composer.IsBooting("server-1", now.AddMinutes(5)));
        Assert.False(composer.IsBooting("server-1", now.AddMinutes(11)));

        composer.MarkBooting("server-1", now);
        composer.ClearBooting("server-1");

        Assert.False(composer.IsBooting("server-1", now.AddMinutes(5)));
    }

    [Fact]
    public void Apply_expires_booting_grace_period_for_warning_server()
    {
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        var server = CreateServer(currentStatusText: "Offline", status: ServerRuntimeStatus.Warning, statusText: "Warning", statusBrushKey: "WarnBrush");
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        composer.MarkBooting(server.Id, now);

        var result = Assert.Single(composer.Apply([server], _ => null, now.AddMinutes(11)));

        Assert.Equal("Offline", result.CurrentStatusText);
        Assert.Equal(ServerRuntimeStatus.Warning, result.Status);
        Assert.Equal("Warning", result.StatusText);
        Assert.Equal("WarnBrush", result.StatusBrushKey);
    }

    [Fact]
    public void Running_server_clears_booting_grace_marker()
    {
        var now = DateTimeOffset.Parse("2026-05-16T12:00:00+00:00");
        var warning = CreateServer(currentStatusText: "Offline", status: ServerRuntimeStatus.Warning, statusText: "Warning", statusBrushKey: "WarnBrush");
        var running = warning with { CurrentStatusText = "Online", Status = ServerRuntimeStatus.Running, StatusText = "Online", StatusBrushKey = "GoodBrush" };
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        composer.MarkBooting(warning.Id, now);
        _ = composer.Apply([running], _ => null, now.AddMinutes(1));
        var result = Assert.Single(composer.Apply([warning], _ => null, now.AddMinutes(2)));

        Assert.Equal("Offline", result.CurrentStatusText);
        Assert.Equal(ServerRuntimeStatus.Warning, result.Status);
    }

    [Fact]
    public void Apply_uses_live_monitored_process_when_loader_snapshot_is_offline()
    {
        var server = CreateServer(
            currentStatusText: "Offline",
            status: ServerRuntimeStatus.Offline,
            statusText: "Offline",
            statusBrushKey: "BadBrush",
            canStart: true,
            canStop: false);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        var result = Assert.Single(composer.Apply(
            [server],
            _ => null,
            getLiveMonitoredProcessId: _ => 38904));

        Assert.Equal(ServerRuntimeStatus.Running, result.Status);
        Assert.Equal("Online", result.CurrentStatusText);
        Assert.Equal("Running", result.StatusText);
        Assert.Equal("38904", result.ProcessId);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void Apply_preserves_completed_operation_metadata_while_using_live_process()
    {
        var server = CreateServer(
            currentStatusText: "Offline",
            status: ServerRuntimeStatus.Offline,
            statusText: "Offline",
            statusBrushKey: "BadBrush",
            canStart: true,
            canStop: false);
        var operation = new ServerOperationSnapshot(
            server.Id,
            server.Name,
            ServerOperationKind.Start,
            "Completed",
            DateTimeOffset.UtcNow,
            LastError: null,
            IsActive: false,
            FinishedAt: DateTimeOffset.UtcNow);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        var result = Assert.Single(composer.Apply(
            [server],
            _ => operation,
            getLiveMonitoredProcessId: _ => 38904));

        Assert.Equal(ServerRuntimeStatus.Running, result.Status);
        Assert.Equal("Online", result.CurrentStatusText);
        Assert.Equal(operation.DisplayText, result.OperationText);
        Assert.Equal("38904", result.ProcessId);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void Apply_preserves_warning_details_when_process_is_monitored()
    {
        var server = CreateServer(
            currentStatusText: "Query failed",
            status: ServerRuntimeStatus.Warning,
            statusText: "Issues found",
            statusBrushKey: "WarnBrush",
            canStart: false,
            canStop: true);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));

        var result = Assert.Single(composer.Apply(
            [server],
            _ => null,
            getLiveMonitoredProcessId: _ => 38904));

        Assert.Equal(ServerRuntimeStatus.Warning, result.Status);
        Assert.Equal("Query failed", result.CurrentStatusText);
        Assert.Equal("Issues found", result.StatusText);
        Assert.Equal("WarnBrush", result.StatusBrushKey);
        Assert.Equal("38904", result.ProcessId);
        Assert.False(result.CanStart);
        Assert.True(result.CanStop);
    }

    [Fact]
    public void Apply_keeps_boot_grace_when_warning_server_has_live_process()
    {
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00+00:00");
        var server = CreateServer(
            currentStatusText: "Query failed",
            status: ServerRuntimeStatus.Warning,
            statusText: "Issues found",
            statusBrushKey: "WarnBrush",
            canStart: false,
            canStop: true);
        var composer = new ServerStatusComposer(TimeSpan.FromMinutes(10));
        composer.MarkBooting(server.Id, now);

        var result = Assert.Single(composer.Apply(
            [server],
            _ => null,
            now.AddMinutes(1),
            _ => 38904));

        Assert.Equal(ServerRuntimeStatus.Running, result.Status);
        Assert.Equal("Booting", result.CurrentStatusText);
        Assert.Equal("Booting", result.StatusText);
        Assert.Equal("38904", result.ProcessId);
        Assert.True(composer.IsBooting(server.Id, now.AddMinutes(2)));
    }

    private static InstalledServer CreateServer(
        string currentStatusText = "Online",
        ServerRuntimeStatus status = ServerRuntimeStatus.Running,
        string statusText = "Online",
        string statusBrushKey = "GoodBrush",
        bool canStart = false,
        bool canStop = true)
    {
        return new InstalledServer(
            Id: "1",
            Name: "WindowsGSH Test Server",
            ModuleId: "test",
            Runtime: "steam",
            ServerFolder: @"C:\WindowsGSH\servers\1",
            InstallPath: @"C:\WindowsGSH\servers\1\server",
            ConfigPath: @"C:\WindowsGSH\servers\1\ServerConfig.json",
            IpAddress: "127.0.0.1",
            Port: "27015",
            SteamAppId: "123",
            SteamBranch: string.Empty,
            MaxPlayers: "8",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: currentStatusText,
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: string.Empty,
            LastOperationError: null,
            IsInstalled: true,
            Status: status,
            StatusText: statusText,
            StatusBrushKey: statusBrushKey,
            HasUpdateAvailable: false,
            LocalBuildId: string.Empty,
            RemoteBuildId: string.Empty,
            IgnoredBuildId: string.Empty,
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: canStart,
            CanStop: canStop);
    }
}
