namespace WindowsGSH.Core.Discord;

public sealed record DiscordCommandParseResult(
    bool IsValid,
    string Command,
    IReadOnlyList<string> Arguments,
    string? ErrorMessage)
{
    public static DiscordCommandParseResult Success(string command, IReadOnlyList<string> arguments)
    {
        return new DiscordCommandParseResult(true, command, arguments, null);
    }

    public static DiscordCommandParseResult Error(string message)
    {
        return new DiscordCommandParseResult(false, string.Empty, [], message);
    }
}

public static class DiscordCommandParser
{
    public static DiscordCommandParseResult Parse(string value)
    {
        var parts = Split(value);
        if (!parts.IsValid)
        {
            return DiscordCommandParseResult.Error(parts.ErrorMessage ?? "Command could not be parsed.");
        }

        var command = parts.Arguments.Count == 0 ? "help" : parts.Arguments[0].ToLowerInvariant();
        var arguments = parts.Arguments.Skip(1).ToArray();
        return DiscordCommandParseResult.Success(command, arguments);
    }

    private static DiscordCommandParseResult Split(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DiscordCommandParseResult.Success("help", []);
        }

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var escaping = false;

        foreach (var character in value)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }

            if (character == '\\' && inQuotes)
            {
                escaping = true;
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                AddCurrentPart(parts, current);
                continue;
            }

            current.Append(character);
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (inQuotes)
        {
            return DiscordCommandParseResult.Error("Command has an unclosed quote.");
        }

        AddCurrentPart(parts, current);
        return DiscordCommandParseResult.Success(parts.Count == 0 ? "help" : parts[0], parts);
    }

    private static void AddCurrentPart(List<string> parts, System.Text.StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        parts.Add(current.ToString());
        current.Clear();
    }
}
