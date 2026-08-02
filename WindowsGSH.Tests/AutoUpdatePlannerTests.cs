using WindowsGSH.Core.Scheduling;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class AutoUpdatePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 16, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_skips_when_interval_disabled()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(interval: TimeSpan.Zero),
            Candidate());

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.IntervalDisabled, result.SkipReason);
    }

    [Fact]
    public void Evaluate_skips_when_app_is_closing()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(isClosing: true),
            Candidate());

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.AppClosing, result.SkipReason);
    }

    [Fact]
    public void Evaluate_skips_running_server()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(canStop: true));

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.ServerRunning, result.SkipReason);
    }

    [Fact]
    public void Evaluate_skips_problem_server_without_a_usable_module()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(hasUsableModule: false));

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.ModuleUnavailable, result.SkipReason);
    }

    [Fact]
    public void Evaluate_skips_active_operation()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(hasActiveOperation: true));

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.ActiveOperation, result.SkipReason);
    }

    [Fact]
    public void Evaluate_skips_when_auto_update_disabled()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(autoUpdateEnabled: false));

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.AutoUpdateDisabled, result.SkipReason);
    }

    [Fact]
    public void Evaluate_runs_when_no_previous_check_exists()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(lastCheckUtc: null));

        Assert.True(result.ShouldRun);
    }

    [Fact]
    public void Evaluate_skips_recent_check()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(interval: TimeSpan.FromMinutes(30)),
            Candidate(lastCheckUtc: Now.AddMinutes(-10)));

        Assert.False(result.ShouldRun);
        Assert.Equal(AutoUpdateSkipReason.CheckedRecently, result.SkipReason);
    }

    [Fact]
    public void Evaluate_runs_old_check()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(interval: TimeSpan.FromMinutes(30)),
            Candidate(lastCheckUtc: Now.AddMinutes(-31)));

        Assert.True(result.ShouldRun);
    }

    [Fact]
    public void Evaluate_runs_ignored_remote_build_so_newer_builds_can_be_discovered()
    {
        var result = new AutoUpdatePlanner().Evaluate(
            Request(),
            Candidate(remoteBuildId: "12345", ignoredBuildId: "12345"));

        Assert.True(result.ShouldRun);
    }

    [Fact]
    public void Plan_returns_only_due_servers()
    {
        var due = Candidate(serverId: "due");
        var recent = Candidate(serverId: "recent", lastCheckUtc: Now.AddMinutes(-2));

        var result = new AutoUpdatePlanner().Plan(Request(servers: [due, recent]));

        var item = Assert.Single(result);
        Assert.Equal("due", item.Server.ServerId);
    }

    private static AutoUpdatePlanRequest Request(
        TimeSpan? interval = null,
        bool isClosing = false,
        IReadOnlyList<AutoUpdateServerCandidate>? servers = null)
    {
        return new AutoUpdatePlanRequest(
            Now,
            interval ?? TimeSpan.FromMinutes(10),
            isClosing,
            servers ?? []);
    }

    private static AutoUpdateServerCandidate Candidate(
        string serverId = "server-1",
        bool configExists = true,
        bool hasUsableModule = true,
        bool canStop = false,
        bool hasActiveOperation = false,
        bool autoUpdateEnabled = true,
        DateTimeOffset? lastCheckUtc = null,
        string remoteBuildId = "",
        string ignoredBuildId = "")
    {
        return new AutoUpdateServerCandidate(
            serverId,
            configExists,
            hasUsableModule,
            canStop,
            hasActiveOperation,
            autoUpdateEnabled,
            lastCheckUtc,
            remoteBuildId,
            ignoredBuildId);
    }
}
