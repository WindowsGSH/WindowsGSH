using System.Diagnostics;
using System.IO;

namespace WindowsGSH.Core;

public static class AppPaths
{
    public static string AppDirectory { get; } = GetAppDirectory();

    public static string GetPath(params string[] parts)
    {
        return Path.Combine([AppDirectory, .. parts]);
    }

    private static string GetAppDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetDirectoryName(processPath)!;
        }

        var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
        return !string.IsNullOrWhiteSpace(mainModulePath)
            ? Path.GetDirectoryName(mainModulePath)!
            : AppContext.BaseDirectory;
    }
}
