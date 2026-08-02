using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Network.Upnp;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class UpnpMappingLifecycleServiceTests
{
    [Fact]
    public async Task MapOnStartAsync_does_nothing_when_policy_is_manual()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        var instance = Instance(UpnpMappingPolicy.Manual);

        await service.MapOnStartAsync(Server(), new PortsModule([Port("game", 7777)]), instance, CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Empty(registry.Items);
        Assert.Empty(gateways);
    }

    [Fact]
    public async Task MapOnStartAsync_honors_newer_coordinator_policy_over_stale_loaded_instance()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        var staleAutomaticInstance = Instance(UpnpMappingPolicy.MapOnStart);
        service.SetCurrentPolicy("server-1", UpnpMappingPolicy.Manual);

        await service.MapOnStartAsync(
            Server(),
            new PortsModule([Port("game", 7777)]),
            staleAutomaticInstance,
            CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Empty(registry.Items);
        Assert.Empty(gateways);
    }

    [Fact]
    public async Task MapOnStartAsync_can_enable_mapping_over_stale_manual_instance()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        service.SetCurrentPolicy("server-1", UpnpMappingPolicy.MapOnStart);

        await service.MapOnStartAsync(
            Server(),
            new PortsModule([Port("game", 7777)]),
            Instance(UpnpMappingPolicy.Manual),
            CancellationToken.None);

        Assert.True(discovery.WasCalled);
        Assert.Single(registry.Items);
        Assert.Single(gateways);
    }

    [Fact]
    public async Task ClearCurrentPolicy_prevents_reused_server_id_from_inheriting_override()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        service.SetCurrentPolicy("server-1", UpnpMappingPolicy.MapOnStart);
        service.ClearCurrentPolicy("server-1");

        await service.MapOnStartAsync(
            Server(),
            new PortsModule([Port("game", 7777)]),
            Instance(UpnpMappingPolicy.Manual),
            CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Empty(registry.Items);
        Assert.Empty(gateways);
    }

    [Fact]
    public async Task MapOnStartAsync_does_nothing_when_no_ports_are_open_externally()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out _);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("rcon", 27016, openExternally: false)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task MapOnStartAsync_creates_a_mapping_for_each_open_externally_port()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777), Port("query", 7778)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Equal(2, gateway.Added.Count);
        Assert.Contains(gateway.Added, request => request.ExternalPort == 7777);
        Assert.Contains(gateway.Added, request => request.ExternalPort == 7778);
        Assert.Equal(2, registry.Items.Count);
        Assert.All(registry.Items, item => Assert.Equal("server-1", item.ServerId));
    }

    [Fact]
    public async Task MapOnStartAsync_expands_both_and_either_protocols_into_tcp_and_udp()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Either)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Equal(2, gateway.Added.Count);
        Assert.Contains(gateway.Added, request => request.ExternalPort == 7777 && request.Protocol == "TCP");
        Assert.Contains(gateway.Added, request => request.ExternalPort == 7777 && request.Protocol == "UDP");
    }

    [Fact]
    public async Task MapOnStartAsync_recreates_a_stale_owned_port_missing_from_the_router()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 86_400,
            DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out var gateways);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Single(gateway.Added);
        Assert.Equal(0, gateway.Added[0].LeaseDurationSeconds);
        Assert.Single(registry.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task MapOnStartAsync_refuses_to_guess_when_gateway_count_is_not_exactly_one(int gatewayCount)
    {
        var discovery = new FakeDiscovery
        {
            Gateways = Enumerable.Range(0, gatewayCount).Select(index => Gateway($"gw-{index}")).ToArray()
        };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        Assert.Empty(gateways);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task MapOnStartAsync_continues_to_the_next_port_when_one_mapping_throws()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways, throwForExternalPort: 7777);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777), Port("query", 7778)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        // The throwing port never got as far as being recorded in Added; the healthy one still did.
        Assert.Single(gateway.Added);
        Assert.Equal(7778, gateway.Added[0].ExternalPort);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task MapOnStartAsync_serializes_mutations_by_gateway_across_different_servers()
    {
        // Regression guard: the mutation gate used to be keyed by server id, so two different
        // servers being started concurrently (e.g. the bulk executor) but resolving to the same
        // physical gateway had nothing serializing their CreateAsync sequences against each other -
        // each server's own fresh UpnpPortMappingService instance has its own internal gate, which
        // never sees the other. Verified here by observing concurrency directly: BeforeAdd delays
        // inside the critical section, and this asserts at most one caller was ever inside it at once.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var concurrentCallers = 0;
        var maxObservedConcurrency = 0;
        var sync = new object();
        var service = Service(discovery, registry, out var gateways, beforeAdd: async () =>
        {
            lock (sync)
            {
                concurrentCallers++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCallers);
            }

            await Task.Delay(50);

            lock (sync)
            {
                concurrentCallers--;
            }
        });
        var instance = Instance(UpnpMappingPolicy.MapOnStart);

        await Task.WhenAll(
            service.MapOnStartAsync(Server() with { Id = "server-a" }, new PortsModule([Port("game", 7777)]), instance, CancellationToken.None),
            service.MapOnStartAsync(Server() with { Id = "server-b" }, new PortsModule([Port("game", 8888)]), instance, CancellationToken.None));

        Assert.Equal(1, maxObservedConcurrency);
        Assert.Equal(2, gateways.Sum(gateway => gateway.Added.Count));
    }

    [Fact]
    public async Task MapOnStartAsync_removes_an_owned_port_that_is_no_longer_configured()
    {
        // Regression guard: reconciliation only used to walk portsToForward (the fresh desired
        // set) - an owned row for a port the server no longer declares (removed, renumbered, or
        // switched to OpenExternally = false) was never revisited under MapOnStart, which by
        // design never does stop-time removal either, so the stale claim would sit on the router
        // and in the registry indefinitely.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var obsolete = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 9999, "TCP", 9999, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(obsolete);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(obsolete)]);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Contains(("", 9999, "TCP"), gateway.Deleted);
        Assert.Contains(gateway.Added, request => request.ExternalPort == 7777);
        Assert.DoesNotContain(registry.Items, item => item.ExternalPort == 9999);
        Assert.Contains(registry.Items, item => item.ExternalPort == 7777);
    }

    [Fact]
    public async Task MapOnStartAsync_skips_reconciliation_when_the_mapping_is_already_healthy()
    {
        // Regression guard: reconciliation used to unconditionally ReleaseAsync (delete) then
        // CreateAsync (recreate) every desired port with a prior ownership row, even when the
        // router already showed the exact same mapping WindowsGSH left behind. That opened a brief
        // but real window with no forwarding at all, and did it on every single restart. When the
        // router's own state already matches (same external/internal port, same protocol, same
        // internal client as this run's resolved LAN IP, same ownership marker), nothing should be
        // deleted or recreated.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 0, DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Empty(gateway.Added);
        Assert.Empty(gateway.Deleted);
        var unchanged = Assert.Single(registry.Items);
        Assert.Equal(owned.OwnershipId, unchanged.OwnershipId);
    }

    [Fact]
    public async Task MapOnStartAsync_replaces_a_mapping_whose_internal_client_no_longer_matches()
    {
        // Companion to the "already healthy" guard above: if this machine's LAN address changed
        // (DHCP lease renewal) since the mapping was created, the router entry now points at a
        // stale address and must NOT be preserved just because port/protocol/ownership still match.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.9", 0, DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Single(gateway.Added);
        Assert.Equal("10.0.0.5", gateway.Added[0].InternalClient);
        var recreated = Assert.Single(registry.Items);
        Assert.Equal("10.0.0.5", recreated.InternalClient);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3600)]
    public async Task MapOnStartAsync_replaces_disabled_or_expiring_owned_mappings(
        bool enabled,
        long leaseDurationSeconds)
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        var existing = ExistingEntryFor(owned) with
        {
            Enabled = enabled,
            LeaseDurationSeconds = leaseDurationSeconds
        };
        var service = Service(discovery, registry, out var gateways, existingMappings: [existing]);

        await service.MapOnStartAsync(
            Server(),
            new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]),
            Instance(UpnpMappingPolicy.MapOnStart),
            CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Single(gateway.Deleted);
        Assert.Single(gateway.Added);
        Assert.Equal(0, gateway.Added[0].LeaseDurationSeconds);
    }

    [Fact]
    public async Task MapOnStartAsync_recognizes_prior_ownership_across_igd1_and_igd2_usn_variants()
    {
        // Regression guard: a single physical router commonly answers M-SEARCH for both
        // InternetGatewayDevice:1 and :2 with the same description location but a different full
        // USN per search target - discovery's own dedup keeps whichever response arrived first, so
        // the exact USN string isn't stable across separate discovery runs for the same device. The
        // registry row below was recorded under the ":1" variant's full USN; this run's discovery
        // returns the ":2" variant for what is really the same gateway - the normalized "uuid:..."
        // prefix must still match so this is recognized as reconciling a prior mapping, not treated
        // as belonging to a different, unreachable gateway. The stored row's GatewayId is already
        // the normalized form (what ResolveGatewayId would have produced for EITHER variant at the
        // time it was written) - the point being tested is that discovering the OTHER variant this
        // run still resolves to that same normalized id, not that a raw, pre-fix stored value is
        // somehow still accepted.
        const string stableUuid = "uuid:12345678-1234-1234-1234-123456789abc";
        var discovery = new FakeDiscovery
        {
            Gateways = [Gateway($"{stableUuid}::urn:schemas-upnp-org:service:WANIPConnection:2")]
        };
        var registry = new FakeRegistry();
        // InternalClient deliberately does NOT match Service()'s "10.0.0.5" getLocalIPv4 stub - as
        // if this machine's LAN address changed (DHCP) since the mapping was created. That keeps
        // this test genuinely exercising reconcile-then-recreate: an exact match here would instead
        // be recognized as already healthy and skipped (see IsExactlyHealthy), which would collapse
        // this test's "recognized across gateway id" assertion and the separate healthy-mapping-skip
        // behavior into an indistinguishable no-op.
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), stableUuid,
            "server-1", "", 7777, "TCP", 7777, "10.0.0.9", 0, DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);
        var instance = Instance(UpnpMappingPolicy.MapOnStart);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]);

        await service.MapOnStartAsync(Server(), module, instance, CancellationToken.None);

        var gateway = Assert.Single(gateways);
        // Reconciled (released the old row) then recreated (a fresh row) - not refused as
        // "belongs to another gateway" just because this run's discovery saw the ":2" search
        // target's response instead of ":1".
        Assert.Single(gateway.Added);
        var recreated = Assert.Single(registry.Items);
        Assert.Equal(stableUuid, recreated.GatewayId);
    }

    [Fact]
    public async Task UnmapOnStopAsync_finds_the_owning_gateway_across_igd1_and_igd2_usn_variants()
    {
        const string stableUuid = "uuid:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var discovery = new FakeDiscovery
        {
            Gateways = [Gateway($"{stableUuid}::urn:schemas-upnp-org:service:WANIPConnection:2")]
        };
        var registry = new FakeRegistry();
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), stableUuid,
            "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 0, DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);

        await service.UnmapOnStopAsync(Server(), Instance(UpnpMappingPolicy.MapOnStartRemoveOnStop), CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Equal(("", 7777, "TCP"), Assert.Single(gateway.Deleted));
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task UnmapOnStopAsync_does_nothing_when_policy_is_not_remove_on_stop()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 86_400,
            DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out _);

        await service.UnmapOnStopAsync(Server(), Instance(UpnpMappingPolicy.MapOnStart), CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task UnmapOnStopAsync_releases_only_this_servers_mappings_on_their_owning_gateway()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var ownershipId = Guid.NewGuid();
        var owned = new OwnedPortMapping(
            ownershipId, "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 86_400,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-2", "", 8888, "TCP", 8888, "10.0.0.5", 86_400,
            DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);

        await service.UnmapOnStopAsync(Server(), Instance(UpnpMappingPolicy.MapOnStartRemoveOnStop), CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Equal(("", 7777, "TCP"), Assert.Single(gateway.Deleted));
        Assert.Single(registry.Items);
        Assert.Equal("server-2", registry.Items[0].ServerId);
    }

    [Fact]
    public async Task UnmapOnStopAsync_skips_a_gateway_that_cannot_be_rediscovered()
    {
        var discovery = new FakeDiscovery { Gateways = [] };
        var registry = new FakeRegistry();
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-missing", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 86_400,
            DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out var gateways);

        await service.UnmapOnStopAsync(Server(), Instance(UpnpMappingPolicy.MapOnStartRemoveOnStop), CancellationToken.None);

        Assert.Empty(gateways);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task RemoveAllOwnedMappingsAsync_removes_every_mapping_regardless_of_policy()
    {
        // Regression guard: a deleted server's config (and whatever UpnpMappingPolicy it carried)
        // is gone by the time deletion completes, and MapOnStartAsync/UnmapOnStopAsync only ever run
        // as part of that same server starting or stopping - which can never happen again once it no
        // longer exists. Without an explicit, policy-independent removal call at delete time, an
        // owned mapping would survive on the router and in the registry indefinitely. This method
        // deliberately takes no ServerInstance/policy at all - it must work purely off the server id.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var owned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 0, DateTimeOffset.UtcNow, null);
        registry.Items.Add(owned);
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-2", "", 8888, "TCP", 8888, "10.0.0.5", 0, DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(owned)]);

        await service.RemoveAllOwnedMappingsAsync(Server(), CancellationToken.None);

        var gateway = Assert.Single(gateways);
        Assert.Equal(("", 7777, "TCP"), Assert.Single(gateway.Deleted));
        Assert.Single(registry.Items);
        Assert.Equal("server-2", registry.Items[0].ServerId);
    }

    [Fact]
    public async Task RemoveAllOwnedMappingsAsync_does_nothing_when_the_server_owns_no_mappings()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out var gateways);

        await service.RemoveAllOwnedMappingsAsync(Server(), CancellationToken.None);

        Assert.False(discovery.WasCalled);
        Assert.Empty(gateways);
    }

    [Fact]
    public async Task ReconcileOrphanedMappingsAsync_retries_rows_for_servers_that_no_longer_exist()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var orphaned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "deleted-server", "", 7777, "TCP", 7777, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        var active = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "active-server", "", 8888, "TCP", 8888, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(orphaned);
        registry.Items.Add(active);
        var service = Service(discovery, registry, out var gateways, existingMappings: [ExistingEntryFor(orphaned)]);

        await service.ReconcileOrphanedMappingsAsync(
            new HashSet<string>(["active-server"], StringComparer.Ordinal));

        var gateway = Assert.Single(gateways);
        Assert.Equal(("", 7777, "TCP"), Assert.Single(gateway.Deleted));
        var remaining = Assert.Single(registry.Items);
        Assert.Equal("active-server", remaining.ServerId);
    }

    [Fact]
    public async Task ReconcileOrphanedMappingsAsync_does_not_retry_a_still_refused_row_before_its_backoff_elapses()
    {
        // Regression guard: this method runs on every ~3s maintenance tick. Without backoff, an
        // orphaned mapping that ReleaseAsync correctly refuses (another tool now owns the router
        // tuple) would repeat a full discovery + enumeration every single cycle forever. Seeded with
        // a router-side entry whose Description does NOT match this row's ownership marker, so
        // ReleaseAsync refuses (leaves the row) on every attempt - simulating a persistent failure.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var orphaned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "deleted-server", "", 7777, "TCP", 7777, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(orphaned);
        var conflictingEntry = new UpnpPortMappingEntry(null, 7777, "TCP", 7777, "10.0.0.5", true, "SomeOtherTool", null);
        var currentTime = DateTimeOffset.UtcNow;
        var service = Service(
            discovery, registry, out var gateways,
            existingMappings: [conflictingEntry],
            utcNow: () => currentTime);
        var activeServers = new HashSet<string>(StringComparer.Ordinal);

        await service.ReconcileOrphanedMappingsAsync(activeServers);
        await service.ReconcileOrphanedMappingsAsync(activeServers);

        // Still refused both times (row untouched), but discovery should only have run once - the
        // second call's only due attempt was suppressed by the backoff window.
        Assert.Single(gateways);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task ReconcileOrphanedMappingsAsync_retries_again_once_the_backoff_window_elapses()
    {
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var orphaned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "deleted-server", "", 7777, "TCP", 7777, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(orphaned);
        var conflictingEntry = new UpnpPortMappingEntry(null, 7777, "TCP", 7777, "10.0.0.5", true, "SomeOtherTool", null);
        var currentTime = DateTimeOffset.UtcNow;
        var service = Service(
            discovery, registry, out var gateways,
            existingMappings: [conflictingEntry],
            utcNow: () => currentTime);
        var activeServers = new HashSet<string>(StringComparer.Ordinal);

        await service.ReconcileOrphanedMappingsAsync(activeServers);
        currentTime += TimeSpan.FromMinutes(2); // past the 1-minute first-tier backoff
        await service.ReconcileOrphanedMappingsAsync(activeServers);

        Assert.Equal(2, gateways.Count);
    }

    [Fact]
    public async Task ReconcileOrphanedMappingsAsync_keeps_daily_retries_after_many_failures()
    {
        // A long router outage must not permanently park cleanup. Once the short backoff tiers are
        // exhausted, the service continues retrying daily so an exposed orphan can be removed when
        // the gateway eventually returns.
        var discovery = new FakeDiscovery { Gateways = [Gateway("gw-1")] };
        var registry = new FakeRegistry();
        var orphaned = new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "deleted-server", "", 7777, "TCP", 7777, "10.0.0.5", 0,
            DateTimeOffset.UtcNow, null);
        registry.Items.Add(orphaned);
        var conflictingEntry = new UpnpPortMappingEntry(null, 7777, "TCP", 7777, "10.0.0.5", true, "SomeOtherTool", null);
        var currentTime = DateTimeOffset.UtcNow;
        var service = Service(
            discovery, registry, out var gateways,
            existingMappings: [conflictingEntry],
            utcNow: () => currentTime);
        var activeServers = new HashSet<string>(StringComparer.Ordinal);

        // Every pass is beyond even the daily tier, including attempts beyond the old parking
        // threshold.
        for (var i = 0; i < 9; i++)
        {
            await service.ReconcileOrphanedMappingsAsync(activeServers);
            currentTime += TimeSpan.FromDays(2);
        }

        Assert.Equal(9, gateways.Count);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task MapOnStartAsync_and_UnmapOnStopAsync_serialize_against_each_other_for_the_same_server()
    {
        // Regression guard: UnmapOnStopAsync's first step reads the registry before any gateway is
        // even discovered. If that read raced a concurrent MapOnStartAsync for the *same* server
        // (e.g. Windows session ending force-cancels an in-flight start's token after only a couple
        // of seconds, then proceeds to stop that same server while the start's own AddPortMapping -
        // which deliberately ignores cancellation once dispatched - is still registering ownership),
        // UnmapOnStopAsync could observe zero owned mappings, return early, and never look again,
        // stranding the mapping the start just created. A per-server gate now forces the two to run
        // one at a time. Verified here the same way the per-gateway gate is: a delay inside the
        // shared discovery step, asserting at most one caller was ever inside it at once.
        var concurrentCallers = 0;
        var maxObservedConcurrency = 0;
        var sync = new object();
        var discovery = new FakeDiscovery
        {
            Gateways = [Gateway("gw-1")],
            BeforeDiscover = async () =>
            {
                lock (sync)
                {
                    concurrentCallers++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCallers);
                }

                await Task.Delay(50);

                lock (sync)
                {
                    concurrentCallers--;
                }
            }
        };
        var registry = new FakeRegistry();
        registry.Items.Add(new OwnedPortMapping(
            Guid.NewGuid(), "gw-1", "server-1", "", 7777, "TCP", 7777, "10.0.0.5", 0, DateTimeOffset.UtcNow, null));
        var service = Service(discovery, registry, out _);
        var instance = Instance(UpnpMappingPolicy.MapOnStartRemoveOnStop);
        var module = new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]);

        await Task.WhenAll(
            service.MapOnStartAsync(Server(), module, instance, CancellationToken.None),
            service.UnmapOnStopAsync(Server(), instance, CancellationToken.None));

        Assert.Equal(1, maxObservedConcurrency);
    }

    [Fact]
    public async Task UnmapOnStopAsync_waits_for_an_inflight_map_even_when_the_lifecycle_token_is_cancelled()
    {
        var firstDiscoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstDiscoveryToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryCalls = 0;
        var discovery = new FakeDiscovery
        {
            Gateways = [Gateway("gw-1")],
            BeforeDiscover = async () =>
            {
                if (Interlocked.Increment(ref discoveryCalls) == 1)
                {
                    firstDiscoveryEntered.TrySetResult();
                    await allowFirstDiscoveryToFinish.Task;
                }
            }
        };
        var registry = new FakeRegistry();
        var service = Service(discovery, registry, out _);
        var instance = Instance(UpnpMappingPolicy.MapOnStartRemoveOnStop);
        var server = Server();
        var mapTask = service.MapOnStartAsync(
            server,
            new PortsModule([Port("game", 7777, protocol: PortProtocol.Tcp)]),
            instance,
            CancellationToken.None);
        await firstDiscoveryEntered.Task;

        using var stopCancellation = new CancellationTokenSource();
        var unmapTask = service.UnmapOnStopAsync(server, instance, stopCancellation.Token);
        stopCancellation.Cancel();
        allowFirstDiscoveryToFinish.TrySetResult();

        await Task.WhenAll(mapTask, unmapTask);

        Assert.Empty(registry.Items);
    }

    private static UpnpMappingLifecycleService Service(
        FakeDiscovery discovery,
        FakeRegistry registry,
        out List<FakeGateway> gateways,
        int? throwForExternalPort = null,
        IReadOnlyList<UpnpPortMappingEntry>? existingMappings = null,
        Func<Task>? beforeAdd = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<string, string?>? log = null)
    {
        var capturedGateways = new List<FakeGateway>();
        gateways = capturedGateways;
        return new UpnpMappingLifecycleService(
            registry,
            discovery,
            descriptor =>
            {
                var gateway = new FakeGateway
                {
                    ThrowForExternalPort = throwForExternalPort,
                    ExistingMappings = existingMappings ?? [],
                    BeforeAdd = beforeAdd
                };
                capturedGateways.Add(gateway);
                return gateway;
            },
            new FakePortResolver(),
            getLocalIPv4: _ => "10.0.0.5",
            log: log ?? ((_, _) => { }),
            utcNow: utcNow);
    }

    private static UpnpPortMappingEntry ExistingEntryFor(OwnedPortMapping owned) => new(
        string.IsNullOrEmpty(owned.RemoteHost) ? null : owned.RemoteHost,
        owned.ExternalPort,
        owned.Protocol,
        owned.InternalPort,
        owned.InternalClient,
        true,
        $"WindowsGSH:{owned.OwnershipId:D}",
        owned.LeaseDurationSeconds);

    private static InstalledServer Server() => new(
        "server-1", "Test Server", "test", "Runtime", @"C:\servers\test", @"C:\servers\test",
        @"C:\servers\test\server.json", "127.0.0.1", "27015", "", "", "0", "", "", "", "", "Offline",
        "", false, "", null, true, ServerRuntimeStatus.Offline, "Offline", "ServerStatusOfflineBrush",
        false, "", "", "", true, true, false, false);

    private static ServerInstance Instance(UpnpMappingPolicy policy) => new(
        "server-1", "Test Server", "test", @"C:\servers\test", @"C:\servers\test", @"C:\servers\test\server.json",
        new Dictionary<string, object?>(),
        ServerConfigAppSettings.Empty with { Network = ServerConfigAppSettings.Empty.Network with { UpnpMappingPolicy = policy } },
        ServerModuleSettings.Empty);

    private static ServerPortDefinition Port(string id, int port, bool openExternally = true, PortProtocol protocol = PortProtocol.Tcp) =>
        new(id, id, protocol, ConfigField: null, FixedValue: port, OffsetFrom: null, Offset: 0, RangeSize: 1,
            Required: true, OpenExternally: openExternally);

    private static UpnpGatewayDescriptor Gateway(string usn) => new(
        new Uri("http://192.168.1.1/root.xml"), "Router", null, null,
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        new Uri("http://192.168.1.1/control"), null, usn);

    private sealed class PortsModule(IReadOnlyList<ServerPortDefinition> ports) : IGameServerModule
    {
        public string Id => "ports-module";
        public string Name => "Ports Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public IReadOnlyList<ServerPortDefinition> GetPorts() => ports;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class FakePortResolver : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            ports.Select(port => new ResolvedPort(
                port.Id, port.Name, port.Protocol, ResolvedPortStatus.Resolved, port.FixedValue, port.RangeSize,
                port.Required, port.OpenExternally)).ToArray();

        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            Resolve(module.GetPorts(), instance.Settings);
    }

    private sealed class FakeDiscovery : IUpnpDiscoveryService
    {
        public IReadOnlyList<UpnpGatewayDescriptor> Gateways { get; set; } = [];
        public bool WasCalled { get; private set; }
        public Func<Task>? BeforeDiscover { get; set; }

        public async Task<IReadOnlyList<UpnpGatewayDescriptor>> DiscoverGatewaysAsync(TimeSpan searchTimeout, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            if (BeforeDiscover != null)
            {
                await BeforeDiscover();
            }

            return Gateways;
        }
    }

    private sealed class FakeGateway : IUpnpGateway
    {
        public int? ThrowForExternalPort { get; set; }
        // Stateful, not a static snapshot: a reconcile-then-recreate sequence (ReleaseAsync followed
        // by CreateAsync against the same fake gateway, both in MapOnStartAsync) needs the delete to
        // actually be reflected before the following create's own "does this already exist" check
        // runs - otherwise that check would see the same stale entry ReleaseAsync just "removed" and
        // wrongly refuse the create. Set via the ExistingMappings property (defaults to empty -
        // correct for tests that need "nothing on the router yet").
        private readonly List<UpnpPortMappingEntry> _mappings = [];
        public IReadOnlyList<UpnpPortMappingEntry> ExistingMappings
        {
            get => _mappings;
            set
            {
                _mappings.Clear();
                _mappings.AddRange(value);
            }
        }

        public List<UpnpPortMappingRequest> Added { get; } = [];
        public List<(string RemoteHost, int ExternalPort, string Protocol)> Deleted { get; } = [];
        public Func<Task>? BeforeAdd { get; set; }

        public Task<UpnpExternalIpResult> GetExternalIpAddressAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UpnpPortMappingsResult> GetExistingPortMappingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UpnpPortMappingsResult.Success((IReadOnlyList<UpnpPortMappingEntry>)_mappings.ToArray()));

        public async Task<UpnpMutationResult> AddPortMappingAsync(UpnpPortMappingRequest request, CancellationToken cancellationToken = default)
        {
            if (request.ExternalPort == ThrowForExternalPort)
            {
                throw new InvalidOperationException("Simulated router failure.");
            }

            if (BeforeAdd != null)
            {
                await BeforeAdd();
            }

            Added.Add(request);
            _mappings.Add(new UpnpPortMappingEntry(
                string.IsNullOrEmpty(request.RemoteHost) ? null : request.RemoteHost,
                request.ExternalPort,
                request.Protocol,
                request.InternalPort,
                request.InternalClient,
                true,
                request.Description,
                request.LeaseDurationSeconds));
            return UpnpMutationResult.Success("added");
        }

        public Task<UpnpMutationResult> DeletePortMappingAsync(string remoteHost, int externalPort, string protocol, CancellationToken cancellationToken = default)
        {
            Deleted.Add((remoteHost, externalPort, protocol));
            _mappings.RemoveAll(entry =>
                string.Equals(entry.RemoteHost ?? string.Empty, remoteHost, StringComparison.OrdinalIgnoreCase) &&
                entry.ExternalPort == externalPort &&
                string.Equals(entry.Protocol, protocol, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(UpnpMutationResult.Success("deleted"));
        }
    }

    private sealed class FakeRegistry : IPortMappingRegistry
    {
        public List<OwnedPortMapping> Items { get; } = [];

        public Task<bool> TryRegisterAsync(OwnedPortMapping mapping, CancellationToken cancellationToken = default)
        {
            Items.Add(mapping);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OwnedPortMapping>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OwnedPortMapping>>(Items.ToArray());

        public Task<bool> TryRemoveAsync(Guid ownershipId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.RemoveAll(item => item.OwnershipId == ownershipId) == 1);
    }
}
