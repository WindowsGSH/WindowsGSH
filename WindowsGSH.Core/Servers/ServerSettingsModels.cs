using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.Scheduling;
using static WindowsGSH.Core.Servers.ServerSettingsJson;

namespace WindowsGSH.Core.Servers;

public sealed record ServerConfigAppSettings(
    ServerAutomationSettings Automation,
    ServerScheduleConfig Schedules,
    ServerBackupSettings Backup,
    ServerDiscordSettings Discord,
    ServerRuntimeSettings Runtime,
    ServerJavaSettings Java,
    ServerNetworkSettings Network)
{
    public static ServerConfigAppSettings Empty { get; } = new(
        ServerAutomationSettings.Default,
        ServerScheduleConfig.Empty,
        ServerBackupSettings.Default,
        ServerDiscordSettings.Default,
        ServerRuntimeSettings.Default,
        ServerJavaSettings.Default,
        ServerNetworkSettings.Default);

    public static ServerConfigAppSettings FromConfigJson(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        return FromRootElement(document.RootElement);
    }

    public static ServerConfigAppSettings FromRootElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var schedules = root.TryGetProperty("schedules", out var schedulesElement)
            ? ServerScheduleConfig.FromSchedulesElement(schedulesElement)
            : ServerScheduleConfig.Empty;

        return new ServerConfigAppSettings(
            ServerAutomationSettings.FromRootElement(root),
            schedules,
            ServerBackupSettings.FromRootElement(root),
            ServerDiscordSettings.FromRootElement(root),
            ServerRuntimeSettings.FromRootElement(root),
            ServerJavaSettings.FromRootElement(root),
            ServerNetworkSettings.FromRootElement(root));
    }

    public void ApplyTo(JsonObject root)
    {
        root["automation"] = Automation.ToJsonObject();
        root["schedules"] = Schedules.ToJsonObject();
        root["backup"] = (Backup with { BackupOnStart = Automation.BackupOnStart }).ToJsonObject();
        root["discord"] = Discord.ToJsonObject();
        root["runtimeSettings"] = Runtime.ToJsonObject();
        if (root.ContainsKey("java") || Java != ServerJavaSettings.Default)
        {
            root["java"] = Java.ToJsonObject();
        }
        root["network"] = Network.ToJsonObject();
    }
}

public sealed record ServerAutomationSettings(
    bool AutoStart,
    bool AutoRestart,
    bool AutoUpdate,
    bool UpdateOnStart,
    bool BackupOnStart,
    bool RestartOnCrash,
    int RestartDelaySeconds,
    int MaxRestartAttempts,
    DateTimeOffset? LastAutoRestartAt,
    DateTimeOffset? LastCrashDetectedAt)
{
    public static ServerAutomationSettings Default { get; } = new(
        AutoStart: false,
        AutoRestart: false,
        AutoUpdate: false,
        UpdateOnStart: false,
        BackupOnStart: false,
        RestartOnCrash: false,
        RestartDelaySeconds: 30,
        MaxRestartAttempts: 3,
        LastAutoRestartAt: null,
        LastCrashDetectedAt: null);

    public static ServerAutomationSettings FromRootElement(JsonElement root)
    {
        var automation = ReadObject(root, "automation");
        var backup = ReadObject(root, "backup");
        return new ServerAutomationSettings(
            ReadBoolean(automation, "autoStart", Default.AutoStart),
            ReadBoolean(automation, "autoRestart", Default.AutoRestart),
            ReadBoolean(automation, "autoUpdate", Default.AutoUpdate),
            ReadBoolean(automation, "updateOnStart", Default.UpdateOnStart),
            ReadBoolean(backup, "backupOnStart", Default.BackupOnStart),
            ReadBoolean(automation, "restartOnCrash", Default.RestartOnCrash),
            ReadInt(automation, "restartDelaySeconds", Default.RestartDelaySeconds),
            ReadInt(automation, "maxRestartAttempts", Default.MaxRestartAttempts),
            ReadDateTimeOffset(automation, "lastAutoRestartAt"),
            ReadDateTimeOffset(automation, "lastCrashDetectedAt"));
    }

    public JsonObject ToJsonObject()
    {
        var automation = new JsonObject
        {
            ["autoStart"] = AutoStart,
            ["autoRestart"] = AutoRestart,
            ["autoUpdate"] = AutoUpdate,
            ["updateOnStart"] = UpdateOnStart,
            ["restartOnCrash"] = RestartOnCrash,
            ["restartDelaySeconds"] = RestartDelaySeconds,
            ["maxRestartAttempts"] = MaxRestartAttempts
        };
        WriteDateTimeOffset(automation, "lastAutoRestartAt", LastAutoRestartAt);
        WriteDateTimeOffset(automation, "lastCrashDetectedAt", LastCrashDetectedAt);
        return automation;
    }
}

