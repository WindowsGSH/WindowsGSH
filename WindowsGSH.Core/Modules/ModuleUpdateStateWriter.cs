using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Modules;

public static class ModuleUpdateStateWriter
{
    public static void Write(string configPath, ModuleUpdateResult result, DateTimeOffset? updatedUtc = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
            ?? throw new InvalidOperationException("Server config is not a JSON object.");

        var update = root["update"] as JsonObject;
        if (update == null)
        {
            update = [];
            root["update"] = update;
        }

        update["lastUpdateUtc"] = (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O");
        update["lastUpdated"] = result.Updated;
        update["lastMessage"] = result.Message;

        if (!string.IsNullOrWhiteSpace(result.InstalledBuildId))
        {
            update["lastInstalledBuildId"] = result.InstalledBuildId;
        }

        if (!string.IsNullOrWhiteSpace(result.DownloadedFileName))
        {
            update["lastDownloadedFileName"] = result.DownloadedFileName;
        }

        AtomicFile.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
