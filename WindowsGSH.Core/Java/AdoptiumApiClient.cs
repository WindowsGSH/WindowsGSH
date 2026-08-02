using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WindowsGSH.Core.Java;

public sealed class AdoptiumApiClient
{
    private const string BaseUrl = "https://api.adoptium.net/v3";

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _httpClient;

    public AdoptiumApiClient() : this(SharedClient) { }

    internal AdoptiumApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<int>> GetAvailableLtsVersionsAsync(CancellationToken ct = default)
    {
        var response = await _httpClient
            .GetFromJsonAsync<AvailableReleasesResponse>($"{BaseUrl}/info/available_releases", ct)
            .ConfigureAwait(false);
        return response?.AvailableLtsReleases ?? [];
    }

    public async Task<ManagedJavaRelease?> GetLatestWindowsX64JreAsync(int majorVersion, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/assets/latest/{majorVersion}/hotspot" +
                  "?architecture=x64&image_type=jre&os=windows&vendor=eclipse";

        var items = await _httpClient
            .GetFromJsonAsync<ReleaseItem[]>(url, ct)
            .ConfigureAwait(false);

        var item = items?.FirstOrDefault(r => r.Binary?.Package != null);
        if (item?.Binary?.Package is not { } pkg)
            return null;

        const long AbsoluteMaxBytes = 500L * 1024 * 1024;
        var maxBytes = Math.Min(AbsoluteMaxBytes, Math.Max(20L * 1024 * 1024, (long)(pkg.Size * 1.2)));

        return new ManagedJavaRelease(
            Vendor: "Temurin",
            MajorVersion: majorVersion,
            Architecture: "x64",
            ReleaseVersion: ParseReleaseVersion(item.ReleaseName),
            DownloadUrl: pkg.Link ?? string.Empty,
            // P3-07 (documented trust model, not fixed): this hash comes from the same
            // Adoptium API response as the download URL itself, over the same HTTPS/TLS
            // channel. It protects against transport corruption and simple mirror tampering,
            // but not against a compromised api.adoptium.net serving a matched bad URL+hash
            // pair. There's no independent/pinned verification (e.g. a maintained catalogue of
            // known-good hashes per LTS release) today. Revisit if Adoptium's API is ever a
            // plausible attack target for this app's threat model.
            ArchiveSha256: (pkg.Checksum ?? string.Empty).ToUpperInvariant(),
            MaxArchiveSizeBytes: maxBytes,
            LicenseUrl: "https://adoptium.net/docs/faq/#q-what-is-the-license-for-eclipse-temurin",
            SourceUrl: item.ReleaseLink ?? $"https://github.com/adoptium/temurin{majorVersion}-binaries");
    }

    private static string ParseReleaseVersion(string? releaseName)
    {
        if (releaseName is null) return string.Empty;
        // "jdk-21.0.11+10"  → "21.0.11+10"
        // "jdk8u492-b09"    → "8u492-b09"
        if (releaseName.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase))
            return releaseName[4..];
        if (releaseName.StartsWith("jdk", StringComparison.OrdinalIgnoreCase))
            return releaseName[3..];
        return releaseName;
    }

    private sealed class AvailableReleasesResponse
    {
        [JsonPropertyName("available_lts_releases")]
        public int[] AvailableLtsReleases { get; init; } = [];
    }

    private sealed class ReleaseItem
    {
        [JsonPropertyName("release_name")]
        public string? ReleaseName { get; init; }

        [JsonPropertyName("release_link")]
        public string? ReleaseLink { get; init; }

        [JsonPropertyName("binary")]
        public BinaryInfo? Binary { get; init; }
    }

    private sealed class BinaryInfo
    {
        [JsonPropertyName("package")]
        public PackageInfo? Package { get; init; }
    }

    private sealed class PackageInfo
    {
        [JsonPropertyName("link")]
        public string? Link { get; init; }

        [JsonPropertyName("checksum")]
        public string? Checksum { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
