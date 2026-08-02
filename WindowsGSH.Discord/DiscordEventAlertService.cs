using WindowsGSH.Core.Events;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using System.Text.RegularExpressions;

namespace WindowsGSH.Discord;

public sealed class DiscordEventAlertService : IDisposable
{
    public static readonly TimeSpan DefaultCrashDebounce = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StatusOperationSuppressWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestartStatusSuppressWindow = TimeSpan.FromMinutes(5);

    private readonly IWindowsGshEventBus _events;
    private readonly Func<string, InstalledServer?> _resolveServer;
    private readonly Func<string, string?, bool> _isEnabled;
    private readonly Func<string, InstalledServer?, Task> _sendAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _log;
    private readonly TimeSpan _crashDebounce;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCrashAlerts = [];
    private readonly Dictionary<string, ServerRuntimeStatus> _lastStatuses = [];
    private readonly Dictionary<string, DateTimeOffset> _recentOperationAlerts = [];
    private readonly List<IDisposable> _subscriptions = [];
    private bool _started;

    public DiscordEventAlertService(
        IWindowsGshEventBus events,
        Func<string, InstalledServer?> resolveServer,
        Func<string, string?, bool> isEnabled,
        Func<string, InstalledServer?, Task> sendAsync,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? crashDebounce = null,
        Action<string>? log = null)
    {
        _events = events;
        _resolveServer = resolveServer;
        _isEnabled = isEnabled;
        _sendAsync = sendAsync;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _crashDebounce = crashDebounce ?? DefaultCrashDebounce;
        _log = log ?? (_ => { });
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _subscriptions.Add(_events.Subscribe<ServerOperationChangedEvent>(message => FireAndForget(ProcessEventAsync(message))));
        _subscriptions.Add(_events.Subscribe<ServerStatusChangedEvent>(message => FireAndForget(ProcessEventAsync(message))));
        _subscriptions.Add(_events.Subscribe<ServerCrashDetectedEvent>(message => FireAndForget(ProcessEventAsync(message))));
        _subscriptions.Add(_events.Subscribe<PublicIpChangedEvent>(message => FireAndForget(ProcessEventAsync(message))));
    }

    public Task SendTestAlertAsync(InstalledServer? server = null)
    {
        return _sendAsync("WindowsGSH Discord test alert.", server);
    }

