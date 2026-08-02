using System.Text;
using System.Text.Json;
using WindowsGSH.Core.Query;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class QueryClientParsingTests
{
    [Fact]
    public void A2s_info_parser_preserves_map_game_and_version()
    {
        var response = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17 };
        AddString(response, "Test Server");
        AddString(response, "de_dust2");
        AddString(response, "csgo");
        AddString(response, "Counter-Strike 2");
        response.AddRange(BitConverter.GetBytes((short)730));
        response.AddRange([3, 20, 0, (byte)'d', (byte)'w', 0, 1]);
        AddString(response, "1.40.0");

        var info = SourceA2sClient.ParseInfo(response.ToArray());

        Assert.Equal("de_dust2", info.Map);
        Assert.Equal("Counter-Strike 2", info.Game);
        Assert.Equal("1.40.0", info.Version);
        Assert.Equal(3, info.Players);
        Assert.Equal(20, info.MaxPlayers);
    }

    [Fact]
    public void A2s_player_parser_returns_player_rows()
    {
        var response = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, 0x44, 2, 0 };
        AddString(response, "Alice");
        response.AddRange(BitConverter.GetBytes(12));
        response.AddRange(BitConverter.GetBytes(90.5f));
        response.Add(1);
        AddString(response, "Bob");
        response.AddRange(BitConverter.GetBytes(-2));
        response.AddRange(BitConverter.GetBytes(15f));

        var players = SourceA2sClient.ParsePlayers(response.ToArray());

        Assert.Collection(
            players,
            player =>
            {
                Assert.Equal("Alice", player.Name);
                Assert.Equal(12, player.Score);
                Assert.Equal(90.5, player.DurationSeconds);
            },
            player =>
            {
                Assert.Equal("Bob", player.Name);
                Assert.Equal(-2, player.Score);
            });
    }

    [Fact]
    public void A2s_player_query_uses_separate_bounded_timeout()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(750), SourceA2sClient.GetPlayerQueryTimeout(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromMilliseconds(300), SourceA2sClient.GetPlayerQueryTimeout(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void A2s_player_parser_reports_unsupported_split_packets_clearly()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SourceA2sClient.ParsePlayers([0xFE, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]));

        Assert.Contains("split packet", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported yet", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FiveM_response_mapping_preserves_players_and_detail()
    {
        var dynamicInfo = new Dictionary<string, object?>
        {
            ["hostname"] = "FiveM Test",
            ["mapname"] = "Los Santos",
            ["clients"] = 2,
            ["sv_maxclients"] = 32,
            ["version"] = "build-1"
        };
        var players = new[]
        {
            new { Name = "Alice", Ping = 42, Score = 5 },
            new { Name = "Bob", Ping = 55, Score = 1 }
        };

        var info = FiveMQueryClient.FromResponses(dynamicInfo, players, 12.4);

        Assert.Equal("Los Santos", info.Map);
        Assert.Equal(2, info.PlayerRows.Count);
        Assert.Equal(42, info.PlayerRows[0].PingMilliseconds);
        Assert.Equal(12.4, info.QueryDurationMilliseconds);
        Assert.Contains("hostname", info.DetailMessage);
    }

    [Fact]
    public void Eos_response_mapping_keeps_missing_player_rows_optional()
    {
        using var document = JsonDocument.Parse("""
            {
              "SESSIONNAME_s": "Ark Test",
              "MAPNAME_s": "TheIsland",
              "PLAYERS_l": 4,
              "MAXPLAYERS_l": 70,
              "BUILDID_s": "12345"
            }
            """);
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());

        var info = EosQueryClient.FromResponse(values, 25);

        Assert.Equal("TheIsland", info.Map);
        Assert.Equal(4, info.Players);
        Assert.Equal(70, info.MaxPlayers);
        Assert.Equal("12345", info.Version);
        Assert.Contains("MAPNAME_s", info.DetailMessage);
    }

    [Fact]
    public void FiveM_response_mapping_reads_json_array_players_with_invariant_numbers()
    {
        using var dynamicDocument = JsonDocument.Parse("""{ "clients": "1", "sv_maxclients": "32" }""");
        using var playerDocument = JsonDocument.Parse("""
            [
              {
                "name": "Alice",
                "score": "12",
                "ping": "42",
                "duration": "90.5",
                "id": "player-1"
              }
            ]
            """);

        var info = FiveMQueryClient.FromResponses(
            dynamicDocument.RootElement.Clone(),
            playerDocument.RootElement.Clone(),
            10);

        var player = Assert.Single(info.PlayerRows);
        Assert.Equal("Alice", player.Name);
        Assert.Equal(12, player.Score);
        Assert.Equal(42, player.PingMilliseconds);
        Assert.Equal(90.5, player.DurationSeconds);
        Assert.Equal("player-1", player.Id);
    }

    [Fact]
    public void Unreal_response_mapping_preserves_game_latency_and_players()
    {
        var details = new
        {
            ServerName = "UT Test",
            MapName = "DM-Deck",
            NumPlayers = 1,
            MaxPlayers = 16,
            GameType = "DeathMatch",
            ServerId = "451"
        };
        var players = new[] { new { Id = 7, Name = "Alice", Ping = 31, Score = 9 } };

        var info = Unreal2QueryClient.FromResponses(details, players, 18.8);

        Assert.Equal("DM-Deck", info.Map);
        Assert.Equal("DeathMatch", info.Game);
        Assert.Equal("451", info.Version);
        Assert.Equal(31, Assert.Single(info.PlayerRows).PingMilliseconds);
    }

    private static void AddString(List<byte> target, string value)
    {
        target.AddRange(Encoding.UTF8.GetBytes(value));
        target.Add(0);
    }
}
