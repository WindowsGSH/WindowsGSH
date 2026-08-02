namespace WindowsGSH.Core.Events;

public static class WindowsGshEventLogBridge
{
    public static IDisposable Connect(IWindowsGshEventBus bus, Action<string, string?> log)
    {
        return bus.Subscribe<ServerLogEvent>(message =>
        {
            var text = message.ExceptionText == null
                ? message.Message
                : $"{message.Message}: {message.ExceptionText}";
            log(text, message.ServerId);
        });
    }
}

public sealed class WindowsGshEventLogThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();
    private readonly Dictionary<string, ThrottleState> _states = [];

    public WindowsGshEventLogThrottle(TimeSpan interval, Func<DateTimeOffset>? utcNow = null)
    {
        _interval = interval;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public bool ShouldPublish(string key, string message)
    {
        var now = _utcNow();
        lock (_sync)
        {
            if (_states.TryGetValue(key, out var previous) &&
                string.Equals(previous.Message, message, StringComparison.Ordinal) &&
                now - previous.PublishedAt < _interval)
            {
                return false;
            }

            _states[key] = new ThrottleState(message, now);
            return true;
        }
    }

    public bool PublishIfDue(
        IWindowsGshEventBus bus,
        string key,
        ServerLogEvent message)
    {
        if (!ShouldPublish(key, message.Message))
        {
            return false;
        }

        bus.Publish(message);
        return true;
    }

    private sealed record ThrottleState(string Message, DateTimeOffset PublishedAt);
}
