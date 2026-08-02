using System.Diagnostics;
using WindowsGSH.Core.Readiness;

namespace WindowsGSH.Core.Modules;

public interface IModuleSteamInstallCapability
{
    SteamInstallDefinition? SteamInstall { get; }

    Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken);
}

public interface IModuleConfigCapability
{
    IReadOnlyList<ConfigFieldDefinition> GetConfigFields();

    Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken);

    Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken);
}

public interface IModuleQueryCapability
{
    Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken);
}

public interface IModuleRconCapability
{
    Task<string> ExecuteRconCommandAsync(
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken);
}

public interface IModuleGracefulStopCapability
{
    Task StopGracefullyAsync(
        ServerInstance instance,
        CancellationToken cancellationToken);
}

public interface IModuleUpdateCapability
{
    Task<ModuleUpdateResult> UpdateAsync(
        ServerInstance instance,
        CancellationToken cancellationToken);
}

public interface IModuleConsoleCommandCapability
{
    Task<string> ExecuteConsoleCommandAsync(
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken);
}

public interface IModuleReadinessCapability
{
    Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(
        ServerInstance instance,
        CancellationToken cancellationToken);
}

public interface IModuleApiActionCapability
{
    ModuleApiConnectionDefinition? GetApiConnection();

    IReadOnlyList<ModuleApiActionDefinition> GetApiActions();
}

public interface IModuleBackupCapability
{
    IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets();
}

public interface IModulePortCapability
{
    IReadOnlyList<ServerPortDefinition> GetPorts();
}

public interface IModuleAddonCapability
{
    IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions();

    ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId);

    Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken);

    Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken);
}

public interface IModuleVerifyCapability
{
    bool AllowsOnlineVerification { get; }

    Task<ModuleVerifyResult> VerifyAsync(ServerInstance instance, CancellationToken cancellationToken);
}

public interface IModuleExistingServerImportCapability
{
    bool CanImport(string path);

    Task<ModuleExistingServerImportProbe> PreviewImportAsync(
        string path,
        CancellationToken cancellationToken);
}

public sealed record ModuleExistingServerImportProbe(
    string SourceName,
    string InstallPath,
    IReadOnlyDictionary<string, object?> Settings,
    IReadOnlyList<string> Warnings);
