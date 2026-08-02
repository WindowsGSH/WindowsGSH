namespace WindowsGSH.Core.Metrics;

/// <summary>
/// Fixed-capacity ring buffer that drops the oldest entry when full.
/// All public members are thread-safe.
/// </summary>
public sealed class MetricHistoryBuffer<T>
{
    private readonly object _sync = new();
    private readonly T[] _items;
    private int _head;
    private int _count;

    public MetricHistoryBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_sync) { return _count; }
        }
    }

    public void Add(T item)
    {
        lock (_sync)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length)
            {
                _count++;
            }
        }
    }

    /// <summary>Returns items in chronological order (oldest first).</summary>
    public IReadOnlyList<T> ToReadOnlyList()
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                return [];
            }

            var result = new T[_count];
            if (_count < _items.Length)
            {
                Array.Copy(_items, 0, result, 0, _count);
            }
            else
            {
                var tail = _head;
                for (var i = 0; i < _count; i++)
                {
                    result[i] = _items[tail];
                    tail = (tail + 1) % _items.Length;
                }
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_items, 0, _items.Length);
            _head = 0;
            _count = 0;
        }
    }
}
