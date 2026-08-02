using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using WindowsGSH.Core.Security;
using WindowsGSH.Core;

namespace WindowsGSH.Services;

public sealed class DepotDownloaderToolManager
{
    public const string Version = "3.4.0";
    public const string LicenseName = "GNU General Public License v2";
    public const string ProjectUrl = "https://github.com/SteamRE/DepotDownloader";
    internal const string DownloadUrl = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";
    internal const string ArchiveSha256 = "41C9E9F0DF54B3AD02E67A11726756E5C73283BD7C2E1B04ACFA5AE4C2ED3767";
    internal const string ExecutableSha256 = "6281279EFCE8F1E20DB9532A58E42382F81AFB9E3827A8B965FFCB43FBE4531F";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly string _installDirectory;
    private readonly HttpClient _httpClient;

    public DepotDownloaderToolManager()
        : this(AppPaths.GetPath("tools", "DepotDownloader", Version), SharedHttpClient)
    {
    }

    internal DepotDownloaderToolManager(string installDirectory, HttpClient httpClient)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _httpClient = httpClient;
    }

    public string ExecutablePath => Path.Combine(_installDirectory, "DepotDownloader.exe");

    public string AccountConfigPath => Path.Combine(_installDirectory, "account.config");

    public bool IsInstalled =>
        File.Exists(ExecutablePath) &&
        HashMatches(ExecutablePath, ExecutableSha256);

    public async Task EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled)
        {
            progress?.Report($"Authenticated Steam downloader found: {ExecutablePath}");
            return;
        }

        var parentDirectory = Path.GetDirectoryName(_installDirectory)!;
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory, $".{Version}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(parentDirectory, $".{Version}-{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report($"Downloading DepotDownloader {Version} from {ProjectUrl}...");
            await using (var remote = await _httpClient.GetStreamAsync(DownloadUrl, cancellationToken).ConfigureAwait(false))
            await using (var local = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            if (!HashMatches(archivePath, ArchiveSha256))
            {
                throw new InvalidDataException("DepotDownloader download failed SHA-256 verification. The executable was not installed.");
            }

            progress?.Report("Verified DepotDownloader archive. Extracting...");
            ExtractArchiveSafely(archivePath, stagingDirectory);
            var stagedExecutable = Path.Combine(stagingDirectory, "DepotDownloader.exe");
            if (!File.Exists(stagedExecutable) || !HashMatches(stagedExecutable, ExecutableSha256))
            {
                throw new InvalidDataException("DepotDownloader executable failed SHA-256 verification. The executable was not installed.");
            }

            if (Directory.Exists(_installDirectory))
            {
                Directory.Delete(_installDirectory, recursive: true);
            }

            Directory.Move(stagingDirectory, _installDirectory);
            progress?.Report($"DepotDownloader {Version} is ready ({LicenseName}).");
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            ArchiveExtractionPath.RejectLink(entry);
            var isDirectory = string.IsNullOrEmpty(entry.Name);
            // SECURITY: Resolve rejects rooted/traversal names, proves canonical
            // containment, and rejects existing reparse-point components.
            var destinationPath = ArchiveExtractionPath.Resolve(destinationRoot, entry.FullName, allowRoot: isDirectory);
            var containmentRoot = Path.TrimEndingDirectorySeparator(destinationRoot) + Path.DirectorySeparatorChar;
            if (!isDirectory && !destinationPath.StartsWith(containmentRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"DepotDownloader archive entry escaped the destination: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
