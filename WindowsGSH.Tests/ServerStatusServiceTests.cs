using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerStatusServiceTests
{
    [Fact]
    public async Task GetStatusAsync_UsesModuleQuery_WhenRunningAndSupported()
    {
        var now = new DateTimeOffset(2026, 5, 24, 14, 0, 0, TimeSpan.Zero);
        var service = new ServerStatusService(
            isRunning: (_, _) => true,
            utcNow: () => now);
        var module = new TestModule(new QueryResult(ModuleServerStatus.Online, 3, 10, "1.2"));

        var snapshot = await service.GetStatusAsync(module, CreateInstance(), hasUpdateAvailable: false);

        Assert.Equal(ServerRuntimeStatus.Running, snapshot.Status);
        Assert.True(snapshot.Queried);
        Assert.Equal(ModuleServerStatus.Online, snapshot.QueryStatus);
        Assert.Equal(3, snapshot.OnlinePlayers);
        Assert.Equal(10, snapshot.MaxPlayers);
        Assert.Equal(now, snapshot.CheckedAt);
        Assert.Equal(now, snapshot.LastSuccessfulQueryAt);
    }

    [Fact]
    public async Task GetStatusAsync_Preserves_rich_query_data()
    {
        var players = new[] { new QueryPlayer("Alice", Score: 8, PingMilliseconds: 35) };
        var result = new QueryResult(
            ModuleServerStatus.Online,
            1,
            20,
            "1.2",
            "Query succeeded.",
            "TheIsland",
            "Ark",
            22.5,
            players,
            "Fields: map, players",
            "EOS");
        var service = new ServerStatusService(isRunning: (_, _) => true);

        var snapshot = await service.GetStatusAsync(new TestModule(result), CreateInstance(), hasUpdateAvailable: false);
        var roundTrip = snapshot.ToQueryResult();

        Assert.Equal("TheIsland", snapshot.Map);
        Assert.Equal("Ark", snapshot.Game);
        Assert.Equal(22.5, snapshot.QueryDurationMilliseconds);
        Assert.Equal("Alice", Assert.Single(snapshot.Players!).Name);
        Assert.Equal(result, roundTrip);
    }

    [Fact]
    public async Task GetStatusAsync_FallsBackToProcessWarning_WhenQueryFails()
    {
        var service = new ServerStatusService(isRunning: (_, _) => true);
        var module = new TestModule(queryException: new InvalidOperationException("query broke"));

        var snapshot = await service.GetStatusAsync(module, CreateInstance(), hasUpdateAvailable: false);

        Assert.Equal(ServerRuntimeStatus.Warning, snapshot.Status);
        Assert.True(snapshot.IsProcessRunning);
        Assert.Equal(ModuleServerStatus.Offline, snapshot.QueryStatus);
        Assert.Contains("query broke", snapshot.Message);
        Assert.True(snapshot.ShouldLogMessage);
        Assert.Contains("query broke", snapshot.LogMessage);
    }

    [Fact]
    public async Task GetStatusAsync_UsesProcessOnlyStatus_WhenQueryUnsupported()
    {
        var service = new ServerStatusService(isRunning: (_, _) => true);
        var module = new TestModule(supportsQuery: false);

        var snapshot = await service.GetStatusAsync(module, CreateInstance(), hasUpdateAvailable: false);

        Assert.Equal(ServerRuntimeStatus.Running, snapshot.Status);
        Assert.True(snapshot.IsProcessRunning);
        Assert.False(snapshot.Queried);
        Assert.Null(snapshot.QueryStatus);
        Assert.False(snapshot.ShouldLogMessage);
        Assert.Null(snapshot.LogMessage);
    }

    [Fact]
    public async Task GetStatusAsync_CachesLatestSnapshot()
    {
        var now = new DateTimeOffset(2026, 5, 24, 15, 0, 0, TimeSpan.Zero);
        var service = new ServerStatusService(
            isRunning: (_, _) => false,
            utcNow: () => now);
        var instance = CreateInstance();

        var snapshot = await service.GetStatusAsync(new TestModule(), instance, hasUpdateAvailable: false);
        var cached = service.GetCachedStatus(instance.Id);

        Assert.Equal(snapshot, cached);
        Assert.Equal(now, cached?.CheckedAt);
    }

    [Fact]
    public async Task GetStatusAsync_ThrottlesRepeatedIdenticalQueryFailures()
    {
        var now = new DateTimeOffset(2026, 5, 24, 16, 0, 0, TimeSpan.Zero);
        var service = new ServerStatusService(
            isRunning: (_, _) => true,
            utcNow: () => now,
            failureLogThrottle: TimeSpan.FromMinutes(5));
        var module = new TestModule(new QueryResult(ModuleServerStatus.Offline, Message: "same failure"));
        var instance = CreateInstance();

        var first = await service.GetStatusAsync(module, instance, hasUpdateAvailable: false);
        var second = await service.GetStatusAsync(module, instance, hasUpdateAvailable: false);

        Assert.True(first.ShouldLogMessage);
        Assert.False(second.ShouldLogMessage);
    }

    private static ServerInstance CreateInstance()
    {
        return new ServerInstance(
            "1",
            "Test Server",
            "test",
            "C:\\servers\\1",
            "C:\\servers\\1\\files",
            "C:\\servers\\1\\ServerConfig.json",
            new Dictionary<string, object?>());
    }

    private sealed class TestModule : IGameServerModule
    {
        private readonly QueryResult _queryResult;
        private readonly Exception? _queryException;

        public TestModule(QueryResult? queryResult = null, bool supportsQuery = true, Exception? queryException = null)
        {
            _queryResult = queryResult ?? new QueryResult(ModuleServerStatus.Online);
            _queryException = queryException;
            Capabilities = new ModuleCapabilities(false, false, supportsQuery, false, false, false, false, false);
        }

        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities { get; }
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"], QueryProtocol: "A2S");
        public SteamInstallDefinition? SteamInstall => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            if (_queryException != null)
            {
                throw _queryException;
            }

            return Task.FromResult(_queryResult);
        }

        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "10");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo("server.exe"));
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}

