using System.Reflection;

namespace WindowsGSH.Core.Modules;

public static class ModuleCompatibility
{
    public const string CurrentModuleApiVersion = "1.0";

    public static Version CurrentWindowsGshVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ??
        typeof(ModuleCompatibility).Assembly.GetName().Version ??
        new Version(1, 0, 0, 0);

    public static ModuleCompatibilityState Evaluate(ModuleManifest manifest)
    {
        return Evaluate(manifest, CurrentWindowsGshVersion);
    }

    public static ModuleCompatibilityState Evaluate(ModuleManifest manifest, Version currentWindowsGshVersion)
    {
        var messages = new List<ModuleValidationMessage>();
        var moduleApiVersion = NormalizeVersionText(manifest.ModuleApiVersion);
        var currentApiVersion = ParseRequiredVersion(CurrentModuleApiVersion);
        var status = ModuleCompatibilityStatus.Compatible;

        if (string.IsNullOrWhiteSpace(moduleApiVersion))
        {
            messages.Add(Warning(
                "compat.moduleApiVersion.missing",
                "manifest.moduleApiVersion",
                $"Module does not declare moduleApiVersion. Treating it as compatible with WindowsGSH module API {CurrentModuleApiVersion}."));
            status = ModuleCompatibilityStatus.Legacy;
        }
        else if (!TryParseVersion(moduleApiVersion, out var parsedApiVersion))
        {
            messages.Add(Error(
                "compat.moduleApiVersion.invalid",
                "manifest.moduleApiVersion",
                $"moduleApiVersion '{manifest.ModuleApiVersion}' is not a valid version."));
            status = ModuleCompatibilityStatus.Incompatible;
        }
        else if (parsedApiVersion.CompareTo(currentApiVersion) > 0)
        {
            messages.Add(Error(
                "compat.moduleApiVersion.future",
                "manifest.moduleApiVersion",
                $"Module requires module API {moduleApiVersion}, but this WindowsGSH supports {CurrentModuleApiVersion}."));
            status = ModuleCompatibilityStatus.Incompatible;
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinimumWindowsGshVersion))
        {
            var minimumText = manifest.MinimumWindowsGshVersion.Trim();
            if (!TryParseVersion(minimumText, out var minimumVersion))
            {
                messages.Add(Error(
                    "compat.minimumWindowsGshVersion.invalid",
                    "manifest.minimumWindowsGshVersion",
                    $"minimumWindowsGshVersion '{manifest.MinimumWindowsGshVersion}' is not a valid version."));
                status = ModuleCompatibilityStatus.Incompatible;
            }
            else if (currentWindowsGshVersion.CompareTo(minimumVersion) < 0)
            {
                messages.Add(Error(
                    "compat.minimumWindowsGshVersion.unsupported",
                    "manifest.minimumWindowsGshVersion",
                    $"Module requires WindowsGSH {minimumText} or newer. Current version is {FormatVersion(currentWindowsGshVersion)}."));
                status = ModuleCompatibilityStatus.Incompatible;
            }
        }

        var supportedVersions = manifest.SupportedWindowsGshVersions ?? [];
        if (supportedVersions.Count > 0)
        {
            var validRanges = new List<string>();
            for (var index = 0; index < supportedVersions.Count; index++)
            {
                var supported = supportedVersions[index]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(supported) || !IsSupportedVersionPattern(supported))
                {
                    messages.Add(Error(
                        "compat.supportedWindowsGshVersions.invalid",
                        $"manifest.supportedWindowsGshVersions[{index}]",
                        $"Supported WindowsGSH version '{supportedVersions[index]}' is not a valid version or wildcard pattern."));
                    status = ModuleCompatibilityStatus.Incompatible;
                    continue;
                }

                validRanges.Add(supported);
            }

            if (validRanges.Count > 0 && !validRanges.Any(pattern => MatchesSupportedVersion(pattern, currentWindowsGshVersion)))
            {
                messages.Add(Error(
                    "compat.supportedWindowsGshVersions.unsupported",
                    "manifest.supportedWindowsGshVersions",
                    $"Module supports WindowsGSH {string.Join(", ", validRanges)}, but current version is {FormatVersion(currentWindowsGshVersion)}."));
                status = ModuleCompatibilityStatus.Incompatible;
            }
        }

        return new ModuleCompatibilityState(
            status,
            moduleApiVersion,
            CurrentModuleApiVersion,
            FormatVersion(currentWindowsGshVersion),
            messages);
    }

    public static string ToDisplayText(ModuleCompatibilityState state)
    {
        return state.Status switch
        {
            ModuleCompatibilityStatus.Legacy => $"Compatibility: legacy module, no API version declared. Current API {state.CurrentModuleApiVersion}.",
            ModuleCompatibilityStatus.Incompatible => "Compatibility: incompatible. " + string.Join(" ", state.Messages
                .Where(message => message.Severity == ModuleValidationSeverity.Error)
                .Select(message => message.Message)),
            _ => $"Compatibility: API {state.ModuleApiVersion ?? CurrentModuleApiVersion} supported by WindowsGSH {state.CurrentWindowsGshVersion}."
        };
    }

    private static bool IsSupportedVersionPattern(string value)
    {
        if (value.EndsWith(".*", StringComparison.Ordinal))
        {
            return TryParseVersion(value[..^2], out _);
        }

        return TryParseVersion(value, out _);
    }

    private static bool MatchesSupportedVersion(string pattern, Version current)
    {
        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefixText = pattern[..^2].Trim().TrimStart('v', 'V');
            var parts = prefixText.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 &&
                int.TryParse(parts[0], out var major))
            {
                return current.Major == major;
            }

            return TryParseVersion(prefixText, out var prefix) &&
                current.Major == prefix.Major &&
                current.Minor == prefix.Minor;
        }

        return TryParseVersion(pattern, out var exact) &&
            current.Major == exact.Major &&
            current.Minor == exact.Minor &&
            current.Build == exact.Build;
    }

    private static string? NormalizeVersionText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('v', 'V');
    }

    private static Version ParseRequiredVersion(string value)
    {
        return TryParseVersion(value, out var version)
            ? version
            : throw new InvalidOperationException($"Internal version '{value}' is not valid.");
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var normalized = value.Trim().TrimStart('v', 'V');
        if (int.TryParse(normalized, out var majorOnly) && majorOnly >= 0)
        {
            version = new Version(majorOnly, 0);
            return true;
        }

        if (!Version.TryParse(normalized, out var parsed))
        {
            return false;
        }

        version = NormalizeVersion(parsed);
        return true;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static string FormatVersion(Version version)
    {
        return version.Build > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private static ModuleValidationMessage Error(string code, string path, string message)
    {
        return new ModuleValidationMessage(ModuleValidationSeverity.Error, code, path, message);
    }

    private static ModuleValidationMessage Warning(string code, string path, string message)
    {
        return new ModuleValidationMessage(ModuleValidationSeverity.Warning, code, path, message);
    }
}

public enum ModuleCompatibilityStatus
{
    Compatible,
    Legacy,
    Incompatible
}

public sealed record ModuleCompatibilityState(
    ModuleCompatibilityStatus Status,
    string? ModuleApiVersion,
    string CurrentModuleApiVersion,
    string CurrentWindowsGshVersion,
    IReadOnlyList<ModuleValidationMessage> Messages)
{
    public bool IsCompatible => Status != ModuleCompatibilityStatus.Incompatible;
}
