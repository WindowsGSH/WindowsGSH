using WindowsGSH.Core.Health;

namespace WindowsGSH.Core.Metrics;

/// <summary>
/// Bounded in-memory history for host and app process metrics.
/// Defaults to 900 samples (~1 hour at 4-second intervals).
/// </summary>
public sealed class SystemMetricHistory
{
    private readonly MetricHistoryBuffer<SystemMetricsSnapshot> _buffer;

    public SystemMetricHistory(int capacity = 900)
    {
        _buffer = new MetricHistoryBuffer<SystemMetricsSnapshot>(capacity);
    }

    public int Capacity => _buffer.Capacity;

    public void Add(SystemMetricsSnapshot snapshot)
    {
        _buffer.Add(snapshot);
    }

    public IReadOnlyList<SystemMetricsSnapshot> GetHistory() => _buffer.ToReadOnlyList();
}
