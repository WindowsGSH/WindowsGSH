using System.Diagnostics;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Events;

namespace WindowsGSH.Data;

/// <summary>
/// Tier 2 (Discord channel routing redesign), Chunk 2. Backfills the new per-server
/// <c>AlertChannelId</c>/<c>DashboardChannelId</c> fields (added in migration v9, see
/// <see cref="AppDatabase"/>'s <c>ApplyV9</c>) from the old global <c>AppSettings</c> channel IDs,
/// for servers that already have a <c>discord_server_settings</c> row but haven't set the new
/// fields explicitly yet.
/// </summary>
public static class DiscordChannelBackfillService
{
    /// <summary>
    /// Key in the <c>app_settings</c> table marking that the one-time legacy-global-channel
    /// backfill below has already run. Set the first time <see cref="Backfill"/> completes,
    /// regardless of whether it actually changed anything - see the High-severity follow-up on
    /// this method for why "ran, found nothing to do" must still count as "done."
    /// </summary>
    internal const string LegacyGlobalBackfillCompletedKey = "discord.legacyGlobalChannelBackfillCompleted";

    /// <summary>
    /// Backfills <c>AlertChannelId</c> (from <paramref name="globalNotificationsChannelId"/>) and
    /// <c>DashboardChannelId</c> (from <paramref name="globalDashboardChannelId"/>, only when
    /// <c>IncludeOnDashboard</c> is true) for every server-settings row where the new field is
    /// still blank. Card-channel backfill for rows that already existed at migration time (from
    /// <c>ChannelName</c>) already happens directly in the v9 SQL migration and isn't repeated
    /// here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// High-severity follow-up: this global-to-per-server copy runs **at most once ever**, guarded
    /// by <see cref="LegacyGlobalBackfillCompletedKey"/> in <paramref name="appSettingsRepository"/>
    /// - not "whenever the field happens to be blank." The global Application Alert Channel is a
    /// permanent, ongoing setting (Tier 2 Chunk 7), not a one-time migration source: running this
    /// on every startup meant a server whose Alert Channel a user deliberately left or set back to
    /// blank would silently get it re-populated from the global value on the very next restart -
    /// re-authorizing that channel for that server's alerts and commands without any action by the
    /// user, and directly contradicting the Settings page's own claim that the Application Alert
    /// Channel only receives public IP alerts. Once the marker is set (even if neither global value
    /// was configured, i.e. there was nothing to migrate), this copy never runs again - a server's
    /// blank Alert/Dashboard Channel from then on means exactly what the user set it to.
    /// </para>
    /// <para>
    /// P1 follow-up: the marker above only tells us whether *this fixed code* has run the copy
    /// before - it says nothing about installs that already went through the earlier, unmarked
    /// versions of this method. For those installs, migration v9 (and therefore the old
    /// marker-less, repeated-every-startup copy) has already existed for at least one full prior
    /// session by the time this version's marker is checked for the first time. If a user cleared
    /// a server's Alert Channel on that old code and then upgraded, the very first launch of this
    /// fixed version would otherwise see "no marker yet" and run the copy one more time - undoing
    /// that deliberate change at exactly the moment the user upgraded to get the fix. So before
    /// running the loop, <see cref="IsFreshLegacyRoutingMigrationWindow"/> checks whether schema
    /// v9 was applied at or after *this process's own* start time (a genuinely fresh migration,
    /// safe to run) versus in some earlier session (an upgrade - the marker is recorded as
    /// satisfied without touching any rows, since the old code already had its chances to migrate
    /// and the user's most recent state should be trusted instead).
    /// </para>
    /// <para>
    /// Before that one-time copy, this also creates a row for any server that has a legacy
    /// <c>discord.channel</c> value in its own <c>ServerConfig.json</c> but no
    /// <c>discord_server_settings</c> row at all — a valid state for a server imported or
    /// hand-configured before the DB mirror existed. This step is **not** gated by the same marker
    /// and keeps running every startup, so it still picks up servers added after the one-time
    /// window has closed. Its <c>AlertChannelId</c> always comes from the server's own JSON
    /// <c>discord.channel</c> value, never the global setting, so it can't reintroduce the bug the
    /// marker exists to prevent. Its <c>DashboardChannelId</c> is the one exception: when the
    /// server's own <c>includeOnDashboard</c> is true, it's seeded from the legacy global
    /// Dashboard Channel too - but only at the moment this row is first created (a row that didn't
    /// exist a moment ago can't have had a value the user deliberately cleared), so this doesn't
    /// reintroduce the bug either; it's a one-time migration for *this* server, same as every other
    /// server got during the original window. A server with no <c>discord</c> section or no
    /// <c>channel</c> value in its JSON at all is left untouched, same as before: it was genuinely
    /// never configured for Discord.
    /// </para>
    /// </remarks>
    public static DiscordChannelBackfillResult Backfill(
        DiscordRepository repository,
        string? globalDashboardChannelId,
        string? globalNotificationsChannelId,
        IWindowsGshEventBus? events = null,
        string? serversRootPath = null,
        AppSettingsRepository? appSettingsRepository = null,
        string? legacyBackfillCompletedKey = null,
        DateTimeOffset? schemaV9AppliedUtc = null,
        DateTimeOffset? processStartUtc = null)
    {
        var bus = events ?? WindowsGshEventBus.Shared;
        var appSettings = appSettingsRepository ?? new AppSettingsRepository();
        var markerKey = legacyBackfillCompletedKey ?? LegacyGlobalBackfillCompletedKey;
        var dashboardChannelId = string.IsNullOrWhiteSpace(globalDashboardChannelId) ? null : globalDashboardChannelId.Trim();
        var notificationsChannelId = string.IsNullOrWhiteSpace(globalNotificationsChannelId) ? null : globalNotificationsChannelId.Trim();

        var backfilledServerIds = new List<string>(
            CreateRowsForJsonOnlyServers(repository, serversRootPath ?? AppPaths.GetPath("servers"), dashboardChannelId, bus));

        if (appSettings.GetValue(markerKey) != "true")
        {
            var isFreshMigrationWindow = IsFreshLegacyRoutingMigrationWindow(
                schemaV9AppliedUtc ?? AppDatabase.GetSchemaMigrationAppliedUtc(9),
                processStartUtc ?? Process.GetCurrentProcess().StartTime.ToUniversalTime());

            if (isFreshMigrationWindow)
            {
                foreach (var settings in repository.GetAllServerSettings())
                {
                    var backfillDashboard = settings.IncludeOnDashboard
                        && dashboardChannelId != null
                        && string.IsNullOrWhiteSpace(settings.DashboardChannelId);
                    var backfillAlert = notificationsChannelId != null
                        && string.IsNullOrWhiteSpace(settings.AlertChannelId);

                    if (!backfillDashboard && !backfillAlert)
                    {
                        continue;
                    }

                    repository.SaveServerSettings(settings with
                    {
                        DashboardChannelId = backfillDashboard ? dashboardChannelId : settings.DashboardChannelId,
                        AlertChannelId = backfillAlert ? notificationsChannelId : settings.AlertChannelId
                    });

                    bus.Publish(new ServerLogEvent(
                        DateTimeOffset.UtcNow,
                        settings.ServerId,
                        null,
                        WindowsGshEventSeverity.Info,
                        "Discord",
                        BuildBackfillMessage(settings.ServerId, backfillDashboard, dashboardChannelId, backfillAlert, notificationsChannelId)));
                    backfilledServerIds.Add(settings.ServerId);
                }
            }

            // Either the fresh-migration loop above just ran (or found nothing to do), or this is
            // an upgrade from an install where schema v9 already existed - either way, the marker
            // is now satisfied and this copy must not run again on a later launch.
            appSettings.SetValue(markerKey, "true");
        }

        // A server whose row was just created from legacy JSON above, during the very first run,
        // can also get touched by the marker-gated loop right after (its AlertChannelId and
        // DashboardChannelId are already set from JSON/at creation, so there's nothing left for
        // that loop to do - see CreateRowsForJsonOnlyServers) - Distinct() keeps such a server's ID
        // appearing once in the result instead of twice.
        return new DiscordChannelBackfillResult(backfilledServerIds.Distinct().ToArray());
    }

