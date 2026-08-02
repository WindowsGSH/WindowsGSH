using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public sealed class ServerStatusComposer
{
    private readonly object _sync = new();
    private readonly TimeSpan _serverBootGracePeriod;
    private readonly Dictionary<string, DateTimeOffset> _bootingUntil = [];

    public ServerStatusComposer(TimeSpan serverBootGracePeriod)
    {
        _serverBootGracePeriod = serverBootGracePeriod;
    }

    public void MarkBooting(string serverId, DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            _bootingUntil[serverId] = (now ?? DateTimeOffset.UtcNow).Add(_serverBootGracePeriod);
        }
    }

    public bool IsBooting(string serverId, DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            return _bootingUntil.TryGetValue(serverId, out var bootingUntil) &&
                bootingUntil > (now ?? DateTimeOffset.UtcNow);
        }
    }

    public void ClearBooting(string serverId)
    {
        lock (_sync)
        {
            _bootingUntil.Remove(serverId);
        }
    }

    public IReadOnlyList<InstalledServer> Apply(
        IReadOnlyList<InstalledServer> servers,
        Func<string, ServerOperationSnapshot?> getOperation,
        DateTimeOffset? now = null,
        Func<string, int?>? getLiveMonitoredProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(getOperation);

        var currentTime = now ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            return servers
                .Select(server => Apply(server, getOperation(server.Id), getLiveMonitoredProcessId?.Invoke(server.Id), currentTime))
                .ToArray();
        }
    }

    private InstalledServer Apply(InstalledServer server, ServerOperationSnapshot? operation, int? liveProcessId, DateTimeOffset now)
    {
        if (operation is { IsActive: true })
        {
            return server with
            {
                CurrentStatusText = operation.Status,
                IsOperationRunning = true,
                OperationText = operation.DisplayText,
                LastOperationError = operation.LastError,
                CanStart = false,
                CanStop = true
            };
        }

        if (operation is { IsActive: false })
        {
            server = server with
            {
                LastOperationError = operation.LastError,
                OperationText = operation.DisplayText
            };
        }

        // Query-enabled servers commonly have a tracked process before they answer queries. Keep
        // the existing boot-grace presentation authoritative until it expires; live PID evidence
        // must not erase the marker and expose a transient query Warning immediately after start.
        if (server.Status == ServerRuntimeStatus.Warning &&
            _bootingUntil.TryGetValue(server.Id, out var liveBootingUntil) &&
            liveBootingUntil > now)
        {
            return server with
            {
                ProcessId = liveProcessId is > 0
                    ? liveProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : server.ProcessId,
                CurrentStatusText = "Booting",
                Status = ServerRuntimeStatus.Running,
                StatusText = "Booting",
                StatusBrushKey = "GoodBrush",
                CanStart = false,
                CanStop = true
            };
        }

        // The runtime tracker owns processes returned directly by a module. Treat that live handle
        // as stronger evidence than a loader snapshot, which can temporarily be offline when its
        // persisted runtime identity could not be refreshed (notably java.exe outside the server
        // install directory). This also keeps the card's PID aligned with crash monitoring.
        if (liveProcessId is > 0)
        {
            _bootingUntil.Remove(server.Id);
            var withLiveProcess = server with
            {
                ProcessId = liveProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CanStart = false,
                CanStop = true
            };

            // A tracked process disproves only an Offline loader result. Warning carries useful
            // query/update diagnostics and Running is already correct, so preserve both states.
            return server.Status == ServerRuntimeStatus.Offline
                ? withLiveProcess with
                {
                    CurrentStatusText = "Online",
                    Status = ServerRuntimeStatus.Running,
                    StatusText = "Running",
                    StatusBrushKey = "GoodBrush"
                }
                : withLiveProcess;
        }

        if (server.Status == ServerRuntimeStatus.Running)
        {
            _bootingUntil.Remove(server.Id);
            return server;
        }

        if (_bootingUntil.TryGetValue(server.Id, out var expiredBootingUntil) &&
            expiredBootingUntil <= now)
        {
            _bootingUntil.Remove(server.Id);
        }

        return server;
    }
}
