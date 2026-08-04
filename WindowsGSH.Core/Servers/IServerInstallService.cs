using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public interface IServerInstallService
{
    Task<ServerInstallValidationResult> ValidateInstallAsync(
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken = default);

    Task<ServerInstallResult> InstallAsync(
        ServerInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<ServerInstallResult> UpdateAsync(
        ServerUpdateRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ServerInstallRequest(
    IGameServerModule Module,
    ServerInstance Instance,
    string SteamBranch = "public",
    string SteamBranchPassword = "",
    bool WriteServerConfig = true,
    bool WriteModuleConfig = true,
    bool UseOperationCoordinator = true,
    IProgress<string>? Progress = null,
    UpnpMappingPolicy UpnpMappingPolicy = UpnpMappingPolicy.Manual);

public sealed record ServerUpdateRequest(
    IGameServerModule Module,
    ServerInstance Instance,
    string Reason,
    string SteamBranch = "",
    string SteamBranchPassword = "",
    bool SkipIgnoredBuild = false,
    string? IgnoredBuildId = null,
    bool UseOperationCoordinator = false,
    IProgress<string>? Progress = null);

public sealed record ServerInstallValidationResult(
    bool Success,
    string? Message = null)
{
    public static ServerInstallValidationResult Passed { get; } = new(true);

    public static ServerInstallValidationResult Failed(string message)
    {
        return new ServerInstallValidationResult(false, message);
    }
}

public sealed record ServerInstallResult(
    bool Success,
    ServerOperationKind Kind,
    string ServerId,
    string ServerName,
    string Message,
    int? SteamExitCode = null,
    string? RemoteBuildId = null,
    InstallPlan? InstallPlan = null,
    ModuleUpdateResult? ModuleUpdate = null,
    ServerOperationStatus Status = ServerOperationStatus.Completed,
    string? LastError = null)
{
    public static ServerInstallResult Failed(
        ServerOperationKind kind,
        ServerInstance instance,
        string message,
        ServerOperationStatus status = ServerOperationStatus.Failed)
    {
        return new ServerInstallResult(false, kind, instance.Id, instance.Name, message, Status: status, LastError: message);
    }
}
