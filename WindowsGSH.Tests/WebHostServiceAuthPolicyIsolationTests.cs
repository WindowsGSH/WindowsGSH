using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Api;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;
using static WindowsGSH.Tests.WebTestExtensions;

namespace WindowsGSH.Tests;

// Regression test for the exact scenario a review caught: AllowLegacyWebSocketQueryStringAuth was
// originally a WebHostService static, so a second host starting with a different value would
// silently flip the policy for every other host already running in the process (real risk in
// this test suite specifically, since WebHostServiceTests starts default-options hosts outside
// WebTestCollection while auth-policy tests run inside it). Proves two hosts running at the same
// time, started with opposite policies, each keep their own.
[Collection(WebTestCollection.Name)]
public sealed class WebHostServiceAuthPolicyIsolationTests : IAsyncLifetime
{
    private static int _nextPort = 15700;
    private readonly int _allowPort = Interlocked.Increment(ref _nextPort);
    private readonly int _denyPort = Interlocked.Increment(ref _nextPort);
    private readonly string _dbPath;
    private readonly byte[] _key;
    private readonly WebUserRepository _repo;
    private WebHostService? _allowHost;
    private WebHostService? _denyHost;
    private readonly HttpClient _http = new();

    private const string ServerId = "console-isolation-test-srv";

    public WebHostServiceAuthPolicyIsolationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"console-isolation-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
        _key = RandomNumberGenerator.GetBytes(32);
        _repo = new WebUserRepository(_dbPath);
    }

    public async Task InitializeAsync()
    {
        WebServerState.UpdateServers([MakeServer(ServerId)]);

        var (allowSvcs, allowPipeline, _) = WebHostSetup.CreateAuth(_repo, _key);
        _allowHost = new WebHostService();
        Assert.True(
            await _allowHost.TryStartAsync(
                new WebHostOptions(_allowPort, AllowLegacyWebSocketQueryStringAuth: true),
                allowSvcs,
                allowPipeline),
            _allowHost.LastStartErrorForTests());

        var (denySvcs, denyPipeline, _) = WebHostSetup.CreateAuth(_repo, _key);
        _denyHost = new WebHostService();
        Assert.True(
            await _denyHost.TryStartAsync(
                new WebHostOptions(_denyPort, AllowLegacyWebSocketQueryStringAuth: false),
                denySvcs,
                denyPipeline),
            _denyHost.LastStartErrorForTests());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (_allowHost != null) await _allowHost.StopWithTimeoutForTestsAsync();
        }
        finally
        {
            try
            {
                if (_denyHost != null) await _denyHost.StopWithTimeoutForTestsAsync();
            }
            finally
            {
                WebServerState.UpdateServers([]);
                ServerConsoleEndpoints.ActiveConnections.TryRemove(ServerId, out _);
                try { File.Delete(_dbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task Two_hosts_with_opposite_policies_running_together_each_keep_their_own()
    {
        var allowMarker = $"Allow-{Guid.NewGuid():N}";
        var denyMarker = $"Deny-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, allowMarker, ServerConsoleStream.Stdout);
        ServerConsoleService.Add(ServerId, denyMarker, ServerConsoleStream.Stdout);

        var allowToken = await LoginAsync(_allowPort, "viewer", "viewerPass1!");
        var denyToken = await LoginAsync(_denyPort, "viewer", "viewerPass1!");

        // Query-token-only connection to the host that allows it must still work...
        using var allowWs = new ClientWebSocket();
        await ConnectWithTimeoutAsync(allowWs,
            new Uri($"ws://localhost:{_allowPort}/api/servers/{ServerId}/console/stream?token={allowToken}"));
        using var allowCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var allowReceived = await ReadUntilAsync(allowWs,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(allowMarker),
            allowCts.Token);

        // ...at the same time as the host that denies it still rejects a query-token-only
        // connection (proven via a deliberately invalid first frame, same as the single-host test).
        using var denyWs = new ClientWebSocket();
        await ConnectWithTimeoutAsync(denyWs,
            new Uri($"ws://localhost:{_denyPort}/api/servers/{ServerId}/console/stream?token={denyToken}"));
        var badAuthBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token = "not-a-valid-token" }));
        await denyWs.SendAsync(new ArraySegment<byte>(badAuthBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        using var denyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var denyReceived = await ReadUntilAsync(denyWs,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(denyMarker),
            denyCts.Token);

        Assert.True(allowReceived, "Host started with AllowLegacyWebSocketQueryStringAuth: true should still accept a query-token-only connection.");
        Assert.False(denyReceived, "Host started with AllowLegacyWebSocketQueryStringAuth: false accepted a query-token-only connection - the policy leaked from the other host.");
    }

    private Task<string> LoginAsync(int port, string username, string password) =>
        Task.FromResult(JwtHelper.Create(
            _key, "1", username, WebRole.Viewer, TimeSpan.FromHours(1)));

    private static InstalledServer MakeServer(string id) =>
        new InstalledServer(
            Id: id, Name: $"Server {id}", ModuleId: "Stub",
            Runtime: "", ServerFolder: "", InstallPath: @"C:\stub",
            ConfigPath: "", IpAddress: "127.0.0.1", Port: "25565",
            SteamAppId: "", SteamBranch: "", MaxPlayers: "0",
            ProcessId: "1234", CpuUsage: "0%", MemoryUsage: "0 MB",
            PlayerCount: "0/0", CurrentStatusText: "Online", Uptime: "0m",
            IsOperationRunning: false, OperationText: "", LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Running,
            StatusText: "Online",
            StatusBrushKey: "", HasUpdateAvailable: false,
            LocalBuildId: "", RemoteBuildId: "", IgnoredBuildId: "",
            CanShowInfo: false, CanEditConfig: false, CanStart: false, CanStop: true);

    private static async Task<bool> ReadUntilAsync(
        ClientWebSocket ws, Func<JsonElement, bool> predicate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await ReadJsonMessageAsync(ws, ct);
            if (msg == null) return false;
            if (predicate(msg.Value)) return true;
        }
        return false;
    }

    private static async Task<JsonElement?> ReadJsonMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // A WebSocket message can arrive fragmented across multiple frames/receives - loop until
        // EndOfMessage rather than assuming one ReceiveAsync call gets the whole thing, with a
        // generous bound so a runaway/malformed stream can't grow this unbounded.
        const int MaxMessageBytes = 256 * 1024;
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxMessageBytes)
                    return null;
            } while (!result.EndOfMessage);

            ms.Position = 0;
            return JsonSerializer.Deserialize<JsonElement>(ms);
        }
        catch (OperationCanceledException) { return null; }
        catch (WebSocketException) { return null; }
    }
}
