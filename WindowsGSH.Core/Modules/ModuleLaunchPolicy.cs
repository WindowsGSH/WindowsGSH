using System.Diagnostics;

namespace WindowsGSH.Core.Modules;

internal static class ModuleLaunchPolicy
{
    public static string ResolveExecutableInsideInstallRoot(ServerInstance instance, string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            throw new InvalidOperationException("Module runtime startPath is required.");
        }

        var normalized = startPath.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidOperationException("Module runtime startPath must be relative to the server install folder.");
        }

        var installRoot = Path.GetFullPath(instance.InstallPath);
        // A module start path is untrusted until both the rooted-path guard
        // above and the canonical containment check below have succeeded.
        var executable = Path.GetFullPath(Path.Join(installRoot, normalized));
        if (!ModuleImportPathPlanner.IsPathInsideDirectory(installRoot, executable))
        {
            throw new InvalidOperationException("Module runtime startPath escapes the server install folder.");
        }

        return executable;
    }

    public static void AddCompatibilityArguments(ProcessStartInfo startInfo, string? arguments)
    {
        foreach (var argument in WindowsCommandLineParser.Split(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}
