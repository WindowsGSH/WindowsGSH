using System.Collections.Concurrent;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Network.Upnp;

/// <summary>
/// Wires a server's <see cref="UpnpMappingPolicy"/> to actual router mutation via
/// <see cref="UpnpPortMappingService"/>. Service-layer only - callers (e.g. MainWindow's existing
/// server start/stop hooks) decide when to invoke this; nothing here runs on any schedule of its
/// own, and there is deliberately no settings UI yet to set a server's policy away from the
/// <see cref="UpnpMappingPolicy.Manual"/> default (Tier 5.4d scope decision).
/// </summary>
public sealed class UpnpMappingLifecycleService
{
    // Generous but bounded - this runs inline with a server start/stop, not on a background
    // schedule, so it must not turn a normal start into a multi-minute hang if no gateway answers.
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    // Until a renewal worker exists, request a static lease. A finite lease here would silently
    // remove forwarding from a healthy long-running server at expiry. Routers that do not support
    // static leases will reject the request visibly instead of accepting a known-to-expire setup.
    private const long DefaultLeaseDurationSeconds = 0;

    private readonly IPortMappingRegistry _registry;
    private readonly IUpnpDiscoveryService _discovery;
    private readonly Func<UpnpGatewayDescriptor, IUpnpGateway> _gatewayFactory;
    private readonly IServerPortResolver _portResolver;
    private readonly Func<UpnpGatewayDescriptor, string?> _getLocalIPv4;
    private readonly Action<string, string?> _log;
    private readonly ConcurrentDictionary<string, UpnpMappingPolicy> _policyOverrides = new(StringComparer.Ordinal);

    // Keyed by gateway, not server: two different servers being started/stopped concurrently
    // (e.g. the bulk executor) can resolve to the same physical gateway. A per-server key would let
    // their UpnpPortMappingService instances - each fresh per call, so its own internal gate never
    // sees the other - mutate the same router tuple at once: both could enumerate it as absent and
    // both call AddPortMapping, and whichever loses the registry's unique-key race would roll back
    // by deleting the mapping the winner just legitimately created.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gatewayMutationGates = new(StringComparer.Ordinal);

    // Keyed by server: a *different* concern from the gateway gate above. UnmapOnStopAsync's first
    // step reads the registry before any gateway is even discovered; if that read races a concurrent
    // MapOnStartAsync for the *same* server (e.g. Windows session ending force-cancels an in-flight
    // start's token after only a couple of seconds and proceeds to stop that same server while the
    // start's own AddPortMapping call - which deliberately ignores cancellation once dispatched - is
    // still registering ownership), UnmapOnStopAsync can observe zero owned mappings, return early,
    // and never look again, leaving the mapping the start just created stranded on the router. This
    // gate forces a server's own start-hook and stop-hook to run one at a time, so the later one
    // always sees the earlier one's fully-committed result. Always acquired before the gateway gate
    // above (never the reverse), so the two can never deadlock against each other.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverLifecycleGates = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;

    // Backoff state for ReconcileOrphanedMappingsAsync, keyed by ownership row. That method runs on
    // every ~3-second maintenance tick; without this, a single orphaned mapping that can't be
    // released (gateway unreachable, or ReleaseAsync correctly refusing because another tool now
    // owns the tuple) would repeat a full SSDP discovery (up to 5s) plus a complete router
    // enumeration every single cycle, forever. Guarded by _orphanRetryStateGate, not one of the
    // async gates above - every access here is a plain dictionary read/write with no await in
    // between, so a lock is enough and avoids taking an async gate just to touch bookkeeping state.
    private readonly Dictionary<Guid, OrphanRetryState> _orphanRetryState = [];
    private readonly object _orphanRetryStateGate = new();

