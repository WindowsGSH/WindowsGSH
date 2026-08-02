using System.Net;
using System.Net.Http;
using System.Text.Json;
using WindowsGSH.Core.Web;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(WebTestCollection.Name)]
public sealed class WebHostServiceTests : IAsyncLifetime
{
    // Use a unique port range per test to avoid cross-test conflicts.
    private static int _nextPort = 15100;
    private readonly int _port = Interlocked.Increment(ref _nextPort);
    private WebHostService? _service;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service != null)
            await _service.StopWithTimeoutForTestsAsync();
    }

    [Fact]
    public async Task Health_endpoint_returns_ok_status_only()
    {
        _service = new WebHostService();
        var started = await _service.TryStartAsync(new WebHostOptions(_port));

        Assert.True(started, _service.LastStartErrorForTests());
        Assert.True(WebHostService.IsRunning);
        Assert.Equal(_port, WebHostService.ActivePort);

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_port}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        // Version must NOT appear in the unauthenticated /health response (P3-03).
        Assert.False(root.TryGetProperty("version", out _),
            "/health must not disclose version to unauthenticated callers.");
    }

    [Fact]
    public async Task Health_endpoint_is_accessible_without_auth()
    {
        _service = new WebHostService();
        Assert.True(
            await _service.TryStartAsync(new WebHostOptions(_port)),
            _service.LastStartErrorForTests());

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_port}/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Service_stops_cleanly_and_releases_port()
    {
        _service = new WebHostService();
        Assert.True(
            await _service.TryStartAsync(new WebHostOptions(_port)),
            _service.LastStartErrorForTests());
        await _service.StopAsync();

        Assert.False(WebHostService.IsRunning);
        Assert.Null(WebHostService.ActivePort);

        // Port should be free — a second service can bind to it.
        await using var service2 = new WebHostService();
        var started = await service2.TryStartAsync(new WebHostOptions(_port));
        Assert.True(started, service2.LastStartErrorForTests());
    }

    [Fact]
    public async Task Port_in_use_returns_false_without_crashing()
    {
        _service = new WebHostService();
        Assert.True(
            await _service.TryStartAsync(new WebHostOptions(_port)),
            _service.LastStartErrorForTests());

        await using var service2 = new WebHostService();
        var started = await service2.TryStartAsync(new WebHostOptions(_port));

        Assert.False(started);
        Assert.True(WebHostService.IsRunning, "original service should still be running");
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        _service = new WebHostService();
        Assert.True(
            await _service.TryStartAsync(new WebHostOptions(_port)),
            _service.LastStartErrorForTests());

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_port}/not-a-real-path");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void AppSettings_WebEnabled_defaults_to_false()
    {
        var settings = new AppSettings();
        Assert.False(settings.WebEnabled);
    }

    [Fact]
    public void AppSettings_WebPort_defaults_to_5000()
    {
        var settings = new AppSettings();
        Assert.Equal(5000, settings.WebPort);
    }

    [Fact]
    public void AppSettings_WebTrustForwardedHeaders_defaults_to_false()
    {
        var settings = new AppSettings();
        Assert.False(settings.WebTrustForwardedHeaders);
    }

    [Fact]
    public void AppSettings_roundtrips_WebEnabled_and_WebPort()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wgsh-test-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { WebEnabled = true, WebPort = 8080, WebTrustForwardedHeaders = true }.SaveTo(path);
            var loaded = AppSettings.LoadFrom(path);
            Assert.True(loaded.WebEnabled);
            Assert.Equal(8080, loaded.WebPort);
            Assert.True(loaded.WebTrustForwardedHeaders);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AppSettings_ApplyFrom_copies_web_settings()
    {
        var live = new AppSettings { WebEnabled = false, WebPort = 5000, WebBindAddress = "127.0.0.1", WebTrustForwardedHeaders = false };
        var candidate = live.CreateCopy();
        candidate.WebEnabled = true;
        candidate.WebPort = 9000;
        candidate.WebBindAddress = "0.0.0.0";
        candidate.WebTrustForwardedHeaders = true;

        Assert.False(live.WebEnabled);
        Assert.Equal(5000, live.WebPort);
        Assert.Equal("127.0.0.1", live.WebBindAddress);
        Assert.False(live.WebTrustForwardedHeaders);

        live.ApplyFrom(candidate);

        Assert.True(live.WebEnabled);
        Assert.Equal(9000, live.WebPort);
        Assert.Equal("0.0.0.0", live.WebBindAddress);
        Assert.True(live.WebTrustForwardedHeaders);
    }

    // ── P2-01: IsLoopbackAddress ──────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.100", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("", false)]
    public void IsLoopbackAddress_classifies_correctly(string address, bool expectedLoopback)
    {
        Assert.Equal(expectedLoopback, WebHostService.IsLoopbackAddress(address));
    }

    // ── P3: IsValidBindAddress ────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("*", false)]
    [InlineData("+", false)]
    [InlineData("http://127.0.0.1:5000", false)]
    [InlineData("127.0.0.1:5000", false)]
    [InlineData("not an address", false)]
    public void IsValidBindAddress_classifies_correctly(string address, bool expected)
    {
        Assert.Equal(expected, WebHostService.IsValidBindAddress(address));
    }

    // ── P3: FormatBindAddress brackets IPv6 literals for Kestrel URLs ─────────

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("localhost", "localhost")]
    [InlineData("::1", "[::1]")]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    [InlineData("fe80::1", "[fe80::1]")]
    public void FormatBindAddress_brackets_IPv6_and_leaves_IPv4_alone(string input, string expected)
    {
        Assert.Equal(expected, WebHostService.FormatBindAddress(input));
    }
}
