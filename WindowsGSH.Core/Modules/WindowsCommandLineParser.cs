namespace WindowsGSH.Core.Modules;

/// <summary>
/// Splits a pre-composed Windows command line into argument tokens. This is used only for fields
/// whose documented type is an entire command line, not for ordinary text fields.
/// </summary>
public static class WindowsCommandLineParser
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var arguments = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }

            if (index == commandLine.Length)
            {
                break;
            }

            var argument = new System.Text.StringBuilder();
            var inQuotes = false;
            while (index < commandLine.Length && (inQuotes || !char.IsWhiteSpace(commandLine[index])))
            {
                var backslashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    argument.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        argument.Append('"');
                    }

                    index++;
                    continue;
                }

                argument.Append('\\', backslashes);
                if (index < commandLine.Length)
                {
                    argument.Append(commandLine[index++]);
                }
            }

            if (inQuotes)
            {
                throw new FormatException("Command-line arguments contain an unmatched quote.");
            }

            arguments.Add(argument.ToString());
        }

        return arguments;
    }
}
