using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Discord;

public sealed class DiscordBotTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.DiscordBotToken.v1");
    private readonly string _tokenPath;
    private bool _decryptionFailureReported;

    public DiscordBotTokenStore()
    {
        _tokenPath = AppPaths.GetPath("WindowsGSH.discord.token.json");
    }

    internal DiscordBotTokenStore(string pathOverride)
    {
        _tokenPath = pathOverride;
    }

    private string TokenPath => _tokenPath;

    public bool HasToken => File.Exists(TokenPath);

    public string? Load()
    {
        if (!File.Exists(TokenPath))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredDiscordToken>(File.ReadAllText(TokenPath));
            if (stored == null || string.IsNullOrWhiteSpace(stored.ProtectedToken))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(stored.ProtectedToken);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            _decryptionFailureReported = false;
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            if (!_decryptionFailureReported)
            {
                LocalStateRecoveryStatus.Report(
                    "Discord bot token",
                    "The Discord bot token could not be decrypted for the current Windows user. Re-enter the bot token in Discord Settings.",
                    TokenPath,
                    isFailure: true);
                _decryptionFailureReported = true;
            }

            return null;
        }
    }

    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Discord bot token is required.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(token.Trim());
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var stored = new StoredDiscordToken(Convert.ToBase64String(protectedBytes), DateTimeOffset.UtcNow);
        AtomicFile.WriteAllText(TokenPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        if (File.Exists(TokenPath))
        {
            File.Delete(TokenPath);
        }
    }

    private sealed record StoredDiscordToken(string ProtectedToken, DateTimeOffset UpdatedUtc);
}
