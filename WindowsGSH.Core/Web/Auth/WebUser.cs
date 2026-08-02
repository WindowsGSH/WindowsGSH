namespace WindowsGSH.Core.Web.Auth;

public sealed record WebUser(
    int Id,
    string Username,
    string PasswordHash,
    string Salt,
    WebRole Role,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,
    bool Enabled,
    bool ForcePasswordChange)
{
    public WebUserSummary ToSummary() =>
        new(Id, Username, Role, CreatedUtc, LastLoginUtc, Enabled, ForcePasswordChange);
}

/// <summary>Projection of <see cref="WebUser"/> without PasswordHash/Salt, for list-style APIs that should never carry credential material past the store/service boundary.</summary>
public sealed record WebUserSummary(
    int Id,
    string Username,
    WebRole Role,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,
    bool Enabled,
    bool ForcePasswordChange);
