using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.Network;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PublicIpServiceTests
{
    private DateTimeOffset _now = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckAsync_skips_when_disabled()
    {
        var called = false;
        var service = new PublicIpService((_, _) =>
        {
            called = true;
            return Task.FromResult("203.0.113.10");
        });

        var result = await service.CheckAsync(Request(enabled: false));

        Assert.False(result.Checked);
        Assert.False(called);
    }

    [Fact]
    public async Task CheckAsync_skips_when_interval_has_not_elapsed()
    {
        var service = new PublicIpService(
            (_, _) => Task.FromResult("203.0.113.10"),
            () => _now);

        var result = await service.CheckAsync(Request(lastCheckedAt: _now.AddMinutes(-10), interval: TimeSpan.FromHours(1)));

        Assert.False(result.Checked);
    }

    [Fact]
    public async Task CheckAsync_detects_change_publishes_event_and_updates_server_config()
    {
        var bus = new WindowsGshEventBus();
        var events = new List<PublicIpChangedEvent>();
        bus.Subscribe<PublicIpChangedEvent>(events.Add);
        var configPath = CreateConfig();
        var service = new PublicIpService(
            (_, _) => Task.FromResult("203.0.113.20\n"),
            () => _now,
            bus);

        var result = await service.CheckAsync(Request(
            lastKnownIp: "203.0.113.10",
            serverConfigPaths: [configPath]));

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal("203.0.113.20", result.CurrentIp);
        var only = Assert.Single(events);
        Assert.Equal("203.0.113.10", only.PreviousIp);
        Assert.Equal("203.0.113.20", only.CurrentIp);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var network = document.RootElement.GetProperty("network");
        Assert.Equal("203.0.113.20", network.GetProperty("lastKnownPublicIp").GetString());
        Assert.Equal(_now.ToUniversalTime().ToString("O"), network.GetProperty("lastPublicIpCheckedAt").GetString());
    }

    [Fact]
    public async Task CheckAsync_invalid_response_is_nonfatal_with_retry_backoff()
    {
        var service = new PublicIpService(
            (_, _) => Task.FromResult("hello"),
            () => _now);

        var result = await service.CheckAsync(Request());

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Equal(PublicIpService.DefaultFailureBackoff, result.RetryAfter);
    }

    [Fact]
    public async Task CheckAsync_lookup_failure_is_nonfatal()
    {
        var service = new PublicIpService(
            (_, _) => throw new HttpRequestException("offline"),
            () => _now);

        var result = await service.CheckAsync(Request());

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Contains("offline", result.Message);
    }

    [Fact]
    public async Task CheckAsync_logs_server_config_write_failures_once()
    {
        var bus = new WindowsGshEventBus();
        var logEvents = new List<ServerLogEvent>();
        bus.Subscribe<ServerLogEvent>(logEvents.Add);
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "ServerConfig.json");
        await File.WriteAllTextAsync(configPath, "{ bad json");
        var service = new PublicIpService(
            (_, _) => Task.FromResult("203.0.113.20"),
            () => _now,
            bus);

        await service.CheckAsync(Request(serverConfigPaths: [configPath]));
        await service.CheckAsync(Request(serverConfigPaths: [configPath]));

        var warning = Assert.Single(logEvents, log => log.Severity == WindowsGshEventSeverity.Warning);
        Assert.Equal("Network", warning.Category);
        Assert.Contains("Could not persist public IP state", warning.Message);
        Assert.Contains("ServerConfig.json", warning.Message);
    }

    // These exercise the real FetchWithHttpClientAsync redirect-following logic (not the
    // fetchIpAsync test seam used above) by substituting a fake HttpMessageHandler, so the
    // security-relevant behavior — redirect revalidation, HTTPS enforcement per hop, relative
    // redirects, missing Location, and the redirect-count limit — is actually covered instead of
    // just asserted to look sound by inspection.

    [Fact]
    public async Task FetchWithHttpClient_rejects_redirect_from_public_to_private_address()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.Host == "8.8.8.8"
            ? Redirect("https://127.0.0.1/internal")
            : throw new InvalidOperationException("Should not have followed the redirect."));
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/")));

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Contains("non-public address", result.Message);
    }

    [Fact]
    public async Task FetchWithHttpClient_rejects_redirect_that_downgrades_to_http()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.Scheme == "https"
            ? Redirect("http://8.8.8.8/insecure")
            : throw new InvalidOperationException("Should not have followed the http redirect."));
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/")));

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Contains("non-public address", result.Message);
    }

    [Fact]
    public async Task FetchWithHttpClient_resolves_relative_redirect_locations()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/start" => Redirect("/next"),
            "/next" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("9.9.9.9") },
            _ => throw new InvalidOperationException("Unexpected path: " + request.RequestUri)
        });
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/start")));

        Assert.True(result.Success);
        Assert.Equal("9.9.9.9", result.CurrentIp);
    }

    [Fact]
    public async Task FetchWithHttpClient_fails_when_redirect_has_no_location_header()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/")));

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Contains("no Location header", result.Message);
    }

    [Fact]
    public async Task FetchWithHttpClient_fails_when_redirect_chain_exceeds_the_limit()
    {
        var handler = new FakeHttpMessageHandler(_ => Redirect("https://8.8.8.8/"));
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/")));

        Assert.True(result.Checked);
        Assert.False(result.Success);
        Assert.Contains("exceeded the maximum", result.Message);
    }

    [Fact]
    public async Task FetchWithHttpClient_succeeds_through_a_single_valid_redirect()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/a"
            ? Redirect("https://8.8.8.8/b")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1.2.3.4") });
        var service = new PublicIpService(fetchIpAsync: null, utcNow: () => _now, events: null, httpMessageHandler: handler);

        var result = await service.CheckAsync(Request(endpoint: new Uri("https://8.8.8.8/a")));

        Assert.True(result.Success);
        Assert.Equal("1.2.3.4", result.CurrentIp);
    }

    [Fact]
    public void IsProxyHop_is_false_when_endpoint_matches_the_request_origin()
    {
        Assert.False(PublicIpService.IsProxyHop(new Uri("https://api.ipify.org/"), new DnsEndPoint("api.ipify.org", 443)));
    }

    [Fact]
    public void IsProxyHop_is_true_when_endpoint_host_differs_from_the_request_origin()
    {
        // A configured proxy causes SocketsHttpHandler to ask the connect callback for the
        // proxy's own address (e.g. a corporate proxy at 10.0.0.5), not the origin being
        // requested — that mismatch is exactly what identifies a proxy hop.
        Assert.True(PublicIpService.IsProxyHop(new Uri("https://api.ipify.org/"), new DnsEndPoint("10.0.0.5", 443)));
    }

    [Fact]
    public void IsProxyHop_is_true_when_only_the_port_differs()
    {
        Assert.True(PublicIpService.IsProxyHop(new Uri("https://api.ipify.org/"), new DnsEndPoint("api.ipify.org", 8080)));
    }

    [Fact]
    public void IsProxyHop_is_false_when_request_uri_is_unavailable()
    {
        Assert.False(PublicIpService.IsProxyHop(null, new DnsEndPoint("10.0.0.5", 443)));
    }

    [Fact]
    public async Task ConnectWithFallbackAsync_falls_back_to_a_later_working_address()
    {
        // Two distinct loopback addresses sharing one port: only the second has a listener, so
        // connecting to the first must fail fast (connection refused) and the method must then
        // try the second rather than giving up after the first failure.
        using var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        await using var stream = await PublicIpService.ConnectWithFallbackAsync(
            [IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.2")], port, CancellationToken.None);

        using var accepted = await acceptTask;
        Assert.NotNull(stream);
        Assert.True(accepted.Connected);
    }

    [Fact]
    public async Task ConnectWithFallbackAsync_throws_when_every_candidate_fails()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PublicIpService.ConnectWithFallbackAsync(
                [IPAddress.Loopback, IPAddress.Loopback], port, CancellationToken.None).AsTask());

        Assert.Contains("Could not connect to any resolved address", exception.Message);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static PublicIpCheckRequest Request(
        bool enabled = true,
        string lastKnownIp = "",
        DateTimeOffset? lastCheckedAt = null,
        TimeSpan? interval = null,
        IReadOnlyList<string>? serverConfigPaths = null,
        Uri? endpoint = null)
    {
        return new PublicIpCheckRequest(
            enabled,
            endpoint ?? new Uri("https://example.test/ip"),
            interval ?? TimeSpan.Zero,
            lastKnownIp,
            lastCheckedAt,
            serverConfigPaths ?? []);
    }

    private static string CreateConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ServerConfig.json");
        File.WriteAllText(path, """{ "id": "1", "network": { "lastKnownPublicIp": "old" } }""");
        return path;
    }
}