public sealed record ServerBackupSettings(
    bool BackupOnStart,
    IReadOnlyList<string> Paths,
    string Destination)
{
    public static ServerBackupSettings Default { get; } = new(BackupOnStart: false, Paths: [], Destination: string.Empty);

    public static ServerBackupSettings FromRootElement(JsonElement root)
    {
        var backup = ReadObject(root, "backup");
        return new ServerBackupSettings(
            ReadBoolean(backup, "backupOnStart", Default.BackupOnStart),
            ReadStringArray(backup, "paths"),
            ReadString(backup, "destination", Default.Destination));
    }

    public JsonObject ToJsonObject()
    {
        var paths = new JsonArray();
        foreach (var path in Paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            paths.Add(path);
        }

        return new JsonObject
        {
            ["backupOnStart"] = BackupOnStart,
            ["paths"] = paths,
            ["destination"] = Destination
        };
    }
}

public sealed record ServerDiscordSettings(
    string Channel,
    bool IncludeOnDashboard,
    bool UseWebhookOverride,
    bool ShowServerAddress,
    bool ShowPlayerCount,
    bool ShowUptime,
    bool ShowResourceUsage,
    bool ShowMap,
    bool ShowGameVersion,
    bool ShowQuerySummary,
    bool ShowPlayerList,
    bool ShowQueryDiagnostics)
{
    public static ServerDiscordSettings Default { get; } = new(
        string.Empty, true, false,
        true, true, true, true, true, true, true, true, true);

    public static ServerDiscordSettings FromRootElement(JsonElement root)
    {
        var discord = ReadObject(root, "discord");
        return new ServerDiscordSettings(
            ReadString(discord, "channel", Default.Channel),
            ReadBoolean(discord, "includeOnDashboard", Default.IncludeOnDashboard),
            ReadBoolean(discord, "useWebhookOverride", Default.UseWebhookOverride),
            ReadBoolean(discord, "showServerAddress", Default.ShowServerAddress),
            ReadBoolean(discord, "showPlayerCount", Default.ShowPlayerCount),
            ReadBoolean(discord, "showUptime", Default.ShowUptime),
            ReadBoolean(discord, "showResourceUsage", Default.ShowResourceUsage),
            ReadBoolean(discord, "showMap", Default.ShowMap),
            ReadBoolean(discord, "showGameVersion", Default.ShowGameVersion),
            ReadBoolean(discord, "showQuerySummary", Default.ShowQuerySummary),
            ReadBoolean(discord, "showPlayerList", Default.ShowPlayerList),
            ReadBoolean(discord, "showQueryDiagnostics", Default.ShowQueryDiagnostics));
    }

    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["channel"] = Channel,
            ["includeOnDashboard"] = IncludeOnDashboard,
            ["useWebhookOverride"] = UseWebhookOverride,
            ["showServerAddress"] = ShowServerAddress,
            ["showPlayerCount"] = ShowPlayerCount,
            ["showUptime"] = ShowUptime,
            ["showResourceUsage"] = ShowResourceUsage,
            ["showMap"] = ShowMap,
            ["showGameVersion"] = ShowGameVersion,
            ["showQuerySummary"] = ShowQuerySummary,
            ["showPlayerList"] = ShowPlayerList,
            ["showQueryDiagnostics"] = ShowQueryDiagnostics
        };
    }
}

