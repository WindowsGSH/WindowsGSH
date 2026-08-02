using Discord;
using System.IO;
using System.Text.Json;
using WindowsGSH.Core.Servers;
using WindowsGSH.Data;

namespace WindowsGSH.Discord;

internal sealed class DiscordPanelService
{
    private readonly DiscordRepository _repository;
    private readonly Func<Task<IReadOnlyList<InstalledServer>>> _getServersAsync;
    private readonly Func<string, IMessageChannel?> _resolveChannel;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _panelUpdateLock = new(1, 1);

    // Every refresh cycle previously re-rendered and re-PATCHed every panel unconditionally, even
    // when nothing about the server changed - the sole cause of the routine Discord rate-limit
    // warnings seen on real deployments with several servers sharing one channel's small per-message
    // edit bucket. Only ever read/written from within RefreshPanelsCoreAsync's _panelUpdateLock, so
    // a plain Dictionary (not ConcurrentDictionary) is safe. Deliberately in-memory only, not
    // persisted: an empty cache after a restart just means "the first cycle re-sends everything,"
    // which is the same, always-correct fallback as before this fix existed.
    private readonly Dictionary<string, SentPanelState> _lastSentPanelStates = new(StringComparer.OrdinalIgnoreCase);

    // Skipping UpsertPanelMessageAsync entirely on an unchanged fingerprint also skips the
    // GetMessageAsync existence check inside it - so a message someone deletes manually in Discord
    // would never be noticed or recreated for as long as the server's content stayed unchanged,
    // potentially the lifetime of the application (a real regression the fingerprint fix itself
    // introduced, caught by review). Forcing a real existence check at least once per
    // MaxVerificationInterval bounds how long a manually-deleted message can stay missing. That
    // check must NOT imply an edit, though (an earlier version of this fix forced a full
    // UpsertPanelMessageAsync call - GetMessageAsync *and* an unconditional ModifyAsync - every
    // MaxVerificationInterval for every unchanged panel, which just moved the rate-limit bursts from
    // every 5 minutes to every 30 rather than actually eliminating them for content that hasn't
    // changed; caught by a second review pass). PanelRefreshDecision.VerifyOnly exists specifically
    // to let the existence check happen without an edit when the message is still there.
    private static readonly TimeSpan MaxVerificationInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultVolatileRefreshInterval = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, DateTimeOffset> _lastVerifiedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _volatileRefreshInterval;

    public DiscordPanelService(
        DiscordRepository repository,
        Func<Task<IReadOnlyList<InstalledServer>>> getServersAsync,
        Func<string, IMessageChannel?> resolveChannel,
        Action<string>? log = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? volatileRefreshInterval = null)
    {
        _repository = repository;
        _getServersAsync = getServersAsync;
        _resolveChannel = resolveChannel;
        _log = log ?? (_ => { });
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _volatileRefreshInterval = volatileRefreshInterval ?? DefaultVolatileRefreshInterval;
    }

    internal enum PanelRefreshDecision
    {
        /// <summary>Fingerprint unchanged and verified recently - make no Discord calls at all.</summary>
        Skip,

        /// <summary>Fingerprint unchanged but the verification window elapsed - check the message
        /// still exists (GetMessageAsync) and recreate it if not, but never edit unchanged content.</summary>
        VerifyOnly,

        /// <summary>Fingerprint changed (or this panel has never been sent) - a real edit/create.</summary>
        Send
    }

    internal readonly record struct PanelFingerprints(int Meaningful, int Exact);

    private sealed record SentPanelState(PanelFingerprints Fingerprints, DateTimeOffset SentAt);

    internal PanelRefreshDecision DecidePanelRefresh(string panelKey, PanelFingerprints fingerprints)
    {
        if (!_lastSentPanelStates.TryGetValue(panelKey, out var lastState) ||
            lastState.Fingerprints.Meaningful != fingerprints.Meaningful)
        {
            return PanelRefreshDecision.Send;
        }

        // Uptime and resource usage are useful but inherently volatile. Keep exact displayed values
        // whenever a send occurs, while limiting edits caused only by those fields to a predictable
        // cadence. Meaningful status/player/control changes above always bypass this delay.
        if (lastState.Fingerprints.Exact != fingerprints.Exact)
        {
            return _utcNow() - lastState.SentAt >= _volatileRefreshInterval
                ? PanelRefreshDecision.Send
                : PanelRefreshDecision.Skip;
        }

        var verifiedRecently = _lastVerifiedAt.TryGetValue(panelKey, out var lastVerified) &&
            _utcNow() - lastVerified < MaxVerificationInterval;
        return verifiedRecently ? PanelRefreshDecision.Skip : PanelRefreshDecision.VerifyOnly;
    }

