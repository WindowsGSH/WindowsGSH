using System.Reflection;

namespace WindowsGSH.Core.Diagnostics;

public static class AppVersionInfo
{
    public static string DisplayVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var version = string.IsNullOrWhiteSpace(informational)
                ? assembly.GetName().Version?.ToString()
                : informational.Split('+', 2)[0];
            return string.IsNullOrWhiteSpace(version) ? "development" : version;
        }
    }
}
