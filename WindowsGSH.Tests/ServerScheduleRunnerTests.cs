using WindowsGSH.Core.Scheduling;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerScheduleRunnerTests
{
    [Fact]
    public void PlanDueActions_ReturnsDueActionsForAllSupportedScheduleTypes()
    {
        var now = LocalTime(2026, 5, 24, 12, 30);
        var runner = new ServerScheduleRunner(now: () => now);

        var plan = runner.PlanDueActions(new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(
                start: "30 12 * * *",
                stop: "30 12 * * *",
                update: "30 12 * * *",
                backup: "30 12 * * *",
                restart: "30 12 * * *",
                rcon: [new ScheduledCommand(true, "30 12 * * *", "say hi")],
                console: [new ScheduledCommand(true, "30 12 * * *", "save-all")],
                api: [new ScheduledApiAction(true, "30 12 * * *", "broadcast", """{ "message": "hi" }""")],
                external:
                [
                    new ScheduledExternalCommand(
                        true,
                        "30 12 * * *",
                        @"C:\Tools\sync.exe",
                        "--sync",
                        @"C:\Tools",
                        60)
                ]))]));

        Assert.Equal(
            ["start", "stop", "update", "backup", "restart", "rcon", "console", "api", "external"],
            plan.DueActions.Select(action => action.Action));
        Assert.Contains(plan.DueActions, action => action.Action == "rcon" && action.Command == "say hi");
        Assert.Contains(plan.DueActions, action => action.Action == "api" && action.Command == "broadcast" && action.ParametersJson!.Contains("message"));
        Assert.Contains(plan.DueActions, action =>
            action.Action == "external" &&
            action.Command == @"C:\Tools\sync.exe" &&
            action.ParametersJson == "--sync" &&
            action.TimeoutSeconds == 60);
    }

    [Fact]
    public void PlanDueActions_LogsInvalidCronAndDoesNotRunItAgainInSameMinute()
    {
        var now = LocalTime(2026, 5, 24, 12, 30);
        var runner = new ServerScheduleRunner(now: () => now);
        var request = new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(start: "bad cron"))]);

        var first = runner.PlanDueActions(request);
        var second = runner.PlanDueActions(request);

        Assert.Empty(first.DueActions);
        var log = Assert.Single(first.LogMessages);
        Assert.Contains("start cron is invalid", log.Message);
        Assert.Empty(second.DueActions);
        Assert.Single(second.LogMessages);
    }

    [Fact]
    public void PlanDueActions_ReservationPreventsDuplicateRunsInSameMinute()
    {
        var now = LocalTime(2026, 5, 24, 12, 30);
        var runner = new ServerScheduleRunner(now: () => now);
        var request = new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(start: "30 12 * * *"))]);

        var first = runner.PlanDueActions(request);
        var second = runner.PlanDueActions(request);

        Assert.Single(first.DueActions);
        Assert.Empty(second.DueActions);
    }

    [Fact]
    public void PlanDueActions_SkipsDisabledRconSchedule()
    {
        var now = LocalTime(2026, 5, 24, 12, 30);
        var runner = new ServerScheduleRunner(now: () => now);

        var plan = runner.PlanDueActions(new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(rcon: [new ScheduledCommand(false, "30 12 * * *", "say hi")]))]));

        Assert.Empty(plan.DueActions);
    }

    [Fact]
    public void PlanDueActions_PreservesOpenConfigWindowSkip()
    {
        var now = LocalTime(2026, 5, 24, 12, 30);
        var runner = new ServerScheduleRunner(now: () => now);

        var plan = runner.PlanDueActions(new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(start: "30 12 * * *"), configOpen: true, canStop: false)]));

        Assert.Empty(plan.DueActions);
        var log = Assert.Single(plan.LogMessages);
        Assert.Contains("CFG window is open", log.Message);
    }

    [Fact]
    public void GetNextRun_ReturnsNextMatchingMinute()
    {
        var runner = new ServerScheduleRunner();
        var after = LocalTime(2026, 5, 24, 12, 29, 10);

        var next = runner.GetNextRun("30 12 * * *", after);

        Assert.Equal(LocalTime(2026, 5, 24, 12, 30), next);
    }

    [Fact]
    public void PlanDueActions_UsesInjectedClockAtMinuteBoundary()
    {
        var now = LocalTime(2026, 5, 24, 0, 0);
        var runner = new ServerScheduleRunner(now: () => now);

        var plan = runner.PlanDueActions(new ServerScheduleRunRequest(
            IsClosing: false,
            [Candidate("1", Schedules(backup: "0 0 * * *"))]));

        var action = Assert.Single(plan.DueActions);
        Assert.Equal("backup", action.Action);
    }

    private static ServerScheduleCandidate Candidate(
        string id,
        ServerScheduleConfig schedules,
        bool configExists = true,
        bool configOpen = false,
        bool canStop = false,
        string? activeOperation = null)
    {
        return new ServerScheduleCandidate(
            id,
            $"Server {id}",
            configExists,
            schedules,
            configOpen,
            canStop,
            activeOperation);
    }

    private static ServerScheduleConfig Schedules(
        string? start = null,
        string? stop = null,
        string? update = null,
        string? backup = null,
        string? restart = null,
        IReadOnlyList<ScheduledCommand>? rcon = null,
        IReadOnlyList<ScheduledCommand>? console = null,
        IReadOnlyList<ScheduledApiAction>? api = null,
        IReadOnlyList<ScheduledExternalCommand>? external = null)
    {
        return new ServerScheduleConfig(
            ToSchedule(start),
            ToSchedule(stop),
            ToSchedule(update),
            ToSchedule(backup),
            ToSchedule(restart),
            rcon ?? [],
            console ?? [],
            api ?? [],
            external ?? []);
    }

    private static CronActionSchedule ToSchedule(string? cron)
    {
        return string.IsNullOrWhiteSpace(cron)
            ? CronActionSchedule.Empty
            : new CronActionSchedule(true, cron);
    }

    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute, int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
