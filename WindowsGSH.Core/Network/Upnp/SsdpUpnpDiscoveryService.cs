using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WindowsGSH.Core.Network.Upnp;

// Read-only SSDP discovery + IGD device-description reader. Deliberately does NOT invoke any SOAP action
// (no GetExternalIPAddress, no AddPortMapping/DeletePortMapping) and creates/changes/removes no
// port mapping - that's later, separate work with its own safety/ownership-tracking review.
public sealed class SsdpUpnpDiscoveryService : IUpnpDiscoveryService
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private const int MaxSsdpResponseBytes = 16 * 1024;
    private const int MaxSsdpResponses = 32;
    private const int MaxGatewayCandidates = 8;
    private const long MaxDescriptionContentBytes = 256 * 1024;
    private static readonly TimeSpan MaxSearchTimeout = TimeSpan.FromSeconds(10);

    // Both IGD generations are searched for in the same window - a router only ever answers the
    // ST(s) it actually implements, so asking for both costs nothing and widens compatibility.
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2"
    ];

    private readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<SsdpResponse>>> _collectSsdpResponses;
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetchDescriptionXml;

    public SsdpUpnpDiscoveryService()
        : this(DefaultCollectSsdpResponsesAsync, DefaultFetchDescriptionXmlAsync)
    {
    }

    internal SsdpUpnpDiscoveryService(
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<SsdpResponse>>> collectSsdpResponses,
        Func<Uri, CancellationToken, Task<string?>> fetchDescriptionXml)
    {
        _collectSsdpResponses = collectSsdpResponses;
        _fetchDescriptionXml = fetchDescriptionXml;
    }

    public async Task<IReadOnlyList<UpnpGatewayDescriptor>> DiscoverGatewaysAsync(
        TimeSpan searchTimeout,
        CancellationToken cancellationToken = default)
    {
        if (searchTimeout <= TimeSpan.Zero)
        {
            return [];
        }

        var boundedTimeout = searchTimeout > MaxSearchTimeout ? MaxSearchTimeout : searchTimeout;
        var rawResponses = await _collectSsdpResponses(boundedTimeout, cancellationToken).ConfigureAwait(false);
        var announcements = ParseUniqueGatewayAnnouncements(rawResponses);

        var tasks = announcements
            .Take(MaxGatewayCandidates)
            .Select(announcement => ReadGatewayDescriptionAsync(announcement, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(descriptor => descriptor != null).Cast<UpnpGatewayDescriptor>().ToArray();
    }

    private async Task<UpnpGatewayDescriptor?> ReadGatewayDescriptionAsync(
        SsdpGatewayAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Re-checked here as defense-in-depth, not trusted solely on ParseUniqueGatewayAnnouncements
        // having already enforced this before dedup - every SsdpGatewayAnnouncement that reaches
        // this method should already be safe, but this method must not assume a future caller can't
        // construct or route one differently. LOCATION is untrusted input supplied by a LAN peer:
        // require a literal RFC1918 unicast address matching the sender of this exact UDP response
        // before making any HTTP request, blocking SSDP-driven requests to localhost, link-local
        // metadata services, multicast, public hosts, and unrelated private machines.
        if (!IsSafeDescriptionLocation(announcement.Location, announcement.ResponderAddress))
        {
            return null;
        }

        try
        {
            var xml = await _fetchDescriptionXml(announcement.Location, cancellationToken).ConfigureAwait(false);
            return xml == null
                ? null
                : ParseGatewayDescription(announcement.Location, announcement.Usn, xml);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // DiscoverGatewaysAsync runs every candidate concurrently via Task.WhenAll - an
            // unexpected exception here (anything ParseGatewayDescription's own narrower
            // XmlException catch doesn't cover) would otherwise fault the whole batch and discard
            // every other, healthy candidate's result, not just this one.
            return null;
        }
    }

    private static async Task<IReadOnlyList<SsdpResponse>> DefaultCollectSsdpResponsesAsync(
        TimeSpan searchTimeout,
        CancellationToken cancellationToken)
    {
        if (searchTimeout <= TimeSpan.Zero)
        {
            return [];
        }

        var localAddresses = GetEligibleLocalIPv4Addresses();
        var tasks = localAddresses
            .Select(address => CollectOnInterfaceAsync(address, searchTimeout, cancellationToken))
            .ToArray();
        if (tasks.Length == 0)
        {
            return [];
        }

        var perInterfaceResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        return perInterfaceResults.SelectMany(response => response).Take(MaxSsdpResponses).ToArray();
    }

    private static async Task<IReadOnlyList<SsdpResponse>> CollectOnInterfaceAsync(
        IPAddress localAddress,
        TimeSpan searchTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CollectOnInterfaceCoreAsync(localAddress, searchTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Binding can fail before the socket reaches the send/receive blocks below when an
            // adapter disappears between enumeration and use. One stale interface must not abort
            // discovery on every other still-valid interface.
            return [];
        }
    }

    private static async Task<IReadOnlyList<SsdpResponse>> CollectOnInterfaceCoreAsync(
        IPAddress localAddress,
        TimeSpan searchTimeout,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient(new IPEndPoint(localAddress, 0));
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
        var mx = Math.Clamp((int)searchTimeout.TotalSeconds, 1, 5);

        try
        {
            client.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                localAddress.GetAddressBytes());
            foreach (var searchTarget in SearchTargets)
            {
                var requestBytes = Encoding.ASCII.GetBytes(BuildSearchRequest(searchTarget, mx));
                await client.SendAsync(requestBytes, multicastEndpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            return [];
        }

        var responses = new List<SsdpResponse>();
        using var timeoutCts = new CancellationTokenSource(searchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (responses.Count < MaxSsdpResponses)
            {
                var result = await client.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);
                if (result.Buffer.Length <= MaxSsdpResponseBytes)
                {
                    responses.Add(new SsdpResponse(
                        Encoding.ASCII.GetString(result.Buffer),
                        result.RemoteEndPoint.Address));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The search window elapsed - normal termination, not an error.
        }
        catch (SocketException)
        {
            // One interface becoming unavailable must not abort discovery on other interfaces.
        }

        return responses;
    }

    private static IReadOnlyList<IPAddress> GetEligibleLocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                    network.SupportsMulticast &&
                    network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and
                        not NetworkInterfaceType.Tunnel and
                        not NetworkInterfaceType.Ppp)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(UpnpAddressPolicy.IsPrivateLanUnicastIPv4)
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static string BuildSearchRequest(string searchTarget, int mx) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"MX: {mx}\r\n" +
        $"ST: {searchTarget}\r\n" +
        "\r\n";

    private static async Task<string?> DefaultFetchDescriptionXmlAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            using var response = await UpnpHttpClient.Shared
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDescriptionContentBytes)
            {
                return null;
            }

            await response.Content.LoadIntoBufferAsync(MaxDescriptionContentBytes, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Covers HttpRequestException, a client-side timeout, and any other transport failure -
            // one unreachable gateway description shouldn't abort discovery of the others.
            return null;
        }
    }

    internal readonly record struct SsdpResponse(string Payload, IPAddress RemoteAddress);

    internal readonly record struct SsdpGatewayAnnouncement(
        Uri Location,
        string? Usn,
        IPAddress ResponderAddress);

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise header
    // parsing/deduplication directly, without a real UDP socket.
    internal static IReadOnlyList<SsdpGatewayAnnouncement> ParseUniqueGatewayAnnouncements(
        IReadOnlyList<SsdpResponse> rawResponses)
    {
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var announcements = new List<SsdpGatewayAnnouncement>();
        foreach (var response in rawResponses.Take(MaxSsdpResponses))
        {
            var headers = ParseHeaders(response.Payload);
            if (!headers.TryGetValue("LOCATION", out var locationText) ||
                !Uri.TryCreate(locationText, UriKind.Absolute, out var location) ||
                (location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            // Validate the sender BEFORE it can claim the dedup key below. LOCATION is untrusted:
            // a LAN peer can reply with the real gateway's own LOCATION but its own, different
            // source address. If that spoofed response were allowed to claim the key first, the
            // genuine gateway's own later, matching response for that same LOCATION would be
            // silently discarded as a "duplicate" here - not merely rejected once fetched, but
            // never even reaching that check, suppressing discovery of a real gateway entirely.
            if (!IsSafeDescriptionLocation(location, response.RemoteAddress))
            {
                continue;
            }

            // A single device commonly answers the same M-SEARCH more than once, and/or answers
            // both search targets above with the same description document - one gateway, not two.
            if (!seenLocations.Add(location.AbsoluteUri))
            {
                continue;
            }

            headers.TryGetValue("USN", out var usn);
            announcements.Add(new SsdpGatewayAnnouncement(
                location,
                string.IsNullOrWhiteSpace(usn) ? null : usn,
                response.RemoteAddress));

            if (announcements.Count >= MaxGatewayCandidates)
            {
                break;
            }
        }

        return announcements;
    }

    private static Dictionary<string, string> ParseHeaders(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (name.Length > 0)
            {
                headers[name] = value;
            }
        }

        return headers;
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise description
    // parsing directly against sample XML, without a real HTTP fetch.
    internal static UpnpGatewayDescriptor? ParseGatewayDescription(Uri descriptionLocation, string? usn, string xml)
    {
        XDocument document;
        try
        {
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxDescriptionContentBytes,
                MaxCharactersFromEntities = 0
            });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        var rootDevice = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "device");
        var deviceType = GetChildValue(rootDevice, "deviceType");
        if (rootDevice == null || deviceType == null ||
            !deviceType.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase))
        {
            // Not an IGD (or not parseable as one) - not a "compatible IGD" for this tier's purposes,
            // regardless of what device type actually answered the search.
            return null;
        }

        var baseUri = descriptionLocation;
        var urlBaseText = GetChildValue(document.Root, "URLBase");
        if (!string.IsNullOrWhiteSpace(urlBaseText))
        {
            if (!Uri.TryCreate(urlBaseText, UriKind.Absolute, out var parsedBase) ||
                !UpnpAddressPolicy.IsSafeRelatedLanUri(descriptionLocation, parsedBase))
            {
                return null;
            }

            baseUri = parsedBase;
        }

        // Depth-first search through every nested <device> (WANDevice, WANConnectionDevice, ...)
        // for the first WAN connection service - IGD's own service hierarchy is arbitrarily nested,
        // never flat.
        foreach (var device in document.Descendants().Where(e => e.Name.LocalName == "device"))
        {
            var services = device.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList")
                ?.Elements().Where(e => e.Name.LocalName == "service") ?? [];

            foreach (var service in services)
            {
                var serviceType = GetChildValue(service, "serviceType");
                if (serviceType == null || !UpnpAddressPolicy.IsSupportedWanServiceType(serviceType))
                {
                    continue;
                }

                var controlUrlText = GetChildValue(service, "controlURL");
                if (string.IsNullOrWhiteSpace(controlUrlText) ||
                    !TryResolve(baseUri, controlUrlText, out var controlUrl) ||
                    !UpnpAddressPolicy.IsSafeRelatedLanUri(descriptionLocation, controlUrl!))
                {
                    continue;
                }

                Uri? eventSubUrl = null;
                var eventSubUrlText = GetChildValue(service, "eventSubURL");
                if (!string.IsNullOrWhiteSpace(eventSubUrlText))
                {
                    if (!TryResolve(baseUri, eventSubUrlText, out eventSubUrl) ||
                        !UpnpAddressPolicy.IsSafeRelatedLanUri(descriptionLocation, eventSubUrl!))
                    {
                        eventSubUrl = null;
                    }
                }

                return new UpnpGatewayDescriptor(
                    descriptionLocation,
                    GetChildValue(rootDevice, "friendlyName"),
                    GetChildValue(rootDevice, "manufacturer"),
                    GetChildValue(rootDevice, "modelName"),
                    deviceType,
                    serviceType,
                    controlUrl!,
                    eventSubUrl,
                    usn);
            }
        }

        // A genuine IGD with no WANIPConnection/WANPPPConnection service exposed has nothing this
        // tier's later mapping work could ever call - not "compatible" for our purposes either.
        return null;
    }

    private static string? GetChildValue(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    private static bool TryResolve(Uri baseUri, string relativeOrAbsolute, out Uri? result)
    {
        if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute))
        {
            result = absolute;
            return true;
        }

        if (Uri.TryCreate(baseUri, relativeOrAbsolute, out var combined))
        {
            result = combined;
            return true;
        }

        result = null;
        return false;
    }

    internal static bool IsSafeDescriptionLocation(Uri location, IPAddress responderAddress)
    {
        if (!UpnpAddressPolicy.IsSafeHttpUri(location) ||
            !IPAddress.TryParse(location.DnsSafeHost, out var locationAddress))
        {
            return false;
        }

        locationAddress = UpnpAddressPolicy.NormalizeAddress(locationAddress);
        responderAddress = UpnpAddressPolicy.NormalizeAddress(responderAddress);
        return UpnpAddressPolicy.IsPrivateLanUnicastIPv4(locationAddress) &&
            UpnpAddressPolicy.IsPrivateLanUnicastIPv4(responderAddress) &&
            locationAddress.Equals(responderAddress);
    }

}
