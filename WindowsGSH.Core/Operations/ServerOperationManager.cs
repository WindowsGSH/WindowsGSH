using WindowsGSH.Core.Events;

namespace WindowsGSH.Core.Operations;

public sealed class ServerOperationManager : IServerOperationCoordinator
{
    private const string GlobalOperationId = "__global__";
    private const string GlobalOperationName = "Global";

    public static ServerOperationManager Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, ServerOperationSnapshot> _operations = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellations = [];
    private readonly List<ServerOperationSnapshot> _history = [];
    private readonly Action<ServerOperationSnapshot> _historySink;
    private readonly IWindowsGshEventBus _events;

    public ServerOperationManager(Action<ServerOperationSnapshot>? historySink = null, IWindowsGshEventBus? events = null)
    {
        _historySink = historySink ?? OperationHistoryLog.Add;
        _events = events ?? WindowsGshEventBus.Shared;
    }

    public bool TryBegin(
        string serverId,
        string serverName,
        ServerOperationKind kind,
        out ServerOperationScope? scope,
        out ServerOperationSnapshot? existing,
        string? description = null)
    {
        return TryBeginServerOperation(serverId, serverName, kind, out scope, out existing, description);
    }

    public bool TryBeginServerOperation(
        string serverId,
        string serverName,
        ServerOperationKind kind,
        out ServerOperationScope? scope,
        out ServerOperationSnapshot? existing,
        string? description = null)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(GlobalOperationId, out existing) && existing.IsActive)
            {
                scope = null;
                return false;
            }

            if (_operations.TryGetValue(serverId, out existing) && existing.IsActive)
            {
                if (kind != ServerOperationKind.ForceStop)
                {
                    scope = null;
                    return false;
                }

                InterruptOperation(serverId, existing, "Interrupted by force stop.");
            }

            var operationId = Guid.NewGuid().ToString("N");
            var operation = new ServerOperationSnapshot(
                serverId,
                serverName,
                kind,
                GetDefaultStatus(kind),
                DateTimeOffset.UtcNow,
                null,
                IsActive: true,
                Description: description,
                OperationId: operationId);
            _operations[serverId] = operation;
            var cancellation = new CancellationTokenSource();
            _cancellations[serverId] = cancellation;
            scope = new ServerOperationScope(this, serverId, operationId, cancellation.Token);
            existing = null;
            PublishOperationChanged(operation);
            return true;
        }
    }

    public bool TryBeginGlobalOperation(
        ServerOperationKind kind,
        out ServerOperationScope? scope,
        out ServerOperationSnapshot? existing,
        string? description = null)
    {
        lock (_sync)
        {
            existing = _operations.Values
                .Where(operation => operation.IsActive)
                .OrderBy(operation => operation.StartedAt)
                .FirstOrDefault();
            if (existing != null)
            {
                scope = null;
                return false;
            }

            var operationId = Guid.NewGuid().ToString("N");
            var operation = new ServerOperationSnapshot(
                GlobalOperationId,
                GlobalOperationName,
                kind,
                GetDefaultStatus(kind),
                DateTimeOffset.UtcNow,
                null,
                IsActive: true,
                Description: description,
                OperationId: operationId);
            _operations[GlobalOperationId] = operation;
            var cancellation = new CancellationTokenSource();
            _cancellations[GlobalOperationId] = cancellation;
            scope = new ServerOperationScope(this, GlobalOperationId, operationId, cancellation.Token);
            PublishOperationChanged(operation);
            return true;
        }
    }

    public void UpdateStatus(string serverId, string status)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(serverId, out var operation))
            {
                var updated = operation with { Status = status };
                _operations[serverId] = updated;
                PublishOperationChanged(updated);
            }
        }
    }

    public void Complete(string serverId)
    {
        CompleteOperation(serverId);
    }

    public ServerOperationResult? CompleteOperation(string serverId)
    {
        return CompleteOperation(serverId, operationId: null);
    }

    internal ServerOperationResult? CompleteOperation(string serverId, string? operationId)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(serverId, out var operation))
            {
                if (operationId != null &&
                    !string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
                {
                    return null;
                }

                if (!operation.IsActive)
                {
                    // Already completed - e.g. a Start operation whose ServerLifecycleStartOptions.ClearStatus
                    // callback completes it mid-flow (ServerLifecycleService.StartAsync, right after the process
                    // launches), followed by ServerOperationsController's own ServerOperationScope.Dispose() in
                    // its finally block completing the same operationId again once the method returns. Without
                    // this guard, the second call re-published a second identical ServerOperationChangedEvent,
                    // producing the duplicate "Server start completed" Discord alerts a real deployment reported
                    // (only Start has an internal ClearStatus completion path; every other operation kind only
                    // ever completes once, via the scope's own Dispose()). A no-op returning the existing
                    // terminal snapshot is correct regardless of which caller happens to complete second.
                    return ServerOperationResult.FromSnapshot(operation);
                }

                var completedStatus = string.Equals(operation.Status, "Cancelling", StringComparison.Ordinal)
                    ? "Cancelled"
                    : "Completed";
                var completed = operation with { Status = completedStatus, IsActive = false, FinishedAt = DateTimeOffset.UtcNow };
                // Keep the terminal snapshot in the map (inactive) so /operation polling
                // can distinguish Cancelled/Completed from "no operation ever ran".
                _operations[serverId] = completed;
                AddHistory(completed);
                PublishOperationChanged(completed);
                if (_cancellations.Remove(serverId, out var completedCancellation))
                {
                    completedCancellation.Dispose();
                }

                return ServerOperationResult.FromSnapshot(completed);
            }

            if (_cancellations.Remove(serverId, out var orphanedCancellation))
            {
                orphanedCancellation.Dispose();
            }

            return null;
        }
    }

    public void Fail(string serverId, Exception exception)
    {
        FailOperation(serverId, exception);
    }

    public ServerOperationResult? FailOperation(string serverId, Exception exception)
    {
        return FailOperation(serverId, operationId: null, exception);
    }

    internal ServerOperationResult? FailOperation(string serverId, string? operationId, Exception exception)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(serverId, out var operation))
            {
                if (operationId != null &&
                    !string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
                {
                    return null;
                }

                var failed = operation with
                {
                    Status = "Failed",
                    LastError = exception.Message,
                    IsActive = false,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                _operations[serverId] = failed;
                AddHistory(failed);
                PublishOperationChanged(failed);
                if (_cancellations.Remove(serverId, out var failedCancellation))
                {
                    failedCancellation.Dispose();
                }

                return ServerOperationResult.FromSnapshot(failed);
            }

            if (_cancellations.Remove(serverId, out var orphanedCancellation))
            {
                orphanedCancellation.Dispose();
            }

            return null;
        }
    }

    public bool Cancel(string serverId)
    {
        return CancelOperation(serverId);
    }

    public bool CancelOperation(string serverId)
    {
        lock (_sync)
        {
            if (!_cancellations.TryGetValue(serverId, out var cancellation))
            {
                return false;
            }

            cancellation.Cancel();
            if (_operations.TryGetValue(serverId, out var operation))
            {
                var cancelling = operation with { Status = "Cancelling" };
                _operations[serverId] = cancelling;
                PublishOperationChanged(cancelling);
            }

            return true;
        }
    }

    // Cancels the active operation and stamps auditDescription into the snapshot description,
    // so the completed history row records who triggered the cancellation.
    public bool TryCancelWithAudit(string serverId, string auditDescription)
    {
        lock (_sync)
        {
            if (!_cancellations.TryGetValue(serverId, out var cancellation))
                return false;

            cancellation.Cancel();
            if (_operations.TryGetValue(serverId, out var operation))
            {
                var updatedDescription = string.IsNullOrWhiteSpace(operation.Description)
                    ? auditDescription
                    : $"{operation.Description}; {auditDescription}";
                var cancelling = operation with { Status = "Cancelling", Description = updatedDescription };
                _operations[serverId] = cancelling;
                PublishOperationChanged(cancelling);
            }

            return true;
        }
    }

    public ServerOperationSnapshot? Get(string serverId)
    {
        lock (_sync)
        {
            return _operations.TryGetValue(serverId, out var operation) ? operation : null;
        }
    }

    public IReadOnlyList<ServerOperationSnapshot> GetActive()
    {
        return GetRunningOperations();
    }

    public IReadOnlyList<ServerOperationSnapshot> GetRunningOperations()
    {
        lock (_sync)
        {
            return _operations.Values
                .Where(operation => operation.IsActive)
                .OrderBy(operation => operation.StartedAt)
                .ToArray();
        }
    }

    public bool IsOperationRunning(string serverId)
    {
        lock (_sync)
        {
            return _operations.TryGetValue(serverId, out var operation) && operation.IsActive;
        }
    }

    public bool IsAnyOperationRunning()
    {
        lock (_sync)
        {
            return _operations.Values.Any(operation => operation.IsActive);
        }
    }

    public string? GetLastOperationError(string serverId)
    {
        lock (_sync)
        {
            if (_operations.TryGetValue(serverId, out var operation))
            {
                return operation.LastError;
            }

            return _history
                .Where(operation => string.Equals(operation.ServerId, serverId, StringComparison.Ordinal))
                .OrderByDescending(operation => operation.FinishedAt ?? operation.StartedAt)
                .FirstOrDefault(operation => !string.IsNullOrWhiteSpace(operation.LastError))
                ?.LastError;
        }
    }

    public IReadOnlyList<ServerOperationSnapshot> GetHistory(int maxCount = 100)
    {
        lock (_sync)
        {
            return _history
                .OrderByDescending(operation => operation.FinishedAt ?? operation.StartedAt)
                .Take(maxCount)
                .ToArray();
        }
    }

    private void InterruptOperation(string serverId, ServerOperationSnapshot operation, string reason)
    {
        if (_cancellations.Remove(serverId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        var interrupted = operation with
        {
            Status = "Interrupted",
            LastError = reason,
            IsActive = false,
            FinishedAt = DateTimeOffset.UtcNow
        };
        _operations.Remove(serverId);
        AddHistory(interrupted);
        PublishOperationChanged(interrupted);
    }

    private void AddHistory(ServerOperationSnapshot operation)
    {
        _history.Add(operation);
        try
        {
            _historySink(operation);
        }
        catch
        {
            // Operation history persistence should not block server operations.
        }

        if (_history.Count > 200)
        {
            _history.RemoveRange(0, _history.Count - 200);
        }
    }

    private static string GetDefaultStatus(ServerOperationKind kind)
    {
        return kind switch
        {
            ServerOperationKind.Start => "Starting",
            ServerOperationKind.Stop => "Stopping",
            ServerOperationKind.Restart => "Restarting",
            ServerOperationKind.Update => "Updating",
            ServerOperationKind.Backup => "Backing up",
            ServerOperationKind.Restore => "Restoring",
            ServerOperationKind.Delete => "Deleting",
            ServerOperationKind.Addon => "Updating addon",
            ServerOperationKind.Import => "Importing",
            ServerOperationKind.Rcon => "Running RCON",
            ServerOperationKind.ConsoleCommand => "Running console command",
            ServerOperationKind.ApiAction => "Running API action",
            ServerOperationKind.ExternalCommand => "Running external command",
            ServerOperationKind.Install => "Installing",
            ServerOperationKind.ForceStop => "Force stopping",
            ServerOperationKind.Verify => "Verifying files",
            _ => "Working"
        };
    }

    private void PublishOperationChanged(ServerOperationSnapshot operation)
    {
        _events.Publish(new ServerOperationChangedEvent(
            DateTimeOffset.UtcNow,
            operation.ServerId,
            null,
            operation.ServerName,
            operation.Kind,
            operation.State,
            operation.DisplayText,
            operation.Description,
            operation.LastError));
    }
}

public sealed class ServerOperationScope : IDisposable
{
    private readonly ServerOperationManager _manager;
    private readonly string _serverId;
    private readonly string _operationId;
    private bool _disposed;

    internal ServerOperationScope(ServerOperationManager manager, string serverId, string operationId, CancellationToken cancellationToken)
    {
        _manager = manager;
        _serverId = serverId;
        _operationId = operationId;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.FailOperation(_serverId, _operationId, exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.CompleteOperation(_serverId, _operationId);
    }
}
