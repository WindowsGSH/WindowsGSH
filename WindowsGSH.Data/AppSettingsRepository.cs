namespace WindowsGSH.Data;

/// <summary>
/// Thin wrapper around the <c>app_settings</c> key-value table (created in migration v1, but
/// otherwise unused until this). Intended for small one-time/one-off state that doesn't belong in
/// the JSON settings file or in a feature-specific table - e.g. a completion marker for a
/// migration that must only ever run once.
/// </summary>
public sealed class AppSettingsRepository
{
    public string? GetValue(string key)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetValue(string key, string value)
    {
        using var connection = AppDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value, updated_utc)
            VALUES ($key, $value, $updatedUtc)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
