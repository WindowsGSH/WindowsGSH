using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerVerifyServiceTests
{
    [Fact]
    public async Task VerifyAsync_ReturnsUnsupported_WithNoHistoryEntry_ForNonSteamNonVerifyModule()
    {
        var operations = new ServerOperationManager(_ => { });
        var service = new ServerVerifyService(new FakeSteamCmdClient(), operations);
        var instance = CreateInstance();

        var result = await service.VerifyAsync(new ServerVerifyRequest(new UnsupportedModule(), instance, ServerIsRunning: false));

        Assert.Equal(ModuleVerifyOutcome.Unsupported, result.Outcome);
        Assert.Contains("does not support", result.Message);
        Assert.Empty(operations.GetHistory());
    }

    [Fact]
    public async Task VerifyAsync_RunsSteamValidate_ForSteamModule()
    {
        var steam = new FakeSteamCmdClient { ExitCode = 0 };
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();

        var result = await service.VerifyAsync(new ServerVerifyRequest(new SteamModule(), instance, ServerIsRunning: false));

        Assert.Equal(ModuleVerifyOutcome.Verified, result.Outcome);
        Assert.True(result.Success);
        Assert.Equal(0, result.SteamExitCode);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsFailure_WhenSteamExitCodeNonZero()
    {
        var steam = new FakeSteamCmdClient { ExitCode = 8 };
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();

        var result = await service.VerifyAsync(new ServerVerifyRequest(new SteamModule(), instance, ServerIsRunning: false));

        Assert.Equal(ModuleVerifyOutcome.Failed, result.Outcome);
        Assert.False(result.Success);
        Assert.Equal(8, result.SteamExitCode);
    }

    [Fact]
    public async Task VerifyAsync_RejectsRunningServer_WhenModuleDoesNotAllowOnline()
    {
        var service = new ServerVerifyService(new FakeSteamCmdClient());
        var instance = CreateInstance();

        var result = await service.VerifyAsync(new ServerVerifyRequest(new SteamModule(), instance, ServerIsRunning: true));

        Assert.Equal(ModuleVerifyOutcome.Failed, result.Outcome);
        Assert.Contains("must be stopped", result.Message);
    }

    [Fact]
    public async Task VerifyAsync_AllowsRunningServer_WhenModuleAllowsOnlineVerification()
    {
        var steam = new FakeSteamCmdClient { ExitCode = 0 };
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();

        var result = await service.VerifyAsync(new ServerVerifyRequest(new OnlineVerifyModule(), instance, ServerIsRunning: true));

        Assert.Equal(ModuleVerifyOutcome.Verified, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_DelegatesToModule_WhenModuleImplementsVerifyCapability()
    {
        var service = new ServerVerifyService(new FakeSteamCmdClient());
        var instance = CreateInstance();
        var module = new VerifyCapableModule(new ModuleVerifyResult(ModuleVerifyOutcome.Repaired, "3 files replaced."));

        var result = await service.VerifyAsync(new ServerVerifyRequest(module, instance, ServerIsRunning: false));

        Assert.Equal(ModuleVerifyOutcome.Repaired, result.Outcome);
        Assert.Equal("3 files replaced.", result.Message);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task VerifyAsync_BlocksConflictingOperations_WhenCoordinatorEnabled()
    {
        var operations = new ServerOperationManager(_ => { });
        var instance = CreateInstance();
        Assert.True(operations.TryBeginServerOperation(instance.Id, instance.Name, ServerOperationKind.Start, out var scope, out _));
        await using var activeOperation = new AsyncScope(scope!);
        var service = new ServerVerifyService(new FakeSteamCmdClient(), operations);

        var result = await service.VerifyAsync(new ServerVerifyRequest(new SteamModule(), instance, ServerIsRunning: false));

        Assert.Equal(ModuleVerifyOutcome.Failed, result.Outcome);
        Assert.Contains("busy", result.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task VerifyAsync_HandlesCancellation_Gracefully()
    {
        var cts = new CancellationTokenSource();
        var steam = new FakeSteamCmdClient(cancelOnCall: cts);
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();

        var result = await service.VerifyAsync(
            new ServerVerifyRequest(new SteamModule(), instance, ServerIsRunning: false, UseOperationCoordinator: false),
            cts.Token);

        Assert.Equal(ModuleVerifyOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancel", result.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task VerifyAsync_PassesBranchAndPassword_ToSteamClient()
    {
        var steam = new FakeSteamCmdClient { ExitCode = 0 };
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();

        await service.VerifyAsync(new ServerVerifyRequest(
            new SteamModule(),
            instance,
            ServerIsRunning: false,
            SteamBranch: "beta",
            SteamBranchPassword: "s3cr3t"));

        Assert.Equal("beta", steam.Branch);
        Assert.Equal("s3cr3t", steam.BranchPassword);
    }

    [Fact]
    public async Task VerifyAsync_DoesNotExposeBranchPassword_InProgressMessages()
    {
        var messages = new List<string>();
        var steam = new FakeSteamCmdClient { ExitCode = 0 };
        var service = new ServerVerifyService(steam);
        var instance = CreateInstance();
        var progress = new ActionProgress(messages.Add);

        await service.VerifyAsync(new ServerVerifyRequest(
            new SteamModule(),
            instance,
            ServerIsRunning: false,
            SteamBranchPassword: "s3cr3t",
            Progress: progress));

        Assert.DoesNotContain(messages, m => m.Contains("s3cr3t"));
    }

    private static ServerInstance CreateInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var serverId = Guid.NewGuid().ToString("N");
        var serverFolder = Path.Combine(root, serverId);
        var installPath = Path.Combine(serverFolder, "files");
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        return new ServerInstance(serverId, "Test Server", "test", serverFolder, installPath, configPath,
            new Dictionary<string, object?> { ["serverName"] = "Test Server" });
    }

    private sealed class ActionProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private sealed class AsyncScope(IDisposable scope) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSteamCmdClient : ISteamCmdClient
    {
        private readonly CancellationTokenSource? _cancelOnCall;

        public FakeSteamCmdClient(CancellationTokenSource? cancelOnCall = null)
        {
            _cancelOnCall = cancelOnCall;
        }

        public int ExitCode { get; init; }
        public string? Branch { get; private set; }
        public string? BranchPassword { get; private set; }

        public Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> InstallOrUpdateAsync(
            string installPath,
            SteamInstallDefinition definition,
            string branch,
            string branchPassword,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ExitCode);

        public Task<int> VerifyAsync(
            string installPath,
            SteamInstallDefinition definition,
            string branch,
            string branchPassword,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Branch = branch;
            BranchPassword = branchPassword;
            _cancelOnCall?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExitCode);
        }

        public Task<string?> GetRemoteBuildIdAsync(
            SteamInstallDefinition definition,
            string branch,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
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
        public Task<IReadOnlyList<System.Diagnostics.Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<System.Diagnostics.Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => settings.TryGetValue("serverName", out var name) ? name?.ToString() ?? string.Empty : string.Empty;
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "");
        public Task<System.Diagnostics.ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new System.Diagnostics.ProcessStartInfo("server.exe"));
        public Task<System.Diagnostics.Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<System.Diagnostics.Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class SteamModule : UnsupportedModule
    {
        public override ModuleCapabilities Capabilities => new(true, true, false, false, false, false, false, false, false, null);
        public override SteamInstallDefinition? SteamInstall => new("12345", LoginAnonymous: true, ValidateByDefault: false, CustomArguments: "");
    }

    private sealed class OnlineVerifyModule : UnsupportedModule, IModuleVerifyCapability
    {
        public bool AllowsOnlineVerification => true;
        public override SteamInstallDefinition? SteamInstall => new("12345", LoginAnonymous: true, ValidateByDefault: false, CustomArguments: "");

        public Task<ModuleVerifyResult> VerifyAsync(ServerInstance instance, CancellationToken cancellationToken)
            => Task.FromResult(new ModuleVerifyResult(ModuleVerifyOutcome.Verified, "OK"));
    }

    private sealed class VerifyCapableModule : UnsupportedModule, IModuleVerifyCapability
    {
        private readonly ModuleVerifyResult _result;

        public VerifyCapableModule(ModuleVerifyResult result) => _result = result;

        public bool AllowsOnlineVerification => false;

        public Task<ModuleVerifyResult> VerifyAsync(ServerInstance instance, CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }
}
