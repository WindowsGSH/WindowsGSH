namespace WindowsGSH.Core.Steam;

public enum SteamGuardChallengeKind
{
    Unknown,
    Email,
    MobileAuthenticator
}

public sealed record SteamGuardChallengeRequest(
    string OperationId,
    SteamGuardChallengeKind Kind,
    string SteamAccount,
    DateTimeOffset DetectedAt)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    public bool IsExpired => DateTimeOffset.UtcNow - DetectedAt > Lifetime;
}

public interface ISteamGuardCodeProvider
{
    /// <summary>
    /// Requests a Steam Guard code from the user.
    /// Returns null if the user cancels, the challenge has expired, or the provider is unavailable.
    /// </summary>
    Task<string?> RequestCodeAsync(SteamGuardChallengeRequest challenge, CancellationToken cancellationToken);
}
