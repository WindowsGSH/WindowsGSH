using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public interface IServerMetricsService
{
    ServerMetricsSnapshot Sample(IGameServerModule? module, ServerInstance instance, int? maxPlayers = null);

    ServerMetricsSnapshot? GetLatest(string serverId);

    IReadOnlyList<ServerMetricsSnapshot> GetHistory(string serverId);

    void RemoveSeries(string serverId);

    void PruneStale(DateTimeOffset olderThan);
}

public sealed record ServerMetricsSnapshot(
    string ServerId,
    string ServerName,
    string ModuleId,
    DateTimeOffset SampledAt,
    int? PrimaryProcessId,
    string? PrimaryProcessName,
    double? CpuPercent,
    long? MemoryBytes,
    TimeSpan? Uptime,
    int? ThreadCount,
    int? MaxPlayers)
{
    public string ProcessIdText => PrimaryProcessId?.ToString() ?? "--";

    public string CpuText => CpuPercent.HasValue ? $"{CpuPercent.Value:0.0}%" : "--";

    public string MemoryText => MemoryBytes.HasValue ? FormatBytes(MemoryBytes.Value) : "--";

    public string PlayersText => $"-- / {MaxPlayers?.ToString() ?? "--"}";

    public string UptimeText => Uptime.HasValue ? FormatUptime(Uptime.Value) : "--";

    private static string FormatBytes(long bytes)
    {
        var value = bytes / 1024d / 1024d;
        return value >= 1024
            ? $"{value / 1024d:0.0} GB"
            : $"{value:0} MB";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }

        return $"{Math.Max(0, (int)uptime.TotalMinutes)}m";
    }
}

public sealed record ProcessMetricsSample(
    int ProcessId,
    string ProcessName,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    DateTimeOffset StartTime,
    int ThreadCount);