    private static readonly TimeSpan[] OrphanRetryBackoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromDays(1)
    ];

    public UpnpMappingLifecycleService(
        IPortMappingRegistry registry,
        IUpnpDiscoveryService? discovery = null,
        Func<UpnpGatewayDescriptor, IUpnpGateway>? gatewayFactory = null,
        IServerPortResolver? portResolver = null,
        Func<UpnpGatewayDescriptor, string?>? getLocalIPv4 = null,
        Action<string, string?>? log = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? new SsdpUpnpDiscoveryService();
        _gatewayFactory = gatewayFactory ?? (descriptor => new UpnpGateway(descriptor));
        _portResolver = portResolver ?? new ServerPortResolver();
        _getLocalIPv4 = getLocalIPv4 ?? (descriptor => UpnpLocalAddressResolver.GetLocalIPv4(descriptor.ControlUrl));
        _log = log ?? ((_, _) => { });
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    private sealed class OrphanRetryState
    {
        public int AttemptCount;
        public DateTimeOffset NextAttemptUtc;
    }

    public Task MapOnStartAsync(
        InstalledServer server,
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            _serverLifecycleGates, server.Id, () => MapOnStartCoreAsync(server, module, instance, cancellationToken), cancellationToken);

    /// <summary>
    /// Publishes a policy saved by the UI to this app-session coordinator. A start that loaded its
    /// instance before the save must observe the newer policy after it reaches the shared server
    /// gate, rather than recreating mappings after Manual cleanup has completed.
    /// </summary>
    public void SetCurrentPolicy(string serverId, UpnpMappingPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        _policyOverrides[serverId] = policy;
    }

    public void ClearCurrentPolicy(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        _policyOverrides.TryRemove(serverId, out _);
    }

    private async Task MapOnStartCoreAsync(
        InstalledServer server,
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        var policy = _policyOverrides.TryGetValue(server.Id, out var currentPolicy)
            ? currentPolicy
            : instance.AppSettings.Network.UpnpMappingPolicy;
        if (policy == UpnpMappingPolicy.Manual)
        {
            return;
        }

        var portsToForward = TryResolvePortsToForward(module, instance, server, out var resolveError);
        if (resolveError != null)
        {
            _log($"{server.Name}: UPnP mapping skipped - {resolveError}", server.Id);
            return;
        }

        // Even with nothing to forward today, this server might still own stale mappings from a
        // previous configuration (a port that was removed, renumbered, or switched off since the
        // last start) - those still need reconciling, not just a fast exit.
        var ownedAnywhereForServer = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(mapping => string.Equals(mapping.ServerId, server.Id, StringComparison.Ordinal))
            .ToArray();
        if (portsToForward.Count == 0 && ownedAnywhereForServer.Length == 0)
        {
            return;
        }

        IReadOnlyList<UpnpGatewayDescriptor> gateways;
        try
        {
            gateways = await _discovery.DiscoverGatewaysAsync(DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"{server.Name}: UPnP mapping skipped - gateway discovery failed: {ex.Message}", server.Id);
            return;
        }

        if (gateways.Count != 1)
        {
            _log(
                gateways.Count == 0
                    ? $"{server.Name}: UPnP mapping skipped - no UPnP gateway was found."
                    : $"{server.Name}: UPnP mapping skipped - {gateways.Count} UPnP gateways were found; WindowsGSH will not guess which one to use.",
                server.Id);
            return;
        }

        string? localIp = null;
        if (portsToForward.Count > 0)
        {
            localIp = _getLocalIPv4(gateways[0]);
            if (string.IsNullOrWhiteSpace(localIp))
            {
                _log($"{server.Name}: UPnP mapping skipped - could not determine this machine's own LAN IP address.", server.Id);
                return;
            }
        }

        var gatewayId = ResolveGatewayId(gateways[0]);
        await ExecuteSerializedAsync(_gatewayMutationGates, gatewayId, async () =>
        {
            var gateway = _gatewayFactory(gateways[0]);
            var mappingService = new UpnpPortMappingService(gatewayId, gateway, _registry);
            // Re-read inside the gateway lock: the pre-check snapshot above could already be stale
            // by the time this gateway's turn comes up, if another server queued ahead of it.
            var ownedOnThisGateway = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Where(mapping =>
                    string.Equals(mapping.ServerId, server.Id, StringComparison.Ordinal) &&
                    string.Equals(mapping.GatewayId, gatewayId, StringComparison.Ordinal))
                .ToArray();

            var desired = new HashSet<(int Port, string Protocol)>(portsToForward);
            foreach (var obsolete in ownedOnThisGateway.Where(owned => !desired.Contains((owned.ExternalPort, owned.Protocol))))
            {
                // Not stop-time removal (MapOnStart never does that) - this server no longer wants
                // this exact port/protocol at all (removed, renumbered, or switched off since the
                // last start), so the stale claim would otherwise sit on the router and in the
                // registry indefinitely, potentially blocking a later, unrelated process from
                // binding the same port.
                var release = await mappingService.ReleaseAsync(obsolete.OwnershipId, cancellationToken).ConfigureAwait(false);
                _log(
                    $"{server.Name}: UPnP {obsolete.Protocol} {obsolete.ExternalPort} mapping (no longer configured) removal {DescribeOutcome(release.Succeeded, release.WasRefused)}: {release.Message}",
                    server.Id);
            }

            // One up-front scan, reused below to skip ports that are already exactly correct
            // instead of unconditionally deleting and recreating every one of them (which would
            // otherwise call GetExistingPortMappingsAsync at least twice more per port on top of
            // this one - a full SOAP enumeration each time, bounded at 30 seconds per call in
            // UpnpGateway). Only used to decide what can be *skipped*; a port that still needs a
            // real mutation goes through ReleaseAsync/CreateAsync exactly as before, each
            // re-verifying router state immediately before acting on it - reusing this same
            // snapshot across mutations within the batch would risk acting on state this batch's
            // own earlier mutations had already invalidated.
            var routerSnapshot = portsToForward.Count > 0
                ? await gateway.GetExistingPortMappingsAsync(cancellationToken).ConfigureAwait(false)
                : null;

            foreach (var (port, protocol) in portsToForward)
            {
                var priorOwnership = ownedOnThisGateway.FirstOrDefault(owned =>
                    owned.ExternalPort == port && string.Equals(owned.Protocol, protocol, StringComparison.OrdinalIgnoreCase));

                if (priorOwnership != null &&
                    routerSnapshot is { Outcome: UpnpSoapOutcome.Success } &&
                    IsExactlyHealthy(routerSnapshot.Mappings, priorOwnership, localIp!))
                {
                    // Already forwarded exactly as WindowsGSH last left it, to this machine's
                    // current LAN address - deleting and recreating it would only open a
                    // needless (if brief) window with no forwarding in place at all.
                    _log($"{server.Name}: UPnP {protocol} {port} mapping already up to date; nothing to do.", server.Id);
                    continue;
                }

                if (priorOwnership != null)
                {
                    // Reconcile the durable row with current router state. ReleaseAsync safely clears
                    // an expired/absent row, refreshes an exact mapping by removing it before the new
                    // create below, and refuses if another tool replaced the tuple.
                    var reconcile = await mappingService.ReleaseAsync(priorOwnership.OwnershipId, cancellationToken).ConfigureAwait(false);
                    if (!reconcile.Succeeded)
                    {
                        _log($"{server.Name}: UPnP {protocol} {port} ownership reconciliation {DescribeOutcome(false, reconcile.WasRefused)}: {reconcile.Message}", server.Id);
                        continue;
                    }
                }

                var request = new UpnpPortMappingRequest(
                    RemoteHost: string.Empty,
                    ExternalPort: port,
                    Protocol: protocol,
                    InternalPort: port,
                    InternalClient: localIp!,
                    Description: "replaced by UpnpPortMappingService's own ownership marker",
                    LeaseDurationSeconds: DefaultLeaseDurationSeconds);

                try
                {
                    var result = await mappingService.CreateAsync(server.Id, request, cancellationToken).ConfigureAwait(false);
                    _log(
                        $"{server.Name}: UPnP {protocol} {port} mapping {DescribeOutcome(result.Succeeded, result.WasRefused)}: {result.Message}",
                        server.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One port failing to map must never abort the others or the server start itself.
                    _log($"{server.Name}: UPnP {protocol} {port} mapping failed: {ex.Message}", server.Id);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task UnmapOnStopAsync(InstalledServer server, ServerInstance instance, CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            _serverLifecycleGates,
            server.Id,
            () => UnmapOnStopCoreAsync(server, instance, CancellationToken.None),
            CancellationToken.None);

    private async Task UnmapOnStopCoreAsync(InstalledServer server, ServerInstance instance, CancellationToken cancellationToken)
    {
        // The caller (ServerLifecycleService's AfterStopAsync hook, or the app-shutdown stop path)
        // already loaded this instance as part of the stop it just ran - reusing it here avoids a
        // redundant config file read + JSON parse + secret resolution on every single stop, not just
        // the ones where the policy below actually ends up mattering.
        if (instance.AppSettings.Network.UpnpMappingPolicy != UpnpMappingPolicy.MapOnStartRemoveOnStop)
        {
            return;
        }

        var owned = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(mapping => string.Equals(mapping.ServerId, server.Id, StringComparison.Ordinal))
            .ToArray();

        await ReleaseOwnedMappingsAsync(server.Id, server.Name, owned, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes every mapping WindowsGSH currently owns for this server, regardless of its configured
    /// policy. A deleted server's config file (and the policy it carried) is gone by the time
    /// anything would next reconcile ownership - MapOnStartAsync/UnmapOnStopAsync only ever run as
    /// part of that same server starting or stopping, which can never happen again once it no longer
    /// exists. Without an explicit call at delete time, an owned router mapping and its registry row
    /// would both survive indefinitely, able to expose whatever unrelated process later binds that
    /// port. Callers should invoke this as part of server deletion, before or after removing the
    /// server's own files - it never reads or needs them.
    /// </summary>
    public Task RemoveAllOwnedMappingsAsync(InstalledServer server, CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            _serverLifecycleGates,
            server.Id,
            () => RemoveAllOwnedMappingsCoreAsync(server, CancellationToken.None),
            CancellationToken.None);

    private async Task RemoveAllOwnedMappingsCoreAsync(InstalledServer server, CancellationToken cancellationToken)
    {
        var owned = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(mapping => string.Equals(mapping.ServerId, server.Id, StringComparison.Ordinal))
            .ToArray();

        await ReleaseOwnedMappingsAsync(server.Id, server.Name, owned, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileOrphanedMappingsAsync(
        IReadOnlySet<string> activeServerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeServerIds);
        var orphaned = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(mapping => !activeServerIds.Contains(mapping.ServerId))
            .ToArray();

        var now = _utcNow();
        IReadOnlyList<OwnedPortMapping> dueMappings;
        lock (_orphanRetryStateGate)
        {
            PruneOrphanRetryState(orphaned);
            dueMappings = orphaned.Where(mapping => IsDueForRetry(mapping.OwnershipId, now)).ToArray();
        }

        if (dueMappings.Count == 0)
        {
            return;
        }

        foreach (var group in dueMappings.GroupBy(mapping => mapping.ServerId, StringComparer.Ordinal))
        {
            var serverId = group.Key;
            var mappings = group.ToArray();
            await ExecuteSerializedAsync(
                _serverLifecycleGates,
                serverId,
                () => ReleaseOwnedMappingsAsync(serverId, $"Deleted server {serverId}", mappings, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        // A released (or already-gone) mapping's row is removed from the registry by ReleaseAsync
        // itself; re-reading once here - rather than changing ReleaseOwnedMappingsAsync's return
        // shape, which is shared with UnmapOnStopAsync and RemoveAllOwnedMappingsAsync, neither of
        // which need retry bookkeeping - is enough to tell which of this batch's attempts actually
        // succeeded versus are still stuck.
        var stillOwned = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Select(mapping => mapping.OwnershipId)
            .ToHashSet();
        lock (_orphanRetryStateGate)
        {
            foreach (var mapping in dueMappings)
            {
                if (stillOwned.Contains(mapping.OwnershipId))
                {
                    AdvanceOrphanRetry(mapping, now);
                }
                else
                {
                    _orphanRetryState.Remove(mapping.OwnershipId);
                }
            }
        }
    }

    private bool IsDueForRetry(Guid ownershipId, DateTimeOffset now)
    {
        return !_orphanRetryState.TryGetValue(ownershipId, out var state) ||
            now >= state.NextAttemptUtc;
    }

    private void AdvanceOrphanRetry(OwnedPortMapping mapping, DateTimeOffset now)
    {
        if (!_orphanRetryState.TryGetValue(mapping.OwnershipId, out var state))
        {
            state = new OrphanRetryState();
            _orphanRetryState[mapping.OwnershipId] = state;
        }

        state.AttemptCount++;
        var tierIndex = Math.Min(state.AttemptCount - 1, OrphanRetryBackoff.Length - 1);
        state.NextAttemptUtc = now + OrphanRetryBackoff[tierIndex];
    }

    private void PruneOrphanRetryState(IReadOnlyList<OwnedPortMapping> currentlyOrphaned)
    {
        if (_orphanRetryState.Count == 0)
        {
            return;
        }

        var currentIds = currentlyOrphaned.Select(mapping => mapping.OwnershipId).ToHashSet();
        foreach (var ownershipId in _orphanRetryState.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _orphanRetryState.Remove(ownershipId);
        }
    }

    private async Task ReleaseOwnedMappingsAsync(
        string serverId,
        string serverName,
        IReadOnlyList<OwnedPortMapping> owned,
        CancellationToken cancellationToken)
    {
        if (owned.Count == 0)
        {
            return;
        }

        IReadOnlyList<UpnpGatewayDescriptor> gateways;
        try
        {
            gateways = await _discovery.DiscoverGatewaysAsync(DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"{serverName}: could not remove {owned.Count} UPnP mapping(s) - gateway discovery failed: {ex.Message}", serverId);
            return;
        }

        // Grouped by gateway, not assumed single: ownership rows for the same server could in
        // principle span more than one previously-used gateway (e.g. the router changed between
        // sessions), unlike MapOnStartAsync's own single-gateway requirement.
        foreach (var group in owned.GroupBy(mapping => mapping.GatewayId, StringComparer.Ordinal))
        {
            var descriptor = gateways.FirstOrDefault(candidate => ResolveGatewayId(candidate) == group.Key);
            if (descriptor == null)
            {
                _log($"{serverName}: could not reach the UPnP gateway that owns {group.Count()} mapping(s); nothing was removed for it.", serverId);
                continue;
            }

            var gatewayId = group.Key;
            var mappingsForGateway = group.ToArray();
            await ExecuteSerializedAsync(_gatewayMutationGates, gatewayId, async () =>
            {
                var mappingService = new UpnpPortMappingService(gatewayId, _gatewayFactory(descriptor), _registry);
                foreach (var mapping in mappingsForGateway)
                {
                    try
                    {
                        var result = await mappingService.ReleaseAsync(mapping.OwnershipId, cancellationToken).ConfigureAwait(false);
                        _log(
                            $"{serverName}: UPnP {mapping.Protocol} {mapping.ExternalPort} mapping removal {DescribeOutcome(result.Succeeded, result.WasRefused)}: {result.Message}",
                            serverId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One mapping failing to release must never abort the others.
                        _log($"{serverName}: UPnP {mapping.Protocol} {mapping.ExternalPort} mapping removal failed: {ex.Message}", serverId);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsExactlyHealthy(IReadOnlyList<UpnpPortMappingEntry> mappings, OwnedPortMapping owned, string currentLocalIp)
    {
        var ownershipMarker = $"WindowsGSH:{owned.OwnershipId:D}";
        return mappings.Any(mapping =>
            mapping.Enabled &&
            string.Equals(mapping.RemoteHost ?? string.Empty, owned.RemoteHost, StringComparison.OrdinalIgnoreCase) &&
            mapping.ExternalPort == owned.ExternalPort &&
            string.Equals(mapping.Protocol, owned.Protocol, StringComparison.OrdinalIgnoreCase) &&
            mapping.InternalPort == owned.InternalPort &&
            // Compared against the *current* desired local IP, not the stored owned.InternalClient -
            // if this machine's LAN address changed (DHCP lease renewal) since this mapping was
            // created, the router entry now points at a stale address and must be treated as
            // unhealthy so it gets replaced below, not preserved.
            string.Equals(mapping.InternalClient, currentLocalIp, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mapping.Description, ownershipMarker, StringComparison.Ordinal) &&
            // A router that silently substituted a finite lease for the requested static lease
            // will eventually drop the mapping. Treat an explicitly positive lease as unhealthy
            // so this start replaces it rather than trusting forwarding that is known to expire.
            mapping.LeaseDurationSeconds is null or 0);
    }

    private static string DescribeOutcome(bool succeeded, bool wasRefused) =>
        succeeded ? "succeeded" : wasRefused ? "refused" : "failed";

    // A single physical device commonly answers M-SEARCH for both InternetGatewayDevice:1 and :2
    // with the same description location, but a different full USN per search target (differing
    // only in the trailing "::urn:...:1" vs "::urn:...:2" suffix) - SsdpUpnpDiscoveryService's own
    // location-based dedup keeps whichever response happened to arrive first, so the complete USN
    // string is not stable for the same device across separate discovery runs. The leading
    // "uuid:..." portion (before "::") identifies the device itself and stays the same regardless
    // of which search target's response won that race.
    private static string ResolveGatewayId(UpnpGatewayDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Usn))
        {
            return descriptor.ControlUrl.ToString();
        }

        var separatorIndex = descriptor.Usn.IndexOf("::", StringComparison.Ordinal);
        var uuidPart = separatorIndex > 0 ? descriptor.Usn[..separatorIndex] : descriptor.Usn;
        return uuidPart.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? uuidPart : descriptor.Usn;
    }

    private static async Task ExecuteSerializedAsync(
        ConcurrentDictionary<string, SemaphoreSlim> gates,
        string key,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var gate = gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Matches ServerHealthService.TryResolveDeclaredPorts's own reasoning: a broken module or
    // resolver must not abort the server start this runs alongside, so every call into
    // module/resolver code here is isolated behind this one method.
    private IReadOnlyList<(int Port, string Protocol)> TryResolvePortsToForward(
        IGameServerModule module,
        ServerInstance instance,
        InstalledServer server,
        out string? error)
    {
        try
        {
            var declared = module.GetPorts();
            if (declared.Count == 0)
            {
                error = null;
                return [];
            }

            var resolved = _portResolver.Resolve(module, instance);
            error = null;
            return ExpandPortsToForward(resolved);
        }
        catch (Exception ex)
        {
            error = $"could not resolve declared ports: {ex.Message}";
            return [];
        }
    }

    private static IReadOnlyList<(int Port, string Protocol)> ExpandPortsToForward(IReadOnlyList<ResolvedPort> resolvedPorts)
    {
        var result = new List<(int, string)>();
        foreach (var port in resolvedPorts)
        {
            if (port.Status != ResolvedPortStatus.Resolved || !port.OpenExternally || port.Port is null)
            {
                continue;
            }

            // Both and Either both forward both protocols: Both genuinely needs both listening,
            // and Either means the module itself doesn't know which one the wrapped server
            // actually uses - forwarding only a guessed protocol risks silently forwarding the
            // wrong one. Matches how ServerHealthService/ModuleValidator already group Both and
            // Either together for their own overlap-detection purposes.
            var protocols = port.Protocol switch
            {
                PortProtocol.Tcp => (IReadOnlyList<string>)["TCP"],
                PortProtocol.Udp => ["UDP"],
                _ => ["TCP", "UDP"]
            };

            foreach (var portNumber in port.PortRange)
            {
                foreach (var protocol in protocols)
                {
                    result.Add((portNumber, protocol));
                }
            }
        }

        return result;
    }
}
