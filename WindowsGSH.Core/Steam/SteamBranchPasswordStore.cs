using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Steam;

public sealed class SteamBranchPasswordStore
{
    private const string ReferencePrefix = "windows-dpapi:server:";
    private const string ReferenceSuffix = ":steamBranchPassword";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.SteamBranchPasswords.v1");
    private static readonly string StoreMutexName = CreateStoreMutexName();
    internal static string StorePath => AppPaths.GetPath("WindowsGSH.steam.branch-passwords.json");

    private readonly HashSet<string> _decryptionFailureReported = new(StringComparer.Ordinal);
    private bool _storeReadFailureReported;

    public static string CreateReference(string serverId) => $"{ReferencePrefix}{serverId}{ReferenceSuffix}";

    public bool IsReference(string value)
    {
        return value.StartsWith(ReferencePrefix, StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith(ReferenceSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public void Save(string serverId, string password)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("Server id is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        WithStoreLock(() =>
        {
            var store = ReadStore();
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
            store.Passwords[serverId] = new StoredSteamBranchPassword(
                Convert.ToBase64String(protectedBytes),
                DateTimeOffset.UtcNow);
            WriteStore(store);
        });
    }

    public string Load(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return string.Empty;
        }

        try
        {
            return WithStoreLock(() =>
            {
                StoredSteamBranchPasswordStore store;
                try
                {
                    store = ReadStore();
                }
                catch
                {
                    if (!_storeReadFailureReported)
                    {
                        LocalStateRecoveryStatus.Report(
                            "Steam branch password",
                            "The Steam branch-password store could not be read. Branch passwords may need to be re-entered.",
                            StorePath,
                            isFailure: true);
                        _storeReadFailureReported = true;
                    }

                    return string.Empty;
                }

                if (!store.Passwords.TryGetValue(serverId, out var stored) ||
                    string.IsNullOrWhiteSpace(stored.ProtectedPassword))
                {
                    return string.Empty;
                }

                try
                {
                    var protectedBytes = Convert.FromBase64String(stored.ProtectedPassword);
                    var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plainBytes);
                }
                catch
                {
                    if (_decryptionFailureReported.Add(serverId))
                    {
                        LocalStateRecoveryStatus.Report(
                            "Steam branch password",
                            $"The Steam branch password for server '{serverId}' could not be decrypted for the current Windows user. Re-enter the branch password in the server configuration.",
                            StorePath,
                            isFailure: true);
                    }

                    return string.Empty;
                }
            });
        }
        catch
        {
            return string.Empty;
        }
    }

    public string Resolve(string serverId, string branchPassword, string branchPasswordReference)
    {
        if (!string.IsNullOrWhiteSpace(branchPassword))
        {
            return branchPassword;
        }

        return IsReference(branchPasswordReference) ? Load(serverId) : string.Empty;
    }

    public void Clear(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return;
        }

        WithStoreLock(() =>
        {
            if (!File.Exists(StorePath))
            {
                return;
            }

            StoredSteamBranchPasswordStore store;
            try
            {
                store = ReadStore();
            }
            catch
            {
                // File exists but cannot be parsed — skip removal to preserve it intact.
                return;
            }

            if (store.Passwords.Remove(serverId))
            {
                WriteStore(store);
            }
        });
    }

    private static StoredSteamBranchPasswordStore ReadStore()
    {
        if (!File.Exists(StorePath))
        {
            return new StoredSteamBranchPasswordStore([]);
        }

        // If the file exists but cannot be parsed, let the exception propagate so
        // Save() does not silently overwrite all stored passwords on a read failure.
        return JsonSerializer.Deserialize<StoredSteamBranchPasswordStore>(File.ReadAllText(StorePath)) ??
            new StoredSteamBranchPasswordStore([]);
    }

    private static void WriteStore(StoredSteamBranchPasswordStore store)
    {
        var contents = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                AtomicFile.WriteAllText(StorePath, contents);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }

        throw new IOException(
            "Steam branch-password storage remained locked after repeated atomic write attempts.",
            lastError);
    }

    private static void WithStoreLock(Action action)
    {
        WithStoreLock(() =>
        {
            action();
            return true;
        });
    }

    private static T WithStoreLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, StoreMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Timed out waiting for Steam branch-password storage.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string CreateStoreMutexName()
    {
        var key = $"{AppPaths.GetPath("WindowsGSH.steam.branch-passwords.json")}|{Environment.UserName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant())));
        return $@"Local\WindowsGSH-SteamBranchPasswords-{hash[..24]}";
    }

    private sealed record StoredSteamBranchPasswordStore(
        Dictionary<string, StoredSteamBranchPassword> Passwords);

    private sealed record StoredSteamBranchPassword(
        string ProtectedPassword,
        DateTimeOffset UpdatedUtc);
}
