using System.Net;
using System.Text;
using System.Text.Json;
using WindowsGSH.Core.Network;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ExternalReachabilityServiceTests
{
    [Fact]
    public async Task CheckAsync_returns_Skipped_and_makes_no_request_when_disabled()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("No request should have been sent while disabled."));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(false, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Skipped, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task CheckAsync_returns_NoPortsToTest_when_every_port_is_filtered_out()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("No request should have been sent with no eligible ports."));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        // 25 is always blocked, 0 and 70000 are out of the valid 1-65535 range.
        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25, 0, 70000]));

        Assert.Equal(ExternalReachabilityOutcome.NoPortsToTest, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task CheckAsync_filters_deduplicates_and_caps_the_outgoing_port_list()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[],"durationMs":1}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        // 8 candidate ports: one duplicate, one blocked (25), one out of range (99999) - after
        // filtering/dedup/ordering, only 6 remain, and only the first 5 (ascending) may be sent.
        await service.CheckAsync(new ExternalReachabilityCheckRequest(
            true,
            [7777, 7777, 25, 99999, 25565, 80, 443, 8080]));

        Assert.NotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var sentPorts = document.RootElement.GetProperty("ports").EnumerateArray()
            .Select(element => element.GetInt32())
            .ToArray();

        Assert.Equal([80, 443, 7777, 8080, 25565], sentPorts);
    }

    [Fact]
    public async Task CheckAsync_sends_only_the_ports_and_requestId_fields()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[],"durationMs":1}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var fieldNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(["ports", "requestId"], fieldNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_sets_an_explicit_WindowsGSH_user_agent()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[],"durationMs":1}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        var userAgent = handler.LastRequest?.Headers.UserAgent.ToString();
        Assert.NotNull(userAgent);
        Assert.StartsWith("WindowsGSH/", userAgent);
    }

    [Fact]
    public async Task CheckAsync_maps_a_successful_response_to_per_port_results_and_address_family()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "requestId": "r",
              "addressFamily": "ipv4",
              "protocol": "tcp",
              "results": [
                { "port": 25565, "status": "reachable", "elapsedMs": 12 },
                { "port": 80, "status": "refused", "elapsedMs": 5 },
                { "port": 443, "status": "timed_out", "elapsedMs": 3000 },
                { "port": 8080, "status": "unavailable", "elapsedMs": 0 }
              ],
              "durationMs": 3020
            }
            """));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565, 80, 443, 8080]));

        Assert.Equal(ExternalReachabilityOutcome.Success, result.Outcome);
        Assert.Equal("ipv4", result.AddressFamily);
        Assert.NotNull(result.Results);
        Assert.Equal(4, result.Results!.Count);
        Assert.Equal(ExternalPortReachability.Reachable, result.Results[0].Status);
        Assert.Equal(ExternalPortReachability.Refused, result.Results[1].Status);
        Assert.Equal(ExternalPortReachability.TimedOut, result.Results[2].Status);
        Assert.Equal(ExternalPortReachability.Unavailable, result.Results[3].Status);
        Assert.Equal(12, result.Results[0].ElapsedMilliseconds);
    }

    [Fact]
    public async Task CheckAsync_reports_rate_limiting_and_reads_the_RetryAfter_header()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    """{"error":{"code":"rate_limited","message":"Too many probe requests. Try again later."}}""",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);
    }

    [Fact]
    public async Task CheckAsync_reads_an_absolute_date_RetryAfter_header()
    {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(retryAt);
            return response;
        });
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.RateLimited, result.Outcome);
        Assert.NotNull(result.RetryAfter);
        Assert.InRange(result.RetryAfter.Value, TimeSpan.FromMinutes(1.5), TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CheckAsync_returns_Unavailable_for_documented_non_success_status_codes(HttpStatusCode statusCode)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"error":{"code":"x","message":"y"}}""", Encoding.UTF8, "application/json")
        });
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_returns_Unavailable_for_a_malformed_response_body()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, "not valid json{{{"));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_rejects_an_oversized_response_body()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            new string('x', 64 * 1024 + 1)));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_returns_Unavailable_for_a_success_response_with_no_results_field()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","durationMs":1}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Constructing_a_second_service_against_an_already_used_client_does_not_throw()
    {
        // Regression guard: HttpClient.Timeout can only be assigned before the first request is ever
        // sent on that instance - assigning it again afterward throws InvalidOperationException. The
        // production parameterless constructor always reuses the same static SharedClient across
        // every call (by design, for connection pooling), so the constructor must never attempt to
        // mutate an already-used client's Timeout - doing so would make every "Test External
        // Reachability" click after the very first one throw, for the rest of the app session.
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[{"port":25565,"status":"reachable","elapsedMs":1}]}"""));
        var httpClient = new HttpClient(handler);

        var first = new ExternalReachabilityService(httpClient, () => "r");
        var firstResult = await first.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));
        Assert.Equal(ExternalReachabilityOutcome.Success, firstResult.Outcome);

        var exception = Record.Exception(() => new ExternalReachabilityService(httpClient, () => "r"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("""{"requestId":"wrong","addressFamily":"ipv4","protocol":"tcp","results":[{"port":25565,"status":"reachable","elapsedMs":1}]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"ipv4","protocol":"udp","results":[{"port":25565,"status":"reachable","elapsedMs":1}]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"unknown","protocol":"tcp","results":[{"port":25565,"status":"reachable","elapsedMs":1}]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[{"port":12345,"status":"reachable","elapsedMs":1}]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[{"port":25565,"status":"unexpected","elapsedMs":1}]}""")]
    [InlineData("""{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[{"port":25565,"status":"reachable","elapsedMs":-1}]}""")]
    public async Task CheckAsync_rejects_a_success_response_that_does_not_match_the_request_contract(string body)
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, body));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_rejects_duplicate_results_even_when_the_count_matches()
    {
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[{"port":25565,"status":"reachable","elapsedMs":1},{"port":25565,"status":"reachable","elapsedMs":1}]}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565, 27015]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_returns_Unavailable_when_the_underlying_request_throws()
    {
        var handler = new ThrowingHandler(() => new HttpRequestException("Simulated DNS/connection failure."));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");

        var result = await service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]));

        Assert.Equal(ExternalReachabilityOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_propagates_the_callers_own_cancellation_instead_of_reporting_Unavailable()
    {
        // Regression guard: a client-side HttpClient.Timeout (or any other unrelated cancellation)
        // must be swallowed into Unavailable, but the CALLER's own cancellation must still propagate
        // normally rather than being silently reported as a plain "unavailable" result.
        var handler = new FakeHttpMessageHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{"requestId":"r","addressFamily":"ipv4","protocol":"tcp","results":[],"durationMs":1}"""));
        var service = new ExternalReachabilityService(new HttpClient(handler), () => "r");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAsync(new ExternalReachabilityCheckRequest(true, [25565]), cts.Token));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            return respond(request);
        }
    }

    private sealed class ThrowingHandler(Func<Exception> createException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw createException();
        }
    }
}
