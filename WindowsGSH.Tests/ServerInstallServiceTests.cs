using System.Diagnostics;
using System.Text.Json;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(SteamBranchPasswordStoreTestCollection.Name)]
public sealed class ServerInstallServiceTests
{
    [Fact]
    public async Task InstallAsync_ReturnsFailure_ForUnsupportedModule()
    {
        var service = new ServerInstallService(new FakeSteamCmdClient());
        var instance = CreateInstance();

        var result = await service.InstallAsync(new ServerInstallRequest(new UnsupportedModule(), instance));

        Assert.False(result.Success);
        Assert.Contains("does not define", result.Message);
    }

    [Fact]
    public async Task InstallAsync_UsesModuleUpdater_AndWritesMetadata()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var service = new ServerInstallService(new FakeSteamCmdClient(), utcNow: () => now);
        var instance = CreateInstance();
        var module = new UpdateModule(new ModuleUpdateResult(true, "Downloaded Paper.", "123", "paper.jar"));

        var result = await service.InstallAsync(new ServerInstallRequest(module, instance));

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(File.ReadAllText(instance.ConfigPath));
        var update = document.RootElement.GetProperty("update");
        Assert.Equal("123", update.GetProperty("lastInstalledBuildId").GetString());
        Assert.Equal("paper.jar", update.GetProperty("lastDownloadedFileName").GetString());
        Assert.Equal(now.ToString("O"), update.GetProperty("lastUpdateUtc").GetString());
    }

    [Fact]
    public async Task InstallAsync_PersistsSelectedUpnpPolicy()
    {
        var service = new ServerInstallService(new FakeSteamCmdClient());
        var instance = CreateInstance();
        var module = new UpdateModule(new ModuleUpdateResult(true, "Downloaded.", "123"));

        var result = await service.InstallAsync(new ServerInstallRequest(
            module,
            instance,
            UpnpMappingPolicy: UpnpMappingPolicy.MapOnStartRemoveOnStop));

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(File.ReadAllText(instance.ConfigPath));
        Assert.Equal(
            nameof(UpnpMappingPolicy.MapOnStartRemoveOnStop),
            document.RootElement.GetProperty("network").GetProperty("upnpMappingPolicy").GetString());
    }

    [Fact]
    public async Task InstallAsync_ForwardsProgressToModuleUpdater()
    {
        var service = new ServerInstallService(new FakeSteamCmdClient());
        var instance = CreateInstance();
        var module = new ProgressUpdateModule();
        var messages = new List<string>();
        var progress = new Progress<string>(messages.Add);

        var result = await service.InstallAsync(new ServerInstallRequest(module, instance, Progress: progress));

        Assert.True(result.Success);
        Assert.Same(progress, module.ObservedProgress);
    }

    [Fact]
    public async Task UpdateAsync_UsesSteamPlanValues_AndWritesMetadata()
    {
        var now = new DateTimeOffset(2026, 5, 24, 13, 0, 0, TimeSpan.Zero);
        var steam = new FakeSteamCmdClient { RemoteBuildId = "9001", ExitCode = 0 };
        var service = new ServerInstallService(steam, utcNow: () => now);
        var instance = CreateInstance();
        try
        {
            ServerInstallService.WriteServerConfig(new SteamModule(), instance, "beta", "secret");

            var result = await service.UpdateAsync(new ServerUpdateRequest(
                new SteamModule(),
                instance,
                "from test",
                "beta",
                "secret"));

            Assert.True(result.Success);
            Assert.Equal(instance.InstallPath, steam.InstallPath);
            Assert.Equal("beta", steam.Branch);
            Assert.Equal("secret", steam.BranchPassword);
            using var document = JsonDocument.Parse(File.ReadAllText(instance.ConfigPath));
            var steamJson = document.RootElement.GetProperty("steam");
            Assert.Equal("beta", steamJson.GetProperty("lastInstalledBranch").GetString());
            Assert.Equal(0, steamJson.GetProperty("lastUpdateExitCode").GetInt32());
            Assert.Equal("9001", steamJson.GetProperty("lastRemoteBuildId").GetString());
            Assert.Equal(now.ToString("O"), steamJson.GetProperty("lastUpdateUtc").GetString());
        }
        finally
        {
            new SteamBranchPasswordStore().Clear(instance.Id);
        }
    }

    [Fact]
    public async Task InstallAsync_CreatesSteamInstallPlan_BeforeRunningSteam()
    {
        var steam = new FakeSteamCmdClient();
        var service = new ServerInstallService(steam);
        var instance = CreateInstance();

        var result = await service.InstallAsync(new ServerInstallRequest(new SteamModule(), instance, "beta"));

        Assert.True(result.Success);
        Assert.NotNull(result.InstallPlan);
        Assert.Equal("steamcmd-test", result.InstallPlan.Tool);
        Assert.Equal(instance.InstallPath, result.InstallPlan.WorkingDirectory);
        Assert.Equal("beta", steam.Branch);
    }

    [Fact]
    public async Task InstallAsync_ReturnsDownloaderCompatibilityErrorWithoutThrowing()
    {
        var steam = new FakeSteamCmdClient
        {
            InstallException = new NotSupportedException(
                "This authenticated Steam module uses SteamCMD-specific install arguments that the persistent downloader cannot safely translate.")
        };
        var service = new ServerInstallService(steam);
        var instance = CreateInstance();

        var result = await service.InstallAsync(new ServerInstallRequest(new SteamModule(), instance));

        Assert.False(result.Success);
        Assert.Contains("cannot safely translate", result.Message);
        Assert.Contains("cannot safely translate", result.LastError);
    }

    [Fact]
    public async Task UpdateAsync_SkipsIgnoredSteamBuild()
    {
        var steam = new FakeSteamCmdClient { RemoteBuildId = "ignored-build" };
        var service = new ServerInstallService(steam);
        var instance = CreateInstance();
        ServerInstallService.WriteServerConfig(new SteamModule(), instance, string.Empty, string.Empty);

        var result = await service.UpdateAsync(new ServerUpdateRequest(
            new SteamModule(),
            instance,
            "on schedule",
            SkipIgnoredBuild: true,
            IgnoredBuildId: "ignored-build"));

        Assert.True(result.Success);
        Assert.Equal(0, steam.InstallCalls);
        Assert.Contains("ignored", result.Message);
    }

    [Fact]
    public async Task InstallAsync_BlocksConflictingOperations_WhenCoordinatorEnabled()
    {
        var operations = new ServerOperationManager(_ => { });
        var instance = CreateInstance();
        Assert.True(operations.TryBeginServerOperation(instance.Id, instance.Name, ServerOperationKind.Start, out var scope, out var existing));
        Assert.Null(existing);
        await using var activeOperation = new AsyncScope(scope!);
        var service = new ServerInstallService(new FakeSteamCmdClient(), operations);

        var result = await service.InstallAsync(new ServerInstallRequest(new SteamModule(), instance));

        Assert.False(result.Success);
        Assert.Equal(ServerOperationStatus.Running, result.Status);
    }

    private static ServerInstance CreateInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var serverId = Guid.NewGuid().ToString("N");
        var serverFolder = Path.Combine(root, serverId);
        var installPath = Path.Combine(serverFolder, "files");
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        return new ServerInstance(serverId, "Test Server", "test", serverFolder, installPath, configPath, new Dictionary<string, object?>
        {
            ["serverName"] = "Test Server"
        });
    }

    private sealed class AsyncScope : IAsyncDisposable
    {
        private readonly IDisposable _scope;

        public AsyncScope(IDisposable scope)
        {
            _scope = scope;
        }

        public ValueTask DisposeAsync()
        {
            _scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSteamCmdClient : ISteamCmdClient
    {
        public string? InstallPath { get; private set; }
        public string? Branch { get; private set; }
        public string? BranchPassword { get; private set; }
        public string? RemoteBuildId { get; init; }
        public int ExitCode { get; init; }
        public Exception? InstallException { get; init; }
        public int InstallCalls { get; private set; }

        public Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> InstallOrUpdateAsync(
            string installPath,
            SteamInstallDefinition definition,
            string branch,
            string branchPassword,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            if (InstallException != null)
            {
                throw InstallException;
            }

            InstallPath = installPath;
            Branch = branch;
            BranchPassword = branchPassword;
            return Task.FromResult(ExitCode);
        }

        public Task<int> VerifyAsync(
            string installPath,
            SteamInstallDefinition definition,
            string branch,
            string branchPassword,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ExitCode);
        }

        public Task<string?> GetRemoteBuildIdAsync(
            SteamInstallDefinition definition,
            string branch,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RemoteBuildId);
        }
    }

    private class UnsupportedModule : IGameServerModule
    {
        public virtual string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public virtual ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false, false, null);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public virtual SteamInstallDefinition? SteamInstall => null;
        public virtual Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new InstallPlan("tool", "", instance.InstallPath));
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new QueryResult(ModuleServerStatus.Offline));
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, string.Empty);
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => settings.TryGetValue("serverName", out var name) ? name?.ToString() ?? string.Empty : string.Empty;
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo("server.exe"));
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class SteamModule : UnsupportedModule
    {
        public override ModuleCapabilities Capabilities => new(true, true, false, false, false, false, false, false, false, null);
        public override SteamInstallDefinition? SteamInstall => new("12345", LoginAnonymous: true, ValidateByDefault: true, CustomArguments: "+custom");

        public override Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new InstallPlan("steamcmd-test", $"+app_update {SteamInstall!.AppId}", instance.InstallPath));
        }
    }

    private sealed class UpdateModule : UnsupportedModule, IModuleUpdateCapability
    {
        private readonly ModuleUpdateResult _result;

        public UpdateModule(ModuleUpdateResult result)
        {
            _result = result;
        }

        public Task<ModuleUpdateResult> UpdateAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class ProgressUpdateModule : UnsupportedModule, IModuleUpdateCapability
    {
        public IProgress<string>? ObservedProgress { get; private set; }

        public Task<ModuleUpdateResult> UpdateAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ModuleUpdateResult(true, "Updated."));

        public Task<ModuleUpdateResult> UpdateAsync(
            ServerInstance instance,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            ObservedProgress = progress;
            progress?.Report("Module progress.");
            return Task.FromResult(new ModuleUpdateResult(true, "Updated."));
        }
    }
}
