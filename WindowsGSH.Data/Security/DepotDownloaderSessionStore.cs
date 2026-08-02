using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Data.Security;

public sealed class DepotDownloaderSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSH.DepotDownloaderSession.v1");
    private readonly string _statePath;
    private readonly string _accountConfigPath;
    private bool _restoreFailureReported;

    public DepotDownloaderSessionStore(string accountConfigPath)
        : this(AppPaths.GetPath("WindowsGSH.depotdownloader.session.json"), accountConfigPath)
    {
    }

    internal DepotDownloaderSessionStore(string statePath, string accountConfigPath)
    {
        _statePath = Path.GetFullPath(statePath);
        _accountConfigPath = ValidateConfiguredAccountConfigPath(accountConfigPath);
    }

    public bool HasProtectedSession => TryLoadState() != null;

    public bool Restore()
    {
        var state = TryLoadState();
        if (state == null)
        {
            return false;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(state.ProtectedAccountConfig);
            var accountConfig = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_accountConfigPath)!);
            File.WriteAllBytes(_accountConfigPath, accountConfig);
            _restoreFailureReported = false;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or FormatException or
            CryptographicException or InvalidOperationException)
        {
            if (!_restoreFailureReported)
            {
                LocalStateRecoveryStatus.Report(
                    "Authenticated Steam session",
                    "The remembered Steam session could not be restored. A new Steam Guard approval may be required; the original encrypted state was left unchanged.",
                    _statePath,
                    isFailure: true);
                _restoreFailureReported = true;
            }

            return false;
        }
    }

    public bool CaptureAndProtect()
    {
        if (!File.Exists(_accountConfigPath))
        {
            return false;
        }

        var accountConfig = File.ReadAllBytes(_accountConfigPath);
        var protectedBytes = ProtectedData.Protect(accountConfig, Entropy, DataProtectionScope.CurrentUser);
        var state = new StoredDepotDownloaderSession(
            _accountConfigPath,
            Convert.ToBase64String(protectedBytes),
            DateTimeOffset.UtcNow);

        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        AtomicFile.WriteAllText(
            _statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Delete(_accountConfigPath);
        return true;
    }

    public void Clear()
    {
        if (File.Exists(_accountConfigPath))
        {
            File.Delete(_accountConfigPath);
        }

        if (File.Exists(_statePath))
        {
            File.Delete(_statePath);
        }
    }

    private StoredDepotDownloaderSession? TryLoadState()
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<StoredDepotDownloaderSession>(File.ReadAllText(_statePath));
            if (state == null ||
                string.IsNullOrWhiteSpace(state.AccountConfigPath) ||
                string.IsNullOrWhiteSpace(state.ProtectedAccountConfig))
            {
                return null;
            }

            if (!string.Equals(
                    Path.GetFullPath(state.AccountConfigPath),
                    _accountConfigPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return state;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            FormatException or CryptographicException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ValidateConfiguredAccountConfigPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(fullPath), "account.config", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DepotDownloader session storage must target its account.config file.");
        }

        return fullPath;
    }

    private sealed record StoredDepotDownloaderSession(
        string AccountConfigPath,
        string ProtectedAccountConfig,
        DateTimeOffset UpdatedUtc);
}
