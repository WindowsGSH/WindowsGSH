using System.IO.Compression;

namespace WindowsGSH.Core.Security;

public static class ArchiveExtractionPath
{
    public static string Resolve(string extractionRoot, string entryName, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new InvalidDataException("Archive contains a blank entry name.");
        }

        if (IsRootedOnWindows(entryName))
        {
            throw new InvalidDataException($"Archive entry uses a rooted path: {entryName}");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionRoot));
        var normalizedName = entryName.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        // The rooted-name guard above is the trust boundary. Path.Join also
        // cannot discard root if this method is changed without that guard.
        var candidate = Path.GetFullPath(Path.Join(root, normalizedName));
        var inside = string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inside || (!allowRoot && string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Archive entry escapes the extraction directory: {entryName}");
        }

        EnsureNoReparsePoint(root, candidate);
        return candidate;
    }

    public static void RejectLink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixFileType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Archive entry is a link or reparse point: {entry.FullName}");
        }
    }

    private static bool IsRootedOnWindows(string entryName)
    {
        return Path.IsPathRooted(entryName) ||
            entryName.StartsWith('/') ||
            entryName.StartsWith('\\') ||
            (entryName.Length >= 2 && char.IsLetter(entryName[0]) && entryName[1] == ':');
    }

    private static void EnsureNoReparsePoint(string root, string candidate)
    {
        for (var current = candidate; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"Archive extraction path contains a reparse point: {current}");
            }

            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new InvalidDataException("Archive extraction path is not rooted in the destination directory.");
    }
}
