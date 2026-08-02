using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PalworldModuleTests
{
    [Fact]
    public async Task Palworld_module_loads_fields_and_writes_config()
    {
        if (!Directory.Exists(GetPalworldModulePath()))
        {
            return;
        }

        var module = LoadPalworldModule();
        var fields = module.GetConfigFields();
        var settings = fields.ToDictionary(
            field => field.Key,
            field => field.DefaultValue,
            StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var installPath = Path.Combine(root, "files");
        var executablePath = Path.Combine(installPath, "Pal", "Binaries", "Win64", "PalServer-Win64-Shipping-Cmd.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, string.Empty);
        var instance = new ServerInstance(
            "palworld-test",
            module.GetServerName(settings),
            module.Id,
            root,
            installPath,
            Path.Combine(root, "ServerConfig.json"),
            settings);

        await module.WriteConfigFileSettingsAsync(instance, CancellationToken.None);
        var startInfo = await module.CreateStartInfoAsync(instance, CancellationToken.None);

        Assert.NotEmpty(fields);
        Assert.True(module.IsInstallValid(instance));
        Assert.True(File.Exists(Path.Combine(installPath, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini")));
        Assert.Equal(executablePath, startInfo.FileName);
        Assert.Contains("-port=8211", startInfo.Arguments);
    }

    private static IGameServerModule LoadPalworldModule()
    {
        return new CSharpModuleLoader().Load(GetPalworldModulePath());
    }

    private static string GetPalworldModulePath()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "release",
            "modules",
            "installed",
            "palworld"));
    }
}
