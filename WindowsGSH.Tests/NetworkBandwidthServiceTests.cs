using WindowsGSH.Core.Metrics;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class NetworkBandwidthServiceTests
{
    [Fact]
    public void First_sample_returns_null_rates()
    {
        var svc = new NetworkBandwidthService();
        var (upload, download) = svc.Sample();
        Assert.Null(upload);
        Assert.Null(download);
    }

    [Fact]
    public void Rate_calculation_divides_delta_by_elapsed_seconds()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(4);
        var counters = new Queue<Dictionary<string, (long Sent, long Received)>>(
        [
            IFaces(("eth0", 0, 0)),
            IFaces(("eth0", 4000, 8000)),
        ]);
        var svc = BuildService([t0, t1], () => counters.Dequeue());

        svc.Sample(); // baseline
        var (upload, download) = svc.Sample();

        Assert.Equal(1000.0, upload);
        Assert.Equal(2000.0, download);
    }

    [Fact]
    public void Zero_delta_returns_zero_not_null()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(4);
        var counters = new Queue<Dictionary<string, (long Sent, long Received)>>(
        [
            IFaces(("eth0", 0, 0)),
            IFaces(("eth0", 0, 0)),
        ]);
        var svc = BuildService([t0, t1], () => counters.Dequeue());

        svc.Sample(); // baseline
        var (upload, download) = svc.Sample();

        Assert.Equal(0.0, upload);
        Assert.Equal(0.0, download);
    }

    [Fact]
    public void Negative_delta_counter_reset_returns_zero_not_null()
    {
        // A counter reset on a single interface contributes 0 (skipped), not null.
        // The chart shows a flat line rather than a gap for a counter reset.
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(4);
        var counters = new Queue<Dictionary<string, (long Sent, long Received)>>(
        [
            IFaces(("eth0", 10000, 10000)),
            IFaces(("eth0", 500, 500)),     // counter reset (decreased)
        ]);
        var svc = BuildService([t0, t1], () => counters.Dequeue());

        svc.Sample(); // baseline at 10000
        var (upload, download) = svc.Sample();

        // Negative delta is skipped per-interface → contributes 0 to aggregate → 0 B/s
        Assert.Equal(0.0, upload);
        Assert.Equal(0.0, download);
    }

    [Fact]
    public void Zero_elapsed_returns_null()
    {
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var counters = new Queue<Dictionary<string, (long Sent, long Received)>>(
        [
            IFaces(("eth0", 0, 0)),
            IFaces(("eth0", 1000, 1000)),
        ]);
        var svc = BuildService([t0, t0], () => counters.Dequeue()); // same timestamp twice

        svc.Sample(); // baseline
        var (upload, download) = svc.Sample();

        Assert.Null(upload);
        Assert.Null(download);
    }

    [Fact]
    public void New_adapter_appearing_between_samples_does_not_spike()
    {
        // eth0 has 1000 B/s real traffic.
        // vpn0 appears in the second sample with 50 MB lifetime bytes already on the counter.
        // vpn0 should be baselined and contribute 0 to the spike, not 50 MB/s.
        var t0 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(4);
        var counters = new Queue<Dictionary<string, (long Sent, long Received)>>(
        [
            IFaces(("eth0", 0, 0)),
            IFaces(("eth0", 4000, 4000), ("vpn0", 50_000_000, 50_000_000)),
        ]);
        var svc = BuildService([t0, t1], () => counters.Dequeue());

        svc.Sample(); // baseline — only eth0
        var (upload, download) = svc.Sample();

        // Only eth0 delta counted → 4000 B / 4 s = 1000 B/s
        Assert.Equal(1000.0, upload);
        Assert.Equal(1000.0, download);
    }

    private static NetworkBandwidthService BuildService(
        IEnumerable<DateTimeOffset> times,
        Func<Dictionary<string, (long Sent, long Received)>> readCounters)
    {
        var queue = new Queue<DateTimeOffset>(times);
        return new NetworkBandwidthServiceTestable(
            utcNow: () => queue.Dequeue(),
            readCounters: readCounters);
    }

    private static Dictionary<string, (long Sent, long Received)> IFaces(
        params (string Id, long Sent, long Received)[] entries)
    {
        return entries.ToDictionary(e => e.Id, e => (e.Sent, e.Received));
    }

    private sealed class NetworkBandwidthServiceTestable : NetworkBandwidthService
    {
        private readonly Func<Dictionary<string, (long Sent, long Received)>> _readCounters;

        public NetworkBandwidthServiceTestable(
            Func<DateTimeOffset> utcNow,
            Func<Dictionary<string, (long Sent, long Received)>> readCounters)
            : base(utcNow)
        {
            _readCounters = readCounters;
        }

        protected override Dictionary<string, (long Sent, long Received)> ReadPerInterfaceCounters()
            => _readCounters();
    }
}
