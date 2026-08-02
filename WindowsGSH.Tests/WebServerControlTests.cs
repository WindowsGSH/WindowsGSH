using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Api;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(WebTestCollection.Name)]
public sealed class WebServerControlTests : IClassFixture<AuthenticatedWebHostFixture>, IAsyncLifetime
{
    private readonly int _port;
    private readonly byte[] _key;
    private readonly HttpClient _http = new();

    // Test-local dispatch state (avoids touching ServerOperationManager.Shared).
    private volatile bool _testOperationRunning;
    private string? _lastCapturedDescription;

    public WebServerControlTests(AuthenticatedWebHostFixture fixture)
    {
        _port = fixture.Port;
        _key = fixture.Key;
    }

    public async Task InitializeAsync()
    {
        SetupStoppedServerState();
        SetupControlDispatch();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        WebServerState.UpdateServers([]);
        await Task.CompletedTask;
    }

    // Default state: server is STOPPED. CanStart: true means start is eligible.
    private static void SetupStoppedServerState()
    {
        var server = MakeServer(canStart: true, canStop: false, status: ServerRuntimeStatus.Offline, statusText: "Offline");
        WebServerState.UpdateServers([server]);
        WebServerState.SetServerMetrics(new StubMetrics());
        WebServerState.UpdateHostMetrics(new SystemMetricsSnapshot(
            SampledAt: DateTimeOffset.UtcNow, CpuModel: "Test CPU",
            LogicalProcessorCount: 8, TotalCpuPercent: 10, TotalMemoryBytes: 8_000_000_000L,
            UsedMemoryBytes: 4_000_000_000L, DriveRoot: "C:\\", DriveFormat: "NTFS",
            DriveTotalBytes: null, DriveFreeBytes: null, DriveUsedPercent: null,
            ProcessCpuPercent: null, ProcessMemoryBytes: null, Error: null));
    }

    // Running state: CanStart: false, CanStop: true — used for eligibility tests.
    private static void SetupRunningServerState()
    {
        var server = MakeServer(canStart: false, canStop: true, status: ServerRuntimeStatus.Running, statusText: "Online");
        WebServerState.UpdateServers([server]);
    }

    private static InstalledServer MakeServer(bool canStart, bool canStop, ServerRuntimeStatus status, string statusText)
        => new InstalledServer(
            Id: "srv-1", Name: "Test MC Server", ModuleId: "Minecraft",
            Runtime: "", ServerFolder: "servers/srv-1", InstallPath: @"C:\servers\srv-1",
            ConfigPath: "", IpAddress: "0.0.0.0", Port: "25565",
            SteamAppId: "", SteamBranch: "", MaxPlayers: "20",
            ProcessId: canStop ? "1234" : "", CpuUsage: "0%", MemoryUsage: "0 MB",
            PlayerCount: canStop ? "3/20" : "0/20", CurrentStatusText: statusText, Uptime: "0m",
            IsOperationRunning: false, OperationText: "", LastOperationError: null,
            IsInstalled: true, Status: status, StatusText: statusText,
            StatusBrushKey: canStop ? "StatusRunning" : "StatusStopped", HasUpdateAvailable: false,
            LocalBuildId: "1.20.4", RemoteBuildId: "", IgnoredBuildId: "",
            CanShowInfo: true, CanEditConfig: true, CanStart: canStart, CanStop: canStop);

    private void SetupControlDispatch()
    {
        WebServerControl.SetDispatch((serverId, kind, username) =>
        {
            var server = WebServerState.GetServers().FirstOrDefault(s => s.Id == serverId);
            if (server == null)
                return WebControlOutcome.NotFound();

            // Simulate cancel audit (production code calls TryCancelWithAudit).
            if (kind == WebControlKind.Cancel)
            {
                if (!_testOperationRunning)
                    return WebControlOutcome.NothingToCancel();
                _testOperationRunning = false;
                _lastCapturedDescription = $"Cancelled via web by {username}";
                return WebControlOutcome.Accepted();
            }

            // Simulate eligibility checks (mirrors MainWindow.WebControl.cs).
            if (kind == WebControlKind.Start && !server.CanStart)
                return WebControlOutcome.Conflict("ServerAlreadyRunning");
            if (kind == WebControlKind.Stop && !server.CanStop)
                return WebControlOutcome.Conflict("ServerAlreadyOffline");
            if (kind == WebControlKind.Restart && !server.CanStop && !server.CanStart)
                return WebControlOutcome.Conflict("ServerNotRestartable");
            if (kind == WebControlKind.Update && server.CanStop)
                return WebControlOutcome.Conflict("ServerMustBeOfflineToUpdate");
            if (kind == WebControlKind.Update && !server.CanStop && !server.CanStart)
                return WebControlOutcome.Conflict("ServerNotUpdateable");

            // ForceStop may always proceed; others require no running operation.
            if (kind != WebControlKind.ForceStop && _testOperationRunning)
                return WebControlOutcome.Conflict("Start");

            _testOperationRunning = true;
            _lastCapturedDescription = $"Initiated via web by {username}";
            return WebControlOutcome.Accepted();
        });
    }

