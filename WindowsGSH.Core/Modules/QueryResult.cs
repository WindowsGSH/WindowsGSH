namespace WindowsGSH.Core.Modules;

public sealed record QueryResult(
    ModuleServerStatus Status,
    int? OnlinePlayers = null,
    int? MaxPlayers = null,
    string? Version = null,
    string? Message = null,
    string? Map = null,
    string? Game = null,
    double? QueryDurationMilliseconds = null,
    IReadOnlyList<QueryPlayer>? Players = null,
    string? DetailMessage = null,
    string? Protocol = null)
{
    public IReadOnlyList<QueryPlayer> PlayerRows => Players ?? [];
}

public sealed record QueryPlayer(
    string Name,
    int? Score = null,
    double? DurationSeconds = null,
    int? PingMilliseconds = null,
    string? Id = null);

public enum ModuleServerStatus
{
    Unknown,
    Online,
    Warning,
    Offline
}
