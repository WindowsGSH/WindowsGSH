namespace WindowsGSH.Core.Discord;

public sealed class DiscordStopAllConfirmationStore
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<Scope, DateTimeOffset> _pendingConfirmations = [];

    public DiscordStopAllConfirmationStore()
        : this(DefaultTimeout, () => DateTimeOffset.UtcNow)
    {
    }

    public DiscordStopAllConfirmationStore(TimeSpan timeout, Func<DateTimeOffset> utcNow)
    {
        _timeout = timeout;
        _utcNow = utcNow;
    }

    public void Request(string userId, string? guildId, string channelId)
    {
        ClearExpired();
        _pendingConfirmations[CreateScope(userId, guildId, channelId)] = _utcNow().Add(_timeout);
    }

    public bool TryConfirm(string userId, string? guildId, string channelId)
    {
        ClearExpired();
        var scope = CreateScope(userId, guildId, channelId);
        if (!_pendingConfirmations.Remove(scope, out var expiresUtc))
        {
            return false;
        }

        return expiresUtc >= _utcNow();
    }

    private void ClearExpired()
    {
        var now = _utcNow();
        foreach (var expired in _pendingConfirmations
                     .Where(item => item.Value < now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _pendingConfirmations.Remove(expired);
        }
    }

    private static Scope CreateScope(string userId, string? guildId, string channelId)
    {
        return new Scope(userId, guildId ?? string.Empty, channelId);
    }

    private readonly record struct Scope(string UserId, string GuildId, string ChannelId);
}
