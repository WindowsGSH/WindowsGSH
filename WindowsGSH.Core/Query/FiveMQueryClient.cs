using System.Diagnostics;
using OpenGSQ.Protocols;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Query;

public sealed class FiveMQueryClient
{
    public async Task<FiveMServerInfo> QueryInfoAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var fiveM = new FiveM(host, port, (int)timeout.TotalMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        var dynamicInfo = await fiveM.GetDynamic().WaitAsync(linkedCancellation.Token);
        object? playerResponse = null;
        string? playerQueryMessage = null;
        try
        {
            playerResponse = await fiveM.GetPlayers().WaitAsync(linkedCancellation.Token);
        }
        catch (Exception ex)
        {
            playerQueryMessage = "FiveM player list unavailable: " + ex.Message;
        }

        stopwatch.Stop();
        return FromResponses(dynamicInfo, playerResponse, stopwatch.Elapsed.TotalMilliseconds, playerQueryMessage);
    }

    internal static FiveMServerInfo FromResponses(
        object dynamicInfo,
        object? playerResponse,
        double latencyMilliseconds,
        string? playerQueryMessage = null)
    {
        var players = QueryResponseReader.ReadPlayers(playerResponse);
        return new FiveMServerInfo(
            Name: QueryResponseReader.ReadString(dynamicInfo, "hostname", "serverName", "name"),
            Map: QueryResponseReader.ReadString(dynamicInfo, "mapname", "map", "mapName"),
            Players: QueryResponseReader.ReadInt(dynamicInfo, "clients", "players", "numPlayers") ?? players.Count,
            MaxPlayers: QueryResponseReader.ReadInt(dynamicInfo, "sv_maxclients", "maxClients", "maxPlayers"),
            Version: QueryResponseReader.ReadString(dynamicInfo, "version", "server", "build"),
            QueryDurationMilliseconds: latencyMilliseconds,
            PlayerRows: players,
            DetailMessage: CombineDetails(QueryResponseReader.Describe(dynamicInfo), playerQueryMessage),
            Raw: dynamicInfo);
    }

    private static string CombineDetails(string detail, string? playerQueryMessage) =>
        string.IsNullOrWhiteSpace(playerQueryMessage) ? detail : detail + " " + playerQueryMessage;
}

public sealed record FiveMServerInfo(
    string? Name,
    string? Map,
    int? Players,
    int? MaxPlayers,
    string? Version,
    double? QueryDurationMilliseconds,
    IReadOnlyList<QueryPlayer> PlayerRows,
    string? DetailMessage,
    object Raw);
