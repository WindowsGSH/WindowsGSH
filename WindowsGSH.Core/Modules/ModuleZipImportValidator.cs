using System.IO;
using System.IO.Compression;
using WindowsGSH.Core.Security;

namespace WindowsGSH.Core.Modules;

public static class ModuleZipImportValidator
{
    public static readonly ModuleZipImportLimits DefaultLimits = new(
        MaxZipFileBytes: 100L * 1024 * 1024,
        MaxExtractedBytes: 500L * 1024 * 1024,
        MaxEntries: 5000);

    public static void Validate(string zipPath, string extractionRoot, ModuleZipImportLimits? limits = null)
    {
        limits ??= DefaultLimits;
        var zipLength = new FileInfo(zipPath).Length;
        if (zipLength > limits.MaxZipFileBytes)
        {
            throw new InvalidOperationException($"Module ZIP is too large. Maximum ZIP size is {limits.MaxZipFileBytes} bytes.");
        }

        var extractionRootPath = Path.GetFullPath(extractionRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        var totalExtractedBytes = 0L;
        var entryCount = 0;

        foreach (var entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > limits.MaxEntries)
            {
                throw new InvalidOperationException($"Module ZIP contains too many entries. Maximum entry count is {limits.MaxEntries}.");
            }

            ValidateEntryName(entry.FullName);
            ArchiveExtractionPath.RejectLink(entry);
            _ = ArchiveExtractionPath.Resolve(extractionRootPath, entry.FullName, allowRoot: IsDirectoryEntry(entry));

            if (!IsDirectoryEntry(entry))
            {
                totalExtractedBytes += entry.Length;
                if (totalExtractedBytes > limits.MaxExtractedBytes)
                {
                    throw new InvalidOperationException($"Module ZIP extracts to too much data. Maximum extracted size is {limits.MaxExtractedBytes} bytes.");
                }
            }
        }
    }

    private static void ValidateEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new InvalidOperationException("Module ZIP contains an empty entry name.");
        }

        if (Path.IsPathRooted(entryName))
        {
            throw new InvalidOperationException($"Module ZIP entry must not use an absolute path: {entryName}");
        }

        if (entryName.Length >= 2 && char.IsLetter(entryName[0]) && entryName[1] == ':')
        {
            throw new InvalidOperationException($"Module ZIP entry must not use a drive-qualified path: {entryName}");
        }

        if (entryName.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Module ZIP entry must not use a UNC path: {entryName}");
        }

        var pathParts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Any(part => string.Equals(part, "..", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Module ZIP entry must not contain path traversal: {entryName}");
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
            entry.FullName.EndsWith("\\", StringComparison.Ordinal);
    }
}

public sealed record ModuleZipImportLimits(
    long MaxZipFileBytes,
    long MaxExtractedBytes,
    int MaxEntries);