    /// <summary>
    /// True if schema v9 (the migration that added the per-server Alert/Dashboard/Card columns)
    /// was applied at or after <paramref name="processStartUtc"/> - meaning *this* process's own
    /// startup migration is what just created it, so the one-time global-to-per-server copy has
    /// never had a chance to run before and is safe to run now. False if v9 was applied earlier -
    /// an existing install upgrading to this version, where the schema (and the old code that ran
    /// against it every startup, before this marker existed) has already been through at least one
    /// full prior session; running the copy now would land on the very first launch after
    /// upgrading, exactly when a user's most recent deliberate change on the old code is most
    /// likely to still be sitting there, and silently undo it. <paramref name="schemaV9AppliedUtc"/>
    /// being <c>null</c> (v9 not applied at all yet) is treated as fresh - there's nothing for an
    /// upgrade concern to protect if the columns don't exist. Pure/static so this decision is
    /// directly testable.
    /// </summary>
    internal static bool IsFreshLegacyRoutingMigrationWindow(DateTimeOffset? schemaV9AppliedUtc, DateTimeOffset processStartUtc)
    {
        return schemaV9AppliedUtc == null || schemaV9AppliedUtc.Value >= processStartUtc;
    }

    private static List<string> CreateRowsForJsonOnlyServers(DiscordRepository repository, string serversRootPath, string? dashboardChannelId, IWindowsGshEventBus bus)
    {
        var createdServerIds = new List<string>();
        if (!Directory.Exists(serversRootPath))
        {
            return createdServerIds;
        }

        foreach (var serverFolder in Directory.EnumerateDirectories(serversRootPath))
        {
            var configPath = Path.Combine(serverFolder, "ServerConfig.json");
            if (!File.Exists(configPath))
            {
                continue;
            }

            string serverId;
            string channel;
            bool includeOnDashboard;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                serverId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()!
                    : Path.GetFileName(serverFolder);

                if (!root.TryGetProperty("discord", out var discord) || discord.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                channel = discord.TryGetProperty("channel", out var channelElement) && channelElement.ValueKind == JsonValueKind.String
                    ? channelElement.GetString() ?? string.Empty
                    : string.Empty;
                includeOnDashboard = !discord.TryGetProperty("includeOnDashboard", out var includeElement) ||
                    includeElement.ValueKind != JsonValueKind.False;
            }
            catch
            {
                // Malformed/unreadable config - nothing safe to backfill from; leave it for the
                // server list's own "needs attention" handling to surface instead.
                continue;
            }

            if (string.IsNullOrWhiteSpace(channel) || repository.GetServerSettings(serverId) != null)
            {
                continue;
            }

            // The legacy discord.channel value served as both the alert target and the card
            // target before Tier 2 - carry it into both new fields so alert routing (Chunk 4)
            // keeps working for this server exactly as it did before, not just card display.
            // Dashboard is different: this row's DashboardChannelId is being set for the very
            // first time right now (a row that didn't exist a moment ago can't have had a
            // "deliberately blanked" value to protect), so it's safe - and necessary, since the
            // marker-gated loop below won't touch it after the one-time window has closed - to
            // inherit the legacy global Dashboard Channel here too, same as any other server got
            // during the original migration, when this server's own IncludeOnDashboard says so.
            var serverDashboardChannelId = includeOnDashboard ? dashboardChannelId : null;
            repository.SaveServerSettings(new DiscordServerSettings(
                serverId,
                channel,
                includeOnDashboard,
                CardChannelId: channel,
                AlertChannelId: channel,
                DashboardChannelId: serverDashboardChannelId));

            bus.Publish(new ServerLogEvent(
                DateTimeOffset.UtcNow,
                serverId,
                null,
                WindowsGshEventSeverity.Info,
                "Discord",
                serverDashboardChannelId == null
                    ? $"Discord channel backfill for server {serverId}: created a settings row from the legacy JSON discord.channel value ({channel}), used for both cardChannelId and alertChannelId."
                    : $"Discord channel backfill for server {serverId}: created a settings row from the legacy JSON discord.channel value ({channel}), used for both cardChannelId and alertChannelId, with dashboardChannelId={serverDashboardChannelId} from legacy global settings."));
            createdServerIds.Add(serverId);
        }

        return createdServerIds;
    }

    private static string BuildBackfillMessage(
        string serverId,
        bool backfillDashboard,
        string? dashboardChannelId,
        bool backfillAlert,
        string? notificationsChannelId)
    {
        var parts = new List<string>();
        if (backfillDashboard)
        {
            parts.Add($"dashboardChannelId={dashboardChannelId}");
        }

        if (backfillAlert)
        {
            parts.Add($"alertChannelId={notificationsChannelId}");
        }

        return $"Discord channel backfill for server {serverId}: set {string.Join(", ", parts)} from legacy global settings.";
    }
}

public sealed record DiscordChannelBackfillResult(IReadOnlyList<string> BackfilledServerIds)
{
    public bool RanAnyBackfill => BackfilledServerIds.Count > 0;
}
