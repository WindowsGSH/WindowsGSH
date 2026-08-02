using OpenGSQ.Protocols;
using System.Diagnostics;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Query;

public sealed class Unreal2QueryClient
{
    public async Task<Unreal2ServerInfo> QueryInfoAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var unreal2 = new Unreal2(host, port, (int)timeout.TotalMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        var details = await unreal2.GetDetails(stripColor: true).WaitAsync(linkedCancellation.Token);
        object? playerResponse = null;
        string? playerQueryMessage = null;
        try
        {
            playerResponse = await unreal2.GetPlayers(stripColor: true).WaitAsync(linkedCancellation.Token);
        }
        catch (Exception ex)
        {
            playerQueryMessage = "Unreal2 player list unavailable: " + ex.Message;
        }

        stopwatch.Stop();
        return FromResponses(details, playerResponse, stopwatch.Elapsed.TotalMilliseconds, playerQueryMessage);
    }

    internal static Unreal2ServerInfo FromResponses(
        object details,
        object? playerResponse,
        double latencyMilliseconds,
        string? playerQueryMessage = null)
    {
        var players = QueryResponseReader.ReadPlayers(playerResponse);
        var detail = QueryResponseReader.Describe(details);
        if (!string.IsNullOrWhiteSpace(playerQueryMessage))
        {
            detail += " " + playerQueryMessage;
        }

        return new Unreal2ServerInfo(
            Name: QueryResponseReader.ReadString(details, "ServerName"),
            Map: QueryResponseReader.ReadString(details, "MapName"),
            Players: QueryResponseReader.ReadInt(details, "NumPlayers") ?? players.Count,
            MaxPlayers: QueryResponseReader.ReadInt(details, "MaxPlayers"),
            Game: QueryResponseReader.ReadString(details, "GameType"),
            Version: QueryResponseReader.ReadString(details, "ServerId"),
            QueryDurationMilliseconds: latencyMilliseconds,
            PlayerRows: players,
            DetailMessage: detail,
            Raw: details);
    }
}

public sealed record Unreal2ServerInfo(
    string? Name,
    string? Map,
    int? Players,
    int? MaxPlayers,
    string? Game,
    string? Version,
    double? QueryDurationMilliseconds,
    IReadOnlyList<QueryPlayer> PlayerRows,
    string? DetailMessage,
    object Raw);