    public async Task ProcessEventAsync(IWindowsGshEvent message)
    {
        try
        {
            switch (message)
            {
                case ServerOperationChangedEvent operation:
                    await ProcessOperationAsync(operation).ConfigureAwait(false);
                    break;
                case ServerStatusChangedEvent status:
                    await ProcessStatusAsync(status).ConfigureAwait(false);
                    break;
                case ServerCrashDetectedEvent crash:
                    await ProcessCrashAsync(crash).ConfigureAwait(false);
                    break;
                case PublicIpChangedEvent publicIp:
                    await ProcessPublicIpAsync(publicIp).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log("Discord alert send failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        _started = false;
    }

    private async Task ProcessOperationAsync(ServerOperationChangedEvent operation)
    {
        if (operation.Kind == ServerOperationKind.Restart)
        {
            if (operation.Status == ServerOperationStatus.Running)
            {
                // A restart's internal stop+start is not reported as separate Stop/Start operations, so the
                // status-polling offline/online blips it causes would otherwise leak past ProcessStatusAsync's
                // suppression check. Cover both keys up front, for the whole restart, not just at completion.
                RememberOperationAlert(operation.ServerId, DiscordAlertEventKeys.Start, RestartStatusSuppressWindow);
                RememberOperationAlert(operation.ServerId, DiscordAlertEventKeys.Stop, RestartStatusSuppressWindow);
                return;
            }

            // The restart is over. Collapse the long pre-suppression down to the normal short window so it
            // only covers the tail-end status-poll catch-up, not the rest of the original 5-minute window -
            // otherwise an unrelated offline/online change shortly after a fast restart would be dropped.
            RememberOperationAlert(operation.ServerId, DiscordAlertEventKeys.Start);
            RememberOperationAlert(operation.ServerId, DiscordAlertEventKeys.Stop);
        }

        var eventKey = GetEventKey(operation.Kind);
        if (eventKey == null ||
            operation.Status == ServerOperationStatus.Running ||
            !IsEnabled(eventKey, operation.ServerId))
        {
            return;
        }

        RememberOperationAlert(operation.ServerId, eventKey);
        await SendAsync(RenderOperationAlert(operation), operation.ServerId).ConfigureAwait(false);
    }

    private async Task ProcessStatusAsync(ServerStatusChangedEvent status)
    {
        ServerRuntimeStatus? previous;
        lock (_sync)
        {
            _lastStatuses.TryGetValue(status.ServerId, out var existing);
            previous = _lastStatuses.ContainsKey(status.ServerId) ? existing : null;
            _lastStatuses[status.ServerId] = status.Status;
        }

        if (previous == null || previous == status.Status)
        {
            return;
        }

        var eventKey = status.Status == ServerRuntimeStatus.Running
            ? DiscordAlertEventKeys.Start
            : (previous is ServerRuntimeStatus.Running or ServerRuntimeStatus.Warning) &&
              status.Status == ServerRuntimeStatus.Offline
                ? DiscordAlertEventKeys.Stop
                : null;
        if (eventKey == null ||
            IsRecentlyCoveredByOperation(status.ServerId, eventKey) ||
            !IsEnabled(eventKey, status.ServerId))
        {
            return;
        }

        var action = eventKey == DiscordAlertEventKeys.Start ? "online" : "offline";
        await SendAsync($"Server {action}: {status.ServerName}.", status.ServerId).ConfigureAwait(false);
    }

    private async Task ProcessCrashAsync(ServerCrashDetectedEvent crash)
    {
        if (!IsEnabled(DiscordAlertEventKeys.Crash, crash.ServerId) || IsDebouncedCrash(crash.ServerId))
        {
            return;
        }

        var report = string.IsNullOrWhiteSpace(crash.ReportPath)
            ? string.Empty
            : $" Report: {Path.GetFileName(crash.ReportPath)}.";
        var detail = string.IsNullOrWhiteSpace(crash.Message)
            ? string.Empty
            : $" {SanitizeAlertText(crash.Message)}";
        await SendAsync($"Server crash detected: {crash.ServerName}.{detail}{report}", crash.ServerId).ConfigureAwait(false);
    }

    private async Task ProcessPublicIpAsync(PublicIpChangedEvent publicIp)
    {
        if (!IsEnabled(DiscordAlertEventKeys.PublicIp, serverId: null))
        {
            return;
        }

        var previous = string.IsNullOrWhiteSpace(publicIp.PreviousIp) ? "unknown" : publicIp.PreviousIp;
        await _sendAsync($"Public IP changed: {previous} -> {publicIp.CurrentIp}.", null).ConfigureAwait(false);
    }

    public static string RenderOperationAlert(ServerOperationChangedEvent operation)
    {
        var action = operation.Kind switch
        {
            ServerOperationKind.Start => "start",
            ServerOperationKind.Stop => "stop",
            ServerOperationKind.ForceStop => "force stop",
            ServerOperationKind.Restart => "restart",
            ServerOperationKind.Update => "update",
            ServerOperationKind.Backup => "backup",
            _ => operation.Kind.ToString().ToLowerInvariant()
        };

        return operation.Status switch
        {
            ServerOperationStatus.Completed => $"Server {action} completed: {operation.ServerName}.",
            ServerOperationStatus.Failed => $"Server {action} failed: {operation.ServerName} - {SanitizeAlertText(operation.LastError ?? "unknown error")}.",
            ServerOperationStatus.Cancelled => $"Server {action} cancelled: {operation.ServerName}.",
            ServerOperationStatus.Interrupted => $"Server {action} interrupted: {operation.ServerName}.",
            _ => $"Server {action}: {operation.ServerName}."
        };
    }

    private static string SanitizeAlertText(string value)
    {
        return Regex.Replace(
            value,
            @"(?i)\b(token|password|secret|api[_-]?key)\b\s*[:=]\s*\S+",
            "$1=<redacted>");
    }

    private static string? GetEventKey(ServerOperationKind kind)
    {
        return kind switch
        {
            ServerOperationKind.Start => DiscordAlertEventKeys.Start,
            ServerOperationKind.Stop or ServerOperationKind.ForceStop => DiscordAlertEventKeys.Stop,
            ServerOperationKind.Restart => DiscordAlertEventKeys.Restart,
            ServerOperationKind.Update => DiscordAlertEventKeys.Update,
            ServerOperationKind.Backup => DiscordAlertEventKeys.Backup,
            _ => null
        };
    }

    private async Task SendAsync(string message, string serverId)
    {
        await _sendAsync(message, _resolveServer(serverId)).ConfigureAwait(false);
    }

    private bool IsEnabled(string eventKey, string? serverId)
    {
        try
        {
            return _isEnabled(eventKey, serverId);
        }
        catch (Exception ex)
        {
            _log($"Discord alert setting lookup failed for {eventKey}/{serverId}: {ex.Message}");
            return true;
        }
    }

    private bool IsDebouncedCrash(string serverId)
    {
        var now = _utcNow();
        lock (_sync)
        {
            if (_lastCrashAlerts.TryGetValue(serverId, out var last) && now - last < _crashDebounce)
            {
                return true;
            }

            _lastCrashAlerts[serverId] = now;
            return false;
        }
    }

    private void RememberOperationAlert(string serverId, string eventKey, TimeSpan? window = null)
    {
        lock (_sync)
        {
            _recentOperationAlerts[$"{serverId}:{eventKey}"] = _utcNow() + (window ?? StatusOperationSuppressWindow);
        }
    }

    private bool IsRecentlyCoveredByOperation(string serverId, string eventKey)
    {
        lock (_sync)
        {
            return _recentOperationAlerts.TryGetValue($"{serverId}:{eventKey}", out var expiresAt) &&
                   _utcNow() < expiresAt;
        }
    }

    private void FireAndForget(Task task)
    {
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("Discord alert send failed: " + ex.Message);
        }
    }
}
