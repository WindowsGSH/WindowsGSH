using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Network;

public sealed class PublicIpService : IPublicIpService
{
    public static readonly TimeSpan DefaultFailureBackoff = TimeSpan.FromMinutes(5);
    private const int MaxRedirects = 5;

    private readonly object _networkStateFailureLock = new();
    private readonly HashSet<string> _loggedNetworkStateFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Uri, CancellationToken, Task<string>> _fetchIpAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IWindowsGshEventBus _events;
    private readonly HttpClient _httpClient;

    public PublicIpService(
        Func<Uri, CancellationToken, Task<string>>? fetchIpAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        IWindowsGshEventBus? events = null,
        HttpMessageHandler? httpMessageHandler = null)
    {
        // AllowAutoRedirect is disabled on the default handler so every redirect hop can be
        // revalidated against PublicIpEndpointPolicy before it's followed (P3-04 follow-up):
        // otherwise a public HTTPS endpoint could redirect to a loopback/private/CGNAT address
        // and the default handler would follow it without re-checking. ConnectCallback resolves,
        // validates, and connects to the same address in one step, so there's no window between
        // "checked" and "used" for a DNS name that could resolve differently a moment later
        // (DNS-rebinding). httpMessageHandler is exposed only so tests can substitute a fake
        // handler and exercise the redirect-following logic without real sockets/DNS.
        _httpClient = new HttpClient(httpMessageHandler ?? CreateDefaultHandler())
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _fetchIpAsync = fetchIpAsync ?? ((endpoint, ct) => FetchWithHttpClientAsync(_httpClient, endpoint, ct));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _events = events ?? WindowsGshEventBus.Shared;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectToValidatedAddressAsync
    };

    /// <summary>
    /// True when <paramref name="dnsEndPoint"/> — what the connect callback was actually asked to
    /// connect to — differs from the original request's own host/port. That only happens when a
    /// proxy (system or user-configured, e.g. a corporate proxy at a loopback/RFC1918 address)
    /// applies: SocketsHttpHandler then calls the connect callback to reach the *proxy*, not the
    /// public-IP origin. The origin itself is still validated per redirect hop in
    /// FetchWithHttpClientAsync before any request is sent, so it's safe — and necessary for
    /// proxy users — to skip the public/private-address policy for a proxy hop and trust the
    /// proxy the user/system already configured.
    /// </summary>
    internal static bool IsProxyHop(Uri? requestUri, DnsEndPoint dnsEndPoint)
    {
        return requestUri != null &&
            (!string.Equals(requestUri.Host, dnsEndPoint.Host, StringComparison.OrdinalIgnoreCase) || requestUri.Port != dnsEndPoint.Port);
    }

