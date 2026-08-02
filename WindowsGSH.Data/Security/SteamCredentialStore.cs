using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Steam;

namespace WindowsGSH.Data.Security;

public sealed class SteamCredentialStore : ISteamCredentialProvider
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.SteamCredentials.v1");
    private readonly string _credentialsPath;
    private bool _decryptionFailureReported;

    public SteamCredentialStore()
    {
        _credentialsPath = AppPaths.GetPath("WindowsGSH.steam.credentials.json");
    }

    internal SteamCredentialStore(string pathOverride)
    {
        _credentialsPath = pathOverride;
    }

    private string CredentialsPath => _credentialsPath;

    public bool HasCredentials => File.Exists(CredentialsPath);

    public SteamCredentials? Load()
    {
        if (!File.Exists(CredentialsPath))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSteamCredentials>(File.ReadAllText(CredentialsPath));
            if (stored == null || string.IsNullOrWhiteSpace(stored.Username) || string.IsNullOrWhiteSpace(stored.ProtectedPassword))
            {
                return null;
            }

            var protectedBytes = Convert.FromBase64String(stored.ProtectedPassword);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            _decryptionFailureReported = false;
            return new SteamCredentials(stored.Username, Encoding.UTF8.GetString(plainBytes));
        }
        catch
        {
            if (!_decryptionFailureReported)
            {
                LocalStateRecoveryStatus.Report(
                    "Steam credentials",
                    "Steam credentials could not be decrypted for the current Windows user. Re-enter the Steam username and password in WindowsGSH Settings.",
                    CredentialsPath,
                    isFailure: true);
                _decryptionFailureReported = true;
            }

            return null;
        }
    }

    public string? LoadUsername()
    {
        if (!File.Exists(CredentialsPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredSteamCredentials>(File.ReadAllText(CredentialsPath))?.Username;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Steam username is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Steam password is required.");
        }

        var plainBytes = Encoding.UTF8.GetBytes(password);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var stored = new StoredSteamCredentials(
            username.Trim(),
            Convert.ToBase64String(protectedBytes),
            DateTimeOffset.UtcNow);

        AtomicFile.WriteAllText(CredentialsPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        if (File.Exists(CredentialsPath))
        {
            File.Delete(CredentialsPath);
        }
    }

    private sealed record StoredSteamCredentials(
        string Username,
        string ProtectedPassword,
        DateTimeOffset UpdatedUtc);
}
