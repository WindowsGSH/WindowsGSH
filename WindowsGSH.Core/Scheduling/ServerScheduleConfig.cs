using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsGSH.Core.Scheduling;

public sealed record ServerScheduleConfig(
    CronActionSchedule Start,
    CronActionSchedule Stop,
    CronActionSchedule Update,
    CronActionSchedule Backup,
    CronActionSchedule Restart,
    IReadOnlyList<ScheduledCommand> RconCommands,
    IReadOnlyList<ScheduledCommand> ConsoleCommands,
    IReadOnlyList<ScheduledApiAction> ApiActions,
    IReadOnlyList<ScheduledExternalCommand> ExternalCommands)
{
    public static ServerScheduleConfig Empty { get; } = new(
        CronActionSchedule.Empty,
        CronActionSchedule.Empty,
        CronActionSchedule.Empty,
        CronActionSchedule.Empty,
        CronActionSchedule.Empty,
        [],
        [],
        [],
        []);

    public static ServerScheduleConfig FromConfigJson(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        if (!document.RootElement.TryGetProperty("schedules", out var schedules))
        {
            return Empty;
        }

        return FromSchedulesElement(schedules);
    }

    public static ServerScheduleConfig FromSchedulesJson(string schedulesJson)
    {
        using var document = JsonDocument.Parse(schedulesJson);
        return FromSchedulesElement(document.RootElement);
    }

    public static ServerScheduleConfig FromSchedulesElement(JsonElement schedules)
    {
        if (schedules.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new ServerScheduleConfig(
            ReadActionSchedule(schedules, "start"),
            ReadActionSchedule(schedules, "stop"),
            ReadActionSchedule(schedules, "update"),
            ReadActionSchedule(schedules, "backup"),
            ReadActionSchedule(schedules, "restart"),
            ReadCommandSchedules(schedules, "rconCommands"),
            ReadCommandSchedules(schedules, "consoleCommands"),
            ReadApiSchedules(schedules, "apiActions"),
            ReadExternalCommandSchedules(schedules, "externalCommands"));
    }

    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["start"] = Start.ToJsonObject(),
            ["stop"] = Stop.ToJsonObject(),
            ["update"] = Update.ToJsonObject(),
            ["backup"] = Backup.ToJsonObject(),
            ["restart"] = Restart.ToJsonObject(),
            ["rconCommands"] = ToCommandArray(RconCommands),
            ["consoleCommands"] = ToCommandArray(ConsoleCommands),
            ["apiActions"] = ToApiArray(ApiActions),
            ["externalCommands"] = ToExternalCommandArray(ExternalCommands)
        };
    }

    private static CronActionSchedule ReadActionSchedule(JsonElement schedules, string key)
    {
        return schedules.TryGetProperty(key, out var schedule) && schedule.ValueKind == JsonValueKind.Object
            ? new CronActionSchedule(ReadBoolean(schedule, "enabled"), ReadString(schedule, "cron"))
            : CronActionSchedule.Empty;
    }

    private static IReadOnlyList<ScheduledCommand> ReadCommandSchedules(JsonElement schedules, string key)
    {
        if (!schedules.TryGetProperty(key, out var commands) || commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return commands
            .EnumerateArray()
            .Where(command => command.ValueKind == JsonValueKind.Object)
            .Select(command => new ScheduledCommand(
                ReadBoolean(command, "enabled"),
                ReadString(command, "cron"),
                ReadString(command, "command")))
            .ToArray();
    }

    private static IReadOnlyList<ScheduledApiAction> ReadApiSchedules(JsonElement schedules, string key)
    {
        if (!schedules.TryGetProperty(key, out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return actions
            .EnumerateArray()
            .Where(action => action.ValueKind == JsonValueKind.Object)
            .Select(action => new ScheduledApiAction(
                ReadBoolean(action, "enabled"),
                ReadString(action, "cron"),
                ReadString(action, "action"),
                action.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                    ? parameters.GetRawText()
                    : "{}"))
            .ToArray();
    }

    private static IReadOnlyList<ScheduledExternalCommand> ReadExternalCommandSchedules(JsonElement schedules, string key)
    {
        if (!schedules.TryGetProperty(key, out var commands) || commands.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return commands
            .EnumerateArray()
            .Where(command => command.ValueKind == JsonValueKind.Object)
            .Select(command => new ScheduledExternalCommand(
                ReadBoolean(command, "enabled"),
                ReadString(command, "cron"),
                ReadString(command, "executable"),
                ReadString(command, "arguments"),
                ReadString(command, "workingDirectory"),
                ReadInt32(command, "timeoutSeconds", 300)))
            .ToArray();
    }

    private static JsonArray ToCommandArray(IEnumerable<ScheduledCommand> commands)
    {
        var array = new JsonArray();
        foreach (var command in commands)
        {
            array.Add(command.ToJsonObject());
        }

        return array;
    }

    private static JsonArray ToApiArray(IEnumerable<ScheduledApiAction> actions)
    {
        var array = new JsonArray();
        foreach (var action in actions)
        {
            array.Add(action.ToJsonObject());
        }

        return array;
    }

    private static JsonArray ToExternalCommandArray(IEnumerable<ScheduledExternalCommand> commands)
    {
        var array = new JsonArray();
        foreach (var command in commands)
        {
            array.Add(command.ToJsonObject());
        }

        return array;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.True;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt32(JsonElement element, string propertyName, int defaultValue)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
            ? value
            : defaultValue;
    }
}

public sealed record CronActionSchedule(bool Enabled, string Cron)
{
    public static CronActionSchedule Empty { get; } = new(Enabled: false, Cron: string.Empty);

    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["cron"] = Cron
        };
    }
}

public sealed record ScheduledCommand(bool Enabled, string Cron, string Command)
{
    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["cron"] = Cron,
            ["command"] = Command
        };
    }
}

public sealed record ScheduledApiAction(bool Enabled, string Cron, string ActionKey, string ParametersJson)
{
    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["cron"] = Cron,
            ["action"] = ActionKey,
            ["parameters"] = ParseParameters(ParametersJson)
        };
    }

    private static JsonObject ParseParameters(string parametersJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(parametersJson))
            {
                return [];
            }

            return JsonNode.Parse(parametersJson) is JsonObject parameters
                ? parameters
                : throw new InvalidOperationException("Scheduled API action parameters must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Scheduled API action parameters must be a JSON object: {ex.Message}");
        }
    }
}

public sealed record ScheduledExternalCommand(
    bool Enabled,
    string Cron,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    int TimeoutSeconds)
{
    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["cron"] = Cron,
            ["executable"] = ExecutablePath,
            ["arguments"] = Arguments,
            ["workingDirectory"] = WorkingDirectory,
            ["timeoutSeconds"] = TimeoutSeconds
        };
    }
}
