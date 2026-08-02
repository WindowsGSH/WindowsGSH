using System.Net.Http;

namespace WindowsGSH.Core.Network.Upnp;

// Every UPnP-related HTTP call in this namespace is inherently LAN-only (a discovered gateway's own
// description/control URLs, already validated by UpnpAddressPolicy) - sharing one client here keeps
// that guarantee in exactly one place instead of something each new call site has to remember to
// reconfigure identically.
internal static class UpnpHttpClient
{
    // UseProxy = false: this client only ever talks to an address UpnpAddressPolicy has already
    // verified is a private LAN unicast address. A system/configured HTTP proxy commonly has no
    // route to that address at all (breaking discovery/control for anyone with a proxy that
    // doesn't bypass RFC1918 destinations), and - more importantly - routing through a proxy means
    // the connection is no longer guaranteed to reach the exact address that was validated: the
    // proxy, not this process, makes the actual TCP connection, silently voiding that guarantee.
    private static readonly SocketsHttpHandler Handler = new()
    {
        AllowAutoRedirect = false,
        UseProxy = false
    };

    public static readonly HttpClient Shared = new(Handler)
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can assert this stays
    // disabled without reflecting into HttpClient's own private handler field.
    internal static bool UsesProxyForTesting => Handler.UseProxy;
}
