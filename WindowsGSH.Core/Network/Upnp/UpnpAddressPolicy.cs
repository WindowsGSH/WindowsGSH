using System.Net;
using System.Net.Sockets;

namespace WindowsGSH.Core.Network.Upnp;

// Shared "is this really a private LAN address we can safely talk to unsupervised" policy for
// every UPnP network call - discovery (SsdpUpnpDiscoveryService) and gateway control (UpnpGateway)
// alike. Kept in one place so a future call site can't independently reinvent (and potentially get
// wrong) the same address classification this tier's own review rounds have already hardened.
internal static class UpnpAddressPolicy
{
    private static readonly string[] WanServiceTypePrefixes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:",
        "urn:schemas-upnp-org:service:WANPPPConnection:"
    ];

    public static bool IsSafeHttpUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    // Deliberately narrower than PublicIpEndpointPolicy.IsNonPublicAddress (which also allows
    // CGNAT and IPv6 site/unique-local, since it exists to reject *private* addresses for a
    // user-editable *public*-IP endpoint). A LAN gateway's own address should always be a plain
    // RFC1918 IPv4 unicast address - loopback, 0.0.0.0/broadcast, link-local (including the
    // 169.254.169.254 cloud-metadata address), CGNAT, and multicast are all excluded even though
    // some of those would otherwise be "non-public."
    public static bool IsPrivateLanUnicastIPv4(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    public static bool IsSafeLanUri(Uri uri) =>
        IsSafeHttpUri(uri) &&
        IPAddress.TryParse(uri.DnsSafeHost, out var address) &&
        IsPrivateLanUnicastIPv4(address);

    public static bool IsSafeRelatedLanUri(Uri descriptionLocation, Uri candidate)
    {
        if (!IsSafeLanUri(descriptionLocation) ||
            !IsSafeLanUri(candidate) ||
            !IPAddress.TryParse(descriptionLocation.DnsSafeHost, out var descriptionAddress) ||
            !IPAddress.TryParse(candidate.DnsSafeHost, out var candidateAddress))
        {
            return false;
        }

        return NormalizeAddress(descriptionAddress).Equals(NormalizeAddress(candidateAddress));
    }

    public static bool IsSupportedWanServiceType(string serviceType)
    {
        foreach (var prefix in WanServiceTypePrefixes)
        {
            if (serviceType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(serviceType[prefix.Length..], out var version) &&
                version > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static UpnpExternalAddressKind? ClassifyExternalAddress(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) ||
            address.IsIPv6Multicast ||
            address.IsIPv6LinkLocal)
        {
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8 ("this network," RFC 791/1122) is non-routable in its entirety, not just
            // the exact 0.0.0.0 value already rejected above (IPAddress.Any) - a gateway reporting
            // e.g. 0.1.2.3 as its "external IP" (typically a not-yet-configured WAN interface) is
            // not a reachable public address either.
            if (bytes[0] == 0 || bytes[0] >= 224 || (bytes[0] == 169 && bytes[1] == 254))
            {
                return null;
            }

            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            {
                return UpnpExternalAddressKind.CarrierGradeNat;
            }

            if (IsPrivateLanUnicastIPv4(address))
            {
                return UpnpExternalAddressKind.Private;
            }

            // Do not describe special-purpose addresses as public/internet-routable. These cover
            // IETF protocol assignments, documentation networks, and benchmark testing networks.
            if ((bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113))
            {
                return null;
            }

            return UpnpExternalAddressKind.Public;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // WANIPConnection's ExternalIPAddress is defined as dotted-decimal IPv4 (or empty).
            // Reject native IPv6 instead of maintaining an incomplete special-purpose prefix table
            // for a value that a conforming IGD must not return here.
            return null;
        }

        return null;
    }
}
