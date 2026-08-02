namespace WindowsGSH.Core.Servers;

public sealed record ServerTelemetrySnapshot(
    int? ProcessId,
    double? CpuPercent,
    long? MemoryBytes,
    int? OnlinePlayers,
    int? MaxPlayers,
    TimeSpan? Uptime = null)
{
    public string ProcessIdText => ProcessId?.ToString() ?? "--";

    public string CpuText => CpuPercent.HasValue ? $"{CpuPercent.Value:0.0}%" : "--";

    public string MemoryText => MemoryBytes.HasValue ? FormatBytes(MemoryBytes.Value) : "--";

    public string PlayersText => OnlinePlayers.HasValue
        ? $"{OnlinePlayers.Value} / {MaxPlayers?.ToString() ?? "--"}"
        : $"-- / {MaxPlayers?.ToString() ?? "--"}";

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
