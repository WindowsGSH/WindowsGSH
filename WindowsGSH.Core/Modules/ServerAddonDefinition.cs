namespace WindowsGSH.Core.Modules;

public sealed record ServerAddonDefinition(
    string Id,
    string Name,
    string Description,
    ModuleCapabilities CapabilitiesAdded,
    IReadOnlyList<ConfigFieldDefinition> ConfigFields,
    string? DownloadUrl = null,
    string? InstallInstructions = null,
    AddonPackageDefinition? Package = null,
    string? SourceName = null,
    string? SourceVersion = null);

public sealed record AddonPackageDefinition(
    AddonPackageKind Kind,
    string SourceUrl,
    string InstallPath,
    string? FileName = null,
    int StripComponents = 0,
    string? ArchiveSubpath = null,
    IReadOnlyList<string>? RequiredMarkers = null,
    string? ExpectedSha256 = null);

public enum AddonPackageKind
{
    Zip,
    Tar,
    TarGz,
    File
}
