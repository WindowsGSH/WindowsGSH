using Discord;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using DiscordColor = Discord.Color;

namespace WindowsGSH.Discord;

public static class DiscordEmbedRenderer
{
    public static Embed BuildDashboardEmbed(IReadOnlyList<InstalledServer> servers)
    {
        var description = servers.Count == 0
            ? "No servers are included on the dashboard."
            : BuildDashboardTable(servers);

        return new EmbedBuilder()
            .WithTitle("WindowsGSH Server Status")
            .WithColor(DiscordColor.Blue)
            .WithDescription(description)
            .WithFooter(GetFooterText("Status Board"))
            .WithCurrentTimestamp()
            .Build();
    }

    public static string BuildDashboardTable(IReadOnlyList<InstalledServer> servers)
    {
        var lines = new List<string>();
        foreach (var server in servers.OrderBy(server => int.TryParse(server.Id, out var id) ? id : int.MaxValue).ThenBy(server => server.Name))
        {
            var name = Truncate(server.Name, 20).PadRight(20);
            var players = FormatPlayers(server).PadRight(10);
            var uptime = FormatUptime(server);
            var detail = server.IsOperationRunning
                ? Truncate(server.OperationText, 18)
                : server.Status == ServerRuntimeStatus.Running
                    ? uptime
                    : server.CurrentStatusText;
            lines.Add($"{GetStatusEmoji(server)} {name} {players} {detail}");
        }

        return "```text" + Environment.NewLine + string.Join(Environment.NewLine, lines) + Environment.NewLine + "```";
    }

    public static Embed BuildServerEmbed(InstalledServer server, DiscordServerCardOptions? options = null)
    {
        options ??= DiscordServerCardOptions.Default;
        var isOnline = server.Status == ServerRuntimeStatus.Running;
        var statusText = server.IsOperationRunning ? server.OperationText : server.CurrentStatusText;
        var description = server.IsOperationRunning
            ? $"Server is currently **{statusText.ToUpperInvariant()}**"
            : isOnline
                ? "Server is currently **ONLINE**"
                : "Server is currently **OFFLINE**";

        var builder = new EmbedBuilder()
            .WithTitle($"{GetStatusEmoji(server)} {server.Name}")
            .WithColor(GetEmbedColor(server))
            .WithDescription(description)
            .AddField("Status", $"{GetStatusEmoji(server)} {statusText}", true)
            .AddField("Game Server", server.ModuleId, true);

        if (options.ShowServerAddress)
        {
            builder.AddField("Server IP:Port", $"`{server.IpAddress}:{server.Port}`", true);
        }

        if (options.ShowPlayerCount)
        {
            builder.AddField("Players", FormatPlayers(server), true);
        }

        if (options.ShowUptime)
        {
            builder.AddField("Uptime", FormatUptime(server), true);
        }

        if (options.ShowResourceUsage)
        {
            builder.AddField("CPU / RAM", $"{server.CpuUsage} / {server.MemoryUsage}", true);
        }

        if (options.ShowMap && !string.IsNullOrWhiteSpace(server.QueryMap))
        {
            builder.AddField("Map", Truncate(server.QueryMap, 100), true);
        }

        var gameVersion = FormatGameVersion(server);
        if (options.ShowGameVersion && !string.IsNullOrWhiteSpace(gameVersion))
        {
            builder.AddField("Game / Version", gameVersion, true);
        }

        var querySummary = FormatQuerySummary(server);
        if (options.ShowQuerySummary && !string.IsNullOrWhiteSpace(querySummary))
        {
            builder.AddField("Query Protocol / Duration", querySummary, true);
        }

        if (options.ShowPlayerList && server.QueryPlayerRows.Count > 0)
        {
            builder.AddField("Player List", FormatPlayerList(server.QueryPlayerRows), false);
        }

        if (options.ShowQueryDiagnostics &&
            server.Status == ServerRuntimeStatus.Warning &&
            !string.IsNullOrWhiteSpace(server.QueryMessage))
        {
            builder.AddField("Query Diagnostic", Truncate(server.QueryMessage, 900), false);
        }
        else if (options.ShowQueryDiagnostics &&
                 server.QueryPlayerRows.Count == 0 &&
                 IsOptionalQueryDetail(server.QueryDetailMessage))
        {
            builder.AddField("Query Detail", Truncate(server.QueryDetailMessage!, 900), false);
        }

        return builder
            .WithFooter(GetFooterText("Live Status Panel"))
            .WithCurrentTimestamp()
            .Build();
    }

