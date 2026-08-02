using Discord;
using Discord.WebSocket;
using System.Collections.Concurrent;
using WindowsGSH.Core.Discord;
using WindowsGSH.Core.Servers;
using WindowsGSH.Data;

namespace WindowsGSH.Discord;

public sealed class DiscordBotHost : IAsyncDisposable
{
    private static readonly TimeSpan PanelRefreshTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DiscordStopTimeout = TimeSpan.FromSeconds(10);
    private readonly DiscordRepository _repository = new();
    private readonly Func<Task<IReadOnlyList<InstalledServer>>> _getServersAsync;
    private readonly Func<string, string, string, Task<string>> _runServerActionAsync;
    private readonly Action<string>? _log;
    private readonly Func<int, string>? _getRecentAppLog;
    private readonly Func<string, int, string>? _getRecentServerLog;
    private readonly DiscordStopAllConfirmationStore _stopAllConfirmations = new();
    private readonly DiscordCommandRateLimiter _commandRateLimiter = new();
    private readonly DiscordPanelService _panelService;
    private readonly DiscordButtonHandler _buttonHandler;
    private readonly DiscordNotificationService _notificationService;
    private DiscordSocketClient? _client;
    private DiscordBotSettings _settings = DiscordBotSettings.Default;
    private System.Threading.Timer? _panelRefreshTimer;
    private readonly DiscordLifecycleGate _lifecycleGate = new();
    private readonly DiscordHostStateTracker _state = new();
    private readonly ConcurrentDictionary<DiscordSocketClient, DiscordClientEventHandlers> _clientEventHandlers = new();
    private readonly ConcurrentDictionary<DiscordSocketClient, byte> _readyHandlersRunning = new();
    private readonly ConcurrentDictionary<ulong, byte> _registeredSlashCommandGuilds = new();
    private IReadOnlyList<InstalledServer> _serverSnapshot = [];
    private bool _enabled = true;

    public DiscordBotHost(
        Func<Task<IReadOnlyList<InstalledServer>>> getServersAsync,
        Func<string, string, string, Task<string>> runServerActionAsync,
        Action<string>? log = null,
        Func<int, string>? getRecentAppLog = null,
        Func<string, int, string>? getRecentServerLog = null)
    {
        _getServersAsync = getServersAsync;
        _runServerActionAsync = runServerActionAsync;
        _log = log;
        _getRecentAppLog = getRecentAppLog;
        _getRecentServerLog = getRecentServerLog;
        _panelService = new DiscordPanelService(_repository, GetServersAndUpdateSnapshotAsync, ResolveChannel, Log);
        _notificationService = new DiscordNotificationService(ResolveChannel, Log);
        _buttonHandler = new DiscordButtonHandler(
            _repository,
            _panelService,
            () => _settings,
            _runServerActionAsync,
            RefreshPanelsAsync,
            FindServerAsync,
            _getRecentServerLog,
            IsAdmin,
            Log);
    }

    public bool IsRunning => _client?.ConnectionState == ConnectionState.Connected;

    public DiscordHostStateSnapshot State => _state.Current;

    public void UpdateServerSnapshot(IReadOnlyList<InstalledServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        Volatile.Write(ref _serverSnapshot, servers.ToArray());
    }

