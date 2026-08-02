using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public sealed class ServerListViewModel
{
    private readonly ServerStatusComposer _statusComposer;
    private readonly Func<string, ServerOperationSnapshot?> _getOperation;
    private readonly Func<string, int?>? _getLiveMonitoredProcessId;

    public ServerListViewModel(
        ServerStatusComposer statusComposer,
        Func<string, ServerOperationSnapshot?> getOperation,
        Func<string, int?>? getLiveMonitoredProcessId = null)
    {
        _statusComposer = statusComposer;
        _getOperation = getOperation;
        _getLiveMonitoredProcessId = getLiveMonitoredProcessId;
    }

    public IReadOnlyList<InstalledServer> LastVisibleServers { get; private set; } = [];

    // A dedicated snapshot for safety-critical shutdown fallback use (see
    // MainWindow.GetServersForShutdownWithFallbackAsync), distinct from LastVisibleServers above.
    // LastVisibleServers is simply "whatever the most recent refresh produced" - it can itself hold a
    // problem card (CanStop=false) for a server whose load merely timed out, even though the real
    // game process is still genuinely running, or be empty before the first refresh completes. This
    // property is only ever updated from a refresh where EVERY server's own load succeeded cleanly
    // (no problem card), so a shutdown flow falling back to it after its own enumeration times out
    // never mistakes "we couldn't confirm this one right now" for "this server isn't running."
    // Remains null until the first such clean refresh - callers must treat null as "no trustworthy
    // data exists yet," not as "confirmed zero servers."
    public IReadOnlyList<InstalledServer>? LastKnownGoodShutdownSnapshot { get; private set; }

    public IReadOnlyList<InstalledServer> ApplyOperationStatuses(IReadOnlyList<InstalledServer> servers)
    {
        return _statusComposer.Apply(
            servers,
            _getOperation,
            getLiveMonitoredProcessId: _getLiveMonitoredProcessId);
    }

    public ServerListViewState Update(IReadOnlyList<InstalledServer> servers, string? selectedLogSource)
    {
        var visibleServers = ApplyOperationStatuses(servers);
        var changed = !LastVisibleServers.SequenceEqual(visibleServers);
        if (changed)
        {
            LastVisibleServers = visibleServers;
        }

        // Checked against the raw loader output (before ApplyOperationStatuses), not the composed
        // visibleServers - the operation-status overlay can set its own LastOperationError from an
        // unrelated past start/stop failure even when the underlying load itself was perfectly
        // healthy, which would make this gate far too conservative if checked post-composition.
        if (servers.All(server => server.LastOperationError == null))
        {
            LastKnownGoodShutdownSnapshot = visibleServers;
        }

        var warningServers = visibleServers
            .Where(server => server.Status == ServerRuntimeStatus.Warning)
            .ToArray();

        var logFilters = BuildLogFilters(servers);
        var selectedFilter = logFilters.FirstOrDefault(item => item.Source == selectedLogSource) ?? logFilters[0];

        return new ServerListViewState(
            VisibleServers: visibleServers,
            Changed: changed,
            IsEmpty: servers.Count == 0,
            OnlineCount: visibleServers.Count(server => string.Equals(server.CurrentStatusText, "Online", StringComparison.OrdinalIgnoreCase)),
            OfflineCount: visibleServers.Count(server => string.Equals(server.CurrentStatusText, "Offline", StringComparison.OrdinalIgnoreCase)),
            WarningCount: warningServers.Length,
            IssueRows: warningServers
                .Select(server => new ServerIssueRow(server, server.Name, GetServerIssueText(server), server.HasUpdateAvailable))
                .ToArray(),
            LogFilters: logFilters,
            SelectedLogFilter: selectedFilter);
    }

    private static IReadOnlyList<ServerLogFilterItem> BuildLogFilters(IReadOnlyList<InstalledServer> servers)
    {
        return new[]
            {
                new ServerLogFilterItem("All logs", null),
                new ServerLogFilterItem("Web", "Web"),
            }
            .Concat(servers
                .OrderBy(server => int.TryParse(server.Id, out var id) ? id : int.MaxValue)
                .Select(server => new ServerLogFilterItem($"[{server.Id}] {server.Name}", server.Id)))
            .ToArray();
    }

    private static string GetServerIssueText(InstalledServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.LastOperationError))
        {
            return server.LastOperationError;
        }

        if (!string.Equals(server.CurrentStatusText, server.StatusText, StringComparison.OrdinalIgnoreCase))
        {
            return $"{server.StatusText}: {server.CurrentStatusText}";
        }

        return server.StatusText;
    }
}

public sealed record ServerListViewState(
    IReadOnlyList<InstalledServer> VisibleServers,
    bool Changed,
    bool IsEmpty,
    int OnlineCount,
    int OfflineCount,
    int WarningCount,
    IReadOnlyList<ServerIssueRow> IssueRows,
    IReadOnlyList<ServerLogFilterItem> LogFilters,
    ServerLogFilterItem SelectedLogFilter);

public sealed record ServerIssueRow(InstalledServer Server, string Name, string Issue, bool CanIgnoreBuild);

public sealed record ServerLogFilterItem(string Label, string? Source)
{
    public override string ToString()
    {
        return Label;
    }
}
