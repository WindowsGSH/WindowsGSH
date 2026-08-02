namespace WindowsGSH.Core.Modules;

public sealed record ServerAddonStatus(
    string AddonId,
    bool IsInstalled,
    bool IsEnabled,
    string StatusText,
    string? SourceName = null,
    string? SourceVersion = null,
    DateTimeOffset? InstalledAtUtc = null,
    string? Sha256 = null);