    public static MessageComponent BuildServerComponents(InstalledServer server, string panelNonce)
    {
        return new ComponentBuilder()
            .WithButton("Refresh", BuildButtonId(server.Id, "refresh", panelNonce), ButtonStyle.Primary, new Emoji("\uD83D\uDD04"))
            .WithButton("View Logs", BuildButtonId(server.Id, "logs", panelNonce), ButtonStyle.Secondary, new Emoji("\uD83D\uDCDC"))
            .WithButton("Restart", BuildButtonId(server.Id, "restart", panelNonce), ButtonStyle.Success, new Emoji("\uD83D\uDD01"), disabled: !server.CanStop && !server.CanStart)
            .WithButton("Stop", BuildButtonId(server.Id, "stop", panelNonce), ButtonStyle.Danger, new Emoji("\u26D4"), disabled: !server.CanStop)
            .Build();
    }

    public static string FormatPlayers(InstalledServer server)
    {
        return string.IsNullOrWhiteSpace(server.PlayerCount) || server.PlayerCount == "--"
            ? "--"
            : server.PlayerCount.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static string FormatUptime(InstalledServer server)
    {
        return string.IsNullOrWhiteSpace(server.Uptime) ? "--" : server.Uptime;
    }

    public static string FormatQuerySummary(InstalledServer server)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(server.QueryProtocol))
        {
            parts.Add(server.QueryProtocol);
        }

        if (server.QueryDurationMilliseconds.HasValue)
        {
            parts.Add($"{Math.Round(server.QueryDurationMilliseconds.Value):0} ms");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" | ", parts);
    }

    public static string FormatPlayerList(IReadOnlyList<QueryPlayer> players)
    {
        const int maxPlayersShown = 12;
        var lines = players
            .Take(maxPlayersShown)
            .Select(player =>
            {
                var details = new List<string>();
                if (player.Score.HasValue)
                {
                    details.Add($"score {player.Score.Value}");
                }

                if (player.PingMilliseconds.HasValue)
                {
                    details.Add($"{player.PingMilliseconds.Value} ms");
                }

                var safeName = Truncate(SanitizeDiscordText(player.Name), 60);
                return details.Count == 0
                    ? safeName
                    : $"{safeName} ({string.Join(", ", details)})";
            })
            .ToList();
        if (players.Count > maxPlayersShown)
        {
            lines.Add($"+ {players.Count - maxPlayersShown} more");
        }

        return Truncate(string.Join(Environment.NewLine, lines), 1000);
    }

    public static string SanitizeDiscordText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(unnamed)";
        }

        var normalized = Regex.Replace(value, @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"(?i)\bhttps?://", match => match.Value.Replace(":", "\u200B:"));

        var result = new StringBuilder(normalized.Length + 8);
        foreach (var character in normalized)
        {
            if (character == '@')
            {
                result.Append("@\u200B");
                continue;
            }

            if (character is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '<' or '[' or ']' or '(' or ')' or '#')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static bool IsOptionalQueryDetail(string? detail)
    {
        return !string.IsNullOrWhiteSpace(detail) &&
               (detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    public static string GetStatusEmoji(InstalledServer server)
    {
        if (server.IsOperationRunning)
        {
            return "\uD83D\uDD35";
        }

        return server.Status switch
        {
            ServerRuntimeStatus.Running => "\uD83D\uDFE2",
            ServerRuntimeStatus.Warning => "\uD83D\uDFE1",
            _ => "\uD83D\uDD34"
        };
    }

    public static DiscordColor GetEmbedColor(InstalledServer server)
    {
        if (server.IsOperationRunning)
        {
            return DiscordColor.Blue;
        }

        return server.Status switch
        {
            ServerRuntimeStatus.Running => DiscordColor.Green,
            ServerRuntimeStatus.Warning => DiscordColor.Gold,
            _ => DiscordColor.Red
        };
    }

    private static string GetFooterText(string panelName)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        return $"WindowsGSH v{version} - {panelName}";
    }

    private static string FormatGameVersion(InstalledServer server)
    {
        if (string.IsNullOrWhiteSpace(server.QueryGame))
        {
            return string.IsNullOrWhiteSpace(server.QueryVersion) ? string.Empty : Truncate(server.QueryVersion, 100);
        }

        return string.IsNullOrWhiteSpace(server.QueryVersion)
            ? Truncate(server.QueryGame, 100)
            : Truncate($"{server.QueryGame} / {server.QueryVersion}", 100);
    }

    private static string BuildButtonId(string serverId, string action, string panelNonce)
    {
        return $"wgsh:{serverId}:{action}:{panelNonce}";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }
}

public sealed record DiscordServerCardOptions(
    bool ShowServerAddress,
    bool ShowPlayerCount,
    bool ShowUptime,
    bool ShowResourceUsage,
    bool ShowMap,
    bool ShowGameVersion,
    bool ShowQuerySummary,
    bool ShowPlayerList,
    bool ShowQueryDiagnostics)
{
    public static DiscordServerCardOptions Default { get; } = new(
        true, true, true, true, true, true, true, true, true);
}
