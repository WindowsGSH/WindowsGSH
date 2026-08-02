using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public interface IServerImportService
{
    Task<ServerImportResult> ImportAsync(ServerImportRequest request, CancellationToken cancellationToken = default);
}

public sealed record ServerImportRequest(
    IGameServerModule Module,
    string ModuleId,
    string ServerId,
    string ServerName,
    string ServerFolder,
    string InstallPath,
    IReadOnlyDictionary<string, object?> Settings,
    bool ReadExistingConfig = false,
    bool UseOperationCoordinator = true);

public sealed record ServerImportResult(
    bool Success,
    ServerOperationStatus Status,
    string Message,
    ServerInstance? Instance,
    IReadOnlyList<string> Warnings)
{
    public static ServerImportResult Failed(string message, ServerOperationStatus status = ServerOperationStatus.Failed)
    {
        return new ServerImportResult(false, status, message, null, []);
    }
}
