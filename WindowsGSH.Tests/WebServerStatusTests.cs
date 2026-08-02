using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Api;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(WebTestCollection.Name)]
public sealed class WebServerStatusTests : IClassFixture<AuthenticatedWebHostFixture>, IAsyncLifetime
{
    private readonly int _port;
    private readonly string _dbPath;
    private readonly byte[] _key;
    private readonly WebUserRepository _repo;
    private readonly HttpClient _http = new();

    public WebServerStatusTests(AuthenticatedWebHostFixture fixture)
    {
        _port = fixture.Port;
        _dbPath = fixture.DatabasePath;
        _key = fixture.Key;
        _repo = fixture.Repository;
    }

    public async Task InitializeAsync()
    {
        SetupServerState();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        WebServerState.UpdateServers([]);
        await Task.CompletedTask;
    }

    private static void SetupServerState()
    {
        var server = new InstalledServer(
            Id: "srv-1",
            Name: "Test MC Server",
            ModuleId: "Minecraft",
            Runtime: "",
            ServerFolder: "servers/srv-1",
            InstallPath: @"C:\servers\srv-1",
            ConfigPath: "",
            IpAddress: "0.0.0.0",
            Port: "25565",
            SteamAppId: "",
            SteamBranch: "",
            MaxPlayers: "20",
            ProcessId: "1234",
            CpuUsage: "12%",
            MemoryUsage: "512 MB",
            PlayerCount: "3/20",
            CurrentStatusText: "Online",
            Uptime: "1h 0m",
            IsOperationRunning: false,
            OperationText: "",
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Running,
            StatusText: "Online",
            StatusBrushKey: "StatusRunning",
            HasUpdateAvailable: false,
            LocalBuildId: "1.20.4",
            RemoteBuildId: "",
            IgnoredBuildId: "",
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: false,
            CanStop: true);

        WebServerState.UpdateServers([server]);
        WebServerState.SetServerMetrics(new StubMetrics());
        WebServerState.UpdateHostMetrics(new SystemMetricsSnapshot(
            SampledAt: DateTimeOffset.UtcNow,
            CpuModel: "Test CPU",
            LogicalProcessorCount: 8,
            TotalCpuPercent: 22.5,
            TotalMemoryBytes: 17_179_869_184L,
            UsedMemoryBytes: 8_589_934_592L,
            DriveRoot: "C:\\",
            DriveFormat: "NTFS",
            DriveTotalBytes: null,
            DriveFreeBytes: null,
            DriveUsedPercent: null,
            ProcessCpuPercent: null,
            ProcessMemoryBytes: null,
            Error: null));
    }

    private Task<string> GetTokenAsync(string username, string password) =>
        Task.FromResult(JwtHelper.Create(
            _key,
            "1",
            username,
            username.Equals("viewer", StringComparison.OrdinalIgnoreCase) ? WebRole.Viewer : WebRole.Operator,
            TimeSpan.FromHours(1)));

    private HttpRequestMessage AuthorizedGet(string path, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_port}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        var response = await _http.GetAsync($"http://localhost:{_port}/api/servers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_token_returns_server_list()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(1, arr.GetArrayLength());

        var item = arr[0];
        Assert.Equal("srv-1", item.GetProperty("id").GetString());
        Assert.Equal("Test MC Server", item.GetProperty("name").GetString());
        Assert.Equal("Minecraft", item.GetProperty("module").GetString());
        Assert.Equal("Online", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Viewer_server_detail_does_not_include_install_path()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("srv-1", root.GetProperty("id").GetString());
        // installPath must be absent from the response entirely for Viewer role.
        Assert.False(root.TryGetProperty("installPath", out _), "installPath must not appear in Viewer response");
    }

    [Fact]
    public async Task Operator_server_detail_includes_install_path()
    {
        var token = await GetTokenAsync("operator", "operPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var installPath = doc.RootElement.GetProperty("installPath").GetString();
        Assert.Equal(@"C:\servers\srv-1", installPath);
    }

    [Fact]
    public async Task Unknown_server_id_returns_404()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/does-not-exist", token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Logs_endpoint_returns_at_most_max_lines()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1/logs?max=5", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() <= 5);
    }

    [Fact]
    public async Task Metrics_endpoint_returns_null_fields_when_no_sample()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1/metrics", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // StubMetrics returns null — all metric fields should be null/missing.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sampledAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cpuPercent").ValueKind);
    }

    [Fact]
    public async Task Host_metrics_endpoint_returns_expected_fields()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/host/metrics", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("sampledAt", out _));
        Assert.Equal(22.5, root.GetProperty("totalCpuPercent").GetDouble(), precision: 1);
    }

    [Fact]
    public async Task Server_detail_contains_last_started_and_last_stopped_fields()
    {
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Fields must exist (null when no history is available).
        Assert.True(root.TryGetProperty("lastStarted", out _));
        Assert.True(root.TryGetProperty("lastStopped", out _));
    }

    [Fact]
    public async Task Logs_endpoint_redacts_secret_patterns()
    {
        // Inject a console line containing a secret-like pattern.
        ServerConsoleService.Add("srv-1", "password = \"hunter2\"");
        var token = await GetTokenAsync("viewer", "viewerPass1!");
        var response = await _http.SendAsync(AuthorizedGet("/api/servers/srv-1/logs", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_change_token_is_rejected_by_status_endpoints()
    {
        var tokenService = new WebTokenService(_repo, _key);
        await tokenService.CreateUserAsync("fpc-user", "TempPass1!", WebRole.Operator);
        var created = await _repo.FindByUsernameAsync("fpc-user");
        Assert.NotNull(created);
        await _repo.UpdateAsync(created with { ForcePasswordChange = true });

        var loginResponse = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { Username = "fpc-user", Password = "TempPass1!" });
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.True(loginResponse.IsSuccessStatusCode,
            $"Login failed ({loginResponse.StatusCode}): {loginBody}");
        using var loginJson = JsonDocument.Parse(loginBody);
        var token = loginJson.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // But the status endpoints must refuse this token with 403.
        var response = await _http.SendAsync(AuthorizedGet("/api/servers", token!));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Restart_operation_counts_as_last_started_in_lifecycle_times()
    {
        var restartTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        OperationHistoryRepository.Add(new ServerOperationSnapshot(
            ServerId: "restart-srv",
            ServerName: "Restart Test Server",
            Kind: ServerOperationKind.Restart,
            Status: "Completed",
            StartedAt: restartTime.AddSeconds(-30),
            LastError: null,
            IsActive: false,
            FinishedAt: restartTime), _dbPath);

        var times = OperationHistoryRepository.GetServerLifecycleTimes("restart-srv", _dbPath);

        Assert.NotNull(times.LastStarted);
        Assert.NotNull(times.LastStopped);
        Assert.Equal(restartTime.ToUnixTimeSeconds(), times.LastStarted!.Value.ToUnixTimeSeconds());
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