    internal PanelRefreshDecision DecidePanelRefresh(string panelKey, int fingerprint) =>
        DecidePanelRefresh(panelKey, new PanelFingerprints(fingerprint, fingerprint));

    internal void RecordPanelSent(string panelKey, PanelFingerprints fingerprints)
    {
        var now = _utcNow();
        _lastSentPanelStates[panelKey] = new SentPanelState(fingerprints, now);
        _lastVerifiedAt[panelKey] = now;
    }

    internal void RecordPanelSent(string panelKey, int fingerprint) =>
        RecordPanelSent(panelKey, new PanelFingerprints(fingerprint, fingerprint));

    /// <summary>
    /// Updated/Created advance both content and verification state. Verified advances only the
    /// existence-check clock: no Discord edit occurred, so it must preserve the actual content-send
    /// time used to throttle volatile-only changes. Failed advances neither, ensuring a real update
    /// is retried on the next cycle.
    /// </summary>
    internal void RecordPanelSentIfSuccessful(
        string panelKey,
        PanelFingerprints fingerprints,
        PanelUpsertOutcome outcome)
    {
        if (outcome == PanelUpsertOutcome.Verified)
        {
            _lastVerifiedAt[panelKey] = _utcNow();
            return;
        }

        if (outcome is PanelUpsertOutcome.Updated or PanelUpsertOutcome.Created)
        {
            RecordPanelSent(panelKey, fingerprints);
        }
    }

    internal void RecordPanelSentIfSuccessful(
        string panelKey,
        int fingerprint,
        PanelUpsertOutcome outcome) =>
        RecordPanelSentIfSuccessful(
            panelKey,
            new PanelFingerprints(fingerprint, fingerprint),
            outcome);

    /// <summary>
    /// The full fingerprint for a per-server card, folding in everything BuildServerComponents'
    /// output can vary by (CanStart/CanStop) plus the resolved destination channel - panelKey alone
    /// ("server:{id}") does not encode the channel the way the dashboard case's key does, so the
    /// channel must be part of the fingerprint itself or a Card Channel change with otherwise-
    /// unchanged content would never be detected as a change.
    /// </summary>
    internal static int ComputeServerPanelFingerprint(Embed embed, bool canStart, bool canStop, ulong channelId)
    {
        return HashCode.Combine(ComputeEmbedFingerprint(embed), canStart, canStop, channelId);
    }

    internal static PanelFingerprints ComputeDashboardPanelFingerprints(
        Embed exactEmbed,
        IReadOnlyList<InstalledServer> servers)
    {
        var meaningfulServers = servers.Select(WithoutVolatileMetrics).ToArray();
        var meaningfulEmbed = DiscordEmbedRenderer.BuildDashboardEmbed(meaningfulServers);
        return new PanelFingerprints(
            ComputeEmbedFingerprint(meaningfulEmbed),
            ComputeEmbedFingerprint(exactEmbed));
    }

    internal static PanelFingerprints ComputeServerPanelFingerprints(
        Embed exactEmbed,
        InstalledServer server,
        DiscordServerCardOptions options,
        bool canStart,
        bool canStop,
        ulong channelId)
    {
        var meaningfulEmbed = DiscordEmbedRenderer.BuildServerEmbed(
            WithoutVolatileMetrics(server),
            options);
        return new PanelFingerprints(
            ComputeServerPanelFingerprint(meaningfulEmbed, canStart, canStop, channelId),
            ComputeServerPanelFingerprint(exactEmbed, canStart, canStop, channelId));
    }

    private static InstalledServer WithoutVolatileMetrics(InstalledServer server) =>
        server with
        {
            Uptime = string.Empty,
            CpuUsage = string.Empty,
            MemoryUsage = string.Empty,
            QueryDurationMilliseconds = null
        };

