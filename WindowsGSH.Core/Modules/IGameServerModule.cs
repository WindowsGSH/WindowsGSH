using System.Diagnostics;

namespace WindowsGSH.Core.Modules;

public interface IGameServerModule :
    IModuleSteamInstallCapability,
    IModuleConfigCapability,
    IModuleQueryCapability,
    IModuleRconCapability,
    IModuleBackupCapability,
    IModulePortCapability,
    IModuleAddonCapability
{
    string Id { get; }

    string Name { get; }

    string Version { get; }

    ModuleCapabilities Capabilities { get; }

    ModuleRuntimeDefinition Runtime { get; }

    string GetServerName(IReadOnlyDictionary<string, object?> settings);

    ServerDisplayInfo GetDisplayInfo(ServerInstance instance);

    Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken);

    Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken);

    Task StopAsync(ServerInstance instance, CancellationToken cancellationToken);

    bool IsInstallValid(ServerInstance instance);

    string? GetConsoleLogPath(ServerInstance instance);

    SteamInstallDefinition? IModuleSteamInstallCapability.SteamInstall => null;

    Task<InstallPlan> IModuleSteamInstallCapability.CreateInstallPlanAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromException<InstallPlan>(new NotSupportedException($"{Name} does not provide install automation."));
    }

    IReadOnlyList<ConfigFieldDefinition> IModuleConfigCapability.GetConfigFields() => [];

    Task<IReadOnlyDictionary<string, object?>> IModuleConfigCapability.ReadConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    Task IModuleConfigCapability.WriteConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    Task<QueryResult> IModuleQueryCapability.QueryAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new QueryResult(ModuleServerStatus.Unknown, Message: $"{Name} does not provide query automation."));
    }

    Task<string> IModuleRconCapability.ExecuteRconCommandAsync(
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken)
    {
        return Task.FromException<string>(new NotSupportedException($"{Name} does not provide RCON automation."));
    }

    IReadOnlyList<ServerBackupTargetDefinition> IModuleBackupCapability.GetBackupTargets() => [];

    IReadOnlyList<ServerPortDefinition> IModulePortCapability.GetPorts() => [];

    IReadOnlyList<ServerAddonDefinition> IModuleAddonCapability.GetAddonDefinitions() => [];

    ServerAddonStatus IModuleAddonCapability.GetAddonStatus(ServerInstance instance, string addonId)
    {
        return new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Manual addon");
    }

    Task IModuleAddonCapability.InstallAddonAsync(
        ServerInstance instance,
        string addonId,
        CancellationToken cancellationToken)
    {
        return Task.FromException(new NotSupportedException($"{Name} does not provide addon installation automation."));
    }

    Task IModuleAddonCapability.RemoveAddonAsync(
        ServerInstance instance,
        string addonId,
        CancellationToken cancellationToken)
    {
        return Task.FromException(new NotSupportedException($"{Name} does not provide addon removal automation."));
    }

    Task<IReadOnlyList<Process>> IModuleAddonCapability.StartAddonProcessesAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Process>>([]);
    }
}
