using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerMetricsServiceTests
{
    [Fact]
    public void CalculateCpuPercent_UsesProcessorCountAndElapsedTime()
    {
        var previousTime = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var currentTime = previousTime.AddSeconds(10);

        var percent = ServerMetricsService.CalculateCpuPercent(
            TimeSpan.FromSeconds(1),
            previousTime,
            TimeSpan.FromSeconds(3),
            currentTime,
            processorCount: 4);

        Assert.Equal(5, percent);
    }

    [Fact]
    public void Sample_ReturnsNoProcessSnapshot_WhenNoProcessSamplesExist()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var service = new ServerMetricsService(
            utcNow: () => now,
            processSampler: (_, _) => []);
        var instance = CreateInstance();

        var snapshot = service.Sample(new TestModule(), instance, maxPlayers: 20);

        Assert.Equal(instance.Id, snapshot.ServerId);
        Assert.Null(snapshot.PrimaryProcessId);
        Assert.Null(snapshot.CpuPercent);
        Assert.Null(snapshot.MemoryBytes);
        Assert.Equal("-- / 20", snapshot.PlayersText);
        Assert.Same(snapshot, service.GetLatest(instance.Id));
    }

    [Fact]
    public void Sample_AggregatesMultipleProcesses_AndChoosesLargestAsPrimary()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var later = now.AddSeconds(10);
        var sampleTime = now;
        var service = new ServerMetricsService(
            utcNow: () => sampleTime,
            processSampler: (_, _) =>
            [
                new ProcessMetricsSample(10, "small", TimeSpan.FromSeconds(sampleTime == now ? 1 : 2), 100, now.AddMinutes(-10), 2),
                new ProcessMetricsSample(20, "large", TimeSpan.FromSeconds(sampleTime == now ? 3 : 6), 900, now.AddMinutes(-5), 7)
            ],
            processorCount: 2);
        var instance = CreateInstance();

        var first = service.Sample(new TestModule(), instance);
        sampleTime = later;
        var second = service.Sample(new TestModule(), instance);

        Assert.Equal(20, first.PrimaryProcessId);
        Assert.Equal("large", first.PrimaryProcessName);
        Assert.Equal(1000, first.MemoryBytes);
        Assert.Equal(9, first.ThreadCount);
        Assert.Equal(0, first.CpuPercent);
        Assert.Equal(20, second.CpuPercent);
        Assert.Equal(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)), second.Uptime);
    }

    [Fact]
    public void History_IsBounded_ByMaxSamplesPerServer()
    {
        var service = new ServerMetricsService(
            processSampler: (_, _) => [],
            maxSamplesPerServer: 3);
        var instance = CreateInstance();

        for (var i = 0; i < 5; i++)
        {
            service.Sample(null, instance);
        }

        Assert.Equal(3, service.GetHistory(instance.Id).Count);
    }

    [Fact]
    public void History_DroppsOldestSamples_WhenCapacityExceeded()
    {
        var base_ = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var times = Enumerable.Range(0, 5).Select(i => base_.AddSeconds(i * 10)).ToArray();
        var timeIndex = 0;
        var service = new ServerMetricsService(
            utcNow: () => times[timeIndex],
            processSampler: (_, _) => [],
            maxSamplesPerServer: 3);
        var instance = CreateInstance();

        for (var i = 0; i < times.Length; i++)
        {
            timeIndex = i;
            service.Sample(null, instance);
        }

        var history = service.GetHistory(instance.Id);
        Assert.Equal(3, history.Count);
        Assert.Equal(times[2], history[0].SampledAt);
        Assert.Equal(times[4], history[2].SampledAt);
    }

    [Fact]
    public void RemoveSeries_ClearsHistoryForServer()
    {
        var service = new ServerMetricsService(processSampler: (_, _) => []);
        var instance = CreateInstance();

        service.Sample(null, instance);
        Assert.NotNull(service.GetLatest(instance.Id));

        service.RemoveSeries(instance.Id);

        Assert.Null(service.GetLatest(instance.Id));
        Assert.Empty(service.GetHistory(instance.Id));
    }

    [Fact]
    public void PruneStale_RemovesSeries_WithNoRecentSamples()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var sampleTime = now;
        var service = new ServerMetricsService(
            utcNow: () => sampleTime,
            processSampler: (_, _) => []);
        var instance1 = CreateInstance("server1");
        var instance2 = CreateInstance("server2");

        service.Sample(null, instance1);
        sampleTime = now.AddMinutes(10);
        service.Sample(null, instance2);

        service.PruneStale(now.AddMinutes(5));

        Assert.Null(service.GetLatest(instance1.Id));
        Assert.NotNull(service.GetLatest(instance2.Id));
    }

    [Fact]
    public void MaxServers_EvictsLeastRecentSeries_WhenLimitReached()
    {
        var now = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var sampleTime = now;
        var service = new ServerMetricsService(
            utcNow: () => sampleTime,
            processSampler: (_, _) => [],
            maxServers: 2);

        service.Sample(null, CreateInstance("s1"));
        sampleTime = now.AddSeconds(1);
        service.Sample(null, CreateInstance("s2"));
        sampleTime = now.AddSeconds(2);
        service.Sample(null, CreateInstance("s3")); // evicts s1 (oldest)

        Assert.Null(service.GetLatest("s1"));
        Assert.NotNull(service.GetLatest("s2"));
        Assert.NotNull(service.GetLatest("s3"));
    }

    [Fact]
    public void GetHistory_ReturnsEmpty_ForUnknownServer()
    {
        var service = new ServerMetricsService(processSampler: (_, _) => []);
        Assert.Empty(service.GetHistory("unknown-server"));
        Assert.Null(service.GetLatest("unknown-server"));
    }

    [Fact]
    public void History_HandlesNullCpuAndMemory_WithoutThrowing()
    {
        var service = new ServerMetricsService(processSampler: (_, _) => []);
        var instance = CreateInstance();

        var snapshot = service.Sample(null, instance);

        Assert.Null(snapshot.CpuPercent);
        Assert.Null(snapshot.MemoryBytes);
        Assert.Equal("--", snapshot.CpuText);
        Assert.Equal("--", snapshot.MemoryText);
    }

    private static ServerInstance CreateInstance(string id = "server1")
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        return new ServerInstance(
            id,
            $"Test Server {id}",
            "test",
            root,
            Path.Combine(root, "files"),
            Path.Combine(root, "ServerConfig.json"),
            new Dictionary<string, object?>());
    }

    private sealed class TestModule : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false, false, null);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new InstallPlan("none", "", instance.InstallPath));
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
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "20");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo("server.exe"));
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}
