using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WindowsGSH.Core.Diagnostics;

namespace WindowsGSH.Core.Network;

public interface IExternalReachabilityService
{
    Task<ExternalReachabilityCheckResult> CheckAsync(
        ExternalReachabilityCheckRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalReachabilityCheckRequest(bool Enabled, IReadOnlyList<int> Ports);

public enum ExternalReachabilityOutcome
{
    Skipped,
    NoPortsToTest,
    Success,
    RateLimited,
    Unavailable
}

public enum ExternalPortReachability
{
    Reachable,
    Refused,
    TimedOut,
    Unavailable
}

public sealed record ExternalPortReachabilityResult(int Port, ExternalPortReachability Status, int ElapsedMilliseconds);

public sealed record ExternalReachabilityCheckResult(
    ExternalReachabilityOutcome Outcome,
    string Message,
    IReadOnlyList<ExternalPortReachabilityResult>? Results = null,
    string? AddressFamily = null,
    TimeSpan? RetryAfter = null)
{
    public static ExternalReachabilityCheckResult Skipped(string message) =>
        new(ExternalReachabilityOutcome.Skipped, message);

    public static ExternalReachabilityCheckResult NoPortsToTest(string message) =>
        new(ExternalReachabilityOutcome.NoPortsToTest, message);

    public static ExternalReachabilityCheckResult Unavailable(string message) =>
        new(ExternalReachabilityOutcome.Unavailable, message);

    public static ExternalReachabilityCheckResult RateLimited(string message, TimeSpan? retryAfter) =>
        new(ExternalReachabilityOutcome.RateLimited, message, RetryAfter: retryAfter);
}

// Client for the WindowsGSH-operated reachability probe (https://probe.windowsgsh.com,
// https://github.com/WindowsGSH/windowsgsh-reachability-probe) - an opt-in, user-triggered check of
// whether specific TCP ports are reachable from outside the local network. Deliberately does not
// reuse PublicIpService's DNS-rebinding/redirect-revalidation machinery: that exists because
// PublicIpEndpoint is a user-editable setting, whereas this service's endpoint is a fixed, first-party
// URL with no equivalent user-controlled-endpoint threat.
public sealed class ExternalReachabilityService : IExternalReachabilityService
{
    private static readonly Uri Endpoint = new("https://probe.windowsgsh.com/v1/tcp-check");
    // The probe service itself may spend up to three seconds on its outbound TCP connect. The
    // desktop's total HTTP budget must additionally allow for DNS, TLS, Cloudflare/hosting latency,
    // and returning the JSON response; using the same three-second value would commonly cancel just
    // before a legitimate timed_out result arrived.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly ProductInfoHeaderValue UserAgentHeader =
        new("WindowsGSH", SanitizeVersionToken(AppVersionInfo.DisplayVersion));

    // Mirrors the probe API's own validation exactly (see its README/tests): at most 5 unique ports,
    // port 25 always blocked, 1-65535 inclusive. Any request the API itself would reject collapses
    // into one generic 400 with no detail, so pre-filtering client-side both avoids a wasted
    // rate-limited round trip and lets this method report something more useful than "invalid
    // request" for a locally-detectable problem.
    private const int MaxPorts = 5;
    private const int BlockedPort = 25;
    private const long MaxResponseContentBytes = 64 * 1024;

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = DefaultTimeout
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string> _requestIdFactory;

    public ExternalReachabilityService() : this(SharedClient)
    {
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can inject an HttpClient
    // wrapping a fake HttpMessageHandler - the same pattern AdoptiumApiClient already uses.
    //
    // Deliberately does NOT set httpClient.Timeout here: HttpClient.Timeout can only be assigned
    // before the FIRST request is ever sent on that instance - it throws InvalidOperationException
    // ("this instance has already started one or more requests") on every attempt after that. The
    // public parameterless constructor below always reuses the same static SharedClient across every
    // call, by design (connection pooling) - so forcing the timeout here would work exactly once per
    // app session and then throw on every subsequent construction, for every server, for the rest of
    // that session. SharedClient's own static initializer already sets Timeout = DefaultTimeout once,
    // before it is ever used, which is sufficient; a caller-supplied HttpClient in tests is expected
    // to already be configured the way that test needs.
    internal ExternalReachabilityService(HttpClient httpClient, Func<string>? requestIdFactory = null)
    {
        _httpClient = httpClient;
        _requestIdFactory = requestIdFactory ?? (() => Guid.NewGuid().ToString());
    }

    public async Task<ExternalReachabilityCheckResult> CheckAsync(
        ExternalReachabilityCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Enabled)
        {
            return ExternalReachabilityCheckResult.Skipped("External reachability checks are disabled.");
        }

        var ports = request.Ports
            .Where(port => port is >= 1 and <= 65535 && port != BlockedPort)
            .Distinct()
            .Order()
            .Take(MaxPorts)
            .ToArray();

        if (ports.Length == 0)
        {
            return ExternalReachabilityCheckResult.NoPortsToTest(
                "No eligible TCP ports were available to test.");
        }

        try
        {
            var requestId = _requestIdFactory();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new TcpCheckRequestBody(ports, requestId))
            };
            httpRequest.Headers.UserAgent.Add(UserAgentHeader);

            using var response = await _httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ExternalReachabilityCheckResult.RateLimited(
                    "The reachability service is rate-limiting requests; try again shortly.",
                    GetRetryAfter(response.Headers.RetryAfter));
            }

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine(
                    $"External reachability check received HTTP {(int)response.StatusCode} from the probe service.");
                return ExternalReachabilityCheckResult.Unavailable(
                    "The external reachability service could not complete the request.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseContentBytes)
            {
                return ExternalReachabilityCheckResult.Unavailable(
                    "The external reachability service returned an unexpected response.");
            }

            await response.Content
                .LoadIntoBufferAsync(MaxResponseContentBytes, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content
                .ReadFromJsonAsync<TcpCheckResponseBody>(cancellationToken)
                .ConfigureAwait(false);
            if (!IsValidResponse(body, requestId, ports))
            {
                return ExternalReachabilityCheckResult.Unavailable(
                    "The external reachability service returned an unexpected response.");
            }

            var results = body!.Results!
                .Select(item => new ExternalPortReachabilityResult(item.Port, ParseStatus(item.Status), item.ElapsedMs))
                .ToArray();

            return new ExternalReachabilityCheckResult(
                ExternalReachabilityOutcome.Success,
                "External reachability check completed.",
                results,
                body.AddressFamily);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Covers HttpRequestException (DNS/TLS/connection failures), a client-side timeout
            // (HttpClient.Timeout surfaces as a TaskCanceledException unrelated to this call's own
            // cancellationToken), and a malformed/truncated response body (JsonException) - all
            // treated identically: "could not determine," never claiming a port is open or closed as
            // if the probe had actually run. A genuine cancellation of this call's own
            // cancellationToken falls through uncaught instead, matching normal cancellation
            // semantics elsewhere in this codebase.
            return ExternalReachabilityCheckResult.Unavailable(
                "The external reachability service could not be reached.");
        }
    }

    private static ExternalPortReachability ParseStatus(string status) => status switch
    {
        "reachable" => ExternalPortReachability.Reachable,
        "refused" => ExternalPortReachability.Refused,
        "timed_out" => ExternalPortReachability.TimedOut,
        _ => ExternalPortReachability.Unavailable
    };

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is not { } date)
        {
            return null;
        }

