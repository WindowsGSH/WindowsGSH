using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Java;

public sealed class ManagedJavaStore
{
    private readonly ManagedJavaInstallService _installService;
    private readonly ManagedJavaCatalogue _catalogue;

    public ManagedJavaStore()
        : this(new ManagedJavaInstallService(), new ManagedJavaCatalogue())
    {
    }

    public ManagedJavaStore(ManagedJavaInstallService installService, ManagedJavaCatalogue catalogue)
    {
        _installService = installService;
        _catalogue = catalogue;
    }

    public ManagedJavaCatalogue Catalogue => _catalogue;

    public ManagedJavaInstallService InstallService => _installService;

    public IReadOnlyList<InstalledManagedRuntime> GetInstalledRuntimes()
    {
        return _catalogue.GetReleases()
            .Where(release => _installService.IsInstalled(release))
            .Select(release => new InstalledManagedRuntime(
                release,
                _installService.GetJavaExecutablePath(release)))
            .ToArray();
    }

    public string? TryGetJavaExecutablePath(string runtimeId)
    {
        var release = ResolveRelease(runtimeId);
        if (release == null || !_installService.IsInstalled(release))
        {
            return null;
        }

        return _installService.GetJavaExecutablePath(release);
    }

    public bool IsReferenced(string runtimeId, IEnumerable<ServerJavaSettings> allServerSettings)
    {
        var managedJavaPath = TryGetJavaExecutablePath(runtimeId);
        return allServerSettings.Any(settings => ReferencesRuntime(settings, runtimeId, managedJavaPath));
    }

    public bool CanRemove(string runtimeId, IEnumerable<ServerJavaSettings> allServerSettings)
    {
        return !IsReferenced(runtimeId, allServerSettings);
    }

    public IReadOnlyList<string> GetReferencingServers(string runtimeId, IEnumerable<InstalledServer> allServers)
    {
        var names = new List<string>();
        var managedJavaPath = TryGetJavaExecutablePath(runtimeId);
        foreach (var server in allServers)
        {
            try
            {
                var instance = ServerInstanceFactory.Load(server);
                if (ReferencesRuntime(instance.AppSettings.Java, runtimeId, managedJavaPath))
                {
                    names.Add(server.Name);
                }
            }
            catch
            {
                // skip servers whose config cannot be read
            }
        }

        return names;
    }

    public void Remove(string runtimeId, IEnumerable<ServerJavaSettings> allServerSettings)
    {
        if (IsReferenced(runtimeId, allServerSettings))
        {
            throw new InvalidOperationException(
                $"Cannot remove runtime '{runtimeId}' because one or more servers are configured to use it. " +
                "Reassign those servers to a different Java runtime first.");
        }

        var release = ResolveRelease(runtimeId);
        if (release != null)
        {
            _installService.Remove(release);
        }
    }

    private static bool ReferencesRuntime(
        ServerJavaSettings settings,
        string runtimeId,
        string? managedJavaPath)
    {
        return string.Equals(settings.ManagedRuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase) ||
               PathsMatch(settings.RuntimePath, managedJavaPath);
    }

    private ManagedJavaRelease? ResolveRelease(string runtimeId)
    {
        return _catalogue.GetRelease(runtimeId) ?? ReconstructRelease(runtimeId);
    }

    private static ManagedJavaRelease? ReconstructRelease(string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return null;
        }

        var parts = runtimeId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 ||
            !int.TryParse(parts[^2], out var majorVersion) ||
            majorVersion <= 0)
        {
            return null;
        }

        var vendor = string.Join('-', parts[..^2]);
        var architecture = parts[^1];
        if (!IsSafePathSegment(vendor) || !IsSafePathSegment(architecture))
        {
            return null;
        }

        return new ManagedJavaRelease(
            vendor,
            majorVersion,
            architecture,
            "Installed",
            string.Empty,
            string.Empty,
            0,
            string.Empty,
            string.Empty);
    }

    private static bool IsSafePathSegment(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value is not "." and not ".." &&
               value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               !value.Contains(Path.DirectorySeparatorChar) &&
               !value.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool PathsMatch(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed record InstalledManagedRuntime(
    ManagedJavaRelease Release,
    string JavaExecutablePath)
{
    public string DisplayText =>
        $"{Release.Vendor} Java {Release.MajorVersion} ({Release.ReleaseVersion}) [{Release.Architecture}]";
}
