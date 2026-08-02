using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using WindowsGSH.Core.Security;

namespace WindowsGSH.Core.Java;

public sealed class ManagedJavaInstallService
{
    // 60 s covers the initial connection + server response headers.
    // After headers are received, the stream read loop uses its own stall-detection CTS.
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _runtimesRoot;
    private readonly HttpClient _httpClient;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string, string> _moveDirectory;

    public ManagedJavaInstallService()
        : this(AppPaths.GetPath("runtimes"), SharedClient, File.Exists)
    {
    }

    internal ManagedJavaInstallService(
        string runtimesRoot,
        HttpClient httpClient,
        Func<string, bool> fileExists,
        Action<string, string>? moveDirectory = null)
    {
        _runtimesRoot = runtimesRoot;
        _httpClient = httpClient;
        _fileExists = fileExists;
        _moveDirectory = moveDirectory ?? Directory.Move;
    }

    public string GetInstallDirectory(ManagedJavaRelease release)
    {
        var fullRoot = Path.GetFullPath(_runtimesRoot);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, release.InstallSubPath));

        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Runtime install path for '{release.Id}' resolves outside the runtimes root. " +
                "The catalogue entry may be tampered.");
        }

        return candidate;
    }

    public bool IsInstalled(ManagedJavaRelease release)
    {
        return _fileExists(GetJavaExecutablePath(release));
    }

    public string GetJavaExecutablePath(ManagedJavaRelease release)
    {
        var installDir = GetInstallDirectory(release);
        return FindJavaExecutable(installDir);
    }

    public async Task InstallAsync(
        ManagedJavaRelease release,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.ArchiveSha256))
        {
            throw new InvalidOperationException(
                $"No SHA-256 checksum is configured for {release.Vendor} Java {release.MajorVersion}. " +
                "The release catalogue must be updated with verified checksums before installing.");
        }

        var installDir = GetInstallDirectory(release);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"WindowsGSH.java.{Guid.NewGuid():N}.zip");

        progress?.Report($"Downloading {release.Vendor} Java {release.MajorVersion} ({release.ReleaseVersion})...");
        progress?.Report($"Source: {release.SourceUrl}");
        progress?.Report($"License: {release.LicenseUrl}");

        // stagingDir is set until staging has been moved to installDir.
        // backupDir is set until the old install has been cleaned up or confirmed restored.
        // rollbackFailed is set when backup restoration was attempted but failed — the backup
        // must never be deleted in that case, as it is the user's only copy.
        string? stagingDir = installDir + $".staging.{Guid.NewGuid():N}";
        string? backupDir = null;
        bool rollbackFailed = false;

        try
        {
            await DownloadWithLimitAsync(release, tempPath, progress, cancellationToken).ConfigureAwait(false);
            VerifyChecksum(tempPath, release.ArchiveSha256, release);
            progress?.Report("Checksum verified.");

            // Extract and validate into staging — original install is untouched until swap.
            ExtractArchiveSafely(tempPath, stagingDir, progress);

            var stagedJavaExe = FindJavaExecutable(stagingDir);
            if (!File.Exists(stagedJavaExe))
            {
                throw new InvalidOperationException(
                    $"Extraction completed but java.exe was not found in the staged directory. " +
                    $"Checked: {stagedJavaExe}");
            }

            // Atomic swap: move old → backup, then staging → install directory.
            if (Directory.Exists(installDir))
            {
                backupDir = installDir + $".old.{Guid.NewGuid():N}";
                _moveDirectory(installDir, backupDir);
            }

            _moveDirectory(stagingDir, installDir);
            stagingDir = null; // ownership transferred — do not delete in finally

            // Backup cleanup is non-critical.
            if (backupDir != null)
            {
                try { Directory.Delete(backupDir, recursive: true); } catch { }
                backupDir = null;
            }

            progress?.Report($"Installed: {GetJavaExecutablePath(release)}");
        }
        catch (Exception originalException)
        {
            // The swap did not complete (stagingDir != null), so restore the backup if present.
            if (stagingDir != null && backupDir != null &&
                Directory.Exists(backupDir) && !Directory.Exists(installDir))
            {
                try
                {
                    _moveDirectory(backupDir, installDir);
                    backupDir = null;
                }
                catch
                {
                    // Rollback itself failed — the backup is the user's only copy of the runtime.
                    // Record this so finally does not delete it, then surface both failures.
                    rollbackFailed = true;
                    throw new InvalidOperationException(
                        $"Install failed and the automatic rollback also failed. " +
                        $"Your previous runtime is preserved at: {backupDir}. " +
                        $"To restore manually, rename that folder to: {installDir}",
                        originalException);
                }
            }

            throw;
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }

            if (stagingDir != null && Directory.Exists(stagingDir))
                try { Directory.Delete(stagingDir, recursive: true); } catch { }

            // If rollback failed the backup is the user's only copy — never delete it.
            if (!rollbackFailed && backupDir != null && Directory.Exists(backupDir))
                try { Directory.Delete(backupDir, recursive: true); } catch { }
        }
    }

    public Task RepairAsync(
        ManagedJavaRelease release,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"Repairing {release.Vendor} Java {release.MajorVersion}...");
        return InstallAsync(release, progress, cancellationToken);
    }

    public void Remove(ManagedJavaRelease release)
    {
        var installDir = GetInstallDirectory(release);
        if (Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
        }
    }

    private async Task DownloadWithLimitAsync(
        ManagedJavaRelease release,
        string targetPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        // Cancel if no bytes arrive for 60 seconds (stalled connection).
        // CancelAfter is reset on each chunk so only a genuine stall triggers it.
        using var stallCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stallCts.Token);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await responseStream.ReadAsync(buffer, combinedCts.Token).ConfigureAwait(false)) > 0)
        {
            stallCts.CancelAfter(TimeSpan.FromSeconds(60)); // reset stall timer after each chunk

            totalRead += bytesRead;

            if (totalRead > release.MaxArchiveSizeBytes)
            {
                throw new InvalidDataException(
                    $"Download exceeded the maximum allowed size of {release.MaxArchiveSizeBytes / (1024 * 1024)} MB. " +
                    "The archive may be corrupt or the URL may be incorrect.");
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), combinedCts.Token).ConfigureAwait(false);

            if (totalRead % (10 * 1024 * 1024) < bytesRead)
            {
                progress?.Report($"Downloaded {totalRead / (1024 * 1024)} MB...");
            }
        }
    }

    private static void VerifyChecksum(string filePath, string expectedSha256, ManagedJavaRelease release)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        var actual = Convert.ToHexString(hash);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 checksum mismatch for {release.Vendor} Java {release.MajorVersion} ({release.ReleaseVersion}). " +
                $"Expected: {expectedSha256}. Got: {actual}. The download may be corrupt.");
        }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destinationDirectory)
    {
        ExtractArchiveSafely(archivePath, destinationDirectory, progress: null);
    }

    internal static void ExtractArchiveSafely(string archivePath, string destinationDirectory, IProgress<string>? progress)
    {
        var fullDestination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestination);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            ArchiveExtractionPath.RejectLink(entry);
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            // SECURITY: Resolve rejects rooted/traversal names, proves canonical
            // containment, and rejects existing reparse-point components.
            var fullEntryPath = ArchiveExtractionPath.Resolve(fullDestination, entry.FullName, allowRoot: isDirectory);
            var containmentRoot = Path.TrimEndingDirectorySeparator(fullDestination) + Path.DirectorySeparatorChar;
            if (!isDirectory && !fullEntryPath.StartsWith(containmentRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Java archive entry escaped the destination: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(fullEntryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullEntryPath)!);
            entry.ExtractToFile(fullEntryPath, overwrite: true);
        }

        progress?.Report($"Extracted to {fullDestination}.");
    }

    private static string FindJavaExecutable(string directory)
    {
        var direct = Path.Combine(directory, "bin", "java.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(directory))
        {
            return direct;
        }

        const int maximumDepth = 3;
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((directory, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();
            if (depth >= maximumDepth)
            {
                continue;
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(current))
            {
                var candidate = Path.Combine(subDirectory, "bin", "java.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                pending.Enqueue((subDirectory, depth + 1));
            }
        }

        return direct;
    }
}
