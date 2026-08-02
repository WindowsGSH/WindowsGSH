using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;

namespace WindowsGSH.Discord;

public sealed class DiscordWebhookStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.DiscordWebhooks.v1");
    private readonly string _storePath;
    private bool _lastLoadFailed;
    private bool _decryptionFailureReported;

    public DiscordWebhookStore()
    {
        _storePath = AppPaths.GetPath("WindowsGSH.discord.webhooks.json");
    }

    internal DiscordWebhookStore(string pathOverride)
    {
        _storePath = pathOverride;
    }

    private string StorePath => _storePath;

    public bool HasGlobalWebhook => !string.IsNullOrWhiteSpace(LoadGlobal());

    public bool HasServerWebhook(string serverId) => !string.IsNullOrWhiteSpace(LoadForServer(serverId));

    public string? LoadGlobal() => Load().GlobalWebhookUrl;

    public string? LoadForServer(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        return Load().ServerWebhookUrls.TryGetValue(serverId.Trim(), out var url) ? url : null;
    }

    public void SaveGlobal(string webhookUrl)
    {
        var store = Load();
        Save(store with { GlobalWebhookUrl = ValidateWebhookUrl(webhookUrl) });
    }

    public void SaveForServer(string serverId, string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("Server ID is required.");
        }

        var store = Load();
        store.ServerWebhookUrls[serverId.Trim()] = ValidateWebhookUrl(webhookUrl);
        Save(store);
    }

    public void ClearGlobal()
    {
        var store = Load();
        Save(store with { GlobalWebhookUrl = null });
    }

    public void ClearForServer(string serverId)
    {
        var store = Load();
        if (store.ServerWebhookUrls.Remove(serverId.Trim()))
        {
            Save(store);
        }
    }

    private static string ValidateWebhookUrl(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase) ||
            !(uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Enter a valid HTTPS Discord webhook URL.");
        }

        return uri.ToString();
    }

    private WebhookStoreData Load()
    {
        if (!File.Exists(StorePath))
        {
            _lastLoadFailed = false;
            return WebhookStoreData.Empty;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredWebhookData>(File.ReadAllText(StorePath));
            if (stored == null || string.IsNullOrWhiteSpace(stored.ProtectedData))
            {
                _lastLoadFailed = false;
                return WebhookStoreData.Empty;
            }

            var protectedBytes = Convert.FromBase64String(stored.ProtectedData);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            _lastLoadFailed = false;
            _decryptionFailureReported = false;
            return JsonSerializer.Deserialize<WebhookStoreData>(plainBytes) ?? WebhookStoreData.Empty;
        }
        catch
        {
            _lastLoadFailed = true;
            if (!_decryptionFailureReported)
            {
                LocalStateRecoveryStatus.Report(
                    "Discord webhooks",
                    "Discord webhook configuration could not be decrypted for the current Windows user. Re-enter your webhook URLs in Discord Settings.",
                    StorePath,
                    isFailure: true);
                _decryptionFailureReported = true;
            }

            return WebhookStoreData.Empty;
        }
    }

    private void Save(WebhookStoreData data)
    {
        if (_lastLoadFailed && File.Exists(StorePath))
        {
            // Back up the original encrypted file before overwriting it.
            // This preserves potentially restorable data if the DPAPI issue is resolved later.
            File.Copy(StorePath, StorePath + ".bak", overwrite: true);
            _lastLoadFailed = false;
        }

        if (string.IsNullOrWhiteSpace(data.GlobalWebhookUrl) && data.ServerWebhookUrls.Count == 0)
        {
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
            }

            return;
        }

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var stored = new StoredWebhookData(Convert.ToBase64String(protectedBytes), DateTimeOffset.UtcNow);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record StoredWebhookData(string ProtectedData, DateTimeOffset UpdatedUtc);

    private sealed record WebhookStoreData(string? GlobalWebhookUrl, Dictionary<string, string> ServerWebhookUrls)
    {
        public static WebhookStoreData Empty => new(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }
}
