namespace WindowsGSH.Core.Operations;

public interface IServerOperationCoordinator
{
    bool TryBeginServerOperation(
        string serverId,
        string serverName,
        ServerOperationKind kind,
        out ServerOperationScope? scope,
        out ServerOperationSnapshot? existing,
        string? description = null);

    bool TryBeginGlobalOperation(
        ServerOperationKind kind,
        out ServerOperationScope? scope,
        out ServerOperationSnapshot? existing,
        string? description = null);

    ServerOperationResult? CompleteOperation(string serverId);

    ServerOperationResult? FailOperation(string serverId, Exception exception);

    bool CancelOperation(string serverId);

    bool IsOperationRunning(string serverId);

    bool IsAnyOperationRunning();

    IReadOnlyList<ServerOperationSnapshot> GetRunningOperations();

    string? GetLastOperationError(string serverId);
}
