namespace WindowsGSH.Core.Modules;

public static class WindowsCommandLineEscaper
{
    public static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.Any(char.IsWhiteSpace) || value.Contains('"');
        if (!needsQuotes)
        {
            return value;
        }

        var result = new System.Text.StringBuilder();
        result.Append('"');
        var backslashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }

            result.Append('\\', backslashCount);
            result.Append(character);
            backslashCount = 0;
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }
}
