using Discord.WebSocket;
using WindowsGSH.Core.Discord;
using WindowsGSH.Core.Servers;
using WindowsGSH.Data;

namespace WindowsGSH.Discord;

internal sealed class DiscordButtonHandler
{
    private static readonly TimeSpan ButtonActionTimeout = TimeSpan.FromMinutes(2);
    private const int DiscordMessageMaxLength = 1900;
    private const string StalePanelMessage = "This WindowsGSH panel is out of date. Refresh the panel and try again.";
    private readonly DiscordRepository _repository;
    private readonly DiscordPanelService _panelService;
    private readonly Func<DiscordBotSettings> _getSettings;
    private readonly Func<string, string, string, Task<string>> _runServerActionAsync;
    private readonly Func<Task> _refreshPanelsAsync;
    private readonly Func<string, Task<InstalledServer?>> _findServerAsync;
    private readonly Func<string, int, string>? _getRecentServerLog;
    private readonly Func<string, string?, string, bool> _isAdmin;
    private readonly Action<string> _log;

    public DiscordButtonHandler(
        DiscordRepository repository,
        DiscordPanelService panelService,
        Func<DiscordBotSettings> getSettings,
        Func<string, string, string, Task<string>> runServerActionAsync,
        Func<Task> refreshPanelsAsync,
        Func<string, Task<InstalledServer?>> findServerAsync,
        Func<string, int, string>? getRecentServerLog,
        Func<string, string?, string, bool> isAdmin,
        Action<string>? log = null)
    {
        _repository = repository;
        _panelService = panelService;
        _getSettings = getSettings;
        _runServerActionAsync = runServerActionAsync;
        _refreshPanelsAsync = refreshPanelsAsync;
        _findServerAsync = findServerAsync;
        _getRecentServerLog = getRecentServerLog;
        _isAdmin = isAdmin;
        _log = log ?? (_ => { });
    }

    public async Task ProcessButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.User.IsBot ||
            string.IsNullOrWhiteSpace(component.Data.CustomId) ||
            !component.Data.CustomId.StartsWith("wgsh:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var parts = component.Data.CustomId.Split(':', 4, StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            await component.RespondAsync(StalePanelMessage, ephemeral: true);
            AddButtonAudit(component, "unknown", string.Empty, StalePanelMessage);
            return;
        }

        var serverId = parts[1];
        var action = parts[2].ToLowerInvariant();
        var panelNonce = parts[3];
        if (!_panelService.IsCurrentPanelNonce(serverId, panelNonce))
        {
            await component.RespondAsync(StalePanelMessage, ephemeral: true);
            AddButtonAudit(component, action, serverId, StalePanelMessage);
            return;
        }

        try
        {
            await component.DeferAsync(ephemeral: true);
        }
        catch (Exception ex)
        {
            _log("Discord button defer failed: " + ex.Message);
            return;
        }

        var permissionCommand = action == "logs" ? "logs" : action;
        var settings = _getSettings();
        if (!DiscordCommandPermissions.IsAllowedByRemoteControlSetting(permissionCommand, settings.AllowDestructiveCommands))
        {
            LogBlockedRemoteControlAttempt(component.User.Id.ToString(), component.User.Username, permissionCommand, serverId);
            AddButtonAudit(component, permissionCommand, serverId, DiscordCommandPermissions.RemoteControlDisabledMessage);
            await component.FollowupAsync(DiscordCommandPermissions.RemoteControlDisabledMessage, ephemeral: true);
            return;
        }

        if (!_isAdmin(component.User.Id.ToString(), serverId, permissionCommand))
        {
            const string result = "You do not have permission to use that WindowsGSH action.";
            AddButtonAudit(component, permissionCommand, serverId, result);
            await component.FollowupAsync(result, ephemeral: true);
            return;
        }

        _ = ObserveAsync(ProcessButtonActionAsync(component, serverId, action), "Discord button action failed");
    }

    private async Task ProcessButtonActionAsync(SocketMessageComponent component, string serverId, string action)
    {
        try
        {
            var result = await WithTimeoutStringAsync(action switch
            {
                "refresh" => RefreshButtonPanelAsync(serverId),
                "logs" => Task.FromResult(GetButtonLogs(serverId)),
                "restart" => _runServerActionAsync("restart", serverId, string.Empty),
                "stop" => _runServerActionAsync("stop", serverId, string.Empty),
                _ => Task.FromResult("Unknown WindowsGSH button action.")
            }, ButtonActionTimeout, $"Discord button action '{action}' for server '{serverId}' timed out.");

            await _refreshPanelsAsync();
            AddButtonAudit(component, action, serverId, result);
            await component.FollowupAsync(TruncateForDiscordMessage(result), ephemeral: true);
        }
        catch (Exception ex)
        {
            var result = "Button action failed: " + ex.Message;
            _log("Discord button action failed: " + ex.Message);
            AddButtonAudit(component, action, serverId, result);
            try
            {
                await component.FollowupAsync(TruncateForDiscordMessage(result), ephemeral: true);
            }
            catch
            {
            }
        }
    }

    private async Task<string> RefreshButtonPanelAsync(string serverId)
    {
        await _refreshPanelsAsync();
        var server = await _findServerAsync(serverId);
        return server == null ? $"Server `{serverId}` was not found." : $"{server.Name} panel refreshed.";
    }

    private string GetButtonLogs(string serverId)
    {
        var log = _getRecentServerLog?.Invoke(serverId, 10) ?? string.Empty;
        return string.IsNullOrWhiteSpace(log)
            ? $"No recent log lines for server `{serverId}`."
            : TruncateForDiscordCodeBlock(log);
    }

    private void AddButtonAudit(SocketMessageComponent component, string command, string serverId, string result)
    {
        var guildId = (component.Channel as SocketGuildChannel)?.Guild.Id.ToString();
        _repository.AddAudit(new DiscordCommandAudit(
            DateTimeOffset.UtcNow,
            guildId,
            component.Channel.Id.ToString(),
            component.User.Id.ToString(),
            component.User.Username,
            command,
            "button",
            serverId,
            result));
    }

    private void LogBlockedRemoteControlAttempt(string userId, string username, string command, string? serverId)
    {
        var target = string.IsNullOrWhiteSpace(serverId) ? "no server" : $"server {serverId}";
        _log($"Discord remote control blocked for user {username} ({userId}), command '{command}', {target}.");
    }

    private async Task ObserveAsync(Task task, string failureMessage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"{failureMessage}: {ex.Message}");
        }
    }

    private async Task<string> WithTimeoutStringAsync(Task<string> task, TimeSpan timeout, string timeoutMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            _log(timeoutMessage);
            return "Discord action timed out before WindowsGSH finished processing it.";
        }

        return await task.ConfigureAwait(false);
    }

    private static string TruncateForDiscordCodeBlock(string value)
    {
        var sanitized = value.Replace("```", "'''", StringComparison.Ordinal);
        if (sanitized.Length > 1800)
        {
            sanitized = sanitized[^1800..];
        }

        return "```text" + Environment.NewLine + sanitized + Environment.NewLine + "```";
    }

    private static string TruncateForDiscordMessage(string value)
    {
        return value.Length <= DiscordMessageMaxLength
            ? value
            : value[..(DiscordMessageMaxLength - 3)] + "...";
    }
}
