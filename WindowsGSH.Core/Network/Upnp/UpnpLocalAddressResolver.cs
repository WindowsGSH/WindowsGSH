using System.Net;
using System.Net.Sockets;

namespace WindowsGSH.Core.Network.Upnp;

// Resolves the InternalClient for a UPnP mapping from the route to the selected gateway. This must
// not use a generic "preferred local IP" helper: on multi-NIC/VPN hosts that can select an address
// belonging to a different network that the gateway cannot route back to.
internal static class UpnpLocalAddressResolver
{
    public static string? GetLocalIPv4(Uri gatewayControlUrl)
    {
        ArgumentNullException.ThrowIfNull(gatewayControlUrl);
        try
        {
            if (!IPAddress.TryParse(gatewayControlUrl.DnsSafeHost, out var gatewayAddress) ||
                !UpnpAddressPolicy.IsPrivateLanUnicastIPv4(gatewayAddress))
            {
                return null;
            }

            // UDP Connect performs route selection without sending a packet. Targeting the chosen
            // gateway—not a public address—ensures the InternalClient belongs to the interface
            // that can actually reach this router on multi-NIC/VPN hosts.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(gatewayAddress, gatewayControlUrl.IsDefaultPort ? 9 : gatewayControlUrl.Port);
            var ip = ((IPEndPoint)socket.LocalEndPoint!).Address;
            if (UpnpAddressPolicy.IsPrivateLanUnicastIPv4(ip))
            {
                return ip.ToString();
            }
        }
        catch
        {
        }

        return null;
    }
}
