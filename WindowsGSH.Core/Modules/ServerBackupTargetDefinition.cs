namespace WindowsGSH.Core.Modules;

public sealed record ServerBackupTargetDefinition(
    string Key,
    string Label,
    string RelativePath,
    bool IsDirectory,
    bool IsRequired = false);
