using WindowsGSH.Core.Config;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PropertiesFileTests : IDisposable
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
    public void Load_reads_string_bool_and_int_values()
    {
        var path = Write("""
            server-port=25565
            online-mode=true
            motd=A PaperMC Server
            """);

        var properties = PropertiesFile.Load(path);

        Assert.Equal(25565, properties.GetInt32("server-port"));
        Assert.True(properties.GetBoolean("online-mode"));
        Assert.Equal("A PaperMC Server", properties.GetString("motd"));
    }

    [Fact]
    public void Save_preserves_comments_and_unknown_properties()
    {
        var path = Write("""
            # Minecraft server properties
            unknown-setting=keep-me
            server-port=25565
            """);

        var properties = PropertiesFile.Load(path);
        properties.Set("server-port", 25566);
        properties.Set("motd", "Updated");
        properties.Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("# Minecraft server properties", text);
        Assert.Contains("unknown-setting=keep-me", text);
        Assert.Contains("server-port=25566", text);
        Assert.Contains("motd=Updated", text);
    }

    [Fact]
    public void Save_creates_missing_file()
    {
        var path = Path.Combine(_root, "server.properties");
        var properties = PropertiesFile.Load(path);

        properties.Set("enable-query", true);
        properties.Save(path);

        Assert.Equal("enable-query=true", File.ReadAllText(path).Trim());
    }

    [Fact]
    public void Set_updates_last_duplicate_key_because_that_is_effective_value()
    {
        var path = Write("""
            server-port=25565
            motd=Original
            server-port=25566
            """);

        var properties = PropertiesFile.Load(path);
        properties.Set("server-port", 25567);
        properties.Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("server-port=25565", text);
        Assert.Contains("server-port=25567", text);
        Assert.DoesNotContain("server-port=25566", text);
        Assert.Equal(25567, PropertiesFile.Load(path).GetInt32("server-port"));
    }

    [Fact]
    public void Set_preserves_existing_separator_for_effective_key()
    {
        var path = Write("""
            query.port=25565
            query.port:25566
            """);

        var properties = PropertiesFile.Load(path);
        properties.Set("query.port", 25567);
        properties.Save(path);

        var text = File.ReadAllText(path);
        Assert.Contains("query.port=25565", text);
        Assert.Contains("query.port:25567", text);
    }

    [Fact]
    public void Save_cleans_up_temporary_files_after_replace()
    {
        var path = Write("motd=Original");
        var properties = PropertiesFile.Load(path);

        properties.Set("motd", "Updated");
        properties.Save(path);

        Assert.Equal("motd=Updated", File.ReadAllText(path).Trim());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private string Write(string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "server.properties");
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        return path;
    }
}
