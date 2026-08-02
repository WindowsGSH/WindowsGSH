using System.Diagnostics;
using System.Text.Json;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerImportDeleteServiceTests
{
    [Fact]
    public async Task ImportAsync_Rejects_ModuleIdMismatch()
    {
        using var workspace = new TestWorkspace();
        var module = new TestModule();
        var service = new ServerImportService(new ServerOperationManager(_ => { }));

        var result = await service.ImportAsync(CreateImportRequest(workspace, module, moduleId: "other"));

        Assert.False(result.Success);
        Assert.Contains("module id", result.Message);
    }

    [Fact]
    public async Task ImportAsync_ReturnsWarning_WhenExistingConfigReadFails()
    {
        using var workspace = new TestWorkspace();
        var module = new TestModule { ThrowOnReadConfig = true };
        File.WriteAllText(Path.Combine(workspace.InstallPath, "server.exe"), "");
        var service = new ServerImportService(new ServerOperationManager(_ => { }));

        var result = await service.ImportAsync(CreateImportRequest(
            workspace,
            module,
            readExistingConfig: true));

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be read"));
    }

    [Fact]
    public async Task ImportAsync_CreatesConfigWithoutCopyingInstallFiles()
    {
        using var workspace = new TestWorkspace();
        var module = new TestModule();
        var marker = Path.Combine(workspace.InstallPath, "server.exe");
        File.WriteAllText(marker, "existing");
        var service = new ServerImportService(new ServerOperationManager(_ => { }));

        var result = await service.ImportAsync(CreateImportRequest(workspace, module));

        Assert.True(result.Success);
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(result.Instance!.ConfigPath));
        Assert.False(Directory.Exists(Path.Combine(result.Instance.ServerFolder, "files")));
        using var document = JsonDocument.Parse(File.ReadAllText(result.Instance.ConfigPath));
        Assert.Equal(workspace.InstallPath, document.RootElement.GetProperty("installPath").GetString());
    }

    [Fact]
    public async Task DeleteAsync_RefusesServerFolderOutsideServersRoot()
    {
        using var workspace = new TestWorkspace();
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        var server = CreateInstalledServer(workspace, serverFolder: outside);
        var service = new ServerDeleteService(new ServerOperationManager(_ => { }), workspace.ServersRoot);

        var result = await service.DeleteAsync(new ServerDeleteRequest(
            server,
            new ServerDeleteOptions(),
            UseOperationCoordinator: false));

        Assert.False(result.Success);
        Assert.Contains("outside", result.Steps.Single().Message);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task DeleteAsync_BlocksRunningServer_WhenRequireStopped()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.ServerFolder);
        var server = CreateInstalledServer(workspace, canStop: true);
        var service = new ServerDeleteService(new ServerOperationManager(_ => { }), workspace.ServersRoot);

        var result = await service.DeleteAsync(new ServerDeleteRequest(
            server,
            new ServerDeleteOptions(RequireStopped: true),
            UseOperationCoordinator: false));

        Assert.False(result.Success);
        Assert.Contains("Stop the server", result.Message);
        Assert.True(Directory.Exists(workspace.ServerFolder));
    }

    [Fact]
    public async Task DeleteAsync_ReportsPartialFailure_AndContinuesSafeSteps()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(workspace.ServerFolder);
        Directory.CreateDirectory(Path.Combine(workspace.ServerFolder, "backups"));
        Directory.CreateDirectory(Path.Combine(workspace.ServerFolder, "logs"));
        File.WriteAllText(workspace.ConfigPath, "{}");
        var outsideInstall = Path.Combine(workspace.Root, "adopted");
        Directory.CreateDirectory(outsideInstall);
        var server = CreateInstalledServer(workspace, installPath: outsideInstall);
        var service = new ServerDeleteService(new ServerOperationManager(_ => { }), workspace.ServersRoot);

        var result = await service.DeleteAsync(new ServerDeleteRequest(
            server,
            new ServerDeleteOptions(
                DeleteServerRecord: true,
                DeleteInstalledFiles: true,
                DeleteBackups: true,
                DeleteLogs: true,
                RemoveFirewallRules: false,
                RequireStopped: true),
            UseOperationCoordinator: false));

        Assert.False(result.Success);
        Assert.Contains(result.Steps, step => step.Name == "Installed files" && !step.Success);
        Assert.False(Directory.Exists(Path.Combine(workspace.ServerFolder, "backups")));
        Assert.False(Directory.Exists(Path.Combine(workspace.ServerFolder, "logs")));
        Assert.False(File.Exists(workspace.ConfigPath));
        Assert.True(Directory.Exists(outsideInstall));
    }

    private static ServerImportRequest CreateImportRequest(
        TestWorkspace workspace,
        TestModule module,
        string? moduleId = null,
        bool readExistingConfig = false)
    {
        return new ServerImportRequest(
            module,
            moduleId ?? module.Id,
            "server1",
            "Imported Server",
            workspace.ServerFolder,
            workspace.InstallPath,
            new Dictionary<string, object?> { ["server.name"] = "Imported Server" },
            ReadExistingConfig: readExistingConfig,
            UseOperationCoordinator: false);
    }

    private static InstalledServer CreateInstalledServer(
        TestWorkspace workspace,
        string? serverFolder = null,
        string? installPath = null,
        bool canStop = false)
    {
        return new InstalledServer(
            "server1",
            "Test Server",
            "test",
            "native",
            serverFolder ?? workspace.ServerFolder,
            installPath ?? workspace.InstallPath,
            workspace.ConfigPath,
            "127.0.0.1",
            "27015",
            "",
            "public",
            "16",
            "",
            "",
            "",
            "0 / 16",
            "Offline",
            "",
            false,
            "",
            null,
            true,
            ServerRuntimeStatus.Offline,
            "Offline",
            "ServerStatusOfflineBrush",
            false,
            "",
            "",
            "",
            true,
            true,
            !canStop,
            canStop);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
            ServersRoot = Path.Combine(Root, "servers");
            ServerFolder = Path.Combine(ServersRoot, "server1");
            InstallPath = Path.Combine(Root, "existing-install");
            ConfigPath = Path.Combine(ServerFolder, "ServerConfig.json");
            Directory.CreateDirectory(ServersRoot);
            Directory.CreateDirectory(InstallPath);
        }

        public string Root { get; }
        public string ServersRoot { get; }
        public string ServerFolder { get; }
        public string InstallPath { get; }
        public string ConfigPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class TestModule : IGameServerModule
    {
        public bool ThrowOnReadConfig { get; init; }
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false, false, null);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new InstallPlan("none", "", instance.InstallPath));
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new QueryResult(ModuleServerStatus.Offline));
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, string.Empty);
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => settings.TryGetValue("server.name", out var value) ? value?.ToString() ?? "Test" : "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "16");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo("server.exe"));
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => File.Exists(Path.Combine(instance.InstallPath, "server.exe"));
        public string? GetConsoleLogPath(ServerInstance instance) => null;

        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            if (ThrowOnReadConfig)
            {
                throw new InvalidOperationException("bad config");
            }

            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["server.name"] = "Read Name"
            });
        }
    }
}
