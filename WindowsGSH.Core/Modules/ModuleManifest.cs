using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsGSH.Core.Modules;

public sealed class ModuleManifest
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = "0.1.0";

    public string? Author { get; set; }

    public string? Description { get; set; }

    public string? Url { get; set; }

    public string? Homepage { get; set; }

    public string? Repository { get; set; }

    public string? SourceUrl { get; set; }

    public string? Color { get; set; }

    public string? ModuleApiVersion { get; set; }

    public string? MinimumWindowsGshVersion { get; set; }

    public List<string>? SupportedWindowsGshVersions { get; set; }

    public string? Type { get; set; }

    public string? Entry { get; set; }

    public string? SteamAppId { get; set; }

    public ManifestSteam? Steam { get; set; }

    public ManifestEntryPoints? EntryPoints { get; set; }

    public ManifestRuntime? Runtime { get; set; }

    public ManifestCapabilities? Capabilities { get; set; }

    public ManifestApi? Api { get; set; }

    public List<ManifestConfigField>? ConfigFields { get; set; }

    public List<ManifestPort>? Ports { get; set; }

    public List<ManifestBackupTarget>? BackupTargets { get; set; }

    public List<ManifestAddon>? Addons { get; set; }

    public static ModuleManifest Load(string moduleJsonPath, Action<ModuleValidationMessage>? warningSink = null)
    {
        var manifest = Read(moduleJsonPath);
        var validation = manifest.Validate();
        foreach (var warning in validation.Warnings)
        {
            warningSink?.Invoke(warning);
        }

        return manifest;
    }

    public static ModuleManifest Read(string moduleJsonPath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(moduleJsonPath), options)
            ?? throw new InvalidOperationException("Module manifest is empty.");
    }

    public ModuleValidationResult Validate()
    {
        var result = ModuleValidator.Validate(this);
        if (result.HasErrors)
        {
            var messages = result.Errors
                .Select(error => $"{error.Path}: {error.Message}")
                .ToArray();
            throw new InvalidOperationException("Module manifest validation failed: " + string.Join(" ", messages));
        }

        return result;
    }

    public ModuleCapabilities ToCapabilities(bool supportsQuery = false, bool supportsRcon = false)
    {
        return new ModuleCapabilities(
            SupportsInstall: ToSteamInstall() != null,
            SupportsUpdate: ToSteamInstall() != null,
            SupportsQuery: supportsQuery,
            SupportsRcon: supportsRcon,
            SupportsConsoleCommands: Capabilities?.ConsoleCommands ?? false,
            SupportsApiActions: Api?.Actions?.Count > 0,
            SupportsBackups: BackupTargets?.Count > 0,
            SupportsDirectConnection: Capabilities?.DirectConnection ?? false,
            RequiresJava: Capabilities?.RequiresJava ?? false,
            MinimumJavaMajor: Capabilities?.MinimumJavaMajor);
    }

    public SteamInstallDefinition? ToSteamInstall()
    {
        var appId = Steam?.AppId ?? SteamAppId;
        return string.IsNullOrWhiteSpace(appId)
            ? null
            : new SteamInstallDefinition(
                appId.Trim(),
                Steam?.Anonymous ?? true,
                Steam?.Validate ?? true,
                Steam?.ModName,
                Steam?.CustomArguments);
    }

    public ModuleRuntimeDefinition ToRuntime()
    {
        return new ModuleRuntimeDefinition(
            NormalizePath(Require(EntryPoints?.Start, "entryPoints.start")),
            GetProcessNames(),
            Runtime?.AllowsEmbeddedConsole ?? false,
            Runtime?.PortIncrements ?? 1,
            Runtime?.QueryProtocol,
            ParseConsoleInputStrategy(Runtime?.ConsoleStrategy));
    }

    public IReadOnlyList<ConfigFieldDefinition> ToConfigFields()
    {
        return (ConfigFields ?? [])
            .Select(field => new ConfigFieldDefinition(
                Require(field.Key, "configFields.key"),
                Require(field.Label, "configFields.label"),
                ParseFieldType(field.Type),
                NormalizeDefaultValue(field.DefaultValue),
                field.Required,
                field.Description,
                field.Options,
                field.Minimum,
                field.Maximum,
                field.Group,
                field.VisibleWhen,
                field.RestartRequired,
                field.ValidationPattern,
                field.ValidationMessage))
            .ToArray();
    }

    public IReadOnlyList<ServerPortDefinition> ToPorts()
    {
        return (Ports ?? [])
            .Select(port => new ServerPortDefinition(
                Require(port.Id, "ports.id"),
                Require(port.Name, "ports.name"),
                ParsePortProtocol(port.Protocol),
                TrimOrNull(port.ConfigField),
                port.FixedValue,
                TrimOrNull(port.OffsetFrom),
                port.Offset,
                port.RangeSize,
                port.Required,
                port.OpenExternally,
                port.CheckLocalListener))
            .ToArray();
    }

    public IReadOnlyList<ServerBackupTargetDefinition> ToBackupTargets()
    {
        return (BackupTargets ?? [])
            .Select(target => new ServerBackupTargetDefinition(
                Require(target.Key, "backupTargets.key"),
                Require(target.Label, "backupTargets.label"),
                NormalizePath(Require(target.Path, "backupTargets.path")),
                string.Equals(target.Type, "directory", StringComparison.OrdinalIgnoreCase),
                target.Required))
            .ToArray();
    }

    public IReadOnlyList<ServerAddonDefinition> ToAddons()
    {
        return (Addons ?? [])
            .Select(addon => new ServerAddonDefinition(
                Require(addon.Id, "addons.id"),
                Require(addon.Name, "addons.name"),
                addon.Description ?? string.Empty,
                new ModuleCapabilities(
                    SupportsInstall: false,
                    SupportsUpdate: false,
                    SupportsQuery: addon.CapabilitiesAdded?.Query ?? false,
                    SupportsRcon: addon.CapabilitiesAdded?.Rcon ?? false,
                    SupportsConsoleCommands: addon.CapabilitiesAdded?.ConsoleCommands ?? false,
                    SupportsApiActions: false,
                    SupportsBackups: false,
                    SupportsDirectConnection: false,
                    RequiresJava: addon.CapabilitiesAdded?.RequiresJava ?? false,
                    MinimumJavaMajor: addon.CapabilitiesAdded?.MinimumJavaMajor),
                (addon.ConfigFields ?? []).Select(field => new ConfigFieldDefinition(
                    Require(field.Key, "addons.configFields.key"),
                    Require(field.Label, "addons.configFields.label"),
                    ParseFieldType(field.Type),
                    NormalizeDefaultValue(field.DefaultValue),
                    field.Required,
                    field.Description,
                    field.Options,
                    field.Minimum,
                    field.Maximum,
                    field.Group,
                    field.VisibleWhen,
                    field.RestartRequired,
                    field.ValidationPattern,
                    field.ValidationMessage)).ToArray(),
                addon.DownloadUrl,
                addon.InstallInstructions,
                addon.Package == null
                    ? null
                    : new AddonPackageDefinition(
                        ParseAddonPackageKind(addon.Package.Kind),
                        Require(addon.Package.SourceUrl, "addons.package.sourceUrl"),
                        NormalizePath(Require(addon.Package.InstallPath, "addons.package.installPath")),
                        addon.Package.FileName,
                        addon.Package.StripComponents,
                        addon.Package.ArchiveSubpath,
                        addon.Package.RequiredMarkers,
                        addon.Package.ExpectedSha256),
                addon.SourceName,
                addon.SourceVersion))
            .ToArray();
    }

    public ModuleApiConnectionDefinition? ToApiConnection()
    {
        if (Api?.Connection == null)
        {
            return null;
        }

        return new ModuleApiConnectionDefinition(
            Api.Connection.EnabledKey ?? string.Empty,
            string.IsNullOrWhiteSpace(Api.Connection.Host) ? "127.0.0.1" : Api.Connection.Host.Trim(),
            Require(Api.Connection.PortKey, "api.connection.portKey"),
            Api.Connection.UsernameKey ?? string.Empty,
            Require(Api.Connection.PasswordKey, "api.connection.passwordKey"),
            string.IsNullOrWhiteSpace(Api.Connection.Scheme) ? "http" : Api.Connection.Scheme.Trim());
    }

    public IReadOnlyList<ModuleApiActionDefinition> ToApiActions()
    {
        return (Api?.Actions ?? [])
            .Select(action => new ModuleApiActionDefinition(
                Require(action.Key, "api.actions.key"),
                Require(action.Label, "api.actions.label"),
                string.IsNullOrWhiteSpace(action.Method) ? "GET" : action.Method.Trim().ToUpperInvariant(),
                Require(action.Path, "api.actions.path"),
                action.Destructive,
                action.Description,
                action.ConfirmMessage,
                action.BodyTemplate,
                (action.Parameters ?? []).Select(field => new ConfigFieldDefinition(
                    Require(field.Key, "api.actions.parameters.key"),
                    Require(field.Label, "api.actions.parameters.label"),
                    ParseFieldType(field.Type),
                    NormalizeDefaultValue(field.DefaultValue),
                    field.Required,
                    field.Description,
                    field.Options,
                    field.Minimum,
                    field.Maximum,
                    field.Group,
                    field.VisibleWhen,
                    field.RestartRequired,
                    field.ValidationPattern,
                    field.ValidationMessage)).ToArray()))
            .ToArray();
    }

    public string GetDefaultArguments()
    {
        return Runtime?.DefaultArguments ?? string.Empty;
    }

    private IReadOnlyList<string> GetProcessNames()
    {
        if (EntryPoints?.ProcessNames?.Count > 0)
        {
            return EntryPoints.ProcessNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(EntryPoints?.ProcessName))
        {
            return [EntryPoints.ProcessName.Trim()];
        }

        return [Path.GetFileNameWithoutExtension(Require(EntryPoints?.Start, "entryPoints.start"))];
    }

    private static ConfigFieldType ParseFieldType(string? type)
    {
        return Enum.TryParse<ConfigFieldType>(type, ignoreCase: true, out var parsed) ? parsed : ConfigFieldType.Text;
    }

    private static ConsoleInputStrategy? ParseConsoleInputStrategy(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.Parse<ConsoleInputStrategy>(value.Trim(), ignoreCase: true);
    }

    private static AddonPackageKind ParseAddonPackageKind(string? value)
    {
        return Enum.TryParse<AddonPackageKind>(value, ignoreCase: true, out var parsed) &&
               Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported addon package kind: {value}");
    }

    private static PortProtocol ParsePortProtocol(string? value)
    {
        return Enum.TryParse<PortProtocol>(value, ignoreCase: true, out var parsed) &&
               Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported port protocol: {value}");
    }

    private static object? NormalizeDefaultValue(object? value)
    {
        return value is JsonElement element
            ? element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
                JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                _ => element.ToString()
            }
            : value;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Module manifest requires {name}.");
        }

        return value.Trim();
    }

    // ModuleValidator trims ports.configField/ports.offsetFrom before matching them against known
    // config field keys / port ids (so " game " passes validation the same way "game" would), but
    // without also trimming here, ToPorts() would store the untrimmed original - a value that
    // passed validation, then failed the identical lookup at resolve time in ServerPortResolver,
    // which has no reason to expect stray whitespace since nothing told it to strip any.
    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ManifestSteam
{
    public string? AppId { get; set; }
    public bool Anonymous { get; set; } = true;
    public bool Validate { get; set; } = true;
    public string? ModName { get; set; }
    public string? CustomArguments { get; set; }
}

