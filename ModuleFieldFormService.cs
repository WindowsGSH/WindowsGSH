using System.Text.Json.Nodes;
using WindowsGSH.Core.Modules;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfFrameworkElement = System.Windows.FrameworkElement;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WindowsGSH;

public static class ModuleFieldFormService
{
    public static IReadOnlySet<string> GetVisibilityControllerKeys(
        IEnumerable<ConfigFieldDefinition> fields)
    {
        return fields
            .Select(field => field.VisibleWhen?.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static object? GetControlValue(IReadOnlyDictionary<string, WpfFrameworkElement> controls, string key)
    {
        return controls.TryGetValue(key, out var control)
            ? GetControlValue(control)
            : null;
    }

    public static object? GetControlValue(WpfFrameworkElement control)
    {
        return control switch
        {
            WpfTextBox textBox => textBox.Text,
            WpfPasswordBox passwordBox => passwordBox.Password,
            WpfCheckBox checkBox => checkBox.IsChecked == true,
            WpfComboBox comboBox => GetComboBoxValue(comboBox.SelectedItem, comboBox.Text),
            _ => null
        };
    }

    public static string GetComboBoxValue(object? selectedItem, string? text)
    {
        return selectedItem?.ToString() ?? text ?? string.Empty;
    }

    public static bool IsFieldVisible(ConfigFieldDefinition field, Func<string, object?> valueProvider)
    {
        if (field.VisibleWhen == null)
        {
            return true;
        }

        var value = valueProvider(field.VisibleWhen.Key);
        if (field.VisibleWhen.IsTruthy.HasValue)
        {
            return IsTruthy(value) == field.VisibleWhen.IsTruthy.Value;
        }

        return string.Equals(
            value?.ToString() ?? string.Empty,
            field.VisibleWhen.EqualsValue ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            int number => number != 0,
            long number => number != 0,
            double number => Math.Abs(number) > double.Epsilon,
            _ => value != null
        };
    }

    public static void ValidateSettings(IEnumerable<ConfigFieldDefinition> fields, IReadOnlyDictionary<string, object?> settings)
    {
        ConfigFieldValidationService.ValidateSettings(fields, settings);
    }

    public static JsonNode? ToJsonValue(ConfigFieldDefinition field, object? value)
    {
        if (field.Type == ConfigFieldType.Boolean)
        {
            return JsonValue.Create(value is bool boolValue && boolValue);
        }

        if (field.Type is ConfigFieldType.Number or ConfigFieldType.Port &&
            int.TryParse(value?.ToString(), out var intValue))
        {
            return JsonValue.Create(intValue);
        }

        return JsonValue.Create(value?.ToString() ?? string.Empty);
    }

    public static object? GetJsonSettingValue(JsonObject settings, string key, object? fallback)
    {
        if (!settings.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolValue) => boolValue,
            JsonValue jsonValue when jsonValue.TryGetValue<int>(out var intValue) => intValue,
            JsonValue jsonValue when jsonValue.TryGetValue<double>(out var doubleValue) => doubleValue,
            _ => fallback
        };
    }
}
