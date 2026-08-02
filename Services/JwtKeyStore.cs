using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core;

namespace WindowsGSH.Services;

/// <summary>
/// Stores the DPAPI-protected JWT signing key in a dedicated file
/// (WindowsGSH.jwt.key) instead of the general-purpose settings JSON.
/// Includes a one-time migration path for users upgrading from the older layout.
/// </summary>
internal static class JwtKeyStore
{
    private static string DefaultKeyPath => AppPaths.GetPath("WindowsGSH.jwt.key");
    private static string DefaultLegacySettingsPath => AppPaths.GetPath("WindowsGSH.settings.json");

    /// <summary>
    /// Performs the one-time migration of the legacy JwtSigningKeyProtected field from
    /// WindowsGSH.settings.json to the dedicated key file, if not already done.
    /// Must be called before any code path that saves AppSettings, because [JsonIgnore]
    /// means a settings save will silently drop the legacy field before Load() can read it.
    /// </summary>
    internal static void MigrateIfNeeded(string? keyPath = null, string? legacySettingsPath = null)
    {
        keyPath ??= DefaultKeyPath;
        if (!File.Exists(keyPath))
            TryMigrateFromLegacySettings(keyPath, legacySettingsPath ?? DefaultLegacySettingsPath);
    }

    /// <summary>
    /// Returns the stored protected key (base64 string), or <see langword="null"/> if none exists.
    /// On first call after an upgrade, migrates the key from the old settings JSON automatically.
    /// </summary>
    internal static string? Load(string? keyPath = null, string? legacySettingsPath = null)
    {
        keyPath ??= DefaultKeyPath;

        if (File.Exists(keyPath))
        {
            try
            {
                var text = File.ReadAllText(keyPath).Trim();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Key file unreadable — fall through to migration attempt.
            }
        }

        return TryMigrateFromLegacySettings(keyPath, legacySettingsPath ?? DefaultLegacySettingsPath);
    }

    /// <summary>
    /// Writes the protected key (base64 string) to the dedicated key file.
    /// Uses a temp-file + rename to avoid partial writes.
    /// </summary>
    internal static void Save(string protectedBase64, string? keyPath = null)
    {
        keyPath ??= DefaultKeyPath;
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = keyPath + ".tmp";
        File.WriteAllText(tmp, protectedBase64);
        File.Move(tmp, keyPath, overwrite: true);
    }

    // Reads the JwtSigningKeyProtected field directly from the raw JSON so the
    // migration works even after [JsonIgnore] prevents AppSettings from reading it.
    private static string? TryMigrateFromLegacySettings(string keyPath, string settingsPath)
    {
        if (!File.Exists(settingsPath))
            return null;

        try
        {
            var json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("JwtSigningKeyProtected", out var el))
                return null;

            // GetString() throws InvalidOperationException on non-string JSON values
            // (number, object, array, bool). Only proceed when the value is a string.
            if (el.ValueKind != JsonValueKind.String)
                return null;

            var value = el.GetString();
            if (string.IsNullOrEmpty(value))
                return null;

            // Write to dedicated file so subsequent loads bypass migration.
            Save(value, keyPath);

            // Scrub the field from settings JSON now that the key is safely in the key file.
            // Best-effort: if this fails, the field will be absent on the next AppSettings.Save()
            // because JwtSigningKeyProtected is [JsonIgnore] and won't be re-written.
            TryScrubFromSettingsJson(settingsPath, json);

            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryScrubFromSettingsJson(string settingsPath, string currentJson)
    {
        try
        {
            var node = JsonNode.Parse(currentJson)?.AsObject();
            if (node == null || !node.ContainsKey("JwtSigningKeyProtected"))
                return;

            node.Remove("JwtSigningKeyProtected");

            var tmp = settingsPath + ".migrating";
            File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Scrub failed — acceptable. The field will disappear on the next settings save
            // because AppSettings no longer writes it ([JsonIgnore]).
        }
    }
}
