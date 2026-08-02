namespace WindowsGSH.Core.Web.Auth;

public sealed record WebRefreshToken(
    int Id,
    int UserId,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    bool Used);
