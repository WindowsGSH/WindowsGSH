using Microsoft.Data.Sqlite;

namespace WindowsGSH.Data;

public static class AppLogRepository
{
    private const int MaxStoredRows = 100_000;
    private const int RetentionCheckInterval = 250;
    private static int _writesSinceRetention;

    public static void Add(DateTimeOffset createdUtc, string? source, string message)
    {
        try
        {
            using var connection = AppDatabase.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_logs (created_utc, source, message)
                VALUES ($createdUtc, $source, $message);
                """;
            command.Parameters.AddWithValue("$createdUtc", createdUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(source) ? DBNull.Value : source);
            command.Parameters.AddWithValue("$message", message);
            command.ExecuteNonQuery();

            if (Interlocked.Increment(ref _writesSinceRetention) >= RetentionCheckInterval)
            {
                Interlocked.Exchange(ref _writesSinceRetention, 0);
                ApplyRetention(connection, MaxStoredRows);
            }
        }
        catch
        {
            // Logging must never break server control paths.
        }
    }

    // Split out of Add so the retention SQL itself can be exercised directly against a small,
    // deterministic number of rows in a test - going through Add's own RetentionCheckInterval-
    // counted path would mean writing 100,000+ real rows just to reach the boundary once.
    // internal (not private) for that same testability reason.
    internal static void ApplyRetention(SqliteConnection connection, int maxStoredRows)
    {
        using var retention = connection.CreateCommand();
        retention.CommandText = """
            DELETE FROM app_logs
            WHERE id <= (
                SELECT id
                FROM app_logs
                ORDER BY id DESC
                LIMIT 1 OFFSET $maxRows
            );
            """;
        retention.Parameters.AddWithValue("$maxRows", maxStoredRows);
        retention.ExecuteNonQuery();
    }
}
