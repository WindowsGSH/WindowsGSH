using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public interface IServerDeleteService
{
    Task<ServerDeleteResult> DeleteAsync(ServerDeleteRequest request, CancellationToken cancellationToken = default);
}

public sealed record ServerDeleteRequest(
    InstalledServer Server,
    ServerDeleteOptions Options,
    Func<InstalledServer, bool>? IsRunning = null,
    Func<InstalledServer, int>? RemoveFirewallRules = null,
    bool UseOperationCoordinator = true);

public sealed record ServerDeleteOptions(
    bool DeleteServerRecord = true,
    bool DeleteInstalledFiles = true,
    bool DeleteBackups = true,
    bool DeleteLogs = true,
    bool RemoveFirewallRules = false,
    bool RequireStopped = true);

public sealed record ServerDeleteResult(
    bool Success,
    ServerOperationStatus Status,
    string Message,
    IReadOnlyList<ServerDeleteStepResult> Steps)
{
    public static ServerDeleteResult Failed(string message, ServerOperationStatus status = ServerOperationStatus.Failed)
    {
        return new ServerDeleteResult(false, status, message, []);
    }
}

public sealed record ServerDeleteStepResult(
    string Name,
    bool Success,
    bool Skipped,
    string? Path,
    string? Message);