    private Task<string> GetTokenAsync(string username, string password) =>
        Task.FromResult(JwtHelper.Create(
            _key,
            "1",
            username,
            username.Equals("viewer", StringComparison.OrdinalIgnoreCase) ? WebRole.Viewer : WebRole.Operator,
            TimeSpan.FromHours(1)));

    private HttpRequestMessage AuthorizedPost(string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(string.Empty);
        return req;
    }

    private HttpRequestMessage AuthorizedGet(string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_port}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    // --- Policy tests ---

    [Fact]
    public async Task Viewer_token_on_start_returns_403()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/start", token));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_start_returns_401()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/api/servers/srv-1/start");
        var response = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Start ---

    [Fact]
    public async Task Operator_start_on_stopped_server_returns_202()
    {
        _testOperationRunning = false; // stopped server is set up by default
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/start", token));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Second_concurrent_start_returns_409()
    {
        _testOperationRunning = true;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/start", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("conflict", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_unknown_server_returns_404()
    {
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/does-not-exist/start", token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Eligibility ---

    [Fact]
    public async Task Start_when_server_already_running_returns_409()
    {
        SetupRunningServerState(); // CanStart: false
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/start", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ServerAlreadyRunning", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_when_server_already_offline_returns_409()
    {
        // Default stopped state has CanStop: false.
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/stop", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ServerAlreadyOffline", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_when_server_running_returns_409()
    {
        SetupRunningServerState(); // CanStop: true
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/update", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ServerMustBeOfflineToUpdate", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_on_non_restartable_server_returns_409()
    {
        // CanStart=false, CanStop=false: neither start nor stop is available (e.g. problem/incomplete install).
        var server = MakeServer(canStart: false, canStop: false, status: ServerRuntimeStatus.Offline, statusText: "Problem");
        WebServerState.UpdateServers([server]);
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/restart", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ServerNotRestartable", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_on_non_updateable_server_returns_409()
    {
        // CanStart=false, CanStop=false: server is offline but not in a clean state for updates.
        var server = MakeServer(canStart: false, canStop: false, status: ServerRuntimeStatus.Offline, statusText: "Problem");
        WebServerState.UpdateServers([server]);
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/update", token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ServerNotUpdateable", body, StringComparison.Ordinal);
    }

    // --- Force stop ---

    [Fact]
    public async Task Force_stop_response_contains_warning_field()
    {
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/force-stop", token));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("warning", out _), "Response must contain a 'warning' field");
    }

    [Fact]
    public async Task Force_stop_proceeds_even_when_operation_is_running()
    {
        _testOperationRunning = true;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/force-stop", token));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // --- Cancel ---

    [Fact]
    public async Task Cancel_active_operation_returns_200()
    {
        _testOperationRunning = true;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/cancel", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_with_no_active_operation_returns_404()
    {
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/cancel", token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_records_web_username_in_audit_description()
    {
        _testOperationRunning = true;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/cancel", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_lastCapturedDescription);
        Assert.Contains("operator", _lastCapturedDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cancelled", _lastCapturedDescription, StringComparison.OrdinalIgnoreCase);
    }

    // --- Operation status ---

    [Fact]
    public async Task Viewer_can_read_operation_status()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1/operation", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("isComplete", out _));
        Assert.True(root.TryGetProperty("succeeded", out _));
        Assert.True(root.TryGetProperty("progressMessage", out _));
    }

    [Fact]
    public async Task Operation_status_returns_404_for_unknown_server()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/does-not-exist/operation", token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Audit: web username in description ---

    [Fact]
    public async Task Accepted_operation_captures_web_username_in_description()
    {
        _testOperationRunning = false;
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedPost("/api/servers/srv-1/start", token));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(_lastCapturedDescription);
        Assert.Contains("operator", _lastCapturedDescription, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubMetrics : IServerMetricsService
    {
        public ServerMetricsSnapshot Sample(IGameServerModule? module, ServerInstance instance, int? maxPlayers = null)
            => throw new NotSupportedException();
        public ServerMetricsSnapshot? GetLatest(string serverId) => null;
        public IReadOnlyList<ServerMetricsSnapshot> GetHistory(string serverId) => [];
        public void RemoveSeries(string serverId) { }
        public void PruneStale(DateTimeOffset olderThan) { }
    }
}
