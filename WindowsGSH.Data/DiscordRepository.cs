using Microsoft.Data.Sqlite;

namespace WindowsGSH.Data;

public sealed class DiscordRepository
{
    // Mirrors AppLogRepository's own retention pattern - static (not an instance field) so the
    // check-every-N-writes cadence holds regardless of how many DiscordRepository instances get
    // created, since this class carries no other state per instance anyway.
    private const int MaxAuditRows = 50_000;
    private const int AuditRetentionCheckInterval = 250;
    private static int _auditWritesSinceRetention;


    public DiscordNotificationSetting GetNotificationSetting(string eventKey, string? serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_key, server_id, enabled
            FROM discord_notifications
            WHERE event_key = $eventKey
              AND (server_id = $serverId OR server_id IS NULL)
            ORDER BY CASE WHEN server_id = $serverId THEN 0 ELSE 1 END, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$eventKey", eventKey);
        command.Parameters.AddWithValue("$serverId", string.IsNullOrWhiteSpace(serverId) ? DBNull.Value : serverId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DiscordNotificationSetting(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2) != 0)
            : new DiscordNotificationSetting(eventKey, serverId, Enabled: true);
    }

    public bool IsNotificationEnabled(string eventKey, string? serverId)
    {
        return GetNotificationSetting(eventKey, serverId).Enabled;
    }

