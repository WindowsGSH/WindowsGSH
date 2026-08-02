using System.Text.Json;
using WindowsGSH.Core.Automation;
using WindowsGSH.Core.Scheduling;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerAutomationServiceTests
{
    [Fact]
    public void PlanStartup_SelectsEnabledServersInInputOrder()
    {
        var service = new ServerAutomationService();
        var now = new DateTimeOffset(2026, 5, 24, 20, 0, 0, TimeSpan.Zero);

        var result = service.PlanStartup(new AutomationStartupPlanRequest(
            now,
            IsClosing: false,
            TimeSpan.FromMinutes(1),
            [
                Candidate("1", autoStart: true),
                Candidate("2", autoStart: false, autoRestart: false),
                Candidate("3", autoRestart: true)
            ]));

        Assert.Equal(["1", "3"], result.Select(candidate => candidate.ServerId));
    }

    [Fact]
    public void PlanStartup_SkipsDisabledBlockedAndRecentlyCheckedServers()
    {
        var service = new ServerAutomationService();
        var now = new DateTimeOffset(2026, 5, 24, 20, 0, 0, TimeSpan.Zero);

        var result = service.PlanStartup(new AutomationStartupPlanRequest(
            now,
            IsClosing: false,
            TimeSpan.FromMinutes(1),
            [
                Candidate("missing", configExists: false),
                Candidate("running", canStart: false),
                Candidate("manual", manuallyStopped: true),
                Candidate("config", configOpen: true),
                Candidate("busy", operationRunning: true),
                Candidate("recent", lastCheck: now.AddSeconds(-30)),
                Candidate("ready")
            ]));

        var only = Assert.Single(result);
        Assert.Equal("ready", only.ServerId);
    }

    [Fact]
    public void PlanBeforeStart_OrdersBackupBeforeUpdate()
    {
        var service = new ServerAutomationService();
        var settings = ServerAutomationSettings.Default with
        {
            BackupOnStart = true,
            UpdateOnStart = true,
            AutoUpdate = true
        };

        var actions = service.PlanBeforeStart(settings, automatic: true);

        Assert.Equal([AutomationBeforeStartAction.Backup, AutomationBeforeStartAction.Update], actions);
    }

    [Fact]
    public void PlanBeforeStart_UsesAutoUpdateOnlyForAutomaticStart()
    {
        var service = new ServerAutomationService();
        var settings = ServerAutomationSettings.Default with { AutoUpdate = true };

        Assert.Empty(service.PlanBeforeStart(settings, automatic: false));
        Assert.Equal([AutomationBeforeStartAction.Update], service.PlanBeforeStart(settings, automatic: true));
    }

    [Fact]
    public void PlanCrashRestart_RespectsDisabledAutomation()
    {
        var service = new ServerAutomationService();
        var decision = service.PlanCrashRestart(new CrashRestartRequest(
            "1",
            "Paper",
            "config.json",
            ServerAutomationSettings.Default,
            DateTimeOffset.UtcNow,
            IsManuallyStopped: false,
            IsClosing: false));

        Assert.False(decision.ShouldRestart);
        Assert.Contains("disabled", decision.Message);
    }

    [Fact]
    public async Task PlanCrashRestart_ObeysDelayAndMaxAttempts()
    {
        var service = new ServerAutomationService();
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "ServerConfig.json");
        await File.WriteAllTextAsync(configPath, """{ "id": "1", "automation": {} }""");
        var now = new DateTimeOffset(2026, 5, 24, 21, 0, 0, TimeSpan.Zero);
        var settings = ServerAutomationSettings.Default with
        {
            RestartOnCrash = true,
            RestartDelaySeconds = 45,
            MaxRestartAttempts = 1
        };

        var first = service.PlanCrashRestart(new CrashRestartRequest("1", "Paper", configPath, settings, now, false, false));
        await service.RecordAutoRestartAsync(configPath, now);
        var second = service.PlanCrashRestart(new CrashRestartRequest("1", "Paper", configPath, settings, now.AddMinutes(1), false, false));

        Assert.True(first.ShouldRestart);
        Assert.Equal(TimeSpan.FromSeconds(45), first.Delay);
        Assert.False(second.ShouldRestart);
        Assert.True(second.IsMaxAttemptsReached);
        Assert.Contains("suppressed", second.Message);
    }

    [Fact]
    public async Task RecordsCrashAndRestartTimestampsInConfig()
    {
        var service = new ServerAutomationService();
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "ServerConfig.json");
        await File.WriteAllTextAsync(configPath, """{ "id": "1", "name": "Paper" }""");
        var crashAt = new DateTimeOffset(2026, 5, 24, 22, 0, 0, TimeSpan.Zero);
        var restartAt = crashAt.AddSeconds(30);

        await service.RecordCrashDetectedAsync(configPath, crashAt);
        await service.RecordAutoRestartAsync(configPath, restartAt);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var automation = document.RootElement.GetProperty("automation");
        Assert.Equal(crashAt.ToString("O"), automation.GetProperty("lastCrashDetectedAt").GetString());
        Assert.Equal(restartAt.ToString("O"), automation.GetProperty("lastAutoRestartAt").GetString());
    }

    [Fact]
    public void PlanAutoUpdates_DelegatesScheduledAutoUpdateSelection()
    {
        var service = new ServerAutomationService();
        var now = new DateTimeOffset(2026, 5, 24, 23, 0, 0, TimeSpan.Zero);

        var result = service.PlanAutoUpdates(new AutoUpdatePlanRequest(
            now,
            TimeSpan.FromMinutes(30),
            IsClosing: false,
            [
                new AutoUpdateServerCandidate("due", true, true, false, false, true, now.AddHours(-1), "2", ""),
                new AutoUpdateServerCandidate("disabled", true, true, false, false, false, now.AddHours(-1), "2", ""),
                new AutoUpdateServerCandidate("problem", true, false, false, false, true, now.AddHours(-1), "2", "")
            ]));

        var only = Assert.Single(result);
        Assert.Equal("due", only);
    }

    private static AutomationStartupCandidate Candidate(
        string id,
        bool configExists = true,
        bool canStart = true,
        bool manuallyStopped = false,
        bool configOpen = false,
        bool operationRunning = false,
        DateTimeOffset? lastCheck = null,
        bool autoStart = true,
        bool autoRestart = false)
    {
        return new AutomationStartupCandidate(
            id,
            $"Server {id}",
            configExists,
            canStart,
            manuallyStopped,
            configOpen,
            operationRunning,
            lastCheck,
            ServerAutomationSettings.Default with
            {
                AutoStart = autoStart,
                AutoRestart = autoRestart
            });
    }
}
