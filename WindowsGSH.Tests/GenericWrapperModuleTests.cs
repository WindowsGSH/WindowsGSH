using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class GenericWrapperModuleTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gsh-wrapper-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GenericWrapperModule _module = new();

    public GenericWrapperModuleTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task CreateStartInfo_uses_configured_executable_and_arguments()
    {
        var exe = Path.Join(_root, "server.exe");
        File.WriteAllText(exe, "");
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = "server.exe",
            [GenericWrapperModule.LaunchArgumentsKey] = "--world alpha",
            [GenericWrapperModule.LaunchModeKey] = "Direct",
            [GenericWrapperModule.RedirectConsoleKey] = true
        });

        var startInfo = await _module.CreateStartInfoAsync(instance, CancellationToken.None);

        Assert.Equal(exe, startInfo.FileName);
        Assert.Equal(["--world", "alpha"], startInfo.ArgumentList);
        Assert.Equal(_root, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task CreateStartInfo_wraps_batch_files_with_cmd()
    {
        var batch = Path.Join(_root, "start server.bat");
        File.WriteAllText(batch, "@echo off");
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = "start server.bat",
            [GenericWrapperModule.LaunchArgumentsKey] = "nogui",
            [GenericWrapperModule.LaunchModeKey] = "Auto"
        });

        var startInfo = await _module.CreateStartInfoAsync(instance, CancellationToken.None);

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/d", startInfo.ArgumentList[0]);
        Assert.Equal("/s", startInfo.ArgumentList[1]);
        Assert.Equal("/c", startInfo.ArgumentList[2]);
        Assert.Contains("start server.bat", startInfo.ArgumentList[3], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nogui", startInfo.ArgumentList[3], StringComparison.Ordinal);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void IsInstallValid_accepts_absolute_launch_target()
    {
        var exe = Path.Join(_root, "absolute.exe");
        File.WriteAllText(exe, "");
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = exe
        });

        Assert.True(_module.IsInstallValid(instance));
    }

    [Fact]
    public async Task Direct_arguments_preserve_spaces_and_quotes_as_single_tokens()
    {
        var exe = Path.Join(_root, "server with spaces.exe");
        File.WriteAllText(exe, "");
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = exe,
            [GenericWrapperModule.LaunchArgumentsKey] = "--name \"alpha server\" --message \"say \\\"hi\\\"\"",
            [GenericWrapperModule.LaunchModeKey] = "Direct"
        });

        var startInfo = await _module.CreateStartInfoAsync(instance, CancellationToken.None);

        Assert.Equal(["--name", "alpha server", "--message", "say \"hi\""], startInfo.ArgumentList);
    }

    [Fact]
    public async Task PowerShell_target_with_spaces_uses_fixed_argument_tokens()
    {
        var script = Path.Join(_root, "start server.ps1");
        File.WriteAllText(script, "");
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = script,
            [GenericWrapperModule.LaunchArgumentsKey] = "-Name \"alpha server\"",
            [GenericWrapperModule.LaunchModeKey] = "PowerShell"
        });

        var startInfo = await _module.CreateStartInfoAsync(instance, CancellationToken.None);

        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Name", "alpha server"], startInfo.ArgumentList);
    }

    [Fact]
    public async Task Invalid_launch_mode_and_working_directory_are_rejected()
    {
        var exe = Path.Join(_root, "server.exe");
        File.WriteAllText(exe, "");
        var invalidMode = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = exe,
            [GenericWrapperModule.LaunchModeKey] = "NotAMode"
        });
        var invalidWorkingDirectory = CreateInstance(new Dictionary<string, object?>
        {
            [GenericWrapperModule.LaunchPathKey] = exe,
            [GenericWrapperModule.WorkingDirectoryKey] = "missing"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _module.CreateStartInfoAsync(invalidMode, CancellationToken.None));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _module.CreateStartInfoAsync(invalidWorkingDirectory, CancellationToken.None));
    }

    [Fact]
    public async Task PreviewImportAsync_selects_preferred_start_file()
    {
        File.WriteAllText(Path.Join(_root, "start.bat"), "@echo off");

        var preview = await _module.PreviewImportAsync(_root, CancellationToken.None);

        Assert.Equal(GenericWrapperModule.ModuleId, _module.Id);
        Assert.Equal("start.bat", preview.Settings[GenericWrapperModule.LaunchPathKey]);
        Assert.Contains(preview.Warnings, warning => warning.Contains("Review ports", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Main_port_uses_either_transport_because_imported_server_protocol_is_unknown()
    {
        var gamePort = Assert.Single(_module.GetPorts(), port => port.Id == "game");

        Assert.Equal(PortProtocol.Either, gamePort.Protocol);
    }

    private ServerInstance CreateInstance(IReadOnlyDictionary<string, object?> overrides)
    {
        var settings = _module.GetConfigFields()
            .ToDictionary(field => field.Key, field => field.DefaultValue, StringComparer.OrdinalIgnoreCase);
        foreach (var item in overrides)
        {
            settings[item.Key] = item.Value;
        }

        return new ServerInstance(
            "wrapper-test",
            "Wrapper Test",
            _module.Id,
            Path.Join(_root, "server-folder"),
            _root,
            Path.Join(_root, "ServerConfig.json"),
            settings);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Test directory cleanup failed: {ex.Message}");
        }
    }
}
