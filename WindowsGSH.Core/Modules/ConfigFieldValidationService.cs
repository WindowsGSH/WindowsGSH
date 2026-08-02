using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Modules;

public static class ConfigFieldValidationService
{
    public static void ValidateSettings(IEnumerable<ConfigFieldDefinition> fields, IReadOnlyDictionary<string, object?> settings)
    {
        foreach (var field in fields)
        {
            settings.TryGetValue(field.Key, out var value);
            var text = value?.ToString() ?? string.Empty;
            if (field.Required && string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"{field.Label} is required.");
            }

            if (field.Type == ConfigFieldType.Number && !string.IsNullOrWhiteSpace(text))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    throw new InvalidOperationException($"{field.Label} must be a number.");
                }

                if (field.Minimum.HasValue && number < field.Minimum.Value)
                {
                    throw new InvalidOperationException($"{field.Label} must be at least {field.Minimum.Value}.");
                }

                if (field.Maximum.HasValue && number > field.Maximum.Value)
                {
                    throw new InvalidOperationException($"{field.Label} must be at most {field.Maximum.Value}.");
                }
            }

            if (field.Type == ConfigFieldType.Port && !string.IsNullOrWhiteSpace(text))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    throw new InvalidOperationException($"{field.Label} must be a number.");
                }

                if (port is < 1 or > 65535)
                {
                    throw new InvalidOperationException($"{field.Label} must be between 1 and 65535.");
                }

                if (field.Minimum.HasValue && port < field.Minimum.Value)
                {
                    throw new InvalidOperationException($"{field.Label} must be at least {field.Minimum.Value}.");
                }

                if (field.Maximum.HasValue && port > field.Maximum.Value)
                {
                    throw new InvalidOperationException($"{field.Label} must be at most {field.Maximum.Value}.");
                }
            }

            if (field.Type == ConfigFieldType.Boolean &&
                !string.IsNullOrWhiteSpace(text) &&
                !Servers.ImportFieldValueConverter.IsValidBooleanText(text))
            {
                throw new InvalidOperationException($"{field.Label} must be true or false.");
            }

            if (!string.IsNullOrWhiteSpace(text) &&
                !string.IsNullOrWhiteSpace(field.ValidationPattern) &&
                !Regex.IsMatch(text, field.ValidationPattern))
            {
                throw new InvalidOperationException(field.ValidationMessage ?? $"{field.Label} is invalid.");
            }
        }
    }
}
