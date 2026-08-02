using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(DiscordDataTestCollection.Name)]
public sealed class DiscordRepositoryTests
{
    [Fact]
    public void SaveNotificationSetting_updates_global_row_without_duplicates()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var eventKey = "test_global_" + Guid.NewGuid().ToString("N");

        try
        {
            repository.SaveNotificationSetting(new DiscordNotificationSetting(eventKey, null, Enabled: true));
            repository.SaveNotificationSetting(new DiscordNotificationSetting(eventKey, null, Enabled: false));
            repository.SaveNotificationSetting(new DiscordNotificationSetting(eventKey, null, Enabled: true));

            var setting = repository.GetNotificationSetting(eventKey, null);

            Assert.True(setting.Enabled);
            Assert.Equal(1, CountNotificationRows(eventKey, serverId: null));
        }
        finally
        {
            DeleteNotificationRows(eventKey);
        }
    }

    [Fact]
    public void SaveNotificationSetting_keeps_server_specific_row_separate_from_global()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var eventKey = "test_server_" + Guid.NewGuid().ToString("N");

        try
        {
            repository.SaveNotificationSetting(new DiscordNotificationSetting(eventKey, null, Enabled: true));
            repository.SaveNotificationSetting(new DiscordNotificationSetting(eventKey, "server-1", Enabled: false));

            Assert.True(repository.GetNotificationSetting(eventKey, null).Enabled);
            Assert.False(repository.GetNotificationSetting(eventKey, "server-1").Enabled);
            Assert.Equal(1, CountNotificationRows(eventKey, serverId: null));
            Assert.Equal(1, CountNotificationRows(eventKey, "server-1"));
        }
        finally
        {
            DeleteNotificationRows(eventKey);
        }
    }

    [Fact]
    public void SaveServerSettings_persists_and_clears_card_alert_and_dashboard_channel_ids()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "round-trip-" + Guid.NewGuid().ToString("N");

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", true, "card-1", "alert-1", "dash-1"));

            var saved = repository.GetServerSettings(serverId);

            Assert.NotNull(saved);
            Assert.Equal("card-1", saved!.CardChannelId);
            Assert.Equal("alert-1", saved.AlertChannelId);
            Assert.Equal("dash-1", saved.DashboardChannelId);

            repository.SaveServerSettings(new DiscordServerSettings(serverId, "chan", true));
            var cleared = repository.GetServerSettings(serverId);

            Assert.Null(cleared!.CardChannelId);
            Assert.Null(cleared.AlertChannelId);
            Assert.Null(cleared.DashboardChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
        }
    }

    [Fact]
    public void GetServerSettings_reads_legacy_row_with_only_channel_name_populated()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var serverId = "legacy-test-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var connection = AppDatabase.OpenConnection())
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO discord_server_settings (server_id, channel_name, include_on_dashboard, updated_utc)
                    VALUES ($serverId, 'legacy-channel', 1, $updatedUtc);
                    """;
                insert.Parameters.AddWithValue("$serverId", serverId);
                insert.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            var saved = repository.GetServerSettings(serverId);

            Assert.NotNull(saved);
            Assert.Equal("legacy-channel", saved!.ChannelName);
            Assert.True(saved.IncludeOnDashboard);
            Assert.Null(saved.CardChannelId);
            Assert.Null(saved.AlertChannelId);
            Assert.Null(saved.DashboardChannelId);

            var all = repository.GetAllServerSettings();
            Assert.Contains(all, s => s.ServerId == serverId && s.ChannelName == "legacy-channel");
        }
        finally
        {
            DeleteServerSettingsRow(serverId);
        }
    }

    [Fact]
    public void GetDistinctAlertChannelIds_returns_distinct_non_blank_values_only()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var alertChannelId = "alert-" + suffix;
        var serverA = "alert-server-a-" + suffix;
        var serverB = "alert-server-b-" + suffix;
        var serverC = "alert-server-c-" + suffix;

        try
        {
            repository.SaveServerSettings(new DiscordServerSettings(serverA, "", false, AlertChannelId: alertChannelId));
            repository.SaveServerSettings(new DiscordServerSettings(serverB, "", false, AlertChannelId: alertChannelId));
            repository.SaveServerSettings(new DiscordServerSettings(serverC, "", false));

            var alertChannelIds = repository.GetDistinctAlertChannelIds();

            Assert.Single(alertChannelIds, id => id == alertChannelId);
        }
        finally
        {
            DeleteServerSettingsRow(serverA);
            DeleteServerSettingsRow(serverB);
            DeleteServerSettingsRow(serverC);
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

    [Fact]
    public void DeletePanelMessage_removes_the_row()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var panelKey = "test-panel-" + Guid.NewGuid().ToString("N");

        try
        {
            repository.SavePanelMessage(new DiscordPanelMessage(panelKey, "1", "2", "nonce"));
            Assert.NotNull(repository.GetPanelMessage(panelKey));

            repository.DeletePanelMessage(panelKey);

            Assert.Null(repository.GetPanelMessage(panelKey));
        }
        finally
        {
            repository.DeletePanelMessage(panelKey);
        }
    }

    [Fact]
    public void GetPanelKeysByPrefix_returns_only_matching_keys()
    {
        AppDatabase.Initialize();
        var repository = new DiscordRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var matching = $"dashboard:{suffix}";
        var nonMatching = $"server:{suffix}";

        try
        {
            repository.SavePanelMessage(new DiscordPanelMessage(matching, "1", "2", ""));
            repository.SavePanelMessage(new DiscordPanelMessage(nonMatching, "1", "3", ""));

            var keys = repository.GetPanelKeysByPrefix("dashboard:");

            Assert.Contains(matching, keys);
            Assert.DoesNotContain(nonMatching, keys);
        }
        finally
        {
            repository.DeletePanelMessage(matching);
            repository.DeletePanelMessage(nonMatching);
        }
    }

    private static int CountNotificationRows(string eventKey, string? serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = serverId == null
            ? "SELECT COUNT(*) FROM discord_notifications WHERE event_key = $eventKey AND server_id IS NULL;"
            : "SELECT COUNT(*) FROM discord_notifications WHERE event_key = $eventKey AND server_id = $serverId;";
        command.Parameters.AddWithValue("$eventKey", eventKey);
        if (serverId != null)
        {
            command.Parameters.AddWithValue("$serverId", serverId);
        }

        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void DeleteNotificationRows(string eventKey)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_notifications WHERE event_key = $eventKey;";
        command.Parameters.AddWithValue("$eventKey", eventKey);
        command.ExecuteNonQuery();
    }
}
