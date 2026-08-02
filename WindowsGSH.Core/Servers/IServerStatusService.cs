using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public interface IServerStatusService
{
    Task<ServerStatusSnapshot> GetStatusAsync(
        IGameServerModule? module,
        ServerInstance instance,
        bool hasUpdateAvailable,
        CancellationToken cancellationToken = default);

    ServerStatusSnapshot? GetCachedStatus(string serverId);
}

public sealed record ServerStatusSnapshot(
    string ServerId,
    string ServerName,
    string ModuleId,
    ServerRuntimeStatus Status,
    bool IsProcessRunning,
    bool Queried,
    ModuleServerStatus? QueryStatus,
    int? OnlinePlayers,
    int? MaxPlayers,
    string? Version,
    string? Message,
    string? Map,
    string? Game,
    double? QueryDurationMilliseconds,
    IReadOnlyList<QueryPlayer>? Players,
    string? DetailMessage,
    string? Protocol,
    string CurrentStatusText,
    string StatusText,
    string StatusBrushKey,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastSuccessfulQueryAt = null,
    bool ShouldLogMessage = false,
    string? LogMessage = null)
{
    public QueryResult? ToQueryResult()
    {
        return Queried && QueryStatus.HasValue
            ? new QueryResult(
                QueryStatus.Value,
                OnlinePlayers,
                MaxPlayers,
                Version,
                Message,
                Map,
                Game,
                QueryDurationMilliseconds,
                Players,
                DetailMessage,
                Protocol)
            : null;
    }
}

