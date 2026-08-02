using Discord;
using WindowsGSH.Core.Servers;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordEmbedRendererTests
{
    [Fact]
    public void Running_server_uses_green_status()
    {
        var server = CreateServer(ServerRuntimeStatus.Running);

        Assert.Equal("\uD83D\uDFE2", DiscordEmbedRenderer.GetStatusEmoji(server));
        Assert.Equal(Color.Green, DiscordEmbedRenderer.GetEmbedColor(server));
    }

    [Fact]
    public void Warning_server_uses_yellow_status()
    {
        var server = CreateServer(ServerRuntimeStatus.Warning);

        Assert.Equal("\uD83D\uDFE1", DiscordEmbedRenderer.GetStatusEmoji(server));
        Assert.Equal(Color.Gold, DiscordEmbedRenderer.GetEmbedColor(server));
    }

    [Fact]
    public void Offline_server_uses_red_status()
    {
        var server = CreateServer(ServerRuntimeStatus.Offline);

        Assert.Equal("\uD83D\uDD34", DiscordEmbedRenderer.GetStatusEmoji(server));
        Assert.Equal(Color.Red, DiscordEmbedRenderer.GetEmbedColor(server));
    }

    [Fact]
    public void Operation_running_server_uses_blue_status()
    {
        var server = CreateServer(ServerRuntimeStatus.Running) with
        {
            IsOperationRunning = true,
            OperationText = "Updating"
        };

        Assert.Equal("\uD83D\uDD35", DiscordEmbedRenderer.GetStatusEmoji(server));
        Assert.Equal(Color.Blue, DiscordEmbedRenderer.GetEmbedColor(server));
    }

    [Fact]
    public void Server_embed_includes_rich_query_fields_and_players()
    {
        var server = CreateServer(ServerRuntimeStatus.Running) with
        {
            QueryMap = "TheIsland",
            QueryGame = "Ark",
            QueryVersion = "1.2.3",
            QueryProtocol = "EOS",
            QueryDurationMilliseconds = 27.4,
            QueryPlayers =
            [
                new("Alice", Score: 12, PingMilliseconds: 41),
                new("Bob", PingMilliseconds: 55)
            ]
        };

        var embed = DiscordEmbedRenderer.BuildServerEmbed(server);

        Assert.Contains(embed.Fields, field => field.Name == "Map" && field.Value.ToString() == "TheIsland");
        Assert.Contains(embed.Fields, field => field.Name == "Game / Version" && field.Value.ToString() == "Ark / 1.2.3");
        Assert.Contains(embed.Fields, field => field.Name == "Query Protocol / Duration" && field.Value.ToString() == "EOS | 27 ms");
        Assert.Contains(embed.Fields, field => field.Name == "Player List" && field.Value.ToString()!.Contains("Alice", StringComparison.Ordinal));
    }

    [Fact]
    public void Player_list_neutralizes_mentions_markdown_links_and_newlines()
    {
        var rendered = DiscordEmbedRenderer.FormatPlayerList(
        [
            new("@everyone **admin** `code` [click](https://example.com)\n@here")
        ]);

        Assert.DoesNotContain("@everyone", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("@here", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("**admin**", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("`code`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', rendered);
        Assert.Contains("@\u200Beveryone", rendered, StringComparison.Ordinal);
        Assert.Contains("\\*\\*admin\\*\\*", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_embed_hides_optional_fields_when_disabled()
    {
        var server = CreateServer(ServerRuntimeStatus.Warning) with
        {
            QueryMap = "TheIsland",
            QueryGame = "Ark",
            QueryVersion = "1.2.3",
            QueryProtocol = "EOS",
            QueryDurationMilliseconds = 27.4,
            QueryPlayers = [new("Alice")],
            QueryMessage = "Query failed."
        };
        var options = new DiscordServerCardOptions(
            ShowServerAddress: false,
            ShowPlayerCount: false,
            ShowUptime: false,
            ShowResourceUsage: false,
            ShowMap: false,
            ShowGameVersion: false,
            ShowQuerySummary: false,
            ShowPlayerList: false,
            ShowQueryDiagnostics: false);

        var embed = DiscordEmbedRenderer.BuildServerEmbed(server, options);

        Assert.Equal(["Status", "Game Server"], embed.Fields.Select(field => field.Name));
    }

    [Fact]
    public void Online_server_shows_optional_player_query_failure_detail()
    {
        var server = CreateServer(ServerRuntimeStatus.Running) with
        {
            QueryDetailMessage = "A2S_PLAYER unavailable: timed out after 750 ms.",
            QueryPlayers = []
        };

        var embed = DiscordEmbedRenderer.BuildServerEmbed(server);

        Assert.Contains(
            embed.Fields,
            field => field.Name == "Query Detail" &&
                     field.Value.ToString()!.Contains("A2S_PLAYER unavailable", StringComparison.Ordinal));
    }

    private static InstalledServer CreateServer(ServerRuntimeStatus status)
    {
        return new InstalledServer(
            Id: "1",
            Name: "WindowsGSH Test Server",
            ModuleId: "test",
            Runtime: "steam",
            ServerFolder: @"C:\WindowsGSH\servers\1",
            InstallPath: @"C:\WindowsGSH\servers\1\server",
            ConfigPath: @"C:\WindowsGSH\servers\1\ServerConfig.json",
            IpAddress: "127.0.0.1",
            Port: "27015",
            SteamAppId: "123",
            SteamBranch: string.Empty,
            MaxPlayers: "8",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: status.ToString(),
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: string.Empty,
            LastOperationError: null,
            IsInstalled: true,
            Status: status,
            StatusText: status.ToString(),
            StatusBrushKey: "StatusBrush",
            HasUpdateAvailable: false,
            LocalBuildId: string.Empty,
            RemoteBuildId: string.Empty,
            IgnoredBuildId: string.Empty,
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: status != ServerRuntimeStatus.Running,
            CanStop: status == ServerRuntimeStatus.Running);
    }
}
