namespace WindowsGSH.Core.Scheduling;

public sealed class ServerScheduleRunner : IServerScheduleRunner
{
    private readonly CronActionReservationService _reservations;
    private readonly Func<DateTimeOffset> _now;

    public ServerScheduleRunner(
        CronActionReservationService? reservations = null,
        Func<DateTimeOffset>? now = null)
    {
        _reservations = reservations ?? new CronActionReservationService();
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public ServerScheduleRunPlan PlanDueActions(ServerScheduleRunRequest request)
    {
        if (request.IsClosing)
        {
            return new ServerScheduleRunPlan([], []);
        }

        var now = request.Now ?? _now();
        var currentMinute = now.ToString("yyyyMMddHHmm");
        _reservations.BeginMinute(currentMinute);
        var actions = new List<ScheduledServerAction>();
        var logs = new List<ServerScheduleLogMessage>();

        foreach (var server in request.Servers.Where(server => server.ConfigExists))
        {
            AddDueActionSchedules(actions, logs, server, now, currentMinute);
            AddDueCommandSchedules(actions, logs, server, now, currentMinute, "rcon", "RCON", server.Schedules.RconCommands);
            AddDueCommandSchedules(actions, logs, server, now, currentMinute, "console", "console", server.Schedules.ConsoleCommands);
            AddDueApiSchedules(actions, logs, server, now, currentMinute, server.Schedules.ApiActions);
            AddDueExternalCommandSchedules(actions, logs, server, now, currentMinute, server.Schedules.ExternalCommands);
        }

        return new ServerScheduleRunPlan(actions, logs);
    }

    public DateTimeOffset? GetNextRun(string cronExpression, DateTimeOffset after)
    {
        var schedule = CronSchedule.Parse(cronExpression);
        var cursor = TruncateToMinute(after).AddMinutes(1);
        var limit = cursor.AddYears(1);
        while (cursor <= limit)
        {
            if (schedule.IsMatch(cursor))
            {
                return cursor;
            }

            cursor = cursor.AddMinutes(1);
        }

        return null;
    }

    private void AddDueActionSchedules(
        List<ScheduledServerAction> actions,
        List<ServerScheduleLogMessage> logs,
        ServerScheduleCandidate server,
        DateTimeOffset now,
        string currentMinute)
    {
        foreach (var (action, schedule) in new[]
        {
            ("start", server.Schedules.Start),
            ("stop", server.Schedules.Stop),
            ("update", server.Schedules.Update),
            ("backup", server.Schedules.Backup),
            ("restart", server.Schedules.Restart)
        })
        {
            if (!schedule.Enabled || string.IsNullOrWhiteSpace(schedule.Cron))
            {
                continue;
            }

            try
            {
                if (CronSchedule.Parse(schedule.Cron).IsMatch(now) &&
                    TryReserve(server, currentMinute, action, action, logs))
                {
                    actions.Add(new ScheduledServerAction(server.ServerId, action));
                }
            }
            catch (FormatException ex)
            {
                _reservations.MarkAttempted(currentMinute, server.ServerId, action);
                logs.Add(new ServerScheduleLogMessage(server.ServerId, $"{server.ServerName} {action} cron is invalid: {ex.Message}"));
            }
        }
    }

    private void AddDueCommandSchedules(
        List<ScheduledServerAction> actions,
        List<ServerScheduleLogMessage> logs,
        ServerScheduleCandidate server,
        DateTimeOffset now,
        string currentMinute,
        string action,
        string label,
        IReadOnlyList<ScheduledCommand> schedules)
    {
        for (var index = 0; index < schedules.Count; index++)
        {
            var commandSchedule = schedules[index];
            var displayIndex = index + 1;
            if (!commandSchedule.Enabled ||
                string.IsNullOrWhiteSpace(commandSchedule.Cron) ||
                string.IsNullOrWhiteSpace(commandSchedule.Command))
            {
                continue;
            }

            try
            {
                if (CronSchedule.Parse(commandSchedule.Cron).IsMatch(now) &&
                    TryReserve(server, currentMinute, $"{action}|{displayIndex}", $"{label} #{displayIndex}", logs))
                {
                    actions.Add(new ScheduledServerAction(server.ServerId, action, commandSchedule.Command));
                }
            }
            catch (FormatException ex)
            {
                _reservations.MarkAttempted(currentMinute, server.ServerId, $"{action}|{displayIndex}");
                logs.Add(new ServerScheduleLogMessage(server.ServerId, $"{server.ServerName} {label} cron #{displayIndex} is invalid: {ex.Message}"));
            }
        }
    }

    private void AddDueApiSchedules(
        List<ScheduledServerAction> actions,
        List<ServerScheduleLogMessage> logs,
        ServerScheduleCandidate server,
        DateTimeOffset now,
        string currentMinute,
        IReadOnlyList<ScheduledApiAction> schedules)
    {
        for (var index = 0; index < schedules.Count; index++)
        {
            var apiSchedule = schedules[index];
            var displayIndex = index + 1;
            if (!apiSchedule.Enabled ||
                string.IsNullOrWhiteSpace(apiSchedule.Cron) ||
                string.IsNullOrWhiteSpace(apiSchedule.ActionKey))
            {
                continue;
            }

            try
            {
                if (CronSchedule.Parse(apiSchedule.Cron).IsMatch(now) &&
                    TryReserve(server, currentMinute, $"api|{displayIndex}", $"API #{displayIndex}", logs))
                {
                    actions.Add(new ScheduledServerAction(server.ServerId, "api", apiSchedule.ActionKey, apiSchedule.ParametersJson));
                }
            }
            catch (FormatException ex)
            {
                _reservations.MarkAttempted(currentMinute, server.ServerId, $"api|{displayIndex}");
                logs.Add(new ServerScheduleLogMessage(server.ServerId, $"{server.ServerName} API cron #{displayIndex} is invalid: {ex.Message}"));
            }
        }
    }

    private void AddDueExternalCommandSchedules(
        List<ScheduledServerAction> actions,
        List<ServerScheduleLogMessage> logs,
        ServerScheduleCandidate server,
        DateTimeOffset now,
        string currentMinute,
        IReadOnlyList<ScheduledExternalCommand> schedules)
    {
        for (var index = 0; index < schedules.Count; index++)
        {
            var schedule = schedules[index];
            var displayIndex = index + 1;
            if (!schedule.Enabled ||
                string.IsNullOrWhiteSpace(schedule.Cron) ||
                string.IsNullOrWhiteSpace(schedule.ExecutablePath))
            {
                continue;
            }

            try
            {
                if (CronSchedule.Parse(schedule.Cron).IsMatch(now) &&
                    TryReserve(server, currentMinute, $"external|{displayIndex}", $"external command #{displayIndex}", logs))
                {
                    actions.Add(new ScheduledServerAction(
                        server.ServerId,
                        "external",
                        schedule.ExecutablePath,
                        schedule.Arguments,
                        schedule.WorkingDirectory,
                        schedule.TimeoutSeconds));
                }
            }
            catch (FormatException ex)
            {
                _reservations.MarkAttempted(currentMinute, server.ServerId, $"external|{displayIndex}");
                logs.Add(new ServerScheduleLogMessage(
                    server.ServerId,
                    $"{server.ServerName} external command cron #{displayIndex} is invalid: {ex.Message}"));
            }
        }
    }

    private bool TryReserve(
        ServerScheduleCandidate server,
        string currentMinute,
        string actionKey,
        string actionLabel,
        List<ServerScheduleLogMessage> logs)
    {
        var result = _reservations.TryReserve(new CronActionReservationRequest(
            currentMinute,
            server.ServerId,
            server.ServerName,
            actionKey,
            actionLabel,
            server.IsConfigOpen,
            server.CanStop,
            server.ActiveOperationDisplayText));
        if (!string.IsNullOrWhiteSpace(result.LogMessage))
        {
            logs.Add(new ServerScheduleLogMessage(server.ServerId, result.LogMessage));
        }

        return result.ShouldRun;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value)
    {
        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            0,
            value.Offset);
    }
}

