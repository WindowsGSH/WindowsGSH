using WindowsGSH.Core.Network.Upnp;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PortMappingRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"WindowsGSH-registry-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public PortMappingRegistryTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "test.db");
        AppDatabase.Initialize(_databasePath);
    }

    [Fact]
    public async Task Register_list_remove_round_trip_uses_ownership_id()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var mapping = Mapping();

        Assert.True(await registry.TryRegisterAsync(mapping));
        Assert.Equal(mapping, Assert.Single(await registry.GetAllAsync()));
        Assert.False(await registry.TryRemoveAsync(Guid.NewGuid()));
        Assert.True(await registry.TryRemoveAsync(mapping.OwnershipId));
        Assert.Empty(await registry.GetAllAsync());
    }

    [Fact]
    public async Task Conflicting_router_tuple_cannot_overwrite_existing_ownership()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var original = Mapping();
        var impostor = original with { OwnershipId = Guid.NewGuid(), ServerId = "other-server" };

        Assert.True(await registry.TryRegisterAsync(original));
        Assert.False(await registry.TryRegisterAsync(impostor));
        Assert.Equal(original, Assert.Single(await registry.GetAllAsync()));
    }

    [Fact]
    public async Task Reregistering_the_identical_ownership_id_and_tuple_succeeds_idempotently()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var mapping = Mapping();
        Assert.True(await registry.TryRegisterAsync(mapping));

        // An idempotent retry (e.g. after a crash before the caller could persist confirmation of
        // its own earlier success) must not read as an unexplained failure indistinguishable from
        // someone else already owning this router mapping.
        Assert.True(await registry.TryRegisterAsync(mapping));
        Assert.Equal(mapping, Assert.Single(await registry.GetAllAsync()));
    }

    [Fact]
    public async Task Multiple_distinct_mappings_all_persist()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var first = Mapping();
        var second = first with { OwnershipId = Guid.NewGuid(), ExternalPort = 7778 };

        Assert.True(await registry.TryRegisterAsync(first));
        Assert.True(await registry.TryRegisterAsync(second));

        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(first, all);
        Assert.Contains(second, all);
    }

    [Fact]
    public async Task Null_remote_host_is_rejected_with_argument_exception_not_a_sqlite_exception()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var mapping = Mapping() with { RemoteHost = null! };

        await Assert.ThrowsAsync<ArgumentException>(() => registry.TryRegisterAsync(mapping));
    }

    [Fact]
    public async Task Unknown_or_empty_ownership_id_cannot_remove_a_mapping()
    {
        var registry = new PortMappingRegistry(_databasePath);
        var mapping = Mapping();
        Assert.True(await registry.TryRegisterAsync(mapping));

        Assert.False(await registry.TryRemoveAsync(Guid.Empty));
        Assert.False(await registry.TryRemoveAsync(Guid.NewGuid()));
        Assert.Equal(mapping, Assert.Single(await registry.GetAllAsync()));
    }

    private static OwnedPortMapping Mapping() => new(
        Guid.NewGuid(), "uuid:gateway", "server-1", "", 7777, "UDP", 7777,
        "192.168.1.50", 3600, DateTimeOffset.Parse("2026-07-31T12:00:00Z"),
        DateTimeOffset.Parse("2026-07-31T12:45:00Z"));

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
