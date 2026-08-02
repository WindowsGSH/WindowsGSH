using System.Diagnostics;
using System.Reflection;
using WindowsGSH.Core;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Diagnostics;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class ReadinessCheckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        LocalStateRecoveryStatus.Clear();
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
    public async Task RunAsync_shares_the_provided_server_loader_instead_of_creating_its_own()
    {
        // Regression guard for a P2 finding: each ReadinessCheckService used to create its own
        // InstalledServerLoader (and each ReadinessCheckWindow its own ReadinessCheckService, and
        // therefore its own loader too) - InstalledServerLoader's hang-protection dedup dictionary
        // (see InstalledServerLoaderHangProtectionTests) is scoped to the loader instance, not
        // app-wide, so a permanently hung module could accumulate one independently stuck worker per
        // window/service instance during a single run of the app. Passing a shared loader into the
        // constructor closes that gap - proven here by having two separate ReadinessCheckService
        // instances share ONE loader whose server folder never resolves, and confirming the
        // underlying per-server load delegate is invoked at most once across both, exactly the same
        // "no duplicate stuck worker" guarantee InstalledServerLoaderHangProtectionTests already
        // proves at the loader level, now proven across service instances too.
        // LoadAsync enumerates every folder under the real AppPaths.GetPath("servers") directory
        // (there is no path-injection seam - same constraint InstalledServerLoaderLoadAsyncTests
        // documents), which can include a developer's real installed servers or folders left behind
        // by other, concurrently-running tests. Counting/hanging on the folder name specifically
        // (rather than on every folder the delegate happens to be invoked for) keeps this test correct
        // regardless of what else is present in that shared, real directory - unrelated folders get an
        // immediate fake result instead of being counted or hung.
        var serverFolder = Path.Combine(
            AppPaths.GetPath("servers"),
            "readiness-shared-loader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(serverFolder);
        var expectedFolderName = Path.GetFileName(serverFolder);
        try
        {
            var invocationCount = 0;
            var hangGate = new TaskCompletionSource<InstalledServer>();
            var loader = new InstalledServerLoader(
                new NeverCalledStatusService(),
                null,
                (folder, modules, cancellationToken) =>
                {
                    var folderName = Path.GetFileName(folder);
                    if (folderName != expectedFolderName)
                    {
                        return Task.FromResult(CreateUnrelatedFakeServer(folderName));
                    }

                    Interlocked.Increment(ref invocationCount);
                    return hangGate.Task;
                },
                TimeSpan.FromMilliseconds(200));

            var serviceA = new ReadinessCheckService(loader);
            var serviceB = new ReadinessCheckService(loader);

            await serviceA.RunAsync();
            await serviceB.RunAsync();

            Assert.Equal(1, invocationCount);
        }
        finally
        {
            Directory.Delete(serverFolder, recursive: true);
        }
    }

    [Fact]
    public void ReadinessCheckService_preserves_its_original_parameterless_constructor()
    {
        // Regression guard for a P2 finding: the loader-sharing constructor added above must not
        // replace the original public parameterless constructor with an optional-parameter version -
        // optional arguments are resolved at compile time on the CALLER's side, so an already-compiled
        // consumer that referenced the old zero-argument constructor would hit a
        // MissingMethodException at runtime against a version of this assembly whose only constructor
        // takes a parameter. Matches the same "preserves N-argument constructor" reflection check this
        // file already uses for ReadinessCheckResult.
        var parameterlessConstructor = typeof(ReadinessCheckService).GetConstructor(Type.EmptyTypes);
        Assert.NotNull(parameterlessConstructor);

        var loaderConstructor = typeof(ReadinessCheckService).GetConstructor([typeof(InstalledServerLoader)]);
        Assert.NotNull(loaderConstructor);

        // Also confirm the parameterless constructor is genuinely usable (not just present via
        // reflection) - it must still create its own default InstalledServerLoader without throwing.
        var service = new ReadinessCheckService();
        Assert.NotNull(service);
    }

    [Fact]
    public void ReadinessCheckWindow_preserves_its_original_public_constructor_and_adds_an_internal_overload()
    {
        // Same guard as above, for ReadinessCheckWindow's constructor. Verified by reflection only,
        // not by actually constructing an instance - ReadinessCheckWindow is a real WPF Window
        // (InitializeComponent, etc.) that this headless test process isn't set up to instantiate,
        // the same limitation noted throughout this session for other WPF windows.
        var publicConstructor = typeof(global::WindowsGSH.ReadinessCheckWindow).GetConstructor(
            [typeof(Action<ReadinessAction>)]);
        Assert.NotNull(publicConstructor);
        Assert.True(publicConstructor!.IsPublic);

        var sharedServiceConstructor = typeof(global::WindowsGSH.ReadinessCheckWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(ReadinessCheckService), typeof(Action<ReadinessAction>)]);
        Assert.NotNull(sharedServiceConstructor);
        Assert.True(sharedServiceConstructor!.IsAssembly);
    }

    [Fact]
    public void BuildLocalStateRecoveryResults_surfaces_recovery_location()
    {
        LocalStateRecoveryStatus.Clear();
        LocalStateRecoveryStatus.Report(
            "App settings",
            "Malformed settings were preserved.",
            @"C:\recovery\settings.json",
            isFailure: true);

        var result = Assert.Single(ReadinessCheckService.BuildLocalStateRecoveryResults());

        Assert.Equal(ReadinessStatus.Warning, result.Status);
        Assert.Contains("settings.json", result.Message);
        LocalStateRecoveryStatus.Clear();
    }

    [Fact]
    public void CheckWritableFolder_creates_and_tests_expected_storage_folder()
    {
        var path = Path.Combine(_root, "storage", "nested");

        var result = ReadinessCheckService.CheckWritableFolder("Storage", path);

        Assert.Equal(ReadinessStatus.Pass, result.Status);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(path, ".windowsgsh-write-test-*"));
    }

    [Fact]
    public void CheckWritableFolder_does_not_create_required_existing_folder()
    {
        var path = Path.Combine(_root, "missing-app-folder");

        var result = ReadinessCheckService.CheckWritableFolder("Application folder", path, createIfMissing: false);

        Assert.Equal(ReadinessStatus.Fail, result.Status);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CheckDpapi_round_trip_succeeds_for_current_windows_user()
    {
        var result = ReadinessCheckService.CheckDpapi();

        Assert.Equal(ReadinessStatus.Pass, result.Status);
    }

    [Fact]
    public void Readiness_result_exposes_typed_action_metadata()
    {
        var result = ReadinessCheckResult.Fail(
            "Java",
            "Java is missing.",
            ReadinessAction.OpenJavaSettings,
            "Java settings");

        Assert.True(result.HasAction);
        Assert.Equal(ReadinessAction.OpenJavaSettings, result.Action);
        Assert.Equal("Java settings", result.ActionLabel);
    }

    [Fact]
    public void OpenFolder_quotes_paths_passed_to_explorer()
    {
        const string path = @"C:\Program Files\WindowsGSH\servers";

        var startInfo = global::WindowsGSH.ReadinessCheckWindow.CreateOpenFolderStartInfo(path);

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.Equal($"\"{path}\"", startInfo.Arguments);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void Readiness_result_preserves_three_argument_constructor()
    {
        var constructor = typeof(ReadinessCheckResult).GetConstructor(
        [
            typeof(string),
            typeof(ReadinessStatus),
            typeof(string)
        ]);

        Assert.NotNull(constructor);
        var result = Assert.IsType<ReadinessCheckResult>(
            constructor.Invoke(["Modules", ReadinessStatus.Pass, "Ready."]));
        Assert.Null(result.Action);
        Assert.Null(result.ActionLabel);
    }

    [Fact]
    public void CheckModuleStoragePaths_targets_the_first_failing_path()
    {
        var writablePath = Path.Combine(_root, "modules", "installed");
        var failingPath = Path.Combine(_root, "modules", "disabled");
        Directory.CreateDirectory(Path.GetDirectoryName(failingPath)!);
        File.WriteAllText(failingPath, "blocks directory creation");

        var result = ReadinessCheckService.CheckModuleStoragePaths([writablePath, failingPath]);

        Assert.Equal(ReadinessStatus.Fail, result.Status);
        Assert.Equal(ReadinessAction.OpenModuleStorage, result.Action);
        Assert.Equal(failingPath, result.ActionPath);
        Assert.Contains(failingPath, result.Message);
    }

    [Fact]
    public async Task CheckModuleReadinessAsync_invokes_module_readiness_capability()
    {
        var server = CreateServer("readiness-module");
        WriteConfig(server.ConfigPath, """
            {
              "settings": {
                "message": "from-config"
              }
            }
            """);
        var module = new ReadinessModule();

        var results = await ReadinessCheckService.CheckModuleReadinessAsync([server], [module]);

        Assert.True(module.WasCalled);
        Assert.Equal("from-config", module.ObservedSettings["message"]);
        Assert.Contains(results, result =>
            result.Name == "Module readiness" &&
            result.Status == ReadinessStatus.Pass &&
            result.Message.Contains(server.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckModuleReadinessAsync_skips_modules_without_readiness_capability()
    {
        var server = CreateServer("basic-module");

        var results = await ReadinessCheckService.CheckModuleReadinessAsync([server], [new BasicModule("basic-module")]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckModuleReadinessAsync_reports_config_load_failures()
    {
        var server = CreateServer("readiness-module");
        var module = new ReadinessModule();

        var results = await ReadinessCheckService.CheckModuleReadinessAsync([server], [module]);

        Assert.False(module.WasCalled);
        Assert.Contains(results, result =>
            result.Name == "Readiness Module readiness" &&
            result.Status == ReadinessStatus.Fail &&
            result.Message.Contains("module readiness check failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckModuleReadinessAsync_reports_module_readiness_failures()
    {
        var server = CreateServer("throwing-readiness-module");
        WriteConfig(server.ConfigPath, """{ "settings": {} }""");

        var results = await ReadinessCheckService.CheckModuleReadinessAsync([server], [new ThrowingReadinessModule()]);

        Assert.Contains(results, result =>
            result.Name == "Throwing Readiness Module readiness" &&
            result.Status == ReadinessStatus.Fail &&
            result.Message.Contains("boom", StringComparison.OrdinalIgnoreCase));
    }

    private void WriteConfig(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private InstalledServer CreateServer(string moduleId)
    {
        var serverFolder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var installPath = Path.Combine(serverFolder, "files");
        return new InstalledServer(
            Id: "server-1",
            Name: "Test Server",
            ModuleId: moduleId,
            Runtime: "test",
            ServerFolder: serverFolder,
            InstallPath: installPath,
            ConfigPath: Path.Combine(serverFolder, "ServerConfig.json"),
            IpAddress: "0.0.0.0",
            Port: "25565",
            SteamAppId: "",
            SteamBranch: "public",
            MaxPlayers: "20",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: "Offline",
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: "",
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Offline,
            StatusText: "Offline",
            StatusBrushKey: "BadBrush",
            HasUpdateAvailable: false,
            LocalBuildId: "",
            RemoteBuildId: "",
            IgnoredBuildId: "",
            CanShowInfo: false,
            CanEditConfig: true,
            CanStart: true,
            CanStop: false);
    }

    private class BasicModule(string id) : IGameServerModule
    {
        public string Id { get; } = id;
        public virtual string Name => "Basic Module";
        public string Version => "1.0.0";
        public virtual ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, IsInstalled: false, IsEnabled: false, StatusText: "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ReadinessModule() : BasicModule("readiness-module"), IModuleReadinessCapability
    {
        public override string Name => "Readiness Module";
        public bool WasCalled { get; private set; }
        public IReadOnlyDictionary<string, object?> ObservedSettings { get; private set; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            WasCalled = true;
            ObservedSettings = instance.Settings;
            return Task.FromResult<IReadOnlyList<ReadinessCheckResult>>(
            [
                ReadinessCheckResult.Pass("Module readiness", $"{instance.Name}: module-specific checks passed.")
            ]);
        }
    }

    private sealed class ThrowingReadinessModule() : BasicModule("throwing-readiness-module"), IModuleReadinessCapability
    {
        public override string Name => "Throwing Readiness Module";

        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    // The substituted load delegate replaces TryLoadAsync entirely for every folder the shared
    // loader scans, so IServerStatusService.GetStatusAsync (only ever called from inside the real
    // TryLoadAsync) must never be reached here.
    private sealed class NeverCalledStatusService : IServerStatusService
    {
        public Task<ServerStatusSnapshot> GetStatusAsync(
            IGameServerModule? module,
            ServerInstance instance,
            bool hasUpdateAvailable,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("GetStatusAsync should not be reached when the load delegate is substituted.");
        }

        public ServerStatusSnapshot? GetCachedStatus(string serverId) => null;
    }

    // A minimal, immediately-resolved InstalledServer for any real server folder the shared loader
    // happens to scan that isn't the one this test actually cares about - keeps this test correct
    // regardless of what else exists under the real AppPaths.GetPath("servers") directory.
    private static InstalledServer CreateUnrelatedFakeServer(string id)
    {
        return new InstalledServer(
            id,
            $"Server {id}",
            "unknown",
            "native",
            "",
            "",
            "",
            "--",
            "--",
            "",
            "public",
            "--",
            "--",
            "--",
            "--",
            "--",
            "Offline",
            "--",
            false,
            "",
            null,
            true,
            ServerRuntimeStatus.Offline,
            "Offline",
            "MutedBrush",
            false,
            "",
            "",
            "",
            true,
            true,
            true,
            false);
    }
}
