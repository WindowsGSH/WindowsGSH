using System.Text.Json;
using WindowsGSH.Core.Diagnostics;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public AppSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "WindowsGSH.settings.json");
        LocalStateRecoveryStatus.Clear();
    }

    [Fact]
    public void SaveTo_replaces_settings_atomically_without_leaving_temporary_files()
    {
        File.WriteAllText(_path, """{"Theme":"Dark"}""");

        new AppSettings { Theme = "Light", BackupRetentionCount = 7 }.SaveTo(_path);

        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
        Assert.Equal("Light", loaded!.Theme);
        Assert.Equal(7, loaded.BackupRetentionCount);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void SaveTo_preserves_previous_valid_settings_as_last_known_good()
    {
        File.WriteAllText(_path, """{"Theme":"Dark","BackupRetentionCount":4}""");

        new AppSettings { Theme = "Light" }.SaveTo(_path);

        var backupPath = Path.Combine(_root, "WindowsGSH.settings.last-good.json");
        Assert.True(File.Exists(backupPath));
        var backup = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(backupPath));
        Assert.Equal("Dark", backup!.Theme);
        Assert.Equal(4, backup.BackupRetentionCount);
    }

    [Fact]
    public void Desktop_settings_round_trip()
    {
        new AppSettings
        {
            MinimizeToTray = true,
            StartMinimized = true,
            StartWithWindows = true,
            FirstRunChecklistDismissed = true
        }.SaveTo(_path);

        var loaded = AppSettings.LoadFrom(_path);

        Assert.True(loaded.MinimizeToTray);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.StartWithWindows);
        Assert.True(loaded.FirstRunChecklistDismissed);
    }

    [Fact]
    public void Accessibility_settings_round_trip()
    {
        new AppSettings
        {
            ReducedMotion = true,
            SoftwareRendering = true,
            RuntimeDiagnosticsEnabled = true,
            ExternalReachabilityChecksEnabled = true,
            ExternalReachabilityConsentAcknowledged = true
        }.SaveTo(_path);

        var loaded = AppSettings.LoadFrom(_path);

        Assert.True(loaded.ReducedMotion);
        Assert.True(loaded.SoftwareRendering);
        Assert.True(loaded.RuntimeDiagnosticsEnabled);
        Assert.True(loaded.ExternalReachabilityChecksEnabled);
        Assert.True(loaded.ExternalReachabilityConsentAcknowledged);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Accessibility_visual_state_composes_animation_state(
        bool reducedMotion,
        bool expectedAnimationsEnabled)
    {
        var state = AccessibilityVisualState.From(new AppSettings { ReducedMotion = reducedMotion });

        Assert.Equal(reducedMotion, state.ReducedMotion);
        Assert.Equal(expectedAnimationsEnabled, state.AnimationsEnabled);
    }

    [Fact]
    public void CreateCopy_and_ApplyFrom_keep_live_settings_isolated_until_commit()
    {
        var live = new AppSettings
        {
            MinimizeToTray = false,
            StartWithWindows = false,
            PersistentAuthenticatedSteamUpdates = false,
            ReducedMotion = false,
            SoftwareRendering = false,
            RuntimeDiagnosticsEnabled = false,
            ExternalReachabilityChecksEnabled = false,
            KnownJavaRuntimePaths = ["old-java"]
        };
        var candidate = live.CreateCopy();

        candidate.MinimizeToTray = true;
        candidate.StartWithWindows = true;
        candidate.PersistentAuthenticatedSteamUpdates = true;
        candidate.ReducedMotion = true;
        candidate.SoftwareRendering = true;
        candidate.RuntimeDiagnosticsEnabled = true;
        candidate.ExternalReachabilityChecksEnabled = true;
        candidate.KnownJavaRuntimePaths.Add("new-java");

        Assert.False(live.MinimizeToTray);
        Assert.False(live.StartWithWindows);
        Assert.False(live.PersistentAuthenticatedSteamUpdates);
        Assert.False(live.ReducedMotion);
        Assert.False(live.SoftwareRendering);
        Assert.False(live.RuntimeDiagnosticsEnabled);
        Assert.False(live.ExternalReachabilityChecksEnabled);
        Assert.Equal(["old-java"], live.KnownJavaRuntimePaths);

        live.ApplyFrom(candidate);

        Assert.True(live.MinimizeToTray);
        Assert.True(live.StartWithWindows);
        Assert.True(live.PersistentAuthenticatedSteamUpdates);
        Assert.True(live.ReducedMotion);
        Assert.True(live.SoftwareRendering);
        Assert.True(live.RuntimeDiagnosticsEnabled);
        Assert.True(live.ExternalReachabilityChecksEnabled);
        Assert.Equal(["old-java", "new-java"], live.KnownJavaRuntimePaths);
    }

    [Fact]
    public void LoadFrom_preserves_malformed_settings_and_returns_safe_defaults()
    {
        File.WriteAllText(_path, """{"Theme":""");

        var settings = AppSettings.LoadFrom(_path);

        Assert.Equal("Dark", settings.Theme);
        // P3-03: a security control should fail closed on a damaged file, not preserve
        // whatever the previous (unknown/unverifiable) value might have been.
        Assert.False(settings.AllowLegacyWebSocketQueryStringAuth);
        Assert.False(File.Exists(_path));
        var recovery = Assert.Single(Directory.EnumerateFiles(_root, "WindowsGSH.settings.corrupt-*.json"));
        Assert.Equal("""{"Theme":""", File.ReadAllText(recovery));
        var status = Assert.Single(LocalStateRecoveryStatus.Snapshot());
        Assert.Equal(recovery, status.RecoveryPath);
        Assert.True(status.IsFailure);
    }

    [Fact]
    public void LoadFrom_keeps_unreadable_settings_and_returns_safe_defaults()
    {
        File.WriteAllText(_path, """{"Theme":"Light"}""");
        using var lockStream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var settings = AppSettings.LoadFrom(_path);

        Assert.Equal("Dark", settings.Theme);
        // P3-03: same fail-closed reasoning as the malformed-file case above.
        Assert.False(settings.AllowLegacyWebSocketQueryStringAuth);
        Assert.True(File.Exists(_path));
        var status = Assert.Single(LocalStateRecoveryStatus.Snapshot());
        Assert.Equal(_path, status.RecoveryPath);
        Assert.Contains("could not be read", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("left unchanged", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveTo_propagates_IOException_when_destination_is_locked()
    {
        // Callers such as AppSettingsView rely on Save throwing to report failure to the user.
        // This test guards against accidentally swallowing the exception at the Save() level.
        File.WriteAllText(_path, """{"Theme":"Dark"}""");
        using var lockStream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => new AppSettings { Theme = "Light" }.SaveTo(_path));
    }

    // ── P3: WebBindAddress null/blank normalization on load ───────────────────

    [Theory]
    [InlineData("""{"WebBindAddress": null}""")]
    [InlineData("""{"WebBindAddress": ""}""")]
    [InlineData("""{"WebBindAddress": "   "}""")]
    [InlineData("""{}""")]
    public void LoadFrom_normalizes_missing_or_blank_WebBindAddress_to_loopback(string json)
    {
        File.WriteAllText(_path, json);
        var settings = AppSettings.LoadFrom(_path);
        Assert.Equal("127.0.0.1", settings.WebBindAddress);
    }

    [Theory]
    [InlineData("""{"WebBindAddress": "*"}""")]
    [InlineData("""{"WebBindAddress": "+"}""")]
    [InlineData("""{"WebBindAddress": "myhostname"}""")]
    [InlineData("""{"WebBindAddress": "http://127.0.0.1:5000"}""")]
    public void LoadFrom_normalizes_invalid_WebBindAddress_to_loopback(string json)
    {
        File.WriteAllText(_path, json);
        var settings = AppSettings.LoadFrom(_path);
        Assert.Equal("127.0.0.1", settings.WebBindAddress);
    }

    [Fact]
    public void LoadFrom_preserves_explicit_WebBindAddress()
    {
        File.WriteAllText(_path, """{"WebBindAddress": "0.0.0.0"}""");
        var settings = AppSettings.LoadFrom(_path);
        Assert.Equal("0.0.0.0", settings.WebBindAddress);
    }

    // ── P3-03: AllowLegacyWebSocketQueryStringAuth migration-safe default ─────

    [Fact]
    public void LoadFrom_defaults_legacy_websocket_query_auth_to_false_for_a_fresh_install()
    {
        // _path was never written to - this is a genuinely fresh install.
        var settings = AppSettings.LoadFrom(_path);
        Assert.False(settings.AllowLegacyWebSocketQueryStringAuth);
    }

    [Fact]
    public void LoadFrom_defaults_legacy_websocket_query_auth_to_true_for_an_existing_file_missing_the_field()
    {
        // Simulates upgrading from a version of WindowsGSH that predates this field.
        File.WriteAllText(_path, """{"Theme":"Dark"}""");
        var settings = AppSettings.LoadFrom(_path);
        Assert.True(settings.AllowLegacyWebSocketQueryStringAuth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadFrom_preserves_explicit_legacy_websocket_query_auth_value(bool value)
    {
        File.WriteAllText(_path, $$"""{"AllowLegacyWebSocketQueryStringAuth": {{(value ? "true" : "false")}}}""");
        var settings = AppSettings.LoadFrom(_path);
        Assert.Equal(value, settings.AllowLegacyWebSocketQueryStringAuth);
    }

    [Fact]
    public void LoadFrom_bounds_old_recovery_files()
    {
        for (var index = 0; index < 5; index++)
        {
            var recovery = Path.Combine(_root, $"WindowsGSH.settings.corrupt-20260101-00000{index}-x.json");
            File.WriteAllText(recovery, "{}");
            File.SetLastWriteTimeUtc(recovery, DateTime.UtcNow.AddMinutes(-index));
        }

        _ = AppSettings.LoadFrom(_path, recoveryRetention: 2);

        Assert.Equal(2, Directory.EnumerateFiles(_root, "WindowsGSH.settings.corrupt-*.json").Count());
    }

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
