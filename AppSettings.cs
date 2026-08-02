using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.IO;

namespace WindowsGSH;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";

    public bool ParallelServerOperations { get; set; }

    public bool MinimizeToTray { get; set; }

    public bool StartMinimized { get; set; }

    public bool StartWithWindows { get; set; }

    public bool FirstRunChecklistDismissed { get; set; }

    public bool RestartAppOnCrash { get; set; }

    public int AppCrashRestartMaxAttempts { get; set; } = 2;

    public int AppCrashRestartWindowMinutes { get; set; } = 10;

    public int BackupRetentionCount { get; set; } = 10;

    public int AutoUpdateIntervalMinutes { get; set; }

    public bool PersistentAuthenticatedSteamUpdates { get; set; }

    public bool PublicIpTrackingEnabled { get; set; }

    public string PublicIpEndpoint { get; set; } = "https://api.ipify.org";

    public int PublicIpCheckIntervalMinutes { get; set; } = 60;

    public string LastKnownPublicIp { get; set; } = string.Empty;

    public DateTimeOffset? LastPublicIpCheckedAt { get; set; }

    public bool DiscordBotEnabled { get; set; }

    public string DiscordBotName { get; set; } = "WindowsGSH";

    public string DiscordBotPrefix { get; set; } = "!";

    public string DiscordDashboardChannelId { get; set; } = string.Empty;

    public string DiscordNotificationsChannelId { get; set; } = string.Empty;

    public int DiscordDashboardRefreshMinutes { get; set; } = 5;

    public bool DiscordAllowDestructiveCommands { get; set; }

    public bool DiscordWebhookEnabled { get; set; }

    public List<string> KnownJavaRuntimePaths { get; set; } = [];

    public bool ExternalScheduledCommandsEnabled { get; set; }

    public List<string> ExternalCommandAllowedPaths { get; set; } = [];

    public bool ReducedMotion { get; set; }

    public bool SoftwareRendering { get; set; }

    public bool RuntimeDiagnosticsEnabled { get; set; }

    // Global kill-switch for the opt-in external reachability check (Tier 5.3c) - off by default.
    // Even when on, a check only ever runs when the user clicks "Test External Reachability" in a
    // specific server's Health tab; this setting never causes a background/automatic network call.
    public bool ExternalReachabilityChecksEnabled { get; set; }

    // Persisted acknowledgment that the user has seen the first-use privacy/consent explanation for
    // the opt-in external reachability check. This keeps the prompt to one-time only while leaving
    // the feature otherwise disabled until the user explicitly turns it on.
    public bool ExternalReachabilityConsentAcknowledged { get; set; }

    public bool WebEnabled { get; set; }

    public int WebPort { get; set; } = 5000;

    public string WebBindAddress { get; set; } = "127.0.0.1";

    public bool WebTrustForwardedHeaders { get; set; }

    // P3-03: the WebSocket console's deprecated ?token= query-string auth path (kept for
    // non-browser API clients that can't do the safer first-frame handshake). Fails closed
    // (false) by default - a fresh install, a damaged/unreadable settings file, or any future
    // code constructing AppSettings directly all get the secure value with no special-casing
    // required. LoadFrom is the ONLY place that grants the upgrade-compatibility exception, and
    // only when it can actually confirm the file is a validly-parsed existing settings file that
    // predates this field - see the migration comment there.
    public bool AllowLegacyWebSocketQueryStringAuth { get; set; }

    // DPAPI-protected base64 blob. Stored in WindowsGSH.jwt.key, not here.
    // [JsonIgnore] prevents it appearing in any serialised output; JwtKeyStore
    // handles load/save and migrates existing values from the settings JSON.
    [JsonIgnore]
    public string JwtSigningKeyProtected { get; set; } = string.Empty;

    public AppSettings CreateCopy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.KnownJavaRuntimePaths = [.. KnownJavaRuntimePaths];
        copy.ExternalCommandAllowedPaths = [.. ExternalCommandAllowedPaths];
        return copy;
    }

    public void ApplyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Theme = source.Theme;
        ParallelServerOperations = source.ParallelServerOperations;
        MinimizeToTray = source.MinimizeToTray;
        StartMinimized = source.StartMinimized;
        StartWithWindows = source.StartWithWindows;
        FirstRunChecklistDismissed = source.FirstRunChecklistDismissed;
        RestartAppOnCrash = source.RestartAppOnCrash;
        AppCrashRestartMaxAttempts = source.AppCrashRestartMaxAttempts;
        AppCrashRestartWindowMinutes = source.AppCrashRestartWindowMinutes;
        BackupRetentionCount = source.BackupRetentionCount;
        AutoUpdateIntervalMinutes = source.AutoUpdateIntervalMinutes;
        PersistentAuthenticatedSteamUpdates = source.PersistentAuthenticatedSteamUpdates;
        PublicIpTrackingEnabled = source.PublicIpTrackingEnabled;
        PublicIpEndpoint = source.PublicIpEndpoint;
        PublicIpCheckIntervalMinutes = source.PublicIpCheckIntervalMinutes;
        LastKnownPublicIp = source.LastKnownPublicIp;
        LastPublicIpCheckedAt = source.LastPublicIpCheckedAt;
        DiscordBotEnabled = source.DiscordBotEnabled;
        DiscordBotName = source.DiscordBotName;
        DiscordBotPrefix = source.DiscordBotPrefix;
        DiscordDashboardChannelId = source.DiscordDashboardChannelId;
        DiscordNotificationsChannelId = source.DiscordNotificationsChannelId;
        DiscordDashboardRefreshMinutes = source.DiscordDashboardRefreshMinutes;
        DiscordAllowDestructiveCommands = source.DiscordAllowDestructiveCommands;
        DiscordWebhookEnabled = source.DiscordWebhookEnabled;
        KnownJavaRuntimePaths = [.. source.KnownJavaRuntimePaths];
        ExternalScheduledCommandsEnabled = source.ExternalScheduledCommandsEnabled;
        ExternalCommandAllowedPaths = [.. source.ExternalCommandAllowedPaths];
        ReducedMotion = source.ReducedMotion;
        SoftwareRendering = source.SoftwareRendering;
        RuntimeDiagnosticsEnabled = source.RuntimeDiagnosticsEnabled;
        ExternalReachabilityChecksEnabled = source.ExternalReachabilityChecksEnabled;
        ExternalReachabilityConsentAcknowledged = source.ExternalReachabilityConsentAcknowledged;
        WebEnabled = source.WebEnabled;
        WebPort = source.WebPort;
        WebBindAddress = source.WebBindAddress;
        WebTrustForwardedHeaders = source.WebTrustForwardedHeaders;
        AllowLegacyWebSocketQueryStringAuth = source.AllowLegacyWebSocketQueryStringAuth;
    }

    private static string SettingsPath => AppPaths.GetPath("WindowsGSH.settings.json");

    public static AppSettings Load()
    {
        return LoadFrom(SettingsPath);
    }

    public static AppSettings LoadFrom(string path, int recoveryRetention = 5)
    {
        try
        {
            CleanupRecoveryFiles(path, recoveryRetention);
            if (!File.Exists(path))
            {
                // Fresh install, no settings file yet - AllowLegacyWebSocketQueryStringAuth
                // already defaults to false (secure).
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new JsonException("The settings document contained no settings object.");
            settings.KnownJavaRuntimePaths ??= [];
            settings.ExternalCommandAllowedPaths ??= [];

            // P3-03 migration compatibility: the ONLY case that gets the legacy-auth-enabled
            // exception is a settings file that parsed successfully but genuinely predates this
            // field - detected by the JSON key being absent, not by relying on a property
            // initializer default (a damaged file would get the same "default" that way, which is
            // exactly the fail-open bug this is avoiding). Anything else - fresh install, an
            // explicit value already in the file (true or false, either way honoured as-is since
            // JsonSerializer already applied it above), or a file that failed to parse at all
            // (handled in the catch blocks below) - keeps the secure default.
            using (var doc = JsonDocument.Parse(json))
            {
                if (!doc.RootElement.TryGetProperty(nameof(AllowLegacyWebSocketQueryStringAuth), out _))
                {
                    settings.AllowLegacyWebSocketQueryStringAuth = true;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.WebBindAddress) ||
                (!settings.WebBindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                 !System.Net.IPAddress.TryParse(settings.WebBindAddress, out _)))
                settings.WebBindAddress = "127.0.0.1";
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var recoveryPath = path;
            var preservationMessage = string.Empty;
            try
            {
                recoveryPath = PreserveMalformedSettings(path);
            }
            catch (Exception preserveEx) when (preserveEx is IOException or UnauthorizedAccessException)
            {
                preservationMessage = $" The original file could not be moved and was left unchanged: {preserveEx.Message}";
            }

            LocalStateRecoveryStatus.Report(
                "App settings",
                $"Malformed settings were replaced with safe defaults: {ex.Message}{preservationMessage}",
                recoveryPath,
                isFailure: true);
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalStateRecoveryStatus.Report(
                "App settings",
                $"Settings could not be read, so safe defaults were loaded. The original file was left unchanged: {ex.Message}",
                path,
                isFailure: true);
            return new AppSettings();
        }
    }

    public void Save()
    {
        SaveTo(SettingsPath);
    }

    public void SaveTo(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        PreserveLastKnownGood(path);
        AtomicFile.WriteAllText(path, json);
    }

    private static void PreserveLastKnownGood(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var existing = File.ReadAllText(path);
        _ = JsonSerializer.Deserialize<AppSettings>(existing)
            ?? throw new JsonException("The current settings file does not contain a settings object.");
        AtomicFile.WriteAllText(GetLastKnownGoodPath(path), existing);
    }

    private static string PreserveMalformedSettings(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var recoveryPath = Path.Combine(
            directory,
            $"{stem}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}{extension}");
        File.Move(path, recoveryPath);
        return recoveryPath;
    }

    private static string GetLastKnownGoodPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}.last-good{Path.GetExtension(path)}");
    }

    private static void CleanupRecoveryFiles(string path, int retention)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var pattern = $"{Path.GetFileNameWithoutExtension(path)}.corrupt-*{Path.GetExtension(path)}";
            foreach (var stale in Directory.EnumerateFiles(directory, pattern)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(Math.Max(0, retention)))
            {
                try
                {
                    File.Delete(stale);
                }
                catch
                {
                    // Best-effort maintenance must not prevent settings from loading.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumerating optional recovery files is also best-effort.
        }
    }
}

public sealed record AccessibilityVisualState(bool ReducedMotion, bool AnimationsEnabled)
{
    public static AccessibilityVisualState From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AccessibilityVisualState(settings.ReducedMotion, !settings.ReducedMotion);
    }

    // Sets DynamicResource keys used by XAML animations: bind Storyboard.Begin to AnimationsEnabled
    // or use a DataTrigger on ReducedMotion to collapse motion-only elements.
    public static void ApplyToApplication(AppSettings settings)
    {
        var application = System.Windows.Application.Current;
        if (application == null)
        {
            return;
        }

        var state = From(settings);
        application.Resources["ReducedMotion"] = state.ReducedMotion;
        application.Resources["AnimationsEnabled"] = state.AnimationsEnabled;
    }
}
