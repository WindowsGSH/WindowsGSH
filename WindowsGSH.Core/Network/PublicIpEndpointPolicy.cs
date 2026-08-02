using System.Net;
using System.Net.Sockets;

namespace WindowsGSH.Core.Network;

/// <summary>
/// Shared validation for the "public IP" tracking endpoint (P3-04 / P2 follow-up). Used both
/// when the user saves the setting (<c>AppSettingsView.SaveConfig</c>) and every time the
/// background tracker actually reads and uses it (<c>MainWindow.RunPublicIpTrackingAsync</c>),
/// so an endpoint that predates this validation (an upgraded settings file, or one hand-edited
/// outside the app) can't keep sending plaintext/private-network requests indefinitely just
/// because the Settings page was never re-saved.
/// </summary>
public static class PublicIpEndpointPolicy
{
    /// <summary>
    /// True for loopback, RFC1918 private, link-local, IPv6 unique-local, and CGNAT
    /// (100.64.0.0/10) addresses — hosts that can't plausibly be a real "what's my public IP"
    /// service. Only meaningful for literal IP hosts; DNS names are left to resolve normally.
    /// </summary>
    public static bool IsNonPublicAddress(IPAddress address)
    {
        // IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) must be unwrapped first: IsLoopback and the
        // IPv6-only properties below don't recognize e.g. "::ffff:127.0.0.1" or
        // "::ffff:169.254.169.254" (a cloud metadata endpoint) as non-public, so an endpoint using
        // the mapped form would otherwise bypass every check in this method.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] is >= 16 and <= 31)      // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16 link-local
                || (b[0] == 100 && b[1] is >= 64 and <= 127);    // 100.64.0.0/10 CGNAT
        }

        return false;
    }

    /// <summary>
    /// True if the endpoint is an absolute HTTPS URL whose host, when it's a literal IP address,
    /// is not loopback/private/link-local/CGNAT. Uses <see cref="Uri.DnsSafeHost"/> rather than
    /// <see cref="Uri.Host"/> so IPv6 literals (which <c>Host</c> returns bracketed, e.g. "[::1]")
    /// parse correctly instead of silently failing <see cref="IPAddress.TryParse(string, out IPAddress)"/>
    /// and skipping the non-public check entirely.
    /// </summary>
    /// <remarks>
    /// This overload only inspects literal IP hosts. A DNS name that currently resolves to a
    /// public address but is later repointed at loopback/private/CGNAT (or that already does,
    /// e.g. via a hosts-file entry) will pass this check. Callers that can afford an async DNS
    /// lookup should prefer <see cref="IsAllowedEndpointAsync"/>, which also resolves and checks
    /// DNS names.
    /// </remarks>
    public static bool IsAllowedEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            return false;

        if (IPAddress.TryParse(endpoint.DnsSafeHost, out var hostAddress) && IsNonPublicAddress(hostAddress))
            return false;

        return true;
    }

    /// <summary>
    /// Same policy as <see cref="IsAllowedEndpoint"/>, but for hosts given as a DNS name (not a
    /// literal IP) it also resolves the name and rejects the endpoint if any resolved address is
    /// loopback/private/link-local/CGNAT. Closes the bypass where a DNS name resolving internally
    /// (e.g. via split-horizon DNS or a hosts-file entry) would otherwise be accepted because
    /// <see cref="IsAllowedEndpoint"/> only inspects literal IP hosts.
    /// </summary>
    /// <summary>
    /// Upper bound on how long DNS resolution in <see cref="IsAllowedEndpointAsync"/> is allowed
    /// to take, so a slow or unresponsive DNS name can't leave Settings-save (or the background
    /// tracker) hanging indefinitely.
    /// </summary>
    public static readonly TimeSpan DnsResolutionTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> IsAllowedEndpointAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = endpoint.DnsSafeHost;
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return !IsNonPublicAddress(literalAddress);
        }

        IPAddress[] resolved;
        using var timeoutCts = new CancellationTokenSource(DnsResolutionTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            resolved = await Dns.GetHostAddressesAsync(host, linkedCts.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout fired, not the caller's own token: fail closed rather than hang.
            return false;
        }

        return resolved.Length > 0 && resolved.All(address => !IsNonPublicAddress(address));
    }
}
