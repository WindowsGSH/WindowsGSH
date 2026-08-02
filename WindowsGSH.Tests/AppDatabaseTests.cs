using WindowsGSH.Data;
using WindowsGSH.Core.Diagnostics;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class AppDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests",
        Guid.NewGuid().ToString("N"));

    public AppDatabaseTests()
    {
        Directory.CreateDirectory(_root);
        LocalStateRecoveryStatus.Clear();
    }

    [Fact]
    public void OpenConnection_initializes_provider_and_uses_expected_native_sqlite()
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        Assert.Equal("3.53.3", Convert.ToString(command.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_creates_consistent_backup_before_pending_migrations()
    {
        var path = Path.Combine(_root, "migration.db");
        using (var connection = AppDatabase.OpenConnection(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var result = AppDatabase.Initialize(path);

        Assert.Equal(0, result.PreviousSchemaVersion);
        Assert.NotNull(result.MigrationBackupPath);
        Assert.True(File.Exists(result.MigrationBackupPath));
        using var backup = AppDatabase.OpenConnection(result.MigrationBackupPath!, readOnly: true);
        using var check = backup.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", Convert.ToString(check.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_fresh_database_reaches_current_schema_version()
    {
        var path = Path.Combine(_root, "fresh.db");

        var result = AppDatabase.Initialize(path);

        Assert.Equal(AppDatabase.CurrentSchemaVersion, result.CurrentSchemaVersion);
        using var connection = AppDatabase.OpenConnection(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(AppDatabase.CurrentSchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_backs_up_before_applying_a_lone_pending_migration()
    {
        // Deletes the row for the *current* latest migration (bump this alongside
        // CurrentSchemaVersion) so the scenario stays "only the newest migration is pending" —
        // GetMigrationState reports the max of whatever remains recorded, so leaving an earlier
        // gap here instead would misreport PreviousSchemaVersion.
        var path = Path.Combine(_root, "lone-pending-migration.db");
        AppDatabase.Initialize(path);
        using (var connection = AppDatabase.OpenConnection(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM schema_migrations WHERE version = {AppDatabase.CurrentSchemaVersion};";
            command.ExecuteNonQuery();
        }

        var result = AppDatabase.Initialize(path);

        Assert.Equal(AppDatabase.CurrentSchemaVersion - 1, result.PreviousSchemaVersion);
        Assert.Equal(AppDatabase.CurrentSchemaVersion, result.CurrentSchemaVersion);
        Assert.NotNull(result.MigrationBackupPath);
        Assert.True(File.Exists(result.MigrationBackupPath));
    }

    [Fact]
    public void Initialize_fresh_database_creates_discord_channel_routing_columns()
    {
        var path = Path.Combine(_root, "fresh-discord-columns.db");

        AppDatabase.Initialize(path);

        using var connection = AppDatabase.OpenConnection(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('discord_server_settings');";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Contains("card_channel_id", columns);
        Assert.Contains("alert_channel_id", columns);
        Assert.Contains("dashboard_channel_id", columns);
    }

    [Fact]
    public void Initialize_upgrades_v8_database_and_backfills_card_channel_from_channel_name()
    {
        var path = Path.Combine(_root, "v8-to-v9-upgrade.db");
        AppDatabase.Initialize(path);
        using (var connection = AppDatabase.OpenConnection(path))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO discord_server_settings (server_id, channel_name, include_on_dashboard, updated_utc)
                VALUES ('legacy-server', 'general', 1, $updatedUtc);
                """;
            insert.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();

            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM schema_migrations WHERE version = 9;";
            delete.ExecuteNonQuery();
        }

        var result = AppDatabase.Initialize(path);

        Assert.Equal(AppDatabase.CurrentSchemaVersion, result.CurrentSchemaVersion);
        using var verify = AppDatabase.OpenConnection(path, readOnly: true);
        using var select = verify.CreateCommand();
        select.CommandText = """
            SELECT card_channel_id, alert_channel_id, dashboard_channel_id
            FROM discord_server_settings
            WHERE server_id = 'legacy-server';
            """;
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("general", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void Initialize_tolerates_discord_channel_columns_already_present_without_migration_record()
    {
        // Simulates a DB hotfixed or restored out of band: the v9 columns already physically
        // exist, but schema_migrations doesn't record v9 as applied. A bare ALTER TABLE ADD
        // COLUMN (as V4/V7 use for their single-column additions) would fail with "duplicate
        // column name" here; AddColumnIfMissing must tolerate it instead.
        var path = Path.Combine(_root, "columns-already-present.db");
        AppDatabase.Initialize(path);
        using (var connection = AppDatabase.OpenConnection(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_migrations WHERE version = 9;";
            command.ExecuteNonQuery();
        }

        var result = AppDatabase.Initialize(path);

        Assert.Equal(AppDatabase.CurrentSchemaVersion, result.CurrentSchemaVersion);
    }

    [Fact]
    public async Task CheckIntegrityAsync_reports_healthy_database()
    {
        var path = Path.Combine(_root, "healthy.db");
        AppDatabase.Initialize(path);

        var result = await AppDatabase.CheckIntegrityAsync(path);

        Assert.True(result.IsHealthy);
        Assert.Equal("ok", result.Details);
    }

    [Fact]
    public void Initialize_does_not_replace_or_delete_corrupt_database()
    {
        var path = Path.Combine(_root, "corrupt.db");
        var original = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        File.WriteAllBytes(path, original);

        Assert.ThrowsAny<Exception>(() => AppDatabase.Initialize(path));

        Assert.True(File.Exists(path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Initialize_backs_up_incomplete_history_before_migration_failure()
    {
        var path = Path.Combine(_root, "incomplete-history.db");
        AppDatabase.Initialize(path);
        using (var connection = AppDatabase.OpenConnection(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_migrations WHERE version = 4;";
            command.ExecuteNonQuery();
        }

        var exception = Assert.ThrowsAny<Exception>(() => AppDatabase.Initialize(path));

        Assert.True(File.Exists(path));
        var backupPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "recovery"),
            "incomplete-history.db.pre-migration-*.bak"));
        Assert.Contains(backupPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        var recovery = Assert.Single(LocalStateRecoveryStatus.Snapshot());
        Assert.Equal(backupPath, recovery.RecoveryPath);
        Assert.Contains("pre-migration backup", recovery.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialize_bounds_migration_backup_retention()
    {
        var path = Path.Combine(_root, "retention.db");
        AppDatabase.Initialize(path);
        var recovery = Path.Combine(_root, "recovery");
        Directory.CreateDirectory(recovery);
        for (var index = 0; index < 5; index++)
        {
            var backup = Path.Combine(recovery, $"retention.db.pre-migration-v1-20260101-00000{index}.bak");
            File.WriteAllText(backup, "test");
            File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddMinutes(-index));
        }

        AppDatabase.Initialize(path, backupRetention: 2);

        Assert.Equal(2, Directory.EnumerateFiles(recovery, "retention.db.pre-migration-*.bak").Count());
    }

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
