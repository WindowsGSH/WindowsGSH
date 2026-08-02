namespace WindowsGSH.Core.Network;

public interface IPublicIpService
{
    Task<PublicIpCheckResult> CheckAsync(PublicIpCheckRequest request, CancellationToken cancellationToken = default);
}

public sealed record PublicIpCheckRequest(
    bool Enabled,
    Uri Endpoint,
    TimeSpan CheckInterval,
    string LastKnownIp,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<string> ServerConfigPaths);

public sealed record PublicIpCheckResult(
    bool Checked,
    bool Changed,
    bool Success,
    string? CurrentIp,
    string? PreviousIp,
    DateTimeOffset? CheckedAt,
    string Message,
    TimeSpan? RetryAfter = null)
{
    public static PublicIpCheckResult Skipped(string message)
    {
        return new PublicIpCheckResult(false, false, true, null, null, null, message);
    }

    public static PublicIpCheckResult Failed(string message, DateTimeOffset checkedAt, TimeSpan retryAfter)
    {
        return new PublicIpCheckResult(true, false, false, null, null, checkedAt, message, retryAfter);
    }
}
