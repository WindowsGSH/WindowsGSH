using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsGSH.Core.Security;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Modules;

public interface IAddonPackageDownloader
{
    Task<AddonDownloadResult> DownloadAsync(Uri source, string targetPath, CancellationToken cancellationToken);
}

public sealed record AddonDownloadResult(string FilePath, string Sha256);

public sealed record AddonPackageLimits(long MaxDownloadBytes, long MaxExtractedBytes, int MaxEntries);

public sealed class HttpAddonPackageDownloader : IAddonPackageDownloader
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _httpClient;
    private readonly long _maxDownloadBytes;

    public HttpAddonPackageDownloader(long maxDownloadBytes = 200L * 1024 * 1024)
        : this(SharedClient, maxDownloadBytes)
    {
    }

    /// <summary>Test-only entry point (P2-03 follow-up) so MaxDownloadBytes enforcement can be exercised against a fake handler instead of a real HTTPS endpoint.</summary>
    internal HttpAddonPackageDownloader(HttpClient httpClient, long maxDownloadBytes)
    {
        _httpClient = httpClient;
        _maxDownloadBytes = maxDownloadBytes;
    }

    public async Task<AddonDownloadResult> DownloadAsync(Uri source, string targetPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > _maxDownloadBytes)
        {
            throw new InvalidOperationException($"Addon package is too large. Maximum download size is {_maxDownloadBytes} bytes.");
        }

        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var local = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var totalBytes = 0L;
            int read;
            while ((read = await remote.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes += read;
                if (totalBytes > _maxDownloadBytes)
                {
                    throw new InvalidOperationException($"Addon package download exceeded the maximum size of {_maxDownloadBytes} bytes.");
                }

                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        await using var input = File.OpenRead(targetPath);
        return new AddonDownloadResult(targetPath, Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)));
    }
}

public sealed class AddonPackageService
{
    public static readonly AddonPackageLimits DefaultLimits = new(
        MaxDownloadBytes: 200L * 1024 * 1024,
        MaxExtractedBytes: 500L * 1024 * 1024,
        MaxEntries: 5000);

    private readonly IAddonPackageDownloader _downloader;
    private readonly AddonPackageLimits _limits;

    public AddonPackageService(IAddonPackageDownloader? downloader = null, AddonPackageLimits? limits = null)
    {
        _limits = limits ?? DefaultLimits;
        _downloader = downloader ?? new HttpAddonPackageDownloader(_limits.MaxDownloadBytes);
    }

    public ServerAddonStatus GetStatus(ServerInstance instance, ServerAddonDefinition addon)
    {
        var metadata = ReadMetadata(instance, addon.Id);
        if (metadata == null)
        {
            return new ServerAddonStatus(addon.Id, false, false, "Not installed");
        }

        var filesPresent = metadata.Files.All(file => File.Exists(ResolveInstallRelativePath(instance, file.RelativePath)));
        var source = string.IsNullOrWhiteSpace(metadata.SourceName) ? metadata.SourceUrl : metadata.SourceName;
        return filesPresent
            ? new ServerAddonStatus(
                addon.Id,
                true,
                true,
                $"Installed from {source} on {metadata.InstalledAtUtc:yyyy-MM-dd}",
                metadata.SourceName,
                metadata.SourceVersion,
                metadata.InstalledAtUtc,
                metadata.Sha256)
            : new ServerAddonStatus(
                addon.Id,
                true,
                false,
                "Installed metadata exists, but one or more files are missing",
                metadata.SourceName,
                metadata.SourceVersion,
                metadata.InstalledAtUtc,
                metadata.Sha256);
    }

