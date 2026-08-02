namespace WindowsGSH.Core.Servers;

public sealed class ShutdownPlanner
{
    public IReadOnlyList<InstalledServer> SelectStopCandidates(IEnumerable<InstalledServer> servers)
    {
        return servers
            .Where(server => server.CanStop)
            .ToArray();
    }

    // Decides what server list a shutdown-related flow (user close, tray Stop All, Windows session
    // ending) should treat as "currently running" when a live, timeout-bounded enumeration attempt
    // (freshServers == null) didn't produce a trustworthy result in time.
    //
    // allowStaleFallback distinguishes two fundamentally different situations, not just "is an empty
    // list acceptable":
    //  - User close / tray Stop All (allowStaleFallback: false) can be safely aborted and retried by
    //    the user - so ANY failed fresh enumeration must abort, even if a last-known-good snapshot
    //    exists. A snapshot only proves what was true when it was recorded: a server it shows offline
    //    may have been started since: a server may have been added since; or a server it shows running
    //    may have changed state while its own module was hung (which is exactly the scenario that
    //    makes fresh enumeration untrustworthy in the first place). Using stale data here could let
    //    SelectStopCandidates silently omit a server that is running right now, and let WindowsGSH
    //    exit while that server's process survives unmanaged.
    //  - Windows session ending (allowStaleFallback: true) cannot block the OS shutdown to demand a
    //    retry, so it must proceed with the best available information - the last-known-good snapshot
    //    if one exists, or an empty list if not - rather than throwing.
    public IReadOnlyList<InstalledServer> ResolveShutdownServers(
        IReadOnlyList<InstalledServer>? freshServers,
        IReadOnlyList<InstalledServer>? lastKnownGoodSnapshot,
        bool allowStaleFallback)
    {
        if (freshServers != null)
        {
            return freshServers;
        }

        if (!allowStaleFallback)
        {
            throw new InvalidOperationException(
                "Could not determine which servers are currently running. Try again once server status can be confirmed.");
        }

        return lastKnownGoodSnapshot ?? [];
    }

    public InstalledServer CreateDiscordOfflineSnapshot(InstalledServer server)
    {
        return server with
        {
            ProcessId = "--",
            CpuUsage = "--",
            MemoryUsage = "--",
            PlayerCount = "--",
            CurrentStatusText = "Offline",
            Uptime = "--",
            IsOperationRunning = false,
            OperationText = "WindowsGSH closed",
            LastOperationError = null,
            Status = ServerRuntimeStatus.Offline,
            StatusText = "Offline",
            StatusBrushKey = "BadBrush",
            HasUpdateAvailable = false,
            CanStart = false,
            CanStop = false
        };
    }
}
