namespace WindowsGSH.Core.Modules;

public sealed record ModuleApiConnectionDefinition(
    string EnabledKey,
    string Host,
    string PortKey,
    string UsernameKey,
    string PasswordKey,
    string Scheme = "http");

public sealed record ModuleApiActionDefinition(
    string Key,
    string Label,
    string Method,
    string Path,
    bool Destructive,
    string? Description,
    string? ConfirmMessage,
    string? BodyTemplate,
    IReadOnlyList<ConfigFieldDefinition> Parameters);
