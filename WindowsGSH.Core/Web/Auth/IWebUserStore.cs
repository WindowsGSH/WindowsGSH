namespace WindowsGSH.Core.Web.Auth;

public interface IWebUserStore
{
    Task<WebUser?> FindByUsernameAsync(string username);
    Task<WebUser?> FindByIdAsync(int id);
    Task<int> CreateAsync(WebUser user);
    Task UpdateAsync(WebUser user);
    Task DeleteAsync(int id);
    Task<IReadOnlyList<WebUser>> ListAsync();
    Task<bool> AnyAsync();

    Task StoreRefreshTokenAsync(int userId, string tokenHash, DateTimeOffset expiresAt);
    Task<WebRefreshToken?> FindRefreshTokenAsync(string tokenHash);
    /// <summary>Marks the token used only if it is currently unused. Returns true when this call was the first to revoke it.</summary>
    Task<bool> RevokeRefreshTokenAsync(string tokenHash);
    Task RevokeAllRefreshTokensForUserAsync(int userId);
    Task PurgeExpiredRefreshTokensAsync();
    Task UpdateLastLoginAsync(int userId, DateTimeOffset lastLoginUtc);

    Task StorePasswordResetTokenAsync(int userId, string tokenHash, DateTimeOffset expiresAt);
    Task<WebPasswordResetToken?> FindPasswordResetTokenAsync(string tokenHash);
    /// <summary>Marks the token used only if currently unused. Returns true when this call was the first to mark it used.</summary>
    Task<bool> UsePasswordResetTokenAsync(string tokenHash);
    Task PurgeExpiredPasswordResetTokensAsync();
}
