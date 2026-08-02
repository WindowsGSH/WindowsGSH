using Discord;
using WindowsGSH.Core.Servers;
using WindowsGSH.Data;
using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(DiscordDataTestCollection.Name)]
public sealed class DiscordPanelServiceTests
{
    [Fact]
    public void GroupServersByDashboardChannel_groups_servers_sharing_the_same_channel()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var serverA = CreateServer("dash-a-" + suffix);
        var serverB = CreateServer("dash-b-" + suffix);
        var channelId = "channel-" + suffix;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverA.Id, "", true, DashboardChannelId: channelId));
            repository.SaveServerSettings(new DiscordServerSettings(serverB.Id, "", true, DashboardChannelId: channelId));

            var groups = DiscordPanelService.GroupServersByDashboardChannel([serverA, serverB], repository);

            var group = Assert.Single(groups);
            Assert.Equal(channelId, group.Key);
            Assert.Equal(2, group.Value.Count);
            Assert.Contains(group.Value, s => s.Id == serverA.Id);
            Assert.Contains(group.Value, s => s.Id == serverB.Id);
        }
        finally
        {
            DeleteServerSettingsRow(serverA.Id);
            DeleteServerSettingsRow(serverB.Id);
        }
    }

    [Fact]
    public void GroupServersByDashboardChannel_puts_different_channels_in_separate_groups()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var serverA = CreateServer("dash-diff-a-" + suffix);
        var serverB = CreateServer("dash-diff-b-" + suffix);

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverA.Id, "", true, DashboardChannelId: "channel-1-" + suffix));
            repository.SaveServerSettings(new DiscordServerSettings(serverB.Id, "", true, DashboardChannelId: "channel-2-" + suffix));

            var groups = DiscordPanelService.GroupServersByDashboardChannel([serverA, serverB], repository);

            Assert.Equal(2, groups.Count);
            Assert.Single(groups["channel-1-" + suffix]);
            Assert.Single(groups["channel-2-" + suffix]);
        }
        finally
        {
            DeleteServerSettingsRow(serverA.Id);
            DeleteServerSettingsRow(serverB.Id);
        }
    }

    [Fact]
    public void GroupServersByDashboardChannel_excludes_servers_with_no_dashboard_channel()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var serverWithout = CreateServer("dash-none-" + suffix);

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverWithout.Id, "", true));

            var groups = DiscordPanelService.GroupServersByDashboardChannel([serverWithout], repository);

            Assert.Empty(groups);
        }
        finally
        {
            DeleteServerSettingsRow(serverWithout.Id);
        }
    }

    [Fact]
    public void GroupServersByDashboardChannel_excludes_a_server_with_no_settings_row_at_all()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var server = CreateServer("dash-no-row-" + Guid.NewGuid().ToString("N"));

        var groups = DiscordPanelService.GroupServersByDashboardChannel([server], repository);

        Assert.Empty(groups);
    }

    [Fact]
    public void GetCardChannelId_prefers_card_channel_id_when_set()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var server = CreateServer("card-preferred-" + Guid.NewGuid().ToString("N"));

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(server.Id, "legacy-name", true, CardChannelId: "card-channel"));

            Assert.Equal("card-channel", DiscordPanelService.GetCardChannelId(server, repository));
        }
        finally
        {
            DeleteServerSettingsRow(server.Id);
        }
    }

    [Fact]
    public void GetCardChannelId_falls_back_to_legacy_channel_name_when_card_channel_id_is_blank()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var server = CreateServer("card-legacy-name-" + Guid.NewGuid().ToString("N"));

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(server.Id, "legacy-name", true));

            Assert.Equal("legacy-name", DiscordPanelService.GetCardChannelId(server, repository));
        }
        finally
        {
            DeleteServerSettingsRow(server.Id);
        }
    }

    [Fact]
    public void GetCardChannelId_falls_back_to_json_discord_channel_when_no_settings_row_exists()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", "DiscordPanelServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "ServerConfig.json");
        File.WriteAllText(configPath, """{ "discord": { "channel": "json-channel" } }""");
        var server = CreateServer("card-json-fallback-" + Guid.NewGuid().ToString("N"), configPath);

        try
        {
            Assert.Equal("json-channel", DiscordPanelService.GetCardChannelId(server, repository));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeServersByResolvedChannelId_merges_different_aliases_of_the_same_channel()
    {
        var serverA = CreateServer("alias-a");
        var serverB = CreateServer("alias-b");
        var groups = new Dictionary<string, IReadOnlyList<InstalledServer>>
        {
            ["123456789"] = [serverA],
            ["game-dashboard"] = [serverB]
        };

        // Both configured values resolve to the same underlying channel (999) - a numeric ID on
        // one server and a name on another.
        var result = DiscordPanelService.MergeServersByResolvedChannelId(
            groups,
            value => value is "123456789" or "game-dashboard" ? 999UL : null);

        var group = Assert.Single(result.Groups);
        Assert.Equal(999UL, group.Key);
        Assert.Equal(2, group.Value.Count);
        Assert.Contains(group.Value, s => s.Id == "alias-a");
        Assert.Contains(group.Value, s => s.Id == "alias-b");
        Assert.Empty(result.UnresolvedConfiguredValues);
    }

    [Fact]
    public void MergeServersByResolvedChannelId_keeps_distinct_resolved_channels_separate()
    {
        var serverA = CreateServer("distinct-a");
        var serverB = CreateServer("distinct-b");
        var groups = new Dictionary<string, IReadOnlyList<InstalledServer>>
        {
            ["channel-one"] = [serverA],
            ["channel-two"] = [serverB]
        };

        var result = DiscordPanelService.MergeServersByResolvedChannelId(
            groups,
            value => value == "channel-one" ? 111UL : value == "channel-two" ? 222UL : null);

        Assert.Equal(2, result.Groups.Count);
        Assert.Single(result.Groups[111UL]);
        Assert.Single(result.Groups[222UL]);
    }

    [Fact]
    public void MergeServersByResolvedChannelId_reports_unresolvable_configured_values()
    {
        var server = CreateServer("unresolved");
        var groups = new Dictionary<string, IReadOnlyList<InstalledServer>>
        {
            ["does-not-exist"] = [server]
        };

        var result = DiscordPanelService.MergeServersByResolvedChannelId(groups, _ => null);

        Assert.Empty(result.Groups);
        Assert.Equal(["does-not-exist"], result.UnresolvedConfiguredValues);
    }

    [Fact]
    public void ClassifyUnresolvedDashboardValues_preserves_numeric_values_and_allows_cleanup()
    {
        var classification = DiscordPanelService.ClassifyUnresolvedDashboardValues(["123456789"]);

        Assert.Contains("dashboard:123456789", classification.KeysToPreserve);
        Assert.True(classification.CanSafelyCleanStale);
    }

    [Fact]
    public void ClassifyUnresolvedDashboardValues_blocks_cleanup_when_a_name_based_value_is_unresolved()
    {
        // A name-based value that failed to resolve this cycle could correspond to any existing
        // dashboard record - we can't tell which, so cleanup must not run at all this cycle
        // (Medium finding: otherwise a temporarily-unresolvable channel gets its record deleted
        // and a duplicate dashboard is posted once it resolves again).
        var classification = DiscordPanelService.ClassifyUnresolvedDashboardValues(["game-dashboard"]);

        Assert.Empty(classification.KeysToPreserve);
        Assert.False(classification.CanSafelyCleanStale);
    }

    [Fact]
    public void ClassifyUnresolvedDashboardValues_blocks_cleanup_if_any_value_is_name_based_even_with_numeric_ones_present()
    {
        var classification = DiscordPanelService.ClassifyUnresolvedDashboardValues(["123456789", "game-dashboard"]);

        Assert.Contains("dashboard:123456789", classification.KeysToPreserve);
        Assert.False(classification.CanSafelyCleanStale);
    }

    [Fact]
    public void ClassifyUnresolvedDashboardValues_allows_cleanup_when_nothing_is_unresolved()
    {
        var classification = DiscordPanelService.ClassifyUnresolvedDashboardValues([]);

        Assert.Empty(classification.KeysToPreserve);
        Assert.True(classification.CanSafelyCleanStale);
    }

    [Fact]
    public void GetCardChannelId_returns_empty_when_nothing_is_configured()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var server = CreateServer("card-nothing-" + Guid.NewGuid().ToString("N"), @"C:\does\not\exist\ServerConfig.json");

        Assert.Equal(string.Empty, DiscordPanelService.GetCardChannelId(server, repository));
    }

    [Fact]
    public void ComputeEmbedFingerprint_matches_for_identical_content_even_with_different_timestamps()
    {
        // BuildDashboardEmbed/BuildServerEmbed both call WithCurrentTimestamp(), so two renders of
        // the same underlying state always carry different Embed.Timestamp values - the whole reason
        // this fingerprint exists is to treat those two renders as "unchanged" anyway (Embed's own
        // Equals/GetHashCode include Timestamp and so can't be used for this).
        var first = new EmbedBuilder()
            .WithTitle("Shennikos Trade Server")
            .WithDescription("Server is currently **ONLINE**")
            .WithColor(Color.Green)
            .WithFooter("Live Status Panel")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
        var second = new EmbedBuilder()
            .WithTitle("Shennikos Trade Server")
            .WithDescription("Server is currently **ONLINE**")
            .WithColor(Color.Green)
            .WithFooter("Live Status Panel")
            .WithTimestamp(DateTimeOffset.UtcNow.AddMinutes(5))
            .Build();

        Assert.NotEqual(first.Timestamp, second.Timestamp);
        Assert.Equal(DiscordPanelService.ComputeEmbedFingerprint(first), DiscordPanelService.ComputeEmbedFingerprint(second));
    }

    [Fact]
    public void ComputeEmbedFingerprint_differs_when_description_changes()
    {
        var online = new EmbedBuilder()
            .WithTitle("Shennikos Trade Server")
            .WithDescription("Server is currently **ONLINE**")
            .WithColor(Color.Green)
            .Build();
        var offline = new EmbedBuilder()
            .WithTitle("Shennikos Trade Server")
            .WithDescription("Server is currently **OFFLINE**")
            .WithColor(Color.Red)
            .Build();

        Assert.NotEqual(DiscordPanelService.ComputeEmbedFingerprint(online), DiscordPanelService.ComputeEmbedFingerprint(offline));
    }

    [Fact]
    public void ComputeEmbedFingerprint_differs_when_a_field_changes()
    {
        var withField = new EmbedBuilder()
            .WithTitle("WindowsGSH Server Status")
            .AddField("Player List", "Alice, Bob", false)
            .Build();
        var withDifferentField = new EmbedBuilder()
            .WithTitle("WindowsGSH Server Status")
            .AddField("Player List", "Alice, Bob, Carol", false)
            .Build();

        Assert.NotEqual(DiscordPanelService.ComputeEmbedFingerprint(withField), DiscordPanelService.ComputeEmbedFingerprint(withDifferentField));
    }

    [Fact]
    public void ComputeServerPanelFingerprint_differs_when_only_the_destination_channel_changes()
    {
        // The exact scenario the reviewed High finding described: a server's Card Channel is
        // reconfigured from one channel to another with everything else about its card completely
        // unchanged. Before this fix, panelKey alone ("server:{id}") didn't encode the channel the
        // way the dashboard case's key does, so this fingerprint had to fold channel.Id in directly
        // or the change would never be detected and the card would stay stuck in the old channel.
        var embed = new EmbedBuilder()
            .WithTitle("Shennikos Trade Server")
            .WithDescription("Server is currently **ONLINE**")
            .WithColor(Color.Green)
            .Build();

        var inChannelA = DiscordPanelService.ComputeServerPanelFingerprint(embed, canStart: false, canStop: true, channelId: 111UL);
        var inChannelB = DiscordPanelService.ComputeServerPanelFingerprint(embed, canStart: false, canStop: true, channelId: 222UL);
        var inChannelAAgain = DiscordPanelService.ComputeServerPanelFingerprint(embed, canStart: false, canStop: true, channelId: 111UL);

        Assert.NotEqual(inChannelA, inChannelB);
        Assert.Equal(inChannelA, inChannelAAgain);
    }

    [Fact]
    public void ComputeServerPanelFingerprint_differs_when_button_enabled_state_changes()
    {
        var embed = new EmbedBuilder().WithTitle("Shennikos Trade Server").Build();

        var canStop = DiscordPanelService.ComputeServerPanelFingerprint(embed, canStart: false, canStop: true, channelId: 111UL);
        var canStart = DiscordPanelService.ComputeServerPanelFingerprint(embed, canStart: true, canStop: false, channelId: 111UL);

        Assert.NotEqual(canStop, canStart);
    }

    [Fact]
    public void ComputeServerPanelFingerprints_treats_only_uptime_and_resource_changes_as_volatile()
    {
        var original = CreateServer("volatile-server") with
        {
            Status = ServerRuntimeStatus.Running,
            CurrentStatusText = "Online",
            Uptime = "1h 5m",
            CpuUsage = "21%",
            MemoryUsage = "1.2 GB"
        };
        var changed = original with
        {
            Uptime = "1h 10m",
            CpuUsage = "47%",
            MemoryUsage = "1.4 GB"
        };
        var options = DiscordServerCardOptions.Default with
        {
            ShowUptime = true,
            ShowResourceUsage = true
        };

        var first = DiscordPanelService.ComputeServerPanelFingerprints(
            DiscordEmbedRenderer.BuildServerEmbed(original, options),
            original,
            options,
            canStart: false,
            canStop: true,
            channelId: 111UL);
        var second = DiscordPanelService.ComputeServerPanelFingerprints(
            DiscordEmbedRenderer.BuildServerEmbed(changed, options),
            changed,
            options,
            canStart: false,
            canStop: true,
            channelId: 111UL);

        Assert.Equal(first.Meaningful, second.Meaningful);
        Assert.NotEqual(first.Exact, second.Exact);
    }

    [Fact]
    public void ComputeServerPanelFingerprints_treats_query_duration_as_volatile_but_keeps_protocol_meaningful()
    {
        var original = CreateServer("query-latency") with
        {
            QueryProtocol = "A2S",
            QueryDurationMilliseconds = 42
        };
        var slower = original with { QueryDurationMilliseconds = 187 };
        var differentProtocol = slower with { QueryProtocol = "GameSpy" };
        var options = DiscordServerCardOptions.Default with { ShowQuerySummary = true };

        var first = DiscordPanelService.ComputeServerPanelFingerprints(
            DiscordEmbedRenderer.BuildServerEmbed(original, options),
            original,
            options,
            canStart: false,
            canStop: true,
            channelId: 111UL);
        var second = DiscordPanelService.ComputeServerPanelFingerprints(
            DiscordEmbedRenderer.BuildServerEmbed(slower, options),
            slower,
            options,
            canStart: false,
            canStop: true,
            channelId: 111UL);
        var third = DiscordPanelService.ComputeServerPanelFingerprints(
            DiscordEmbedRenderer.BuildServerEmbed(differentProtocol, options),
            differentProtocol,
            options,
            canStart: false,
            canStop: true,
            channelId: 111UL);

        Assert.Equal(first.Meaningful, second.Meaningful);
        Assert.NotEqual(first.Exact, second.Exact);
        Assert.NotEqual(second.Meaningful, third.Meaningful);
    }

    [Fact]
    public void ComputeDashboardPanelFingerprints_treats_uptime_as_volatile()
    {
        var original = CreateServer("dashboard-volatile") with
        {
            Status = ServerRuntimeStatus.Running,
            CurrentStatusText = "Online",
            Uptime = "2h 5m"
        };
        var changed = original with { Uptime = "2h 10m" };

        var first = DiscordPanelService.ComputeDashboardPanelFingerprints(
            DiscordEmbedRenderer.BuildDashboardEmbed([original]),
            [original]);
        var second = DiscordPanelService.ComputeDashboardPanelFingerprints(
            DiscordEmbedRenderer.BuildDashboardEmbed([changed]),
            [changed]);

        Assert.Equal(first.Meaningful, second.Meaningful);
        Assert.NotEqual(first.Exact, second.Exact);
    }

    [Fact]
    public void DecidePanelRefresh_throttles_volatile_changes_but_sends_meaningful_changes_immediately()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        AppDatabase.Initialize();
        var service = new DiscordPanelService(
            new DiscordRepository(),
            () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            _ => null,
            utcNow: () => now,
            volatileRefreshInterval: TimeSpan.FromMinutes(15));
        const string panelKey = "server:volatile-cadence";
        var initial = new DiscordPanelService.PanelFingerprints(Meaningful: 10, Exact: 100);

        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, initial));
        service.RecordPanelSent(panelKey, initial);

        now = now.AddMinutes(5);
        var volatileChange = initial with { Exact = 101 };
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Skip, service.DecidePanelRefresh(panelKey, volatileChange));

        now = now.AddMinutes(10);
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, volatileChange));
        service.RecordPanelSent(panelKey, volatileChange);

        now = now.AddMinutes(1);
        var meaningfulChange = new DiscordPanelService.PanelFingerprints(Meaningful: 11, Exact: 102);
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, meaningfulChange));
    }

    [Fact]
    public void DecidePanelRefresh_skips_within_the_window_verifies_only_once_it_elapses_and_always_sends_on_real_change()
    {
        // The reviewed Medium finding (round 1): skipping UpsertPanelMessageAsync entirely on an
        // unchanged fingerprint also skips its GetMessageAsync existence check, so a message someone
        // deletes manually in Discord would never be noticed/recreated for as long as the content
        // stayed unchanged - potentially the application's whole lifetime.
        // The reviewed Medium finding (round 2): the first fix for that forced a *full* send (edit
        // included) once the window elapsed, which just moved the rate-limit bursts from every 5
        // minutes to every 30 rather than eliminating them for genuinely unchanged content.
        // VerifyOnly exists specifically as the third option this test asserts: an existence check
        // without an edit, distinct from both Skip (do nothing) and Send (a real edit/create).
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        AppDatabase.Initialize();
        var service = new DiscordPanelService(
            new DiscordRepository(),
            () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            _ => null,
            utcNow: () => now);
        const int fingerprint = 12345;
        const string panelKey = "server:verify-window-test";

        // No prior send recorded at all - always a real Send (must send/verify at least once).
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, fingerprint));

        service.RecordPanelSent(panelKey, fingerprint);

        // Same fingerprint, well within the verification window - safe to fully skip.
        now = now.AddMinutes(5);
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Skip, service.DecidePanelRefresh(panelKey, fingerprint));

        // Same fingerprint, but the verification window has fully elapsed - VerifyOnly, not Send:
        // check the message still exists, but must not edit content that hasn't actually changed.
        now = now.AddMinutes(30);
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.VerifyOnly, service.DecidePanelRefresh(panelKey, fingerprint));

        // A successful VerifyOnly pass resets only the existence-check window, so an
        // immediately-following check with the same fingerprint goes back to Skip.
        service.RecordPanelSentIfSuccessful(
            panelKey,
            fingerprint,
            DiscordPanelService.PanelUpsertOutcome.Verified);
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Skip, service.DecidePanelRefresh(panelKey, fingerprint));

        // A genuinely different fingerprint is always a real Send, regardless of timing.
        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, fingerprint + 1));
    }

    [Fact]
    public void RecordPanelSentIfSuccessful_does_not_advance_the_cache_on_a_failed_outcome()
    {
        // The exact scenario a review round reported: card content changes, the Discord lookup or
        // ModifyAsync call fails (network blip, API outage, rate limiting), UpsertPanelMessageAsync
        // catches that internally and returns Failed - but both callers used to call RecordPanelSent
        // unconditionally afterward regardless of the outcome, so the failed update got cached as if
        // it had actually been applied. The next refresh cycle would then see the new fingerprint as
        // "already sent," return Skip, and never retry the update that genuinely never happened - the
        // in-code comment claiming "the next refresh cycle retries the edit" was simply false. This
        // test proves the retry actually happens now: after a Failed outcome, DecidePanelRefresh for
        // the same panelKey/fingerprint still returns Send, not Skip.
        AppDatabase.Initialize();
        var service = new DiscordPanelService(
            new DiscordRepository(),
            () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            _ => null);
        const int fingerprint = 54321;
        const string panelKey = "server:failed-outcome-test";

        service.RecordPanelSentIfSuccessful(panelKey, fingerprint, DiscordPanelService.PanelUpsertOutcome.Failed);

        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Send, service.DecidePanelRefresh(panelKey, fingerprint));
    }

    [Fact]
    public void RecordPanelSentIfSuccessful_Verified_preserves_the_actual_content_send_time()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        AppDatabase.Initialize();
        var service = new DiscordPanelService(
            new DiscordRepository(),
            () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            _ => null,
            utcNow: () => now,
            volatileRefreshInterval: TimeSpan.FromMinutes(15));
        const string panelKey = "server:verified-content-time";
        var sent = new DiscordPanelService.PanelFingerprints(Meaningful: 1, Exact: 10);
        service.RecordPanelSent(panelKey, sent);

        now = now.AddMinutes(30);
        Assert.Equal(
            DiscordPanelService.PanelRefreshDecision.VerifyOnly,
            service.DecidePanelRefresh(panelKey, sent));
        service.RecordPanelSentIfSuccessful(
            panelKey,
            sent,
            DiscordPanelService.PanelUpsertOutcome.Verified);

        now = now.AddMinutes(1);
        var volatileChange = sent with { Exact = 11 };
        Assert.Equal(
            DiscordPanelService.PanelRefreshDecision.Send,
            service.DecidePanelRefresh(panelKey, volatileChange));
    }

    [Fact]
    public void RecordPanelSentIfSuccessful_advances_the_cache_on_Updated()
    {
        AssertOutcomeAdvancesCache(DiscordPanelService.PanelUpsertOutcome.Updated);
    }

    [Fact]
    public void RecordPanelSentIfSuccessful_advances_the_cache_on_Created()
    {
        AssertOutcomeAdvancesCache(DiscordPanelService.PanelUpsertOutcome.Created);
    }

    private static void AssertOutcomeAdvancesCache(DiscordPanelService.PanelUpsertOutcome outcome)
    {
        AppDatabase.Initialize();
        var service = new DiscordPanelService(
            new DiscordRepository(),
            () => Task.FromResult<IReadOnlyList<InstalledServer>>([]),
            _ => null);
        const int fingerprint = 98765;
        var panelKey = "server:success-outcome-test-" + outcome;

        service.RecordPanelSentIfSuccessful(panelKey, fingerprint, outcome);

        Assert.Equal(DiscordPanelService.PanelRefreshDecision.Skip, service.DecidePanelRefresh(panelKey, fingerprint));
    }

    private static void DeleteServerSettingsRow(string serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_server_settings WHERE server_id = $serverId;";
        command.Parameters.AddWithValue("$serverId", serverId);
        command.ExecuteNonQuery();
    }

    private static InstalledServer CreateServer(string id, string? configPath = null)
    {
        return new InstalledServer(
            Id: id,
            Name: "Paper",
            ModuleId: "test",
            Runtime: "steam",
            ServerFolder: @"C:\WindowsGSH\servers\" + id,
            InstallPath: @"C:\WindowsGSH\servers\" + id + @"\server",
            ConfigPath: configPath ?? (@"C:\WindowsGSH\servers\" + id + @"\ServerConfig.json"),
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
