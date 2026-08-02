using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ServerTelemetryService
{
    private readonly IServerMetricsService _metricsService;

    public ServerTelemetryService(IServerMetricsService? metricsService = null)
    {
        _metricsService = metricsService ?? new ServerMetricsService();
    }

    public ServerTelemetrySnapshot GetSnapshot(IGameServerModule? module, ServerInstance instance, int? maxPlayers)
    {
        var metrics = _metricsService.Sample(module, instance, maxPlayers);
        return new ServerTelemetrySnapshot(
            metrics.PrimaryProcessId,
            metrics.CpuPercent,
            metrics.MemoryBytes,
            OnlinePlayers: null,
            MaxPlayers: maxPlayers,
            Uptime: metrics.Uptime);
    }

    public ServerTelemetrySnapshot GetSnapshot(IGameServerModule? module, string installPath, int? maxPlayers)
    {
        var instance = new ServerInstance(
            Path.GetFullPath(installPath),
            module?.Name ?? "Server",
            module?.Id ?? "unknown",
            Path.GetDirectoryName(Path.GetFullPath(installPath)) ?? Path.GetFullPath(installPath),
            installPath,
            string.Empty,
            new Dictionary<string, object?>());
        return GetSnapshot(module, instance, maxPlayers);
    }
}
