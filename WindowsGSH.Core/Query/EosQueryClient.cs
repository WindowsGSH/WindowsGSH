using System.Text.Json;
using System.Diagnostics;
using OpenGSQ.Protocols;

namespace WindowsGSH.Core.Query;

public sealed class EosQueryClient
{
    public async Task<EosServerInfo> QueryInfoAsync(
        string host,
        int port,
        string deploymentId,
        string clientId,
        string clientSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deploymentId))
        {
            throw new InvalidOperationException("EOS deployment id is required.");
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("EOS client id and client secret are required.");
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        var stopwatch = Stopwatch.StartNew();
        var token = await EOS.GetAccessTokenAsync(clientId, clientSecret, deploymentId, "client_credentials", string.Empty, string.Empty)
            .WaitAsync(linkedCancellation.Token);
        var eos = new EOS(host, port, deploymentId, token, (int)timeout.TotalMilliseconds);
        var info = await eos.GetInfo().WaitAsync(linkedCancellation.Token);

        stopwatch.Stop();
        return FromResponse(info, stopwatch.Elapsed.TotalMilliseconds);
    }

    internal static EosServerInfo FromResponse(Dictionary<string, JsonElement> info, double latencyMilliseconds)
    {
        return new EosServerInfo(
            Name: QueryResponseReader.ReadString(info, "name", "serverName", "SESSIONNAME_s", "server_name"),
            Map: QueryResponseReader.ReadString(info, "map", "mapName", "MAPNAME_s", "map_name"),
            Players: QueryResponseReader.ReadInt(info, "players", "numPlayers", "PLAYERS_l", "totalPlayers"),
            MaxPlayers: QueryResponseReader.ReadInt(info, "maxPlayers", "MAXPLAYERS_l", "max_players", "settings.maxPublicPlayers"),
            Version: QueryResponseReader.ReadString(info, "version", "build", "BUILDID_s"),
            QueryDurationMilliseconds: latencyMilliseconds,
            DetailMessage: QueryResponseReader.Describe(info),
            Raw: info);
    }
}

public sealed record EosServerInfo(
    string? Name,
    string? Map,
    int? Players,
    int? MaxPlayers,
    string? Version,
    double? QueryDurationMilliseconds,
    string? DetailMessage,
    Dictionary<string, JsonElement> Raw);
