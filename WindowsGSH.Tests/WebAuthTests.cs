using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_and_verify_correct_password_succeeds()
    {
        var (hash, salt) = PasswordHasher.Hash("correct-horse-battery-staple");
        Assert.True(PasswordHasher.Verify("correct-horse-battery-staple", hash, salt));
    }

    [Fact]
    public void Verify_wrong_password_fails()
    {
        var (hash, salt) = PasswordHasher.Hash("original-password");
        Assert.False(PasswordHasher.Verify("wrong-password", hash, salt));
    }

    [Fact]
    public void Hash_produces_different_salts_each_call()
    {
        var (_, salt1) = PasswordHasher.Hash("same-password");
        var (_, salt2) = PasswordHasher.Hash("same-password");
        Assert.NotEqual(salt1, salt2);
    }
}

public sealed class JwtHelperTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Create_and_validate_round_trip()
    {
        var token = JwtHelper.Create(Key, "42", "alice", WebRole.Admin, TimeSpan.FromMinutes(15));
        Assert.True(JwtHelper.TryValidate(token, Key, out var sub, out var username, out var role));
        Assert.Equal("42", sub);
        Assert.Equal("alice", username);
        Assert.Equal(WebRole.Admin, role);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var token = JwtHelper.Create(Key, "1", "bob", WebRole.Viewer, TimeSpan.FromSeconds(-1));
        Assert.False(JwtHelper.TryValidate(token, Key, out _, out _, out _));
    }

    [Fact]
    public void Tampered_signature_is_rejected()
    {
        var token = JwtHelper.Create(Key, "1", "bob", WebRole.Viewer, TimeSpan.FromMinutes(15));
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.False(JwtHelper.TryValidate(tampered, Key, out _, out _, out _));
    }

    [Fact]
    public void Wrong_signing_key_is_rejected()
    {
        var otherKey = RandomNumberGenerator.GetBytes(32);
        var token = JwtHelper.Create(Key, "1", "bob", WebRole.Viewer, TimeSpan.FromMinutes(15));
        Assert.False(JwtHelper.TryValidate(token, otherKey, out _, out _, out _));
    }

    // ── P3-01: alg header assertion ─────────────────────────────────

    [Fact]
    public void Header_alg_none_is_rejected()
    {
        // A validly HMAC-signed token whose header declares alg:none must still be rejected.
        var token = BuildSignedToken(Key, new { alg = "none", typ = "JWT" }, ValidPayload());
        Assert.False(JwtHelper.TryValidate(token, Key, out _, out _, out _));
    }

    [Fact]
    public void Header_unexpected_alg_is_rejected()
    {
        var token = BuildSignedToken(Key, new { alg = "RS256", typ = "JWT" }, ValidPayload());
        Assert.False(JwtHelper.TryValidate(token, Key, out _, out _, out _));
    }

    // ── P3-02: iss / aud claim assertion ────────────────────────────

    [Fact]
    public void Token_without_iss_is_rejected()
    {
        var payload = ValidPayload();
        payload.Remove("iss");
        Assert.False(JwtHelper.TryValidate(BuildSignedToken(Key, StandardHeader(), payload), Key, out _, out _, out _));
    }

    [Fact]
    public void Token_with_wrong_iss_is_rejected()
    {
        var payload = ValidPayload();
        payload["iss"] = "other-service";
        Assert.False(JwtHelper.TryValidate(BuildSignedToken(Key, StandardHeader(), payload), Key, out _, out _, out _));
    }

    [Fact]
    public void Token_without_aud_is_rejected()
    {
        var payload = ValidPayload();
        payload.Remove("aud");
        Assert.False(JwtHelper.TryValidate(BuildSignedToken(Key, StandardHeader(), payload), Key, out _, out _, out _));
    }

    [Fact]
    public void Token_with_wrong_aud_is_rejected()
    {
        var payload = ValidPayload();
        payload["aud"] = "other-audience";
        Assert.False(JwtHelper.TryValidate(BuildSignedToken(Key, StandardHeader(), payload), Key, out _, out _, out _));
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static object StandardHeader() => new { alg = "HS256", typ = "JWT" };

    private static Dictionary<string, object> ValidPayload() => new()
    {
        ["sub"]      = "1",
        ["username"] = "testuser",
        ["role"]     = "Viewer",
        ["iat"]      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["exp"]      = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds(),
        ["jti"]      = Guid.NewGuid().ToString("N"),
        ["iss"]      = "windowsgsh",
        ["aud"]      = "windowsgsh-web",
    };

    private static string BuildSignedToken(byte[] key, object header, Dictionary<string, object> payload)
    {
        var headerPart  = JwtHelper.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadPart = JwtHelper.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var hmac  = new HMACSHA256(key);
        var sig = JwtHelper.Base64UrlEncode(
            hmac.ComputeHash(Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}")));
        return $"{headerPart}.{payloadPart}.{sig}";
    }
}

public sealed class WebUserRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public WebUserRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webauth-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private WebUserRepository Repo() => new(_dbPath);

    [Fact]
    public async Task CreateAsync_and_FindByUsernameAsync_round_trip()
    {
        var (hash, salt) = PasswordHasher.Hash("password123");
        var user = new WebUser(0, "alice", hash, salt, WebRole.Admin, DateTimeOffset.UtcNow, null, true, false);
        var id = await Repo().CreateAsync(user);
        Assert.True(id > 0);

        var found = await Repo().FindByUsernameAsync("alice");
        Assert.NotNull(found);
        Assert.Equal("alice", found.Username);
        Assert.Equal(WebRole.Admin, found.Role);
    }

    [Fact]
    public async Task FindByUsernameAsync_is_case_insensitive()
    {
        var (hash, salt) = PasswordHasher.Hash("pw");
        var user = new WebUser(0, "Alice", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, false);
        await Repo().CreateAsync(user);

        var found = await Repo().FindByUsernameAsync("ALICE");
        Assert.NotNull(found);
    }

    [Fact]
    public async Task AnyAsync_returns_false_on_empty_store()
    {
        Assert.False(await Repo().AnyAsync());
    }

    [Fact]
    public async Task AnyAsync_returns_true_after_create()
    {
        var (hash, salt) = PasswordHasher.Hash("pw");
        await Repo().CreateAsync(new WebUser(0, "user1", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, false));
        Assert.True(await Repo().AnyAsync());
    }

    [Fact]
    public async Task DeleteAsync_removes_user()
    {
        var (hash, salt) = PasswordHasher.Hash("pw");
        var id = await Repo().CreateAsync(new WebUser(0, "temp", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, false));
        await Repo().DeleteAsync(id);
        Assert.Null(await Repo().FindByIdAsync(id));
    }

    [Fact]
    public async Task RefreshToken_store_find_revoke_cycle()
    {
        var (hash, salt) = PasswordHasher.Hash("pw");
        var userId = await Repo().CreateAsync(new WebUser(0, "charlie", hash, salt, WebRole.Operator, DateTimeOffset.UtcNow, null, true, false));

        var tokenHash = "test-token-hash-abc123";
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        await Repo().StoreRefreshTokenAsync(userId, tokenHash, expires);

        var token = await Repo().FindRefreshTokenAsync(tokenHash);
        Assert.NotNull(token);
        Assert.False(token.Used);
        Assert.Equal(userId, token.UserId);

        var firstRevoke = await Repo().RevokeRefreshTokenAsync(tokenHash);
        Assert.True(firstRevoke);
        var revoked = await Repo().FindRefreshTokenAsync(tokenHash);
        Assert.True(revoked?.Used);

        // Second revoke on an already-used token returns false (conditional update).
        var secondRevoke = await Repo().RevokeRefreshTokenAsync(tokenHash);
        Assert.False(secondRevoke);
    }
}