    public async Task<IReadOnlyList<InstalledServer>?> RefreshPanelsCoreAsync(
        DiscordBotSettings settings,
        IReadOnlyList<InstalledServer>? serverSnapshot,
        CancellationToken cancellationToken)
    {
        if (!await _panelUpdateLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            IReadOnlyList<InstalledServer> servers;
            try
            {
                servers = serverSnapshot ?? await _getServersAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log("Discord panel refresh failed to load servers: " + ex.Message);
                return null;
            }

            // Each dashboard channel and each server card is refreshed independently below (own
            // try/catch per item) so one missing permission or transient channel error can't
            // block every other channel/card - and, by returning the server list normally, still
            // lets the caller's presence update run afterward.
            await UpsertDashboardPanelsAsync(servers, cancellationToken);
            foreach (var server in servers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpsertServerPanelAsync(settings, server, cancellationToken);
            }

            return servers;
        }
        finally
        {
            _panelUpdateLock.Release();
        }
    }

    /// <summary>
    /// Fingerprints an embed's user-visible content, deliberately excluding <see cref="Embed.Timestamp"/>
    /// - both BuildDashboardEmbed and BuildServerEmbed call WithCurrentTimestamp(), so the timestamp
    /// always differs between calls regardless of whether anything meaningful changed; Embed's own
    /// Equals/GetHashCode include it and so cannot be used directly for this purpose. Title/Description/
    /// Color/Footer/Fields together are a complete description of what the embed actually renders.
    /// </summary>
    internal static int ComputeEmbedFingerprint(Embed embed)
    {
        var hash = new HashCode();
        hash.Add(embed.Type);
        hash.Add(embed.Title);
        hash.Add(embed.Description);
        hash.Add(embed.Url);
        hash.Add(embed.Color);
        hash.Add(embed.Footer);
        foreach (var field in embed.Fields)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }

