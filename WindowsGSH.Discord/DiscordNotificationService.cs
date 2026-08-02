using Discord;
using Discord.WebSocket;

namespace WindowsGSH.Discord;

internal sealed class DiscordNotificationService
{
    private const int DiscordMessageMaxLength = 1900;
    private readonly Func<string, IMessageChannel?> _resolveChannel;
    private readonly Action<string> _log;

    public DiscordNotificationService(
        Func<string, IMessageChannel?> resolveChannel,
        Action<string>? log = null)
    {
        _resolveChannel = resolveChannel;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Sends to exactly one channel - whatever the caller resolved (Tier 2 Chunk 4: callers now
    /// decide the target themselves, e.g. a server's <c>AlertChannelId</c> or, for alerts not
    /// tied to a server, the legacy global notifications channel). A blank <paramref name="channelId"/>
    /// means "do not post" by design - there is no further fallback here.
    /// </summary>
    public async Task SendNotificationAsync(string message, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        var channel = _resolveChannel(channelId);
        if (channel == null)
        {
            _log($"Discord notification channel '{channelId}' was not found.");
            return;
        }

        var notification = TruncateForDiscordMessage(message.Trim());
        try
        {
            LogNotificationChannelPermissions(channel);
            await channel.SendMessageAsync(notification);
        }
        catch (Exception ex)
        {
            _log($"Discord notification send failed for channel {channel.Id}: {ex.Message}");
        }
    }

    private void LogNotificationChannelPermissions(IMessageChannel channel)
    {
        if (channel is not SocketGuildChannel guildChannel)
        {
            _log($"Discord notification target {channel.Id} is {channel.GetType().Name}, not a guild text channel.");
            return;
        }

        var currentUser = guildChannel.Guild.CurrentUser;
        if (currentUser == null)
        {
            _log($"Discord notification target {guildChannel.Name} ({guildChannel.Id}) permissions could not be checked because the bot user is not cached.");
            return;
        }

        var permissions = currentUser.GetPermissions(guildChannel);
        _log(
            "Discord notification target " +
            $"{guildChannel.Name} ({guildChannel.Id}, {guildChannel.GetType().Name}) permissions: " +
            $"ViewChannel={permissions.ViewChannel}, " +
            $"SendMessages={permissions.SendMessages}, " +
            $"SendMessagesInThreads={permissions.SendMessagesInThreads}, " +
            $"ReadMessageHistory={permissions.ReadMessageHistory}.");
    }

    private static string TruncateForDiscordMessage(string value)
    {
        return value.Length <= DiscordMessageMaxLength
            ? value
            : value[..(DiscordMessageMaxLength - 3)] + "...";
    }
}
