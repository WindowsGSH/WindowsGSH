using WindowsGSH.Core.Network.Upnp;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class UpnpPortMappingServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    [Fact]
    public async Task Create_refuses_to_mutate_when_router_enumeration_is_incomplete()
    {
        var gateway = new FakeGateway
        {
            Existing = UpnpPortMappingsResult.Incomplete([], "partial")
        };
        var registry = new FakeRegistry();
        var service = Service(gateway, registry);

        var result = await service.CreateAsync("server-1", Request());

        Assert.True(result.WasRefused);
        Assert.Empty(gateway.Added);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task Create_never_replaces_an_existing_router_tuple()
    {
        var gateway = new FakeGateway
        {
            Existing = UpnpPortMappingsResult.Success([
                new(null, 7777, "UDP", 9999, "192.168.1.99", true, "someone else", 0)
            ])
        };
        var service = Service(gateway, new FakeRegistry());

        var result = await service.CreateAsync("server-1", Request());

        Assert.True(result.WasRefused);
        Assert.Empty(gateway.Added);
    }

    [Fact]
    public async Task Create_records_ownership_only_after_router_success()
    {
        var gateway = new FakeGateway();
        var registry = new FakeRegistry();
        var service = Service(gateway, registry);

        var result = await service.CreateAsync("server-1", Request());

        Assert.True(result.Succeeded);
        var added = Assert.Single(gateway.Added);
        var owned = Assert.Single(registry.Items);
        Assert.Equal(result.OwnershipId, owned.OwnershipId);
        Assert.Equal($"WindowsGSH:{owned.OwnershipId:D}", added.Description);
        Assert.Equal(Now.AddMinutes(45), owned.RefreshDueUtc);
    }

    [Fact]
    public async Task Create_rolls_back_router_when_registry_rejects_the_record()
    {
        var gateway = new FakeGateway();
        var registry = new FakeRegistry { RejectRegistration = true };
        var service = Service(gateway, registry);

        var result = await service.CreateAsync("server-1", Request());

        Assert.False(result.Succeeded);
        Assert.Single(gateway.Added);
        Assert.Equal(("", 7777, "UDP"), Assert.Single(gateway.Deleted));
    }

    [Fact]
    public async Task Release_refuses_unknown_ownership_without_contacting_router()
    {
        var gateway = new FakeGateway();
        var service = Service(gateway, new FakeRegistry());

        var result = await service.ReleaseAsync(Guid.NewGuid());

        Assert.True(result.WasRefused);
        Assert.Empty(gateway.Deleted);
    }

    [Fact]
    public async Task Release_deletes_exact_registered_tuple_then_removes_registry_record()
    {
        var gateway = new FakeGateway();
        var registry = new FakeRegistry();
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = ExistingOwned(owned);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId);

        Assert.True(result.Succeeded);
        Assert.Equal((owned.RemoteHost, owned.ExternalPort, owned.Protocol), Assert.Single(gateway.Deleted));
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task Release_finishes_registry_cleanup_non_cancelably_after_router_delete()
    {
        using var cts = new CancellationTokenSource();
        var gateway = new FakeGateway { OnDelete = cts.Cancel };
        var registry = new FakeRegistry();
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = ExistingOwned(owned);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId, cts.Token);

        Assert.True(result.Succeeded);
        Assert.Equal(CancellationToken.None, registry.LastRemoveCancellationToken);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task Release_reports_cleanup_failure_instead_of_leaking_registry_exception()
    {
        var gateway = new FakeGateway();
        var registry = new FakeRegistry { ThrowOnRemove = true };
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = UpnpPortMappingsResult.Success([]);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId);

        Assert.False(result.Succeeded);
        Assert.Contains("ownership record could not be cleared", result.Message, StringComparison.Ordinal);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task Release_keeps_ownership_record_when_router_delete_fails()
    {
        var gateway = new FakeGateway { DeleteResult = UpnpMutationResult.Fault("rejected") };
        var registry = new FakeRegistry();
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = ExistingOwned(owned);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId);

        Assert.False(result.Succeeded);
        Assert.Single(registry.Items);
    }

    [Fact]
    public async Task Release_clears_ownership_when_the_router_mapping_is_already_gone()
    {
        // Regression guard: a finite lease expiring, a router reboot, or a manual removal all
        // leave a complete-but-empty enumeration for this tuple. Previously that fell into the
        // same "doesn't match" refusal as a replaced mapping, permanently stranding the ownership
        // row - the registry's unique tuple constraint then made the next CreateAsync for the same
        // port fail registration and roll itself back, with no way to recreate it short of editing
        // the database by hand.
        var gateway = new FakeGateway();
        var registry = new FakeRegistry();
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = UpnpPortMappingsResult.Success([]);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId);

        Assert.True(result.Succeeded);
        Assert.False(result.WasRefused);
        Assert.Empty(gateway.Deleted);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public async Task Release_refuses_when_the_tuple_was_replaced_after_windowsgsh_created_it()
    {
        var gateway = new FakeGateway();
        var registry = new FakeRegistry();
        var owned = Owned();
        registry.Items.Add(owned);
        gateway.Existing = UpnpPortMappingsResult.Success([
            new(null, owned.ExternalPort, owned.Protocol, 9999, "192.168.1.99", true, "another tool", 0)
        ]);
        var service = Service(gateway, registry);

        var result = await service.ReleaseAsync(owned.OwnershipId);

        Assert.True(result.WasRefused);
        Assert.Empty(gateway.Deleted);
        Assert.Single(registry.Items);
    }

    private static UpnpPortMappingService Service(FakeGateway gateway, FakeRegistry registry) =>
        new("uuid:gateway", gateway, registry, () => Now);

    private static UpnpPortMappingRequest Request() =>
        new("", 7777, "udp", 7777, "192.168.1.50", "ignored", 3600);

    private static OwnedPortMapping Owned() =>
        new(Guid.NewGuid(), "uuid:gateway", "server-1", "", 7777, "UDP", 7777,
            "192.168.1.50", 3600, Now, Now.AddMinutes(45));

    private static UpnpPortMappingsResult ExistingOwned(OwnedPortMapping owned) =>
        UpnpPortMappingsResult.Success([
            new(
                string.IsNullOrEmpty(owned.RemoteHost) ? null : owned.RemoteHost,
                owned.ExternalPort,
                owned.Protocol,
                owned.InternalPort,
                owned.InternalClient,
                true,
                $"WindowsGSH:{owned.OwnershipId:D}",
                owned.LeaseDurationSeconds)
        ]);

    private sealed class FakeGateway : IUpnpGateway
    {
        public UpnpPortMappingsResult Existing { get; set; } = UpnpPortMappingsResult.Success([]);
        public UpnpMutationResult AddResult { get; set; } = UpnpMutationResult.Success("added");
        public UpnpMutationResult DeleteResult { get; set; } = UpnpMutationResult.Success("deleted");
        public Action? OnDelete { get; set; }
        public List<UpnpPortMappingRequest> Added { get; } = [];
        public List<(string RemoteHost, int ExternalPort, string Protocol)> Deleted { get; } = [];

        public Task<UpnpExternalIpResult> GetExternalIpAddressAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UpnpPortMappingsResult> GetExistingPortMappingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Existing);

        public Task<UpnpMutationResult> AddPortMappingAsync(UpnpPortMappingRequest request, CancellationToken cancellationToken = default)
        {
            Added.Add(request);
            return Task.FromResult(AddResult);
        }

        public Task<UpnpMutationResult> DeletePortMappingAsync(string remoteHost, int externalPort, string protocol, CancellationToken cancellationToken = default)
        {
            Deleted.Add((remoteHost, externalPort, protocol));
            OnDelete?.Invoke();
            return Task.FromResult(DeleteResult);
        }
    }

    private sealed class FakeRegistry : IPortMappingRegistry
    {
        public bool RejectRegistration { get; set; }
        public bool ThrowOnRemove { get; set; }
        public CancellationToken? LastRemoveCancellationToken { get; private set; }
        public List<OwnedPortMapping> Items { get; } = [];

        public Task<bool> TryRegisterAsync(OwnedPortMapping mapping, CancellationToken cancellationToken = default)
        {
            if (RejectRegistration)
            {
                return Task.FromResult(false);
            }

            Items.Add(mapping);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OwnedPortMapping>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OwnedPortMapping>>(Items.ToArray());

        public Task<bool> TryRemoveAsync(Guid ownershipId, CancellationToken cancellationToken = default)
        {
            LastRemoveCancellationToken = cancellationToken;
            if (ThrowOnRemove)
            {
                throw new InvalidOperationException("test persistence failure");
            }

            return Task.FromResult(Items.RemoveAll(item => item.OwnershipId == ownershipId) == 1);
        }
    }
}
