using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SteamAppManifestReaderTests : IDisposable
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
    public void Inspect_reports_missing_manifest()
    {
        var result = SteamAppManifestReader.Inspect(_root, "123");

        Assert.Equal(SteamAppManifestState.Missing, result.State);
        Assert.Equal(string.Empty, result.BuildId);
    }

    [Fact]
    public void Inspect_reads_valid_manifest()
    {
        WriteManifest("123", """
            "AppState"
            {
                "appid" "123"
                "buildid" "456"
            }
            """);

        var result = SteamAppManifestReader.Inspect(_root, "123");

        Assert.Equal(SteamAppManifestState.Valid, result.State);
        Assert.Equal("456", result.BuildId);
    }

    [Theory]
    [InlineData("\"AppState\" { \"appid\" \"999\" \"buildid\" \"456\" }")]
    [InlineData("\"AppState\" { \"appid\" \"123\" }")]
    [InlineData("not an acf manifest")]
    public void Inspect_reports_corrupt_manifest(string content)
    {
        WriteManifest("123", content);

        var result = SteamAppManifestReader.Inspect(_root, "123");

        Assert.Equal(SteamAppManifestState.Corrupt, result.State);
        Assert.Equal(string.Empty, result.BuildId);
    }

    private void WriteManifest(string appId, string content)
    {
        var steamApps = Path.Combine(_root, "steamapps");
        Directory.CreateDirectory(steamApps);
        File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{appId}.acf"), content);
    }
}