    public async Task InstallAsync(
        ServerInstance instance,
        ServerAddonDefinition addon,
        CancellationToken cancellationToken)
    {
        var package = addon.Package ?? throw new NotSupportedException($"{addon.Name} does not define automated package installation.");
        if (!Uri.TryCreate(package.SourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme is not "https")
        {
            throw new InvalidOperationException("Addon package source must be an absolute HTTPS URL.");
        }

        var workRoot = Path.Combine(instance.ServerFolder, "addon-work", $"{addon.Id}-{Guid.NewGuid():N}");
        var downloadPath = Path.Combine(workRoot, "package.bin");
        var stagingPath = Path.Combine(workRoot, "staging");
        Directory.CreateDirectory(stagingPath);
        try
        {
            var download = await _downloader.DownloadAsync(source, downloadPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(package.ExpectedSha256) &&
                !string.Equals(download.Sha256, package.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Addon package hash does not match the expected value. Expected {package.ExpectedSha256}, got {download.Sha256}.");
            }

            if (package.Kind == AddonPackageKind.File)
            {
                var fileName = string.IsNullOrWhiteSpace(package.FileName)
                    ? Path.GetFileName(source.LocalPath)
                    : package.FileName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    throw new InvalidOperationException("Direct addon files require a fileName or a URL with a file name.");
                }

                File.Copy(download.FilePath, Path.Combine(stagingPath, ValidateFileName(fileName)), overwrite: true);
            }
            else
            {
                ExtractArchive(download.FilePath, stagingPath, package, _limits);
            }

            ValidateRequiredMarkers(stagingPath, package.RequiredMarkers);
            var installed = ReadMetadata(instance, addon.Id);
            if (installed == null)
            {
                ApplyStagedFiles(instance, addon, stagingPath, download.Sha256);
            }
            else
            {
                await ReplaceInstalledAddonAsync(
                    instance,
                    addon,
                    installed,
                    stagingPath,
                    download.Sha256,
                    Path.Combine(workRoot, "previous"),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }
    }

    public Task RemoveAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = ReadMetadata(instance, addonId);
        if (metadata == null)
        {
            return Task.CompletedTask;
        }

        foreach (var file in metadata.Files.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveInstallRelativePath(instance, file.RelativePath);
            if (file.HadOriginal && !string.IsNullOrWhiteSpace(file.BackupPath))
            {
                var backup = ResolveServerRelativePath(instance, file.BackupPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }

            DeleteEmptyParents(Path.GetDirectoryName(target), instance.InstallPath);
        }

        TryDeleteDirectory(GetAddonStateDirectory(instance, addonId));
        return Task.CompletedTask;
    }

    internal void ApplyStagedFiles(
        ServerInstance instance,
        ServerAddonDefinition addon,
        string stagingPath,
        string sha256)
    {
        var package = addon.Package ?? throw new InvalidOperationException("Addon package metadata is missing.");
        var installRoot = ResolveInstallRelativePath(instance, package.InstallPath);
        Directory.CreateDirectory(installRoot);
        var stateDirectory = GetAddonStateDirectory(instance, addon.Id);
        var backupDirectory = Path.Combine(stateDirectory, "originals");
        TryDeleteDirectory(stateDirectory);
        Directory.CreateDirectory(backupDirectory);

        var applied = new List<AddonInstalledFile>();
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var stagedRelativePath = Path.GetRelativePath(stagingPath, sourcePath);
                var target = ResolveWithinRoot(installRoot, stagedRelativePath);
                var installRelativePath = Path.GetRelativePath(instance.InstallPath, target);
                var hadOriginal = File.Exists(target);
                string? backupPath = null;
                if (hadOriginal)
                {
                    var backup = ResolveWithinRoot(backupDirectory, stagedRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                    backupPath = Path.GetRelativePath(instance.ServerFolder, backup);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                applied.Add(new AddonInstalledFile(installRelativePath, hadOriginal, backupPath));
                File.Copy(sourcePath, target, overwrite: true);
            }

            if (applied.Count == 0)
            {
                throw new InvalidOperationException("Addon package did not contain any installable files.");
            }

            var metadata = new AddonInstallMetadata(
                addon.Id,
                addon.Name,
                addon.SourceName,
                addon.SourceVersion,
                package.SourceUrl,
                sha256,
                DateTimeOffset.UtcNow,
                applied);
            AtomicFile.WriteAllText(
                Path.Combine(stateDirectory, "installed.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            Rollback(instance, applied);
            TryDeleteDirectory(stateDirectory);
            throw;
        }
    }

    private async Task ReplaceInstalledAddonAsync(
        ServerInstance instance,
        ServerAddonDefinition addon,
        AddonInstallMetadata installed,
        string stagingPath,
        string sha256,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var snapshot = SnapshotInstalledAddon(instance, addon.Id, installed, snapshotPath);
        try
        {
            await RemoveAsync(instance, addon.Id, cancellationToken).ConfigureAwait(false);
            ApplyStagedFiles(instance, addon, stagingPath, sha256);
        }
        catch
        {
            RestoreInstalledAddon(instance, addon.Id, snapshot);
            throw;
        }
    }

    private static InstalledAddonSnapshot SnapshotInstalledAddon(
        ServerInstance instance,
        string addonId,
        AddonInstallMetadata installed,
        string snapshotPath)
    {
        Directory.CreateDirectory(snapshotPath);
        var files = new List<InstalledAddonFileSnapshot>();
        foreach (var (file, index) in installed.Files.Select((file, index) => (file, index)))
        {
            var target = ResolveInstallRelativePath(instance, file.RelativePath);
            var snapshotFile = Path.Combine(snapshotPath, "files", index.ToString("D8"));
            var exists = File.Exists(target);
            if (exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotFile)!);
                File.Copy(target, snapshotFile, overwrite: true);
            }

            files.Add(new InstalledAddonFileSnapshot(file.RelativePath, exists, snapshotFile));
        }

        var stateDirectory = GetAddonStateDirectory(instance, addonId);
        var stateSnapshot = Path.Combine(snapshotPath, "state");
        if (Directory.Exists(stateDirectory))
        {
            CopyDirectory(stateDirectory, stateSnapshot);
        }

        return new InstalledAddonSnapshot(files, stateSnapshot);
    }

    private static void RestoreInstalledAddon(
        ServerInstance instance,
        string addonId,
        InstalledAddonSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            var target = ResolveInstallRelativePath(instance, file.RelativePath);
            if (file.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.SnapshotPath, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        var stateDirectory = GetAddonStateDirectory(instance, addonId);
        TryDeleteDirectory(stateDirectory);
        if (Directory.Exists(snapshot.StateDirectory))
        {
            CopyDirectory(snapshot.StateDirectory, stateDirectory);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ExtractArchive(string archivePath, string stagingPath, AddonPackageDefinition package, AddonPackageLimits limits)
    {
        var budget = new ExtractionBudget(limits);
        switch (package.Kind)
        {
            case AddonPackageKind.Zip:
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        budget.TrackEntry();
                        ArchiveExtractionPath.RejectLink(entry);
                        try
                        {
                            _ = ArchiveExtractionPath.Resolve(stagingPath, entry.FullName, allowRoot: string.IsNullOrEmpty(entry.Name));
                        }
                        catch (InvalidDataException ex)
                        {
                            throw new InvalidOperationException($"Addon archive entry is unsafe: {entry.FullName}", ex);
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }

                        ExtractEntry(entry.FullName, () => entry.Open(), stagingPath, package, budget);
                    }
                }
                break;
            case AddonPackageKind.Tar:
                using (var stream = File.OpenRead(archivePath))
                {
                    ExtractTar(stream, stagingPath, package, budget);
                }
                break;
            case AddonPackageKind.TarGz:
                using (var stream = File.OpenRead(archivePath))
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                {
                    ExtractTar(gzip, stagingPath, package, budget);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported addon package kind: {package.Kind}.");
        }
    }

    private static void ExtractTar(Stream stream, string stagingPath, AddonPackageDefinition package, ExtractionBudget budget)
    {
        using var reader = new TarReader(stream, leaveOpen: false);
        while (reader.GetNextEntry() is { } entry)
        {
            budget.TrackEntry();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream == null)
            {
                continue;
            }

            ExtractEntry(entry.Name, () => entry.DataStream, stagingPath, package, budget);
        }
    }

    private static void ExtractEntry(
        string archivePath,
        Func<Stream> openStream,
        string stagingPath,
        AddonPackageDefinition package,
        ExtractionBudget budget)
    {
        var relative = MapArchivePath(archivePath, package);
        if (relative == null)
        {
            return;
        }

        var target = ResolveWithinRoot(stagingPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = openStream();
        using var output = File.Create(target);
        budget.CopyWithLimit(input, output);
    }

    private sealed class ExtractionBudget(AddonPackageLimits limits)
    {
        private int _entryCount;
        private long _extractedBytes;

        public void TrackEntry()
        {
            _entryCount++;
            if (_entryCount > limits.MaxEntries)
            {
                throw new InvalidOperationException($"Addon package contains too many entries. Maximum entry count is {limits.MaxEntries}.");
            }
        }

        public void CopyWithLimit(Stream input, Stream output)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                _extractedBytes += read;
                if (_extractedBytes > limits.MaxExtractedBytes)
                {
                    throw new InvalidOperationException($"Addon package extracts to too much data. Maximum extracted size is {limits.MaxExtractedBytes} bytes.");
                }

                output.Write(buffer, 0, read);
            }
        }
    }

    internal static string? MapArchivePath(string archivePath, AddonPackageDefinition package)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || Path.IsPathRooted(archivePath) ||
            archivePath.StartsWith('/') || archivePath.StartsWith('\\') ||
            (archivePath.Length >= 2 && char.IsLetter(archivePath[0]) && archivePath[1] == ':'))
        {
            throw new InvalidOperationException($"Addon archive entry must be a non-empty relative path: {archivePath}");
        }

        var normalized = archivePath.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(package.ArchiveSubpath))
        {
            var prefix = package.ArchiveSubpath.Replace('\\', '/').Trim('/');
            if (!normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            normalized = normalized[(prefix.Length + 1)..];
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".."))
        {
            throw new InvalidOperationException($"Addon archive entry escapes the install root: {archivePath}");
        }

        if (package.StripComponents < 0 || parts.Length <= package.StripComponents)
        {
            return null;
        }

        return Path.Combine(parts.Skip(package.StripComponents).ToArray());
    }

