namespace WindowsGSH.Core.Discord;

public static class DiscordCommandPermissions
{
    public const string RemoteControlDisabledMessage = "Discord remote control is disabled in WindowsGSH settings.";

    public static bool HasPermission(IEnumerable<string> allowedServerIds, string command, string? targetServerId)
    {
        var serverIds = allowedServerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (serverIds.Length == 0)
        {
            return false;
        }

        if (serverIds.Contains("0", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        command = command.ToLowerInvariant();
        return command switch
        {
            "help" or "check" or "stats" or "list" => true,
            "stopall" => false,
            "logs" => !string.IsNullOrWhiteSpace(targetServerId) && HasServerAccess(serverIds, targetServerId),
            "status" or "start" or "stop" or "restart" or "update" or "backup" or "send" or "sendr" =>
                !string.IsNullOrWhiteSpace(targetServerId) && HasServerAccess(serverIds, targetServerId),
            _ => false
        };
    }

    public static bool IsDestructiveCommand(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "start" or "stop" or "restart" or "update" or "backup" or "send" or "sendr" or "stopall" => true,
            _ => false
        };
    }

    public static bool IsAllowedByRemoteControlSetting(string command, bool allowDestructiveCommands)
    {
        return allowDestructiveCommands || !IsDestructiveCommand(command);
    }

    public static string? GetTargetServerId(string command, IReadOnlyList<string> arguments)
    {
        command = command.ToLowerInvariant();
        return command switch
        {
            "logs" or "status" or "start" or "stop" or "restart" or "update" or "backup" or "send" or "sendr"
                when arguments.Count > 0 => arguments[0],
            _ => null
        };
    }

    private static bool HasServerAccess(IEnumerable<string> serverIds, string targetServerId)
    {
        return serverIds.Contains(targetServerId, StringComparer.OrdinalIgnoreCase);
    }
}
