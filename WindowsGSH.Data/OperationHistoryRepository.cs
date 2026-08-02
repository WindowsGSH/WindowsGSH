using Microsoft.Data.Sqlite;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Data;

public sealed record ServerLifecycleTimes(DateTimeOffset? LastStarted, DateTimeOffset? LastStopped);

public static class OperationHistoryRepository
{
    // Mirrors AppLogRepository's own retention pattern - app_logs already had this, operation_history
    // never did, so a long-running install accumulated this table forever.
    private const int MaxStoredRows = 50_000;
    private const int RetentionCheckInterval = 250;
    private static int _writesSinceRetention;

    public static IReadOnlyList<ServerOperationSnapshot> GetRecent(int maxCount = 100, string? databasePath = null)
    {
        try
        {
            using var connection = databasePath == null ? AppDatabase.OpenConnection() : AppDatabase.OpenConnection(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT server_id, server_name, kind, status, started_utc, finished_utc, last_error, description
                FROM operation_history
                ORDER BY COALESCE(finished_utc, started_utc) DESC
                LIMIT $maxCount;
                """;
            command.Parameters.AddWithValue("$maxCount", Math.Clamp(maxCount, 1, 500));

            using var reader = command.ExecuteReader();
            var operations = new List<ServerOperationSnapshot>();
            while (reader.Read())
            {
                operations.Add(ReadSnapshot(reader));
            }

            return operations;
        }
        catch
        {
            return [];
        }
    }

    // For Server Doctor's operation-history check (WindowsGSH.Core/Health/ServerHealthService.cs),
    // which needs one server's own recent operations, not the global feed GetRecent returns -
    // filtering client-side out of GetRecent's top 100 could miss this server's own history
    // entirely on a machine running many active servers.
    //
    // Deliberately NOT wrapped in a swallow-all try/catch the way GetRecent/GetServerLifecycleTimes/
    // Add are - those exist so a real server action (start/stop/etc.) is never blocked by history
    // persistence trouble, but this method backs a *diagnostic read* for Server Doctor, which needs
    // to tell "no history recorded yet" apart from "the database couldn't be read" (locked,
    // corrupt, or otherwise inaccessible) - swallowing here would silently misreport the latter as
    // the former. The caller (ServerInfoWindow.xaml.cs) catches this the same way it already
    // catches WindowsFirewallService.GetRuleStatuses, converting a failure into
    // ServerHealthRequest.RecentOperationsError instead of letting it propagate further.
    public static IReadOnlyList<ServerOperationSnapshot> GetRecentForServer(string serverId, int maxCount = 10, string? databasePath = null)
    {
        using var connection = databasePath == null ? AppDatabase.OpenConnection() : AppDatabase.OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, server_name, kind, status, started_utc, finished_utc, last_error, description
            FROM operation_history
            WHERE server_id = $serverId
            ORDER BY COALESCE(finished_utc, started_utc) DESC
            LIMIT $maxCount;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$maxCount", Math.Clamp(maxCount, 1, 500));

        using var reader = command.ExecuteReader();
        var operations = new List<ServerOperationSnapshot>();
        while (reader.Read())
        {
            operations.Add(ReadSnapshot(reader));
        }

        return operations;
    }

    private static ServerOperationSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        return new ServerOperationSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            ParseKind(reader.GetString(2)),
            reader.GetString(3),
            ParseDate(reader.GetString(4)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            IsActive: false,
            reader.IsDBNull(5) ? null : ParseDate(reader.GetString(5)),
            Description: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public static ServerLifecycleTimes GetServerLifecycleTimes(string serverId, string? databasePath = null)
    {
        try
        {
            using var connection = databasePath == null ? AppDatabase.OpenConnection() : AppDatabase.OpenConnection(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind, COALESCE(finished_utc, started_utc)
                FROM operation_history
                WHERE server_id = $serverId
                  AND status = 'Completed'
                  AND kind IN ('Start', 'Stop', 'ForceStop', 'Restart')
                ORDER BY COALESCE(finished_utc, started_utc) DESC
                LIMIT 20;
                """;
            command.Parameters.AddWithValue("$serverId", serverId);
            using var reader = command.ExecuteReader();

            DateTimeOffset? lastStarted = null;
            DateTimeOffset? lastStopped = null;

            while (reader.Read())
            {
                var kind = reader.GetString(0);
                var time = ParseDate(reader.GetString(1));
                // A completed Restart means the server came back up, so it counts as both
                // a start event (finished_utc ≈ when the server was back online) and a stop
                // event (the server was stopped as part of the restart sequence).
                if ((kind == "Start" || kind == "Restart") && lastStarted == null)
                    lastStarted = time;
                if ((kind == "Stop" || kind == "ForceStop" || kind == "Restart") && lastStopped == null)
                    lastStopped = time;
                if (lastStarted.HasValue && lastStopped.HasValue)
                    break;
            }

            return new ServerLifecycleTimes(lastStarted, lastStopped);
        }
        catch
        {
            return new ServerLifecycleTimes(null, null);
        }
    }

    public static void Add(ServerOperationSnapshot operation, string? databasePath = null)
    {
        try
        {
            using var connection = databasePath == null ? AppDatabase.OpenConnection() : AppDatabase.OpenConnection(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operation_history
                    (server_id, server_name, kind, status, started_utc, finished_utc, last_error, description)
                VALUES
                    ($serverId, $serverName, $kind, $status, $startedUtc, $finishedUtc, $lastError, $description);
                """;
            command.Parameters.AddWithValue("$serverId", operation.ServerId);
            command.Parameters.AddWithValue("$serverName", operation.ServerName);
            command.Parameters.AddWithValue("$kind", operation.Kind.ToString());
            command.Parameters.AddWithValue("$status", operation.Status);
            command.Parameters.AddWithValue("$startedUtc", operation.StartedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$finishedUtc", operation.FinishedAt.HasValue ? operation.FinishedAt.Value.ToUniversalTime().ToString("O") : DBNull.Value);
            command.Parameters.AddWithValue("$lastError", string.IsNullOrWhiteSpace(operation.LastError) ? DBNull.Value : operation.LastError);
            command.Parameters.AddWithValue("$description", string.IsNullOrWhiteSpace(operation.Description) ? DBNull.Value : operation.Description);
            command.ExecuteNonQuery();

            if (Interlocked.Increment(ref _writesSinceRetention) >= RetentionCheckInterval)
            {
                Interlocked.Exchange(ref _writesSinceRetention, 0);
                ApplyRetention(connection, MaxStoredRows);
            }
        }
        catch
        {
            // Operation history persistence should not block server operations.
        }
    }

    // Split out of Add so the retention SQL itself can be exercised directly against a small,
    // deterministic number of rows in a test - see AppLogRepository.ApplyRetention for the same
    // reasoning. internal (not private) for that same testability reason.
    internal static void ApplyRetention(SqliteConnection connection, int maxStoredRows)
    {
        using var retention = connection.CreateCommand();
        retention.CommandText = """
            DELETE FROM operation_history
            WHERE id <= (
                SELECT id
                FROM operation_history
                ORDER BY id DESC
                LIMIT 1 OFFSET $maxRows
            );
            """;
        retention.Parameters.AddWithValue("$maxRows", maxStoredRows);
        retention.ExecuteNonQuery();
    }

    private static ServerOperationKind ParseKind(string value)
    {
        return Enum.TryParse<ServerOperationKind>(value, ignoreCase: true, out var kind)
            ? kind
            : ServerOperationKind.Install;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
