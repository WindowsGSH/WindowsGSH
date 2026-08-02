using System.IO;
using Microsoft.Data.Sqlite;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;

namespace WindowsGSH.Data;

public static class AppDatabase
{
    private static readonly object ProviderLock = new();
    private static volatile bool _providerInitialized;

    public const int CurrentSchemaVersion = 10;

    public static string DatabasePath => AppPaths.GetPath("WindowsGSH.db");

    public static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public static void Initialize()
    {
        try
        {
            var result = Initialize(DatabasePath);
            if (!string.IsNullOrWhiteSpace(result.MigrationBackupPath))
            {
                LocalStateRecoveryStatus.Report(
                    "Database",
                    $"Created a pre-migration backup before upgrading schema v{result.PreviousSchemaVersion} to v{result.CurrentSchemaVersion}.",
                    result.MigrationBackupPath);
            }
        }
        catch (Exception ex)
        {
            if (ex is DatabaseMigrationException)
            {
                throw;
            }

            LocalStateRecoveryStatus.Report(
                "Database",
                $"Database initialization failed without resetting user data: {ex.Message}",
                DatabasePath,
                isFailure: true);
            throw;
        }
    }

    public static DatabaseInitializationResult Initialize(string databasePath, int backupRetention = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        CleanupMigrationBackups(fullPath, backupRetention);

        var existed = File.Exists(fullPath);
        using var connection = OpenConnection(fullPath);
        var migrationState = existed
            ? GetMigrationState(connection)
            : new DatabaseMigrationState(CurrentSchemaVersion, HasPendingMigrations: false);
        string? backupPath = null;
        if (existed && migrationState.HasPendingMigrations)
        {
            backupPath = CreateMigrationBackup(connection, fullPath, migrationState.SchemaVersion);
        }

        try
        {
            ExecutePragma(connection, "PRAGMA journal_mode = WAL;");
            using var transaction = connection.BeginTransaction();

            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                """);

            ApplyMigration(connection, transaction, 1, ApplyV1);
            ApplyMigration(connection, transaction, 2, ApplyV2);
            ApplyMigration(connection, transaction, 3, ApplyV3);
            ApplyMigration(connection, transaction, 4, ApplyV4);
            ApplyMigration(connection, transaction, 5, ApplyV5);
            ApplyMigration(connection, transaction, 6, ApplyV6);
            ApplyMigration(connection, transaction, 7, ApplyV7);
            ApplyMigration(connection, transaction, 8, ApplyV8);
            ApplyMigration(connection, transaction, 9, ApplyV9);
            ApplyMigration(connection, transaction, 10, ApplyV10);

            transaction.Commit();
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(backupPath))
        {
            var message =
                $"Database migration failed without resetting user data. " +
                $"The pre-migration backup is available at {backupPath}: {ex.Message}";
            LocalStateRecoveryStatus.Report(
                "Database migration",
                message,
                backupPath,
                isFailure: true);
            throw new DatabaseMigrationException(message, backupPath, ex);
        }

        CleanupMigrationBackups(fullPath, backupRetention);
        return new DatabaseInitializationResult(
            fullPath,
            migrationState.SchemaVersion,
            CurrentSchemaVersion,
            backupPath);
    }

    /// <summary>
    /// When <paramref name="version"/> was recorded as applied in <c>schema_migrations</c>, or
    /// <c>null</c> if it hasn't been applied (or the database/table doesn't exist yet). Lets a
    /// caller distinguish "this schema version was just introduced by the current process's own
    /// startup migration" from "this schema version - and any code that has run against it -
    /// already existed for at least one earlier session," e.g. to decide whether a one-time
    /// migration tied to that schema version is still safe to run.
    /// </summary>
    public static DateTimeOffset? GetSchemaMigrationAppliedUtc(int version, string? databasePath = null)
    {
        var path = Path.GetFullPath(databasePath ?? DatabasePath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var connection = OpenConnection(path, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT applied_utc FROM schema_migrations WHERE version = $version;";
            command.Parameters.AddWithValue("$version", version);
            var value = command.ExecuteScalar() as string;
            return value != null && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static async Task<DatabaseIntegrityResult> CheckIntegrityAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(databasePath ?? DatabasePath);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = OpenConnection(path, readOnly: true);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                var rows = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(reader.GetString(0));
                }

                var healthy = rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase);
                var result = new DatabaseIntegrityResult(path, healthy, string.Join(Environment.NewLine, rows));
                if (!healthy)
                {
                    LocalStateRecoveryStatus.Report(
                        "Database integrity",
                        $"SQLite quick check reported: {result.Details}",
                        path,
                        isFailure: true);
                }

                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocalStateRecoveryStatus.Report(
                "Database integrity",
                $"SQLite quick check failed: {ex.Message}",
                path,
                isFailure: true);
            return new DatabaseIntegrityResult(path, false, ex.Message);
        }
    }

    private static string BuildConnectionString(string databasePath, bool readOnly)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public static SqliteConnection OpenConnection()
    {
        return OpenConnection(DatabasePath);
    }

    public static SqliteConnection OpenConnection(string databasePath, bool readOnly = false)
    {
        EnsureProviderInitialized();
        var connection = new SqliteConnection(BuildConnectionString(Path.GetFullPath(databasePath), readOnly));
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static DatabaseMigrationState GetMigrationState(SqliteConnection connection)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_migrations';
            """;
        if (Convert.ToInt32(tableCommand.ExecuteScalar()) == 0)
        {
            return new DatabaseMigrationState(0, HasPendingMigrations: true);
        }

        using var migrationsCommand = connection.CreateCommand();
        migrationsCommand.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        var applied = new HashSet<int>();
        using var reader = migrationsCommand.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(reader.GetInt32(0));
        }

        var schemaVersion = applied.Count == 0 ? 0 : applied.Max();
        var hasPending = Enumerable.Range(1, CurrentSchemaVersion).Any(version => !applied.Contains(version));
        return new DatabaseMigrationState(schemaVersion, hasPending);
    }