    /// <summary>
    /// Tries every candidate address in order (e.g. a dual-stack host that returns AAAA before A)
    /// rather than only the first, so a machine with broken/unreachable IPv6 can still fall back
    /// to a working IPv4 address instead of failing outright.
    /// </summary>
    internal static async ValueTask<Stream> ConnectWithFallbackAsync(IReadOnlyList<IPAddress> candidates, int port, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var address in candidates)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("Could not connect to any resolved address.", lastFailure);
    }

    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var isProxyHop = IsProxyHop(context.InitialRequestMessage.RequestUri, context.DnsEndPoint);

        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literal))
        {
            candidates = [literal];
        }
        else
        {
            using var timeoutCts = new CancellationTokenSource(PublicIpEndpointPolicy.DnsResolutionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                candidates = await Dns.GetHostAddressesAsync(host, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                throw new InvalidOperationException($"Could not resolve host '{host}' for the public IP endpoint.", ex);
            }
        }

        var allowedCandidates = isProxyHop
            ? candidates
            : candidates.Where(candidate => !PublicIpEndpointPolicy.IsNonPublicAddress(candidate)).ToArray();

        if (allowedCandidates.Length == 0)
        {
            throw new InvalidOperationException($"Host '{host}' resolves only to loopback/private/link-local addresses; refusing to connect.");
        }

        try
        {
            return await ConnectWithFallbackAsync(allowedCandidates, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Could not connect to any resolved address for host '{host}'.", ex);
        }
    }

    public async Task<PublicIpCheckResult> CheckAsync(PublicIpCheckRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Enabled)
        {
            return PublicIpCheckResult.Skipped("Public IP tracking is disabled.");
        }

        var now = _utcNow();
        var interval = request.CheckInterval <= TimeSpan.Zero ? TimeSpan.FromMinutes(60) : request.CheckInterval;
        if (request.LastCheckedAt.HasValue && now - request.LastCheckedAt.Value < interval)
        {
            return PublicIpCheckResult.Skipped("Public IP check interval has not elapsed.");
        }

        string ipText;
        try
        {
            ipText = NormalizeIp(await _fetchIpAsync(request.Endpoint, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PublicIpCheckResult.Failed("Public IP lookup failed: " + ex.Message, now, DefaultFailureBackoff);
        }

        if (!IPAddress.TryParse(ipText, out var parsed))
        {
            return PublicIpCheckResult.Failed($"Public IP lookup returned an invalid address: {ipText}", now, DefaultFailureBackoff);
        }

        var currentIp = parsed.ToString();
        var previousIp = request.LastKnownIp.Trim();
        var changed = !string.Equals(previousIp, currentIp, StringComparison.OrdinalIgnoreCase);
        foreach (var configPath in request.ServerConfigPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryWriteServerNetworkState(configPath, currentIp, now);
        }

        if (changed)
        {
            _events.Publish(new PublicIpChangedEvent(now, previousIp, currentIp));
            _events.Publish(new ServerLogEvent(
                now,
                null,
                null,
                WindowsGshEventSeverity.Info,
                "Network",
                string.IsNullOrWhiteSpace(previousIp)
                    ? $"Public IP detected: {currentIp}"
                    : $"Public IP changed from {previousIp} to {currentIp}."));
        }

        return new PublicIpCheckResult(
            Checked: true,
            Changed: changed,
            Success: true,
            CurrentIp: currentIp,
            PreviousIp: previousIp,
            CheckedAt: now,
            Message: changed ? "Public IP changed." : "Public IP unchanged.");
    }

    private static async Task<string> FetchWithHttpClientAsync(HttpClient httpClient, Uri endpoint, CancellationToken cancellationToken)
    {
        var current = endpoint;
        for (var redirectCount = 0; ; redirectCount++)
        {
            if (!await PublicIpEndpointPolicy.IsAllowedEndpointAsync(current, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Public IP endpoint '{current}' resolves to a non-public address.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            if (redirectCount >= MaxRedirects)
            {
                throw new InvalidOperationException($"Public IP endpoint '{endpoint}' exceeded the maximum of {MaxRedirects} redirects.");
            }

            var location = response.Headers.Location
                ?? throw new InvalidOperationException($"Public IP endpoint '{current}' returned a redirect with no Location header.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static string NormalizeIp(string value)
    {
        return value.Trim().Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private void TryWriteServerNetworkState(string configPath, string currentIp, DateTimeOffset checkedAt)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return;
            }

            var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            if (root == null)
            {
                return;
            }

            var network = root["network"] as JsonObject ?? [];
            root["network"] = network;
            network["lastKnownPublicIp"] = currentIp;
            network["lastPublicIpCheckedAt"] = checkedAt.ToUniversalTime().ToString("O");
            AtomicFile.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LogNetworkStateWriteFailureOnce(configPath, ex, checkedAt);
        }
    }

    private void LogNetworkStateWriteFailureOnce(string configPath, Exception ex, DateTimeOffset checkedAt)
    {
        var key = $"{configPath}|{ex.GetType().FullName}|{ex.Message}";
        lock (_networkStateFailureLock)
        {
            if (!_loggedNetworkStateFailures.Add(key))
            {
                return;
            }
        }

        _events.Publish(new ServerLogEvent(
            checkedAt,
            null,
            null,
            WindowsGshEventSeverity.Warning,
            "Network",
            $"Could not persist public IP state to server config {Path.GetFileName(configPath)}: {ex.Message}",
            ex.ToString()));
    }
}
