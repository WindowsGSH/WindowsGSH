using WindowsGSH.Core.Servers;
using WindowsGSH.Data;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(DiscordDataTestCollection.Name)]
public sealed class DiscordBotHostCommandChannelFilteringTests
{
    [Fact]
    public void IsChannelAllowed_accepts_any_channel_when_no_alert_channels_are_configured()
    {
        // Tier 2 Chunk 6: no Alert Channels configured anywhere yet - first-run safety, so a
        // fresh install (or one mid-migration through Chunk 2's backfill) is never locked out.
        Assert.True(DiscordBotHost.IsChannelAllowed([], [], 111UL));
    }

    [Fact]
    public void IsChannelAllowed_accepts_a_channel_present_in_the_resolved_allow_list()
    {
        Assert.True(DiscordBotHost.IsChannelAllowed(["111"], [111UL], 111UL));
    }

    [Fact]
    public void IsChannelAllowed_rejects_a_channel_not_in_a_non_empty_resolved_allow_list()
    {
        Assert.False(DiscordBotHost.IsChannelAllowed(["111"], [111UL], 333UL));
    }

    [Fact]
    public void IsChannelAllowed_fails_closed_when_configured_but_none_resolved()
    {
        // A named Alert Channel that no longer exists (renamed/deleted) must not silently fall
        // back to "accept anywhere" just because the resolved set ended up empty - the raw
        // configured count is what decides "nothing configured" vs. "configured but unresolved."
        Assert.False(DiscordBotHost.IsChannelAllowed(["server-alerts"], [], 111UL));
    }

    [Fact]
    public void ResolveAllowedChannelIds_uses_whatever_the_resolver_returns_for_each_configured_value()
    {
        // The resolver argument stands in for ResolveChannel in production, which already
        // handles both numeric IDs and names itself - ResolveAllowedChannelIds no longer does any
        // numeric-vs-name special-casing of its own, it just delegates every value through.
        var resolved = DiscordBotHost.ResolveAllowedChannelIds(
            ["123456789012345678", "server-alerts"],
            value => value switch
            {
                "123456789012345678" => 123456789012345678UL,
                "server-alerts" => 999UL,
                _ => null
            });

        Assert.Contains(123456789012345678UL, resolved);
        Assert.Contains(999UL, resolved);
    }

