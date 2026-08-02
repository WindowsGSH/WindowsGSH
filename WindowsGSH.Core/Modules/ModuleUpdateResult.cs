namespace WindowsGSH.Core.Modules;

public sealed record ModuleUpdateResult(
    bool Updated,
    string Message,
    string? InstalledBuildId = null,
    string? DownloadedFileName = null);
