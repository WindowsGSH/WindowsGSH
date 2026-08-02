using System.IO;
using System.IO.Compression;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleImportService
{
    private readonly ModuleProvenanceService _provenanceService = new();

    public IReadOnlyList<ModuleImportCandidate> GetInstalledModules()
    {
        ModuleStoragePaths.EnsureCreated();
        return EnumerateModuleDirectories(ModuleStoragePaths.InstalledModules).ToArray();
    }

    public IReadOnlyList<ModuleImportCandidate> GetDisabledModules()
    {
        ModuleStoragePaths.EnsureCreated();
        return EnumerateModuleDirectories(ModuleStoragePaths.DisabledModules).ToArray();
    }

    public ModuleImportCandidate ImportFromFolder(string folderPath)
    {
        ModuleStoragePaths.EnsureCreated();
        var moduleJsonPath = FindModuleJson(folderPath)
            ?? throw new InvalidOperationException("Selected folder does not contain a module.json file.");
        var sourceFolder = Path.GetDirectoryName(moduleJsonPath)!;

        var module = JsonGameServerModule.Load(moduleJsonPath, warning => LogValidationWarning(sourceFolder, warning));
        EnsureModuleIdAvailable(module.Id);
        ValidateModule(sourceFolder);
        var targetPath = Path.Combine(ModuleStoragePaths.InstalledModules, module.Id);
        CopyDirectory(sourceFolder, targetPath);
        WriteImportMetadata(targetPath, "windowsgsh-module-folder", Path.GetFileName(sourceFolder));
        return ToCandidate(targetPath, Enabled: true);
    }

    public ModuleImportCandidate ImportFromZip(string zipPath)
    {
        ModuleStoragePaths.EnsureCreated();
        var inboxPath = Path.Combine(ModuleStoragePaths.ImportInbox, Path.GetFileName(zipPath));
        File.Copy(zipPath, inboxPath, overwrite: true);

        var extractPath = Path.Combine(ModuleStoragePaths.ImportInbox, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractPath);
        try
        {
            ModuleZipImportValidator.Validate(inboxPath, extractPath);
            ZipFile.ExtractToDirectory(inboxPath, extractPath, overwriteFiles: true);
            var moduleJsonPath = FindModuleJson(extractPath)
                ?? throw new InvalidOperationException("ZIP does not contain a module.json file.");
            var sourceFolder = Path.GetDirectoryName(moduleJsonPath)!;
            var module = JsonGameServerModule.Load(moduleJsonPath, warning => LogValidationWarning(sourceFolder, warning));
            EnsureModuleIdAvailable(module.Id);
            ValidateModule(sourceFolder);
            var targetPath = Path.Combine(ModuleStoragePaths.InstalledModules, module.Id);
            CopyDirectory(sourceFolder, targetPath);
            WriteImportMetadata(targetPath, "windowsgsh-module-zip", Path.GetFileName(zipPath));
            TryMoveFileToAvailablePath(inboxPath, ModuleStoragePaths.ImportArchive, Path.GetFileName(zipPath));
            TryDeleteDirectory(extractPath);
            return ToCandidate(targetPath, Enabled: true);
        }
        catch
        {
            TryMoveFileToAvailablePath(inboxPath, ModuleStoragePaths.ImportRejected, Path.GetFileName(zipPath));
            TryDeleteDirectory(extractPath);
            throw;
        }
    }

    public void Disable(string moduleId)
    {
        MoveModule(moduleId, ModuleStoragePaths.InstalledModules, ModuleStoragePaths.DisabledModules);
    }

    public void Enable(string moduleId)
    {
        MoveModule(moduleId, ModuleStoragePaths.DisabledModules, ModuleStoragePaths.InstalledModules);
    }

    public void DeleteDisabled(string moduleId)
    {
        DeleteModule(moduleId, ModuleStoragePaths.DisabledModules);
    }

    /// <summary>
    /// Re-validates a module whose hash has changed since import and, if valid, refreshes
    /// the stored hash so the module can load again. Use when the user explicitly confirms
    /// they trust the updated module files.
    /// </summary>
    public ModuleImportCandidate ReTrustModule(string moduleId)
    {
        var installedPath = ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.InstalledModules, moduleId);
        var disabledPath = ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.DisabledModules, moduleId);

        string modulePath;
        bool enabled;
        if (Directory.Exists(installedPath))
        {
            modulePath = installedPath;
            enabled = true;
        }
        else if (Directory.Exists(disabledPath))
        {
            modulePath = disabledPath;
            enabled = false;
        }
        else
        {
            throw new InvalidOperationException($"Module not found: {moduleId}");
        }

        ValidateModule(modulePath);
        WriteImportMetadata(modulePath, "windowsgsh-retrust", Path.GetFileName(modulePath));
        return ToCandidate(modulePath, enabled);
    }

    public ModuleImportCandidate UpdateFromZip(string moduleId, string zipPath, string? fallbackSourceUrl = null)
    {
        ModuleStoragePaths.EnsureCreated();
        var existingPath = FindExistingModulePath(moduleId, out var enabled);
        fallbackSourceUrl = FirstNonBlank(fallbackSourceUrl, _provenanceService.GetSnapshot(existingPath).Metadata?.SourceUrl);

        var inboxPath = Path.Combine(ModuleStoragePaths.ImportInbox, Path.GetFileName(zipPath));
        File.Copy(zipPath, inboxPath, overwrite: true);

        var extractPath = Path.Combine(ModuleStoragePaths.ImportInbox, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractPath);
        try
        {
            ModuleZipImportValidator.Validate(inboxPath, extractPath);
            ZipFile.ExtractToDirectory(inboxPath, extractPath, overwriteFiles: true);
            var moduleJsonPath = FindModuleJson(extractPath)
                ?? throw new InvalidOperationException("ZIP does not contain a module.json file.");
            var sourceFolder = Path.GetDirectoryName(moduleJsonPath)!;
            var module = JsonGameServerModule.Load(moduleJsonPath, warning => LogValidationWarning(sourceFolder, warning));
            if (!string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Module ID mismatch: expected '{moduleId}' but got '{module.Id}'. The ZIP must contain the same module.");
            }

            ValidateModule(sourceFolder);
            using var replacement = ReplaceModuleDirectory(existingPath, sourceFolder);
            WriteImportMetadata(existingPath, "windowsgsh-module-update-zip", Path.GetFileName(zipPath), fallbackSourceUrl);
            var updated = ToCandidate(existingPath, enabled);
            replacement.Commit();
            TryMoveFileToAvailablePath(inboxPath, ModuleStoragePaths.ImportArchive, Path.GetFileName(zipPath));
            TryDeleteDirectory(extractPath);
            return updated;
        }
        catch
        {
            TryMoveFileToAvailablePath(inboxPath, ModuleStoragePaths.ImportRejected, Path.GetFileName(zipPath));
            TryDeleteDirectory(extractPath);
            throw;
        }
    }

    public ModuleImportCandidate UpdateFromFolder(string moduleId, string folderPath, string? fallbackSourceUrl = null)
    {
        ModuleStoragePaths.EnsureCreated();
        var existingPath = FindExistingModulePath(moduleId, out var enabled);
        fallbackSourceUrl = FirstNonBlank(fallbackSourceUrl, _provenanceService.GetSnapshot(existingPath).Metadata?.SourceUrl);

        var moduleJsonPath = FindModuleJson(folderPath)
            ?? throw new InvalidOperationException("Selected folder does not contain a module.json file.");
        var sourceFolder = Path.GetDirectoryName(moduleJsonPath)!;
        var module = JsonGameServerModule.Load(moduleJsonPath, warning => LogValidationWarning(sourceFolder, warning));
        if (!string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Module ID mismatch: expected '{moduleId}' but got '{module.Id}'. The folder must contain the same module.");
        }

        ValidateModule(sourceFolder);
        using var replacement = ReplaceModuleDirectory(existingPath, sourceFolder);
        WriteImportMetadata(existingPath, "windowsgsh-module-update-folder", Path.GetFileName(sourceFolder), fallbackSourceUrl);
        var updated = ToCandidate(existingPath, enabled);
        replacement.Commit();
        return updated;
    }

    private static IEnumerable<ModuleImportCandidate> EnumerateModuleDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var moduleJsonPath = Path.Combine(directory, "module.json");
            if (!File.Exists(moduleJsonPath))
            {
                continue;
            }

            try
            {
                _ = ModuleManifest.Read(moduleJsonPath);
            }
            catch
            {
                continue;
            }

            ModuleImportCandidate candidate;
            try
            {
                candidate = ToCandidate(
                    directory,
                    Enabled: string.Equals(Path.GetFullPath(root), Path.GetFullPath(ModuleStoragePaths.InstalledModules), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (IsTransientModuleEnumerationException(ex))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static ModuleImportCandidate ToCandidate(string modulePath, bool Enabled)
    {
        var manifest = ModuleManifest.Read(Path.Combine(modulePath, "module.json"));
        var compatibility = ModuleCompatibility.Evaluate(manifest);
        var hasCSharp = Directory.EnumerateFiles(modulePath, "*.cs", SearchOption.AllDirectories).Any();
        var moduleType = hasCSharp ? "C# module" : "JSON module";
        var provenance = new ModuleProvenanceService().GetSnapshot(modulePath);
        var manifestProvenance = ModuleProvenanceService.ReadManifestProvenance(Path.Combine(modulePath, "module.json"));

        return new ModuleImportCandidate(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            provenance.Metadata?.Description ?? manifestProvenance.Description,
            provenance.Metadata?.Author ?? manifestProvenance.Author,
            provenance.Metadata?.Url ?? manifestProvenance.Url,
            provenance.Metadata?.Color ?? manifestProvenance.Color,
            FindModuleImage(modulePath, "author"),
            modulePath,
            Enabled,
            moduleType,
            provenance.CurrentHash,
            provenance.Metadata?.ImportedUtc,
            provenance.Metadata?.SourceType ?? "unknown",
            provenance.Metadata?.OriginalFileName,
            provenance.Metadata?.Homepage,
            provenance.Metadata?.Repository,
            provenance.Metadata?.SourceUrl,
            provenance.HasChangedSinceImport,
            provenance.Warning ?? GetDefaultWarning(hasCSharp),
            compatibility.Status,
            ModuleCompatibility.ToDisplayText(compatibility),
            compatibility.ModuleApiVersion ?? provenance.Metadata?.ModuleApiVersion,
            compatibility.CurrentModuleApiVersion,
            compatibility.CurrentWindowsGshVersion);
    }

    private static void MoveModule(string moduleId, string sourceRoot, string targetRoot)
    {
        var sourcePath = ModuleImportPathPlanner.GetModulePath(sourceRoot, moduleId);
        if (!Directory.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Module not found: {moduleId}");
        }

        var targetPath = GetAvailableModulePath(targetRoot, moduleId);
        Directory.CreateDirectory(targetRoot);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!ModuleImportPathPlanner.IsPathInsideDirectory(sourceRoot, fullSourcePath) ||
            !ModuleImportPathPlanner.IsPathInsideDirectory(targetRoot, fullTargetPath))
        {
            throw new InvalidOperationException("Module move path is outside the expected module folders.");
        }

        MoveDirectoryWithFallback(fullSourcePath, fullTargetPath);
    }

    private static void DeleteModule(string moduleId, string root)
    {
        var modulePath = ModuleImportPathPlanner.GetModulePath(root, moduleId);
        if (!Directory.Exists(modulePath))
        {
            throw new InvalidOperationException($"Module not found: {moduleId}");
        }

        var fullModulePath = Path.GetFullPath(modulePath);
        if (!ModuleImportPathPlanner.IsPathInsideDirectory(root, fullModulePath))
        {
            throw new InvalidOperationException("Module delete path is outside the disabled modules folder.");
        }

        DeleteDirectoryWithRetries(fullModulePath);
    }

    private static void EnsureModuleIdAvailable(string moduleId)
    {
        if (Directory.Exists(ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.InstalledModules, moduleId)) ||
            Directory.Exists(ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.DisabledModules, moduleId)))
        {
            throw new InvalidOperationException($"Module already exists: {moduleId}");
        }
    }

    private static string FindExistingModulePath(string moduleId, out bool enabled)
    {
        var installedPath = ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.InstalledModules, moduleId);
        var disabledPath = ModuleImportPathPlanner.GetModulePath(ModuleStoragePaths.DisabledModules, moduleId);

        if (Directory.Exists(installedPath))
        {
            enabled = true;
            return installedPath;
        }

        if (Directory.Exists(disabledPath))
        {
            enabled = false;
            return disabledPath;
        }

        throw new InvalidOperationException($"Module not found: {moduleId}");
    }

    private static ModuleUpdateTransaction ReplaceModuleDirectory(string targetPath, string sourcePath)
    {
        var backupRoot = Path.Combine(ModuleStoragePaths.ImportRoot, "update-backups");
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(
            backupRoot,
            $"{Path.GetFileName(targetPath)}.{DateTime.UtcNow.Ticks}.{Guid.NewGuid():N}");
        MoveDirectoryWithFallback(targetPath, backupPath);
        try
        {
            CopyDirectory(sourcePath, targetPath);
            return new ModuleUpdateTransaction(targetPath, backupPath);
        }
        catch
        {
            TryDeleteDirectory(targetPath);
            try
            {
                MoveDirectoryWithFallback(backupPath, targetPath);
            }
            catch
            {
            }
            throw;
        }
    }

    private static void ValidateModule(string modulePath)
    {
        if (!Directory.EnumerateFiles(modulePath, "*.cs", SearchOption.AllDirectories).Any())
        {
            return;
        }

        try
        {
            _ = new CSharpModuleLoader().Load(modulePath, warning => LogValidationWarning(modulePath, warning));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"C# module validation failed: {ex.Message}", ex);
        }
    }

    public static string? ResolveModuleFolder(string rootPath)
    {
        var moduleJsonPath = FindModuleJson(rootPath);
        return moduleJsonPath == null ? null : Path.GetDirectoryName(moduleJsonPath);
    }

    private static string? FindModuleJson(string root)
    {
        var rootModuleJson = Path.Combine(root, "module.json");
        return File.Exists(rootModuleJson)
            ? rootModuleJson
            : Directory.EnumerateFiles(root, "module.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (ShouldSkipModuleCopyPath(sourcePath, file))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourcePath, file);
            var targetFile = Path.Combine(targetPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private static void MoveDirectoryWithFallback(string sourcePath, string targetPath)
    {
        NormalizeAttributes(sourcePath);
        try
        {
            RetryFileSystemOperation(() => Directory.Move(sourcePath, targetPath));
            return;
        }
        catch (Exception ex) when (IsRetriableFileSystemException(ex))
        {
            try
            {
                CopyDirectory(sourcePath, targetPath);
                DeleteDirectoryWithRetries(sourcePath);
            }
            catch (Exception fallbackEx) when (IsRetriableFileSystemException(fallbackEx))
            {
                TryDeleteDirectory(targetPath);
                throw new InvalidOperationException(
                    $"Windows could not move module folder '{sourcePath}'. Close any running servers or tools using that module folder, then try again. Details: {fallbackEx.Message}",
                    fallbackEx);
            }
        }
    }

    private static void DeleteDirectoryWithRetries(string path)
    {
        NormalizeAttributes(path);
        RetryFileSystemOperation(() => Directory.Delete(path, recursive: true));
    }

    private static void NormalizeAttributes(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Directory);
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Directory);
    }

    private static void RetryFileSystemOperation(Action action)
    {
        const int Attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && IsRetriableFileSystemException(ex))
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    private static bool IsRetriableFileSystemException(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private static bool IsTransientModuleEnumerationException(Exception ex)
    {
        return ex is DirectoryNotFoundException or FileNotFoundException;
    }

    private static bool ShouldSkipModuleCopyPath(string sourcePath, string path)
    {
        var relativePath = Path.GetRelativePath(sourcePath, path);
        var parts = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part =>
            string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindModuleImage(string modulePath, string baseName)
    {
        string[] supportedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
        return Directory.EnumerateFiles(modulePath)
            .FirstOrDefault(path =>
                supportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetAvailableModulePath(string root, string moduleId)
    {
        Directory.CreateDirectory(root);
        return ModuleImportPathPlanner.GetAvailableModulePath(root, moduleId, Directory.Exists);
    }

    private static string GetAvailableFilePath(string root, string fileName)
    {
        Directory.CreateDirectory(root);
        return ModuleImportPathPlanner.GetAvailableFilePath(root, fileName, File.Exists);
    }

    private static void TryMoveFileToAvailablePath(string sourcePath, string targetRoot, string fileName)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            File.Move(sourcePath, GetAvailableFilePath(targetRoot, fileName));
        }
        catch
        {
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

    private void WriteImportMetadata(
        string modulePath,
        string sourceType,
        string originalFileName,
        string? fallbackSourceUrl = null)
    {
        var manifestProvenance = ModuleProvenanceService.ReadManifestProvenance(Path.Combine(modulePath, "module.json"));
        if (string.IsNullOrWhiteSpace(manifestProvenance.SourceUrl) &&
            !string.IsNullOrWhiteSpace(fallbackSourceUrl))
        {
            manifestProvenance = manifestProvenance with { SourceUrl = fallbackSourceUrl };
        }

        _provenanceService.WriteImportMetadata(
            modulePath,
            sourceType,
            originalFileName,
            manifestProvenance);
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    public static string GetDefaultWarning(bool hasCSharp)
    {
        return hasCSharp
            ? "C# modules run code on this machine. WindowsGSH does not review or guarantee third-party modules."
            : "WindowsGSH does not review or guarantee third-party modules.";
    }

    private static void LogValidationWarning(string modulePath, ModuleValidationMessage warning)
    {
        ModuleValidationWarningLog.AddOnce(modulePath, warning);
    }

    private sealed class ModuleUpdateTransaction : IDisposable
    {
        private readonly string _targetPath;
        private readonly string _backupPath;
        private bool _committed;

        public ModuleUpdateTransaction(string targetPath, string backupPath)
        {
            _targetPath = targetPath;
            _backupPath = backupPath;
        }

        public void Commit()
        {
            _committed = true;
            TryDeleteDirectory(_backupPath);
        }

        public void Dispose()
        {
            if (_committed)
            {
                return;
            }

            TryDeleteDirectory(_targetPath);
            if (Directory.Exists(_backupPath))
            {
                MoveDirectoryWithFallback(_backupPath, _targetPath);
            }
        }
    }
}

public sealed record ModuleImportCandidate(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    string? Url,
    string? Color,
    string? AuthorImagePath,
    string Path,
    bool Enabled,
    string ModuleType,
    string CurrentHash,
    DateTimeOffset? ImportedUtc,
    string SourceType,
    string? OriginalFileName,
    string? Homepage,
    string? Repository,
    string? SourceUrl,
    bool HasChangedSinceImport,
    string Warning,
    ModuleCompatibilityStatus CompatibilityStatus,
    string CompatibilityText,
    string? ModuleApiVersion,
    string CurrentModuleApiVersion,
    string CurrentWindowsGshVersion)
{
    private const string DefaultColor = "#2F7BDF";

    public string ShortHash => CurrentHash.Length <= 12 ? CurrentHash : CurrentHash[..12];

    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);

    public bool HasNoSourceUrl => string.IsNullOrWhiteSpace(SourceUrl);

    public string DisplayAuthor => string.IsNullOrWhiteSpace(Author) ? "Unknown author" : Author;

    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "No description provided." : Description;

    public string? DisplayUrl => FirstNonBlank(Url, Homepage, Repository, SourceUrl);

    public string DisplayColor => IsHexColor(Color) ? Color!.Trim() : DefaultColor;

    public string ImportedText => ImportedUtc.HasValue
        ? $"Imported {ImportedUtc.Value.LocalDateTime:g}"
        : "Import metadata missing";

    public string ChangeText => HasChangedSinceImport ? "Files changed since import" : "Files unchanged since import";

    public string ModuleApiText => string.IsNullOrWhiteSpace(ModuleApiVersion)
        ? $"Module API legacy (current {CurrentModuleApiVersion})"
        : $"Module API {ModuleApiVersion} (current {CurrentModuleApiVersion})";

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static bool IsHexColor(string? value)
    {
        var color = value?.Trim();
        if (color is not { Length: 7 or 9 } || color[0] != '#')
        {
            return false;
        }

        return color.Skip(1).All(IsHex);
    }

    private static bool IsHex(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }
}
