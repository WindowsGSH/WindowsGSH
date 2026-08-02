using System.Text.Json;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Java;

public sealed class ManagedJavaCatalogue
{
    private readonly string _cachePath;
    private IReadOnlyList<ManagedJavaRelease> _releases;

    public ManagedJavaCatalogue()
        : this(AppPaths.GetPath("runtimes", "catalogue.json"))
    {
    }

    public ManagedJavaCatalogue(string cachePath)
    {
        _cachePath = cachePath;
        _releases = LoadFromFile(cachePath, GetLastKnownGoodPath(cachePath));
    }

    internal ManagedJavaCatalogue(IReadOnlyList<ManagedJavaRelease> releases)
    {
        _cachePath = string.Empty;
        _releases = releases;
    }

    public IReadOnlyList<ManagedJavaRelease> GetReleases() => _releases;

    public ManagedJavaRelease? GetRelease(string runtimeId)
    {
        return _releases.FirstOrDefault(release =>
            string.Equals(release.Id, runtimeId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ManagedJavaRelease>> RefreshFromApiAsync(
        AdoptiumApiClient client,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Fetching available Java LTS releases from Adoptium...");

        var ltsVersions = await client
            .GetAvailableLtsVersionsAsync(cancellationToken)
            .ConfigureAwait(false);
        var previousByMajor = _releases.ToDictionary(release => release.MajorVersion);
        var releases = new List<ManagedJavaRelease>();

        foreach (var version in ltsVersions.OrderByDescending(version => version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Fetching Java {version} release info...");
            try
            {
                var release = await client
                    .GetLatestWindowsX64JreAsync(version, cancellationToken)
                    .ConfigureAwait(false);
                if (release != null)
                {
                    releases.Add(release);
                    previousByMajor.Remove(version);
                }
                else if (previousByMajor.Remove(version, out var previous))
                {
                    releases.Add(previous);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report($"Could not fetch Java {version}: {ex.Message}");
                if (previousByMajor.Remove(version, out var previous))
                {
                    progress?.Report($"Retaining previous catalogue entry for Java {version} ({previous.ReleaseVersion}).");
                    releases.Add(previous);
                }
            }
        }

        releases.AddRange(previousByMajor.Values);
        _releases = releases;

        if (!string.IsNullOrEmpty(_cachePath))
        {
            SaveToFile(_cachePath, releases);
        }

        return releases;
    }

    private static ManagedJavaRelease[] LoadFromFile(string path, string fallbackPath)
    {
        foreach (var candidate in new[] { path, fallbackPath })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var cached = JsonSerializer.Deserialize<CatalogueCache>(
                    File.ReadAllText(candidate),
                    SerializerOptions);
                if (cached?.Releases != null)
                {
                    return cached.Releases;
                }
            }
            catch
            {
                // Try the last-known-good cache before falling back to an empty catalogue.
            }
        }

        return [];
    }

    private static void SaveToFile(string path, IReadOnlyList<ManagedJavaRelease> releases)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                try
                {
                    var existing = File.ReadAllText(path);
                    if (JsonSerializer.Deserialize<CatalogueCache>(existing, SerializerOptions)?.Releases != null)
                    {
                        AtomicFile.WriteAllText(GetLastKnownGoodPath(path), existing);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A corrupt primary must not prevent a verified fresh catalogue replacing it.
                }
            }

            var cached = new CatalogueCache(DateTimeOffset.UtcNow, [.. releases]);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(cached, SerializerOptions));
        }
        catch (Exception ex)
        {
            WindowsGshEventBus.Shared.Publish(new ServerLogEvent(
                DateTimeOffset.UtcNow, null, null,
                WindowsGshEventSeverity.Warning, "Java",
                $"Java catalogue cache could not be saved: {ex.Message}"));
        }
    }

    private static string GetLastKnownGoodPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(path)}.last-good{Path.GetExtension(path)}");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record CatalogueCache(
        DateTimeOffset FetchedAt,
        ManagedJavaRelease[] Releases);
}