public sealed record ServerRuntimeSettings(
    string ArgumentsOverride,
    string ProcessPriorityClass,
    string CpuAffinityMask,
    bool ApplyPriorityOnStart,
    bool ApplyAffinityOnStart,
    bool AllowEmbeddedConsole,
    bool ShowExternalConsole,
    string WorkingDirectoryOverride)
{
    public static ServerRuntimeSettings Default { get; } = new(
        ArgumentsOverride: string.Empty,
        ProcessPriorityClass: string.Empty,
        CpuAffinityMask: string.Empty,
        ApplyPriorityOnStart: false,
        ApplyAffinityOnStart: false,
        AllowEmbeddedConsole: false,
        ShowExternalConsole: false,
        WorkingDirectoryOverride: string.Empty);

    public static ServerRuntimeSettings FromRootElement(JsonElement root)
    {
        var runtime = ReadObject(root, "runtimeSettings");
        return new ServerRuntimeSettings(
            ReadString(runtime, "argumentsOverride", Default.ArgumentsOverride),
            ReadString(runtime, "processPriorityClass", Default.ProcessPriorityClass),
            ReadString(runtime, "cpuAffinityMask", Default.CpuAffinityMask),
            ReadBoolean(runtime, "applyPriorityOnStart", Default.ApplyPriorityOnStart),
            ReadBoolean(runtime, "applyAffinityOnStart", Default.ApplyAffinityOnStart),
            ReadBoolean(runtime, "allowEmbeddedConsole", Default.AllowEmbeddedConsole),
            ReadBoolean(runtime, "showExternalConsole", Default.ShowExternalConsole),
            ReadString(runtime, "workingDirectoryOverride", Default.WorkingDirectoryOverride));
    }

    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["argumentsOverride"] = ArgumentsOverride,
            ["processPriorityClass"] = ProcessPriorityClass,
            ["cpuAffinityMask"] = CpuAffinityMask,
            ["applyPriorityOnStart"] = ApplyPriorityOnStart,
            ["applyAffinityOnStart"] = ApplyAffinityOnStart,
            ["allowEmbeddedConsole"] = AllowEmbeddedConsole,
            ["showExternalConsole"] = ShowExternalConsole,
            ["workingDirectoryOverride"] = WorkingDirectoryOverride
        };
    }
}

public sealed record ServerJavaSettings(
    string RuntimePath,
    string ManagedRuntimeId,
    string MemoryPreset,
    int InitialMemoryMb,
    int MaximumMemoryMb,
    string AdditionalJvmArguments)
{
    public const string CustomPreset = "Custom";

    public static ServerJavaSettings Default { get; } = new(
        RuntimePath: string.Empty,
        ManagedRuntimeId: string.Empty,
        MemoryPreset: "4 GB",
        InitialMemoryMb: 1024,
        MaximumMemoryMb: 4096,
        AdditionalJvmArguments: string.Empty);

    public static ServerJavaSettings FromRootElement(JsonElement root)
    {
        var java = ReadObject(root, "java");
        var legacySettings = ReadObject(root, "settings");
        var runtimePath = ReadString(java, "runtimePath", string.Empty);
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            runtimePath = ReadString(legacySettings, "java.path", Default.RuntimePath);
        }

        var managedRuntimeId = ReadString(java, "managedRuntimeId", string.Empty);

        var initialMemoryMb = ReadInt(java, "initialMemoryMb", 0);
        if (initialMemoryMb <= 0)
        {
            initialMemoryMb = ParseLegacyMemoryMb(ReadString(legacySettings, "java.xms", string.Empty), Default.InitialMemoryMb);
        }

        var maximumMemoryMb = ReadInt(java, "maximumMemoryMb", 0);
        if (maximumMemoryMb <= 0)
        {
            maximumMemoryMb = ParseLegacyMemoryMb(ReadString(legacySettings, "java.xmx", string.Empty), Default.MaximumMemoryMb);
        }

        var preset = ReadString(java, "memoryPreset", string.Empty);
        if (string.IsNullOrWhiteSpace(preset))
        {
            preset = GetPreset(initialMemoryMb, maximumMemoryMb);
        }

        var additionalJvmArguments = ReadString(java, "additionalJvmArguments", string.Empty);
        if (string.IsNullOrWhiteSpace(additionalJvmArguments))
        {
            additionalJvmArguments = ReadString(legacySettings, "server.additionalJvmArgs", Default.AdditionalJvmArguments);
        }

        return new ServerJavaSettings(runtimePath, managedRuntimeId, preset, initialMemoryMb, maximumMemoryMb, additionalJvmArguments);
    }

    public JsonObject ToJsonObject()
    {
        var obj = new JsonObject
        {
            ["runtimePath"] = RuntimePath,
            ["memoryPreset"] = MemoryPreset,
            ["initialMemoryMb"] = InitialMemoryMb,
            ["maximumMemoryMb"] = MaximumMemoryMb,
            ["additionalJvmArguments"] = AdditionalJvmArguments
        };

        if (!string.IsNullOrWhiteSpace(ManagedRuntimeId))
        {
            obj["managedRuntimeId"] = ManagedRuntimeId;
        }

        return obj;
    }

    public static string GetPreset(int initialMemoryMb, int maximumMemoryMb)
    {
        return (initialMemoryMb, maximumMemoryMb) switch
        {
            (1024, 2048) => "2 GB",
            (1024, 4096) => "4 GB",
            (2048, 8192) => "8 GB",
            (4096, 16384) => "16 GB",
            _ => CustomPreset
        };
    }

    private static int ParseLegacyMemoryMb(string value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        var multiplier = 1;
        if (trimmed.EndsWith("G", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^1];
        }

        return int.TryParse(trimmed, out var amount) && amount > 0
            ? checked(amount * multiplier)
            : fallback;
    }
}

