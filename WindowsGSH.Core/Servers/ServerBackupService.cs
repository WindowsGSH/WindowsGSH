using System.IO;
using System.IO.Compression;
using System.Text.Json;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Security;

namespace WindowsGSH.Core.Servers;

public sealed class ServerBackupService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> DestinationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IGameServerModule, string, bool> _isRunning;
    private readonly Func<string, Stream> _openBackupSource;
    private readonly Func<string, Stream> _createBackupStaging;

    public ServerBackupService(
        Func<DateTimeOffset>? utcNow = null,
        Func<IGameServerModule, string, bool>? isRunning = null,
        Func<string, Stream>? openBackupSource = null,
        Func<string, Stream>? createBackupStaging = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _isRunning = isRunning ?? ((module, installPath) => ServerProcessLocator.IsRunning(module, installPath));
        _openBackupSource = openBackupSource ?? (path => new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan));
        _createBackupStaging = createBackupStaging ?? (path => new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.SequentialScan));
    }

    public string CreateBackup(ServerInstance instance, IEnumerable<string> relativePaths, int retentionCount = 0)
    {
        return CreateBackupAsync(instance, null, relativePaths, retentionCount).GetAwaiter().GetResult().BackupPath
            ?? throw new InvalidOperationException("Backup was not created.");
    }

    public async Task<BackupResult> CreateBackupAsync(
        ServerInstance instance,
        IGameServerModule? module,
        IEnumerable<string>? relativePaths = null,
        int retentionCount = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backupsFolder = ResolveBackupFolder(instance);
        EnsureBackupFolderWritable(instance, backupsFolder);
        var requestedPaths = ResolveBackupPaths(module, relativePaths);

        var createdUtc = _utcNow();
        var destinationGate = DestinationGates.GetOrAdd(backupsFolder, _ => new SemaphoreSlim(1, 1));
        await destinationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = ReserveUniqueBackupPath(backupsFolder, createdUtc);
            BackupResult result;
            try
            {
                // Building the zip (CreateEntryFromFile per file, CompressionLevel.Optimal) is real CPU
                // and disk I/O, not something that should ever run inline on a caller's thread.
                result = await Task.Run(
                    () => BuildBackupArchive(instance, module, reservation.Path, reservation.Stream, requestedPaths, createdUtc, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                reservation.Stream.Dispose();
                TryDeletePartialBackup(reservation.Path);
                throw;
            }

            if (retentionCount > 0 && result.FailedPaths.Count > 0)
            {
                // An archive with unreadable files is useful as a partial snapshot, but it must not
                // evict the last complete restore point merely because it is newer.
                var warning = "Retention cleanup skipped because this backup contains unreadable files; previous backups were preserved.";
                result = result with
                {
                    Message = $"{result.Message} {warning}",
                    RetentionWarning = warning
                };
            }
            else
            {
                try
                {
                    PruneBackups(instance, retentionCount);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The archive is already finalized and valid. Retention failure must not turn it
                    // back into a "partial" file or erase the successful backup.
                    var warning = $"Retention cleanup failed: {ex.Message}";
                    result = result with
                    {
                        Message = $"{result.Message} {warning}",
                        RetentionWarning = warning
                    };
                }
            }

            return result;
        }
        finally
        {
            destinationGate.Release();
        }
    }

    private BackupResult BuildBackupArchive(
        ServerInstance instance,
        IGameServerModule? module,
        string backupPath,
        FileStream backupStream,
        IReadOnlyList<string> requestedPaths,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        var backedUp = new List<string>();
        var backedUpFiles = new List<string>();
        var backedUpFolders = new List<string>();
        var missing = new List<string>();
        var failed = new List<string>();
        using (backupStream)
        using (var archive = new ZipArchive(backupStream, ZipArchiveMode.Create))
        {
            foreach (var relativePath in requestedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.GetFullPath(Path.Combine(instance.InstallPath, relativePath));
                if (!IsInside(instance.InstallPath, sourcePath))
                {
                    missing.Add(relativePath);
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    if (TryAddFile(archive, sourcePath, relativePath, Path.GetDirectoryName(backupPath)!, failed))
                    {
                        backedUp.Add(relativePath);
                        backedUpFiles.Add(relativePath);
                    }

                    continue;
                }

                if (!Directory.Exists(sourcePath))
                {
                    missing.Add(relativePath);
                    continue;
                }

                var anyFileFound = false;
                var anyFileSucceeded = false;
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    anyFileFound = true;
                    var entryName = Path.GetRelativePath(instance.InstallPath, file).Replace('\\', '/');
                    if (TryAddFile(archive, file, entryName, Path.GetDirectoryName(backupPath)!, failed))
                    {
                        anyFileSucceeded = true;
                    }
                }

                if (!anyFileFound || anyFileSucceeded)
                {
                    backedUp.Add(relativePath);
                    backedUpFolders.Add(relativePath);
                }
                else
                {
                    // Every file this folder contained failed to read (e.g. the whole directory was
                    // held open by a running database) - nothing from it actually made it into the
                    // archive, so the folder itself must not be reported as backed up even though it
                    // was "processed." The individual failures are still visible via FailedPaths.
                    missing.Add(relativePath);
                }
            }

            AddManifest(archive, instance, module, backedUp, backedUpFiles, backedUpFolders, missing, failed, createdUtc);
        }

        var info = CreateBackupInfo(backupPath);
        return new BackupResult(
            Success: true,
            BackupPath: backupPath,
            Info: info,
            BackedUpPaths: backedUp,
            MissingPaths: missing,
            FailedPaths: failed,
            Message: failed.Count == 0
                ? $"Backup created: {Path.GetFileName(backupPath)}"
                : $"Backup created: {Path.GetFileName(backupPath)} ({failed.Count} file(s) could not be read and were skipped - the server may have had them open)");
    }

    public IReadOnlyList<string> ListBackups(ServerInstance instance)
    {
        var backupsFolder = ResolveBackupFolder(instance);
        if (!Directory.Exists(backupsFolder))
        {
            return [];
        }

        return Directory.EnumerateFiles(backupsFolder, "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
    }

    public IReadOnlyList<ServerBackupInfo> ListBackupInfo(ServerInstance instance)
    {
        return ListBackups(instance)
            .Select(CreateBackupInfo)
            .ToArray();
    }

    public void RestoreBackup(ServerInstance instance, string backupPath)
    {
        var result = RestoreBackupAsync(instance, backupPath).GetAwaiter().GetResult();
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    public Task<RestoreResult> RestoreBackupAsync(
        ServerInstance instance,
        string backupPath,
        IGameServerModule? module = null,
        bool confirmedWhileRunning = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (module != null &&
            !confirmedWhileRunning &&
            _isRunning(module, instance.InstallPath))
        {
            return Task.FromResult(new RestoreResult(
                false,
                backupPath,
                [],
                "Stop the server before restoring a backup."));
        }

        var resolvedBackupPath = Path.GetFullPath(backupPath);
        var backupsFolder = ResolveBackupFolder(instance);
        if (!IsInside(backupsFolder, resolvedBackupPath) || !File.Exists(resolvedBackupPath))
        {
            throw new InvalidOperationException("Backup file is not in this server's configured backup folder.");
        }

        var restored = new List<string>();
        using var archive = ZipFile.OpenRead(resolvedBackupPath);
        foreach (var entry in archive.Entries)
        {
            ArchiveExtractionPath.RejectLink(entry);
            if (!entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                !entry.FullName.EndsWith("\\", StringComparison.Ordinal) &&
                !string.Equals(entry.FullName, "backup-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                _ = ArchiveExtractionPath.Resolve(instance.InstallPath, entry.FullName);
            }
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveExtractionPath.RejectLink(entry);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
                string.Equals(entry.FullName, "backup-manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // SECURITY: Resolve rejects rooted/traversal names, proves canonical
            // containment, and rejects existing reparse-point components.
            var destinationPath = ArchiveExtractionPath.Resolve(instance.InstallPath, entry.FullName);
            var containmentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.InstallPath)) +
                Path.DirectorySeparatorChar;
            if (!destinationPath.StartsWith(containmentRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup archive entry escaped the server installation: {entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            restored.Add(entry.FullName);
        }

        return Task.FromResult(new RestoreResult(
            true,
            resolvedBackupPath,
            restored,
            $"Restored backup: {Path.GetFileName(resolvedBackupPath)}"));
    }

    public Task<DeleteBackupResult> DeleteBackupAsync(
        ServerInstance instance,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedBackupPath = Path.GetFullPath(backupPath);
        var backupsFolder = ResolveBackupFolder(instance);
        if (!IsInside(backupsFolder, resolvedBackupPath) || !File.Exists(resolvedBackupPath))
        {
            return Task.FromResult(new DeleteBackupResult(false, resolvedBackupPath, "Backup file is not in this server's configured backup folder."));
        }

        File.Delete(resolvedBackupPath);
        return Task.FromResult(new DeleteBackupResult(true, resolvedBackupPath, $"Deleted backup: {Path.GetFileName(resolvedBackupPath)}"));
    }

    public BackupPreview GetBackupPreview(ServerInstance instance, string backupPath)
    {
        var resolvedBackupPath = Path.GetFullPath(backupPath);
        var backupsFolder = ResolveBackupFolder(instance);
        if (!IsInside(backupsFolder, resolvedBackupPath) || !File.Exists(resolvedBackupPath))
        {
            throw new InvalidOperationException("Backup file is not in this server's configured backup folder.");
        }

        using var archive = ZipFile.OpenRead(resolvedBackupPath);
        var archiveFiles = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                !string.Equals(entry.FullName, "backup-manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.FullName)
            .OrderBy(path => path)
            .ToArray();
        var files = archiveFiles
            .Where(path => IsSafeRestoreTarget(instance.InstallPath, path))
            .ToArray();
        var skippedUnsafeFiles = archiveFiles.Length - files.Length;
        var existingFiles = files.Count(path => IsRestoreTargetExisting(instance.InstallPath, path));
        var newFiles = files.Length - existingFiles;
        var manifest = archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, "backup-manifest.json", StringComparison.OrdinalIgnoreCase));
        string? manifestText = null;
        IReadOnlyList<string> fileTargets = [];
        IReadOnlyList<string> folderTargets = [];
        IReadOnlyList<string> missingTargets = [];
        IReadOnlyList<string> failedTargets = [];
        if (manifest != null)
        {
            using var reader = new StreamReader(manifest.Open());
            manifestText = reader.ReadToEnd();
            try
            {
                using var document = JsonDocument.Parse(manifestText);
                fileTargets = ReadManifestStringArray(document.RootElement, "fileTargets");
                folderTargets = ReadManifestStringArray(document.RootElement, "folderTargets");
                missingTargets = ReadManifestStringArray(document.RootElement, "missingPaths");
                failedTargets = ReadManifestStringArray(document.RootElement, "failedPaths");
            }
            catch (JsonException)
            {
            }
        }

        return new BackupPreview(
            Path.GetFileName(resolvedBackupPath),
            new FileInfo(resolvedBackupPath).Length,
            files.Length,
            files,
            manifestText,
            fileTargets,
            folderTargets,
            existingFiles,
            newFiles,
            skippedUnsafeFiles,
            missingTargets,
            failedTargets);
    }

    public void PruneBackups(ServerInstance instance, int retentionCount)
    {
        if (retentionCount <= 0)
        {
            return;
        }

        foreach (var backupPath in ListBackups(instance).Skip(retentionCount))
        {
            File.Delete(backupPath);
        }
    }

    public string ResolveBackupFolder(ServerInstance instance)
    {
        var configured = instance.AppSettings.Backup.Destination?.Trim().Trim('"') ?? string.Empty;
        var folder = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(instance.ServerFolder, "backups")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(instance.ServerFolder, configured);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    public void ValidateBackupDestination(ServerInstance instance)
    {
        EnsureBackupFolderWritable(instance, ResolveBackupFolder(instance));
    }

    // Backups are allowed while the server is running (nothing gates this on stopped state), and a
    // running game commonly holds world/database files open exclusively. Previously the first
    // IOException here aborted the entire backup and left a corrupt partial zip behind - one locked
    // file is now recorded as failed instead, so the rest of the backup still completes.
    private bool TryAddFile(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        string stagingFolder,
        List<string> failed)
    {
        var stagingPath = Path.Combine(stagingFolder, $".windowsgsh-backup-stage-{Guid.NewGuid():N}.tmp");
        try
        {
            Stream source;
            try
            {
                source = _openBackupSource(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(entryName);
                return false;
            }

            using (source)
            using (var staging = _createBackupStaging(stagingPath))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = source.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed.Add(entryName);
                        return false;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    // Destination write failures deliberately escape. Treating disk-full or an
                    // unavailable backup share as an unreadable source would return false success.
                    staging.Write(buffer, 0, bytesRead);
                }
            }

            // Commit only after the source has been read completely. A failure while writing the
            // staged bytes to the ZIP is an archive-construction failure and deliberately escapes,
            // causing the outer operation to remove the whole partial archive.
            archive.CreateEntryFromFile(stagingPath, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            return true;
        }
        finally
        {
            TryDeleteStagingFile(stagingPath);
        }
    }

    private static void TryDeleteStagingFile(string stagingPath)
    {
        try
        {
            File.Delete(stagingPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeletePartialBackup(string backupPath)
    {
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            // Best-effort cleanup only; if this also fails there is nothing more this method can do
            // beyond leaving the partial file for the user or a support bundle to notice.
        }
    }

    private static void EnsureBackupFolderWritable(ServerInstance instance, string backupsFolder)
    {
        var installPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.InstallPath));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupsFolder));
        if (string.Equals(installPath, destination, StringComparison.OrdinalIgnoreCase) ||
            IsInside(installPath, destination))
        {
            throw new InvalidOperationException("Backup destination cannot be inside the server files folder.");
        }

        try
        {
            Directory.CreateDirectory(destination);
            var probePath = Path.Combine(destination, $".windowsgsh-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException($"Backup destination is not writable: {destination}. {ex.Message}", ex);
        }
    }

    private void AddManifest(
        ZipArchive archive,
        ServerInstance instance,
        IGameServerModule? module,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> fileTargets,
        IReadOnlyList<string> folderTargets,
        IReadOnlyList<string> missingPaths,
        IReadOnlyList<string> failedPaths,
        DateTimeOffset createdUtc)
    {
        var entry = archive.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, new
        {
            createdUtc,
            serverId = instance.Id,
            serverName = instance.Name,
            moduleId = instance.ModuleId,
            moduleVersion = module?.Version,
            paths,
            fileTargets,
            folderTargets,
            missingPaths,
            failedPaths
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<string> ResolveBackupPaths(IGameServerModule? module, IEnumerable<string>? relativePaths)
    {
        var customPaths = relativePaths?
            .Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
        var modulePaths = module?.Capabilities.SupportsBackups == true
            ? module.GetBackupTargets()
                .Select(target => NormalizeRelativePath(target.RelativePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
            : [];

        return modulePaths
            .Concat(customPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadManifestStringArray(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];
    }

    private static bool IsRestoreTargetExisting(string installPath, string entryName)
    {
        try
        {
            return File.Exists(ArchiveExtractionPath.Resolve(installPath, entryName));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeRestoreTarget(string installPath, string entryName)
    {
        try
        {
            _ = ArchiveExtractionPath.Resolve(installPath, entryName);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static ServerBackupInfo CreateBackupInfo(string backupPath)
    {
        var file = new FileInfo(backupPath);
        return new ServerBackupInfo(
            backupPath,
            Path.GetFileName(backupPath),
            file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue);
    }

    private static (string Path, FileStream Stream) ReserveUniqueBackupPath(string backupsFolder, DateTimeOffset createdUtc)
    {
        var stem = createdUtc.LocalDateTime.ToString("yyyyMMdd-HHmmss");
        for (var suffix = 0; ; suffix++)
        {
            var fileName = suffix == 0 ? $"{stem}.zip" : $"{stem}-{suffix}.zip";
            var candidate = Path.Combine(backupsFolder, fileName);
            try
            {
                // FileMode.CreateNew is the cross-process reservation. The returned stream proves
                // this invocation owns the path, so its failure cleanup cannot delete another
                // concurrently completed backup.
                return (candidate, new FileStream(candidate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another backup reserved this timestamp/suffix first; try the next suffix.
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static bool IsInside(string root, string path)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(resolvedPath, resolvedRoot, StringComparison.OrdinalIgnoreCase) ||
               resolvedPath.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record BackupPreview(
    string FileName,
    long SizeBytes,
    int FileCount,
    IReadOnlyList<string> Files,
    string? ManifestText,
    IReadOnlyList<string> FileTargets,
    IReadOnlyList<string> FolderTargets,
    int ExistingFileCount,
    int NewFileCount,
    int SkippedUnsafeFileCount,
    IReadOnlyList<string> MissingTargets,
    IReadOnlyList<string> FailedTargets)
{
    public string Summary => $"{FileName} - {FileCount} files - {SizeBytes / 1024d / 1024d:0.0} MB";

    public string RestoreSummary =>
        $"{ExistingFileCount} overwrite, {NewFileCount} new" +
        (SkippedUnsafeFileCount > 0 ? $", {SkippedUnsafeFileCount} unsafe skipped" : string.Empty);
}

public sealed record ServerBackupInfo(
    string BackupPath,
    string FileName,
    long SizeBytes,
    DateTime CreatedUtc);

public sealed record BackupResult(
    bool Success,
    string? BackupPath,
    ServerBackupInfo? Info,
    IReadOnlyList<string> BackedUpPaths,
    IReadOnlyList<string> MissingPaths,
    IReadOnlyList<string> FailedPaths,
    string Message,
    string? RetentionWarning = null)
{
    // Shared by every caller that reports a backup outcome (manual/Discord/cron backup, and
    // backup-before-start) so a locked file being silently skipped - the exact case per-file
    // resilience exists for - is never reported back as a plain, unqualified "Backup created."
    public string BuildWarningSuffix()
    {
        var warnings = new List<string>();
        if (MissingPaths.Count > 0 || FailedPaths.Count > 0)
        {
            warnings.Add($"{MissingPaths.Count} missing target(s), {FailedPaths.Count} unreadable file(s)");
        }

        if (!string.IsNullOrWhiteSpace(RetentionWarning))
        {
            warnings.Add(RetentionWarning);
        }

        return warnings.Count == 0 ? string.Empty : $" ({string.Join("; ", warnings)})";
    }
}

public sealed record RestoreResult(
    bool Success,
    string BackupPath,
    IReadOnlyList<string> RestoredFiles,
    string Message);

public sealed record DeleteBackupResult(
    bool Success,
    string BackupPath,
    string Message);
