using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Modules;

public enum ModuleValidationSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ModuleValidationMessage(
    ModuleValidationSeverity Severity,
    string Code,
    string Path,
    string Message);

public sealed class ModuleValidationResult
{
    private readonly List<ModuleValidationMessage> _messages = [];

    public IReadOnlyList<ModuleValidationMessage> Messages => _messages;

    public bool HasErrors => _messages.Any(message => message.Severity == ModuleValidationSeverity.Error);

    public IReadOnlyList<ModuleValidationMessage> Errors => _messages
        .Where(message => message.Severity == ModuleValidationSeverity.Error)
        .ToArray();

    public IReadOnlyList<ModuleValidationMessage> Warnings => _messages
        .Where(message => message.Severity == ModuleValidationSeverity.Warning)
        .ToArray();

    internal void Add(ModuleValidationSeverity severity, string code, string path, string message)
    {
        _messages.Add(new ModuleValidationMessage(severity, code, path, message));
    }
}

public static class ModuleValidator
{
    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedQueryProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        "none",
        "process",
        "A2S",
        "EOS",
        "EOS/A2S",
        "FiveM",
        "FIVEM",
        "UT3",
        "Unreal2",
        "Unreal 2"
    };

    public static ModuleValidationResult Validate(ModuleManifest manifest)
    {
        var result = new ModuleValidationResult();

        ValidateIdentity(manifest, result);
        ValidateCompatibility(manifest, result);
        ValidateEntryPoints(manifest.EntryPoints, result);
        ValidateRuntime(manifest.Runtime, result);
        ValidateConfigFields(manifest.ConfigFields, "configFields", result);
        ValidatePorts(manifest, result);
        ValidateBackupTargets(manifest.BackupTargets, result);
        ValidateAddons(manifest.Addons, result);
        ValidateLaunchPlaceholders(manifest, result);
        ValidateSteam(manifest.Steam, result);

        return result;
    }

    private static void ValidateSteam(ManifestSteam? steam, ModuleValidationResult result)
    {
        if (steam == null)
        {
            return;
        }

        try
        {
            Steam.SteamCmdPolicy.ValidateAppId(steam.AppId);
        }
        catch (ArgumentException)
        {
            Error(result, "steam.appId.invalid", "steam.appId", "Steam App ID must be a positive numeric value.");
        }

        if (string.IsNullOrWhiteSpace(steam.CustomArguments))
        {
            return;
        }

        try
        {
            var tokens = WindowsCommandLineParser.Split(steam.CustomArguments);
            if (tokens.Any(token => token.StartsWith('+') || token.IndexOfAny(['\0', '\r', '\n']) >= 0))
            {
                Error(
                    result,
                    "steam.customArguments.command",
                    "steam.customArguments",
                    "Steam custom arguments cannot add SteamCMD +commands. Use dedicated manifest fields instead.");
                return;
            }

            if (tokens.Count == 2 &&
                tokens[0].Equals("-beta", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(tokens[1], "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
            {
                return;
            }

            Warning(
                result,
                "steam.customArguments.present",
                "steam.customArguments",
                "This module supplies privileged SteamCMD option tokens. Review them before trusting an unfamiliar module.");
        }
        catch (FormatException ex)
        {
            Error(result, "steam.customArguments.invalid", "steam.customArguments", ex.Message);
        }
    }

    private static void ValidateCompatibility(ModuleManifest manifest, ModuleValidationResult result)
    {
        foreach (var message in ModuleCompatibility.Evaluate(manifest).Messages)
        {
            result.Add(message.Severity, message.Code, message.Path, message.Message);
        }
    }

    private static void ValidateIdentity(ModuleManifest manifest, ModuleValidationResult result)
    {
        Require(manifest.Id, "manifest.id", "id.required", "Module manifest requires id.", result);
        Require(manifest.Name, "manifest.name", "name.required", "Module manifest requires name.", result);
        Require(manifest.Version, "manifest.version", "version.required", "Module manifest requires version.", result);

        if (!string.IsNullOrWhiteSpace(manifest.Id) && !SafeIdPattern.IsMatch(manifest.Id.Trim()))
        {
            Error(result, "id.invalid", "manifest.id", "Module id must use only letters, numbers, dots, underscores, or hyphens, and cannot start with punctuation.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Version) &&
            !Version.TryParse(manifest.Version.Trim().TrimStart('v', 'V'), out _))
        {
            Warning(result, "version.display", "manifest.version", "Module version is not a standard numeric version. It will still be displayed as provided.");
        }
    }

    private static void ValidateEntryPoints(ManifestEntryPoints? entryPoints, ModuleValidationResult result)
    {
        Require(entryPoints?.Start, "entryPoints.start", "entryPoint.start.required", "Module manifest requires entryPoints.start.", result);
        ValidateRelativePath(entryPoints?.Start, "entryPoints.start", allowCurrentDirectory: false, result);

        var processNames = entryPoints?.ProcessNames ?? [];
        for (var index = 0; index < processNames.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(processNames[index]))
            {
                Error(result, "entryPoint.processName.required", $"entryPoints.processNames[{index}]", "Process names cannot be blank.");
            }
        }
    }

    private static void ValidateRuntime(ManifestRuntime? runtime, ModuleValidationResult result)
    {
        if (runtime == null)
        {
            return;
        }

        if (runtime.PortIncrements < 1)
        {
            Error(result, "runtime.portIncrements.invalid", "runtime.portIncrements", "Runtime portIncrements must be 1 or greater.");
        }

        if (!SupportedQueryProtocols.Contains(runtime.QueryProtocol?.Trim() ?? string.Empty))
        {
            Error(result, "runtime.queryProtocol.unsupported", "runtime.queryProtocol", $"Unsupported query protocol: {runtime.QueryProtocol}.");
        }

        if (!string.IsNullOrWhiteSpace(runtime.ConsoleStrategy) &&
            !Enum.TryParse<ConsoleInputStrategy>(runtime.ConsoleStrategy.Trim(), ignoreCase: true, out _))
        {
            Error(
                result,
                "runtime.consoleStrategy.unsupported",
                "runtime.consoleStrategy",
                $"Unsupported console strategy: {runtime.ConsoleStrategy}. Expected Redirected, WindowMessage, RconPreferred, LogTailOnly, or None.");
        }

        ValidateRelativePath(runtime.LogPath, "runtime.logPath", allowCurrentDirectory: false, result);

        if (runtime.Stop != null && runtime.Stop.WaitMilliseconds < 0)
        {
            Error(result, "runtime.stop.wait.invalid", "runtime.stop.waitMilliseconds", "Stop waitMilliseconds cannot be negative.");
        }

        var steps = runtime.Stop?.Steps ?? [];
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].WaitMilliseconds < 0)
            {
                Error(result, "runtime.stop.step.wait.invalid", $"runtime.stop.steps[{index}].waitMilliseconds", "Stop step waitMilliseconds cannot be negative.");
            }
        }
    }

    private static void ValidateConfigFields(
        IReadOnlyList<ManifestConfigField>? fields,
        string path,
        ModuleValidationResult result)
    {
        if (fields == null)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var fieldPath = $"{path}[{index}]";
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                Error(result, "config.key.required", $"{fieldPath}.key", "Config field requires key.");
            }
            else if (!keys.Add(field.Key.Trim()))
            {
                Error(result, "config.key.duplicate", $"{fieldPath}.key", $"Duplicate config field key: {field.Key}.");
            }

            Require(field.Label, $"{fieldPath}.label", "config.label.required", "Config field requires label.", result);
            ValidateFieldType(field, fieldPath, result);
            ValidateNumericBounds(field, fieldPath, result);
            ValidateValidationPattern(field, fieldPath, result);

            if (field.Required && IsEmptyDefault(field.DefaultValue))
            {
                Warning(result, "config.required.emptyDefault", $"{fieldPath}.defaultValue", $"Required config field {field.Key ?? fieldPath} has an empty default value.");
            }

            if (IsSecretLikeKey(field.Key) && IsPlainTextStorageType(ParseFieldType(field.Type)))
            {
                Warning(
                    result,
                    "config.secret.plaintext",
                    $"{fieldPath}.type",
                    $"Config field {field.Key ?? fieldPath} looks like a secret but is declared as {field.Type ?? "Text"}. " +
                    "Declare it as Password so WindowsGSH stores it with Windows user encryption.");
            }
        }

        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.VisibleWhen?.Key) && !keys.Contains(field.VisibleWhen.Key.Trim()))
            {
                Warning(result, "config.visibleWhen.unknownKey", $"{path}.{field.Key}.visibleWhen.key", $"Visibility condition references unknown field: {field.VisibleWhen.Key}.");
            }
        }
    }

    private static void ValidateFieldType(ManifestConfigField field, string fieldPath, ModuleValidationResult result)
    {
        var type = ParseFieldType(field.Type);
        if (!string.IsNullOrWhiteSpace(field.Type) && !Enum.TryParse<ConfigFieldType>(field.Type, ignoreCase: true, out _))
        {
            Warning(result, "config.type.unknown", $"{fieldPath}.type", $"Unknown config field type '{field.Type}' will be treated as Text.");
        }

        if ((type == ConfigFieldType.Select || type == ConfigFieldType.MultiSelect) &&
            (field.Options == null || field.Options.Count == 0 || field.Options.Any(string.IsNullOrWhiteSpace)))
        {
            Error(result, "config.options.required", $"{fieldPath}.options", $"{type} config fields require at least one non-empty option.");
        }

        if (type != ConfigFieldType.Port)
        {
            return;
        }

        if (!TryReadNumber(field.DefaultValue, out var portDefault))
        {
            // A blank/absent default on an optional Port field is a legitimate, documented pattern
            // now (see network.queryPort in GenericWrapperModule.cs and BlankSteamTemplate's own
            // module.json) - a Port field that's genuinely meant to stay unresolved until the user
            // configures it. Warning on that unconditionally punished the exact thing this
            // validator's own recommended template does. Still warn when the field is Required
            // (an empty default there is a real gap, same as before), or when a value IS present
            // but doesn't parse as a number at all (e.g. "abc") - that's not "intentionally blank,"
            // it's a different value that happens not to be numeric, and still deserves a warning
            // regardless of Required. Only "optional and genuinely blank" is exempted.
            if (field.Required || !IsEmptyDefault(field.DefaultValue))
            {
                Warning(result, "config.port.default.missing", $"{fieldPath}.defaultValue", "Port fields should provide a numeric default value.");
            }
        }
        else if (portDefault < 1 || portDefault > 65535)
        {
            Error(result, "config.port.default.range", $"{fieldPath}.defaultValue", "Port field default value must be between 1 and 65535.");
        }
    }

    private static void ValidateNumericBounds(ManifestConfigField field, string fieldPath, ModuleValidationResult result)
    {
        if (field.Minimum.HasValue && field.Maximum.HasValue && field.Minimum.Value > field.Maximum.Value)
        {
            Error(result, "config.range.invalid", fieldPath, "Config field minimum cannot be greater than maximum.");
        }

        if (ParseFieldType(field.Type) == ConfigFieldType.Port)
        {
            if (field.Minimum.HasValue && field.Minimum.Value < 1)
            {
                Error(result, "config.port.minimum.range", $"{fieldPath}.minimum", "Port field minimum must be 1 or greater.");
            }

            if (field.Maximum.HasValue && field.Maximum.Value > 65535)
            {
                Error(result, "config.port.maximum.range", $"{fieldPath}.maximum", "Port field maximum must be 65535 or lower.");
            }
        }
    }

    private static void ValidateValidationPattern(ManifestConfigField field, string fieldPath, ModuleValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(field.ValidationPattern))
        {
            return;
        }

        try
        {
            _ = new Regex(field.ValidationPattern);
        }
        catch (ArgumentException ex)
        {
            Error(result, "config.validationPattern.invalid", $"{fieldPath}.validationPattern", $"Validation regex is invalid: {ex.Message}");
        }
    }

    private static void ValidatePorts(ModuleManifest manifest, ModuleValidationResult result)
    {
        var ports = manifest.Ports;
        if (ports == null)
        {
            return;
        }

        var configFieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in manifest.ConfigFields ?? [])
        {
            if (!string.IsNullOrWhiteSpace(field.Key))
            {
                configFieldKeys.Add(field.Key.Trim());
            }
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ports.Count; index++)
        {
            var port = ports[index];
            var portPath = $"ports[{index}]";

            if (string.IsNullOrWhiteSpace(port.Id))
            {
                Error(result, "ports.id.required", $"{portPath}.id", "Port requires id.");
            }
            else if (!ids.Add(port.Id.Trim()))
            {
                Error(result, "ports.id.duplicate", $"{portPath}.id", $"Duplicate port id: {port.Id}.");
            }

            Require(port.Name, $"{portPath}.name", "ports.name.required", "Port requires name.", result);

            var protocolValid = !string.IsNullOrWhiteSpace(port.Protocol) &&
                Enum.TryParse<PortProtocol>(port.Protocol, ignoreCase: true, out var parsedProtocol) &&
                Enum.IsDefined(parsedProtocol);
            if (!protocolValid)
            {
                // Enum.TryParse alone accepts any numeric-looking string (e.g. "99") and produces
                // an undefined enum value rather than failing - Enum.IsDefined is required on top,
                // otherwise this check and ModuleManifest.ToPorts()'s own ParsePortProtocol (which
                // does check IsDefined) would disagree: a manifest could pass validation here and
                // then throw later, the first time ToPorts()/GetPorts() actually runs, instead of
                // failing cleanly with a structured validation error at load time.
                Error(result, "ports.protocol.unsupported", $"{portPath}.protocol", $"Port protocol must be tcp, udp, both, or either. Got: {port.Protocol}.");
            }

            // Exactly one source: configField (read from the resolved server's settings at
            // runtime), fixedValue (a literal port number), or offsetFrom (another port in this
            // same array, plus offset). Anything else - none set, or more than one - is ambiguous
            // about which value should actually win, so it's rejected outright rather than picking
            // a silent priority order a module author might not expect.
            var sourceCount = (string.IsNullOrWhiteSpace(port.ConfigField) ? 0 : 1) +
                (port.FixedValue.HasValue ? 1 : 0) +
                (string.IsNullOrWhiteSpace(port.OffsetFrom) ? 0 : 1);
            if (sourceCount != 1)
            {
                Error(
                    result,
                    "ports.source.exclusive",
                    portPath,
                    "Port must set exactly one of configField, fixedValue, or offsetFrom.");
            }

            if (!string.IsNullOrWhiteSpace(port.ConfigField) && !configFieldKeys.Contains(port.ConfigField.Trim()))
            {
                Error(result, "ports.configField.unknown", $"{portPath}.configField", $"Port references unknown config field: {port.ConfigField}.");
            }

            var fixedValueValid = true;
            if (port.FixedValue.HasValue && (port.FixedValue.Value < 1 || port.FixedValue.Value > 65535))
            {
                fixedValueValid = false;
                Error(result, "ports.fixedValue.range", $"{portPath}.fixedValue", "Port fixedValue must be between 1 and 65535.");
            }

            if (!string.IsNullOrWhiteSpace(port.OffsetFrom) &&
                string.Equals(port.OffsetFrom.Trim(), port.Id?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Error(result, "ports.offsetFrom.selfReference", $"{portPath}.offsetFrom", "Port cannot be offset from itself.");
            }

            var rangeSizeValid = port.RangeSize >= 1;
            if (!rangeSizeValid)
            {
                Error(result, "ports.rangeSize.invalid", $"{portPath}.rangeSize", "Port rangeSize must be 1 or greater.");
            }

            // Only a Fixed-source port's effective range is fully known at manifest-validation
            // time (a configField's actual value isn't known until a real server is resolved) -
            // ServerPortResolver re-checks this same bound at resolve time for every source kind,
            // but a Fixed port that's already guaranteed to fail deserves an immediate, structured
            // validation error rather than waiting for someone to actually resolve it. Skipped
            // when fixedValue or rangeSize is already individually invalid, to avoid a confusing
            // second error about the same underlying problem.
            //
            // long, not int, for the same reason ServerPortResolver.BuildResult uses long: rangeSize
            // only has a >= 1 lower bound (no upper bound), so an extreme value like int.MaxValue
            // would overflow an int sum and could wrap past the > 65535 check undetected.
            if (port.FixedValue.HasValue && fixedValueValid && rangeSizeValid &&
                (long)port.FixedValue.Value + port.RangeSize - 1 > 65535)
            {
                Error(
                    result,
                    "ports.fixedValue.rangeExceeds65535",
                    $"{portPath}.rangeSize",
                    $"Port {port.Id ?? portPath}'s fixed range ({port.FixedValue.Value}-{(long)port.FixedValue.Value + port.RangeSize - 1}) extends beyond 65535.");
            }
        }

        // Second pass, after every id is known: offsetFrom may reference a port declared later in
        // the array, so this can't be checked in the loop above.
        for (var index = 0; index < ports.Count; index++)
        {
            var offsetFrom = ports[index].OffsetFrom;
            if (!string.IsNullOrWhiteSpace(offsetFrom) && !ids.Contains(offsetFrom.Trim()))
            {
                Error(result, "ports.offsetFrom.unknown", $"ports[{index}].offsetFrom", $"Port references unknown offsetFrom port id: {offsetFrom}.");
            }
        }

        // Cross-port offset cycles (A offsets from B, B offsets from A) are deliberately NOT
        // detected here - ServerPortResolver's fixed-point resolution algorithm already has to
        // walk this same dependency graph at resolve time to support forward references, and any
        // port that never settles after that walk is, by construction, part of a cycle. Duplicating
        // a second graph-cycle check here would just be the same algorithm written twice.

        ValidateFixedPortOverlaps(ports, result);
    }

    // Only Fixed-source ports can be checked for overlap here - a configField's actual value, and
    // therefore an offset port's effective value, isn't known until a real server's settings are
    // resolved (ServerPortResolver does the equivalent protocol-aware overlap check there, across
    // every resolved port regardless of source). Fixed-vs-Fixed is the one case that's fully known
    // at manifest-validation time, so it gets an immediate structured error instead of waiting.
    private static void ValidateFixedPortOverlaps(IReadOnlyList<ManifestPort> ports, ModuleValidationResult result)
    {
        var fixedPorts = ports
            .Where(port => port.FixedValue is >= 1 and <= 65535 && port.RangeSize >= 1)
            .ToArray();

        for (var i = 0; i < fixedPorts.Length; i++)
        {
            for (var j = i + 1; j < fixedPorts.Length; j++)
            {
                var a = fixedPorts[i];
                var b = fixedPorts[j];
                if (!ProtocolsCouldOverlap(a.Protocol, b.Protocol))
                {
                    continue;
                }

                // long, not int - same overflow concern as the rangeExceeds65535 check above,
                // since rangeSize has no upper bound.
                long aStart = a.FixedValue!.Value;
                var aEnd = aStart + a.RangeSize - 1;
                long bStart = b.FixedValue!.Value;
                var bEnd = bStart + b.RangeSize - 1;
                if (aStart > bEnd || bStart > aEnd)
                {
                    continue;
                }

                Error(
                    result,
                    "ports.fixedValue.overlap",
                    "ports",
                    $"Port {a.Id} ({aStart}-{aEnd}) statically overlaps port {b.Id} ({bStart}-{bEnd}) on a protocol they share.");
            }
        }
    }

    // Two ports on genuinely different protocols (one Tcp-only, one Udp-only) don't conflict even
    // if their numbers coincide - Both, or a shared protocol, is what actually matters.
    private static bool ProtocolsCouldOverlap(string? protocolA, string? protocolB)
    {
        var a = Enum.TryParse<PortProtocol>(protocolA, ignoreCase: true, out var parsedA) && Enum.IsDefined(parsedA) ? parsedA : (PortProtocol?)null;
        var b = Enum.TryParse<PortProtocol>(protocolB, ignoreCase: true, out var parsedB) && Enum.IsDefined(parsedB) ? parsedB : (PortProtocol?)null;
        if (a == null || b == null)
        {
            // An unrecognized protocol already produces its own ports.protocol.unsupported error;
            // don't also report a possibly-spurious overlap for data that's already known-bad.
            return false;
        }

        return a is PortProtocol.Both or PortProtocol.Either ||
               b is PortProtocol.Both or PortProtocol.Either ||
               a == b;
    }

    private static void ValidateBackupTargets(IReadOnlyList<ManifestBackupTarget>? targets, ModuleValidationResult result)
    {
        if (targets == null)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var targetPath = $"backupTargets[{index}]";
            if (string.IsNullOrWhiteSpace(target.Key))
            {
                Error(result, "backup.key.required", $"{targetPath}.key", "Backup target requires key.");
            }
            else if (!keys.Add(target.Key.Trim()))
            {
                Error(result, "backup.key.duplicate", $"{targetPath}.key", $"Duplicate backup target key: {target.Key}.");
            }

            Require(target.Label, $"{targetPath}.label", "backup.label.required", "Backup target requires label.", result);
            Require(target.Path, $"{targetPath}.path", "backup.path.required", "Backup target requires path.", result);
            ValidateRelativePath(target.Path, $"{targetPath}.path", allowCurrentDirectory: true, result);

            if (!string.IsNullOrWhiteSpace(target.Type) &&
                !string.Equals(target.Type, "directory", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target.Type, "file", StringComparison.OrdinalIgnoreCase))
            {
                Warning(result, "backup.type.unknown", $"{targetPath}.type", $"Unknown backup target type '{target.Type}' will be treated as file.");
            }
        }
    }

    private static void ValidateAddons(IReadOnlyList<ManifestAddon>? addons, ModuleValidationResult result)
    {
        if (addons == null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < addons.Count; index++)
        {
            var addon = addons[index];
            var addonPath = $"addons[{index}]";
            if (string.IsNullOrWhiteSpace(addon.Id))
            {
                Error(result, "addon.id.required", $"{addonPath}.id", "Addon requires id.");
            }
            else if (!SafeIdPattern.IsMatch(addon.Id.Trim()))
            {
                Error(result, "addon.id.invalid", $"{addonPath}.id", "Addon id must use only letters, numbers, dots, underscores, or hyphens, and cannot start with punctuation.");
            }
            else if (!ids.Add(addon.Id.Trim()))
            {
                Error(result, "addon.id.duplicate", $"{addonPath}.id", $"Duplicate addon id: {addon.Id}.");
            }

            Require(addon.Name, $"{addonPath}.name", "addon.name.required", "Addon requires name.", result);
            ValidateConfigFields(addon.ConfigFields, $"{addonPath}.configFields", result);
            ValidateAddonPackage(addon.Package, $"{addonPath}.package", result);
        }
    }

    private static void ValidateAddonPackage(
        ManifestAddonPackage? package,
        string path,
        ModuleValidationResult result)
    {
        if (package == null)
        {
            return;
        }

        if (!Enum.TryParse<AddonPackageKind>(package.Kind, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            Error(result, "addon.package.kind.invalid", $"{path}.kind", "Addon package kind must be Zip, Tar, TarGz, or File.");
        }

        if (!Uri.TryCreate(package.SourceUrl, UriKind.Absolute, out var source) || source.Scheme is not "https")
        {
            Error(result, "addon.package.sourceUrl.invalid", $"{path}.sourceUrl", "Addon package sourceUrl must be an absolute HTTPS URL.");
        }

        Require(
            package.InstallPath,
            $"{path}.installPath",
            "addon.package.installPath.required",
            "Addon package requires installPath.",
            result);
        ValidateRelativePath(package.InstallPath, $"{path}.installPath", allowCurrentDirectory: true, result);
        ValidateRelativePath(package.ArchiveSubpath, $"{path}.archiveSubpath", allowCurrentDirectory: false, result);
        foreach (var (marker, index) in (package.RequiredMarkers ?? []).Select((marker, index) => (marker, index)))
        {
            ValidateRelativePath(marker, $"{path}.requiredMarkers[{index}]", allowCurrentDirectory: false, result);
        }

        if (package.StripComponents < 0)
        {
            Error(result, "addon.package.stripComponents.invalid", $"{path}.stripComponents", "stripComponents cannot be negative.");
        }

        if (kind == AddonPackageKind.File && string.IsNullOrWhiteSpace(package.FileName) &&
            string.IsNullOrWhiteSpace(Path.GetFileName(source?.LocalPath)))
        {
            Error(result, "addon.package.fileName.required", $"{path}.fileName", "Direct file packages require fileName or a URL ending in a file name.");
        }

        if (string.IsNullOrWhiteSpace(package.ExpectedSha256))
        {
            Warning(
                result,
                "addon.package.expectedSha256.missing",
                $"{path}.expectedSha256",
                "Addon package does not declare an expectedSha256. The download will still be installed, but its integrity won't be verified against a known-good hash.");
        }
    }

    // ConfigFieldType.CommandLine is deliberately excluded: that type's own established meaning in
    // this codebase (server.additionalArguments, GenericWrapperModule.LaunchArgumentsKey) is
    // "pre-composed, module-author-facing command-line text meant to be spliced in as-is" - the
    // opposite of a single opaque value. Warning on it would tell authors to wrap it in
    // {quote:key}, which merges multiple intended arguments (e.g. "-foo -bar") into one and breaks
    // the launch command. ConfigFieldType.Path is included: paths are a single value (not composed
    // syntax) and commonly contain spaces (e.g. "C:\Program Files\..."), so they need the same
    // quoting as Text/Password.
    private static readonly HashSet<ConfigFieldType> FreeFormLaunchArgumentTypes = new()
    {
        ConfigFieldType.Text,
        ConfigFieldType.Password,
        ConfigFieldType.Path
    };

    private static void ValidateLaunchPlaceholders(ModuleManifest manifest, ModuleValidationResult result)
    {
        var arguments = manifest.Runtime?.DefaultArguments;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return;
        }

        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "server.additionalArguments"
        };
        var fieldTypesByKey = new Dictionary<string, ConfigFieldType>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in manifest.ConfigFields ?? [])
        {
            if (!string.IsNullOrWhiteSpace(field.Key))
            {
                var key = field.Key.Trim();
                knownKeys.Add(key);
                fieldTypesByKey[key] = ParseFieldType(field.Type);
            }
        }

        var referencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unquotedReferencedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAllPlaceholderKeys(arguments, referencedKeys, unquotedReferencedKeys);

        foreach (var key in referencedKeys.Where(key => !knownKeys.Contains(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            Error(result, "runtime.arguments.placeholder.unknown", "runtime.defaultArguments", $"Launch arguments reference unknown config key: {key}.");
        }

        // P3-05: {key} substitutes the raw value with no escaping (unlike {quote:key}, which goes
        // through WindowsCommandLineEscaper.Quote). A free-form Text/Password/Path field containing
        // spaces or quotes can break out of its intended single argument, or inject extra
        // command-line arguments into the launched process. server.additionalArguments is excluded
        // by name (belt-and-suspenders - it's normally CommandLine-typed anyway, already excluded
        // by type below) since it's documented elsewhere as intentionally privileged raw text that
        // module authors splice in as-is, not a single value needing escaping.
        foreach (var key in unquotedReferencedKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(key, "server.additionalArguments", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fieldTypesByKey.TryGetValue(key, out var type) && FreeFormLaunchArgumentTypes.Contains(type))
            {
                Warning(
                    result,
                    "runtime.arguments.placeholder.unquoted",
                    "runtime.defaultArguments",
                    $"Launch arguments reference {{{key}}} unquoted, but {key} is a {type} field. Use {{quote:{key}}} instead so values containing spaces or quotes can't break out of their argument or inject extra command-line arguments.");
            }
        }
    }

    private static void CollectAllPlaceholderKeys(string template, HashSet<string> referencedKeys, HashSet<string> unquotedReferencedKeys)
    {
        for (var index = 0; index < template.Length;)
        {
            if (template[index] != '{')
            {
                index++;
                continue;
            }

            if (index + 1 < template.Length && template[index + 1] == '?')
            {
                var keyStart = index + 2;
                var colon = template.IndexOf(':', keyStart);
                if (colon < 0)
                {
                    index++;
                    continue;
                }

                // The condition key itself is only ever tested for truthiness, never substituted
                // into the argument text, so it doesn't belong in unquotedReferencedKeys.
                AddReferencedKey(template[keyStart..colon].Trim(), referencedKeys);

                var valueStart = colon + 1;
                var close = ModuleLaunchArgumentBuilder.FindConditionalClose(template, valueStart);
                if (close < 0)
                {
                    index++;
                    continue;
                }

                CollectAllPlaceholderKeys(template[valueStart..close], referencedKeys, unquotedReferencedKeys);
                index = close + 1;
            }
            else
            {
                var close = template.IndexOf('}', index + 1);
                if (close < 0)
                {
                    index++;
                    continue;
                }

                var inner = template[(index + 1)..close];
                if (inner.StartsWith("quote:", StringComparison.OrdinalIgnoreCase))
                {
                    AddReferencedKey(inner["quote:".Length..].Trim(), referencedKeys);
                }
                else if (!inner.StartsWith("?", StringComparison.Ordinal))
                {
                    AddReferencedKey(inner.Trim(), referencedKeys);
                    AddReferencedKey(inner.Trim(), unquotedReferencedKeys);
                }

                index = close + 1;
            }
        }
    }

    private static void AddReferencedKey(string key, HashSet<string> referencedKeys)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            referencedKeys.Add(key.Trim());
        }
    }

    private static void ValidateRelativePath(
        string? value,
        string path,
        bool allowCurrentDirectory,
        ModuleValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (!allowCurrentDirectory && (trimmed == "." || trimmed == "./" || trimmed == ".\\"))
        {
            Error(result, "path.currentDirectory", path, "Path must point to a file or child path, not the install directory itself.");
            return;
        }

        if (Path.IsPathRooted(trimmed))
        {
            Error(result, "path.rooted", path, "Path must be relative to the server install directory.");
            return;
        }

        var validationRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WindowsGSHModuleValidation"));
        var fullPath = Path.GetFullPath(Path.Combine(validationRoot, NormalizePath(trimmed)));
        var trimmedRoot = Path.TrimEndingDirectorySeparator(validationRoot);
        if (!string.Equals(fullPath, trimmedRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Error(result, "path.escape", path, "Path cannot escape the server install directory.");
        }
    }

    private static void Require(
        string? value,
        string path,
        string code,
        string message,
        ModuleValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Error(result, code, path, message);
        }
    }

    private static bool IsEmptyDefault(object? value)
    {
        if (value == null)
        {
            return true;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Null ||
                element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString());
        }

        return value is string text && string.IsNullOrWhiteSpace(text);
    }

    private static bool IsSecretLikeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("gslt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadNumber(object? value, out double number)
    {
        number = 0;
        return value switch
        {
            byte typed => SetNumber(typed, out number),
            short typed => SetNumber(typed, out number),
            int typed => SetNumber(typed, out number),
            long typed => SetNumber(typed, out number),
            float typed => SetNumber(typed, out number),
            double typed => SetNumber(typed, out number),
            decimal typed => SetNumber((double)typed, out number),
            string text => double.TryParse(text, out number),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.TryGetDouble(out number),
            JsonElement element when element.ValueKind == JsonValueKind.String => double.TryParse(element.GetString(), out number),
            _ => false
        };
    }

    private static bool SetNumber(double value, out double number)
    {
        number = value;
        return true;
    }

    private static ConfigFieldType ParseFieldType(string? type)
    {
        return Enum.TryParse<ConfigFieldType>(type, ignoreCase: true, out var parsed) ? parsed : ConfigFieldType.Text;
    }

    private static bool IsPlainTextStorageType(ConfigFieldType type) =>
        type is not (ConfigFieldType.Password
            or ConfigFieldType.Number
            or ConfigFieldType.Boolean
            or ConfigFieldType.Select
            or ConfigFieldType.MultiSelect
            or ConfigFieldType.Port);

    private static string NormalizePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static void Error(ModuleValidationResult result, string code, string path, string message)
    {
        result.Add(ModuleValidationSeverity.Error, code, path, message);
    }

    private static void Warning(ModuleValidationResult result, string code, string path, string message)
    {
        result.Add(ModuleValidationSeverity.Warning, code, path, message);
    }
}
