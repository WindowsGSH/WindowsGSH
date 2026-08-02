using System.Security.Cryptography;
using System.Text;

namespace WindowsGSH.Core.Web.Auth;

public sealed class WebTokenService
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const int MaxLockoutEntries = 1_000;

    private readonly IWebUserStore _store;
    private readonly byte[] _signingKey;
    private readonly Dictionary<string, (int Failures, DateTimeOffset LockUntil)> _lockouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lockoutsLock = new();

    public WebTokenService(IWebUserStore store, byte[] signingKey)
    {
        _store = store;
        _signingKey = signingKey;
    }

    public async Task<WebLoginResult> LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return WebLoginResult.Fail("Username and password are required.");

        if (IsLockedOut(username, out var lockUntil))
        {
            var remaining = (int)(lockUntil - DateTimeOffset.UtcNow).TotalMinutes + 1;
            WebLog.Add($"Web login rejected for '{username}': account locked for {remaining} more minute(s).");
            return WebLoginResult.Fail($"Account locked. Try again in {remaining} minute(s).");
        }

        var user = await _store.FindByUsernameAsync(username).ConfigureAwait(false);
        if (user == null || !PasswordHasher.Verify(password, user.PasswordHash, user.Salt))
        {
            RecordFailure(username);
            WebLog.Add($"Web login failed for '{username}': invalid credentials.");
            return WebLoginResult.Fail("Invalid username or password.");
        }

        if (!user.Enabled)
        {
            WebLog.Add($"Web login rejected for '{username}': account disabled.");
            return WebLoginResult.Fail("Account is disabled.");
        }

        ClearFailures(username);

        // Update only last_login_utc — a full UpdateAsync would race with concurrent password resets.
        await _store.UpdateLastLoginAsync(user.Id, DateTimeOffset.UtcNow).ConfigureAwait(false);

        var accessToken = JwtHelper.Create(_signingKey, user.Id.ToString(), user.Username, user.Role, AccessTokenLifetime, user.ForcePasswordChange);

        // Do not issue a refresh token when a password change is mandatory — the access token
        // alone gives them enough to call /api/auth/change-password, nothing more.
        var refreshToken = user.ForcePasswordChange
            ? null
            : await IssueRefreshTokenAsync(user.Id).ConfigureAwait(false);

        WebLog.Add($"Web login succeeded for '{username}' (role: {user.Role}){(user.ForcePasswordChange ? " — password change required" : string.Empty)}.");
        return WebLoginResult.Ok(accessToken, refreshToken, user.Role, DateTimeOffset.UtcNow.Add(AccessTokenLifetime), user.ForcePasswordChange);
    }

    public async Task<WebRefreshResult> RefreshAsync(string refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
            return WebRefreshResult.Fail("Refresh token is required.");

        var hash = HashToken(refreshTokenValue);
        var stored = await _store.FindRefreshTokenAsync(hash).ConfigureAwait(false);

        if (stored == null || stored.Used || stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            WebLog.Add("Web token refresh rejected: token invalid, used, or expired.");
            return WebRefreshResult.Fail("Refresh token is invalid or expired.");
        }

        // Conditional update (AND used = 0) makes rotation atomic — if two requests race,
        // only the first revocation returns true; the second gets a failure here.
        var revoked = await _store.RevokeRefreshTokenAsync(hash).ConfigureAwait(false);
        if (!revoked)
        {
            WebLog.Add("Web token refresh rejected: concurrent rotation attempt detected.");
            return WebRefreshResult.Fail("Refresh token is invalid or expired.");
        }

        var user = await _store.FindByIdAsync(stored.UserId).ConfigureAwait(false);
        if (user == null || !user.Enabled)
        {
            WebLog.Add($"Web token refresh rejected for user id {stored.UserId}: user not found or disabled.");
            return WebRefreshResult.Fail("Account is not available.");
        }

        if (user.ForcePasswordChange)
        {
            WebLog.Add($"Web token refresh rejected for '{user.Username}': password change required.");
            return WebRefreshResult.Fail("Password change required. Please log in and change your password.");
        }

        var accessToken = JwtHelper.Create(_signingKey, user.Id.ToString(), user.Username, user.Role, AccessTokenLifetime);
        var newRefreshToken = await IssueRefreshTokenAsync(user.Id).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);

        WebLog.Add($"Web token refreshed for '{user.Username}'.");
        return WebRefreshResult.Ok(accessToken, newRefreshToken, expiresAt);
    }

    public async Task LogoutAsync(string refreshTokenValue)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
            return;

        var hash = HashToken(refreshTokenValue);
        var stored = await _store.FindRefreshTokenAsync(hash).ConfigureAwait(false);
        if (stored == null)
            return;

        await _store.RevokeRefreshTokenAsync(hash).ConfigureAwait(false);

        var user = await _store.FindByIdAsync(stored.UserId).ConfigureAwait(false);
        WebLog.Add($"Web logout for '{user?.Username ?? stored.UserId.ToString()}'.");
    }

    public async Task<string?> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        if (!WebPasswordPolicy.IsValid(newPassword))
            return $"New password must be at least {WebPasswordPolicy.MinimumLength} characters.";

        var user = await _store.FindByIdAsync(userId).ConfigureAwait(false);
        if (user == null)
            return "User not found.";

        if (!user.Enabled)
            return "Account is disabled.";

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.Salt))
            return "Current password is incorrect.";

        var (hash, salt) = PasswordHasher.Hash(newPassword);
        var updated = user with { PasswordHash = hash, Salt = salt, ForcePasswordChange = false };
        await _store.UpdateAsync(updated).ConfigureAwait(false);
        await _store.RevokeAllRefreshTokensForUserAsync(userId).ConfigureAwait(false);

        WebLog.Add($"Password changed for '{user.Username}'.");
        return null;
    }

    public async Task<WebUser?> CreateUserAsync(string username, string password, WebRole role)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var (hash, salt) = PasswordHasher.Hash(password);
        var user = new WebUser(0, username.Trim(), hash, salt, role, DateTimeOffset.UtcNow, null, true, false);
        var id = await _store.CreateAsync(user).ConfigureAwait(false);
        return user with { Id = id };
    }

    private static readonly TimeSpan PasswordResetTokenLifetime = TimeSpan.FromHours(1);

    public async Task<string> GeneratePasswordResetTokenAsync(int userId)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var tokenValue = JwtHelper.Base64UrlEncode(tokenBytes);
        var hash = HashToken(tokenValue);
        await _store.StorePasswordResetTokenAsync(userId, hash, DateTimeOffset.UtcNow.Add(PasswordResetTokenLifetime)).ConfigureAwait(false);
        await _store.PurgeExpiredPasswordResetTokensAsync().ConfigureAwait(false);
        return tokenValue;
    }

    public async Task<string?> UsePasswordResetAsync(string tokenValue, string newPassword)
    {
        if (!WebPasswordPolicy.IsValid(newPassword))
            return $"New password must be at least {WebPasswordPolicy.MinimumLength} characters.";

        var hash = HashToken(tokenValue);
        var stored = await _store.FindPasswordResetTokenAsync(hash).ConfigureAwait(false);

        if (stored == null || stored.Used || stored.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            WebLog.Add("Password reset rejected: token invalid, used, or expired.");
            return "Reset token is invalid or expired.";
        }

        var used = await _store.UsePasswordResetTokenAsync(hash).ConfigureAwait(false);
        if (!used)
        {
            WebLog.Add("Password reset rejected: concurrent use attempt.");
            return "Reset token is invalid or expired.";
        }

        var user = await _store.FindByIdAsync(stored.UserId).ConfigureAwait(false);
        if (user == null || !user.Enabled)
        {
            WebLog.Add($"Password reset rejected for user id {stored.UserId}: account not found or disabled.");
            return "Account is not available.";
        }

        var (pwHash, salt) = PasswordHasher.Hash(newPassword);
        var updated = user with { PasswordHash = pwHash, Salt = salt, ForcePasswordChange = false };
        await _store.UpdateAsync(updated).ConfigureAwait(false);
        await _store.RevokeAllRefreshTokensForUserAsync(user.Id).ConfigureAwait(false);

        WebLog.Add($"Password reset completed for '{user.Username}'.");
        return null;
    }

    public async Task<IReadOnlyList<WebUserSummary>> ListUsersAsync()
    {
        var users = await _store.ListAsync().ConfigureAwait(false);
        return users.Select(u => u.ToSummary()).ToList();
    }

    public async Task<string?> AdminUpdateUserAsync(int targetId, int callerId, WebRole? role, bool? enabled, string callerName)
    {
        if (targetId == callerId && enabled == false)
            return "You cannot disable your own account.";

        if (targetId == callerId && role is not null && role != WebRole.Admin)
            return "You cannot remove your own Admin role.";

        var user = await _store.FindByIdAsync(targetId).ConfigureAwait(false);
        if (user == null) return "User not found.";

        if (role is not null && role != WebRole.Admin && user.Role == WebRole.Admin)
        {
            var allUsers = await _store.ListAsync().ConfigureAwait(false);
            if (allUsers.Count(u => u.Role == WebRole.Admin) <= 1)
                return "Cannot demote the last admin account.";
        }

        var updated = user with
        {
            Role = role ?? user.Role,
            Enabled = enabled ?? user.Enabled,
        };
        await _store.UpdateAsync(updated).ConfigureAwait(false);
        // Revoke existing refresh tokens so the change takes effect at the next refresh attempt
        // rather than waiting for the up-to-15-minute access token lifetime to expire.
        await _store.RevokeAllRefreshTokensForUserAsync(targetId).ConfigureAwait(false);
        WebLog.Add($"Admin '{callerName}' updated user '{user.Username}': role={updated.Role}, enabled={updated.Enabled}.");
        return null;
    }

    public async Task<string?> AdminDeleteUserAsync(int targetId, int callerId, string callerName)
    {
        if (targetId == callerId)
            return "You cannot delete your own account.";

        var user = await _store.FindByIdAsync(targetId).ConfigureAwait(false);
        if (user == null) return "User not found.";

        if (user.Role == WebRole.Admin)
        {
            var allUsers = await _store.ListAsync().ConfigureAwait(false);
            var adminCount = allUsers.Count(u => u.Role == WebRole.Admin);
            if (adminCount <= 1)
                return "Cannot delete the last admin account.";
        }

        await _store.DeleteAsync(targetId).ConfigureAwait(false);
        WebLog.Add($"Admin '{callerName}' deleted user '{user.Username}'.");
        return null;
    }

    public bool TryValidateAccessToken(string token, out string? sub, out string? username, out WebRole? role)
        => JwtHelper.TryValidate(token, _signingKey, out sub, out username, out role);

    public bool TryValidateAccessToken(string token, out string? sub, out string? username, out WebRole? role, out bool forcePasswordChange)
        => JwtHelper.TryValidate(token, _signingKey, out sub, out username, out role, out forcePasswordChange);

    public async Task<string?> EnsureDefaultAdminAsync()
    {
        if (await _store.AnyAsync().ConfigureAwait(false))
            return null;

        var password = GenerateOneTimePassword();
        var (hash, salt) = PasswordHasher.Hash(password);
        var admin = new WebUser(0, "admin", hash, salt, WebRole.Admin, DateTimeOffset.UtcNow, null, true, true);
        await _store.CreateAsync(admin).ConfigureAwait(false);

        WebLog.Add("Web first run: default admin account created. A notification will appear with the temporary password.");
        return password;
    }

    private async Task<string> IssueRefreshTokenAsync(int userId)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var tokenValue = JwtHelper.Base64UrlEncode(tokenBytes);
        var hash = HashToken(tokenValue);
        await _store.StoreRefreshTokenAsync(userId, hash, DateTimeOffset.UtcNow.Add(RefreshTokenLifetime)).ConfigureAwait(false);
        await _store.PurgeExpiredRefreshTokensAsync().ConfigureAwait(false);
        return tokenValue;
    }

    private static string HashToken(string tokenValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue));
        return Convert.ToBase64String(bytes);
    }

    private static string GenerateOneTimePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
    }

    private bool IsLockedOut(string username, out DateTimeOffset lockUntil)
    {
        lock (_lockoutsLock)
        {
            lockUntil = default;
            if (!_lockouts.TryGetValue(username, out var entry))
                return false;

            // LockUntil == MinValue means failures have accumulated but lockout not yet triggered.
            if (entry.LockUntil == default)
                return false;

            if (entry.LockUntil > DateTimeOffset.UtcNow)
            {
                lockUntil = entry.LockUntil;
                return true;
            }

            // Lockout expired — reset so the failure counter starts fresh.
            _lockouts.Remove(username);
            return false;
        }
    }

    private void RecordFailure(string username)
    {
        lock (_lockoutsLock)
        {
            _lockouts.TryGetValue(username, out var entry);
            var failures = entry.Failures + 1;
            var lockUntil = failures >= MaxFailedAttempts
                ? DateTimeOffset.UtcNow.Add(LockoutDuration)
                : entry.LockUntil;
            _lockouts[username] = (failures, lockUntil);

            // Prevent unbounded growth from a flood of unique invented usernames.
            // Only prune when necessary — entries with an active lockout are kept.
            if (_lockouts.Count > MaxLockoutEntries)
                PruneStaleLockoutEntries();
        }
    }

    private void PruneStaleLockoutEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = _lockouts
            .Where(kv => kv.Value.LockUntil == default || kv.Value.LockUntil < now)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in stale)
            _lockouts.Remove(key);
    }

    private void ClearFailures(string username)
    {
        lock (_lockoutsLock)
        {
            _lockouts.Remove(username);
        }
    }
}

public sealed record WebLoginResult(
    bool Succeeded,
    string? AccessToken,
    string? RefreshToken,
    WebRole? Role,
    DateTimeOffset? ExpiresAt,
    bool ForcePasswordChange,
    string? Error)
{
    public static WebLoginResult Ok(string accessToken, string? refreshToken, WebRole role, DateTimeOffset expiresAt, bool forcePasswordChange)
        => new(true, accessToken, refreshToken, role, expiresAt, forcePasswordChange, null);

    public static WebLoginResult Fail(string error)
        => new(false, null, null, null, null, false, error);
}

public sealed record WebRefreshResult(
    bool Succeeded,
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? Error)
{
    public static WebRefreshResult Ok(string accessToken, string refreshToken, DateTimeOffset expiresAt)
        => new(true, accessToken, refreshToken, expiresAt, null);

    public static WebRefreshResult Fail(string error)
        => new(false, null, null, null, error);
}
