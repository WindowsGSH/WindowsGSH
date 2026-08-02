using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSH.Core;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Steam;

namespace WindowsGSH.Core.Servers;

public sealed class WindowsGsmServerImportService
{
    private readonly Action<string, string, IProgress<WindowsGsmServerImportProgress>?> _copyDirectory;
    private readonly string _serversBasePath;
    private readonly SteamBranchPasswordStore _branchPasswordStore = new();

    private static readonly Dictionary<string, string[]> BuiltInSettingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["servername"] = ["server.name"],
        ["serverip"] = ["network.ip", "network.publicIp"],
        ["serverport"] = ["network.port"],
        ["serverqueryport"] = ["network.queryPort"],
        ["servermap"] = ["server.map"],
        ["servermaxplayer"] = ["server.maxPlayers"],
        ["servergslt"] = ["server.gslt"],
        ["serverparam"] = ["server.additionalArguments"],
        ["rconip"] = ["rcon.ip"],
        ["rconport"] = ["rcon.port"],
        ["rconpassword"] = ["rcon.password"]
    };

    public WindowsGsmServerImportService()
        : this(CopyDirectory, serversBasePath: null)
    {
    }

    public WindowsGsmServerImportService(string serversBasePath)
        : this(CopyDirectory, serversBasePath)
    {
    }

    public WindowsGsmServerImportService(Action<string, string> copyDirectory)
        : this(copyDirectory, serversBasePath: null)
    {
    }

    public WindowsGsmServerImportService(Action<string, string> copyDirectory, string? serversBasePath)
        : this((source, target, _) => copyDirectory(source, target), serversBasePath)
    {
    }

    public WindowsGsmServerImportService(Action<string, string, IProgress<WindowsGsmServerImportProgress>?> copyDirectory)
        : this(copyDirectory, serversBasePath: null)
    {
    }

    public WindowsGsmServerImportService(Action<string, string, IProgress<WindowsGsmServerImportProgress>?> copyDirectory, string? serversBasePath)
    {
        _copyDirectory = copyDirectory ?? throw new ArgumentNullException(nameof(copyDirectory));
        _serversBasePath = string.IsNullOrWhiteSpace(serversBasePath)
            ? AppPaths.GetPath("servers")
            : Path.GetFullPath(serversBasePath);
    }

    public async Task<WindowsGsmServerImportPreview> PreviewAsync(
        IGameServerModule module,
        string windowsGsmServerFolder,
        CancellationToken cancellationToken = default)
    {
        var source = CreateSource(module, windowsGsmServerFolder);
        var windowsGsmSettings = ReadWindowsGsmConfig(source.WindowsGsmConfigPath);
        var values = new Dictionary<string, WindowsGsmImportedValue>(StringComparer.OrdinalIgnoreCase);
        var moduleFields = module.GetConfigFields().ToArray();
        var moduleFieldKeys = moduleFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (windowsGsmKey, moduleKeys) in BuiltInSettingMap)
        {
            if (!windowsGsmSettings.TryGetValue(windowsGsmKey, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var moduleKey in moduleKeys.Where(moduleFieldKeys.Contains))
            {
                values[moduleKey] = new WindowsGsmImportedValue(value, $"WindowsGSM {windowsGsmKey}", IsCertain: true);
            }
        }

        var probe = new ServerInstance(
            Id: Path.GetFileName(source.ServerFolder),
            Name: GetWindowsGsmValue(windowsGsmSettings, "servername", module.Name),
            ModuleId: module.Id,
            ServerFolder: source.ServerFolder,
            InstallPath: source.ServerFilesPath,
            ConfigPath: source.WindowsGsmConfigPath,
            Settings: values.ToDictionary(item => item.Key, item => (object?)item.Value.Value, StringComparer.OrdinalIgnoreCase));

        try
        {
            var fileSettings = await module.ReadConfigFileSettingsAsync(probe, cancellationToken).ConfigureAwait(false);
            foreach (var (key, value) in fileSettings)
            {
                if (!moduleFieldKeys.Contains(key) || value == null || string.IsNullOrWhiteSpace(value.ToString()))
                {
                    continue;
                }

                var sourceLabel = values.ContainsKey(key)
                    ? "Game config, replacing WindowsGSM value"
                    : "Game config";
                values[key] = new WindowsGsmImportedValue(value, sourceLabel, IsCertain: true);
            }
        }
        catch (Exception ex)
        {
            values["__moduleReadWarning"] = new WindowsGsmImportedValue(
                ex.Message,
                "Module config reader",
                IsCertain: false);
        }

        var rows = moduleFields
            .Select(field => CreateRow(field, values.TryGetValue(field.Key, out var found) ? found : null))
            .ToArray();

        var steamBranch = GetWindowsGsmValue(windowsGsmSettings, "steambranch", "");
        var steamBranchPassword = GetWindowsGsmValue(windowsGsmSettings, "steambeta_password", "");
        var sourceGame = GetWindowsGsmValue(windowsGsmSettings, "servergame", "");
        var warnings = new List<string>();
        var backupSettings = ReadWindowsGsmBackupSettings(source, windowsGsmSettings, warnings);
        if (values.TryGetValue("__moduleReadWarning", out var warning))
        {
            warnings.Add($"Module config read warning: {warning.Value}");
        }

        if (!string.IsNullOrWhiteSpace(sourceGame) &&
            !string.Equals(sourceGame, module.Name, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"WindowsGSM servergame is '{sourceGame}'. Confirm it matches the selected module '{module.Name}'.");
        }

        return new WindowsGsmServerImportPreview(
            ModuleId: module.Id,
            ModuleName: module.Name,
            SourceGame: sourceGame,
            SourceServerFolder: source.ServerFolder,
            SourceServerFilesPath: source.ServerFilesPath,
            SourceConfigPath: source.WindowsGsmConfigPath,
            SteamBranch: steamBranch,
            SteamBranchPassword: steamBranchPassword,
            Rows: rows,
            Warnings: warnings,
            BackupOnStart: ReadWindowsGsmBoolean(windowsGsmSettings, "backuponstart"),
            BackupDestination: backupSettings.Destination,
            BackupPaths: backupSettings.Paths);
    }

    public WindowsGsmServerImportResult Import(
        IGameServerModule module,
        WindowsGsmServerImportPreview preview,
        IEnumerable<WindowsGsmServerImportFieldValue> fieldValues,
        WindowsGsmServerImportMode mode,
        IProgress<WindowsGsmServerImportProgress>? progress = null)
    {
        EnsurePreviewMatchesModule(module, preview.ModuleId);
        var settings = BuildSettings(module.GetConfigFields(), fieldValues);
        ValidateSettings(module.GetConfigFields(), settings);

        var serverId = GetNextServerId();
        var serverFolder = Path.Combine(_serversBasePath, serverId);
        if (Directory.Exists(serverFolder))
        {
            throw new InvalidOperationException($"Target server folder already exists: {serverFolder}");
        }

        var installPath = mode == WindowsGsmServerImportMode.Copy
            ? Path.Combine(serverFolder, "files")
            : preview.SourceServerFilesPath;
        if (mode == WindowsGsmServerImportMode.Copy)
        {
            EnsureSafeCopyPath(preview.SourceServerFilesPath, installPath);
        }

        var serverFolderCreated = false;
        try
        {
            Directory.CreateDirectory(serverFolder);
            serverFolderCreated = true;

            if (mode == WindowsGsmServerImportMode.Copy)
            {
                _copyDirectory(preview.SourceServerFilesPath, installPath, progress);
            }

            var serverName = module.GetServerName(settings);
            var configPath = Path.Combine(serverFolder, "ServerConfig.json");
            var steamInstall = module.GetSteamInstall();
            var steamBranchPasswordReference = string.Empty;
            if (steamInstall != null && !string.IsNullOrWhiteSpace(preview.SteamBranchPassword))
            {
                _branchPasswordStore.Save(serverId, preview.SteamBranchPassword);
                steamBranchPasswordReference = SteamBranchPasswordStore.CreateReference(serverId);
            }

            var config = JsonSerializer.SerializeToNode(new
            {
                id = serverId,
                name = string.IsNullOrWhiteSpace(serverName) ? $"{module.Name} {serverId}" : serverName,
                moduleId = module.Id,
                runtime = "native",
                installPath = mode == WindowsGsmServerImportMode.Copy ? "files" : installPath,
                steam = steamInstall == null
                    ? null
                    : new
                    {
                        appId = steamInstall.AppId,
                        branch = string.Equals(preview.SteamBranch, "public", StringComparison.OrdinalIgnoreCase) ? "" : preview.SteamBranch,
                        branchPasswordRef = steamBranchPasswordReference
                    },
                backup = new
                {
                    backupOnStart = preview.BackupOnStart,
                    destination = preview.BackupDestination,
                    paths = preview.ImportedBackupPaths
                },
                settings
            })?.AsObject() ?? [];

            new ServerSecretStore().ProtectConfigSecrets(module, serverFolder, config);
            AtomicFile.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AtomicFile.WriteAllText(
                Path.Combine(serverFolder, "import.json"),
                JsonSerializer.Serialize(CreateImportMetadata(module, preview, mode, installPath), new JsonSerializerOptions { WriteIndented = true }));

            return new WindowsGsmServerImportResult(serverId, serverName, serverFolder, installPath, configPath, settings);
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

    private static WindowsGsmImportMetadata CreateImportMetadata(
        IGameServerModule module,
        WindowsGsmServerImportPreview preview,
        WindowsGsmServerImportMode mode,
        string installPath)
    {
        return new WindowsGsmImportMetadata(
            SourceType: "windowsgsm-server",
            Mode: mode.ToString(),
            ImportedFrom: "WindowsGSM",
            ModuleId: module.Id,
            ModuleName: module.Name,
            ImportMode: mode.ToString(),
            SourceServerFolder: preview.SourceServerFolder,
            SourceServerFilesPath: preview.SourceServerFilesPath,
            SourceConfigPath: preview.SourceConfigPath,
            SourceGame: preview.SourceGame,
            CopiedOrAdoptedInstallPath: installPath,
            SourceLeftUnchanged: mode == WindowsGsmServerImportMode.Copy,
            ImportedUtc: DateTimeOffset.UtcNow,
            Warnings: preview.Warnings.Select(SanitizeImportMetadataText).ToArray());
    }

    private static WindowsGsmImportSource CreateSource(IGameServerModule module, string windowsGsmServerFolder)
    {
        if (string.IsNullOrWhiteSpace(windowsGsmServerFolder))
        {
            throw new InvalidOperationException("Select a WindowsGSM server folder.");
        }

        var serverFolder = Path.GetFullPath(windowsGsmServerFolder);
        var configPath = Path.Combine(serverFolder, "configs", "WindowsGSM.cfg");
        var serverFilesPath = Path.Combine(serverFolder, "serverfiles");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException($"WindowsGSM.cfg was not found: {configPath}");
        }

        if (!Directory.Exists(serverFilesPath))
        {
            throw new InvalidOperationException($"WindowsGSM serverfiles folder was not found: {serverFilesPath}");
        }

        var probe = new ServerInstance(
            Path.GetFileName(serverFolder),
            module.Name,
            module.Id,
            serverFolder,
            serverFilesPath,
            configPath,
            new Dictionary<string, object?>());
        if (!module.IsInstallValid(probe))
        {
            throw new InvalidOperationException($"The selected folder does not look valid for {module.Name}. The module could not find its server executable.");
        }

        return new WindowsGsmImportSource(serverFolder, configPath, serverFilesPath);
    }

    private static WindowsGsmServerImportRow CreateRow(ConfigFieldDefinition field, WindowsGsmImportedValue? imported)
    {
        var value = imported?.Value ?? field.DefaultValue;
        var source = imported?.Source ?? (field.DefaultValue == null ? "Missing" : "Module default");
        var status = imported == null
            ? field.Required && IsBlank(value) ? WindowsGsmImportFieldStatus.Missing : WindowsGsmImportFieldStatus.Defaulted
            : imported.IsCertain ? WindowsGsmImportFieldStatus.Imported : WindowsGsmImportFieldStatus.Review;

        return new WindowsGsmServerImportRow(
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
        IEnumerable<WindowsGsmServerImportFieldValue> values)
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

    private static void ValidateSettings(IEnumerable<ConfigFieldDefinition> fields, IReadOnlyDictionary<string, object?> settings)
    {
        ConfigFieldValidationService.ValidateSettings(fields, settings);
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
            IsPathInsideDirectory(sourcePath, targetPath) ||
            IsPathInsideDirectory(targetPath, sourcePath))
        {
            throw new InvalidOperationException("Copy target must not be inside the source folder, and source must not be inside the copy target.");
        }
    }

    private static Dictionary<string, string> ReadWindowsGsmConfig(string configPath)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            settings[key] = Unquote(value);
        }

        return settings;
    }

    private static string GetWindowsGsmValue(IReadOnlyDictionary<string, string> settings, string key, string fallback)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static WindowsGsmBackupImportSettings ReadWindowsGsmBackupSettings(
        WindowsGsmImportSource source,
        IReadOnlyDictionary<string, string> windowsGsmSettings,
        List<string> warnings)
    {
        var backupConfigPath = Path.Combine(source.ServerFolder, "configs", "BackupConfig.cfg");
        var backupSettings = File.Exists(backupConfigPath)
            ? ReadWindowsGsmConfig(backupConfigPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "backuplocation", "saveslocation", "fileslocation" })
        {
            if (windowsGsmSettings.TryGetValue(key, out var overrideValue) && !string.IsNullOrWhiteSpace(overrideValue))
            {
                backupSettings[key] = overrideValue;
            }
        }

        var destination = GetWindowsGsmValue(backupSettings, "backuplocation", string.Empty);
        if (!string.IsNullOrWhiteSpace(destination))
        {
            warnings.Add($"WindowsGSM custom backup destination will be imported: {destination}");
        }

        var paths = new List<string>();
        foreach (var sourcePath in SplitWindowsGsmPaths(
                     GetWindowsGsmValue(backupSettings, "saveslocation", string.Empty),
                     GetWindowsGsmValue(backupSettings, "fileslocation", string.Empty)))
        {
            var converted = ConvertWindowsGsmBackupPath(source.ServerFilesPath, sourcePath);
            if (string.IsNullOrWhiteSpace(converted))
            {
                warnings.Add($"WindowsGSM backup target is outside serverfiles and was not imported: {sourcePath}");
                continue;
            }

            paths.Add(converted);
        }

        return new WindowsGsmBackupImportSettings(
            destination,
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> SplitWindowsGsmPaths(params string[] values)
    {
        return values.SelectMany(value => value.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string ConvertWindowsGsmBackupPath(string serverFilesPath, string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        }

        var resolved = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverFilesPath));
        return resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, resolved)
            : string.Empty;
    }

    private static bool ReadWindowsGsmBoolean(IReadOnlyDictionary<string, string> settings, string key)
    {
        var value = GetWindowsGsmValue(settings, key, string.Empty);
        return value == "1" || bool.TryParse(value, out var parsed) && parsed;
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? text[1..^1]
            : text;
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

    private static void CopyDirectory(string source, string target, IProgress<WindowsGsmServerImportProgress>? progress = null)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();
        var totalBytes = files.Sum(file => file.Length);
        var copiedBytes = 0L;
        var copiedFiles = 0;

        progress?.Report(new WindowsGsmServerImportProgress(
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
            progress?.Report(new WindowsGsmServerImportProgress(
                Phase: "Copying files",
                CurrentPath: relativePath,
                FilesCopied: copiedFiles,
                TotalFiles: files.Length,
                BytesCopied: copiedBytes,
                TotalBytes: totalBytes));

            File.Copy(file.FullName, destination, overwrite: false);
            copiedFiles++;
            copiedBytes += file.Length;

            progress?.Report(new WindowsGsmServerImportProgress(
                Phase: "Copying files",
                CurrentPath: relativePath,
                FilesCopied: copiedFiles,
                TotalFiles: files.Length,
                BytesCopied: copiedBytes,
                TotalBytes: totalBytes));
        }

        progress?.Report(new WindowsGsmServerImportProgress(
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
            if (!IsPathInsideDirectory(serversPath, fullServerFolder))
            {
                return;
            }

            Directory.Delete(fullServerFolder, recursive: true);
        }
        catch
        {
            // Preserve the original import exception. Cleanup is best effort.
        }
    }

    private static bool IsPathInsideDirectory(string parent, string candidate)
    {
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var candidatePath = Path.GetFullPath(candidate);
        return !string.Equals(parentPath, candidatePath, StringComparison.OrdinalIgnoreCase) &&
            candidatePath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WindowsGsmImportSource(
        string ServerFolder,
        string WindowsGsmConfigPath,
        string ServerFilesPath);

    private sealed record WindowsGsmImportedValue(
        object? Value,
        string Source,
        bool IsCertain);

    // Preferred fields for new consumers are ImportedFrom and ImportMode.
    // SourceType and Mode remain for compatibility with earlier import.json readers.
    private sealed record WindowsGsmImportMetadata(
        [property: JsonPropertyName("sourceType")] string SourceType,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("importedFrom")] string ImportedFrom,
        [property: JsonPropertyName("moduleId")] string ModuleId,
        [property: JsonPropertyName("moduleName")] string ModuleName,
        [property: JsonPropertyName("importMode")] string ImportMode,
        [property: JsonPropertyName("sourceServerFolder")] string SourceServerFolder,
        [property: JsonPropertyName("sourceServerFilesPath")] string SourceServerFilesPath,
        [property: JsonPropertyName("sourceConfigPath")] string SourceConfigPath,
        [property: JsonPropertyName("sourceGame")] string SourceGame,
        [property: JsonPropertyName("copiedOrAdoptedInstallPath")] string CopiedOrAdoptedInstallPath,
        [property: JsonPropertyName("sourceLeftUnchanged")] bool SourceLeftUnchanged,
        [property: JsonPropertyName("importedUtc")] DateTimeOffset ImportedUtc,
        [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

    private sealed record WindowsGsmBackupImportSettings(
        string Destination,
        IReadOnlyList<string> Paths);
}

public sealed record WindowsGsmServerImportPreview(
    string ModuleId,
    string ModuleName,
    string SourceGame,
    string SourceServerFolder,
    string SourceServerFilesPath,
    string SourceConfigPath,
    string SteamBranch,
    string SteamBranchPassword,
    IReadOnlyList<WindowsGsmServerImportRow> Rows,
    IReadOnlyList<string> Warnings,
    bool BackupOnStart = false,
    string BackupDestination = "",
    IReadOnlyList<string>? BackupPaths = null)
{
    public IReadOnlyList<string> ImportedBackupPaths => BackupPaths ?? [];
}

public sealed record WindowsGsmServerImportRow(
    string Key,
    string Label,
    string Type,
    bool Required,
    string Value,
    string Source,
    WindowsGsmImportFieldStatus Status);

public sealed record WindowsGsmServerImportFieldValue(string Key, string Value);

public sealed record WindowsGsmServerImportResult(
    string ServerId,
    string ServerName,
    string ServerFolder,
    string InstallPath,
    string ConfigPath,
    IReadOnlyDictionary<string, object?> Settings);

public sealed record WindowsGsmServerImportProgress(
    string Phase,
    string CurrentPath,
    int FilesCopied,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes)
{
    public double Percent => TotalBytes > 0
        ? Math.Clamp(BytesCopied * 100.0 / TotalBytes, 0, 100)
        : TotalFiles > 0
            ? Math.Clamp(FilesCopied * 100.0 / TotalFiles, 0, 100)
            : 0;
}

public enum WindowsGsmServerImportMode
{
    Copy,
    Adopt
}

public enum WindowsGsmImportFieldStatus
{
    Imported,
    Defaulted,
    Missing,
    Review
}
