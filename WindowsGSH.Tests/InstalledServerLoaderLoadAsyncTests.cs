using WindowsGSH.Core;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class InstalledServerLoaderLoadAsyncTests : IDisposable
{
    // LoadAsync itself calls AppPaths.GetPath("servers") internally rather than accepting a
    // path - there is no way to redirect it to an isolated temp directory without changing its
    // signature, so this writes real, uniquely-named server folders directly under the real path
    // every server actually loads from. No other test currently touches this exact directory
    // (grepped for AppPaths.GetPath("servers") across the test project to confirm before adding
    // this), and every folder created here is uniquely named and removed in Dispose.
    private readonly List<string> _serverFolders = [];
    private readonly string _serversRoot = AppPaths.GetPath("servers");

    [Fact]
    public async Task LoadAsync_resolves_multiple_servers_concurrently_instead_of_one_at_a_time()
    {
        // Regression guard for a real bug: LoadAsync's per-server loop awaited TryLoadAsync
        // sequentially, one folder at a time. TryLoadAsync's own dominant cost - IServerStatusService
        // .GetStatusAsync - is a real, mostly-I/O-bound operation (a module query bounded by a
        // multi-second timeout for a running server), so loading N servers took roughly N times as
        // long as loading one. The fake status service holds each expected call behind an async gate
        // until all four have arrived. This proves overlap structurally without depending on thread
        // scheduling speed or a narrow wall-clock delay.
        const int serverCount = 4;
        var expectedIds = Enumerable.Range(100, serverCount).Select(i => i.ToString()).ToHashSet();
        var statusService = new ConcurrencyTrackingStatusService(expectedIds);

        foreach (var id in expectedIds)
        {
            CreateMinimalServerFolder(id);
        }

        var loader = new InstalledServerLoader(statusService);
        var loadTask = loader.LoadAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var firstCompleted = await Task.WhenAny(statusService.AllExpectedCallsStarted, loadTask, timeoutTask);

        // Always release and observe LoadAsync, including the assertion-failure path. Otherwise a
        // sequential regression would leave its first status call blocked in the test process.
        statusService.ReleaseExpectedCalls();
        var servers = await loadTask;

        Assert.Same(statusService.AllExpectedCallsStarted, firstCompleted);

        // Exact id match, not a folder-name substring/suffix check - this runs against the real
        // AppPaths.GetPath("servers") directory (see the class comment), which could in principle
        // contain other, unrelated server folders.
        var loadedTestServers = servers.Where(server => expectedIds.Contains(server.Id)).ToArray();
        Assert.Equal(serverCount, loadedTestServers.Length);

        // The real, load-independent regression guard: proves every status check was genuinely
        // in flight at once, which cannot happen under the pre-fix sequential loop regardless of
        // how fast or slow the machine running this test is.
        Assert.Equal(serverCount, statusService.MaxConcurrentCalls);
    }

    private void CreateMinimalServerFolder(string id)
    {
        var folder = Path.Combine(_serversRoot, "loadasync-test-" + id);
        _serverFolders.Add(folder);
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        File.WriteAllText(
            Path.Combine(folder, "ServerConfig.json"),
            $$"""
            {
              "id": "{{id}}",
              "name": "LoadAsync Test {{id}}",
              "moduleId": "loadasync-test-module",
              "runtime": "native",
              "installPath": "files"
            }
            """);
    }

    public void Dispose()
    {
        foreach (var folder in _serverFolders)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class ConcurrencyTrackingStatusService(IReadOnlySet<string> expectedIds) : IServerStatusService
    {
        private int _inFlight;
        public int MaxConcurrentCalls { get; private set; }
        private readonly object _gate = new();
        private readonly TaskCompletionSource _allExpectedCallsStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseExpectedCalls =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AllExpectedCallsStarted => _allExpectedCallsStarted.Task;

        public void ReleaseExpectedCalls() => _releaseExpectedCalls.TrySetResult();

        public async Task<ServerStatusSnapshot> GetStatusAsync(
            IGameServerModule? module,
            ServerInstance instance,
            bool hasUpdateAvailable,
            CancellationToken cancellationToken = default)
        {
            if (!expectedIds.Contains(instance.Id))
            {
                return CreateSnapshot(instance);
            }

            lock (_gate)
            {
                _inFlight++;
                MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _inFlight);
                if (_inFlight == expectedIds.Count)
                {
                    _allExpectedCallsStarted.TrySetResult();
                }
            }

            try
            {
                await _releaseExpectedCalls.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }

            return CreateSnapshot(instance);
        }

        private static ServerStatusSnapshot CreateSnapshot(ServerInstance instance) => new(
                instance.Id,
                instance.Name,
                instance.ModuleId,
                ServerRuntimeStatus.Offline,
                IsProcessRunning: false,
                Queried: false,
                QueryStatus: null,
                OnlinePlayers: null,
                MaxPlayers: null,
                Version: null,
                Message: null,
                Map: null,
                Game: null,
                QueryDurationMilliseconds: null,
                Players: null,
                DetailMessage: null,
                Protocol: null,
                CurrentStatusText: "Offline",
                StatusText: "Offline",
                StatusBrushKey: "MutedBrush",
                CheckedAt: DateTimeOffset.UtcNow);

        public ServerStatusSnapshot? GetCachedStatus(string serverId) => null;
    }
}