    public void SaveNotificationSetting(DiscordNotificationSetting setting)
    {
        using var connection = AppDatabase.OpenConnection();
        var eventKey = setting.EventKey.Trim();
        var serverId = string.IsNullOrWhiteSpace(setting.ServerId) ? null : setting.ServerId.Trim();
        if (serverId == null)
        {
            SaveGlobalNotificationSetting(connection, eventKey, setting.Enabled);
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_notifications (event_key, server_id, enabled, updated_utc)
            VALUES ($eventKey, $serverId, $enabled, $updatedUtc)
            ON CONFLICT(event_key, server_id) DO UPDATE SET
                enabled = excluded.enabled,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$eventKey", eventKey);
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$enabled", setting.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void SaveGlobalNotificationSetting(SqliteConnection connection, string eventKey, bool enabled)
    {
        using var transaction = connection.BeginTransaction();
        var updatedUtc = DateTimeOffset.UtcNow.ToString("O");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE discord_notifications
                SET enabled = $enabled,
                    updated_utc = $updatedUtc
                WHERE event_key = $eventKey
                  AND server_id IS NULL;
                """;
            update.Parameters.AddWithValue("$eventKey", eventKey);
            update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            update.Parameters.AddWithValue("$updatedUtc", updatedUtc);
            var rows = update.ExecuteNonQuery();
            if (rows == 0)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO discord_notifications (event_key, server_id, enabled, updated_utc)
                    VALUES ($eventKey, NULL, $enabled, $updatedUtc);
                    """;
                insert.Parameters.AddWithValue("$eventKey", eventKey);
                insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                insert.Parameters.AddWithValue("$updatedUtc", updatedUtc);
                insert.ExecuteNonQuery();
            }
        }

        using (var dedupe = connection.CreateCommand())
        {
            dedupe.Transaction = transaction;
            dedupe.CommandText = """
                DELETE FROM discord_notifications
                WHERE event_key = $eventKey
                  AND server_id IS NULL
                  AND id NOT IN (
                    SELECT id
                    FROM discord_notifications
                    WHERE event_key = $eventKey
                      AND server_id IS NULL
                    ORDER BY id DESC
                    LIMIT 1
                  );
                """;
            dedupe.Parameters.AddWithValue("$eventKey", eventKey);
            dedupe.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public DiscordPanelMessage? GetPanelMessage(string panelKey)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT panel_key, channel_id, message_id, panel_nonce
            FROM discord_panel_messages
            WHERE panel_key = $panelKey;
            """;
        command.Parameters.AddWithValue("$panelKey", panelKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DiscordPanelMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    public void SavePanelMessage(DiscordPanelMessage message)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_panel_messages (panel_key, channel_id, message_id, panel_nonce, updated_utc)
            VALUES ($panelKey, $channelId, $messageId, $panelNonce, $updatedUtc)
            ON CONFLICT(panel_key) DO UPDATE SET
                channel_id = excluded.channel_id,
                message_id = excluded.message_id,
                panel_nonce = excluded.panel_nonce,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$panelKey", message.PanelKey);
        command.Parameters.AddWithValue("$channelId", message.ChannelId);
        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$panelNonce", message.PanelNonce);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePanelMessage(string panelKey)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM discord_panel_messages WHERE panel_key = $panelKey;";
        command.Parameters.AddWithValue("$panelKey", panelKey);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Panel keys currently starting with <paramref name="prefix"/> - used to find stale
    /// <c>dashboard:{channelId}</c> records for channels no server is grouped into anymore (Tier 2
    /// Chunk 5 follow-up). <paramref name="prefix"/> is always a caller-supplied literal (e.g.
    /// <c>"dashboard:"</c>), never user input, so a plain SQL LIKE without wildcard-escaping is
    /// safe here.
    /// </summary>
    public IReadOnlyList<string> GetPanelKeysByPrefix(string prefix)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT panel_key FROM discord_panel_messages WHERE panel_key LIKE $prefix;";
        command.Parameters.AddWithValue("$prefix", prefix + "%");
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public DiscordServerSettings? GetServerSettings(string serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, channel_name, include_on_dashboard, card_channel_id, alert_channel_id, dashboard_channel_id
            FROM discord_server_settings
            WHERE server_id = $serverId;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadServerSettings(reader) : null;
    }

    public IReadOnlyList<DiscordServerSettings> GetAllServerSettings()
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, channel_name, include_on_dashboard, card_channel_id, alert_channel_id, dashboard_channel_id
            FROM discord_server_settings
            ORDER BY server_id;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<DiscordServerSettings>();
        while (reader.Read())
        {
            results.Add(ReadServerSettings(reader));
        }

        return results;
    }

    /// <summary>
    /// Distinct, non-blank Alert Channel IDs across every server — the allow-list Chunk 6's
    /// command-channel filtering builds from. An empty result means no Alert Channels are
    /// configured yet, which callers must treat as "accept commands from any channel" (first-run
    /// safety), not as "reject everything."
    /// </summary>
    public IReadOnlyList<string> GetDistinctAlertChannelIds()
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT alert_channel_id
            FROM discord_server_settings
            WHERE alert_channel_id IS NOT NULL AND alert_channel_id != ''
            ORDER BY alert_channel_id;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static DiscordServerSettings ReadServerSettings(SqliteDataReader reader)
    {
        return new DiscordServerSettings(
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public void SaveServerSettings(DiscordServerSettings settings)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_server_settings (server_id, channel_name, include_on_dashboard, card_channel_id, alert_channel_id, dashboard_channel_id, updated_utc)
            VALUES ($serverId, $channelName, $includeOnDashboard, $cardChannelId, $alertChannelId, $dashboardChannelId, $updatedUtc)
            ON CONFLICT(server_id) DO UPDATE SET
                channel_name = excluded.channel_name,
                include_on_dashboard = excluded.include_on_dashboard,
                card_channel_id = excluded.card_channel_id,
                alert_channel_id = excluded.alert_channel_id,
                dashboard_channel_id = excluded.dashboard_channel_id,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$serverId", settings.ServerId);
        command.Parameters.AddWithValue("$channelName", string.IsNullOrWhiteSpace(settings.ChannelName) ? DBNull.Value : settings.ChannelName.Trim());
        command.Parameters.AddWithValue("$includeOnDashboard", settings.IncludeOnDashboard ? 1 : 0);
        command.Parameters.AddWithValue("$cardChannelId", string.IsNullOrWhiteSpace(settings.CardChannelId) ? DBNull.Value : settings.CardChannelId.Trim());
        command.Parameters.AddWithValue("$alertChannelId", string.IsNullOrWhiteSpace(settings.AlertChannelId) ? DBNull.Value : settings.AlertChannelId.Trim());
        command.Parameters.AddWithValue("$dashboardChannelId", string.IsNullOrWhiteSpace(settings.DashboardChannelId) ? DBNull.Value : settings.DashboardChannelId.Trim());
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteServerData(string serverId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM discord_server_settings WHERE server_id = $serverId;";
            command.Parameters.AddWithValue("$serverId", serverId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM discord_notifications WHERE server_id = $serverId;";
            command.Parameters.AddWithValue("$serverId", serverId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM discord_panel_messages WHERE panel_key = $panelKey;";
            command.Parameters.AddWithValue("$panelKey", $"server:{serverId}");
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<DiscordAdminBinding> GetAdmins()
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT discord_user_id, server_ids, note FROM discord_admins ORDER BY discord_user_id;";
        using var reader = command.ExecuteReader();
        var admins = new List<DiscordAdminBinding>();
        while (reader.Read())
        {
            admins.Add(new DiscordAdminBinding(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return admins;
    }

    public void SaveAdmins(IEnumerable<DiscordAdminBinding> admins)
    {
        using var connection = AppDatabase.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM discord_admins;";
            delete.ExecuteNonQuery();
        }

        foreach (var admin in admins)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO discord_admins (discord_user_id, server_ids, note, updated_utc)
                VALUES ($userId, $serverIds, $note, $updatedUtc);
                """;
            command.Parameters.AddWithValue("$userId", admin.DiscordUserId.Trim());
            command.Parameters.AddWithValue("$serverIds", string.IsNullOrWhiteSpace(admin.ServerIds) ? "0" : admin.ServerIds.Trim());
            command.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(admin.Note) ? DBNull.Value : admin.Note);
            command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<string> GetServerIdsForAdmin(string discordUserId)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT server_ids FROM discord_admins WHERE discord_user_id = $userId;";
        command.Parameters.AddWithValue("$userId", discordUserId);
        var value = command.ExecuteScalar()?.ToString() ?? string.Empty;
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    public void UpsertGuild(string guildId, string? guildName, string commandPrefix, string? dashboardChannelId, string? notificationsChannelId, int dashboardRefreshMinutes)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_guilds (guild_id, guild_name, enabled, command_prefix, dashboard_channel_id, notifications_channel_id, dashboard_refresh_minutes, updated_utc)
            VALUES ($guildId, $guildName, 1, $prefix, $dashboardChannelId, $notificationsChannelId, $refreshMinutes, $updatedUtc)
            ON CONFLICT(guild_id) DO UPDATE SET
                guild_name = excluded.guild_name,
                command_prefix = excluded.command_prefix,
                dashboard_channel_id = excluded.dashboard_channel_id,
                notifications_channel_id = excluded.notifications_channel_id,
                dashboard_refresh_minutes = excluded.dashboard_refresh_minutes,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$guildId", guildId);
        command.Parameters.AddWithValue("$guildName", string.IsNullOrWhiteSpace(guildName) ? DBNull.Value : guildName);
        command.Parameters.AddWithValue("$prefix", string.IsNullOrWhiteSpace(commandPrefix) ? "!" : commandPrefix);
        command.Parameters.AddWithValue("$dashboardChannelId", string.IsNullOrWhiteSpace(dashboardChannelId) ? DBNull.Value : dashboardChannelId);
        command.Parameters.AddWithValue("$notificationsChannelId", string.IsNullOrWhiteSpace(notificationsChannelId) ? DBNull.Value : notificationsChannelId);
        command.Parameters.AddWithValue("$refreshMinutes", Math.Clamp(dashboardRefreshMinutes, 1, 1440));
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void AddAudit(DiscordCommandAudit audit)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discord_command_audit
                (created_utc, guild_id, channel_id, user_id, username, command, arguments, server_id, result)
            VALUES
                ($createdUtc, $guildId, $channelId, $userId, $username, $command, $arguments, $serverId, $result);
            """;
        command.Parameters.AddWithValue("$createdUtc", audit.CreatedUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$guildId", string.IsNullOrWhiteSpace(audit.GuildId) ? DBNull.Value : audit.GuildId);
        command.Parameters.AddWithValue("$channelId", string.IsNullOrWhiteSpace(audit.ChannelId) ? DBNull.Value : audit.ChannelId);
        command.Parameters.AddWithValue("$userId", audit.UserId);
        command.Parameters.AddWithValue("$username", audit.Username);
        command.Parameters.AddWithValue("$command", audit.Command);
        command.Parameters.AddWithValue("$arguments", string.IsNullOrWhiteSpace(audit.Arguments) ? DBNull.Value : audit.Arguments);
        command.Parameters.AddWithValue("$serverId", string.IsNullOrWhiteSpace(audit.ServerId) ? DBNull.Value : audit.ServerId);
        command.Parameters.AddWithValue("$result", audit.Result);
        command.ExecuteNonQuery();

        if (Interlocked.Increment(ref _auditWritesSinceRetention) >= AuditRetentionCheckInterval)
        {
            Interlocked.Exchange(ref _auditWritesSinceRetention, 0);
            ApplyAuditRetention(connection, MaxAuditRows);
        }
    }

    // Split out of AddAudit so the retention SQL itself can be exercised directly against a small,
    // deterministic number of rows in a test - see AppLogRepository.ApplyRetention for the same
    // reasoning. internal (not private) for that same testability reason.
    internal static void ApplyAuditRetention(SqliteConnection connection, int maxStoredRows)
    {
        using var retention = connection.CreateCommand();
        retention.CommandText = """
            DELETE FROM discord_command_audit
            WHERE id <= (
                SELECT id
                FROM discord_command_audit
                ORDER BY id DESC
                LIMIT 1 OFFSET $maxRows
            );
            """;
        retention.Parameters.AddWithValue("$maxRows", maxStoredRows);
        retention.ExecuteNonQuery();
    }
}

public sealed record DiscordAdminBinding(string DiscordUserId, string ServerIds, string? Note);

/// <summary>
/// <c>ChannelName</c>/<c>IncludeOnDashboard</c> are the pre-Tier-2 fields (kept for backward
/// compatibility — <c>ChannelName</c> is the legacy "Server Channel", used as both card and alert
/// fallback before the three-channel redesign). <c>CardChannelId</c>/<c>AlertChannelId</c>/
/// <c>DashboardChannelId</c> are optional so existing positional constructor calls still compile;
/// Chunk 2 backfills them from the legacy fields where blank.
/// </summary>
public sealed record DiscordServerSettings(
    string ServerId,
    string ChannelName,
    bool IncludeOnDashboard,
    string? CardChannelId = null,
    string? AlertChannelId = null,
    string? DashboardChannelId = null);

public sealed record DiscordNotificationSetting(string EventKey, string? ServerId, bool Enabled);

public sealed record DiscordPanelMessage(string PanelKey, string ChannelId, string MessageId, string PanelNonce);

public sealed record DiscordCommandAudit(
    DateTimeOffset CreatedUtc,
    string? GuildId,
    string? ChannelId,
    string UserId,
    string Username,
    string Command,
    string? Arguments,
    string? ServerId,
    string Result);
