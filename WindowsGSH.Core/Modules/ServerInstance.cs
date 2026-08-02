using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Modules;

public sealed record ServerInstance(
    string Id,
    string Name,
    string ModuleId,
    string ServerFolder,
    string InstallPath,
    string ConfigPath,
    IReadOnlyDictionary<string, object?> Settings,
    ServerConfigAppSettings AppSettings,
    ServerModuleSettings ModuleSettings)
{
    public ServerInstance(
        string Id,
        string Name,
        string ModuleId,
        string ServerFolder,
        string InstallPath,
        string ConfigPath,
        IReadOnlyDictionary<string, object?> Settings)
        : this(
            Id,
            Name,
            ModuleId,
            ServerFolder,
            InstallPath,
            ConfigPath,
            Settings,
            ServerConfigAppSettings.Empty,
            new ServerModuleSettings(Settings))
    {
    }
}
