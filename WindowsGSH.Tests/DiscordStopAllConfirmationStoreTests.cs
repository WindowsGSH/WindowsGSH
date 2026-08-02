using WindowsGSH.Core.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordStopAllConfirmationStoreTests
{
    private DateTimeOffset _now = new(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirm_succeeds_for_matching_scope_within_timeout()
    {
        var store = CreateStore();

        store.Request("user-1", "guild-1", "channel-1");

        Assert.True(store.TryConfirm("user-1", "guild-1", "channel-1"));
    }

    [Fact]
    public void Confirm_is_consumed_after_success()
    {
        var store = CreateStore();
        store.Request("user-1", "guild-1", "channel-1");

        Assert.True(store.TryConfirm("user-1", "guild-1", "channel-1"));
        Assert.False(store.TryConfirm("user-1", "guild-1", "channel-1"));
    }

    [Fact]
    public void Confirm_is_scoped_to_user()
    {
        var store = CreateStore();
        store.Request("user-1", "guild-1", "channel-1");

        Assert.False(store.TryConfirm("user-2", "guild-1", "channel-1"));
    }

    [Fact]
    public void Confirm_is_scoped_to_guild_and_channel()
    {
        var store = CreateStore();
        store.Request("user-1", "guild-1", "channel-1");

        Assert.False(store.TryConfirm("user-1", "guild-2", "channel-1"));
        Assert.False(store.TryConfirm("user-1", "guild-1", "channel-2"));
    }

    [Fact]
    public void Confirm_fails_after_timeout()
    {
        var store = CreateStore();
        store.Request("user-1", "guild-1", "channel-1");

        _now = _now.AddSeconds(61);

        Assert.False(store.TryConfirm("user-1", "guild-1", "channel-1"));
    }

    private DiscordStopAllConfirmationStore CreateStore()
    {
        return new DiscordStopAllConfirmationStore(TimeSpan.FromSeconds(60), () => _now);
    }
}
