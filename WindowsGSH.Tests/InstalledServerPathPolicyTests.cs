using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class InstalledServerPathPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly string _serversRoot;

    public InstalledServerPathPolicyTests()
    {
        _root = Path.Join(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        _serversRoot = Path.Join(_root, "servers");
        Directory.CreateDirectory(_serversRoot);
    }

    [Fact]
    public void Valid_server_uses_fixed_config_file_inside_servers_root()
    {
        var folder = Path.Join(_serversRoot, "1");
        var server = CreateServer(folder, Path.Join(folder, "ServerConfig.json"), Path.Join(folder, "files"));

        var valid = InstalledServerPathPolicy.TryGetExpectedConfigPath(
            server, _serversRoot, out var configPath, out _);

        Assert.True(valid);
        Assert.Equal(Path.Join(folder, "ServerConfig.json"), configPath);
    }

    [Fact]
    public void Config_path_outside_server_folder_is_rejected()
    {
        var folder = Path.Join(_serversRoot, "1");
        var server = CreateServer(folder, Path.Join(_root, "outside.json"), Path.Join(folder, "files"));

        Assert.False(InstalledServerPathPolicy.TryGetExpectedConfigPath(
            server, _serversRoot, out _, out _));
    }

    [Fact]
    public void Server_folder_outside_servers_root_is_rejected()
    {
        var folder = Path.Join(_root, "outside", "1");
        var server = CreateServer(folder, Path.Join(folder, "ServerConfig.json"), Path.Join(folder, "files"));

        Assert.False(InstalledServerPathPolicy.TryGetExpectedConfigPath(
            server, _serversRoot, out _, out _));
    }

    [Fact]
    public void Adopted_server_can_keep_external_install_path_when_metadata_is_inside_servers_root()
    {
        var folder = Path.Join(_serversRoot, "adopted");
        var externalInstall = Path.Join(_root, "external-game-files");
        var server = CreateServer(folder, Path.Join(folder, "ServerConfig.json"), externalInstall);

        Assert.True(InstalledServerPathPolicy.TryGetExpectedConfigPath(
            server, _serversRoot, out _, out _));
    }

    private static InstalledServer CreateServer(string serverFolder, string configPath, string installPath) => new(
        "1", "Test", "test", "native", serverFolder, installPath, configPath,
        "127.0.0.1", "1", "", "public", "0", "", "", "", "", "Offline", "", false, "", null,
        true, ServerRuntimeStatus.Offline, "Offline", "", false, "", "", "", true, true, true, false);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Test directory cleanup failed: {ex.Message}");
        }
    }
}
