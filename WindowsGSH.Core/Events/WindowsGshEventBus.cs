namespace WindowsGSH.Core.Events;

public sealed class WindowsGshEventBus : IWindowsGshEventBus
{
    public static WindowsGshEventBus Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(typeof(TEvent), message => handler((TEvent)message), Remove);
        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var subscriptions))
            {
                subscriptions = [];
                _subscriptions[typeof(TEvent)] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish<TEvent>(TEvent message)
    {
        if (message == null)
        {
            return;
        }

        Subscription[] subscriptions;
        lock (_sync)
        {
            subscriptions = _subscriptions
                .Where(pair => pair.Key.IsAssignableFrom(typeof(TEvent)))
                .SelectMany(pair => pair.Value)
                .ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Invoke(message);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var subscriptions))
            {
                return;
            }

            subscriptions.Remove(subscription);
            if (subscriptions.Count == 0)
            {
                _subscriptions.Remove(subscription.EventType);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action<object> _handler;
        private readonly Action<Subscription> _remove;
        private bool _disposed;

        public Subscription(Type eventType, Action<object> handler, Action<Subscription> remove)
        {
            EventType = eventType;
            _handler = handler;
            _remove = remove;
        }

        public Type EventType { get; }

        public void Invoke(object message)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _handler(message);
            }
            catch
            {
                // Event subscribers must not break server control paths.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _remove(this);
        }
    }
}