    public event EventHandler<DiscordHostStateSnapshot>? StateChanged
    {
        add => _state.Changed += value;
        remove => _state.Changed -= value;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled && _client == null)
        {
            _state.Transition(DiscordHostState.Disabled);
        }
        else if (enabled && State.State == DiscordHostState.Disabled)
        {
            _state.Transition(DiscordHostState.Stopped);
        }
    }

    public void ReportFailure(string message)
    {
        _state.Transition(DiscordHostState.Failed, failure: message);
    }

    public async Task SendNotificationAsync(string message, InstalledServer? server = null)
    {
        if (_client == null ||
            _client.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        await _notificationService.SendNotificationAsync(message, GetAlertChannelId(server));
        await RefreshPanelsAsync();
    }

    /// <summary>
    /// Tier 2 Chunk 4: server-tied alerts route to that server's own <c>AlertChannelId</c> only -
    /// a blank value means "do not post," with no fallback to another channel. App-level alerts
    /// not tied to any server have no per-server concept to route through at all, so they keep
    /// using <c>NotificationsChannelId</c> - a deliberate, permanent exception, not a transitional
    /// one. Chunk 7 renamed its Settings UI control to "Application alert channel" to make this
    /// narrower scope explicit (it's no longer "the" notification channel now that servers have
    /// their own), but the setting itself, and this fallback, are not going away - there's no
    /// per-server equivalent for an alert that isn't about any one server. Currently the only
    /// event actually routed through here is <c>PublicIpChangedEvent</c> - there's no bot
    /// connected/disconnected notification implemented today (and a disconnected bot couldn't
    /// post one through its own connection anyway; that would need the separate webhook
    /// transport, which doesn't depend on the bot's gateway connection).
    /// </summary>
    private string? GetAlertChannelId(InstalledServer? server)
    {
        return server == null
            ? _settings.NotificationsChannelId
            : _repository.GetServerSettings(server.Id)?.AlertChannelId;
    }

    private IMessageChannel? ResolveChannel(string channelTarget)
    {
        if (_client == null)
        {
            return null;
        }

        var trimmed = channelTarget.Trim();
        if (ulong.TryParse(trimmed, out var channelId))
        {
            return _client.GetChannel(channelId) as IMessageChannel;
        }

        var channelName = trimmed.TrimStart('#');
        return _client.Guilds
            .SelectMany(guild => guild.TextChannels)
            .FirstOrDefault(channel => string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task StartAsync(DiscordBotSettings settings, string token)
    {
        await _lifecycleGate.EnterAsync();

        try
        {
            _state.Transition(DiscordHostState.Connecting);
            await StopCoreAsync(updateState: false);
            _settings = settings;
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds |
                                 GatewayIntents.GuildMessages |
                                 GatewayIntents.DirectMessages |
                                 GatewayIntents.MessageContent,
                UseInteractionSnowflakeDate = false,
                // Discord.Net defaults to RetryMode.AlwaysRetry, which includes RetryTimeouts -
                // an indefinitely-retrying REST call (e.g. a stalled connection) is a realistic
                // way for a panel refresh to hang forever and hold _panelUpdateLock permanently.
                // Rate-limit retry alone is bounded by Discord's own Retry-After header, so it's
                // safe to keep; timeout/502 retries are not.
                DefaultRetryMode = RetryMode.RetryRatelimit
            };

            var client = new DiscordSocketClient(config);
            var handlers = new DiscordClientEventHandlers(
                Connected: () => OnConnectedAsync(client),
                Ready: () => OnReadyAsync(client),
                Disconnected: exception => OnDisconnectedAsync(client, exception));
            _clientEventHandlers[client] = handlers;
            _client = client;
            client.Log += OnLogAsync;
            client.Connected += handlers.Connected;
            client.Ready += handlers.Ready;
            client.Disconnected += handlers.Disconnected;
            client.JoinedGuild += OnJoinedGuildAsync;
            client.MessageReceived += OnMessageReceivedAsync;
            client.ButtonExecuted += OnButtonExecutedAsync;
            client.SlashCommandExecuted += OnSlashCommandExecutedAsync;
            client.AutocompleteExecuted += OnAutocompleteExecutedAsync;

            await client.LoginAsync(TokenType.Bot, token);
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            _state.Transition(DiscordHostState.Failed, failure: ex.Message);
            await StopCoreAsync(updateState: false);
            throw;
        }
        finally
        {
            _lifecycleGate.Exit();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.EnterAsync();

        try
        {
            await StopCoreAsync(updateState: true);
        }
        finally
        {
            _lifecycleGate.Exit();
        }
    }

    private async Task StopCoreAsync(bool updateState)
    {
        var client = _client;
        if (client == null)
        {
            if (updateState)
            {
                _state.Transition(
                    !_enabled
                        ? DiscordHostState.Disabled
                        : DiscordHostState.Stopped);
            }
            return;
        }

        if (updateState)
        {
            _state.Transition(DiscordHostState.Stopping);
        }

        client.Log -= OnLogAsync;
        if (_clientEventHandlers.TryRemove(client, out var handlers))
        {
            client.Connected -= handlers.Connected;
            client.Ready -= handlers.Ready;
            client.Disconnected -= handlers.Disconnected;
        }
        client.JoinedGuild -= OnJoinedGuildAsync;
        client.MessageReceived -= OnMessageReceivedAsync;
        client.ButtonExecuted -= OnButtonExecutedAsync;
        client.SlashCommandExecuted -= OnSlashCommandExecutedAsync;
        client.AutocompleteExecuted -= OnAutocompleteExecutedAsync;
        await DisposePanelRefreshTimerAsync();
        try
        {
            if (await TryWithTimeoutAsync(client.StopAsync(), DiscordStopTimeout, "Discord bot stop timed out."))
            {
                await TryWithTimeoutAsync(client.LogoutAsync(), DiscordStopTimeout, "Discord bot logout timed out.");
            }
        }
        catch (Exception ex)
        {
            Log("Discord bot stop failed: " + ex.Message);
        }

        client.Dispose();
        if (ReferenceEquals(_client, client))
        {
            _client = null;
            _registeredSlashCommandGuilds.Clear();
        }

        if (updateState)
        {
            _state.Transition(_enabled ? DiscordHostState.Stopped : DiscordHostState.Disabled);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private Task OnReadyAsync(DiscordSocketClient client)
    {
        if (!_readyHandlersRunning.TryAdd(client, 0))
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(() => HandleReadyAsync(client));
        return Task.CompletedTask;
    }

    private Task OnConnectedAsync(DiscordSocketClient client)
    {
        if (ReferenceEquals(_client, client))
        {
            if (State.State != DiscordHostState.Stopping)
            {
                _state.Transition(DiscordHostState.Connected);
            }
        }

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(DiscordSocketClient client, Exception exception)
    {
        if (ReferenceEquals(_client, client) &&
            State.State is not DiscordHostState.Stopping and not DiscordHostState.Stopped)
        {
            _state.Transition(DiscordHostState.Failed, failure: exception?.Message ?? "Discord disconnected.");
        }

        return Task.CompletedTask;
    }

    private async Task HandleReadyAsync(DiscordSocketClient client)
    {
        await _lifecycleGate.EnterAsync();
        try
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            foreach (var guild in client.Guilds)
            {
                _repository.UpsertGuild(
                    guild.Id.ToString(),
                    guild.Name,
                _settings.BotPrefix,
                _settings.DashboardChannelId,
                _settings.NotificationsChannelId,
                _settings.DashboardRefreshMinutes);
            }

            FireAndForget(RegisterSlashCommandsAsync(client.Guilds), "Discord slash-command registration failed");
            await UpdatePresenceAsync();
            await RefreshPanelsAsync();
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            StartPanelRefreshTimer();
            _state.Transition(
                DiscordHostState.Ready,
                client.CurrentUser.Username,
                client.CurrentUser.Id);
            Log($"Discord bot connected as {client.CurrentUser.Username}.");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_client, client))
            {
                _state.Transition(
                    DiscordHostState.Failed,
                    client.CurrentUser?.Username,
                    client.CurrentUser?.Id,
                    ex.Message);
            }

            Log("Discord ready handling failed: " + ex.Message);
        }
        finally
        {
            _readyHandlersRunning.TryRemove(client, out _);
            _lifecycleGate.Exit();
        }
    }

    private sealed record DiscordClientEventHandlers(
        Func<Task> Connected,
        Func<Task> Ready,
        Func<Exception, Task> Disconnected);

    private void StartPanelRefreshTimer()
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.DashboardRefreshMinutes, 1, 1440));
        _panelRefreshTimer?.Dispose();
        _panelRefreshTimer = new System.Threading.Timer(
            _ => FireAndForget(RefreshPanelsAsync(), "Discord scheduled panel refresh failed"),
            null,
            interval,
            interval);
    }

    private async Task DisposePanelRefreshTimerAsync()
    {
        if (_panelRefreshTimer == null)
        {
            return;
        }

        await _panelRefreshTimer.DisposeAsync();
        _panelRefreshTimer = null;
    }

    private Task OnJoinedGuildAsync(SocketGuild guild)
    {
        _repository.UpsertGuild(
            guild.Id.ToString(),
            guild.Name,
            _settings.BotPrefix,
            _settings.DashboardChannelId,
            _settings.NotificationsChannelId,
            _settings.DashboardRefreshMinutes);
        FireAndForget(RegisterSlashCommandAsync(guild), $"Discord slash-command registration failed for guild {guild.Name}");
        return Task.CompletedTask;
    }

    private Task OnLogAsync(LogMessage message)
    {
        Log($"Discord: {message.Message ?? message.Exception?.Message ?? message.ToString()}");
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        FireAndForget(ProcessMessageReceivedAsync(message), "Discord message command failed");
        return Task.CompletedTask;
    }

    private async Task ProcessMessageReceivedAsync(SocketMessage message)
    {
        if (_client == null ||
            message.Author.IsBot ||
            message.Author.Id == _client.CurrentUser.Id ||
            string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var prefix = string.IsNullOrWhiteSpace(_settings.BotPrefix) ? "!" : _settings.BotPrefix;
        var root = $"{prefix}wgsh";
        var content = message.Content.Trim();
        if (!content.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !content.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var remainder = content.Length == root.Length ? "help" : content[root.Length..].Trim();
        var parsed = DiscordCommandParser.Parse(remainder);
        var command = parsed.Command;
        var args = parsed.Arguments;

        if (!await IsCommandChannelAllowedAsync(DiscordCommandPermissions.GetTargetServerId(command, args), message.Channel.Id))
        {
            // Tier 2 Chunk 6: an Alert Channel allow-list restricts commands to those channels -
            // silently ignored here, same as any other message that isn't a command (no reply, no
            // audit entry, nothing that reveals the bot is even listening). Checked against the
            // parsed command's own target server (P2 follow-up), not just any configured channel
            // globally, so a command about server A can't be issued from server B's channel.
            return;
        }

        var guildId = (message.Channel as SocketGuildChannel)?.Guild.Id.ToString();
        var channelId = message.Channel.Id.ToString();
        if (!parsed.IsValid)
        {
            var error = parsed.ErrorMessage ?? "Command could not be parsed.";
            TryAddCommandAudit(guildId, channelId, message.Author.Id.ToString(), message.Author.Username, command, args, null, error);
            await message.Channel.SendMessageAsync(error);
            return;
        }

        var result = await ExecuteAuthorizedCommandAsync(
            command,
            args,
            message.Author.Username,
            message.Author.Id.ToString(),
            guildId,
            channelId);
        await message.Channel.SendMessageAsync(ClampDiscordResponse(result));
    }

    private Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (_client == null)
        {
            return Task.CompletedTask;
        }

        FireAndForget(_buttonHandler.ProcessButtonExecutedAsync(component), "Discord button handler failed");
        return Task.CompletedTask;
    }

    private Task OnSlashCommandExecutedAsync(SocketSlashCommand command)
    {
        FireAndForget(ProcessSlashCommandAsync(command), "Discord slash command failed");
        return Task.CompletedTask;
    }

    private async Task ProcessSlashCommandAsync(SocketSlashCommand interaction)
    {
        if (_client == null ||
            !string.Equals(interaction.Data.Name, DiscordSlashCommands.RootName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var subcommand = interaction.Data.Options.FirstOrDefault();
        IReadOnlyDictionary<string, object?> optionValues = subcommand?.Options.ToDictionary(
            option => option.Name,
            option => (object?)option.Value,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var request = DiscordSlashCommandRequest.FromOptions(subcommand?.Name ?? string.Empty, optionValues);

        if (!await IsCommandChannelAllowedAsync(DiscordCommandPermissions.GetTargetServerId(request.Command, request.Arguments), interaction.Channel.Id))
        {
            // Tier 2 Chunk 6: same allow-list as text commands, checked against the parsed
            // command's own target server (P2 follow-up) so a command about server A can't be
            // issued from server B's channel. Slash commands arrive via a different event
            // (SlashCommandExecuted, not MessageReceived) than text commands, so this has to be
            // applied here too, not just in ProcessMessageReceivedAsync, or it would be an
            // inconsistent bypass. Unlike an ordinary text message, Discord already expects a
            // response to this interaction within a few seconds - returning without ever
            // responding (as a silent text-command ignore does) would leave the caller looking at
            // a generic "the application did not respond" gateway-timeout error, so respond
            // ephemerally instead (P2 follow-up).
            await interaction.RespondAsync("Commands are not accepted in this channel.", ephemeral: true);
            return;
        }

        await interaction.DeferAsync(ephemeral: true);
        var guildId = (interaction.Channel as SocketGuildChannel)?.Guild.Id.ToString();
        var channelId = interaction.Channel.Id.ToString();
        string result;

        if (!request.IsValid)
        {
            result = request.ErrorMessage ?? "Slash command could not be parsed.";
            TryAddCommandAudit(
                guildId,
                channelId,
                interaction.User.Id.ToString(),
                interaction.User.Username,
                request.Command,
                request.Arguments,
                DiscordCommandPermissions.GetTargetServerId(request.Command, request.Arguments),
                result);
        }
        else if (DiscordSlashCommands.RunsInBackground(request.Command, request.Arguments))
        {
            var authorizationFailure = GetCommandAuthorizationFailure(
                request.Command,
                request.Arguments,
                interaction.User.Username,
                interaction.User.Id.ToString(),
                out var serverId);
            if (authorizationFailure != null)
            {
                TryAddCommandAudit(
                    guildId,
                    channelId,
                    interaction.User.Id.ToString(),
                    interaction.User.Username,
                    request.Command,
                    request.Arguments,
                    serverId,
                    authorizationFailure);
                await SetSlashResponseAsync(interaction, authorizationFailure);
                return;
            }

            await SetSlashResponseAsync(
                interaction,
                DiscordSlashCommands.GetAcceptedMessage(request.Command, request.Arguments));
            FireAndForget(
                ExecutePreauthorizedCommandAsync(
                    request.Command,
                    request.Arguments,
                    interaction.User.Username,
                    interaction.User.Id.ToString(),
                    guildId,
                    channelId,
                    serverId),
                $"Discord background slash command '{request.Command}' failed");
            return;
        }
        else
        {
            result = await ExecuteAuthorizedCommandAsync(
                request.Command,
                request.Arguments,
                interaction.User.Username,
                interaction.User.Id.ToString(),
                guildId,
                channelId);
        }

        await SetSlashResponseAsync(interaction, result);
    }

    private Task OnAutocompleteExecutedAsync(SocketAutocompleteInteraction interaction)
    {
        FireAndForget(ProcessAutocompleteAsync(interaction), "Discord server autocomplete failed");
        return Task.CompletedTask;
    }

    private async Task ProcessAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        if (_client == null ||
            !string.Equals(interaction.Data.CommandName, DiscordSlashCommands.RootName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(interaction.Data.Current.Name, "server", StringComparison.OrdinalIgnoreCase))
        {
            await interaction.RespondAsync([]);
            return;
        }

        try
        {
            var servers = Volatile.Read(ref _serverSnapshot);
            var allowedServerIds = _repository.GetServerIdsForAdmin(interaction.User.Id.ToString());
            var matches = DiscordServerAutocomplete.Filter(
                servers.Select(server => new DiscordServerAutocompleteItem(server.Id, server.Name)),
                allowedServerIds,
                interaction.Data.Current.Value?.ToString());
            await interaction.RespondAsync(matches.Select(server =>
                new AutocompleteResult(ClampAutocompleteName($"{server.Name} ({server.Id})"), server.Id)));
        }
        catch (Exception ex)
        {
            Log("Discord server autocomplete failed: " + ex.Message);
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync([]);
            }
        }
    }

    private async Task RegisterSlashCommandsAsync(IEnumerable<SocketGuild> guilds)
    {
        foreach (var guild in guilds)
        {
            try
            {
                await RegisterSlashCommandAsync(guild);
            }
            catch (Exception ex)
            {
                Log($"Discord slash-command registration failed for guild {guild.Name}: {ex.Message}");
            }
        }
    }

    private async Task RegisterSlashCommandAsync(SocketGuild guild)
    {
        if (!_registeredSlashCommandGuilds.TryAdd(guild.Id, 0))
        {
            return;
        }

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync([DiscordSlashCommandBuilder.Build()]);
        }
        catch
        {
            _registeredSlashCommandGuilds.TryRemove(guild.Id, out _);
            throw;
        }
    }

    public DateTimeOffset? LastSuccessfulPanelRefreshUtc { get; private set; }

    public Task RefreshPanelsAsync()
    {
        return RefreshPanelsAsync(null);
    }

    public async Task RefreshPanelsAsync(IReadOnlyList<InstalledServer>? serverSnapshot)
    {
        if (serverSnapshot != null)
        {
            UpdateServerSnapshot(serverSnapshot);
        }

        // Cooperative cancellation reaches the Discord API calls via RequestOptions.CancelToken,
        // but not every awaited call inside RefreshPanelsCoreAsync is guaranteed to honor it (a
        // future change could easily add one that doesn't, the way UpdatePresenceAsync's
        // SetGameAsync originally didn't below). WaitAsync is a hard backstop on top of that: even
        // if something inside ignores the token, this method still returns within PanelRefreshTimeout
        // - matching what the old Task.WhenAny-based WithTimeoutAsync guaranteed, without giving up
        // the real cancellation (which lets a hung REST call actually abort instead of just being
        // abandoned to keep running in the background). Without either, a single hung REST call
        // permanently holds _panelUpdateLock and every later refresh silently no-ops until restart.
        using var timeoutCts = new CancellationTokenSource(PanelRefreshTimeout);
        try
        {
            await RefreshPanelsCoreAsync(serverSnapshot, timeoutCts.Token).WaitAsync(PanelRefreshTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            Log("Discord panel refresh timed out. Last successful refresh: " +
                (LastSuccessfulPanelRefreshUtc?.ToString("u") ?? "never") + ".");
        }
    }

    private async Task RefreshPanelsCoreAsync(IReadOnlyList<InstalledServer>? serverSnapshot, CancellationToken cancellationToken)
    {
        if (_client == null || _client.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        var servers = await _panelService.RefreshPanelsCoreAsync(_settings, serverSnapshot, cancellationToken);
        if (servers == null)
        {
            return;
        }

        LastSuccessfulPanelRefreshUtc = DateTimeOffset.UtcNow;

        try
        {
            await UpdatePresenceAsync(servers);
        }
        catch (Exception ex)
        {
            Log("Discord presence update failed after panel refresh: " + ex.Message);
        }
    }

    private async Task<string> ExecuteAuthorizedCommandAsync(
        string command,
        IReadOnlyList<string> args,
        string username,
        string userId,
        string? guildId,
        string channelId)
    {
        var authorizationFailure = GetCommandAuthorizationFailure(
            command,
            args,
            username,
            userId,
            out var serverId);
        if (authorizationFailure != null)
        {
            TryAddCommandAudit(guildId, channelId, userId, username, command, args, serverId, authorizationFailure);
            return authorizationFailure;
        }

        return await ExecutePreauthorizedCommandAsync(
            command,
            args,
            username,
            userId,
            guildId,
            channelId,
            serverId);
    }

    private string? GetCommandAuthorizationFailure(
        string command,
        IReadOnlyList<string> args,
        string username,
        string userId,
        out string? serverId)
    {
        serverId = DiscordCommandPermissions.GetTargetServerId(command, args);
        try
        {
            if (DiscordCommandRateLimiter.RequiresAcquire(command, args) &&
                !_commandRateLimiter.TryAcquire(userId, command, serverId, out var retryAfter))
            {
                Log(
                    "Discord command rate-limited for user " +
                    $"{username} ({userId}), command '{command}', " +
                    $"{(string.IsNullOrWhiteSpace(serverId) ? "no server" : $"server {serverId}")}, " +
                    $"retry after {Math.Ceiling(retryAfter.TotalSeconds)}s.");
                return DiscordCommandRateLimiter.RateLimitedMessage;
            }

            if (!DiscordCommandPermissions.IsAllowedByRemoteControlSetting(command, _settings.AllowDestructiveCommands))
            {
                LogBlockedRemoteControlAttempt(userId, username, command, serverId);
                return DiscordCommandPermissions.RemoteControlDisabledMessage;
            }

            return IsAdmin(userId, serverId, command)
                ? null
                : "You do not have permission to use that WindowsGSH command.";
        }
        catch (Exception ex)
        {
            return "Command failed: " + ex.Message;
        }
    }

    private async Task<string> ExecutePreauthorizedCommandAsync(
        string command,
        IReadOnlyList<string> args,
        string username,
        string userId,
        string? guildId,
        string channelId,
        string? serverId)
    {
        var result = string.Empty;
        try
        {
            result = await ExecuteCommandAsync(command, args, username, userId, guildId, channelId);
            return result;
        }
        catch (Exception ex)
        {
            result = "Command failed: " + ex.Message;
            return result;
        }
        finally
        {
            TryAddCommandAudit(guildId, channelId, userId, username, command, args, serverId, result);
        }
    }

    private void TryAddCommandAudit(
        string? guildId,
        string channelId,
        string userId,
        string username,
        string command,
        IReadOnlyList<string> args,
        string? serverId,
        string result)
    {
        try
        {
            _repository.AddAudit(new DiscordCommandAudit(
                DateTimeOffset.UtcNow,
                guildId,
                channelId,
                userId,
                username,
                command,
                args.Count == 0 ? null : string.Join(' ', args),
                serverId,
                result));
        }
        catch (Exception ex)
        {
            Log($"Discord command audit write failed for '{command}': {ex.Message}");
        }
    }

    private static Task SetSlashResponseAsync(SocketSlashCommand interaction, string result)
    {
        var response = ClampDiscordResponse(result);
        return interaction.ModifyOriginalResponseAsync(properties => properties.Content = response);
    }

    private static string ClampDiscordResponse(string response)
    {
        const int maximumLength = 1900;
        if (string.IsNullOrWhiteSpace(response))
        {
            return "Command completed without output.";
        }

        return response.Length <= maximumLength
            ? response
            : response[..(maximumLength - 20)] + Environment.NewLine + "… output truncated";
    }

    private static string ClampAutocompleteName(string name)
    {
        const int maximumLength = 100;
        return name.Length <= maximumLength ? name : name[..maximumLength];
    }

    private async Task<string> ExecuteCommandAsync(
        string command,
        IReadOnlyList<string> args,
        string username,
        string userId,
        string? guildId,
        string channelId)
    {
        return command switch
        {
            "help" => GetHelpText(),
            "check" => GetPermissionText(args),
            "list" => await GetServerListAsync(),
            "status" => await GetServerStatusAsync(args),
            "stats" => await GetStatsAsync(),
            "logs" => GetLogs(args),
            "start" or "stop" or "restart" or "update" or "backup" or "send" or "sendr" => await RunActionAsync(command, args, username),
            "stopall" => await StopAllAsync(args, userId, guildId, channelId),
            _ => GetHelpText()
        };
    }

    private async Task<string> RunActionAsync(string command, IReadOnlyList<string> args, string username)
    {
        if (args.Count == 0)
        {
            return $"Usage: `{_settings.BotPrefix}wgsh {command} <serverId>`";
        }

        var serverId = args[0];
        var actionArgs = args.Count > 1 ? string.Join(' ', args.Skip(1)) : string.Empty;
        return await _runServerActionAsync(command, serverId, actionArgs);
    }

    private async Task<string> StopAllAsync(IReadOnlyList<string> args, string userId, string? guildId, string channelId)
    {
        var prefix = string.IsNullOrWhiteSpace(_settings.BotPrefix) ? "!" : _settings.BotPrefix;
        if (args.Count == 0 || !string.Equals(args[0], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            _stopAllConfirmations.Request(userId, guildId, channelId);
            return $"This will stop all running servers. Run `/wgsh stopall confirm:true` or `{prefix}wgsh stopall confirm` within 60 seconds to continue.";
        }

        if (!_stopAllConfirmations.TryConfirm(userId, guildId, channelId))
        {
            return $"Stop all confirmation expired or was not found. Run `/wgsh stopall` or `{prefix}wgsh stopall` again to continue.";
        }

        var servers = await GetServersAndUpdateSnapshotAsync();
        var running = servers.Where(server => server.CanStop).ToArray();
        if (running.Length == 0)
        {
            return "No running servers to stop.";
        }

        var results = new List<string>();
        foreach (var server in running)
        {
            results.Add(await _runServerActionAsync("stop", server.Id, string.Empty));
        }

        return string.Join(Environment.NewLine, results);
    }

    private async Task<string> GetServerListAsync()
    {
        var servers = await GetServersAndUpdateSnapshotAsync();
        if (servers.Count == 0)
        {
            return "No servers are installed.";
        }

        return string.Join(Environment.NewLine, servers.Select(server =>
            $"`{server.Id}` {server.CurrentStatusText} - {server.Name} ({server.PlayerCount})"));
    }

    private async Task<string> GetServerStatusAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return $"Usage: `{_settings.BotPrefix}wgsh status <serverId>`";
        }

        var server = await FindServerAsync(args[0]);
        return server == null
            ? $"Server `{args[0]}` was not found."
            : $"`{server.Id}` {server.Name}: {server.CurrentStatusText}, players {server.PlayerCount}, CPU {server.CpuUsage}, RAM {server.MemoryUsage}, uptime {server.Uptime}.";
    }

    private async Task<string> GetStatsAsync()
    {
        var servers = await GetServersAndUpdateSnapshotAsync();
        var online = servers.Count(server => server.Status == ServerRuntimeStatus.Running);
        var warnings = servers.Count(server => server.Status == ServerRuntimeStatus.Warning);
        var offline = servers.Count(server => server.Status == ServerRuntimeStatus.Offline);
        return $"Servers: {servers.Count} total, {online} online, {warnings} warning, {offline} offline.";
    }

    private string GetLogs(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return _getRecentAppLog?.Invoke(10) ?? string.Empty;
        }

        var log = _getRecentServerLog?.Invoke(args[0], 10) ?? string.Empty;
        return string.IsNullOrWhiteSpace(log) ? $"No recent log lines for server `{args[0]}`." : log;
    }

    private string GetPermissionText(IReadOnlyList<string> args)
    {
        return args.Count == 0
            ? "You have permission to use WindowsGSH Discord commands."
            : $"Permission check target: `{args[0]}`.";
    }

    private string GetHelpText()
    {
        var prefix = string.IsNullOrWhiteSpace(_settings.BotPrefix) ? "!" : _settings.BotPrefix;
        return string.Join(Environment.NewLine, new[]
        {
            "`/wgsh` provides the same commands with autocomplete and private responses.",
            $"`{prefix}wgsh check`",
            $"`{prefix}wgsh list`",
            $"`{prefix}wgsh status <serverId>`",
            $"`{prefix}wgsh start <serverId>`",
            $"`{prefix}wgsh stop <serverId>`",
            $"`{prefix}wgsh stopAll`",
            $"`{prefix}wgsh restart <serverId>`",
            $"`{prefix}wgsh update <serverId>`",
            $"`{prefix}wgsh backup <serverId>`",
            $"`{prefix}wgsh send <serverId> <command>`",
            $"`{prefix}wgsh sendR <serverId> <command>`",
            $"`{prefix}wgsh stats`",
            $"`{prefix}wgsh logs [serverId]`"
        });
    }

    /// <summary>
    /// Tier 2 Chunk 6 (P2 follow-up): a command that targets a specific server (e.g.
    /// <c>restart server-a</c>) must be checked against *that server's own* Alert Channel, not
    /// just "any configured Alert Channel anywhere" - the Server Config UI's own help text says
    /// Alert Channel is "where commands for this server are accepted" (singular, per server), so
    /// a global flattened allow-list would let server A's commands through in server B's channel.
    /// A command with no target server (list/stats/help/check/logs-with-no-argument/stopall) has
    /// no per-server context to check, so it falls back to "any configured Alert Channel anywhere"
    /// - the original, simpler policy - since there's nothing more specific to check it against.
    /// </summary>
    /// <remarks>
    /// Medium follow-up: <paramref name="targetServerArgument"/> is whatever the caller typed -
    /// normal command execution (<see cref="FindServerAsync"/>) accepts a server ID or its display
    /// name, case-insensitively, so the channel check must resolve to the same canonical server
    /// before looking up its Alert Channel, or a valid name/differently-cased ID would be wrongly
    /// rejected. An argument that doesn't resolve to any real server isn't a channel-permission
    /// question at all - it's let through unfiltered so the normal "server not found" handling
    /// further down the pipeline can respond, rather than reporting a misleading
    /// channel-restriction rejection for what's really just a typo.
    /// </remarks>
    internal async Task<bool> IsCommandChannelAllowedAsync(string? targetServerArgument, ulong channelId)
    {
        var configuredAlertChannelIds = _repository.GetDistinctAlertChannelIds();
        if (string.IsNullOrWhiteSpace(targetServerArgument))
        {
            var resolvedAllowedChannelIds = ResolveAllowedChannelIds(configuredAlertChannelIds, value => ResolveChannel(value)?.Id);
            return IsChannelAllowed(configuredAlertChannelIds, resolvedAllowedChannelIds, channelId);
        }

        var targetServer = await FindServerAsync(targetServerArgument);
        if (targetServer == null)
        {
            return true;
        }

        var targetServerAlertChannel = _repository.GetServerSettings(targetServer.Id)?.AlertChannelId;
        return IsServerCommandChannelAllowed(
            configuredAlertChannelIds.Count > 0,
            targetServerAlertChannel,
            value => ResolveChannel(value)?.Id,
            channelId);
    }

    /// <summary>
    /// Decides whether a command about one specific server may come from
    /// <paramref name="channelId"/>. If no Alert Channel is configured anywhere
    /// (<paramref name="anyAlertChannelsConfigured"/> is false), commands are accepted from any
    /// channel - first-run safety, unchanged from the non-server-specific case. Once channel
    /// restrictions are active anywhere, this specific server's own configured value (resolved
    /// through the same bot-wide <see cref="ResolveChannel"/> used for alert delivery) is the only
    /// channel that may issue commands about it - a server with no Alert Channel of its own is
    /// rejected everywhere in that case (fail closed), not silently treated as unrestricted, since
    /// the whole point is that server A's commands can't ride in on server B's channel. Pure/
    /// static (aside from the resolver function) so this is testable without a Discord.Net
    /// connection.
    /// </summary>
    internal static bool IsServerCommandChannelAllowed(
        bool anyAlertChannelsConfigured,
        string? targetServerAlertChannelValue,
        Func<string, ulong?> resolveChannelId,
        ulong channelId)
    {
        if (!anyAlertChannelsConfigured)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetServerAlertChannelValue))
        {
            return false;
        }

        return resolveChannelId(targetServerAlertChannelValue) == channelId;
    }

    /// <summary>
    /// Resolves each configured Alert Channel value through <paramref name="resolveChannelId"/> -
    /// in production, the same bot-wide <see cref="ResolveChannel"/> used to deliver alerts, so
    /// "which channel can send commands" and "which channel alerts actually go to" are always the
    /// same answer for a given configured value, whether it's a numeric ID or a name. Takes a
    /// plain resolver function rather than calling <see cref="ResolveChannel"/> directly so this
    /// stays testable without a Discord.Net connection.
    /// </summary>
    internal static HashSet<ulong> ResolveAllowedChannelIds(
        IReadOnlyList<string> configuredAlertChannelIds,
        Func<string, ulong?> resolveChannelId)
    {
        var resolved = new HashSet<ulong>();
        foreach (var value in configuredAlertChannelIds)
        {
            var resolvedId = resolveChannelId(value);
            if (resolvedId != null)
            {
                resolved.Add(resolvedId.Value);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Tier 2 Chunk 6: <paramref name="configuredAlertChannelIds"/> being empty means no Alert
    /// Channels are configured anywhere yet, so commands are accepted from any channel - first-run
    /// safety, so a fresh install (or one mid-migration through Chunk 2's backfill) is never
    /// locked out of its own bot. Once at least one Alert Channel exists, only channels that
    /// actually resolved may issue commands - deliberately checked against the raw configured
    /// count, not the resolved count, so a non-empty configuration that failed to resolve at all
    /// (e.g. a named channel that no longer exists) still fails closed (rejects everything) rather
    /// than silently falling back to "anywhere allowed." Pure/static so the decision is testable
    /// without any Discord.Net socket types.
    /// </summary>
    internal static bool IsChannelAllowed(
        IReadOnlyCollection<string> configuredAlertChannelIds,
        IReadOnlyCollection<ulong> resolvedAllowedChannelIds,
        ulong channelId)
    {
        return configuredAlertChannelIds.Count == 0 || resolvedAllowedChannelIds.Contains(channelId);
    }

    private bool IsAdmin(string userId, string? serverId, string command)
    {
        var serverIds = _repository.GetServerIdsForAdmin(userId);
        return DiscordCommandPermissions.HasPermission(serverIds, command, serverId);
    }

    private void LogBlockedRemoteControlAttempt(string userId, string username, string command, string? serverId)
    {
        var target = string.IsNullOrWhiteSpace(serverId) ? "no server" : $"server {serverId}";
        Log($"Discord remote control blocked for user {username} ({userId}), command '{command}', {target}.");
    }

    private async Task<InstalledServer?> FindServerAsync(string serverId)
    {
        var servers = await GetServersAndUpdateSnapshotAsync();
        return ResolveTargetServer(servers, serverId);
    }

    /// <summary>
    /// Matches a server ID or display name, case-insensitively - extracted as its own step (used
    /// by <see cref="FindServerAsync"/>) so command-channel filtering's server resolution is
    /// testable against a plain server list, without needing the async server-fetch delegate or
    /// any Discord.Net connection.
    /// </summary>
    internal static InstalledServer? ResolveTargetServer(IReadOnlyList<InstalledServer> servers, string targetServerArgument)
    {
        return servers.FirstOrDefault(server =>
            string.Equals(server.Id, targetServerArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(server.Name, targetServerArgument, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<InstalledServer>> GetServersAndUpdateSnapshotAsync()
    {
        var servers = await _getServersAsync();
        UpdateServerSnapshot(servers);
        return servers;
    }

    private async Task UpdatePresenceAsync(IReadOnlyList<InstalledServer>? serverSnapshot = null)
    {
        if (_client == null)
        {
            return;
        }

        // SetGameAsync sends a gateway presence update, not a REST call - Discord.Net gives it no
        // RequestOptions/CancelToken overload to cooperatively cancel. The hard WaitAsync backstop
        // in RefreshPanelsAsync is what actually bounds this if it ever stalls.
        var servers = serverSnapshot ?? await GetServersAndUpdateSnapshotAsync();
        await _client.SetGameAsync($"{servers.Count} game server{(servers.Count == 1 ? string.Empty : "s")}");
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }

    private void FireAndForget(Task task, string failureMessage)
    {
        _ = ObserveAsync(task, failureMessage);
    }

    private async Task ObserveAsync(Task task, string failureMessage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"{failureMessage}: {ex.Message}");
        }
    }

    private async Task<bool> TryWithTimeoutAsync(Task task, TimeSpan timeout, string timeoutMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            Log(timeoutMessage);
            return false;
        }

        await task.ConfigureAwait(false);
        return true;
    }
}
