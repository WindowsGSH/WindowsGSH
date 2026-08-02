using System.Globalization;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public static class ImportFieldValueConverter
{
    public static object? ConvertValue(ConfigFieldDefinition field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return field.Type == ConfigFieldType.Boolean ? false : "";
        }

        var text = value.Trim();
        return field.Type switch
        {
            ConfigFieldType.Boolean => TryParseBoolean(text, out var boolean)
                ? boolean
                : text,
            ConfigFieldType.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : text,
            ConfigFieldType.Port => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                ? port
                : text,
            _ => text
        };
    }

    public static bool IsValidBooleanText(string value)
    {
        return string.IsNullOrWhiteSpace(value) || TryParseBoolean(value.Trim(), out _);
    }

    public static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
            case "on":
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
