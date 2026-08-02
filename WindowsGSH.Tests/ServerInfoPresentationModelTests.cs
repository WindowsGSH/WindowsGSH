using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerInfoPresentationModelTests
{
    [Fact]
    public void Rich_query_formats_details_diagnostics_and_player_rows()
    {
        var query = new QueryResult(
            ModuleServerStatus.Online,
            OnlinePlayers: 2,
            MaxPlayers: 20,
            Version: "1.2.3",
            Message: "A2S responded.",
            Map: "de_dust2",
            Game: "Counter-Strike 2",
            QueryDurationMilliseconds: 27.4,
            Players:
            [
                new("Alice", Score: 12, DurationSeconds: 90.5, PingMilliseconds: 41),
                new("Bob", DurationSeconds: 3700)
            ],
            DetailMessage: "Player query complete.",
            Protocol: "A2S");

        var model = ServerInfoPresentationModel.FromQuery(query, configuredMaxPlayers: 10, supportsQuery: true);

        Assert.Equal("2 / 20", model.PlayersText);
        Assert.Equal("de_dust2", model.MapText);
        Assert.Equal("Counter-Strike 2 / 1.2.3", model.GameVersionText);
        Assert.Equal("A2S", model.ProtocolText);
        Assert.Equal("27 ms", model.QueryDurationText);
        Assert.Equal("Query online", model.QueryStateText);
        Assert.Contains("A2S responded.", model.DiagnosticText);
        Assert.Contains("Player query complete.", model.DiagnosticText);
        Assert.True(model.HasDiagnostic);
        Assert.Collection(
            model.Players,
            player =>
            {
                Assert.Equal("Alice", player.Name);
                Assert.Equal("12", player.ScoreText);
                Assert.Equal("41 ms", player.PingText);
                Assert.Equal("1m 30s", player.DurationText);
            },
            player => Assert.Equal("1h 1m", player.DurationText));
    }

    [Fact]
    public void Unsupported_query_has_clear_empty_state()
    {
        var model = ServerInfoPresentationModel.FromQuery(
            query: null,
            configuredMaxPlayers: 16,
            supportsQuery: false);

        Assert.Equal("-- / 16", model.PlayersText);
        Assert.Equal("This module does not support server queries.", model.QueryStateText);
        Assert.Equal("Player data is unavailable because querying is not supported.", model.PlayerListStateText);
        Assert.False(model.HasPlayers);
        Assert.False(model.HasDiagnostic);
    }

    [Fact]
    public void Query_failure_is_shown_as_diagnostic()
    {
        var model = ServerInfoPresentationModel.FromQuery(
            query: null,
            configuredMaxPlayers: null,
            supportsQuery: true,
            failureMessage: "Query timed out.");

        Assert.Equal("Query unavailable", model.QueryStateText);
        Assert.Equal("Query timed out.", model.DiagnosticText);
        Assert.True(model.HasDiagnostic);
        Assert.False(model.HasPlayers);
    }

    [Fact]
    public void Online_count_without_player_rows_explains_partial_result()
    {
        var model = ServerInfoPresentationModel.FromQuery(
            new QueryResult(ModuleServerStatus.Online, OnlinePlayers: 3, MaxPlayers: 10),
            configuredMaxPlayers: null,
            supportsQuery: true);

        Assert.Equal("The server reported players online but did not return player rows.", model.PlayerListStateText);
    }
}
