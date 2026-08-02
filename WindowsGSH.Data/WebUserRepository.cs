using Microsoft.Data.Sqlite;
using WindowsGSH.Core.Web.Auth;

namespace WindowsGSH.Data;

public sealed class WebUserRepository : IWebUserStore
{
    private readonly string? _databasePathOverride;

    public WebUserRepository(string? databasePathOverride = null)
    {
        _databasePathOverride = databasePathOverride;
    }

    private SqliteConnection Open()
        => _databasePathOverride != null
            ? AppDatabase.OpenConnection(_databasePathOverride)
            : AppDatabase.OpenConnection();

    public async Task<WebUser?> FindByUsernameAsync(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, password_hash, salt, role, created_utc, last_login_utc, enabled, force_password_change
            FROM web_users WHERE LOWER(username) = LOWER($username);
            """;
        cmd.Parameters.AddWithValue("$username", username);
        using var reader = await cmd.ExecuteReaderAsync();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public async Task<WebUser?> FindByIdAsync(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, password_hash, salt, role, created_utc, last_login_utc, enabled, force_password_change
            FROM web_users WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public async Task<int> CreateAsync(WebUser user)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_users (username, password_hash, salt, role, created_utc, last_login_utc, enabled, force_password_change)
            VALUES ($username, $passwordHash, $salt, $role, $createdUtc, $lastLoginUtc, $enabled, $forcePasswordChange);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$username", user.Username);
        cmd.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
        cmd.Parameters.AddWithValue("$salt", user.Salt);
        cmd.Parameters.AddWithValue("$role", user.Role.ToString());
        cmd.Parameters.AddWithValue("$createdUtc", user.CreatedUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$lastLoginUtc", user.LastLoginUtc.HasValue ? (object)user.LastLoginUtc.Value.ToUniversalTime().ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", user.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$forcePasswordChange", user.ForcePasswordChange ? 1 : 0);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateAsync(WebUser user)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE web_users SET
                username = $username,
                password_hash = $passwordHash,
                salt = $salt,
                role = $role,
                last_login_utc = $lastLoginUtc,
                enabled = $enabled,
                force_password_change = $forcePasswordChange
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$username", user.Username);
        cmd.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
        cmd.Parameters.AddWithValue("$salt", user.Salt);
        cmd.Parameters.AddWithValue("$role", user.Role.ToString());
        cmd.Parameters.AddWithValue("$lastLoginUtc", user.LastLoginUtc.HasValue ? (object)user.LastLoginUtc.Value.ToUniversalTime().ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", user.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$forcePasswordChange", user.ForcePasswordChange ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_users WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
        await RevokeAllRefreshTokensForUserAsync(id);
    }

    public async Task<IReadOnlyList<WebUser>> ListAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, password_hash, salt, role, created_utc, last_login_utc, enabled, force_password_change
            FROM web_users ORDER BY id;
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<WebUser>();
        while (reader.Read())
            list.Add(ReadUser(reader));
        return list;
    }

    public async Task<bool> AnyAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM web_users;";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    public async Task StoreRefreshTokenAsync(int userId, string tokenHash, DateTimeOffset expiresAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_refresh_tokens (user_id, token_hash, expires_at, used)
            VALUES ($userId, $tokenHash, $expiresAt, 0);
            """;
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("$expiresAt", expiresAt.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<WebRefreshToken?> FindRefreshTokenAsync(string tokenHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, token_hash, expires_at, used
            FROM web_refresh_tokens WHERE token_hash = $tokenHash;
            """;
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!reader.Read()) return null;
        return new WebRefreshToken(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4) != 0);
    }

    public async Task<bool> RevokeRefreshTokenAsync(string tokenHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_refresh_tokens SET used = 1 WHERE token_hash = $tokenHash AND used = 0;";
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task UpdateLastLoginAsync(int userId, DateTimeOffset lastLoginUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_users SET last_login_utc = $lastLoginUtc WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.Parameters.AddWithValue("$lastLoginUtc", lastLoginUtc.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RevokeAllRefreshTokensForUserAsync(int userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_refresh_tokens SET used = 1 WHERE user_id = $userId;";
        cmd.Parameters.AddWithValue("$userId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PurgeExpiredRefreshTokensAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_refresh_tokens WHERE expires_at < $now OR used = 1;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task StorePasswordResetTokenAsync(int userId, string tokenHash, DateTimeOffset expiresAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_password_reset_tokens (user_id, token_hash, expires_at, used)
            VALUES ($userId, $tokenHash, $expiresAt, 0);
            """;
        cmd.Parameters.AddWithValue("$userId", userId);
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        cmd.Parameters.AddWithValue("$expiresAt", expiresAt.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<WebPasswordResetToken?> FindPasswordResetTokenAsync(string tokenHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, token_hash, expires_at, used
            FROM web_password_reset_tokens WHERE token_hash = $tokenHash;
            """;
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!reader.Read()) return null;
        return new WebPasswordResetToken(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4) != 0);
    }

    public async Task<bool> UsePasswordResetTokenAsync(string tokenHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE web_password_reset_tokens SET used = 1 WHERE token_hash = $tokenHash AND used = 0;";
        cmd.Parameters.AddWithValue("$tokenHash", tokenHash);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task PurgeExpiredPasswordResetTokensAsync()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM web_password_reset_tokens WHERE expires_at < $now OR used = 1;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static WebUser ReadUser(SqliteDataReader reader)
    {
        var lastLoginRaw = reader.IsDBNull(6) ? null : (string?)reader.GetString(6);
        return new WebUser(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<WebRole>(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)),
            lastLoginRaw != null ? DateTimeOffset.Parse(lastLoginRaw) : null,
            reader.GetInt32(7) != 0,
            reader.GetInt32(8) != 0);
    }
}