    private static string CreateMigrationBackup(SqliteConnection source, string databasePath, int schemaVersion)
    {
        var recoveryDirectory = GetRecoveryDirectory(databasePath);
        Directory.CreateDirectory(recoveryDirectory);
        var backupPath = Path.Combine(
            recoveryDirectory,
            $"{Path.GetFileName(databasePath)}.pre-migration-v{schemaVersion}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bak");
        using var destination = new SqliteConnection(BuildConnectionString(backupPath, readOnly: false));
        destination.Open();
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static string GetRecoveryDirectory(string databasePath)
    {
        return Path.Combine(Path.GetDirectoryName(databasePath)!, "recovery");
    }

    private static void CleanupMigrationBackups(string databasePath, int retention)
    {
        try
        {
            var recoveryDirectory = GetRecoveryDirectory(databasePath);
            if (!Directory.Exists(recoveryDirectory))
            {
                return;
            }

            foreach (var stale in Directory.EnumerateFiles(
                             recoveryDirectory,
                             $"{Path.GetFileName(databasePath)}.pre-migration-*.bak")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(Math.Max(0, retention)))
            {
                try
                {
                    File.Delete(stale);
                }
                catch
                {
                    // Best-effort maintenance must not block database startup.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumerating optional recovery backups is also best-effort.
        }
    }

    private static void EnsureProviderInitialized()
    {
        if (_providerInitialized)
        {
            return;
        }

        lock (ProviderLock)
        {
            if (_providerInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _providerInitialized = true;
        }
    }

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool HasMigration(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = $version;";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private static void ApplyMigration(SqliteConnection connection, SqliteTransaction transaction, int version, Action<SqliteConnection, SqliteTransaction> apply)
    {
        if (HasMigration(connection, transaction, version))
        {
            return;
        }

        apply(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_migrations (version, applied_utc) VALUES ($version, $appliedUtc);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ApplyV1(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                source TEXT NULL,
                message TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_app_logs_created_utc ON app_logs (created_utc);
            CREATE INDEX IF NOT EXISTS ix_app_logs_source_created_utc ON app_logs (source, created_utc);

            CREATE TABLE IF NOT EXISTS operation_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id TEXT NOT NULL,
                server_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                status TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NULL,
                last_error TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_operation_history_finished_utc ON operation_history (finished_utc);
            CREATE INDEX IF NOT EXISTS ix_operation_history_server_id ON operation_history (server_id);

            CREATE TABLE IF NOT EXISTS discord_guilds (
                guild_id TEXT PRIMARY KEY,
                guild_name TEXT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                command_prefix TEXT NOT NULL DEFAULT '!',
                dashboard_channel_id TEXT NULL,
                notifications_channel_id TEXT NULL,
                dashboard_refresh_minutes INTEGER NOT NULL DEFAULT 5,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS discord_admins (
                discord_user_id TEXT PRIMARY KEY,
                server_ids TEXT NOT NULL DEFAULT '0',
                note TEXT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS discord_command_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                guild_id TEXT NULL,
                channel_id TEXT NULL,
                user_id TEXT NOT NULL,
                username TEXT NOT NULL,
                command TEXT NOT NULL,
                arguments TEXT NULL,
                server_id TEXT NULL,
                result TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_discord_command_audit_created_utc ON discord_command_audit (created_utc);
            CREATE INDEX IF NOT EXISTS ix_discord_command_audit_user_id ON discord_command_audit (user_id);

            CREATE TABLE IF NOT EXISTS discord_notifications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_key TEXT NOT NULL,
                server_id TEXT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                updated_utc TEXT NOT NULL,
                UNIQUE(event_key, server_id)
            );
            """);
    }

    private static void ApplyV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS discord_server_settings (
                server_id TEXT PRIMARY KEY,
                channel_name TEXT NULL,
                include_on_dashboard INTEGER NOT NULL DEFAULT 1,
                updated_utc TEXT NOT NULL
            );
            """);
    }

    private static void ApplyV3(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS discord_panel_messages (
                panel_key TEXT PRIMARY KEY,
                channel_id TEXT NOT NULL,
                message_id TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """);
    }

    private static void ApplyV4(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE discord_panel_messages ADD COLUMN panel_nonce TEXT NOT NULL DEFAULT '';
            """);
    }

    private static void ApplyV5(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            DELETE FROM discord_notifications
            WHERE server_id IS NULL
              AND id NOT IN (
                SELECT MAX(id)
                FROM discord_notifications
                WHERE server_id IS NULL
                GROUP BY event_key
              );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_discord_notifications_global_event
            ON discord_notifications (event_key)
            WHERE server_id IS NULL;
            """);
    }

    private static void ApplyV6(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS web_users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                salt TEXT NOT NULL,
                role TEXT NOT NULL DEFAULT 'Viewer',
                created_utc TEXT NOT NULL,
                last_login_utc TEXT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                force_password_change INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS web_refresh_tokens (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL REFERENCES web_users(id) ON DELETE CASCADE,
                token_hash TEXT NOT NULL UNIQUE,
                expires_at TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_web_refresh_tokens_user_id ON web_refresh_tokens (user_id);
            CREATE INDEX IF NOT EXISTS ix_web_refresh_tokens_expires_at ON web_refresh_tokens (expires_at);
            """);
    }

    private static void ApplyV7(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction,
            "ALTER TABLE operation_history ADD COLUMN description TEXT NULL;");
    }

    private static void ApplyV8(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS web_password_reset_tokens (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL REFERENCES web_users(id) ON DELETE CASCADE,
                token_hash TEXT NOT NULL UNIQUE,
                expires_at TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_web_prt_user_id ON web_password_reset_tokens (user_id);
            CREATE INDEX IF NOT EXISTS ix_web_prt_expires_at ON web_password_reset_tokens (expires_at);
            """);
    }

    private static void ApplyV9(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Tier 2 (Discord channel routing redesign), Chunk 1. Guarded with AddColumnIfMissing
        // rather than a bare ALTER TABLE (unlike V4/V7's single-column additions): a DB that was
        // hotfixed or restored out of band could already have these columns without this
        // migration being recorded, and a bare ALTER TABLE ADD COLUMN would fail with "duplicate
        // column name" in that case.
        AddColumnIfMissing(connection, transaction, "discord_server_settings", "card_channel_id", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "discord_server_settings", "alert_channel_id", "TEXT NULL");
        AddColumnIfMissing(connection, transaction, "discord_server_settings", "dashboard_channel_id", "TEXT NULL");

        // Only the card-channel backfill belongs in this SQL migration: it's a same-table column
        // copy with no external dependency. Backfilling alert/dashboard channel IDs needs the
        // global AppSettings values from settings.json, which is an application-level concern
        // (Chunk 2), not something the SQL migration should reach for.
        Execute(connection, transaction, """
            UPDATE discord_server_settings
            SET card_channel_id = channel_name
            WHERE channel_name IS NOT NULL AND channel_name != '' AND card_channel_id IS NULL;
            """);
    }

    private static void ApplyV10(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS owned_port_mappings (
                ownership_id TEXT PRIMARY KEY,
                gateway_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                remote_host TEXT NOT NULL,
                external_port INTEGER NOT NULL CHECK (external_port BETWEEN 0 AND 65535),
                protocol TEXT NOT NULL CHECK (protocol IN ('TCP', 'UDP')),
                internal_port INTEGER NOT NULL CHECK (internal_port BETWEEN 1 AND 65535),
                internal_client TEXT NOT NULL,
                lease_duration_seconds INTEGER NOT NULL CHECK (lease_duration_seconds BETWEEN 0 AND 4294967295),
                created_utc TEXT NOT NULL,
                refresh_due_utc TEXT NULL,
                UNIQUE (gateway_id, remote_host, external_port, protocol)
            );
            CREATE INDEX IF NOT EXISTS ix_owned_port_mappings_server
            ON owned_port_mappings (server_id);
            """);
    }

    private static void AddColumnIfMissing(SqliteConnection connection, SqliteTransaction transaction, string table, string columnName, string columnDefinitionSql)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.Transaction = transaction;
        checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $columnName;";
        checkCommand.Parameters.AddWithValue("$columnName", columnName);
        if (Convert.ToInt32(checkCommand.ExecuteScalar()) > 0)
        {
            return;
        }

        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {columnName} {columnDefinitionSql};");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed record DatabaseInitializationResult(
    string DatabasePath,
    int PreviousSchemaVersion,
    int CurrentSchemaVersion,
    string? MigrationBackupPath);

public sealed record DatabaseIntegrityResult(string DatabasePath, bool IsHealthy, string Details);

internal sealed record DatabaseMigrationState(int SchemaVersion, bool HasPendingMigrations);

internal sealed class DatabaseMigrationException(
    string message,
    string migrationBackupPath,
    Exception innerException) : InvalidOperationException(message, innerException)
{
    public string MigrationBackupPath { get; } = migrationBackupPath;
}
