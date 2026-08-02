using WindowsGSH.Core.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordSlashCommandTests
{
    [Fact]
    public void Catalog_has_unique_supported_commands()
    {
        var names = DiscordSlashCommands.Definitions.Select(command => command.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("list", names);
        Assert.Contains("status", names);
        Assert.Contains("stats", names);
        Assert.Contains("logs", names);
        Assert.Contains("start", names);
        Assert.Contains("stop", names);
        Assert.Contains("restart", names);
        Assert.Contains("update", names);
        Assert.Contains("backup", names);
        Assert.Contains("send", names);
        Assert.Contains("sendr", names);
        Assert.Contains("stopall", names);
    }

    [Fact]
    public void Server_action_routes_server_and_command_text_to_existing_arguments()
    {
        var result = DiscordSlashCommandRequest.FromOptions(
            "sendr",
            new Dictionary<string, object?>
            {
                ["server"] = "server-2",
                ["command"] = "status players"
            });

        Assert.True(result.IsValid);
        Assert.Equal("sendr", result.Command);
        Assert.Equal(["server-2", "status players"], result.Arguments);
    }

    [Fact]
    public void Required_server_is_validated()
    {
        var result = DiscordSlashCommandRequest.FromOptions(
            "restart",
            new Dictionary<string, object?>());

        Assert.False(result.IsValid);
        Assert.Equal("A server is required for this command.", result.ErrorMessage);
    }

    [Fact]
    public void Stop_all_confirmation_maps_to_existing_confirmation_argument()
    {
        var result = DiscordSlashCommandRequest.FromOptions(
            "stopall",
            new Dictionary<string, object?> { ["confirm"] = true });

        Assert.True(result.IsValid);
        Assert.Equal(["confirm"], result.Arguments);
    }

    [Fact]
    public void Unknown_command_is_rejected()
    {
        var result = DiscordSlashCommandRequest.FromOptions(
            "destroy-everything",
            new Dictionary<string, object?>());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Autocomplete_only_returns_servers_the_admin_can_access()
    {
        var servers = new[]
        {
            new DiscordServerAutocompleteItem("1", "Alpha"),
            new DiscordServerAutocompleteItem("2", "Bravo"),
            new DiscordServerAutocompleteItem("3", "Charlie")
        };

        var result = DiscordServerAutocomplete.Filter(servers, ["2"], null);

        Assert.Equal(["2"], result.Select(server => server.Id));
    }

    [Fact]
    public void Global_admin_autocomplete_matches_name_or_id()
    {
        var servers = new[]
        {
            new DiscordServerAutocompleteItem("ark-1", "Island"),
            new DiscordServerAutocompleteItem("mc-2", "Minecraft"),
            new DiscordServerAutocompleteItem("pz-3", "Knox County")
        };

        var byName = DiscordServerAutocomplete.Filter(servers, ["0"], "mine");
        var byId = DiscordServerAutocomplete.Filter(servers, ["0"], "pz-");

        Assert.Equal(["mc-2"], byName.Select(server => server.Id));
        Assert.Equal(["pz-3"], byId.Select(server => server.Id));
    }

    [Fact]
    public void Autocomplete_is_bounded_to_discord_limit()
    {
        var servers = Enumerable.Range(1, 40)
            .Select(index => new DiscordServerAutocompleteItem(index.ToString(), $"Server {index:00}"));

        var result = DiscordServerAutocomplete.Filter(servers, ["0"], null, maximumResults: 100);

        Assert.Equal(DiscordServerAutocomplete.MaximumResults, result.Count);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("update")]
    [InlineData("backup")]
    [InlineData("send")]
    [InlineData("sendr")]
    public void Destructive_server_commands_run_in_background(string command)
    {
        Assert.True(DiscordSlashCommands.RunsInBackground(command, ["server-1"]));
    }

    [Fact]
    public void Stop_all_only_runs_in_background_after_confirmation()
    {
        Assert.False(DiscordSlashCommands.RunsInBackground("stopall", []));
        Assert.True(DiscordSlashCommands.RunsInBackground("stopall", ["confirm"]));
    }

    [Fact]
    public void Accepted_message_identifies_the_target_server()
    {
        var message = DiscordSlashCommands.GetAcceptedMessage("backup", ["server-7"]);

        Assert.Contains("backup", message);
        Assert.Contains("server-7", message);
    }
}