        var remaining = date - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool IsValidResponse(TcpCheckResponseBody? body, string requestId, IReadOnlyList<int> requestedPorts)
    {
        if (body?.Results == null ||
            !string.Equals(body.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(body.Protocol, "tcp", StringComparison.Ordinal) ||
            body.AddressFamily is not ("ipv4" or "ipv6") ||
            body.Results.Count != requestedPorts.Count)
        {
            return false;
        }

        var requested = requestedPorts.ToHashSet();
        var returned = new HashSet<int>();
        foreach (var result in body.Results)
        {
            if (!requested.Contains(result.Port) ||
                !returned.Add(result.Port) ||
                result.ElapsedMs < 0 ||
                result.Status is not ("reachable" or "refused" or "timed_out" or "unavailable"))
            {
                return false;
            }
        }

        return returned.SetEquals(requested);
    }

    // ProductInfoHeaderValue enforces RFC 7230 token syntax for its version component - strip
    // anything that isn't alphanumeric/dot/dash so a build's informational version string (which can
    // contain characters like '+' or spaces in edge cases) never throws a FormatException here.
    private static string SanitizeVersionToken(string version)
    {
        var sanitized = new string(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
        return string.IsNullOrEmpty(sanitized) ? "0.0.0" : sanitized;
    }

    private sealed record TcpCheckRequestBody(
        [property: JsonPropertyName("ports")] IReadOnlyList<int> Ports,
        [property: JsonPropertyName("requestId")] string RequestId);

    private sealed record TcpCheckResponseBody(
        [property: JsonPropertyName("requestId")] string? RequestId,
        [property: JsonPropertyName("addressFamily")] string? AddressFamily,
        [property: JsonPropertyName("protocol")] string? Protocol,
        [property: JsonPropertyName("results")] IReadOnlyList<TcpCheckPortResult>? Results);

    private sealed record TcpCheckPortResult(
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("elapsedMs")] int ElapsedMs);
}
