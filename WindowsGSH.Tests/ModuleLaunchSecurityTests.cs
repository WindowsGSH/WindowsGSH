using System.Diagnostics;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleLaunchSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));

    public ModuleLaunchSecurityTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"..\outside.exe")]
    [InlineData("nested/../../outside.exe")]
    public void Module_start_path_must_stay_inside_install_root(string startPath)
    {
        var instance = CreateInstance();

        Assert.Throws<InvalidOperationException>(() =>
            ModuleLaunchPolicy.ResolveExecutableInsideInstallRoot(instance, startPath));
    }

    [Fact]
    public void Module_start_path_resolves_normal_nested_executable()
    {
        var instance = CreateInstance();

        var result = ModuleLaunchPolicy.ResolveExecutableInsideInstallRoot(instance, @"bin\server.exe");

        Assert.Equal(Path.Combine(_root, "bin", "server.exe"), result);
    }

    [Fact]
    public void Compatibility_arguments_preserve_a_user_value_as_one_argument()
    {
        var startInfo = new ProcessStartInfo("server.exe");

        ModuleLaunchPolicy.AddCompatibilityArguments(startInfo, "--name \"one value\" --flag");

        Assert.Equal(["--name", "one value", "--flag"], startInfo.ArgumentList);
    }

    private ServerInstance CreateInstance() => new(
        "id", "name", "module", Path.Combine(_root, "metadata"), _root,
        Path.Combine(_root, "metadata", "ServerConfig.json"),
        new Dictionary<string, object?>());

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
            Debug.WriteLine($"Test directory cleanup failed: {ex.Message}");
        }
    }
}
