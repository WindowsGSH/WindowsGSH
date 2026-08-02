namespace WindowsGSH.Core.Discord;

public sealed class DiscordCommandRateLimiter
{
    public const string RateLimitedMessage = "Please wait a few seconds before running that command again.";
    public static readonly TimeSpan ReadOnlyCooldown = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DestructiveCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StopAllCooldown = TimeSpan.FromSeconds(60);
    private readonly object _syncRoot = new();
    private readonly Dictionary<DiscordCommandRateLimitKey, DateTimeOffset> _lastRuns = [];
    private readonly Func<DateTimeOffset> _utcNow;

    public DiscordCommandRateLimiter()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    public DiscordCommandRateLimiter(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow;
    }

    public bool TryAcquire(string userId, string command, string? serverId, out TimeSpan retryAfter)
    {
        command = string.IsNullOrWhiteSpace(command) ? "help" : command.ToLowerInvariant();
        var key = new DiscordCommandRateLimitKey(
            string.IsNullOrWhiteSpace(userId) ? string.Empty : userId,
            command,
            string.IsNullOrWhiteSpace(serverId) ? string.Empty : serverId);
        var cooldown = GetCooldown(command);
        var now = _utcNow();

        lock (_syncRoot)
        {
            PruneExpired(now);
            if (_lastRuns.TryGetValue(key, out var lastRun))
            {
                var elapsed = now - lastRun;
                if (elapsed < cooldown)
                {
                    retryAfter = cooldown - elapsed;
                    return false;
                }
            }

            _lastRuns[key] = now;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public static TimeSpan GetCooldown(string command)
    {
        if (string.Equals(command, "stopall", StringComparison.OrdinalIgnoreCase))
        {
            return StopAllCooldown;
        }

        return DiscordCommandPermissions.IsDestructiveCommand(command)
            ? DestructiveCooldown
            : ReadOnlyCooldown;
    }

    public static bool RequiresAcquire(string command, IReadOnlyList<string> arguments)
    {
        return !string.Equals(command, "stopall", StringComparison.OrdinalIgnoreCase) ||
               arguments.Count == 0 ||
               !string.Equals(arguments[0], "confirm", StringComparison.OrdinalIgnoreCase);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        var oldest = now - StopAllCooldown;
        foreach (var item in _lastRuns.Where(item => item.Value < oldest).ToArray())
        {
            _lastRuns.Remove(item.Key);
        }
    }

    private sealed record DiscordCommandRateLimitKey(string UserId, string Command, string ServerId);
}
