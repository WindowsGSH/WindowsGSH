using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ServerSecretStore
{
    private const string ReferencePrefix = "dpapi://server-secret/";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.ServerSecrets.v1");
    private const string StoreFileName = "server-secrets.json";

    public bool ProtectConfigSecrets(IGameServerModule module, string serverFolder, JsonObject config)
    {
        var secrets = Load(serverFolder);
        var changed = false;
        var settings = config["settings"] as JsonObject;
        changed |= ProtectFields(settings, module.GetConfigFields(), "settings", secrets);

        if (config["addons"] is JsonObject addons)
        {
            foreach (var addon in module.GetAddonDefinitions())
            {
                var addonSettings = addons[addon.Id]?["settings"] as JsonObject;
                changed |= ProtectFields(addonSettings, addon.ConfigFields, $"addons.{addon.Id}.settings", secrets);
            }
        }

        if (changed)
        {
            Save(serverFolder, secrets);
        }

        return changed;
    }

    public IReadOnlyDictionary<string, object?> ResolveSettings(
        string serverFolder,
        IReadOnlyDictionary<string, object?> settings)
    {
        var secrets = Load(serverFolder);
        return settings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is string value && IsReference(value)
                ? ResolveReference(value, expectedKey: null, secrets)
                : pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public void ResolveConfigSecrets(IGameServerModule module, string serverFolder, JsonObject config)
    {
        var secrets = Load(serverFolder);
        ResolveFields(config["settings"] as JsonObject, module.GetConfigFields(), "settings", secrets);
        if (config["addons"] is not JsonObject addons)
        {
            return;
        }

        foreach (var addon in module.GetAddonDefinitions())
        {
            ResolveFields(
                addons[addon.Id]?["settings"] as JsonObject,
                addon.ConfigFields,
                $"addons.{addon.Id}.settings",
                secrets);
        }
    }

    public static bool IsReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(ReferencePrefix, StringComparison.Ordinal);

    private static bool ProtectFields(
        JsonObject? settings,
        IReadOnlyList<ConfigFieldDefinition> fields,
        string scope,
        IDictionary<string, string> secrets)
    {
        if (settings == null)
        {
            return false;
        }

        var changed = false;
        foreach (var field in fields.Where(field => field.Type == ConfigFieldType.Password))
        {
            if (!settings.TryGetPropertyValue(field.Key, out var node) ||
                node is not JsonValue value ||
                !value.TryGetValue<string>(out var plaintext))
            {
                continue;
            }

            if (TryParseReference(plaintext, out _))
            {
                continue;
            }

            var secretKey = $"{scope}.{field.Key}";
            if (string.IsNullOrEmpty(plaintext))
            {
                changed |= secrets.Remove(secretKey);
                continue;
            }

            secrets[secretKey] = plaintext;
            settings[field.Key] = CreateReference(secretKey);
            changed = true;
        }

        return changed;
    }

    private static void ResolveFields(
        JsonObject? settings,
        IReadOnlyList<ConfigFieldDefinition> fields,
        string scope,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (settings == null)
        {
            return;
        }

        foreach (var field in fields.Where(field => field.Type == ConfigFieldType.Password))
        {
            if (settings[field.Key] is not JsonValue value ||
                !value.TryGetValue<string>(out var reference) ||
                !IsReference(reference))
            {
                continue;
            }

            var expectedKey = $"{scope}.{field.Key}";
            settings[field.Key] = ResolveReference(reference, expectedKey, secrets);
        }
    }

    private static string ResolveReference(
        string reference,
        string? expectedKey,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (!TryParseReference(reference, out var secretKey))
        {
            throw new InvalidOperationException(
                "A protected server secret reference is malformed. Re-enter the protected setting and save the server configuration.");
        }

        if (!string.IsNullOrWhiteSpace(expectedKey) &&
            !string.Equals(secretKey, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Protected server secret reference '{secretKey}' does not match the expected setting '{expectedKey}'. " +
                "Re-enter the protected setting and save the server configuration.");
        }

        if (!secrets.TryGetValue(secretKey, out var secret))
        {
            throw new InvalidOperationException(
                $"Protected server secret '{secretKey}' is unavailable. Restore server-secrets.json for this server " +
                "under the same Windows user account, or re-enter the protected setting.");
        }

        return secret;
    }

    private static string CreateReference(string secretKey) =>
        ReferencePrefix + Uri.EscapeDataString(secretKey);

    private static bool TryParseReference(string value, out string secretKey)
    {
        if (value.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            secretKey = Uri.UnescapeDataString(value[ReferencePrefix.Length..]);
            return !string.IsNullOrWhiteSpace(secretKey);
        }

        secretKey = string.Empty;
        return false;
    }

    private static Dictionary<string, string> Load(string serverFolder)
    {
        try
        {
            var path = Path.Combine(serverFolder, StoreFileName);
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var stored = JsonSerializer.Deserialize<StoredSecrets>(File.ReadAllText(path));
            if (stored == null || string.IsNullOrWhiteSpace(stored.ProtectedData))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var encrypted = Convert.FromBase64String(stored.ProtectedData);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext);
            return values == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Server secrets could not be decrypted for the current Windows user.", ex);
        }
    }

    private static void Save(string serverFolder, IReadOnlyDictionary<string, string> secrets)
    {
        Directory.CreateDirectory(serverFolder);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets);
        var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var stored = new StoredSecrets(Convert.ToBase64String(encrypted), DateTimeOffset.UtcNow);
        AtomicFile.WriteAllText(
            Path.Combine(serverFolder, StoreFileName),
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record StoredSecrets(string ProtectedData, DateTimeOffset UpdatedUtc);
}
