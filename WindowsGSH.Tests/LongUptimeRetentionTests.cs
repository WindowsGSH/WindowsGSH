using Microsoft.Data.Sqlite;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class LongUptimeRetentionTests
{
    [Fact]
    public void Operation_history_retention_keeps_only_the_newest_rows()
    {
        using var connection = OpenDatabaseWithTable("""
            CREATE TABLE operation_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                marker TEXT NOT NULL
            );
            """);
        InsertMarkers(connection, "operation_history", 10);

        OperationHistoryRepository.ApplyRetention(connection, maxStoredRows: 3);

        Assert.Equal(["marker-7", "marker-8", "marker-9"], ReadMarkers(connection, "operation_history"));
        OperationHistoryRepository.ApplyRetention(connection, maxStoredRows: 3);
        Assert.Equal(["marker-7", "marker-8", "marker-9"], ReadMarkers(connection, "operation_history"));
    }

    [Fact]
    public void Discord_audit_retention_keeps_only_the_newest_rows()
    {
        using var connection = OpenDatabaseWithTable("""
            CREATE TABLE discord_command_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                marker TEXT NOT NULL
            );
            """);
        InsertMarkers(connection, "discord_command_audit", 10);

        DiscordRepository.ApplyAuditRetention(connection, maxStoredRows: 4);

        Assert.Equal(["marker-6", "marker-7", "marker-8", "marker-9"], ReadMarkers(connection, "discord_command_audit"));
        DiscordRepository.ApplyAuditRetention(connection, maxStoredRows: 4);
        Assert.Equal(["marker-6", "marker-7", "marker-8", "marker-9"], ReadMarkers(connection, "discord_command_audit"));
    }

    private static SqliteConnection OpenDatabaseWithTable(string schema)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertMarkers(SqliteConnection connection, string table, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO {table} (marker) VALUES ($marker);";
            command.Parameters.AddWithValue("$marker", $"marker-{i}");
            command.ExecuteNonQuery();
        }
    }

    private static List<string> ReadMarkers(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT marker FROM {table} ORDER BY id;";
        using var reader = command.ExecuteReader();
        var markers = new List<string>();
        while (reader.Read())
        {
            markers.Add(reader.GetString(0));
        }

        return markers;
    }
}
