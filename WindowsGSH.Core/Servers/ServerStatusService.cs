using WindowsGSH.Core.Events;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ServerStatusService : IServerStatusService
{
    private readonly Func<IGameServerModule, string, bool> _isRunning;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _queryTimeout;
    private readonly TimeSpan _failureLogThrottle;
    private readonly IWindowsGshEventBus _events;
    private readonly object _sync = new();
    private readonly Dictionary<string, ServerStatusSnapshot> _cache = [];
    private readonly Dictionary<string, QueryFailureLogState> _failureLogs = [];

    public ServerStatusService(
        Func<IGameServerModule, string, bool>? isRunning = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? queryTimeout = null,
        TimeSpan? failureLogThrottle = null,
        IWindowsGshEventBus? events = null)
    {
        _isRunning = isRunning ?? ((module, installPath) => ServerProcessLocator.IsRunning(module, installPath));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(3);
        _failureLogThrottle = failureLogThrottle ?? TimeSpan.FromMinutes(5);
        _events = events ?? WindowsGshEventBus.Shared;
    }

    public async Task<ServerStatusSnapshot> GetStatusAsync(
        IGameServerModule? module,
        ServerInstance instance,
        bool hasUpdateAvailable,
        CancellationToken cancellationToken = default)
    {
        var checkedAt = _utcNow();
        var isProcessRunning = module != null && _isRunning(module, instance.InstallPath);
        QueryResult? queryResult = null;
        var queried = false;

        if (isProcessRunning && module?.Capabilities.SupportsQuery == true)
        {
            queried = true;
            queryResult = await QueryServerAsync(module, instance, cancellationToken).ConfigureAwait(false);
        }

        var status = GetStatus(module, isProcessRunning, queryResult, hasUpdateAvailable);
        var logMessage = CreateQueryStateMessage(module, instance, isProcessRunning, queryResult);
        var shouldLog = ShouldLog(instance.Id, logMessage, checkedAt);
        var lastSuccessfulQueryAt = queryResult?.Status == ModuleServerStatus.Online
            ? checkedAt
            : GetCachedStatus(instance.Id)?.LastSuccessfulQueryAt;

        var snapshot = new ServerStatusSnapshot(
            instance.Id,
            instance.Name,
            instance.ModuleId,
            status,
            isProcessRunning,
            queried,
            queryResult?.Status,
            queryResult?.OnlinePlayers,
            queryResult?.MaxPlayers,
            queryResult?.Version,
            queryResult?.Message,
            queryResult?.Map,
            queryResult?.Game,
            queryResult?.QueryDurationMilliseconds,
            queryResult?.Players,
            queryResult?.DetailMessage,
            queryResult?.Protocol ?? module?.Runtime.QueryProtocol,
            GetCurrentStatusText(status, hasUpdateAvailable),
            GetStatusText(status, hasUpdateAvailable),
            GetStatusBrushKey(status),
            checkedAt,
            lastSuccessfulQueryAt,
            shouldLog,
            shouldLog ? logMessage : null);

        ServerStatusSnapshot? previous;
        lock (_sync)
        {
            _cache.TryGetValue(instance.Id, out previous);
            _cache[instance.Id] = snapshot;
        }

        if (ShouldPublishStatusChanged(previous, snapshot))
        {
            _events.Publish(new ServerStatusChangedEvent(
                snapshot.CheckedAt,
                snapshot.ServerId,
                snapshot.ModuleId,
                snapshot.ServerName,
                snapshot.Status,
                snapshot.IsProcessRunning,
                snapshot.Queried,
                snapshot.StatusText,
                snapshot.Message ?? snapshot.LogMessage));
        }

        return snapshot;
    }

    public ServerStatusSnapshot? GetCachedStatus(string serverId)
    {
        lock (_sync)
        {
            return _cache.TryGetValue(serverId, out var snapshot) ? snapshot : null;
        }
    }

    private async Task<QueryResult> QueryServerAsync(
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_queryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await module.QueryAsync(instance, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: "Query timed out.");
        }
        catch (Exception ex)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: ex.Message);
        }
    }

    private bool ShouldLog(string serverId, string? message, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                _failureLogs.Remove(serverId);
                return false;
            }

            if (_failureLogs.TryGetValue(serverId, out var previous) &&
                string.Equals(previous.Message, message, StringComparison.Ordinal) &&
                now - previous.LoggedAt < _failureLogThrottle)
            {
                return false;
            }

            _failureLogs[serverId] = new QueryFailureLogState(message, now);
            return true;
        }
    }

    private static bool ShouldPublishStatusChanged(ServerStatusSnapshot? previous, ServerStatusSnapshot current)
    {
        return previous == null ||
            previous.Status != current.Status ||
            previous.IsProcessRunning != current.IsProcessRunning ||
            !string.Equals(previous.StatusText, current.StatusText, StringComparison.Ordinal) ||
            !string.Equals(previous.Message, current.Message, StringComparison.Ordinal);
    }

    private static string? CreateQueryStateMessage(
        IGameServerModule? module,
        ServerInstance instance,
        bool isProcessRunning,
        QueryResult? queryResult)
    {
        if (module == null)
        {
            return $"Query skipped for {instance.Name}: module '{instance.ModuleId}' is not loaded.";
        }

        if (!module.Capabilities.SupportsQuery)
        {
            return null;
        }

        if (!isProcessRunning)
        {
            return null;
        }

        if (queryResult == null)
        {
            return $"Query skipped for {instance.Name}: no query result was produced.";
        }

        var protocol = module.Runtime.QueryProtocol ?? "module query";
        var message = $"Query {instance.Name} via {protocol}: {queryResult.Status}";
        if (queryResult.OnlinePlayers.HasValue || queryResult.MaxPlayers.HasValue)
        {
            message += $" ({queryResult.OnlinePlayers?.ToString() ?? "--"} / {queryResult.MaxPlayers?.ToString() ?? "--"} players)";
        }

        if (!string.IsNullOrWhiteSpace(queryResult.Message))
        {
            message += $". {queryResult.Message}";
        }

        if (!string.IsNullOrWhiteSpace(queryResult.DetailMessage))
        {
            message += $" Detail: {queryResult.DetailMessage}";
        }

        return queryResult.Status == ModuleServerStatus.Online ? null : message;
    }

    private static ServerRuntimeStatus GetStatus(
        IGameServerModule? module,
        bool isProcessRunning,
        QueryResult? queryResult,
        bool hasUpdateAvailable)
    {
        if (module == null)
        {
            return ServerRuntimeStatus.Offline;
        }

        if (hasUpdateAvailable)
        {
            return ServerRuntimeStatus.Warning;
        }

        if (!isProcessRunning)
        {
            return ServerRuntimeStatus.Offline;
        }

        if (module.Capabilities.SupportsQuery && queryResult?.Status != ModuleServerStatus.Online)
        {
            return ServerRuntimeStatus.Warning;
        }

        return ServerRuntimeStatus.Running;
    }

    private static string GetCurrentStatusText(ServerRuntimeStatus status, bool hasUpdateAvailable)
    {
        return status switch
        {
            ServerRuntimeStatus.Running => "Online",
            ServerRuntimeStatus.Warning when hasUpdateAvailable => "Update available",
            ServerRuntimeStatus.Warning => "Warning",
            _ => "Offline"
        };
    }

    private static string GetStatusText(ServerRuntimeStatus status, bool hasUpdateAvailable)
    {
        return status switch
        {
            ServerRuntimeStatus.Running => "Running",
            ServerRuntimeStatus.Warning when hasUpdateAvailable => "Update available",
            ServerRuntimeStatus.Warning => "Issues found",
            _ => "Offline"
        };
    }

    private static string GetStatusBrushKey(ServerRuntimeStatus status)
    {
        return status switch
        {
            ServerRuntimeStatus.Running => "GoodBrush",
            ServerRuntimeStatus.Warning => "WarnBrush",
            _ => "BadBrush"
        };
    }

    private sealed record QueryFailureLogState(string Message, DateTimeOffset LoggedAt);
}

