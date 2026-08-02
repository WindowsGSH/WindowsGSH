using WindowsGSH.Core.Events;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordEventAlertServiceTests
{
    private DateTimeOffset _now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RenderOperationAlert_formats_completed_operation()
    {
        var alert = DiscordEventAlertService.RenderOperationAlert(new ServerOperationChangedEvent(
            _now,
            "1",
            "test",
            "Paper",
            ServerOperationKind.Start,
            ServerOperationStatus.Completed,
            "Completed",
            null,
            null));

        Assert.Equal("Server start completed: Paper.", alert);
    }

    [Fact]
    public void RenderOperationAlert_redacts_secret_values()
    {
        var alert = DiscordEventAlertService.RenderOperationAlert(new ServerOperationChangedEvent(
            _now,
            "1",
            "test",
            "Paper",
            ServerOperationKind.Update,
            ServerOperationStatus.Failed,
            "Failed",
            null,
            "password=super-secret token:abc123"));

        Assert.Contains("password=<redacted>", alert);
        Assert.Contains("token=<redacted>", alert);
        Assert.DoesNotContain("super-secret", alert);
        Assert.DoesNotContain("abc123", alert);
    }

    [Fact]
    public async Task Disabled_alerts_are_not_sent()
    {
        var sent = new List<string>();
        var service = CreateService(
            sent,
            isEnabled: (_, _) => false);

        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now,
            "1",
            "test",
            "Paper",
            ServerOperationKind.Stop,
            ServerOperationStatus.Completed,
            "Completed",
            null,
            null));

        Assert.Empty(sent);
    }

    [Fact]
    public async Task Crash_alerts_are_debounced_per_server()
    {
        var sent = new List<string>();
        var service = CreateService(sent, crashDebounce: TimeSpan.FromMinutes(5));

        await service.ProcessEventAsync(Crash("1"));
        _now = _now.AddMinutes(1);
        await service.ProcessEventAsync(Crash("1"));
        _now = _now.AddMinutes(5);
        await service.ProcessEventAsync(Crash("1"));

        Assert.Equal(2, sent.Count);
        Assert.All(sent, message => Assert.Contains("Server crash detected: Paper.", message));
    }

    [Fact]
    public async Task Channel_resolution_failures_do_not_escape_alert_pipeline()
    {
        var logs = new List<string>();
        var service = new DiscordEventAlertService(
            new WindowsGshEventBus(),
            _ => CreateServer(),
            (_, _) => true,
            (_, _) => throw new InvalidOperationException("channel missing"),
            () => _now,
            log: logs.Add);

        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now,
            "1",
            "test",
            "Paper",
            ServerOperationKind.Backup,
            ServerOperationStatus.Completed,
            "Completed",
            null,
            null));

        Assert.Contains(logs, message => message.Contains("Discord alert send failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_suppresses_mid_restart_status_flicker_and_reports_once()
    {
        var sent = new List<string>();
        var service = CreateService(sent);

        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Running));
        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now, "1", "test", "Paper", ServerOperationKind.Restart, ServerOperationStatus.Running,
            "Restarting", null, null));
        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Offline));
        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Running));
        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now, "1", "test", "Paper", ServerOperationKind.Restart, ServerOperationStatus.Completed,
            "Completed", null, null));

        var only = Assert.Single(sent);
        Assert.Equal("Server restart completed: Paper.", only);
    }

    [Fact]
    public async Task Restart_completion_shortens_suppression_so_a_later_unrelated_change_still_alerts()
    {
        var sent = new List<string>();
        var service = CreateService(sent);

        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Running));
        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now, "1", "test", "Paper", ServerOperationKind.Restart, ServerOperationStatus.Running,
            "Restarting", null, null));
        _now = _now.AddSeconds(5);
        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now, "1", "test", "Paper", ServerOperationKind.Restart, ServerOperationStatus.Completed,
            "Completed", null, null));
        _now = _now.AddMinutes(2);
        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Offline));

        Assert.Equal(["Server restart completed: Paper.", "Server offline: Paper."], sent);
    }

    [Fact]
    public async Task Restart_status_suppression_expires_after_its_window()
    {
        var sent = new List<string>();
        var service = CreateService(sent);

        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Running));
        await service.ProcessEventAsync(new ServerOperationChangedEvent(
            _now, "1", "test", "Paper", ServerOperationKind.Restart, ServerOperationStatus.Running,
            "Restarting", null, null));
        _now = _now.AddMinutes(10);
        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Offline));
        await service.ProcessEventAsync(Status("1", ServerRuntimeStatus.Running));

        Assert.Equal(["Server offline: Paper.", "Server online: Paper."], sent);
    }

    [Fact]
    public async Task Public_ip_change_sends_global_alert()
    {
        var sent = new List<string>();
        var service = CreateService(sent);

        await service.ProcessEventAsync(new PublicIpChangedEvent(_now, "203.0.113.10", "203.0.113.20"));

        var only = Assert.Single(sent);
        Assert.Equal("Public IP changed: 203.0.113.10 -> 203.0.113.20.", only);
    }

    private DiscordEventAlertService CreateService(
        List<string> sent,
        Func<string, string?, bool>? isEnabled = null,
        TimeSpan? crashDebounce = null)
    {
        return new DiscordEventAlertService(
            new WindowsGshEventBus(),
            _ => CreateServer(),
            isEnabled ?? ((_, _) => true),
            (message, _) =>
            {
                sent.Add(message);
                return Task.CompletedTask;
            },
            () => _now,
            crashDebounce);
    }

    private ServerStatusChangedEvent Status(string serverId, ServerRuntimeStatus status)
    {
        return new ServerStatusChangedEvent(
            _now, serverId, "test", "Paper", status, status == ServerRuntimeStatus.Running, false, status.ToString(), null);
    }

    private ServerCrashDetectedEvent Crash(string serverId)
    {
        return new ServerCrashDetectedEvent(
            _now,
            serverId,
            "test",
            "Paper",
            1234,
            @"C:\WindowsGSH\logs\crash.txt",
            "Paper exited unexpectedly.");
    }

    private static InstalledServer CreateServer()
    {
        return new InstalledServer(
            Id: "1",
            Name: "Paper",
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
            CurrentStatusText: "Offline",
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: string.Empty,
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Offline,
            StatusText: "Offline",
            StatusBrushKey: "StatusBrush",
            HasUpdateAvailable: false,
            LocalBuildId: string.Empty,
            RemoteBuildId: string.Empty,
            IgnoredBuildId: string.Empty,
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: true,
            CanStop: false);
    }
}
