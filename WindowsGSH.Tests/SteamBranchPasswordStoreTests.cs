using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(SteamBranchPasswordStoreTestCollection.Name)]
public sealed class SteamBranchPasswordStoreTests
{
    [Fact]
    public void Save_and_load_round_trips_branch_password()
    {
        var store = new SteamBranchPasswordStore();
        var serverId = Guid.NewGuid().ToString("N");

        try
        {
            store.Save(serverId, "branch-secret");

            Assert.Equal("branch-secret", store.Load(serverId));
        }
        finally
        {
            store.Clear(serverId);
        }
    }

    [Fact]
    public void Resolve_prefers_legacy_plain_config_value()
    {
        var store = new SteamBranchPasswordStore();

        Assert.Equal(
            "legacy-secret",
            store.Resolve("1", "legacy-secret", SteamBranchPasswordStore.CreateReference("1")));
    }

    [Fact]
    public void Resolve_loads_referenced_password_when_plain_value_is_empty()
    {
        var store = new SteamBranchPasswordStore();
        var serverId = Guid.NewGuid().ToString("N");

        try
        {
            store.Save(serverId, "branch-secret");

            Assert.Equal(
                "branch-secret",
                store.Resolve(serverId, "", SteamBranchPasswordStore.CreateReference(serverId)));
        }
        finally
        {
            store.Clear(serverId);
        }
    }

    [Fact]
    public async Task Concurrent_saves_preserve_all_passwords_without_file_lock_failures()
    {
        var store = new SteamBranchPasswordStore();
        var values = Enumerable.Range(0, 20)
            .Select(index => (
                ServerId: Guid.NewGuid().ToString("N"),
                Password: $"branch-secret-{index}"))
            .ToArray();

        try
        {
            await Task.WhenAll(values.Select(value =>
                Task.Run(() => store.Save(value.ServerId, value.Password))));

            foreach (var value in values)
            {
                Assert.Equal(value.Password, store.Load(value.ServerId));
            }
        }
        finally
        {
            foreach (var value in values)
            {
                store.Clear(value.ServerId);
            }
        }
    }
}
