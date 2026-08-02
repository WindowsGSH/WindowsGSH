using System.IO;
using System.Text.Json;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public static class ServerInstanceFactory
{
    public static ServerInstance Load(InstalledServer server)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(server.ConfigPath));
        var root = document.RootElement;
        var settingsElement = root.TryGetProperty("settings", out var foundSettingsElement)
            ? foundSettingsElement
            : default;
        var moduleSettings = ServerModuleSettings.FromSettingsElement(settingsElement);
        var resolvedSettings = new ServerSecretStore().ResolveSettings(server.ServerFolder, moduleSettings.Values);
        var appSettings = ServerConfigAppSettings.FromRootElement(root);

        return new ServerInstance(
            server.Id,
            server.Name,
            server.ModuleId,
            server.ServerFolder,
            server.InstallPath,
            server.ConfigPath,
            resolvedSettings,
            appSettings,
            new ServerModuleSettings(resolvedSettings));
    }
}
