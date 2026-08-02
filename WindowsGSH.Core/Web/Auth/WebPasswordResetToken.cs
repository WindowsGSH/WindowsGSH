namespace WindowsGSH.Core.Web.Auth;

public sealed record WebPasswordResetToken(
    int Id,
    int UserId,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    bool Used);
