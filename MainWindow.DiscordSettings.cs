using WindowsGSH.Core.Servers;
using WindowsGSH.Discord;

namespace WindowsGSH;

public partial class MainWindow
{
    internal void OpenDiscordSettingsFromTray()
    {
        RestoreAndActivate();
        NavigateTo(ConfigNavigationId);
        ConfigView.ShowDiscordSettings();
    }

    private async Task StartDiscordBotIfEnabledAsync(bool force = false)
    {
        if (!_settings.DiscordBotEnabled && !force)
        {
            _discordBotHost.SetEnabled(false);
            await _discordBotHost.StopAsync();
            return;
        }

        _discordBotHost.SetEnabled(true);
        var token = _discordTokenStore.Load();
        if (string.IsNullOrWhiteSpace(token))
        {
            _discordBotHost.ReportFailure("Discord bot token is missing.");
            ConfigView.SetStatus("Discord bot token is missing.");
            return;
        }

        try
        {
            await _discordBotHost.StartAsync(CreateDiscordBotSettings(), token);
            ConfigView.SetStatus("Discord bot started.");
        }
        catch (Exception ex)
        {
            ConfigView.SetStatus("Discord bot failed to start: " + ex.Message);
            AppLogService.Add("Discord bot failed to start: " + ex.Message);
        }
    }

    private async Task StopDiscordBotAsync()
    {
        await _discordBotHost.StopAsync();
    }

    private DiscordBotSettings CreateDiscordBotSettings()
    {
        return new DiscordBotSettings(
            string.IsNullOrWhiteSpace(_settings.DiscordBotPrefix) ? "!" : _settings.DiscordBotPrefix,
            _settings.DiscordDashboardChannelId,
            _settings.DiscordNotificationsChannelId,
            _settings.DiscordDashboardRefreshMinutes,
            _settings.DiscordAllowDestructiveCommands);
    }

    private async Task SendDiscordAlertToAllTransportsAsync(string message, InstalledServer? server)
    {
        // Run both transports concurrently and isolate failures so a bot-side error (e.g. a channel
        // lookup that throws) or a slow bot panel refresh cannot delay or suppress the webhook send,
        // and vice versa.
        await Task.WhenAll(
            SendDiscordBotAlertAsync(message, server),
            SendDiscordWebhookAlertAsync(message, server)).ConfigureAwait(false);
    }

    private async Task SendDiscordBotAlertAsync(string message, InstalledServer? server)
    {
        try
        {
            await _discordBotHost.SendNotificationAsync(message, server);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Discord bot alert send failed: {ex.Message}");
        }
    }

    private async Task SendDiscordWebhookAlertAsync(string message, InstalledServer? server)
    {
        try
        {
            await _discordWebhookNotificationService.SendNotificationAsync(message, server);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Discord webhook alert send failed: {ex.Message}");
        }
    }

    private InstalledServer? ResolveDiscordAlertServer(string serverId)
    {
        return _serverListViewModel.LastVisibleServers.FirstOrDefault(server =>
            string.Equals(server.Id, serverId, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveServerWebhook(string serverId)
    {
        var server = ResolveDiscordAlertServer(serverId);
        if (server == null)
        {
            return null;
        }

        try
        {
            var settings = ServerConfigAppSettings.FromConfigJson(System.IO.File.ReadAllText(server.ConfigPath));
            return settings.Discord.UseWebhookOverride
                ? _discordWebhookStore.LoadForServer(serverId)
                : null;
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Discord webhook setting lookup failed for server {serverId}: {ex.Message}");
            return null;
        }
    }
}