    private static void ValidateRequiredMarkers(string stagingPath, IReadOnlyList<string>? markers)
    {
        foreach (var marker in markers ?? [])
        {
            var path = ResolveWithinRoot(stagingPath, marker);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidOperationException($"Addon package is missing required marker: {marker}.");
            }
        }
    }

    private static void Rollback(ServerInstance instance, IEnumerable<AddonInstalledFile> files)
    {
        foreach (var file in files.Reverse())
        {
            var target = ResolveInstallRelativePath(instance, file.RelativePath);
            if (file.HadOriginal && !string.IsNullOrWhiteSpace(file.BackupPath))
            {
                File.Copy(ResolveServerRelativePath(instance, file.BackupPath), target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    private static AddonInstallMetadata? ReadMetadata(ServerInstance instance, string addonId)
    {
        var path = Path.Combine(GetAddonStateDirectory(instance, addonId), "installed.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AddonInstallMetadata>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static string GetAddonStateDirectory(ServerInstance instance, string addonId) =>
        ResolveServerRelativePath(instance, Path.Combine("addons", ValidateFileName(addonId)));

    private static string ResolveInstallRelativePath(ServerInstance instance, string relativePath) =>
        ResolveWithinRoot(instance.InstallPath, relativePath);

    private static string ResolveServerRelativePath(ServerInstance instance, string relativePath) =>
        ResolveWithinRoot(instance.ServerFolder, relativePath);

    internal static string ResolveWithinRoot(string root, string relativePath)
    {
        try
        {
            return ArchiveExtractionPath.Resolve(root, relativePath, allowRoot: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Addon path escapes the allowed root: {relativePath}", ex);
        }
    }

    private static string ValidateFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Invalid addon file name or id: {value}");
        }
        return value;
    }

    private sealed record InstalledAddonSnapshot(
        IReadOnlyList<InstalledAddonFileSnapshot> Files,
        string StateDirectory);

    private sealed record InstalledAddonFileSnapshot(
        string RelativePath,
        bool Existed,
        string SnapshotPath);

    private static void DeleteEmptyParents(string? path, string stopAt)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        while (!string.IsNullOrWhiteSpace(path) &&
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(path) &&
               !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record AddonInstallMetadata(
    string AddonId,
    string AddonName,
    string? SourceName,
    string? SourceVersion,
    string SourceUrl,
    string Sha256,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyList<AddonInstalledFile> Files);

public sealed record AddonInstalledFile(string RelativePath, bool HadOriginal, string? BackupPath);
