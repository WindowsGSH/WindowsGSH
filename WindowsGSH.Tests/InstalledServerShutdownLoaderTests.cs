using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class InstalledServerShutdownLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH-shutdown-loader-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shutdown_loader_reads_direct_config_without_querying_module()
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(
            Path.Combine(_root, "ServerConfig.json"),
            """
            {
              "id": "7",
              "name": "Shutdown Test",
              "moduleId": "test",
              "runtime": "native",
              "installPath": "files"
            }
            """);
        var module = new NoQueryModule();

        var server = InstalledServerLoader.TryLoadForShutdown(_root, [module]);

        Assert.NotNull(server);
        Assert.Equal("7", server.Id);
        Assert.Equal("Shutdown Test", server.Name);
        Assert.Equal(Path.Combine(_root, "files"), server.InstallPath);
        Assert.False(module.QueryCalled);
    }

    [Fact]
    public void Shutdown_loader_never_calls_the_two_hang_prone_display_or_install_check_methods()
    {
        // Regression guard for a P1 finding: MainWindow.LoadRunningServersForExitAsync (used by
        // user-close and tray Stop All) switched from the regular async LoadAsync to this
        // synchronous shutdown loader specifically because LoadAsync's own hang-protection timeout
        // can return a CanStop=false problem card for a server whose module merely hung answering
        // GetDisplayInfo/IsInstallValid - even though the real game server process is genuinely
        // still running. ShutdownPlanner.SelectStopCandidates would then never treat that server as
        // a stop candidate, and the exit/tray-stop-all flows could conclude zero servers are running
        // and skip their own safety prompts and stop logic entirely, exiting while the real game
        // process keeps running unmanaged. This proves TryLoadForShutdown never reaches either of
        // those two specific methods at all - "is this server running" comes only from
        // ServerProcessLocator.IsRunning, a direct OS process-list check - so a module hang in
        // either one cannot affect this path's result the way it can affect the regular LoadAsync
        // path.
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(
            Path.Combine(_root, "ServerConfig.json"),
            """
            {
              "id": "8",
              "name": "Shutdown Safety Test",
              "moduleId": "test",
              "runtime": "native",
              "installPath": "files"
            }
            """);
        var module = new ThrowsOnDisplayOrInstallCheckModule();

        var server = InstalledServerLoader.TryLoadForShutdown(_root, [module]);

        Assert.NotNull(server);
        Assert.Equal("8", server.Id);
    }

    [Fact]
    public void Shutdown_loader_ignores_missing_or_malformed_config()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(InstalledServerLoader.TryLoadForShutdown(_root, []));

        File.WriteAllText(Path.Combine(_root, "ServerConfig.json"), "{broken");
        Assert.Null(InstalledServerLoader.TryLoadForShutdown(_root, []));
    }

    [Fact]
    public void Initial_card_loader_does_not_require_or_invoke_a_module()
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        File.WriteAllText(
            Path.Combine(_root, "ServerConfig.json"),
            """
            {
              "id": "7",
              "name": "Initial Test",
              "moduleId": "test",
              "runtime": "native",
              "installPath": "files"
            }
            """);

        var server = InstalledServerLoader.TryLoadInitialCard(_root);

        Assert.Equal("7", server.Id);
        Assert.Equal("Initial Test", server.Name);
        Assert.Equal("Loading status…", server.StatusText);
        Assert.True(server.IsInstalled);
        Assert.False(server.CanStart);
        Assert.False(server.CanStop);
    }

    [Fact]
    public void Initial_card_loader_returns_a_problem_card_for_malformed_config()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ServerConfig.json"), "{broken");

        var server = InstalledServerLoader.TryLoadInitialCard(_root);

        Assert.Equal("Needs attention", server.CurrentStatusText);
        Assert.NotNull(server.LastOperationError);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoQueryModule : IGameServerModule
    {
        public bool QueryCalled { get; private set; }
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, true, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", []);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("--", "--", "--");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            QueryCalled = true;
            throw new InvalidOperationException("Shutdown loading must not query.");
        }
    }

    private sealed class ThrowsOnDisplayOrInstallCheckModule : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, true, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", []);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) =>
            throw new InvalidOperationException("Shutdown loading must not call GetDisplayInfo - it can hang.");

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool IsInstallValid(ServerInstance instance) =>
            throw new InvalidOperationException("Shutdown loading must not call IsInstallValid - it can hang.");

        public string? GetConsoleLogPath(ServerInstance instance) => null;

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Shutdown loading must not query.");
    }
}
