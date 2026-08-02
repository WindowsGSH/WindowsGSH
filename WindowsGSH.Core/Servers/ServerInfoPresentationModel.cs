using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed record ServerInfoPresentationModel(
    string PlayersText,
    string MapText,
    string GameVersionText,
    string ProtocolText,
    string QueryDurationText,
    string QueryStateText,
    string DiagnosticText,
    bool HasDiagnostic,
    IReadOnlyList<ServerInfoPlayerRow> Players,
    string PlayerListStateText)
{
    public bool HasPlayers => Players.Count > 0;

    public static ServerInfoPresentationModel FromServer(InstalledServer server, bool? supportsQuery = null)
    {
        var queryStatus = server.Status == ServerRuntimeStatus.Running
            ? ModuleServerStatus.Online
            : server.Status == ServerRuntimeStatus.Warning
                ? ModuleServerStatus.Warning
                : ModuleServerStatus.Offline;
        var (onlinePlayers, maxPlayers) = ParsePlayerCount(server.PlayerCount);
        return FromQuery(
            new QueryResult(
                queryStatus,
                onlinePlayers,
                maxPlayers,
                server.QueryVersion,
                server.QueryMessage,
                server.QueryMap,
                server.QueryGame,
                server.QueryDurationMilliseconds,
                server.QueryPlayers,
                server.QueryDetailMessage,
                server.QueryProtocol),
            ParseInt(server.MaxPlayers),
            supportsQuery: supportsQuery ??
                           (!string.IsNullOrWhiteSpace(server.QueryProtocol) ||
                            server.QueryMessage != null ||
                            server.QueryDetailMessage != null));
    }

    public static ServerInfoPresentationModel FromQuery(
        QueryResult? query,
        int? configuredMaxPlayers,
        bool supportsQuery,
        string? failureMessage = null)
    {
        if (!supportsQuery)
        {
            return Empty(
                playersText: $"-- / {configuredMaxPlayers?.ToString() ?? "--"}",
                queryState: "This module does not support server queries.",
                playerState: "Player data is unavailable because querying is not supported.");
        }

        if (query == null)
        {
            var failureDiagnostic = string.IsNullOrWhiteSpace(failureMessage)
                ? "No query result is available yet."
                : failureMessage.Trim();
            return Empty(
                playersText: $"-- / {configuredMaxPlayers?.ToString() ?? "--"}",
                queryState: "Query unavailable",
                playerState: "No player data is available.",
                diagnostic: failureDiagnostic);
        }

        var maxPlayers = query.MaxPlayers ?? configuredMaxPlayers;
        var playersText = query.OnlinePlayers.HasValue
            ? $"{query.OnlinePlayers.Value} / {maxPlayers?.ToString() ?? "--"}"
            : $"-- / {maxPlayers?.ToString() ?? "--"}";
        var players = query.PlayerRows
            .Select(player => new ServerInfoPlayerRow(
                player.Name,
                player.Score?.ToString() ?? "--",
                player.PingMilliseconds.HasValue ? $"{player.PingMilliseconds.Value} ms" : "--",
                FormatDuration(player.DurationSeconds)))
            .ToArray();
        var diagnostic = CombineDiagnostics(query.Message, query.DetailMessage, failureMessage);

        return new ServerInfoPresentationModel(
            playersText,
            ValueOrDash(query.Map),
            FormatGameVersion(query.Game, query.Version),
            ValueOrDash(query.Protocol),
            query.QueryDurationMilliseconds.HasValue
                ? $"{query.QueryDurationMilliseconds.Value:0} ms"
                : "--",
            FormatQueryState(query.Status),
            diagnostic,
            !string.IsNullOrWhiteSpace(diagnostic),
            players,
            players.Length > 0
                ? $"{players.Length} player row{(players.Length == 1 ? string.Empty : "s")} returned."
                : query.OnlinePlayers > 0
                    ? "The server reported players online but did not return player rows."
                    : "No players are currently listed.");
    }

    private static ServerInfoPresentationModel Empty(
        string playersText,
        string queryState,
        string playerState,
        string diagnostic = "")
    {
        return new ServerInfoPresentationModel(
            playersText,
            "--",
            "--",
            "--",
            "--",
            queryState,
            diagnostic,
            !string.IsNullOrWhiteSpace(diagnostic),
            [],
            playerState);
    }

    private static string FormatQueryState(ModuleServerStatus status)
    {
        return status switch
        {
            ModuleServerStatus.Online => "Query online",
            ModuleServerStatus.Warning => "Query warning",
            ModuleServerStatus.Offline => "Query offline",
            _ => "Query status unknown"
        };
    }

    private static string FormatGameVersion(string? game, string? version)
    {
        if (string.IsNullOrWhiteSpace(game))
        {
            return ValueOrDash(version);
        }

        return string.IsNullOrWhiteSpace(version) ? game : $"{game} / {version}";
    }

    private static string FormatDuration(double? durationSeconds)
    {
        if (!durationSeconds.HasValue || durationSeconds.Value < 0)
        {
            return "--";
        }

        var duration = TimeSpan.FromSeconds(durationSeconds.Value);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                : $"{duration.TotalSeconds:0}s";
    }

    private static string CombineDiagnostics(params string?[] messages)
    {
        return string.Join(
            Environment.NewLine,
            messages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message!.Trim())
                .Distinct(StringComparer.Ordinal));
    }

    private static (int? Online, int? Max) ParsePlayerCount(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (ParseInt(parts[0]), ParseInt(parts[1]))
            : (null, null);
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    private static string ValueOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
}

public sealed record ServerInfoPlayerRow(
    string Name,
    string ScoreText,
    string PingText,
    string DurationText);