    public bool IsCurrentPanelNonce(string serverId, string panelNonce)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(panelNonce))
        {
            return false;
        }

        var saved = _repository.GetPanelMessage($"server:{serverId}");
        return saved != null && string.Equals(saved.PanelNonce, panelNonce, StringComparison.Ordinal);
    }

    private async Task UpsertDashboardPanelsAsync(IReadOnlyList<InstalledServer> servers, CancellationToken cancellationToken)
    {
        var groupsByConfiguredValue = GroupServersByDashboardChannel(servers, _repository);
        var merged = MergeServersByResolvedChannelId(groupsByConfiguredValue, value => _resolveChannel(value)?.Id);

        var currentPanelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channelId, included) in merged.Groups)
        {
            var panelKey = $"dashboard:{channelId}";
            currentPanelKeys.Add(panelKey);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_resolveChannel(channelId.ToString()) is not IMessageChannel channel)
                {
                    continue;
                }

                var embed = DiscordEmbedRenderer.BuildDashboardEmbed(included);
                // Dashboard panelKeys already encode the resolved channel ("dashboard:{channelId}"),
                // so a channel change alone always produces a fresh key/cache-miss here - no need to
                // fold channel.Id into this fingerprint the way the per-server case below must.
                var fingerprints = ComputeDashboardPanelFingerprints(embed, included);
                var decision = DecidePanelRefresh(panelKey, fingerprints);
                if (decision == PanelRefreshDecision.Skip)
                {
                    continue;
                }

                var outcome = await UpsertPanelMessageAsync(panelKey, channel, embed, components: null, panelNonce: string.Empty, contentChanged: decision == PanelRefreshDecision.Send, cancellationToken);
                RecordPanelSentIfSuccessful(panelKey, fingerprints, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"Discord dashboard panel refresh failed for channel '{channelId}': {ex.Message}");
            }
        }

        foreach (var configuredValue in merged.UnresolvedConfiguredValues)
        {
            _log($"Discord dashboard channel '{configuredValue}' was not found.");
        }

        // A channel that failed to resolve this cycle might just be temporarily unreachable (e.g.
        // the gateway cache isn't warm yet), not genuinely removed from configuration - deleting
        // its panel record now would cause a duplicate message once it resolves again next cycle.
        var classification = ClassifyUnresolvedDashboardValues(merged.UnresolvedConfiguredValues);
        currentPanelKeys.UnionWith(classification.KeysToPreserve);
        if (classification.CanSafelyCleanStale)
        {
            await RemoveStaleDashboardPanelsAsync(currentPanelKeys, cancellationToken);
        }
    }

    /// <summary>
    /// Decides, for dashboard channel values that failed to resolve this cycle, which existing
    /// panel records must be preserved (rather than treated as stale) and whether stale-panel
    /// cleanup can run at all this cycle. A numeric configured value is self-describing - its
    /// would-be panel key (<c>dashboard:{value}</c>) can be preserved directly even without a
    /// successful resolution. A name-based value that fails to resolve could correspond to any
    /// existing record and we have no way to tell which, so the conservative choice is to skip
    /// stale-panel cleanup entirely for this cycle rather than risk deleting a record for a
    /// channel that's still configured, just temporarily unresolvable. Pure/static so this
    /// decision is testable without a Discord connection.
    /// </summary>
    internal static UnresolvedDashboardClassification ClassifyUnresolvedDashboardValues(IReadOnlyList<string> unresolvedConfiguredValues)
    {
        var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canSafelyCleanStale = true;
        foreach (var value in unresolvedConfiguredValues)
        {
            if (ulong.TryParse(value, out var numericChannelId))
            {
                preserve.Add($"dashboard:{numericChannelId}");
            }
            else
            {
                canSafelyCleanStale = false;
            }
        }

        return new UnresolvedDashboardClassification(preserve, canSafelyCleanStale);
    }

    /// <summary>
    /// Removes any <c>dashboard:{channelId}</c> panel record no longer in
    /// <paramref name="currentPanelKeys"/> - e.g. every server left that dashboard channel, moved
    /// to a different one, or cleared its Dashboard Channel entirely. Without this the old message
    /// would keep displaying outdated server state indefinitely. The legacy bare
    /// <c>"dashboard"</c> key (pre-Tier-2, no colon) never matches the <c>"dashboard:"</c> prefix
    /// and is intentionally left alone - see this tier's own note that it's orphaned for v1.
    /// </summary>
    private async Task RemoveStaleDashboardPanelsAsync(IReadOnlySet<string> currentPanelKeys, CancellationToken cancellationToken)
    {
        foreach (var panelKey in _repository.GetPanelKeysByPrefix("dashboard:"))
        {
            if (currentPanelKeys.Contains(panelKey))
            {
                continue;
            }

            await TryRemovePanelMessageAsync(panelKey, cancellationToken);
        }
    }

    /// <summary>
    /// Tier 2 Chunk 5: groups servers by their own <c>DashboardChannelId</c> exactly as
    /// configured - a blank value means "not on any dashboard" (the old global "Include on
    /// Dashboard" checkbox was retired in Chunk 3). This is a first pass only: two servers can
    /// have different configured values (a numeric ID vs. a channel name) that resolve to the
    /// same actual Discord channel, so <see cref="MergeServersByResolvedChannelId"/> collapses
    /// groups like that afterward, once resolution is available. Kept as its own pure step
    /// (rather than merging in one pass) so it stays testable without a Discord connection.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<InstalledServer>> GroupServersByDashboardChannel(
        IReadOnlyList<InstalledServer> servers,
        DiscordRepository repository)
    {
        return servers
            .Select(server => (Server: server, DashboardChannelId: repository.GetServerSettings(server.Id)?.DashboardChannelId))
            .Where(item => !string.IsNullOrWhiteSpace(item.DashboardChannelId))
            .GroupBy(item => item.DashboardChannelId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<InstalledServer>)group.Select(item => item.Server).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses groups keyed by whatever string each server has configured into groups keyed by
    /// the actually-resolved Discord channel ID, so a channel referenced by numeric ID on one
    /// server and by name on another still produces a single combined dashboard instead of two
    /// partial ones in the same channel. <paramref name="resolveChannelId"/> is a plain
    /// string-to-ID lookup (not the full channel-resolving delegate) so this stays testable
    /// without needing a Discord.Net channel object.
    /// </summary>
    internal static DashboardChannelMergeResult MergeServersByResolvedChannelId(
        IReadOnlyDictionary<string, IReadOnlyList<InstalledServer>> groupsByConfiguredValue,
        Func<string, ulong?> resolveChannelId)
    {
        var merged = new Dictionary<ulong, List<InstalledServer>>();
        var unresolved = new List<string>();
        foreach (var (configuredValue, included) in groupsByConfiguredValue)
        {
            var channelId = resolveChannelId(configuredValue);
            if (channelId == null)
            {
                unresolved.Add(configuredValue);
                continue;
            }

            if (!merged.TryGetValue(channelId.Value, out var servers))
            {
                servers = [];
                merged[channelId.Value] = servers;
            }

            servers.AddRange(included);
        }

        return new DashboardChannelMergeResult(
            merged.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InstalledServer>)pair.Value),
            unresolved);
    }

    private async Task UpsertServerPanelAsync(DiscordBotSettings settings, InstalledServer server, CancellationToken cancellationToken)
    {
        var panelKey = $"server:{server.Id}";
        try
        {
            var cardChannelId = GetCardChannelId(server, _repository);
            if (string.IsNullOrWhiteSpace(cardChannelId))
            {
                // The Card Channel was explicitly cleared: remove the stale record (and the old
                // message, best-effort) immediately rather than leaving a previously-interactive
                // card active with buttons nobody expects to still work.
                await TryRemovePanelMessageAsync(panelKey, cancellationToken);
                return;
            }

            if (_resolveChannel(cardChannelId) is not IMessageChannel channel)
            {
                _log($"Discord card channel '{cardChannelId}' was not found for server {server.Id}.");
                // The configured destination no longer resolves - any existing card (almost
                // certainly in a different channel than whatever's configured now) must stop
                // being treated as authorized via IsCurrentPanelNonce, even though a replacement
                // can't be created this cycle.
                await TryRemovePanelMessageAsync(panelKey, cancellationToken);
                return;
            }

            var options = GetServerCardOptions(server);
            var embed = DiscordEmbedRenderer.BuildServerEmbed(server, options);
            var componentServer = settings.AllowDestructiveCommands
                ? server
                : server with { CanStart = false, CanStop = false };

            var fingerprints = ComputeServerPanelFingerprints(
                embed,
                server,
                options,
                componentServer.CanStart,
                componentServer.CanStop,
                channel.Id);
            var decision = DecidePanelRefresh(panelKey, fingerprints);
            if (decision == PanelRefreshDecision.Skip)
            {
                return;
            }

            // A fresh nonce/components pair is cheap to build (no Discord API call) even for a
            // VerifyOnly pass where it likely won't be used - UpsertPanelMessageAsync only actually
            // needs it if the existence check finds the message missing and must recreate it, in
            // which case a brand-new message needs its own fresh nonce regardless of contentChanged.
            var panelNonce = CreatePanelNonce();
            var components = DiscordEmbedRenderer.BuildServerComponents(componentServer, panelNonce);
            var outcome = await UpsertPanelMessageAsync(panelKey, channel, embed, components, panelNonce, contentChanged: decision == PanelRefreshDecision.Send, cancellationToken);
            RecordPanelSentIfSuccessful(panelKey, fingerprints, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"Discord server panel refresh failed for server {server.Id}: {ex.Message}");
            // Creating/sending the replacement card failed - same reasoning as the unresolved
            // case above: the old card must not remain authorized just because we couldn't
            // confirm or replace it this cycle.
            await TryRemovePanelMessageAsync(panelKey, cancellationToken);
        }
    }

    internal enum PanelUpsertOutcome
    {
        /// <summary>Message existed, content was unchanged, left alone - a VerifyOnly existence check.</summary>
        Verified,

        /// <summary>Message existed and was successfully edited.</summary>
        Updated,

        /// <summary>Message was confirmed missing (or there was no usable saved record) and was created.</summary>
        Created,

        /// <summary>The lookup or edit failed with an exception - nothing about the message or the
        /// repository record actually changed. Callers must NOT advance the fingerprint cache on this
        /// outcome, or a genuinely-failed update would be silently treated as successfully applied and
        /// never retried.</summary>
        Failed
    }

    private async Task<PanelUpsertOutcome> UpsertPanelMessageAsync(string panelKey, IMessageChannel channel, Embed embed, MessageComponent? components, string panelNonce, bool contentChanged, CancellationToken cancellationToken)
    {
        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        var saved = _repository.GetPanelMessage(panelKey);
        if (saved != null &&
            ulong.TryParse(saved.ChannelId, out var savedChannelId) &&
            ulong.TryParse(saved.MessageId, out var savedMessageId) &&
            savedChannelId == channel.Id)
        {
            // Discord.Net's GetMessageAsync returns null for a genuine 404 (confirmed via decompile -
            // ChannelHelper.GetMessageAsync explicitly maps a not-found API response to null, never an
            // exception for that case) and only throws for something else going wrong entirely
            // (network interruption, an API outage, permission/cache delay, rate limiting). Those are
            // two very different situations: a null return is real confirmation the message is gone,
            // safe to recreate; an exception is not confirmation of anything and the original message
            // most likely still exists - falling through to SendMessageAsync in that case would create
            // a genuine duplicate next to a message that never actually went away. This distinction
            // matters more now that periodic re-verification (MaxVerificationInterval) means this
            // lookup runs on a schedule even when nothing else prompted it, not just when the
            // maintainer happens to be watching.
            IUserMessage? message;
            try
            {
                message = await channel.GetMessageAsync(savedMessageId, options: requestOptions) as IUserMessage;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"Discord panel lookup failed for {panelKey}: {ex.Message}");
                return PanelUpsertOutcome.Failed;
            }

            if (message != null)
            {
                if (contentChanged)
                {
                    try
                    {
                        await message.ModifyAsync(properties =>
                        {
                            properties.Embed = embed;
                            properties.Components = components;
                        }, requestOptions);
                        _repository.SavePanelMessage(new DiscordPanelMessage(panelKey, channel.Id.ToString(), savedMessageId.ToString(), panelNonce));
                        return PanelUpsertOutcome.Updated;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Same reasoning as the lookup above - the message is confirmed to exist (it
                        // was just fetched successfully), so a failed edit must not fall through to
                        // creating a duplicate. Leave the saved record as-is and report Failed, not a
                        // normal return - the caller must not cache this fingerprint as sent, or the
                        // next refresh cycle would wrongly see it as already-current and never retry
                        // the edit that actually failed.
                        _log($"Discord panel update failed for {panelKey}: {ex.Message}");
                        return PanelUpsertOutcome.Failed;
                    }
                }

                // else: this was a periodic existence-only re-verification (see
                // MaxVerificationInterval/PanelRefreshDecision.VerifyOnly) - the message is still
                // there and nothing about its content actually changed, so leave the Discord message
                // and its already-current saved nonce untouched. Editing it here would be exactly the
                // redundant PATCH this whole fingerprint mechanism exists to avoid.
                return PanelUpsertOutcome.Verified;
            }
        }

        // Reaching here means either there was no usable saved record at all, or GetMessageAsync
        // completed successfully and confirmed (via a null return, not an exception) that the message
        // is genuinely gone - always (re)create regardless of contentChanged. A confirmed-missing
        // message is never "redundant" to recreate, and a brand-new message needs its own send
        // regardless of whether the fingerprint technically matched the last one sent. Deliberately
        // not wrapped in its own try/catch: a thrown SendMessageAsync propagates straight to the
        // caller's own outer try/catch (UpsertServerPanelAsync/UpsertDashboardPanelsAsync), which
        // already handles a failed create appropriately - and, just as importantly, means the
        // `await UpsertPanelMessageAsync(...)` call site itself throws, so the caller's conditional
        // RecordPanelSent (gated on the returned outcome) is never reached for this failure either.
        var newMessage = await channel.SendMessageAsync(embed: embed, components: components, options: requestOptions);
        _repository.SavePanelMessage(new DiscordPanelMessage(panelKey, channel.Id.ToString(), newMessage.Id.ToString(), panelNonce));

        // Reaching here means the panel moved to a different channel, or its old message
        // couldn't be found/edited - either way, saved (fetched above, before the overwrite)
        // still describes the message this one just replaced. Best-effort clean it up so users
        // don't see a stale, no-longer-authorized card left behind in the old channel.
        if (saved != null &&
            ulong.TryParse(saved.MessageId, out var previousMessageId) &&
            _resolveChannel(saved.ChannelId) is IMessageChannel previousChannel)
        {
            try
            {
                await previousChannel.DeleteMessageAsync(previousMessageId, requestOptions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"Discord previous panel message delete failed for {panelKey}: {ex.Message}");
            }
        }

        return PanelUpsertOutcome.Created;
    }

    /// <summary>
    /// Deletes a panel record and best-effort deletes the Discord message it pointed at. Used
    /// both when a server's Card Channel is cleared and when a dashboard channel no longer has
    /// any servers grouped into it - in both cases the DB record is removed immediately (so a
    /// stale nonce can't keep an old card's buttons active, and a stale dashboard can't keep
    /// displaying outdated state), and the actual message delete is attempted but not required to
    /// succeed (the bot may have been removed from the channel, lack permission, etc.).
    /// </summary>
    private async Task RemovePanelMessageAsync(string panelKey, CancellationToken cancellationToken)
    {
        // Clear regardless of whether saved is null below - a caller can remove a panel this
        // service never itself sent (e.g. IsCurrentPanelNonce found the repository record before
        // this process ever ran a refresh cycle), and a stale cached fingerprint left behind would
        // wrongly suppress the real re-creation the next cycle should perform for this panelKey.
        _lastSentPanelStates.Remove(panelKey);
        _lastVerifiedAt.Remove(panelKey);

        var saved = _repository.GetPanelMessage(panelKey);
        if (saved == null)
        {
            return;
        }

        _repository.DeletePanelMessage(panelKey);

        if (ulong.TryParse(saved.MessageId, out var messageId) &&
            _resolveChannel(saved.ChannelId) is IMessageChannel channel)
        {
            try
            {
                await channel.DeleteMessageAsync(messageId, new RequestOptions { CancelToken = cancellationToken });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log($"Discord panel message delete failed for {panelKey}: {ex.Message}");
            }
        }
    }

    private async Task TryRemovePanelMessageAsync(string panelKey, CancellationToken cancellationToken)
    {
        try
        {
            await RemovePanelMessageAsync(panelKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"Discord panel cleanup failed for {panelKey}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tier 2 Chunk 5: prefers <c>CardChannelId</c>; falls back to the legacy <c>ChannelName</c>
    /// (a row saved through the pre-Chunk-3 editor after the v9 migration ran kept
    /// <c>ChannelName</c> current without ever touching <c>CardChannelId</c>); falls back to the
    /// server's own JSON <c>discord.channel</c> (a server with no <c>discord_server_settings</c>
    /// row at all - should be rare now that Chunk 2's backfill creates one for any server with a
    /// legacy JSON value, but this keeps card display working even if that backfill hasn't run
    /// yet for some reason, e.g. a server added mid-session). Internal so tests can exercise the
    /// fallback chain directly.
    /// </summary>
    internal static string GetCardChannelId(InstalledServer server, DiscordRepository repository)
    {
        var saved = repository.GetServerSettings(server.Id);
        if (!string.IsNullOrWhiteSpace(saved?.CardChannelId))
        {
            return saved.CardChannelId;
        }

        if (!string.IsNullOrWhiteSpace(saved?.ChannelName))
        {
            return saved.ChannelName;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(server.ConfigPath));
            if (document.RootElement.TryGetProperty("discord", out var discord) &&
                discord.TryGetProperty("channel", out var channel) &&
                channel.ValueKind == JsonValueKind.String)
            {
                return channel.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static DiscordServerCardOptions GetServerCardOptions(InstalledServer server)
    {
        try
        {
            var settings = ServerConfigAppSettings.FromConfigJson(File.ReadAllText(server.ConfigPath)).Discord;
            return new DiscordServerCardOptions(
                settings.ShowServerAddress,
                settings.ShowPlayerCount,
                settings.ShowUptime,
                settings.ShowResourceUsage,
                settings.ShowMap,
                settings.ShowGameVersion,
                settings.ShowQuerySummary,
                settings.ShowPlayerList,
                settings.ShowQueryDiagnostics);
        }
        catch
        {
            return DiscordServerCardOptions.Default;
        }
    }

    private static string CreatePanelNonce()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }
}

internal sealed record DashboardChannelMergeResult(
    IReadOnlyDictionary<ulong, IReadOnlyList<InstalledServer>> Groups,
    IReadOnlyList<string> UnresolvedConfiguredValues);

internal sealed record UnresolvedDashboardClassification(
    IReadOnlySet<string> KeysToPreserve,
    bool CanSafelyCleanStale);