public sealed class ManifestEntryPoints
{
    public string? Start { get; set; }
    public string? ProcessName { get; set; }
    public List<string>? ProcessNames { get; set; }
}

public sealed class ManifestRuntime
{
    public bool AllowsEmbeddedConsole { get; set; }
    public string? ConsoleStrategy { get; set; }
    public int PortIncrements { get; set; } = 1;
    public string? QueryProtocol { get; set; }
    public string? DefaultArguments { get; set; }
    public string? LogPath { get; set; }
    public ManifestStopStrategy? Stop { get; set; }
}

public sealed class ManifestStopStrategy
{
    public string? Strategy { get; set; }
    public List<string>? Commands { get; set; }
    public List<ManifestStopStep>? Steps { get; set; }
    public int WaitMilliseconds { get; set; } = 2000;
    public bool KillAfterTimeout { get; set; } = true;
}

public sealed class ManifestStopStep
{
    public string? Type { get; set; }
    public string? Value { get; set; }
    public int WaitMilliseconds { get; set; }
}

public sealed class ManifestCapabilities
{
    public bool DirectConnection { get; set; }
    public bool ConsoleCommands { get; set; }
    public bool RequiresJava { get; set; }
    public int? MinimumJavaMajor { get; set; }
}

public sealed class ManifestApi
{
    public ManifestApiConnection? Connection { get; set; }
    public List<ManifestApiAction>? Actions { get; set; }
}

