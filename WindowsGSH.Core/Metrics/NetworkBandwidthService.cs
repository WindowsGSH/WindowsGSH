using System.Net.NetworkInformation;

namespace WindowsGSH.Core.Metrics;

public class NetworkBandwidthService
{
    private readonly Func<DateTimeOffset> _utcNow;
    private Dictionary<string, (long Sent, long Received)> _prevCounters = [];
    private DateTimeOffset _prevSampledAt;
    private bool _hasPreviousSample;

    public NetworkBandwidthService(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Sample current network bandwidth. Returns null on the first call (no delta available).
    /// New interfaces appearing between samples are baselined and contribute 0 to that sample's
    /// delta, preventing lifetime-counter spikes when adapters connect mid-session.
    /// </summary>
    public (double? UploadBytesPerSecond, double? DownloadBytesPerSecond) Sample()
    {
        var now = _utcNow();
        var current = ReadPerInterfaceCounters();

        if (!_hasPreviousSample)
        {
            _prevCounters = current;
            _prevSampledAt = now;
            _hasPreviousSample = true;
            return (null, null);
        }

        var elapsedSeconds = (now - _prevSampledAt).TotalSeconds;
        var prev = _prevCounters;
        _prevCounters = current;
        _prevSampledAt = now;

        if (elapsedSeconds <= 0)
        {
            return (null, null);
        }

        long sentDelta = 0;
        long receivedDelta = 0;

        foreach (var (id, counters) in current)
        {
            if (!prev.TryGetValue(id, out var prevCounters))
            {
                // New interface: baseline it. Its lifetime bytes are not traffic in this window.
                continue;
            }

            var sd = counters.Sent - prevCounters.Sent;
            var rd = counters.Received - prevCounters.Received;

            // Skip negative deltas — counter reset or interface reconnect.
            if (sd >= 0) sentDelta += sd;
            if (rd >= 0) receivedDelta += rd;
        }

        return (sentDelta / elapsedSeconds, receivedDelta / elapsedSeconds);
    }

    protected virtual Dictionary<string, (long Sent, long Received)> ReadPerInterfaceCounters()
    {
        var result = new Dictionary<string, (long Sent, long Received)>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                try
                {
                    var stats = iface.GetIPv4Statistics();
                    result[iface.Id] = (stats.BytesSent, stats.BytesReceived);
                }
                catch
                {
                    // Skip interfaces that fail to report
                }
            }
        }
        catch
        {
            // No interfaces available
        }

        return result;
    }
}
