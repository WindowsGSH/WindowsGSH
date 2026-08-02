using System.Text.Json;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleUpdateStateWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Write_adds_module_update_metadata_without_removing_existing_config()
    {
        var path = WriteConfig("""
            {
              "id": "1",
              "name": "Paper",
              "settings": {
                "minecraft.version": "1.21.11"
              }
            }
            """);

        ModuleUpdateStateWriter.Write(
            path,
            new ModuleUpdateResult(
                Updated: true,
                Message: "Downloaded Paper build.",
                InstalledBuildId: "123",
                DownloadedFileName: "paper-1.21.11-123.jar"),
            DateTimeOffset.Parse("2026-05-24T10:30:00+00:00"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("Paper", root.GetProperty("name").GetString());
        Assert.Equal("1.21.11", root.GetProperty("settings").GetProperty("minecraft.version").GetString());

        var update = root.GetProperty("update");
        Assert.True(update.GetProperty("lastUpdated").GetBoolean());
        Assert.Equal("Downloaded Paper build.", update.GetProperty("lastMessage").GetString());
        Assert.Equal("123", update.GetProperty("lastInstalledBuildId").GetString());
        Assert.Equal("paper-1.21.11-123.jar", update.GetProperty("lastDownloadedFileName").GetString());
        Assert.Equal("2026-05-24T10:30:00.0000000+00:00", update.GetProperty("lastUpdateUtc").GetString());
    }

    [Fact]
    public void Write_preserves_previous_build_metadata_when_result_does_not_replace_it()
    {
        var path = WriteConfig("""
            {
              "update": {
                "lastInstalledBuildId": "123",
                "lastDownloadedFileName": "paper.jar"
              }
            }
            """);

        ModuleUpdateStateWriter.Write(
            path,
            new ModuleUpdateResult(Updated: false, Message: "Already current."),
            DateTimeOffset.Parse("2026-05-24T11:00:00+00:00"));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var update = document.RootElement.GetProperty("update");
        Assert.False(update.GetProperty("lastUpdated").GetBoolean());
        Assert.Equal("Already current.", update.GetProperty("lastMessage").GetString());
        Assert.Equal("123", update.GetProperty("lastInstalledBuildId").GetString());
        Assert.Equal("paper.jar", update.GetProperty("lastDownloadedFileName").GetString());
    }

    [Fact]
    public void Write_rejects_non_object_config()
    {
        var path = WriteConfig("""["not", "object"]""");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModuleUpdateStateWriter.Write(path, new ModuleUpdateResult(true, "done")));

        Assert.Contains("Server config is not a JSON object", exception.Message);
    }

    private string WriteConfig(string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ServerConfig.json");
        File.WriteAllText(path, content);
        return path;
    }
}
