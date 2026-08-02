using System.Net.Http.Headers;
using System.Text.Json;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleRepositorySearchService
{
    public const string RequiredRepositoryPrefix = "WindowsGSH.";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly Func<Uri, CancellationToken, Task<string>> _downloadStringAsync;

    public ModuleRepositorySearchService(Func<Uri, CancellationToken, Task<string>>? downloadStringAsync = null)
    {
        _downloadStringAsync = downloadStringAsync ?? DownloadStringAsync;
    }

    public async Task<IReadOnlyList<ModuleRepositorySearchResult>> SearchAsync(
        string searchTerm,
        CancellationToken cancellationToken = default)
    {
        var normalizedSearchTerm = searchTerm?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSearchTerm))
        {
            return [];
        }

        var query = $"{RequiredRepositoryPrefix}{normalizedSearchTerm} in:name";
        var uri = new Uri($"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&sort=stars&order=desc&per_page=25");
        var json = await _downloadStringAsync(uri, cancellationToken).ConfigureAwait(false);
        return ParseGitHubSearchResults(json, normalizedSearchTerm);
    }

    public static IReadOnlyList<ModuleRepositorySearchResult> ParseGitHubSearchResults(string json, string searchTerm)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ModuleRepositorySearchResult>();
        foreach (var item in items.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var fullName = ReadString(item, "full_name");
            if (!IsWindowsGshModuleNameMatch(name, searchTerm) &&
                !IsWindowsGshModuleNameMatch(fullName, searchTerm))
            {
                continue;
            }

            var owner = item.TryGetProperty("owner", out var ownerElement)
                ? ReadString(ownerElement, "login")
                : string.Empty;
            var ownerAvatarUrl = item.TryGetProperty("owner", out ownerElement)
                ? ReadString(ownerElement, "avatar_url")
                : string.Empty;
            var license = item.TryGetProperty("license", out var licenseElement)
                ? ReadString(licenseElement, "spdx_id")
                : string.Empty;

            results.Add(new ModuleRepositorySearchResult(
                name,
                fullName,
                owner,
                ownerAvatarUrl,
                ReadString(item, "description"),
                ReadString(item, "html_url"),
                ReadString(item, "language"),
                license,
                ReadString(item, "default_branch"),
                ReadString(item, "zipball_url"),
                ReadInt(item, "stargazers_count"),
                ReadInt(item, "forks_count"),
                ReadInt(item, "open_issues_count"),
                ReadDateTimeOffset(item, "updated_at")));
        }

        return results;
    }

    public static bool IsWindowsGshModuleNameMatch(string value, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(searchTerm))
        {
            return false;
        }

        var normalizedValue = value.Trim();
        var slashIndex = normalizedValue.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            normalizedValue = normalizedValue[(slashIndex + 1)..];
        }

        if (!normalizedValue.StartsWith(RequiredRepositoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var repositoryToken = NormalizeSearchToken(normalizedValue);
        var searchToken = NormalizeSearchToken(searchTerm);
        return !string.IsNullOrWhiteSpace(searchToken) &&
            repositoryToken.Contains(searchToken, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeSearchToken(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private static async Task<string> DownloadStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsGSH", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}

public sealed record ModuleRepositorySearchResult(
    string Name,
    string FullName,
    string Owner,
    string OwnerAvatarUrl,
    string Description,
    string Url,
    string Language,
    string License,
    string DefaultBranch,
    string ZipUrl,
    int Stars,
    int Forks,
    int OpenIssues,
    DateTimeOffset? UpdatedAt)
{
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Name : FullName;

    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "No GitHub repository description provided." : Description;

    public string DisplayUpdated => UpdatedAt.HasValue ? UpdatedAt.Value.LocalDateTime.ToString("g") : "unknown";

    public string DisplayDetails => $"{Owner} - {FormatDetail(Language, "Unknown language")} - Updated {DisplayUpdated}";

    public string DisplayMetadata => $"License {FormatDetail(License, "not specified")} - Open issues {OpenIssues} - Branch {FormatDetail(DefaultBranch, "unknown")}";

    public string StatsText => $"Stars {Stars} - Forks {Forks}";

    private static string FormatDetail(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
