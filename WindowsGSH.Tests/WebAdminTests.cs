using System.Security.Cryptography;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(WebTestCollection.Name)]
public sealed class WebAdminTests : IDisposable
{
    private readonly string _dbPath;
    private readonly WebUserRepository _repo;
    private readonly WebTokenService _svc;

    public WebAdminTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"webadmin-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
        _repo = new WebUserRepository(_dbPath);
        _svc = new WebTokenService(_repo, RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<WebUser> MakeUserAsync(string username, WebRole role = WebRole.Admin, bool enabled = true)
    {
        var (hash, salt) = PasswordHasher.Hash("Password1!");
        var u = new WebUser(0, username, hash, salt, role, DateTimeOffset.UtcNow, null, enabled, false);
        var id = await _repo.CreateAsync(u);
        return u with { Id = id };
    }

    // ── ListUsersAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListUsersAsync_returns_all_users()
    {
        await MakeUserAsync("admin1");
        await MakeUserAsync("viewer1", WebRole.Viewer);
        var users = await _svc.ListUsersAsync();
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public async Task ListUsersAsync_projects_summary_without_credential_material()
    {
        var admin = await MakeUserAsync("admin1");

        var users = await _svc.ListUsersAsync();

        var summary = Assert.Single(users);
        Assert.Equal(admin.Username, summary.Username);
        Assert.Equal(admin.Role, summary.Role);
        // WebUserSummary has no PasswordHash/Salt properties at all — this is a compile-time
        // guarantee (P3-01), not just a runtime check. Assert it stays that way.
        Assert.Null(typeof(WebUserSummary).GetProperty("PasswordHash"));
        Assert.Null(typeof(WebUserSummary).GetProperty("Salt"));
    }

    // ── AdminUpdateUserAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task AdminUpdateUserAsync_changes_role()
    {
        var admin = await MakeUserAsync("admin1");
        var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);

        var error = await _svc.AdminUpdateUserAsync(viewer.Id, admin.Id, WebRole.Operator, null, "admin1");

        Assert.Null(error);
        var updated = await _repo.FindByIdAsync(viewer.Id);
        Assert.Equal(WebRole.Operator, updated!.Role);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_changes_enabled_state()
    {
        var admin = await MakeUserAsync("admin1");
        var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);

        var error = await _svc.AdminUpdateUserAsync(viewer.Id, admin.Id, null, false, "admin1");

        Assert.Null(error);
        var updated = await _repo.FindByIdAsync(viewer.Id);
        Assert.False(updated!.Enabled);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_prevents_self_disable()
    {
        var admin = await MakeUserAsync("admin1");

        var error = await _svc.AdminUpdateUserAsync(admin.Id, admin.Id, null, false, "admin1");

        Assert.NotNull(error);
        Assert.Contains("disable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_prevents_self_demotion_from_admin()
    {
        var admin = await MakeUserAsync("admin1");

        var error = await _svc.AdminUpdateUserAsync(admin.Id, admin.Id, WebRole.Viewer, null, "admin1");

        Assert.NotNull(error);
        Assert.Contains("Admin role", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_prevents_demoting_last_admin()
    {
        var admin = await MakeUserAsync("admin1");

        // admin1 is the only admin; another admin (hypothetically) tries to demote them.
        var error = await _svc.AdminUpdateUserAsync(admin.Id, admin.Id + 99, WebRole.Viewer, null, "admin2");

        Assert.NotNull(error);
        Assert.Contains("last admin", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_allows_demoting_non_last_admin()
    {
        var admin1 = await MakeUserAsync("admin1");
        var admin2 = await MakeUserAsync("admin2");

        // Two admins exist, so demoting admin2 is allowed.
        var error = await _svc.AdminUpdateUserAsync(admin2.Id, admin1.Id, WebRole.Viewer, null, "admin1");

        Assert.Null(error);
        var updated = await _repo.FindByIdAsync(admin2.Id);
        Assert.Equal(WebRole.Viewer, updated!.Role);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_returns_not_found_for_missing_user()
    {
        var admin = await MakeUserAsync("admin1");

        var error = await _svc.AdminUpdateUserAsync(9999, admin.Id, WebRole.Viewer, null, "admin1");

        Assert.Equal("User not found.", error);
    }

    // ── AdminDeleteUserAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task AdminDeleteUserAsync_deletes_non_admin_user()
    {
        var admin = await MakeUserAsync("admin1");
        var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);

        var error = await _svc.AdminDeleteUserAsync(viewer.Id, admin.Id, "admin1");

        Assert.Null(error);
        Assert.Null(await _repo.FindByIdAsync(viewer.Id));
    }

    [Fact]
    public async Task AdminDeleteUserAsync_prevents_self_delete()
    {
        var admin = await MakeUserAsync("admin1");

        var error = await _svc.AdminDeleteUserAsync(admin.Id, admin.Id, "admin1");

        Assert.NotNull(error);
        Assert.Contains("own account", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminDeleteUserAsync_prevents_deleting_last_admin()
    {
        var admin1 = await MakeUserAsync("admin1");
        var admin2 = await MakeUserAsync("admin2");

        // Delete admin2 (leaves admin1 as sole admin).
        var ok = await _svc.AdminDeleteUserAsync(admin2.Id, admin1.Id, "admin1");
        Assert.Null(ok);

        // Now admin1 is the last admin; admin2 tries to delete admin1.
        // (In practice a deleted user has no session, but the service enforces
        //  the last-admin rule regardless of caller identity here.)
        var error = await _svc.AdminDeleteUserAsync(admin1.Id, admin2.Id, "admin2");
        Assert.NotNull(error);
        Assert.Contains("last admin", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminDeleteUserAsync_returns_not_found_for_missing_user()
    {
        var admin = await MakeUserAsync("admin1");

        var error = await _svc.AdminDeleteUserAsync(9999, admin.Id, "admin1");

        Assert.Equal("User not found.", error);
    }

    // ── Password reset token ──────────────────────────────────────────────────

    [Fact]
    public async Task GeneratePasswordResetTokenAsync_returns_non_empty_token()
    {
        var user = await MakeUserAsync("user1", WebRole.Viewer);

        var token = await _svc.GeneratePasswordResetTokenAsync(user.Id);

        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task UsePasswordResetAsync_succeeds_with_valid_token()
    {
        var user = await MakeUserAsync("user1", WebRole.Viewer);
        var token = await _svc.GeneratePasswordResetTokenAsync(user.Id);

        var error = await _svc.UsePasswordResetAsync(token, "NewPassword1!");

        Assert.Null(error);
        Assert.True(PasswordHasher.Verify("NewPassword1!", (await _repo.FindByIdAsync(user.Id))!.PasswordHash, (await _repo.FindByIdAsync(user.Id))!.Salt));
    }

    [Fact]
    public async Task UsePasswordResetAsync_rejects_second_use_of_same_token()
    {
        var user = await MakeUserAsync("user1", WebRole.Viewer);
        var token = await _svc.GeneratePasswordResetTokenAsync(user.Id);

        await _svc.UsePasswordResetAsync(token, "NewPassword1!");
        var error = await _svc.UsePasswordResetAsync(token, "AnotherPass1!");

        Assert.NotNull(error);
        Assert.Contains("invalid or expired", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsePasswordResetAsync_rejects_expired_token()
    {
        var user = await MakeUserAsync("user1", WebRole.Viewer);
        // Store a token that expired an hour ago by inserting directly into the repo.
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var tokenValue = Convert.ToBase64String(tokenBytes);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokenValue));
        var hashB64 = Convert.ToBase64String(hash);
        await _repo.StorePasswordResetTokenAsync(user.Id, hashB64, DateTimeOffset.UtcNow.AddHours(-1));

        var error = await _svc.UsePasswordResetAsync(tokenValue, "NewPassword1!");

        Assert.NotNull(error);
        Assert.Contains("invalid or expired", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsePasswordResetAsync_rejects_short_password()
    {
        var user = await MakeUserAsync("user1", WebRole.Viewer);
        var token = await _svc.GeneratePasswordResetTokenAsync(user.Id);

        var error = await _svc.UsePasswordResetAsync(token, "short");

        Assert.NotNull(error);
        Assert.Contains("12 characters", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminUpdateUserAsync_revokes_refresh_tokens_so_next_refresh_fails()
    {
        var admin = await MakeUserAsync("admin1");
        var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);

        // Issue a refresh token for viewer.
        var loginResult = await _svc.LoginAsync("viewer1", "Password1!");
        Assert.True(loginResult.Succeeded);

        // Admin promotes viewer to Operator — should revoke their refresh token.
        await _svc.AdminUpdateUserAsync(viewer.Id, admin.Id, WebRole.Operator, null, "admin1");

        // viewer's refresh token must now be invalid.
        var refreshResult = await _svc.RefreshAsync(loginResult.RefreshToken!);
        Assert.False(refreshResult.Succeeded);
    }

    // ── WebLog entries ────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminDeleteUserAsync_logs_action()
    {
        var logs = new List<string>();
        WebLog.Sink = (msg, _) => logs.Add(msg);
        try
        {
            var admin = await MakeUserAsync("admin1");
            var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);
            await _svc.AdminDeleteUserAsync(viewer.Id, admin.Id, "admin1");
            Assert.Contains(logs, l => l.Contains("admin1") && l.Contains("viewer1"));
        }
        finally
        {
            WebLog.Sink = null;
        }
    }

    [Fact]
    public async Task AdminUpdateUserAsync_logs_action()
    {
        var logs = new List<string>();
        WebLog.Sink = (msg, _) => logs.Add(msg);
        try
        {
            var admin = await MakeUserAsync("admin1");
            var viewer = await MakeUserAsync("viewer1", WebRole.Viewer);
            await _svc.AdminUpdateUserAsync(viewer.Id, admin.Id, WebRole.Operator, null, "admin1");
            Assert.Contains(logs, l => l.Contains("admin1") && l.Contains("viewer1"));
        }
        finally
        {
            WebLog.Sink = null;
        }
    }
}

public sealed class PasswordResetRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly WebUserRepository _repo;

    public PasswordResetRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"prt-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
        _repo = new WebUserRepository(_dbPath);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<int> MakeUserIdAsync()
    {
        var (hash, salt) = PasswordHasher.Hash("pw");
        return await _repo.CreateAsync(new WebUser(0, $"u{Guid.NewGuid():N}", hash, salt, WebRole.Viewer, DateTimeOffset.UtcNow, null, true, false));
    }

    [Fact]
    public async Task StoreAndFind_password_reset_token_round_trip()
    {
        var userId = await MakeUserIdAsync();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        await _repo.StorePasswordResetTokenAsync(userId, "hash-abc", expires);

        var found = await _repo.FindPasswordResetTokenAsync("hash-abc");
        Assert.NotNull(found);
        Assert.Equal(userId, found.UserId);
        Assert.False(found.Used);
    }

    [Fact]
    public async Task UsePasswordResetToken_marks_used_and_prevents_double_use()
    {
        var userId = await MakeUserIdAsync();
        await _repo.StorePasswordResetTokenAsync(userId, "hash-xyz", DateTimeOffset.UtcNow.AddHours(1));

        var first = await _repo.UsePasswordResetTokenAsync("hash-xyz");
        Assert.True(first);

        var second = await _repo.UsePasswordResetTokenAsync("hash-xyz");
        Assert.False(second);
    }
}
