using System.Net;
using WindowsGSH.Core.Network.Upnp;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SsdpUpnpDiscoveryServiceTests
{
    private const string TypicalIgdXml = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <friendlyName>Home Router</friendlyName>
            <manufacturer>Acme</manufacturer>
            <modelName>Router 9000</modelName>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <friendlyName>WAN Device</friendlyName>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>
                    <friendlyName>WAN Connection Device</friendlyName>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>
                        <controlURL>/upnp/control/WANIPConn1</controlURL>
                        <eventSubURL>/upnp/event/WANIPConn1</eventSubURL>
                        <SCPDURL>/WANIPCn.xml</SCPDURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    private static readonly Uri Location = new("http://192.168.1.1:5000/rootDesc.xml");
    private static SsdpUpnpDiscoveryService.SsdpResponse Response(
        string payload,
        string responderAddress = "192.168.1.1") =>
        new(payload, IPAddress.Parse(responderAddress));

    // --- ParseUniqueGatewayAnnouncements ---

    [Fact]
    public void ParseUniqueGatewayAnnouncements_extracts_location_and_usn()
    {
        var raw = "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            "LOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
            "USN: uuid:abc-123::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([Response(raw)]);

        var announcement = Assert.Single(announcements);
        Assert.Equal(Location, announcement.Location);
        Assert.Equal("uuid:abc-123::urn:schemas-upnp-org:device:InternetGatewayDevice:1", announcement.Usn);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_deduplicates_same_location_case_insensitively()
    {
        var first = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";
        var second = "HTTP/1.1 200 OK\r\nLOCATION: HTTP://192.168.1.1:5000/ROOTDESC.XML\r\n\r\n";

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([Response(first), Response(second)]);

        Assert.Single(announcements);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_skips_a_response_with_no_location_header()
    {
        var raw = "HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n";

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([Response(raw)]);

        Assert.Empty(announcements);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_skips_a_non_http_location()
    {
        var raw = "HTTP/1.1 200 OK\r\nLOCATION: not-a-valid-uri\r\n\r\n";

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([Response(raw)]);

        Assert.Empty(announcements);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_usn_is_null_when_header_is_absent()
    {
        var raw = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";

        var announcement = Assert.Single(SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([Response(raw)]));

        Assert.Null(announcement.Usn);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_caps_unique_candidates()
    {
        var responses = Enumerable.Range(1, 20)
            .Select(index => Response(
                $"HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.{index}:5000/root.xml\r\n\r\n",
                $"192.168.1.{index}"))
            .ToArray();

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements(responses);

        Assert.Equal(8, announcements.Count);
    }

    [Fact]
    public void ParseUniqueGatewayAnnouncements_does_not_let_a_spoofed_response_suppress_a_later_genuine_one()
    {
        const string location = "http://192.168.1.1:5000/rootDesc.xml";
        // A spoofed reply claiming the real gateway's own LOCATION but sent from a different,
        // unrelated address - arrives first.
        var spoofed = Response($"HTTP/1.1 200 OK\r\nLOCATION: {location}\r\n\r\n", "192.168.1.50");
        // The real gateway's own, genuine reply for that same LOCATION - arrives second.
        var genuine = Response($"HTTP/1.1 200 OK\r\nLOCATION: {location}\r\n\r\n", "192.168.1.1");

        var announcements = SsdpUpnpDiscoveryService.ParseUniqueGatewayAnnouncements([spoofed, genuine]);

        var announcement = Assert.Single(announcements);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), announcement.ResponderAddress);
    }

    [Fact]
    public void Safe_description_location_requires_the_location_to_match_its_private_lan_responder()
    {
        Assert.True(SsdpUpnpDiscoveryService.IsSafeDescriptionLocation(
            new Uri("http://192.168.1.1/root.xml"),
            IPAddress.Parse("192.168.1.1")));
        Assert.False(SsdpUpnpDiscoveryService.IsSafeDescriptionLocation(
            new Uri("http://192.168.1.2/root.xml"),
            IPAddress.Parse("192.168.1.1")));
    }

    [Theory]
    [InlineData("http://127.0.0.1/root.xml", "127.0.0.1")]
    [InlineData("http://0.0.0.0/root.xml", "0.0.0.0")]
    [InlineData("http://169.254.169.254/root.xml", "169.254.169.254")]
    [InlineData("http://239.255.255.250/root.xml", "239.255.255.250")]
    [InlineData("http://203.0.113.10/root.xml", "203.0.113.10")]
    [InlineData("file:///C:/root.xml", "192.168.1.1")]
    public void Unsafe_description_locations_are_rejected(string location, string responder)
    {
        Assert.False(SsdpUpnpDiscoveryService.IsSafeDescriptionLocation(
            new Uri(location),
            IPAddress.Parse(responder)));
    }

    // --- ParseGatewayDescription ---

    [Fact]
    public void ParseGatewayDescription_extracts_metadata_and_resolves_control_url_against_location()
    {
        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, "uuid:abc-123", TypicalIgdXml);

        Assert.NotNull(descriptor);
        Assert.Equal("Home Router", descriptor!.FriendlyName);
        Assert.Equal("Acme", descriptor.Manufacturer);
        Assert.Equal("Router 9000", descriptor.ModelName);
        Assert.Equal("urn:schemas-upnp-org:device:InternetGatewayDevice:1", descriptor.DeviceType);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", descriptor.ServiceType);
        Assert.Equal(new Uri("http://192.168.1.1:5000/upnp/control/WANIPConn1"), descriptor.ControlUrl);
        Assert.Equal(new Uri("http://192.168.1.1:5000/upnp/event/WANIPConn1"), descriptor.EventSubUrl);
        Assert.Equal("uuid:abc-123", descriptor.Usn);
    }

    [Fact]
    public void ParseGatewayDescription_resolves_control_url_against_urlbase_when_present()
    {
        var xml = TypicalIgdXml.Replace(
            "<specVersion><major>1</major><minor>0</minor></specVersion>",
            "<specVersion><major>1</major><minor>0</minor></specVersion><URLBase>http://192.168.1.1:49152/</URLBase>");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.NotNull(descriptor);
        Assert.Equal(new Uri("http://192.168.1.1:49152/upnp/control/WANIPConn1"), descriptor!.ControlUrl);
    }

    [Fact]
    public void ParseGatewayDescription_recognizes_wanpppconnection_service()
    {
        var xml = TypicalIgdXml.Replace(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.NotNull(descriptor);
        Assert.Equal("urn:schemas-upnp-org:service:WANPPPConnection:1", descriptor!.ServiceType);
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:")]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:evil")]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:0")]
    public void ParseGatewayDescription_rejects_a_malformed_wan_service_version(string serviceType)
    {
        var xml = TypicalIgdXml.Replace(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            serviceType);

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Fact]
    public void ParseGatewayDescription_returns_null_for_a_non_igd_device()
    {
        var xml = TypicalIgdXml.Replace(
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:MediaServer:1");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Fact]
    public void ParseGatewayDescription_returns_null_when_igd_has_no_wan_connection_service()
    {
        const string xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
                <friendlyName>Bare Gateway</friendlyName>
              </device>
            </root>
            """;

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Fact]
    public void ParseGatewayDescription_returns_null_for_malformed_xml_instead_of_throwing()
    {
        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, "<not-well-formed");

        Assert.Null(descriptor);
    }

    [Fact]
    public void ParseGatewayDescription_rejects_a_dtd()
    {
        var xml = TypicalIgdXml.Replace(
            "<?xml version=\"1.0\"?>",
            "<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Theory]
    [InlineData("http://127.0.0.1:49152/")]
    [InlineData("http://203.0.113.10:49152/")]
    [InlineData("file:///C:/")]
    public void ParseGatewayDescription_rejects_an_unsafe_urlbase(string urlBase)
    {
        var xml = TypicalIgdXml.Replace(
            "<specVersion><major>1</major><minor>0</minor></specVersion>",
            $"<specVersion><major>1</major><minor>0</minor></specVersion><URLBase>{urlBase}</URLBase>");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Theory]
    [InlineData("http://127.0.0.1/control")]
    [InlineData("http://192.168.1.2/control")]
    [InlineData("https://public.example/control")]
    [InlineData("file:///C:/control")]
    public void ParseGatewayDescription_rejects_an_unsafe_control_url(string controlUrl)
    {
        var xml = TypicalIgdXml.Replace("/upnp/control/WANIPConn1", controlUrl);

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    [Fact]
    public void ParseGatewayDescription_drops_an_unsafe_event_url_but_keeps_the_safe_control_url()
    {
        var xml = TypicalIgdXml.Replace(
            "/upnp/event/WANIPConn1",
            "http://127.0.0.1/event");

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.EventSubUrl);
    }

    [Fact]
    public void ParseGatewayDescription_requires_the_igd_to_be_the_root_device()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
                    <serviceList>
                      <service>
                        <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                        <controlURL>/control</controlURL>
                      </service>
                    </serviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;

        var descriptor = SsdpUpnpDiscoveryService.ParseGatewayDescription(Location, null, xml);

        Assert.Null(descriptor);
    }

    // --- DiscoverGatewaysAsync (end-to-end against injected fakes; no real network) ---

    [Fact]
    public async Task DiscoverGatewaysAsync_returns_a_descriptor_for_a_valid_gateway()
    {
        var raw = "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";
        var service = new SsdpUpnpDiscoveryService(
            (_, _) => Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>([Response(raw)]),
            (_, _) => Task.FromResult<string?>(TypicalIgdXml));

        var gateways = await service.DiscoverGatewaysAsync(TimeSpan.FromSeconds(1));

        var gateway = Assert.Single(gateways);
        Assert.Equal("Home Router", gateway.FriendlyName);
    }

    [Fact]
    public async Task DiscoverGatewaysAsync_never_fetches_a_location_pointing_at_a_public_address()
    {
        var raw = "HTTP/1.1 200 OK\r\nLOCATION: http://203.0.113.10:5000/rootDesc.xml\r\n\r\n";
        var fetchCount = 0;
        var service = new SsdpUpnpDiscoveryService(
            (_, _) => Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>(
                [Response(raw, "203.0.113.10")]),
            (_, _) => { fetchCount++; return Task.FromResult<string?>(TypicalIgdXml); });

        var gateways = await service.DiscoverGatewaysAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(gateways);
        Assert.Equal(0, fetchCount);
    }

    [Fact]
    public async Task DiscoverGatewaysAsync_skips_a_gateway_whose_description_could_not_be_fetched_but_keeps_others()
    {
        var responses = new[]
        {
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n",
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.2:5000/rootDesc.xml\r\n\r\n"
        };
        var service = new SsdpUpnpDiscoveryService(
            (_, _) => Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>(
                responses.Select(response => Response(
                    response,
                    response.Contains("192.168.1.1", StringComparison.Ordinal)
                        ? "192.168.1.1"
                        : "192.168.1.2")).ToArray()),
            (location, _) => Task.FromResult(location.Host == "192.168.1.1" ? null : TypicalIgdXml));

        var gateways = await service.DiscoverGatewaysAsync(TimeSpan.FromSeconds(1));

        var gateway = Assert.Single(gateways);
        Assert.Equal("192.168.1.2", gateway.DescriptionLocation.Host);
    }

    [Fact]
    public async Task DiscoverGatewaysAsync_returns_empty_when_nothing_responds()
    {
        var service = new SsdpUpnpDiscoveryService(
            (_, _) => Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>([]),
            (_, _) => throw new InvalidOperationException("Should not be called when there are no locations."));

        var gateways = await service.DiscoverGatewaysAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(gateways);
    }

    [Fact]
    public async Task DiscoverGatewaysAsync_keeps_other_gateways_when_one_candidates_fetch_throws_unexpectedly()
    {
        var responses = new[]
        {
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n",
            "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.2:5000/rootDesc.xml\r\n\r\n"
        };
        var service = new SsdpUpnpDiscoveryService(
            (_, _) => Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>(
                responses.Select(response => Response(
                    response,
                    response.Contains("192.168.1.1", StringComparison.Ordinal)
                        ? "192.168.1.1"
                        : "192.168.1.2")).ToArray()),
            (location, _) => location.Host == "192.168.1.1"
                ? throw new InvalidOperationException("Simulated unexpected failure for one candidate.")
                : Task.FromResult<string?>(TypicalIgdXml));

        var gateways = await service.DiscoverGatewaysAsync(TimeSpan.FromSeconds(1));

        var gateway = Assert.Single(gateways);
        Assert.Equal("192.168.1.2", gateway.DescriptionLocation.Host);
    }

    [Fact]
    public async Task DiscoverGatewaysAsync_clamps_an_excessive_search_timeout()
    {
        TimeSpan? observedTimeout = null;
        var service = new SsdpUpnpDiscoveryService(
            (timeout, _) =>
            {
                observedTimeout = timeout;
                return Task.FromResult<IReadOnlyList<SsdpUpnpDiscoveryService.SsdpResponse>>([]);
            },
            (_, _) => throw new InvalidOperationException("No description should be fetched."));

        await service.DiscoverGatewaysAsync(TimeSpan.FromDays(30));

        Assert.Equal(TimeSpan.FromSeconds(10), observedTimeout);
    }
}
