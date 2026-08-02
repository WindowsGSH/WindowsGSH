using Microsoft.Data.Sqlite;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class AppLogRepositoryTests
{
    [Fact]
    public void ApplyRetention_keeps_only_the_newest_rows_and_is_a_no_op_under_the_cap()
    {
        // Regression guard for a real bug: the app_logs SQLite table itself grew forever - only
        // the in-memory Messages/Buffer view was ever bounded. This exercises the retention SQL
        // directly (see ApplyRetention's own comment for why: Add's real path only prunes every
        // 250 writes once the table already exceeds 100,000 rows, which isn't practical to drive
        // in a fast test) against an isolated in-memory database, not the shared app database -
        // AppLogRepository.Add's own connection ultimately points at the same real, shared
        // WindowsGSH.db every other test in this process uses, and calling a small-maxRows
        // retention sweep against that would prune rows unrelated tests/the app's own background
        // log writer are concurrently relying on.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreateAppLogsTable(connection);

        for (var i = 0; i < 10; i++)
        {
            InsertRow(connection, $"message-{i}");
        }

        AppLogRepository.ApplyRetention(connection, maxStoredRows: 3);

        var remaining = ReadMessagesOrderedById(connection);
        Assert.Equal(["message-7", "message-8", "message-9"], remaining);

        // Applying retention again with nothing over the cap must not delete anything further -
        // the subquery finds no row at that offset, and `id <= NULL` is never true in SQL.
        AppLogRepository.ApplyRetention(connection, maxStoredRows: 3);
        Assert.Equal(["message-7", "message-8", "message-9"], ReadMessagesOrderedById(connection));
    }

    [Fact]
    public void ApplyRetention_does_not_delete_anything_when_the_table_is_under_the_cap()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        CreateAppLogsTable(connection);

        for (var i = 0; i < 5; i++)
        {
            InsertRow(connection, $"message-{i}");
        }

        AppLogRepository.ApplyRetention(connection, maxStoredRows: 100);

        Assert.Equal(
            ["message-0", "message-1", "message-2", "message-3", "message-4"],
            ReadMessagesOrderedById(connection));
    }

    private static void CreateAppLogsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // Mirrors AppDatabase's own app_logs schema (ApplyV1) closely enough for ApplyRetention's
        // purposes - it only ever reads/writes id, which AUTOINCREMENT guarantees is monotonically
        // increasing, the property the retention SQL's ORDER BY id DESC relies on.
        command.CommandText = """
            CREATE TABLE app_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                source TEXT NULL,
                message TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertRow(SqliteConnection connection, string message)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_logs (created_utc, source, message) VALUES ($createdUtc, NULL, $message);";
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static List<string> ReadMessagesOrderedById(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT message FROM app_logs ORDER BY id;";
        using var reader = command.ExecuteReader();
        var messages = new List<string>();
        while (reader.Read())
        {
            messages.Add(reader.GetString(0));
        }

        return messages;
    }
}
