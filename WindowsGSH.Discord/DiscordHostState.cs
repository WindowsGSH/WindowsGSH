namespace WindowsGSH.Discord;

using System.Text.RegularExpressions;

public enum DiscordHostState
{
    Disabled,
    Stopped,
    Connecting,
    Connected,
    Ready,
    Stopping,
    Failed
}

public sealed record DiscordHostStateSnapshot(
    DiscordHostState State,
    string? BotUsername,
    ulong? ApplicationId,
    string? LastFailure,
    DateTimeOffset ChangedAt)
{
    public bool IsTransitional => State is DiscordHostState.Connecting or DiscordHostState.Stopping;
    public bool CanStart => State is DiscordHostState.Stopped or DiscordHostState.Disabled or DiscordHostState.Failed;
    public bool CanStop => State is DiscordHostState.Connecting or DiscordHostState.Connected or DiscordHostState.Ready or DiscordHostState.Failed;
}

public sealed class DiscordHostStateTracker
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private DiscordHostStateSnapshot _current;

    public DiscordHostStateTracker(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _current = new DiscordHostStateSnapshot(
            DiscordHostState.Stopped,
            null,
            null,
            null,
            _utcNow());
    }

    public event EventHandler<DiscordHostStateSnapshot>? Changed;

    public DiscordHostStateSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void Transition(
        DiscordHostState state,
        string? botUsername = null,
        ulong? applicationId = null,
        string? failure = null)
    {
        DiscordHostStateSnapshot snapshot;
        lock (_sync)
        {
            snapshot = new DiscordHostStateSnapshot(
                state,
                botUsername,
                applicationId,
                SanitizeFailure(failure),
                _utcNow());
            _current = snapshot;
        }

        Changed?.Invoke(this, snapshot);
    }

    private static string? SanitizeFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return null;
        }

        var singleLine = string.Join(
            " ",
            failure.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        singleLine = Regex.Replace(
            singleLine,
            @"(?i)\b(token|password|secret)\s*[:=]\s*\S+",
            "$1=[REDACTED]");
        singleLine = Regex.Replace(
            singleLine,
            @"https://(?:discord(?:app)?\.com)/api/webhooks/\S+",
            "https://discord.com/api/webhooks/[REDACTED]",
            RegexOptions.IgnoreCase);
        singleLine = Regex.Replace(
            singleLine,
            @"\b(?:mfa\.[A-Za-z0-9_-]{20,}|[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{20,})\b",
            "[REDACTED]");
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "...";
    }
}

public sealed class DiscordLifecycleGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool TryEnter() => _semaphore.Wait(0);

    public Task EnterAsync() => _semaphore.WaitAsync();

    public void Exit() => _semaphore.Release();
}

public static class DiscordInviteLink
{
    public const ulong MinimumPermissions = 84_992;

    public static string? Create(ulong? applicationId)
    {
        return applicationId is > 0
            ? $"https://discord.com/oauth2/authorize?client_id={applicationId.Value}&permissions={MinimumPermissions}&scope=bot"
            : null;
    }
}
