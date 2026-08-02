namespace WindowsGSH.Discord;

public sealed record DiscordBotSettings(
    string BotPrefix,
    string DashboardChannelId,
    string NotificationsChannelId,
    int DashboardRefreshMinutes,
    bool AllowDestructiveCommands)
{
    public static DiscordBotSettings Default { get; } = new("!", string.Empty, string.Empty, 5, false);
}
