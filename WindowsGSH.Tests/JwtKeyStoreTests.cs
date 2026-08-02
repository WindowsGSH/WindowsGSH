using System.Text.Json;
using WindowsGSH.Services;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class JwtKeyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests.JwtKeyStore",
        Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_root, "WindowsGSH.jwt.key");
    private string LegacySettingsPath => Path.Combine(_root, "WindowsGSH.settings.json");

    public JwtKeyStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Load_returns_null_when_no_key_file_exists()
    {
        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);
        Assert.Null(result);
    }

    [Fact]
    public void Save_and_Load_round_trip()
    {
        JwtKeyStore.Save("AAABBBCCC", KeyPath);
        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);
        Assert.Equal("AAABBBCCC", result);
    }

    [Fact]
    public void Load_migrates_key_from_legacy_settings_json()
    {
        File.WriteAllText(LegacySettingsPath, """{"JwtSigningKeyProtected":"LEGACYKEY=="}""");

        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        Assert.Equal("LEGACYKEY==", result);
        // Dedicated file created so next load does not re-read the settings JSON.
        Assert.True(File.Exists(KeyPath));
        Assert.Equal("LEGACYKEY==", File.ReadAllText(KeyPath).Trim());
    }

    [Fact]
    public void Load_prefers_dedicated_file_over_legacy_settings()
    {
        JwtKeyStore.Save("NEWKEY==", KeyPath);
        File.WriteAllText(LegacySettingsPath, """{"JwtSigningKeyProtected":"OLDKEY=="}""");

        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        Assert.Equal("NEWKEY==", result);
    }

    [Fact]
    public void Load_returns_null_when_legacy_settings_lack_the_field()
    {
        File.WriteAllText(LegacySettingsPath, """{"Theme":"Dark"}""");

        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        Assert.Null(result);
    }

    [Fact]
    public void Load_returns_null_when_legacy_field_is_empty()
    {
        File.WriteAllText(LegacySettingsPath, """{"JwtSigningKeyProtected":""}""");

        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        Assert.Null(result);
    }

    // ── P2: migration scrubs the field from settings JSON ─────────────────────

    [Fact]
    public void Load_migration_scrubs_field_from_legacy_settings_json()
    {
        File.WriteAllText(LegacySettingsPath, """{"Theme":"Dark","JwtSigningKeyProtected":"LEGACYKEY=="}""");

        JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        var json = File.ReadAllText(LegacySettingsPath);
        Assert.DoesNotContain("JwtSigningKeyProtected", json, StringComparison.OrdinalIgnoreCase);
        // Other settings must be preserved.
        Assert.Contains("Dark", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_migration_leaves_settings_valid_json_after_scrub()
    {
        File.WriteAllText(LegacySettingsPath, """{"Theme":"Dark","JwtSigningKeyProtected":"LEGACYKEY==","WebPort":5000}""");

        JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        var json = File.ReadAllText(LegacySettingsPath);
        var settings = AppSettings.LoadFrom(Path.Combine(_root, "WindowsGSH.settings.json"));
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(5000, settings.WebPort);
    }

    // ── P3: non-string legacy field must not throw ────────────────────────────

    [Theory]
    [InlineData("""{"JwtSigningKeyProtected": 123}""")]
    [InlineData("""{"JwtSigningKeyProtected": {}}""")]
    [InlineData("""{"JwtSigningKeyProtected": []}""")]
    [InlineData("""{"JwtSigningKeyProtected": true}""")]
    public void Load_returns_null_for_non_string_legacy_field(string json)
    {
        File.WriteAllText(LegacySettingsPath, json);

        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);

        Assert.Null(result);
    }

    // ── Startup-race fix: MigrateIfNeeded runs before any settings save ──────

    [Fact]
    public void MigrateIfNeeded_migrates_key_and_scrubs_before_any_settings_save()
    {
        // Simulates an upgraded install: legacy field present, no key file yet.
        File.WriteAllText(LegacySettingsPath, """{"Theme":"Dark","JwtSigningKeyProtected":"LEGACY=="}""");

        JwtKeyStore.MigrateIfNeeded(KeyPath, LegacySettingsPath);

        // Key file must exist immediately after MigrateIfNeeded.
        Assert.True(File.Exists(KeyPath));
        Assert.Equal("LEGACY==", File.ReadAllText(KeyPath).Trim());

        // Simulate a settings save (what MainWindow ctor does right after this).
        AppSettings.LoadFrom(LegacySettingsPath).SaveTo(LegacySettingsPath);

        // Load must still return the migrated key (not null / a new key).
        var result = JwtKeyStore.Load(KeyPath, LegacySettingsPath);
        Assert.Equal("LEGACY==", result);
    }

    [Fact]
    public void MigrateIfNeeded_is_idempotent_when_key_file_already_exists()
    {
        JwtKeyStore.Save("EXISTING==", KeyPath);
        File.WriteAllText(LegacySettingsPath, """{"JwtSigningKeyProtected":"DIFFERENT=="}""");

        JwtKeyStore.MigrateIfNeeded(KeyPath, LegacySettingsPath);

        // Existing key file must not be overwritten.
        Assert.Equal("EXISTING==", File.ReadAllText(KeyPath).Trim());
    }

    [Fact]
    public void Save_uses_atomic_write_and_leaves_no_temp_files()
    {
        JwtKeyStore.Save("TESTKEY==", KeyPath);

        Assert.Equal("TESTKEY==", File.ReadAllText(KeyPath).Trim());
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void JwtSigningKeyProtected_is_not_serialised_to_settings_json()
    {
        // After [JsonIgnore], the field must not appear in the serialised output
        // regardless of the value on the object.
        var settings = new AppSettings { JwtSigningKeyProtected = "should-not-appear" };
        var path = Path.Combine(_root, "check.json");
        settings.SaveTo(path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("JwtSigningKeyProtected", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("should-not-appear", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JwtSigningKeyProtected_is_not_read_back_from_settings_json()
    {
        // Even if an old JSON file contains the field, LoadFrom must not populate it
        // (the value travels to JwtKeyStore instead).
        var path = Path.Combine(_root, "old-settings.json");
        File.WriteAllText(path, """{"JwtSigningKeyProtected":"OLDKEY==","Theme":"Light"}""");

        var loaded = AppSettings.LoadFrom(path);

        Assert.Empty(loaded.JwtSigningKeyProtected);
        Assert.Equal("Light", loaded.Theme);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
