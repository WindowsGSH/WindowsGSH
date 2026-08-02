using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

[Collection(WebTestCollection.Name)]
public sealed class WebConsoleEndpointTests : IClassFixture<LegacyAuthenticatedWebHostFixture>, IAsyncLifetime
{
    private readonly int _port;
    private readonly byte[] _key;
    private readonly HttpClient _http = new();

    private const string ServerId = "console-test-srv";

    public WebConsoleEndpointTests(LegacyAuthenticatedWebHostFixture fixture)
    {
        _port = fixture.Port;
        _key = fixture.Key;
    }

    public async Task InitializeAsync()
    {
        WebServerState.UpdateServers([MakeServer(ServerId, running: true)]);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        WebServerState.UpdateServers([]);
        ServerConsoleEndpoints.ActiveConnections.TryRemove(ServerId, out _);
        await Task.CompletedTask;
    }

    // ── History ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_returns_200_with_buffered_lines_for_viewer()
    {
        ServerConsoleService.Add(ServerId, "Hello from test", ServerConsoleStream.Stdout);

        var token = await LoginAsync("viewer", "viewerPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.GetAsync($"http://localhost:{_port}/api/servers/{ServerId}/console/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var lines = await resp.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(lines);
        Assert.Contains(lines, l =>
            l.TryGetProperty("line", out var lineEl) && lineEl.GetString()!.Contains("Hello from test"));
    }

    [Fact]
    public async Task History_returns_401_without_token()
    {
        var resp = await _http.GetAsync($"http://localhost:{_port}/api/servers/{ServerId}/console/history");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task History_returns_404_for_unknown_server()
    {
        var token = await LoginAsync("viewer", "viewerPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.GetAsync($"http://localhost:{_port}/api/servers/no-such-server/console/history");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task History_count_is_capped_at_500()
    {
        var token = await LoginAsync("viewer", "viewerPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.GetAsync($"http://localhost:{_port}/api/servers/{ServerId}/console/history?count=9999");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── Console input ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Input_returns_409_when_server_has_no_stdin()
    {
        var token = await LoginAsync("operator", "operPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/servers/{ServerId}/console/input",
            new { command = "say hello" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Input_returns_401_without_token()
    {
        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/servers/{ServerId}/console/input",
            new { command = "say hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Input_returns_403_for_viewer_role()
    {
        var token = await LoginAsync("viewer", "viewerPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/servers/{ServerId}/console/input",
            new { command = "say hello" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Input_returns_404_for_unknown_server()
    {
        var token = await LoginAsync("operator", "operPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/servers/no-such-server/console/input",
            new { command = "say hello" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Input_returns_400_for_blank_command()
    {
        var token = await LoginAsync("operator", "operPass1!");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/servers/{ServerId}/console/input",
            new { command = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── WebSocket stream ──────────────────────────────────────────────────────

    [Fact]
    public async Task WebSocket_with_valid_token_connects_and_receives_buffered_lines()
    {
        var marker = $"Buffered-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, marker, ServerConsoleStream.Stdout);

        var token = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        await ConnectWithTimeoutAsync(ws,
            new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream?token={token}"));

        Assert.Equal(WebSocketState.Open, ws.State);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool found = false;
        while (!cts.IsCancellationRequested)
        {
            var msg = await ReadJsonMessageAsync(ws, cts.Token);
            if (msg == null) break;
            if (msg.Value.TryGetProperty("line", out var l) && l.GetString()!.Contains(marker))
            {
                found = true;
                break;
            }
        }

        Assert.True(found, "Expected buffered line not received from WebSocket.");

        // Close with a timeout so the test cannot hang if something goes wrong.
        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token); }
        catch { /* server may already have closed */ }
    }

    [Fact]
    public async Task WebSocket_without_query_token_accepts_first_frame_auth()
    {
        var marker = $"FirstFrame-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, marker, ServerConsoleStream.Stdout);

        var token = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        await ConnectWithTimeoutAsync(ws,
            new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream"));

        var authBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token }));
        await ws.SendAsync(
            new ArraySegment<byte>(authBytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var found = await ReadUntilAsync(ws,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(marker),
            cts.Token);

        Assert.True(found, "Expected buffered line not received after first-frame WebSocket auth.");

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token); }
        catch { }
    }

    [Fact]
    public async Task WebSocket_with_invalid_token_is_rejected()
    {
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            ConnectWithTimeoutAsync(ws,
                new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream?token=not-a-valid-token")));
    }

    [Fact]
    public async Task WebSocket_for_unknown_server_is_rejected()
    {
        var token = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            ConnectWithTimeoutAsync(ws,
                new Uri($"ws://localhost:{_port}/api/servers/no-such-server/console/stream?token={token}")));
    }

    [Fact]
    public async Task WebSocket_receives_live_lines_after_connect()
    {
        // Seed a known anchor line in history. The server subscribes to NewLineAdded BEFORE
        // replaying history, so receiving the anchor proves the subscription is already active —
        // any subsequent ServerConsoleService.Add call is guaranteed to be delivered.
        var anchor = $"Anchor-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, anchor, ServerConsoleStream.Stdout);

        var token = await LoginAsync("viewer", "viewerPass1!");
        using var ws = new ClientWebSocket();
        await ConnectWithTimeoutAsync(ws,
            new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream?token={token}"));

        // Wait for the anchor line — proves the server's NewLineAdded subscription is active.
        using var anchorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool anchorFound = await ReadUntilAsync(ws,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(anchor),
            anchorCts.Token);
        Assert.True(anchorFound, "Anchor history line not received — server may not have connected.");

        // Now safe to add the live line.
        var liveLine = $"Live-{Guid.NewGuid():N}";
        ServerConsoleService.Add(ServerId, liveLine, ServerConsoleStream.Stdout);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool liveFound = await ReadUntilAsync(ws,
            msg => msg.TryGetProperty("line", out var l) && l.GetString()!.Contains(liveLine),
            cts.Token);

        Assert.True(liveFound, "Live console line not received from WebSocket within timeout.");

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token); }
        catch { }
    }

    [Fact]
    public async Task WebSocket_offline_server_sends_offline_status_message()
    {
        const string offlineId = "console-offline-srv";
        WebServerState.UpdateServers([MakeServer(ServerId, running: true), MakeServer(offlineId, running: false)]);
        ServerConsoleEndpoints.ActiveConnections.TryRemove(offlineId, out _);
        try
        {
            var token = await LoginAsync("viewer", "viewerPass1!");
            using var ws = new ClientWebSocket();
            await ConnectWithTimeoutAsync(ws,
                new Uri($"ws://localhost:{_port}/api/servers/{offlineId}/console/stream?token={token}"));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool gotOffline = await ReadUntilAsync(ws,
                msg => msg.TryGetProperty("status", out var s) && s.GetString() == "offline",
                cts.Token);

            Assert.True(gotOffline, "Expected { status: 'offline' } message not received.");

            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token); }
            catch { }
        }
        finally
        {
            ServerConsoleEndpoints.ActiveConnections.TryRemove(offlineId, out _);
            WebServerState.UpdateServers([MakeServer(ServerId, running: true)]);
        }
    }

    [Fact]
    public async Task WebSocket_11th_connection_returns_503()
    {
        ServerConsoleEndpoints.ActiveConnections[ServerId] = 10;
        try
        {
            var token = await LoginAsync("viewer", "viewerPass1!");
            using var ws = new ClientWebSocket();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                ConnectWithTimeoutAsync(ws,
                    new Uri($"ws://localhost:{_port}/api/servers/{ServerId}/console/stream?token={token}")));

            // Count decremented back to 10 after the 503 rejection.
            var count = ServerConsoleEndpoints.ActiveConnections.TryGetValue(ServerId, out var c) ? c : 0;
            Assert.Equal(10, count);
        }
        finally
        {
            ServerConsoleEndpoints.ActiveConnections.TryRemove(ServerId, out _);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<string> LoginAsync(string username, string password) =>
        Task.FromResult(JwtHelper.Create(
            _key,
            "1",
            username,
            username.Equals("viewer", StringComparison.OrdinalIgnoreCase) ? WebRole.Viewer : WebRole.Operator,
            TimeSpan.FromHours(1)));

    private static InstalledServer MakeServer(string id, bool running) =>
        new InstalledServer(
            Id: id, Name: $"Server {id}", ModuleId: "Stub",
            Runtime: "", ServerFolder: "", InstallPath: @"C:\stub",
            ConfigPath: "", IpAddress: "127.0.0.1", Port: "25565",
            SteamAppId: "", SteamBranch: "", MaxPlayers: "0",
            ProcessId: running ? "1234" : "", CpuUsage: "0%", MemoryUsage: "0 MB",
            PlayerCount: "0/0", CurrentStatusText: running ? "Online" : "Offline", Uptime: "0m",
            IsOperationRunning: false, OperationText: "", LastOperationError: null,
            IsInstalled: true,
            Status: running ? ServerRuntimeStatus.Running : ServerRuntimeStatus.Offline,
            StatusText: running ? "Online" : "Offline",
            StatusBrushKey: "", HasUpdateAvailable: false,
            LocalBuildId: "", RemoteBuildId: "", IgnoredBuildId: "",
            CanShowInfo: false, CanEditConfig: false, CanStart: !running, CanStop: running);

    // Reads messages until predicate returns true, or until null/timeout.
    // Returns true if predicate was satisfied, false if connection closed or cancelled.
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
