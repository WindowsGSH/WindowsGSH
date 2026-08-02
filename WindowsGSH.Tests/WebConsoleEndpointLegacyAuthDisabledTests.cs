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

// The auth policy itself is captured per-host via DI (WebSocketAuthPolicy), not process-wide
// static state, so it can't race with other hosts. Still sharing WebTestCollection with
// WebConsoleEndpointTests because WebHostService.IsRunning/ActivePort/ActiveBindAddress remain
// static and shared across every WebHostService instance in the process.
[Collection(WebTestCollection.Name)]
public sealed class WebConsoleEndpointLegacyAuthDisabledTests : IClassFixture<AuthenticatedWebHostFixture>, IAsyncLifetime
{
    private readonly int _port;
    private readonly byte[] _key;
    private readonly HttpClient _http = new();

    private const string ServerId = "console-legacy-off-test-srv";

    public WebConsoleEndpointLegacyAuthDisabledTests(AuthenticatedWebHostFixture fixture)
    {
        _port = fixture.Port;
        _key = fixture.Key;
    }

    public async Task InitializeAsync()
    {
        WebServerState.UpdateServers([MakeServer(ServerId)]);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        WebServerState.UpdateServers([]);
        ServerConsoleEndpoints.ActiveConnections.TryRemove(ServerId, out _);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WebSocket_ignores_query_token_when_legacy_auth_is_disabled()
    {
        var marker = $"LegacyOff-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, marker, ServerConsoleStream.Stdout);

        var validToken = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        // A valid query token alone must no longer authenticate. The upgrade still succeeds
        // (the server always accepts first and authenticates via the first frame instead - see
        // ServerConsoleEndpoints path B), so prove the query token was ignored by sending a
        // deliberately invalid first frame: if the query token had (incorrectly) authenticated
        // the connection already, this bad frame would just be ignored as stray input and
        // buffered lines would still arrive.
        await ConnectWithTimeoutAsync(ws,
            new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream?token={validToken}"));

        var badAuthBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token = "not-a-valid-token" }));
        await ws.SendAsync(new ArraySegment<byte>(badAuthBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedMarker = await ReadUntilAsync(ws,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(marker),
            cts.Token);

        Assert.False(receivedMarker, "Query-string token authenticated the connection even though legacy auth is disabled.");
    }

    [Fact]
    public async Task WebSocket_first_frame_auth_still_works_when_legacy_auth_is_disabled()
    {
        var marker = $"LegacyOffFirstFrame-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, marker, ServerConsoleStream.Stdout);

        var token = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        await ConnectWithTimeoutAsync(ws,
            new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream"));

        var authBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token }));
        await ws.SendAsync(new ArraySegment<byte>(authBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var found = await ReadUntilAsync(ws,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(marker),
            cts.Token);

        Assert.True(found, "First-frame auth should still work when legacy query-string auth is disabled.");
    }

    private Task<string> LoginAsync(string username, string password) =>
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
