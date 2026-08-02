using System.IO;
using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Modules;

public static class ModuleImportPathPlanner
{
    private static readonly Regex SafeModuleIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    public static string GetModulePath(string root, string moduleId)
    {
        var safeModuleId = NormalizeModuleId(moduleId);
        return Path.Combine(root, safeModuleId);
    }

    public static string GetAvailableModulePath(string root, string moduleId, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        var safeModuleId = NormalizeModuleId(moduleId);
        var basePath = Path.Combine(root, safeModuleId);
        if (!directoryExists(basePath))
        {
            return basePath;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(root, $"{safeModuleId}-{index}");
            if (!directoryExists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string GetAvailableFilePath(string root, string fileName, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var safeFileName = NormalizeFileName(fileName);
        var basePath = Path.Combine(root, safeFileName);
        if (!fileExists(basePath))
        {
            return basePath;
        }

        var name = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(root, $"{name}-{index}{extension}");
            if (!fileExists(candidate))
            {
                return candidate;
            }
        }
    }

    public static bool IsPathInsideDirectory(string root, string path)
    {
        var rootPath = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModuleId(string moduleId)
    {
        var trimmed = moduleId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !SafeModuleIdPattern.IsMatch(trimmed))
        {
            throw new InvalidOperationException("Module id must use only letters, numbers, dots, underscores, or hyphens, and cannot start with punctuation.");
        }

        return trimmed;
    }

    private static string NormalizeFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeFileName) ||
            !string.Equals(safeFileName, fileName.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("File name must not include a directory path.");
        }

        return safeFileName;
    }
}
