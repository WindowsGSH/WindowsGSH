using System.Text.Json.Serialization;

namespace WindowsGSH.Core.Modules;

public sealed record ConfigFieldDefinition(
    string Key,
    string Label,
    ConfigFieldType Type,
    object? DefaultValue = null,
    bool Required = false,
    string? Description = null,
    IReadOnlyList<string>? Options = null,
    double? Minimum = null,
    double? Maximum = null,
    string? Group = null,
    FieldVisibilityCondition? VisibleWhen = null,
    bool RestartRequired = false,
    string? ValidationPattern = null,
    string? ValidationMessage = null);

public sealed record FieldVisibilityCondition(
    string Key,
    [property: JsonPropertyName("equals")] string? EqualsValue = null,
    bool? IsTruthy = null);

public enum ConfigFieldType
{
    Text,
    Password,
    Number,
    Boolean,
    Select,
    MultiSelect,
    Path,
    Port,
    Cron,
    CommandLine
}