    [Fact]
    public void ResolveAllowedChannelIds_drops_a_configured_value_that_does_not_resolve()
    {
        var resolved = DiscordBotHost.ResolveAllowedChannelIds(["deleted-channel"], _ => null);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Same_named_channel_in_a_different_guild_is_rejected_because_resolution_is_bot_wide()
    {
        // Medium/High finding: resolving names per-guild (the first version of this fix) would
        // authorize commands from *every* guild that happened to contain a channel named
        // "server-alerts", even guilds the configuration never intended - because the stored
        // value has no guild identity of its own. Resolving through the same bot-wide
        // ResolveChannel used for alert delivery closes that: it always returns exactly one
        // channel ID for a given name (whichever guild it finds first, same as alert posting),
        // so a *different* channel that merely shares the same literal name in another guild is
        // never in the resolved set and is correctly rejected - "the command source is whatever
        // channel alerts actually go to," not "any channel with a matching name anywhere."
        var configured = new[] { "server-alerts" };
        Func<string, ulong?> resolveChannelId = value => value == "server-alerts" ? 111UL : null;

        var resolvedAllowedChannelIds = DiscordBotHost.ResolveAllowedChannelIds(configured, resolveChannelId);

        Assert.True(DiscordBotHost.IsChannelAllowed(configured, resolvedAllowedChannelIds, 111UL));
        Assert.False(DiscordBotHost.IsChannelAllowed(configured, resolvedAllowedChannelIds, 222UL));
    }

    [Fact]
    public void Numeric_configured_channel_is_accepted_directly()
    {
        var configured = new[] { "123456789012345678" };
        Func<string, ulong?> resolveChannelId = value => ulong.TryParse(value, out var id) ? id : null;

        var resolvedAllowedChannelIds = DiscordBotHost.ResolveAllowedChannelIds(configured, resolveChannelId);

        Assert.True(DiscordBotHost.IsChannelAllowed(configured, resolvedAllowedChannelIds, 123456789012345678UL));
        Assert.False(DiscordBotHost.IsChannelAllowed(configured, resolvedAllowedChannelIds, 1UL));
    }

    [Fact]
    public void IsServerCommandChannelAllowed_accepts_anywhere_when_nothing_is_configured_anywhere()
    {
        Assert.True(DiscordBotHost.IsServerCommandChannelAllowed(
            anyAlertChannelsConfigured: false,
            targetServerAlertChannelValue: null,
            resolveChannelId: _ => null,
            channelId: 111UL));
    }

    [Fact]
    public void IsServerCommandChannelAllowed_accepts_the_target_servers_own_resolved_channel()
    {
        Assert.True(DiscordBotHost.IsServerCommandChannelAllowed(
            anyAlertChannelsConfigured: true,
            targetServerAlertChannelValue: "111",
            resolveChannelId: value => value == "111" ? 111UL : null,
            channelId: 111UL));
    }

    [Fact]
    public void IsServerCommandChannelAllowed_rejects_a_different_servers_channel()
    {
        // P2 finding: !wgsh restart server-a sent in server B's Alert Channel must be rejected -
        // server A's own configured channel (111) doesn't match the incoming channel (222), even
        // though 222 is a perfectly valid Alert Channel for some other server.
        Assert.False(DiscordBotHost.IsServerCommandChannelAllowed(
            anyAlertChannelsConfigured: true,
            targetServerAlertChannelValue: "111",
            resolveChannelId: value => value == "111" ? 111UL : null,
            channelId: 222UL));
    }

    [Fact]
    public void IsServerCommandChannelAllowed_fails_closed_when_the_target_server_has_no_alert_channel_of_its_own()
    {
        // Once any Alert Channel exists anywhere, a server with none configured for itself must
        // not be treated as unrestricted - that would let its commands through from any channel,
        // defeating the whole per-server restriction for that one server.
        Assert.False(DiscordBotHost.IsServerCommandChannelAllowed(
            anyAlertChannelsConfigured: true,
            targetServerAlertChannelValue: null,
            resolveChannelId: _ => null,
            channelId: 111UL));
    }

    [Fact]
    public void IsServerCommandChannelAllowed_rejects_when_the_target_servers_channel_does_not_resolve()
    {
        Assert.False(DiscordBotHost.IsServerCommandChannelAllowed(
            anyAlertChannelsConfigured: true,
            targetServerAlertChannelValue: "deleted-channel",
            resolveChannelId: _ => null,
            channelId: 111UL));
    }

    [Fact]
    public void ResolveTargetServer_matches_by_display_name_case_insensitively()
    {
        // Medium finding: normal command execution (FindServerAsync) accepts a server's display
        // name, not just its ID, case-insensitively - channel filtering must resolve the same way
        // or a perfectly valid "!wgsh status My Server" gets wrongly treated as an unknown target.
        var server = CreateServer("server-a", name: "My Server");

        var resolved = DiscordBotHost.ResolveTargetServer([server], "my server");

        Assert.Equal("server-a", resolved?.Id);
    }

    [Fact]
    public void ResolveTargetServer_matches_by_id_case_insensitively()
    {
        var server = CreateServer("server-a", name: "My Server");

        var resolved = DiscordBotHost.ResolveTargetServer([server], "SERVER-A");

        Assert.Equal("server-a", resolved?.Id);
    }

    [Fact]
    public void ResolveTargetServer_returns_null_for_an_unknown_target()
    {
        var server = CreateServer("server-a", name: "My Server");

        var resolved = DiscordBotHost.ResolveTargetServer([server], "does-not-exist");

        Assert.Null(resolved);
    }

    [Fact]
    public async Task IsCommandChannelAllowedAsync_bypasses_filtering_for_an_unknown_server()
    {
        // Low/Medium finding: a mistyped or nonexistent server target isn't a channel-permission
        // question - it must be let through so the normal "server not found" response can run,
        // instead of a misleading "commands are not accepted in this channel" rejection.
        AppDatabase.Initialize();
        var host = new DiscordBotHost(
            getServersAsync: () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            runServerActionAsync: (_, _, _) => Task.FromResult(string.Empty));

        var allowed = await host.IsCommandChannelAllowedAsync("does-not-exist", 111UL);

        Assert.True(allowed);
    }

    [Fact]
    public async Task IsCommandChannelAllowedAsync_resolves_target_by_name_before_failing_closed()
    {
        // Combines the Medium finding (name resolution) with the earlier fail-closed policy:
        // the target is specified by its display name ("My Server", differently cased), must
        // resolve to the real server "server-a", and since that server has no Alert Channel of
        // its own while channel restrictions are active elsewhere, the command is rejected - not
        // because the name failed to resolve, but because that specific server hasn't opted in.
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var serverA = "server-a-" + suffix;
        var serverB = "server-b-" + suffix;
        var server = CreateServer(serverA, name: "My Server");

        try
        {
            // Some Alert Channel is configured somewhere (server B's), so restrictions are active,
            // but server A itself has none.
            repository.SaveServerSettings(new DiscordServerSettings(serverB, "", false, AlertChannelId: "999"));

            var host = new DiscordBotHost(
                getServersAsync: () => Task.FromResult<IReadOnlyList<InstalledServer>>([server]),
                runServerActionAsync: (_, _, _) => Task.FromResult(string.Empty));

            var allowed = await host.IsCommandChannelAllowedAsync("MY SERVER", 111UL);

            Assert.False(allowed);
        }
        finally
        {
            DeleteServerSettingsRow(serverA);
            DeleteServerSettingsRow(serverB);
        }
    }

    private static void DeleteServerSettingsRow(string serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_server_settings WHERE server_id = $serverId;";
        command.Parameters.AddWithValue("$serverId", serverId);
        command.ExecuteNonQuery();
    }

    private static InstalledServer CreateServer(string id, string name)
    {
        return new InstalledServer(
            Id: id,
            Name: name,
            ModuleId: "test",
            Runtime: "steam",
            ServerFolder: @"C:\WindowsGSH\servers\" + id,
            InstallPath: @"C:\WindowsGSH\servers\" + id + @"\server",
            ConfigPath: @"C:\WindowsGSH\servers\" + id + @"\ServerConfig.json",
            IpAddress: "127.0.0.1",
            Port: "27015",
            SteamAppId: "123",
            SteamBranch: string.Empty,
            MaxPlayers: "8",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: "Offline",
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: string.Empty,
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Offline,
            StatusText: "Offline",
            StatusBrushKey: "StatusBrush",
            HasUpdateAvailable: false,
            LocalBuildId: string.Empty,
            RemoteBuildId: string.Empty,
            IgnoredBuildId: string.Empty,
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: true,
            CanStop: false);
    }
}