// Manual (default): WindowsGSH never calls AddPortMapping/DeletePortMapping for this server, only
// ever shows manual instructions (Tier 5.5). MapOnStart: create mappings for the server's
// openExternally ports when it starts, leave them in place after it stops. MapOnStartRemoveOnStop:
// same, but also remove them again on a successful stop. There is deliberately no UI to set this
// yet (Tier 5.4d scope decision) - every existing server keeps reading Manual until one exists.
public enum UpnpMappingPolicy
{
    Manual,
    MapOnStart,
    MapOnStartRemoveOnStop
}

public sealed record ServerNetworkSettings(
    string LastKnownPublicIp,
    DateTimeOffset? LastPublicIpCheckedAt,
    UpnpMappingPolicy UpnpMappingPolicy)
{
    public static ServerNetworkSettings Default { get; } = new(
        LastKnownPublicIp: string.Empty,
        LastPublicIpCheckedAt: null,
        UpnpMappingPolicy: UpnpMappingPolicy.Manual);

    public static ServerNetworkSettings FromRootElement(JsonElement root)
    {
        var network = ReadObject(root, "network");
        return new ServerNetworkSettings(
            ReadString(network, "lastKnownPublicIp", Default.LastKnownPublicIp),
            ReadDateTimeOffset(network, "lastPublicIpCheckedAt"),
            ReadUpnpMappingPolicy(network));
    }

    public JsonObject ToJsonObject()
    {
        var network = new JsonObject
        {
            ["lastKnownPublicIp"] = LastKnownPublicIp,
            ["upnpMappingPolicy"] = UpnpMappingPolicy.ToString()
        };
        WriteDateTimeOffset(network, "lastPublicIpCheckedAt", LastPublicIpCheckedAt);
        return network;
    }

    private static UpnpMappingPolicy ReadUpnpMappingPolicy(JsonElement network)
    {
        var text = ReadString(network, "upnpMappingPolicy", string.Empty);
        return Enum.TryParse<UpnpMappingPolicy>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : Default.UpnpMappingPolicy;
    }
}

public sealed record ServerModuleSettings(IReadOnlyDictionary<string, object?> Values)
{
    public static ServerModuleSettings Empty { get; } = new(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    public static ServerModuleSettings FromSettingsElement(JsonElement settingsElement)
    {
        if (settingsElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        return new ServerModuleSettings(ReadObjectSettings(settingsElement));
    }
}

internal static class ServerSettingsJson
{
    public static JsonElement ReadObject(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    public static Dictionary<string, object?> ReadObjectSettings(JsonElement settingsElement)
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (settingsElement.ValueKind != JsonValueKind.Object)
        {
            return settings;
        }

        foreach (var property in settingsElement.EnumerateObject())
        {
            settings[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number => property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.ToString()
            };
        }

        return settings;
    }

    public static string ReadString(JsonElement element, string propertyName, string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    public static bool ReadBoolean(JsonElement element, string propertyName, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    public static int ReadInt(JsonElement element, string propertyName, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    public static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    public static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    public static void WriteDateTimeOffset(JsonObject parent, string propertyName, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            parent[propertyName] = value.Value.ToUniversalTime().ToString("O");
        }
    }
}
