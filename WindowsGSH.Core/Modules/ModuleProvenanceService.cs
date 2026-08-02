using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleProvenanceService
{
    public const string MetadataFileName = "import.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public ModuleProvenanceSnapshot GetSnapshot(string modulePath)
    {
        var metadata = LoadMetadata(modulePath);
        var currentHash = ComputeModuleHash(modulePath);
        var changed = !string.IsNullOrWhiteSpace(metadata?.ImportedHash) &&
            !string.Equals(metadata.ImportedHash, currentHash, StringComparison.OrdinalIgnoreCase);
        return new ModuleProvenanceSnapshot(
            metadata,
            currentHash,
            changed,
            GetWarning(metadata, changed));
    }

    public ModuleImportMetadata WriteImportMetadata(
        string modulePath,
        string sourceType,
        string originalFileName,
        ModuleManifestProvenance manifestProvenance,
        DateTimeOffset? importedUtc = null)
    {
        var metadata = new ModuleImportMetadata(
            sourceType,
            originalFileName,
            importedUtc ?? DateTimeOffset.UtcNow,
            ComputeModuleHash(modulePath),
            manifestProvenance.Author,
            manifestProvenance.Description,
            manifestProvenance.Homepage,
            manifestProvenance.Repository,
            manifestProvenance.SourceUrl,
            manifestProvenance.Url,
            manifestProvenance.Color,
            [
                "Imported modules are not created, owned, reviewed, signed, or guaranteed by WindowsGSH.",
                "If you download and run a third-party module, responsibility for that module is yours."
            ],
            manifestProvenance.ModuleApiVersion,
            manifestProvenance.MinimumWindowsGshVersion,
            manifestProvenance.SupportedWindowsGshVersions);
        File.WriteAllText(
            Path.Combine(modulePath, MetadataFileName),
            JsonSerializer.Serialize(metadata, JsonOptions));
        return metadata;
    }

    public static string ComputeModuleHash(string modulePath)
    {
        using var sha = SHA256.Create();
        var files = Directory.EnumerateFiles(modulePath, "*", SearchOption.AllDirectories)
            .Where(path => !ShouldIgnore(modulePath, path))
            .Select(path => new
            {
                Path = path,
                RelativePath = Path.GetRelativePath(modulePath, path).Replace('\\', '/')
            })
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath.ToLowerInvariant());
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            var contents = File.ReadAllBytes(file.Path);
            sha.TransformBlock(contents, 0, contents.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []);
    }

    public static ModuleManifestProvenance ReadManifestProvenance(string moduleJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(moduleJsonPath));
            var root = document.RootElement;
            return new ModuleManifestProvenance(
                ReadString(root, "author"),
                ReadString(root, "description"),
                ReadString(root, "homepage"),
                ReadString(root, "repository"),
                ReadString(root, "sourceUrl"),
                ReadString(root, "url"),
                ReadString(root, "color"),
                ReadString(root, "moduleApiVersion"),
                ReadString(root, "minimumWindowsGshVersion"),
                ReadStringArray(root, "supportedWindowsGshVersions"));
        }
        catch
        {
            return ModuleManifestProvenance.Empty;
        }
    }

    private static ModuleImportMetadata? LoadMetadata(string modulePath)
    {
        try
        {
            var path = Path.Combine(modulePath, MetadataFileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ModuleImportMetadata>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWarning(ModuleImportMetadata? metadata, bool changed)
    {
        if (changed)
        {
            return "Module files changed since import.";
        }

        return metadata == null
            ? "No WindowsGSH import metadata found."
            : null;
    }

    private static bool ShouldIgnore(string modulePath, string path)
    {
        var relativePath = Path.GetRelativePath(modulePath, path);
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part =>
            string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Path.GetFileName(path), MetadataFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }
}

public sealed record ModuleImportMetadata(
    string SourceType,
    string OriginalFileName,
    DateTimeOffset ImportedUtc,
    string ImportedHash,
    string? Author,
    string? Description,
    string? Homepage,
    string? Repository,
    string? SourceUrl,
    string? Url,
    string? Color,
    IReadOnlyList<string> Warnings,
    string? ModuleApiVersion = null,
    string? MinimumWindowsGshVersion = null,
    IReadOnlyList<string>? SupportedWindowsGshVersions = null);

public sealed record ModuleManifestProvenance(
    string? Author,
    string? Description,
    string? Homepage,
    string? Repository,
    string? SourceUrl,
    string? Url,
    string? Color,
    string? ModuleApiVersion,
    string? MinimumWindowsGshVersion,
    IReadOnlyList<string> SupportedWindowsGshVersions)
{
    public static ModuleManifestProvenance Empty { get; } = new(null, null, null, null, null, null, null, null, null, []);
}

public sealed record ModuleProvenanceSnapshot(
    ModuleImportMetadata? Metadata,
    string CurrentHash,
    bool HasChangedSinceImport,
    string? Warning);
