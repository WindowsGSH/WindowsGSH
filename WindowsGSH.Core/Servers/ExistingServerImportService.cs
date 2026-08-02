using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ExistingServerImportService
{
    private readonly Action<string, string, IProgress<ExistingServerImportProgress>?> _copyDirectory;
    private readonly string _serversBasePath;

    public ExistingServerImportService()
        : this(CopyDirectory, serversBasePath: null)
    {
    }

    public ExistingServerImportService(string serversBasePath)
        : this(CopyDirectory, serversBasePath)
    {
    }

    public ExistingServerImportService(Action<string, string, IProgress<ExistingServerImportProgress>?> copyDirectory, string? serversBasePath = null)
    {
        _copyDirectory = copyDirectory ?? throw new ArgumentNullException(nameof(copyDirectory));
        _serversBasePath = string.IsNullOrWhiteSpace(serversBasePath)
            ? AppPaths.GetPath("servers")
            : Path.GetFullPath(serversBasePath);
    }

    public async Task<ExistingServerImportPreview> PreviewAsync(
        IGameServerModule module,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var capability = GetCapability(module);
        var source = ValidateSourcePath(sourcePath);
        if (!capability.CanImport(source))
        {
            throw new InvalidOperationException($"The selected folder does not look importable by {module.Name}.");
        }

        var probe = await capability.PreviewImportAsync(source, cancellationToken).ConfigureAwait(false);
        var installPath = ResolveProbeInstallPath(source, probe.InstallPath);

        if (!Directory.Exists(installPath))
        {
            throw new InvalidOperationException($"Import install path does not exist: {installPath}");
        }

        var instanceProbe = new ServerInstance(
            Id: Path.GetFileName(source),
            Name: string.IsNullOrWhiteSpace(probe.SourceName) ? module.Name : probe.SourceName,
            ModuleId: module.Id,
            ServerFolder: source,
            InstallPath: installPath,
            ConfigPath: Path.Combine(source, "ServerConfig.json"),
            Settings: probe.Settings);
        var fields = module.GetConfigFields();
        var rows = fields.Select(field => CreateRow(field, probe.Settings)).ToArray();
        var warnings = probe.Warnings.ToArray();

        return new ExistingServerImportPreview(
            ModuleId: module.Id,
            ModuleName: module.Name,
            SourceName: string.IsNullOrWhiteSpace(probe.SourceName) ? Path.GetFileName(source) : probe.SourceName,
            SourcePath: source,
            InstallPath: installPath,
            Rows: rows,
            Warnings: warnings);
    }

    public ExistingServerImportResult Import(
        IGameServerModule module,
        ExistingServerImportPreview preview,
        IEnumerable<ExistingServerImportFieldValue> fieldValues,
        ExistingServerImportMode mode,
        IProgress<ExistingServerImportProgress>? progress = null)
    {
        _ = GetCapability(module);
        EnsurePreviewMatchesModule(module, preview.ModuleId);
        var settings = BuildSettings(module.GetConfigFields(), fieldValues);
        ConfigFieldValidationService.ValidateSettings(module.GetConfigFields(), settings);

        var serverId = GetNextServerId();
        var serverFolder = Path.Combine(_serversBasePath, serverId);
        if (Directory.Exists(serverFolder))
        {
            throw new InvalidOperationException($"Target server folder already exists: {serverFolder}");
        }

        var installPath = mode == ExistingServerImportMode.Copy
            ? Path.Combine(serverFolder, "files")
            : preview.InstallPath;
        if (mode == ExistingServerImportMode.Copy)
        {
            EnsureSafeCopyPath(preview.InstallPath, installPath);
        }
        var serverFolderCreated = false;

        try
        {
            Directory.CreateDirectory(serverFolder);
            serverFolderCreated = true;
            if (mode == ExistingServerImportMode.Copy)
            {
                _copyDirectory(preview.InstallPath, installPath, progress);
            }

            var serverName = module.GetServerName(settings);
            var configPath = Path.Combine(serverFolder, "ServerConfig.json");
            var instance = new ServerInstance(
                serverId,
                string.IsNullOrWhiteSpace(serverName) ? $"{module.Name} {serverId}" : serverName,
                module.Id,
                serverFolder,
                installPath,
                configPath,
                settings);
            if (!module.IsInstallValid(instance))
            {
                throw new InvalidOperationException($"The imported install path is not valid for {module.Name}.");
            }

            WriteServerConfig(module, instance, mode);
            AtomicFile.WriteAllText(
                Path.Combine(serverFolder, "import.json"),
                JsonSerializer.Serialize(CreateImportMetadata(module, preview, mode, installPath), new JsonSerializerOptions { WriteIndented = true }));

            return new ExistingServerImportResult(serverId, instance.Name, serverFolder, installPath, configPath, settings);
        }
        catch
        {
            if (serverFolderCreated)
            {
                DeleteCreatedServerFolder(serverFolder);
            }

            throw;
        }
    }

    private static IModuleExistingServerImportCapability GetCapability(IGameServerModule module)
    {
        return module as IModuleExistingServerImportCapability
            ?? throw new InvalidOperationException($"{module.Name} does not support generic existing-server imports.");
    }

    private static string ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new InvalidOperationException("Select an existing server folder.");
        }

        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"Existing server folder was not found: {source}");
        }

        return source;
    }

    private static ExistingServerImportRow CreateRow(
        ConfigFieldDefinition field,
        IReadOnlyDictionary<string, object?> importedSettings)
    {
        var imported = importedSettings.TryGetValue(field.Key, out var importedValue) ? importedValue : null;
        var value = imported ?? field.DefaultValue;
        var source = imported == null ? field.DefaultValue == null ? "Missing" : "Module default" : "Existing server";
        var status = imported == null
            ? field.Required && IsBlank(value) ? ExistingServerImportFieldStatus.Missing : ExistingServerImportFieldStatus.Defaulted
            : ExistingServerImportFieldStatus.Imported;

        return new ExistingServerImportRow(
            field.Key,
            field.Label,
            field.Type.ToString(),
            field.Required,
            value?.ToString() ?? "",
            source,
            status);
    }

    private static Dictionary<string, object?> BuildSettings(
        IEnumerable<ConfigFieldDefinition> fields,
        IEnumerable<ExistingServerImportFieldValue> values)
    {
        var fieldMap = fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!fieldMap.TryGetValue(value.Key, out var field))
            {
                continue;
            }

            settings[value.Key] = ImportFieldValueConverter.ConvertValue(field, value.Value);
        }

        return settings;
    }

    private static string ResolveProbeInstallPath(string source, string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return source;
        }

        var path = installPath.Trim();
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(source, path));
    }

    private static void EnsurePreviewMatchesModule(IGameServerModule module, string previewModuleId)
    {
        if (!string.Equals(previewModuleId, module.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Import preview was created for module '{previewModuleId}', not '{module.Id}'. Scan the folder again with the selected module.");
        }
    }

    private static void EnsureSafeCopyPath(string source, string target)
    {
        var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) ||
            ModuleImportPathPlanner.IsPathInsideDirectory(sourcePath, targetPath) ||
            ModuleImportPathPlanner.IsPathInsideDirectory(targetPath, sourcePath))
        {
            throw new InvalidOperationException("Copy target must not be inside the source folder, and source must not be inside the copy target.");
        }
    }

    private string GetNextServerId()
    {
        Directory.CreateDirectory(_serversBasePath);
        var usedIds = Directory.EnumerateDirectories(_serversBasePath)
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        var nextId = 1;
        while (usedIds.Contains(nextId))
        {
            nextId++;
        }

        return nextId.ToString();
    }

    private static void WriteServerConfig(IGameServerModule module, ServerInstance instance, ExistingServerImportMode mode)
    {
        var steamInstall = module.GetSteamInstall();
        var config = JsonSerializer.SerializeToNode(new
        {
            id = instance.Id,
            name = instance.Name,
            moduleId = module.Id,
            runtime = "native",
            installPath = mode == ExistingServerImportMode.Copy ? "files" : instance.InstallPath,
            steam = steamInstall == null
                ? null
                : new
                {
                    appId = steamInstall.AppId,
                    branch = string.Empty
                },
            settings = instance.Settings
        })?.AsObject() ?? [];

        new ServerSecretStore().ProtectConfigSecrets(module, instance.ServerFolder, config);
        AtomicFile.WriteAllText(instance.ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ExistingServerImportMetadata CreateImportMetadata(
        IGameServerModule module,
        ExistingServerImportPreview preview,
        ExistingServerImportMode mode,
        string installPath)
    {
        return new ExistingServerImportMetadata(
            SourceType: "existing-server",
            Mode: mode.ToString(),
            ImportedFrom: "Existing server folder",
            ModuleId: module.Id,
            ModuleName: module.Name,
            ImportMode: mode.ToString(),
            SourcePathHint: SanitizePathHint(preview.SourcePath),
            SourceInstallPathHint: SanitizePathHint(preview.InstallPath),
            CopiedOrAdoptedInstallPath: installPath,
            SourceLeftUnchanged: mode == ExistingServerImportMode.Copy,
            ImportedUtc: DateTimeOffset.UtcNow,
            Warnings: preview.Warnings.Select(SanitizeImportMetadataText).ToArray());
    }

    private static string SanitizePathHint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return string.IsNullOrWhiteSpace(name) ? "[root]" : name;
    }

    private static string SanitizeImportMetadataText(string value)
    {
        var text = value;
        foreach (var marker in new[] { "password", "token", "gslt", "secret" })
        {
            text = RedactAfterMarker(text, marker);
        }

        return text;
    }

    private static string RedactAfterMarker(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var cursor = index + marker.Length;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor < value.Length && value[cursor] is '"' or '\'')
            {
                cursor++;
                while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
                {
                    cursor++;
                }
            }

            if (cursor >= value.Length || value[cursor] is not (':' or '='))
            {
                index = value.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            cursor++;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            var quote = cursor < value.Length && value[cursor] is '"' or '\'' ? value[cursor++] : '\0';
            var end = cursor;
            while (end < value.Length && ShouldContinueSecretValue(value[end], quote))
            {
                end++;
            }

            value = value[..cursor] + "[redacted]" + value[end..];
            index = value.IndexOf(marker, cursor + "[redacted]".Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static bool ShouldContinueSecretValue(char value, char quote)
    {
        if (quote != '\0')
        {
            return value != quote;
        }

        return !char.IsWhiteSpace(value) &&
            value is not (';' or ',' or '}' or ']');
    }

    private static bool IsBlank(object? value)
    {
        return string.IsNullOrWhiteSpace(value?.ToString());
    }

    private static void CopyDirectory(string source, string target, IProgress<ExistingServerImportProgress>? progress = null)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();
        var totalBytes = files.Sum(file => file.Length);
        var copiedBytes = 0L;
        var copiedFiles = 0;

        progress?.Report(new ExistingServerImportProgress(
            Phase: "Preparing copy",
            CurrentPath: "",
            FilesCopied: copiedFiles,
            TotalFiles: files.Length,
            BytesCopied: copiedBytes,
            TotalBytes: totalBytes));

        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(source, file.FullName);
            var destination = Path.Combine(target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.FullName, destination, overwrite: false);
            copiedFiles++;
            copiedBytes += file.Length;
            progress?.Report(new ExistingServerImportProgress("Copying files", relativePath, copiedFiles, files.Length, copiedBytes, totalBytes));
        }

        progress?.Report(new ExistingServerImportProgress(
            Phase: "Copy complete",
            CurrentPath: "",
            FilesCopied: copiedFiles,
            TotalFiles: files.Length,
            BytesCopied: copiedBytes,
            TotalBytes: totalBytes));
    }

    private void DeleteCreatedServerFolder(string serverFolder)
    {
        try
        {
            var serversPath = Path.GetFullPath(_serversBasePath);
            var fullServerFolder = Path.GetFullPath(serverFolder);
            if (!ModuleImportPathPlanner.IsPathInsideDirectory(serversPath, fullServerFolder))
            {
                return;
            }

            Directory.Delete(fullServerFolder, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ExistingServerImportMetadata(
        [property: JsonPropertyName("sourceType")] string SourceType,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("importedFrom")] string ImportedFrom,
        [property: JsonPropertyName("moduleId")] string ModuleId,
        [property: JsonPropertyName("moduleName")] string ModuleName,
        [property: JsonPropertyName("importMode")] string ImportMode,
        [property: JsonPropertyName("sourcePathHint")] string SourcePathHint,
        [property: JsonPropertyName("sourceInstallPathHint")] string SourceInstallPathHint,
        [property: JsonPropertyName("copiedOrAdoptedInstallPath")] string CopiedOrAdoptedInstallPath,
        [property: JsonPropertyName("sourceLeftUnchanged")] bool SourceLeftUnchanged,
        [property: JsonPropertyName("importedUtc")] DateTimeOffset ImportedUtc,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);
}

public sealed record ExistingServerImportPreview(
    string ModuleId,
    string ModuleName,
    string SourceName,
    string SourcePath,
    string InstallPath,
    IReadOnlyList<ExistingServerImportRow> Rows,
    IReadOnlyList<string> Warnings);

public sealed record ExistingServerImportRow(
    string Key,
    string Label,
    string Type,
    bool Required,
    string Value,
    string Source,
    ExistingServerImportFieldStatus Status);

public sealed record ExistingServerImportFieldValue(string Key, string Value);

public sealed record ExistingServerImportResult(
    string ServerId,
    string ServerName,
    string ServerFolder,
    string InstallPath,
    string ConfigPath,
    IReadOnlyDictionary<string, object?> Settings);

public sealed record ExistingServerImportProgress(
    string Phase,
    string CurrentPath,
    int FilesCopied,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes);

public enum ExistingServerImportMode
{
    Copy,
    Adopt
}

public enum ExistingServerImportFieldStatus
{
    Imported,
    Defaulted,
    Missing,
    Review
}
