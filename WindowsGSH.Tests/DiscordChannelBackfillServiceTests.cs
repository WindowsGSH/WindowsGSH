using System.Text.Json.Nodes;
using WindowsGSH.Core.Events;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(DiscordDataTestCollection.Name)]
public sealed class DiscordChannelBackfillServiceTests
{
    // A path that never exists, passed to every call below so these tests never scan the real
    // servers directory on the machine running them (Backfill's JSON-only-server scan is a no-op
    // when the directory doesn't exist).
    private static string NoServersRoot => Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", "NoServers", Guid.NewGuid().ToString("N"));

    // A fresh, never-before-used marker key per test, so the one-time legacy-global-backfill gate
    // (shared across the whole real DB, by design - see DiscordChannelBackfillService's own docs)
    // doesn't leak between tests or get tripped by whatever the real app's own marker already is.
    // Every test captures this once into a local variable and deletes that row in its own
    // `finally`, so repeated test runs don't accumulate garbage app_settings rows forever.
    private static string NoMarkerYet => "test-marker-" + Guid.NewGuid().ToString("N");

    // Simulates "this is the very first launch where schema v9 exists" - v9 applied just now,
    // this simulated process started a moment earlier - the window where the one-time migration
    // loop is safe to run. Every test below except the upgrade-path ones uses this; on the real
    // shared dev DB, schema v9 was applied long ago, so without this override every test here
    // would otherwise be treated as an upgrade and the loop would never run.
    private static DateTimeOffset FreshSchemaV9AppliedUtc => DateTimeOffset.UtcNow;
    private static DateTimeOffset FreshProcessStartUtc => DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public void Backfill_sets_alert_and_dashboard_channel_from_globals_when_blank()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: true));

            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.Contains(serverId, result.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.Equal("global-dashboard", saved!.DashboardChannelId);
            Assert.Equal("global-alert", saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_does_not_set_dashboard_channel_when_include_on_dashboard_is_false()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-no-dash-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: false));

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            var saved = repository.GetServerSettings(serverId);
            Assert.Null(saved!.DashboardChannelId);
            Assert.Equal("global-alert", saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_never_overwrites_an_already_set_per_server_channel()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-preset-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(
                serverId, "chan", IncludeOnDashboard: true, AlertChannelId: "server-specific-alert", DashboardChannelId: "server-specific-dash"));

            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.DoesNotContain(serverId, result.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.Equal("server-specific-alert", saved!.AlertChannelId);
            Assert.Equal("server-specific-dash", saved.DashboardChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_is_idempotent_across_repeated_runs()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-idempotent-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: true));

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);
            var secondRun = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.DoesNotContain(serverId, secondRun.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.Equal("global-dashboard", saved!.DashboardChannelId);
            Assert.Equal("global-alert", saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_does_not_repopulate_a_deliberately_cleared_alert_channel_on_a_later_run()
    {
        // Reproduces the exact High-severity regression the one-time marker exists to prevent:
        // migration completes once, a user then deliberately clears one server's own Alert
        // Channel (to opt it out of alerts/commands there), and a later startup - still passing
        // the same nonblank Application Alert Channel, exactly as a real restart would - must not
        // silently repopulate it.
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-intentional-blank-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = NoServersRoot;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: true));

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);
            Assert.Equal("global-alert", repository.GetServerSettings(serverId)!.AlertChannelId);

            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", true, AlertChannelId: null));
            Assert.Null(repository.GetServerSettings(serverId)!.AlertChannelId);

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.Null(repository.GetServerSettings(serverId)!.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_does_not_run_the_global_copy_on_first_launch_after_upgrading_from_a_pre_existing_schema()
    {
        // P1 finding: an install that already went through the earlier, unmarked versions of this
        // method has no marker yet either - schema v9 (and the old marker-less, repeated-every-
        // startup copy) already existed for at least one full prior session by the time this
        // fixed version's marker is checked for the first time. If a user cleared a server's
        // Alert Channel on that old code and then upgraded, the very first launch of this version
        // must NOT run the global copy "one more time" - that would undo the deliberate change at
        // exactly the moment the user upgraded to get the fix. Simulated here by making
        // schemaV9AppliedUtc long before processStartUtc (the opposite of every other test above).
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-upgrade-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            // Simulates: user cleared this server's Alert Channel on the old, unmarked code.
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", true, AlertChannelId: null));

            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: DateTimeOffset.UtcNow.AddDays(-30), processStartUtc: DateTimeOffset.UtcNow);

            Assert.DoesNotContain(serverId, result.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.Null(saved!.AlertChannelId);
            Assert.Null(saved.DashboardChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void IsFreshLegacyRoutingMigrationWindow_is_true_when_schema_applied_at_or_after_process_start()
    {
        var processStart = DateTimeOffset.UtcNow;
        Assert.True(DiscordChannelBackfillService.IsFreshLegacyRoutingMigrationWindow(processStart, processStart));
        Assert.True(DiscordChannelBackfillService.IsFreshLegacyRoutingMigrationWindow(processStart.AddSeconds(1), processStart));
    }

    [Fact]
    public void IsFreshLegacyRoutingMigrationWindow_is_false_when_schema_applied_before_process_start()
    {
        var processStart = DateTimeOffset.UtcNow;
        Assert.False(DiscordChannelBackfillService.IsFreshLegacyRoutingMigrationWindow(processStart.AddDays(-1), processStart));
    }

    [Fact]
    public void IsFreshLegacyRoutingMigrationWindow_is_true_when_schema_was_never_applied()
    {
        Assert.True(DiscordChannelBackfillService.IsFreshLegacyRoutingMigrationWindow(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Backfill_does_nothing_when_global_settings_are_blank()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-no-globals-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: true));

            var result = DiscordChannelBackfillService.Backfill(
                repository, globalDashboardChannelId: null, globalNotificationsChannelId: "",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.False(result.RanAnyBackfill);
            var saved = repository.GetServerSettings(serverId);
            Assert.Null(saved!.DashboardChannelId);
            Assert.Null(saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_publishes_a_server_log_event_per_backfilled_server()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "backfill-log-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var bus = new WindowsGshEventBus();
        var logEvents = new List<ServerLogEvent>();
        bus.Subscribe<ServerLogEvent>(logEvents.Add);

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", IncludeOnDashboard: true));

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert", bus, NoServersRoot,
                legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            var entry = Assert.Single(logEvents, e => e.ServerId == serverId);
            Assert.Equal("Discord", entry.Category);
            Assert.Contains("global-dashboard", entry.Message);
            Assert.Contains("global-alert", entry.Message);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
        }
    }

    [Fact]
    public void Backfill_creates_settings_row_from_legacy_json_channel_for_alert_and_card()
    {
        // Regression coverage: a server with a legacy discord.channel in its own JSON config but
        // no discord_server_settings row must still get AlertChannelId populated, or
        // DiscordBotHost.GetAlertChannelId (which has no JSON fallback) silently drops every alert
        // for it forever.
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "json-only-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = CreateServerWithConfig(serverId, new JsonObject
        {
            ["id"] = serverId,
            ["discord"] = new JsonObject { ["channel"] = "legacy-channel-123", ["includeOnDashboard"] = true }
        });

        try
        {
            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.Contains(serverId, result.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.NotNull(saved);
            Assert.Equal("legacy-channel-123", saved!.ChannelName);
            Assert.Equal("legacy-channel-123", saved.CardChannelId);
            Assert.Equal("legacy-channel-123", saved.AlertChannelId);
            // IncludeOnDashboard is true, so the newly-created row is seeded with the legacy
            // global Dashboard Channel directly (see CreateRowsForJsonOnlyServers) - not via the
            // separate marker-gated loop, which this same call also runs since it's the first use
            // of this fresh marker key.
            Assert.Equal("global-dashboard", saved.DashboardChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    [Fact]
    public void Backfill_seeds_dashboard_channel_for_a_json_only_server_discovered_after_the_marker_is_set()
    {
        // Medium finding: a legacy server imported *after* the one-time migration has already
        // completed must still get its Dashboard Channel seeded from the legacy global setting -
        // this is that server's own first-ever migration moment, not a repeat of the bug the
        // marker exists to prevent (a brand new row can't have had a value the user deliberately
        // cleared).
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "json-late-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = CreateServerWithConfig(serverId, new JsonObject
        {
            ["id"] = serverId,
            ["discord"] = new JsonObject { ["channel"] = "legacy-channel-456", ["includeOnDashboard"] = true }
        });

        try
        {
            // Complete the one-time migration first, with no servers present yet, so the marker
            // is already set by the time this server is "imported."
            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: NoServersRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.Contains(serverId, result.BackfilledServerIds);
            var saved = repository.GetServerSettings(serverId);
            Assert.Equal("global-dashboard", saved!.DashboardChannelId);
            Assert.Equal("legacy-channel-456", saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    [Fact]
    public void Backfill_skips_json_only_server_with_no_discord_channel_value()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "json-blank-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = CreateServerWithConfig(serverId, new JsonObject
        {
            ["id"] = serverId,
            ["discord"] = new JsonObject { ["channel"] = "", ["includeOnDashboard"] = true }
        });

        try
        {
            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.DoesNotContain(serverId, result.BackfilledServerIds);
            Assert.Null(repository.GetServerSettings(serverId));
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    [Fact]
    public void Backfill_skips_json_only_server_with_no_discord_section()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "no-discord-section-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = CreateServerWithConfig(serverId, new JsonObject { ["id"] = serverId });

        try
        {
            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.DoesNotContain(serverId, result.BackfilledServerIds);
            Assert.Null(repository.GetServerSettings(serverId));
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    [Fact]
    public void Backfill_does_not_touch_json_config_when_a_db_row_already_exists()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "already-has-row-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = CreateServerWithConfig(serverId, new JsonObject
        {
            ["id"] = serverId,
            ["discord"] = new JsonObject { ["channel"] = "json-channel", ["includeOnDashboard"] = true }
        });

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "db-channel", true, AlertChannelId: "db-alert"));

            DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            var saved = repository.GetServerSettings(serverId);
            Assert.Equal("db-channel", saved!.ChannelName);
            Assert.Equal("db-alert", saved.AlertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    [Fact]
    public void Backfill_skips_a_server_folder_with_malformed_config_json()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "malformed-" + Guid.NewGuid().ToString("N");
        var markerKey = NoMarkerYet;
        var serversRoot = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", "JsonOnlyServers", Guid.NewGuid().ToString("N"));
        var serverFolder = Path.Combine(serversRoot, serverId);
        Directory.CreateDirectory(serverFolder);
        File.WriteAllText(Path.Combine(serverFolder, "ServerConfig.json"), "{ not valid json");

        try
        {
            var result = DiscordChannelBackfillService.Backfill(
                repository, "global-dashboard", "global-alert",
                serversRootPath: serversRoot, legacyBackfillCompletedKey: markerKey,
                schemaV9AppliedUtc: FreshSchemaV9AppliedUtc, processStartUtc: FreshProcessStartUtc);

            Assert.False(result.RanAnyBackfill);
            Assert.Null(repository.GetServerSettings(serverId));
        }
        finally
        {
            DeleteAppSettingsRow(markerKey);
            Directory.Delete(serversRoot, recursive: true);
        }
    }

    private static string CreateServerWithConfig(string serverId, JsonObject configRoot)
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", "JsonOnlyServers", Guid.NewGuid().ToString("N"));
        var serverFolder = Path.Combine(root, serverId);
        Directory.CreateDirectory(serverFolder);
        File.WriteAllText(Path.Combine(serverFolder, "ServerConfig.json"), configRoot.ToJsonString());
        return root;
    }

    private static void DeleteServerSettingsRow(string serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_server_settings WHERE server_id = $serverId;";
        command.Parameters.AddWithValue("$serverId", serverId);
        command.ExecuteNonQuery();
    }

    private static void DeleteAppSettingsRow(string key)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }
}