public sealed class ManifestApiConnection
{
    public string? EnabledKey { get; set; }
    public string? Host { get; set; }
    public string? PortKey { get; set; }
    public string? UsernameKey { get; set; }
    public string? PasswordKey { get; set; }
    public string? Scheme { get; set; }
}

public sealed class ManifestApiAction
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public bool Destructive { get; set; }
    public string? Description { get; set; }
    public string? ConfirmMessage { get; set; }
    public string? BodyTemplate { get; set; }
    public List<ManifestConfigField>? Parameters { get; set; }
}

public sealed class ManifestConfigField
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public string? Type { get; set; }
    public object? DefaultValue { get; set; }
    public bool Required { get; set; }
    public string? Description { get; set; }
    public List<string>? Options { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public string? Group { get; set; }
    public FieldVisibilityCondition? VisibleWhen { get; set; }
    public bool RestartRequired { get; set; }
    public string? ValidationPattern { get; set; }
    public string? ValidationMessage { get; set; }
}

public sealed class ManifestPort
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Protocol { get; set; }
    public string? ConfigField { get; set; }
    public int? FixedValue { get; set; }
    public string? OffsetFrom { get; set; }
    public int Offset { get; set; }
    public int RangeSize { get; set; } = 1;
    public bool Required { get; set; }
    public bool OpenExternally { get; set; } = true;
    public bool CheckLocalListener { get; set; } = true;
}

public sealed class ManifestBackupTarget
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public string? Path { get; set; }
    public string? Type { get; set; }
    public bool Required { get; set; }
}

public sealed class ManifestAddon
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? DownloadUrl { get; set; }
    public string? InstallInstructions { get; set; }
    public string? SourceName { get; set; }
    public string? SourceVersion { get; set; }
    public ManifestAddonPackage? Package { get; set; }
    public ManifestAddonCapabilities? CapabilitiesAdded { get; set; }
    public List<ManifestConfigField>? ConfigFields { get; set; }
}

public sealed class ManifestAddonPackage
{
    public string? Kind { get; set; }
    public string? SourceUrl { get; set; }
    public string? InstallPath { get; set; }
    public string? FileName { get; set; }
    public int StripComponents { get; set; }
    public string? ArchiveSubpath { get; set; }
    public List<string>? RequiredMarkers { get; set; }
    public string? ExpectedSha256 { get; set; }
}

public sealed class ManifestAddonCapabilities
{
    public bool Query { get; set; }
    public bool Rcon { get; set; }
    public bool ConsoleCommands { get; set; }
    public bool RequiresJava { get; set; }
    public int? MinimumJavaMajor { get; set; }
}
