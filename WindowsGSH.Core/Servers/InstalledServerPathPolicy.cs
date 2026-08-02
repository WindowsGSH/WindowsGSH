namespace WindowsGSH.Core.Servers;

public static class InstalledServerPathPolicy
{
    public const string ConfigFileName = "ServerConfig.json";

    public static bool TryGetExpectedConfigPath(
        InstalledServer server,
        string serversRoot,
        out string configPath,
        out string error)
    {
        configPath = string.Empty;
        error = string.Empty;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serversRoot));
            var serverFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.ServerFolder));
            if (!serverFolder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "Server folder is outside the WindowsGSH servers directory.";
                return false;
            }

            // ConfigFileName is a fixed application-owned leaf name. Path.Join
            // avoids any future rooted-segment replacement semantics.
            configPath = Path.GetFullPath(Path.Join(serverFolder, ConfigFileName));
            var storedConfigPath = Path.GetFullPath(server.ConfigPath);
            if (!string.Equals(configPath, storedConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Server configuration path must be {ConfigFileName} inside its server folder.";
                configPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"Server paths are invalid: {ex.Message}";
            configPath = string.Empty;
            return false;
        }
    }
}
