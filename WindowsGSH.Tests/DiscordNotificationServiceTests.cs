using WindowsGSH.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordNotificationServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendNotificationAsync_does_not_resolve_a_channel_when_channel_id_is_blank(string? channelId)
    {
        // Tier 2 Chunk 4: a blank target channel means "do not post," with no fallback to any
        // other channel - this is the core behavior change this chunk introduces.
        var resolveCalled = false;
        var service = new DiscordNotificationService(_ =>
        {
            resolveCalled = true;
            return null;
        });

        await service.SendNotificationAsync("hello", channelId);

        Assert.False(resolveCalled);
    }

    [Fact]
    public async Task SendNotificationAsync_logs_and_does_nothing_when_channel_cannot_be_resolved()
    {
        var logs = new List<string>();
        var service = new DiscordNotificationService(_ => null, logs.Add);

        await service.SendNotificationAsync("hello", "123456789");

        var entry = Assert.Single(logs);
        Assert.Contains("123456789", entry);
        Assert.Contains("not found", entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendNotificationAsync_does_nothing_for_a_blank_message()
    {
        var resolveCalled = false;
        var service = new DiscordNotificationService(_ =>
        {
            resolveCalled = true;
            return null;
        });

        await service.SendNotificationAsync("   ", "123456789");

        Assert.False(resolveCalled);
    }
}
