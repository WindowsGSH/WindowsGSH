namespace WindowsGSH.Core.Scheduling;

public interface IServerScheduleRunner
{
    ServerScheduleRunPlan PlanDueActions(ServerScheduleRunRequest request);

    DateTimeOffset? GetNextRun(string cronExpression, DateTimeOffset after);
}

public sealed record ServerScheduleRunRequest(
    bool IsClosing,
    IReadOnlyList<ServerScheduleCandidate> Servers,
    DateTimeOffset? Now = null);

public sealed record ServerScheduleCandidate(
    string ServerId,
    string ServerName,
    bool ConfigExists,
    ServerScheduleConfig Schedules,
    bool IsConfigOpen,
    bool CanStop,
    string? ActiveOperationDisplayText);

public sealed record ServerScheduleRunPlan(
    IReadOnlyList<ScheduledServerAction> DueActions,
    IReadOnlyList<ServerScheduleLogMessage> LogMessages);

public sealed record ScheduledServerAction(
    string ServerId,
    string Action,
    string? Command = null,
    string? ParametersJson = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 0);

public sealed record ServerScheduleLogMessage(
    string ServerId,
    string Message);

