using System.IO;
using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Steam;

public static class SteamAppManifestReader
{
    public static string ReadBuildId(string installPath, string appId)
    {
        return Inspect(installPath, appId).BuildId;
    }

    public static SteamAppManifestInspection Inspect(string installPath, string appId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(appId))
        {
            return new SteamAppManifestInspection(SteamAppManifestState.Missing, string.Empty, string.Empty);
        }

        var manifestPath = Path.Combine(installPath, "steamapps", $"appmanifest_{appId}.acf");
        if (!File.Exists(manifestPath))
        {
            return new SteamAppManifestInspection(SteamAppManifestState.Missing, manifestPath, string.Empty);
        }

        try
        {
            var content = File.ReadAllText(manifestPath);
            var appIdMatch = Regex.Match(content, "\"appid\"\\s+\"(?<appid>[^\"]+)\"", RegexOptions.IgnoreCase);
            var buildMatch = Regex.Match(content, "\"buildid\"\\s+\"(?<buildid>[^\"]+)\"", RegexOptions.IgnoreCase);
            if (!appIdMatch.Success ||
                !string.Equals(appIdMatch.Groups["appid"].Value, appId, StringComparison.Ordinal) ||
                !buildMatch.Success ||
                string.IsNullOrWhiteSpace(buildMatch.Groups["buildid"].Value))
            {
                return new SteamAppManifestInspection(SteamAppManifestState.Corrupt, manifestPath, string.Empty);
            }

            return new SteamAppManifestInspection(
                SteamAppManifestState.Valid,
                manifestPath,
                buildMatch.Groups["buildid"].Value);
        }
        catch
        {
            return new SteamAppManifestInspection(SteamAppManifestState.Corrupt, manifestPath, string.Empty);
        }
    }
}

public sealed record SteamAppManifestInspection(
    SteamAppManifestState State,
    string ManifestPath,
    string BuildId);

public enum SteamAppManifestState
{
    Missing,
    Valid,
    Corrupt
}
