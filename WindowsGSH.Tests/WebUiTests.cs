using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

/// <summary>
/// Tests for the static-file web UI, CSP headers, and browser cookie-based auth flow.
/// </summary>
[Collection(WebTestCollection.Name)]
public sealed class WebUiTests : IAsyncLifetime
{
    private static int _nextPort = 15700;
    private readonly int _port = Interlocked.Increment(ref _nextPort);
    private readonly string _dbPath;
    private readonly byte[] _key;
    private readonly WebUserRepository _repo;
    private WebHostService? _host;

    // Handler with cookie jar so we can inspect Set-Cookie responses.
    private readonly HttpClientHandler _handler = new() { UseCookies = true, AllowAutoRedirect = false };
    private readonly HttpClient _http;

    public WebUiTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ui-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(_dbPath);
        _key = RandomNumberGenerator.GetBytes(32);
        _repo = new WebUserRepository(_dbPath);
        _http = new HttpClient(_handler);
    }

    public async Task InitializeAsync()
    {
        await new WebTokenService(_repo, _key).CreateUserAsync("admin", "AdminPass1!", WebRole.Admin);
        await new WebTokenService(_repo, _key).CreateUserAsync("viewer", "ViewerPass1!", WebRole.Viewer);

        var (configSvcs, configPipeline, _) = WebHostSetup.CreateAuth(_repo, _key);
        _host = new WebHostService();
        Assert.True(
            await _host.TryStartAsync(new WebHostOptions(_port), configSvcs, configPipeline),
            _host.LastStartErrorForTests());
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        try
        {
            if (_host != null) await _host.StopWithTimeoutForTestsAsync();
        }
        finally
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    // ── CSP header ────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_responses_include_CSP_header()
    {
        // Hit the public login endpoint — no auth required.
        var resp = await _http.PostAsync(
            $"http://localhost:{_port}/api/auth/login",
            new StringContent("{\"username\":\"x\",\"password\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.True(resp.Headers.Contains("Content-Security-Policy"),
            "Expected Content-Security-Policy header on every response.");
    }

    [Fact]
    public async Task CSP_header_contains_script_src_self()
    {
        var resp = await _http.PostAsync(
            $"http://localhost:{_port}/api/auth/login",
            new StringContent("{\"username\":\"x\",\"password\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        var csp = resp.Headers.GetValues("Content-Security-Policy").First();
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp);
    }

    [Fact]
    public async Task All_responses_include_X_Frame_Options_DENY()
    {
        var resp = await _http.PostAsync(
            $"http://localhost:{_port}/api/auth/login",
            new StringContent("{\"username\":\"x\",\"password\":\"x\"}", System.Text.Encoding.UTF8, "application/json"));

        Assert.True(resp.Headers.TryGetValues("X-Frame-Options", out var vals));
        Assert.Equal("DENY", vals!.First());
    }

    // ── Login sets HttpOnly cookie ────────────────────────────────────────────

    [Fact]
    public async Task Login_sets_HttpOnly_refresh_cookie()
    {
        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The cookie jar should contain gsh_rt scoped to /api/auth.
        var cookies = _handler.CookieContainer.GetCookies(
            new Uri($"http://localhost:{_port}/api/auth"));
        var rt = cookies["gsh_rt"];

        Assert.NotNull(rt);
        Assert.True(rt.HttpOnly, "Refresh cookie must be HttpOnly.");
        Assert.False(string.IsNullOrEmpty(rt.Value), "Refresh cookie value must not be empty.");
    }

    [Fact]
    public async Task Login_over_normal_http_does_not_mark_refresh_cookie_secure()
    {
        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain("; secure", GetRefreshSetCookie(resp), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_https_marks_refresh_cookie_secure()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";

        Assert.True(AuthEndpoints.ShouldUseSecureRefreshCookie(ctx));
    }

    [Fact]
    public async Task Forwarded_proto_https_with_trust_enabled_marks_refresh_cookie_secure()
    {
        var port = Interlocked.Increment(ref _nextPort);
        var dbPath = Path.Combine(Path.GetTempPath(), $"ui-forwarded-test-{Guid.NewGuid():N}.db");
        AppDatabase.Initialize(dbPath);
        await using var host = new WebHostService();
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var repo = new WebUserRepository(dbPath);
            await new WebTokenService(repo, key).CreateUserAsync("admin", "AdminPass1!", WebRole.Admin);
            var (configSvcs, configPipeline, _) = WebHostSetup.CreateAuth(repo, key, trustForwardedHeaders: true);
            Assert.True(await host.TryStartAsync(new WebHostOptions(port), configSvcs, configPipeline));

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/auth/login")
            {
                Content = JsonContent.Create(new { username = "admin", password = "AdminPass1!" })
            };
            req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("; secure", GetRefreshSetCookie(resp), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Forwarded_proto_https_with_trust_disabled_does_not_mark_refresh_cookie_secure()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "admin", password = "AdminPass1!" })
        };
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        var resp = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.DoesNotContain("; secure", GetRefreshSetCookie(resp), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_proto_https_from_non_loopback_ip_is_ignored_even_when_trust_enabled()
    {
        // Proves that a LAN/internet client cannot spoof X-Forwarded-Proto: https
        // even when trustForwardedHeaders = true, because KnownProxies = {loopback only}.
        // We exercise ForwardedHeadersMiddleware directly since the test HttpClient always
        // connects from loopback and cannot simulate a remote non-loopback RemoteIpAddress.
        var options = Options.Create(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback },
        });
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50"); // non-loopback LAN IP
        ctx.Request.Headers["X-Forwarded-Proto"] = "https";
        ctx.Request.Scheme = "http";

        await middleware.Invoke(ctx);

        // Scheme must remain http — untrusted proxy's header must be discarded.
        Assert.Equal("http", ctx.Request.Scheme);
    }

    [Fact]
    public async Task Browser_login_response_does_not_return_refresh_token_in_body()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "admin", password = "AdminPass1!" })
        };
        req.Headers.TryAddWithoutValidation("X-WindowsGSH-Client", "browser");

        var resp = await _http.SendAsync(req);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("refreshToken", out var refreshToken));
        Assert.Equal(JsonValueKind.Null, refreshToken.ValueKind);
    }

    [Fact]
    public async Task Login_also_returns_refresh_token_in_body_for_API_clients()
    {
        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var rt = body.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrEmpty(rt), "refreshToken must still be in JSON body for API clients.");
    }

    [Fact]
    public async Task Login_response_includes_role()
    {
        var resp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "viewer", password = "ViewerPass1!" });

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Viewer", body.GetProperty("role").GetString());
    }

    // ── Refresh via HttpOnly cookie ───────────────────────────────────────────

    [Fact]
    public async Task Refresh_with_cookie_returns_new_access_token()
    {
        // Step 1: login — cookie is stored in _handler's CookieContainer.
        var loginResp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });
        var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        var firstToken = loginBody.GetProperty("accessToken").GetString();

        // Step 2: refresh with empty body — server reads token from the cookie.
        var refreshResp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/refresh",
            new { });   // empty body — refreshToken == null

        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
        var refreshBody = await refreshResp.Content.ReadFromJsonAsync<JsonElement>();
        var newToken = refreshBody.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(newToken), "Refresh should return a new access token.");
    }

    [Fact]
    public async Task Refresh_with_cookie_rotates_cookie()
    {
        await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });

        var before = _handler.CookieContainer
            .GetCookies(new Uri($"http://localhost:{_port}/api/auth"))["gsh_rt"]?.Value;

        await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/refresh",
            new { });

        var after = _handler.CookieContainer
            .GetCookies(new Uri($"http://localhost:{_port}/api/auth"))["gsh_rt"]?.Value;

        Assert.NotNull(after);
        // Token rotation: the cookie value after refresh should differ from the one set at login.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Refresh_with_body_token_still_works_for_API_clients()
    {
        // API clients that store the refresh token in their own storage use the body.
        // This must remain functional (backward compatibility).
        var loginResp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });
        var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        var rt = loginBody.GetProperty("refreshToken").GetString();

        // Use a fresh HttpClient with no cookies to simulate an API client.
        using var apiClient = new HttpClient();
        var refreshResp = await apiClient.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/refresh",
            new { refreshToken = rt });

        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
    }

    // ── Logout clears the cookie ──────────────────────────────────────────────

    [Fact]
    public async Task Logout_clears_the_HttpOnly_cookie()
    {
        await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });

        var before = _handler.CookieContainer
            .GetCookies(new Uri($"http://localhost:{_port}/api/auth"))["gsh_rt"];
        Assert.NotNull(before);

        // Logout with empty body — server reads token from cookie.
        var logoutResp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/logout",
            new { });
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        // After logout the refresh cookie should be expired / removed.
        var after = _handler.CookieContainer
            .GetCookies(new Uri($"http://localhost:{_port}/api/auth"))["gsh_rt"];
        // Cookie is cleared by setting Expires in the past — value is now empty or cookie gone.
        Assert.True(after == null || string.IsNullOrEmpty(after.Value),
            "Cookie should be cleared after logout.");
    }

    // ── Page routes ───────────────────────────────────────────────────────────

    // Page routes serve HTML files from wwwroot/. These tests only run when the
    // output directory contains the static files (i.e. after a normal build).
    // If the wwwroot folder is absent the tests verify a 404 — that is also safe.

    // ── P2: Per-IP rate limiter on login ─────────────────────────────────────

    // ── P3-03: /api/status requires auth and returns version ─────────────────

    [Fact]
    public async Task Status_endpoint_returns_401_without_auth()
    {
        using var anon = new HttpClient();
        var resp = await anon.GetAsync($"http://localhost:{_port}/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Status_endpoint_returns_version_and_utc_when_authenticated()
    {
        var loginResp = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "AdminPass1!" });
        var loginBody = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        var token = loginBody.GetProperty("accessToken").GetString();

        using var authClient = new HttpClient();
        authClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await authClient.GetAsync($"http://localhost:{_port}/api/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("version", out _), "/api/status must return version.");
        Assert.True(body.TryGetProperty("utc", out var utcProp), "/api/status must return utc.");
        Assert.True(DateTime.TryParse(utcProp.GetString(), out _), "utc must be a valid ISO-8601 datetime.");
    }

    // ── P2: Per-IP rate limiter on login ─────────────────────────────────────

    [Fact]
    public async Task Login_endpoint_returns_429_after_ten_attempts_from_same_IP()
    {
        // Send 10 attempts — all pass through the rate limiter (they may 401 but not 429).
        for (var i = 0; i < 10; i++)
        {
            var r = await _http.PostAsJsonAsync(
                $"http://localhost:{_port}/api/auth/login",
                new { username = "admin", password = $"wrong{i}" });
            Assert.NotEqual(429, (int)r.StatusCode);
        }

        // 11th attempt from same loopback IP must be rate-limited.
        var limited = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/login",
            new { username = "admin", password = "wrong" });
        Assert.Equal(429, (int)limited.StatusCode);
    }

    [Fact]
    public async Task Reset_password_endpoint_returns_429_after_ten_invalid_attempts_from_same_IP()
    {
        for (var i = 0; i < 10; i++)
        {
            var r = await _http.PostAsJsonAsync(
                $"http://localhost:{_port}/api/auth/reset-password",
                new { resetToken = $"invalid-token-{i}", newPassword = "NewPassword1!" });
            Assert.NotEqual(429, (int)r.StatusCode);
        }

        var limited = await _http.PostAsJsonAsync(
            $"http://localhost:{_port}/api/auth/reset-password",
            new { resetToken = "invalid-token-final", newPassword = "NewPassword1!" });
        Assert.Equal(429, (int)limited.StatusCode);
    }

    [Fact]
    public async Task GET_root_returns_html()
    {
        var resp = await _http.GetAsync($"http://localhost:{_port}/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GET_dashboard_returns_html()
    {
        var resp = await _http.GetAsync($"http://localhost:{_port}/dashboard");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GET_page_includes_CSP_header()
    {
        var resp = await _http.GetAsync($"http://localhost:{_port}/");
        // CSP must be present regardless of whether the file exists.
        Assert.True(resp.Headers.Contains("Content-Security-Policy"),
            "Page routes must include Content-Security-Policy.");
    }

    [Fact]
    public void Static_ui_uses_sessionStorage_not_localStorage()
    {
        var files = ReadStaticUiFiles();
        var combined = string.Join(Environment.NewLine, files.Select(item => item.Content));

        Assert.Contains("sessionStorage", combined);
        Assert.DoesNotContain("localStorage", combined);
    }

    [Fact]
    public void Dashboard_and_server_controls_are_gated_by_operator_role()
    {
        var dashboard = ReadStaticUiFile("js", "dashboard.js");
        var server = ReadStaticUiFile("js", "server.js");

        Assert.Contains("canOperate()", dashboard);
        Assert.Contains("data-action=\"start\"", dashboard);
        Assert.Contains("data-action=\"stop\"", dashboard);
        Assert.Contains("data-action=\"restart\"", dashboard);

        Assert.Contains("canOperate()", server);
        Assert.Contains("actionsEl.innerHTML = ''", server);
        Assert.Contains("id=\"startBtn\"", server);
        Assert.Contains("id=\"stopBtn\"", server);
        Assert.Contains("id=\"rstBtn\"", server);
    }

    [Fact]
    public void Console_input_is_hidden_by_default_and_gated_by_operator_role()
    {
        var consoleHtml = ReadStaticUiFile("console.html");
        var consoleJs = ReadStaticUiFile("js", "console.js");

        Assert.Contains("console-input-row hidden", consoleHtml);
        Assert.Contains("canOperate()", consoleJs);
        Assert.Contains("inputRow.classList.remove('hidden')", consoleJs);
    }

    [Fact]
    public void Static_ui_has_no_inline_styles_or_javascript_urls()
    {
        foreach (var file in ReadStaticUiFiles())
        {
            Assert.DoesNotContain("style=", file.Content);
            Assert.DoesNotContain("javascript:", file.Content);
        }
    }

    private static IReadOnlyList<(string Path, string Content)> ReadStaticUiFiles()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Assert.True(Directory.Exists(root), $"Expected copied web UI assets at {root}");

        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .Select(path => (path, File.ReadAllText(path)))
            .ToArray();
    }

    private static string ReadStaticUiFile(params string[] segments)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"Expected copied web UI asset at {path}");
        return File.ReadAllText(path);
    }

    private static string GetRefreshSetCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values), "Expected Set-Cookie header.");
        var cookie = values.SingleOrDefault(value => value.StartsWith("gsh_rt=", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(cookie), "Expected gsh_rt Set-Cookie header.");
        return cookie!;
    }
}
