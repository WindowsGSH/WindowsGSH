using WindowsGSH.Core.Health;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Web.Api;

/// <summary>
/// Thread-safe snapshot store written by MainWindow on each refresh cycle and read by
/// ASP.NET Core request threads. Intentionally static — same pattern as AppLogService
/// and ServerConsoleService.SharedInstance.
/// </summary>
public static class WebServerState
{
    private static IReadOnlyList<InstalledServer> _servers = [];
    private static readonly object _serversLock = new();

    private static IServerMetricsService? _serverMetrics;

    // Nullable DateTimeOffset pair: (lastStarted, lastStopped)
    private static Func<string, (DateTimeOffset? LastStarted, DateTimeOffset? LastStopped)>? _lifecycleQuery;

    private static SystemMetricsSnapshot? _hostMetrics;
    private static readonly object _hostMetricsLock = new();

    /// <summary>Called once at app startup so the API layer can query last-started/last-stopped times.</summary>
    public static void SetLifecycleQuery(Func<string, (DateTimeOffset? LastStarted, DateTimeOffset? LastStopped)> query)
    {
        _lifecycleQuery = query;
    }

    public static (DateTimeOffset? LastStarted, DateTimeOffset? LastStopped) GetServerLifecycleTimes(string serverId)
        => _lifecycleQuery?.Invoke(serverId) ?? default;

    /// <summary>Called once at app startup with the metrics service from InstalledServerLoader.</summary>
    public static void SetServerMetrics(IServerMetricsService metrics)
    {
        _serverMetrics = metrics;
    }

    /// <summary>Called by MainWindow each time the server list is refreshed.</summary>
    public static void UpdateServers(IReadOnlyList<InstalledServer> servers)
    {
        lock (_serversLock)
        {
            _servers = servers;
        }
    }

    /// <summary>Called by MainWindow each time the system metrics sampler fires.</summary>
    public static void UpdateHostMetrics(SystemMetricsSnapshot snapshot)
    {
        lock (_hostMetricsLock)
        {
            _hostMetrics = snapshot;
        }
    }

    public static IReadOnlyList<InstalledServer> GetServers()
    {
        lock (_serversLock)
        {
            return _servers;
        }
    }

    public static ServerMetricsSnapshot? GetServerMetrics(string serverId)
        => _serverMetrics?.GetLatest(serverId);

    public static SystemMetricsSnapshot? GetHostMetrics()
    {
        lock (_hostMetricsLock)
        {
            return _hostMetrics;
        }
    }
}
