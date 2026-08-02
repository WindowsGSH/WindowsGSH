namespace WindowsGSH.Core.Discord;

public sealed record DiscordSlashCommandDefinition(
    string Name,
    string Description,
    bool HasServerOption = false,
    bool ServerRequired = false,
    bool HasCommandTextOption = false,
    bool HasConfirmationOption = false);

public sealed record DiscordSlashCommandRequest(
    bool IsValid,
    string Command,
    IReadOnlyList<string> Arguments,
    string? ErrorMessage)
{
    public static DiscordSlashCommandRequest FromOptions(
        string command,
        IReadOnlyDictionary<string, object?> options)
    {
        command = command.Trim().ToLowerInvariant();
        var definition = DiscordSlashCommands.Definitions.FirstOrDefault(item => item.Name == command);
        if (definition == null)
        {
            return new DiscordSlashCommandRequest(false, command, [], "Unknown WindowsGSH command.");
        }

        var arguments = new List<string>();
        if (definition.HasServerOption)
        {
            var serverId = GetString(options, "server");
            if (definition.ServerRequired && string.IsNullOrWhiteSpace(serverId))
            {
                return new DiscordSlashCommandRequest(false, command, [], "A server is required for this command.");
            }

            if (!string.IsNullOrWhiteSpace(serverId))
            {
                arguments.Add(serverId);
            }
        }

        if (definition.HasCommandTextOption)
        {
            var commandText = GetString(options, "command");
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return new DiscordSlashCommandRequest(false, command, arguments, "Command text is required.");
            }

            arguments.Add(commandText);
        }

        if (definition.HasConfirmationOption &&
            options.TryGetValue("confirm", out var confirmation) &&
            confirmation is true)
        {
            arguments.Add("confirm");
        }

        return new DiscordSlashCommandRequest(true, command, arguments, null);
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> options, string name)
    {
        return options.TryGetValue(name, out var value)
            ? value?.ToString()?.Trim()
            : null;
    }
}

public static class DiscordSlashCommands
{
    public const string RootName = "wgsh";

    public static IReadOnlyList<DiscordSlashCommandDefinition> Definitions { get; } =
    [
        new("help", "Show WindowsGSH Discord command help."),
        new("check", "Check your WindowsGSH command access.", HasServerOption: true),
        new("list", "List installed game servers."),
        new("status", "Show the status of one server.", HasServerOption: true, ServerRequired: true),
        new("stats", "Show a summary of all server states."),
        new("logs", "Show recent app or server log lines.", HasServerOption: true),
        new("start", "Start a server.", HasServerOption: true, ServerRequired: true),
        new("stop", "Stop a server.", HasServerOption: true, ServerRequired: true),
        new("restart", "Restart a server.", HasServerOption: true, ServerRequired: true),
        new("update", "Update an offline server.", HasServerOption: true, ServerRequired: true),
        new("backup", "Back up a server.", HasServerOption: true, ServerRequired: true),
        new("send", "Send a command through the server console.", HasServerOption: true, ServerRequired: true, HasCommandTextOption: true),
        new("sendr", "Send an RCON command and return its response.", HasServerOption: true, ServerRequired: true, HasCommandTextOption: true),
        new("stopall", "Stop every running server.", HasConfirmationOption: true)
    ];

    public static bool RunsInBackground(string command, IReadOnlyList<string> arguments)
    {
        if (string.Equals(command, "stopall", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Count > 0 &&
                   string.Equals(arguments[0], "confirm", StringComparison.OrdinalIgnoreCase);
        }

        return DiscordCommandPermissions.IsDestructiveCommand(command);
    }

    public static string GetAcceptedMessage(string command, IReadOnlyList<string> arguments)
    {
        var target = DiscordCommandPermissions.GetTargetServerId(command, arguments);
        return string.IsNullOrWhiteSpace(target)
            ? $"WindowsGSH accepted the `{command}` request. Follow its progress in WindowsGSH."
            : $"WindowsGSH accepted the `{command}` request for `{target}`. Follow its progress in WindowsGSH.";
    }
}

public sealed record DiscordServerAutocompleteItem(string Id, string Name);

public static class DiscordServerAutocomplete
{
    public const int MaximumResults = 25;

    public static IReadOnlyList<DiscordServerAutocompleteItem> Filter(
        IEnumerable<DiscordServerAutocompleteItem> servers,
        IEnumerable<string> allowedServerIds,
        string? searchText,
        int maximumResults = MaximumResults)
    {
        var allowed = allowedServerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 || maximumResults <= 0)
        {
            return [];
        }

        var allServersAllowed = allowed.Contains("0");
        var search = searchText?.Trim() ?? string.Empty;
        return servers
            .Where(server => allServersAllowed || allowed.Contains(server.Id))
            .Where(server =>
                search.Length == 0 ||
                server.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                server.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Min(maximumResults, MaximumResults))
            .ToArray();
    }
}