[Collection(WebTestCollection.Name)]
public sealed class WebTokenServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly byte[] _key;
    private readonly WebUserRepository _repo;
    private readonly WebTokenService _svc;

    public WebTokenServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tokensvc-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
        _key = RandomNumberGenerator.GetBytes(32);
        _repo = new WebUserRepository(_dbPath);
        _svc = new WebTokenService(_repo, _key);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<WebUser> CreateUserAsync(string username, string password, WebRole role = WebRole.Operator, bool enabled = true)
    {
        var (hash, salt) = PasswordHasher.Hash(password);
        var user = new WebUser(0, username, hash, salt, role, DateTimeOffset.UtcNow, null, enabled, false);
        var id = await _repo.CreateAsync(user);
        return user with { Id = id };
    }

    [Fact]
    public async Task LoginAsync_correct_credentials_succeeds()
    {
        await CreateUserAsync("bob", "Password1!");
        var result = await _svc.LoginAsync("bob", "Password1!");
        Assert.True(result.Succeeded);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Equal(WebRole.Operator, result.Role);
    }

    [Fact]
    public async Task LoginAsync_wrong_password_fails()
    {
        await CreateUserAsync("carol", "correct-pw");
        var result = await _svc.LoginAsync("carol", "wrong-pw");
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task LoginAsync_disabled_account_fails()
    {
        await CreateUserAsync("disabled", "password", enabled: false);
        var result = await _svc.LoginAsync("disabled", "password");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task LoginAsync_rate_limits_after_5_failures()
    {
        await CreateUserAsync("dave", "right-password");
        for (var i = 0; i < 5; i++)
            await _svc.LoginAsync("dave", "wrong");

        var result = await _svc.LoginAsync("dave", "right-password");
        Assert.False(result.Succeeded);
        Assert.Contains("locked", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_rotates_token()
    {
        await CreateUserAsync("eve", "pass123");
        var login = await _svc.LoginAsync("eve", "pass123");
        var refresh = await _svc.RefreshAsync(login.RefreshToken!);

        Assert.True(refresh.Succeeded);
        Assert.NotNull(refresh.AccessToken);
        Assert.NotNull(refresh.RefreshToken);
        Assert.NotEqual(login.RefreshToken, refresh.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_rejects_old_token_after_rotation()
    {
        await CreateUserAsync("frank", "pass123");
        var login = await _svc.LoginAsync("frank", "pass123");
        await _svc.RefreshAsync(login.RefreshToken!);

        var second = await _svc.RefreshAsync(login.RefreshToken!);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task LogoutAsync_revokes_refresh_token()
    {
        await CreateUserAsync("grace", "pass123");
        var login = await _svc.LoginAsync("grace", "pass123");
        await _svc.LogoutAsync(login.RefreshToken!);

        var refresh = await _svc.RefreshAsync(login.RefreshToken!);
        Assert.False(refresh.Succeeded);
    }

    [Fact]
    public async Task TryValidateAccessToken_returns_correct_claims()
    {
        await CreateUserAsync("heidi", "pass123", WebRole.Admin);
        var login = await _svc.LoginAsync("heidi", "pass123");
        Assert.True(_svc.TryValidateAccessToken(login.AccessToken!, out var sub, out var username, out var role));
        Assert.Equal("heidi", username);
        Assert.Equal(WebRole.Admin, role);
        Assert.False(string.IsNullOrEmpty(sub));
    }

    [Fact]
    public async Task EnsureDefaultAdminAsync_creates_admin_only_when_empty()
    {
        var capturedLogMessages = new List<string>();
        var previousSink = WebLog.Sink;
        WebLog.Sink = (msg, _) => capturedLogMessages.Add(msg);
        try
        {
            var password = await _svc.EnsureDefaultAdminAsync();

            Assert.NotNull(password);
            Assert.False(string.IsNullOrWhiteSpace(password));
            Assert.True(await _repo.AnyAsync());

            var users = await _repo.ListAsync();
            Assert.Single(users);
            Assert.Equal("admin", users[0].Username);
            Assert.Equal(WebRole.Admin, users[0].Role);
            Assert.True(users[0].ForcePasswordChange);

            // The one-time password must NOT appear in the log.
            Assert.DoesNotContain(capturedLogMessages, msg => msg.Contains(password, StringComparison.Ordinal));

            // Second call must not create a duplicate and must return null.
            var password2 = await _svc.EnsureDefaultAdminAsync();
            Assert.Null(password2);
            Assert.Single(await _repo.ListAsync());
        }
        finally
        {
            WebLog.Sink = previousSink;
        }
    }

    [Fact]
    public void JwtSigningKey_not_present_in_access_token_string()
    {
        var keyBase64 = Convert.ToBase64String(_key);
        var token = JwtHelper.Create(_key, "1", "ivan", WebRole.Viewer, TimeSpan.FromMinutes(15));
        Assert.DoesNotContain(keyBase64, token, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcePasswordChange_login_returns_no_refresh_token()
    {
        var (hash, salt) = PasswordHasher.Hash("pw123");
        await _repo.CreateAsync(new WebUser(0, "forced", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, true));

        var login = await _svc.LoginAsync("forced", "pw123");

        Assert.True(login.Succeeded);
        Assert.True(login.ForcePasswordChange);
        Assert.NotNull(login.AccessToken);
        Assert.Null(login.RefreshToken);
    }

    [Fact]
    public async Task ForcePasswordChange_refresh_rejected_after_admin_sets_flag()
    {
        await CreateUserAsync("mustchange", "pw123");
        var login = await _svc.LoginAsync("mustchange", "pw123");
        var originalRefresh = login.RefreshToken!;

        // Admin sets force_password_change while the user has a live session.
        var users = await _repo.ListAsync();
        var user = users.Single(u => u.Username == "mustchange");
        await _repo.UpdateAsync(user with { ForcePasswordChange = true });

        var refresh = await _svc.RefreshAsync(originalRefresh);
        Assert.False(refresh.Succeeded);
        Assert.Contains("Password change required", refresh.Error);
    }

    [Fact]
    public async Task UpdateLastLoginAsync_does_not_change_password()
    {
        var (hash, salt) = PasswordHasher.Hash("original");
        var id = await _repo.CreateAsync(new WebUser(0, "logintest", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, false));

        await _repo.UpdateLastLoginAsync(id, DateTimeOffset.UtcNow);

        var after = await _repo.FindByIdAsync(id);
        Assert.Equal(hash, after!.PasswordHash);
        Assert.Equal(salt, after.Salt);
        Assert.NotNull(after.LastLoginUtc);
    }
}
