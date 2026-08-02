using WindowsGSH.Core.Network.Upnp;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class UpnpGatewayTests
{
    private const string ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1";

    private static UpnpGatewayDescriptor Descriptor(
        string controlUrl = "http://192.168.1.1:5000/upnp/control/WANIPConn1",
        string serviceType = ServiceType) =>
        new(
            new Uri("http://192.168.1.1:5000/rootDesc.xml"),
            "Home Router",
            "Acme",
            "Router 9000",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            serviceType,
            new Uri(controlUrl),
            null,
            "uuid:abc-123");

    private static string SuccessEnvelope(string actionName, params (string Name, string Value)[] arguments) =>
        $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:{actionName}Response xmlns:u="{ServiceType}">
              {string.Concat(arguments.Select(a => $"<{a.Name}>{a.Value}</{a.Name}>"))}
            </u:{actionName}Response>
          </s:Body>
        </s:Envelope>
        """;

    private const string FaultEnvelope = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>713</errorCode>
                  <errorDescription>SpecifiedArrayIndexInvalid</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    // --- Constructor safety ---

    [Fact]
    public void Constructor_accepts_a_descriptor_with_a_safe_control_url()
    {
        var gateway = new UpnpGateway(Descriptor());

        Assert.NotNull(gateway);
    }

    [Theory]
    [InlineData("http://203.0.113.10/control")]
    [InlineData("http://127.0.0.1/control")]
    [InlineData("file:///C:/control")]
    public void Constructor_rejects_a_descriptor_whose_control_url_is_not_a_safe_lan_address(string controlUrl)
    {
        Assert.Throws<ArgumentException>(() => new UpnpGateway(Descriptor(controlUrl)));
    }

    [Fact]
    public void Constructor_rejects_a_control_url_on_a_different_private_host()
    {
        Assert.Throws<ArgumentException>(() =>
            new UpnpGateway(Descriptor("http://192.168.1.2/control")));
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:Layer3Forwarding:1")]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:0")]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:not-a-version")]
    public void Constructor_rejects_an_unsupported_service_type(string serviceType)
    {
        Assert.Throws<ArgumentException>(() =>
            new UpnpGateway(Descriptor(serviceType: serviceType)));
    }

    // --- BuildSoapRequestBody ---

    [Fact]
    public void BuildSoapRequestBody_includes_the_action_and_arguments()
    {
        var body = UpnpGateway.BuildSoapRequestBody(
            ServiceType, "AddPortMapping", [("NewExternalPort", "7777"), ("NewProtocol", "UDP")]);

        Assert.Contains("AddPortMapping", body, StringComparison.Ordinal);
        Assert.Contains(ServiceType, body, StringComparison.Ordinal);
        Assert.Contains("<NewExternalPort>7777</NewExternalPort>", body, StringComparison.Ordinal);
        Assert.Contains("<NewProtocol>UDP</NewProtocol>", body, StringComparison.Ordinal);
        // The action element must be namespace-qualified (conventionally "u:") while its argument
        // elements must NOT be - UPnP only qualifies the action itself. A gateway that validates
        // this strictly will reject implemented actions as having invalid/missing arguments if the
        // arguments are namespace-qualified too.
        Assert.Contains($"<u:AddPortMapping xmlns:u=\"{ServiceType}\">", body, StringComparison.Ordinal);
        // No element anywhere should declare a *default* namespace (xmlns="...") - only the
        // prefixed xmlns:s=/xmlns:u= declarations above are expected.
        Assert.DoesNotContain("xmlns=\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSoapRequestBody_supports_an_action_with_no_arguments()
    {
        var body = UpnpGateway.BuildSoapRequestBody(ServiceType, "GetExternalIPAddress", []);

        Assert.Contains("GetExternalIPAddress", body, StringComparison.Ordinal);
    }

    // --- ParseSoapResponse ---

    [Fact]
    public void ParseSoapResponse_extracts_success_arguments()
    {
        var response = UpnpGateway.ParseSoapResponse(
            SuccessEnvelope("GetExternalIPAddress", ("NewExternalIPAddress", "203.0.113.5")));

        Assert.Equal(UpnpSoapResponseKind.Success, response.Kind);
        Assert.Equal("203.0.113.5", response.Arguments!["NewExternalIPAddress"]);
    }

    [Fact]
    public void ParseSoapResponse_extracts_fault_code_and_description()
    {
        var response = UpnpGateway.ParseSoapResponse(FaultEnvelope);

        Assert.Equal(UpnpSoapResponseKind.Fault, response.Kind);
        Assert.Equal(713, response.FaultCode);
        Assert.Equal("SpecifiedArrayIndexInvalid", response.Message);
    }

    [Fact]
    public void ParseSoapResponse_falls_back_to_faultstring_when_no_upnp_error_detail_is_present()
    {
        const string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <s:Fault>
                  <faultcode>s:Client</faultcode>
                  <faultstring>Invalid Action</faultstring>
                </s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        var response = UpnpGateway.ParseSoapResponse(xml);

        Assert.Equal(UpnpSoapResponseKind.Fault, response.Kind);
        Assert.Equal("Invalid Action", response.Message);
    }

    [Fact]
    public void ParseSoapResponse_returns_transport_failure_for_malformed_xml_instead_of_throwing()
    {
        var response = UpnpGateway.ParseSoapResponse("<not-well-formed");

        Assert.Equal(UpnpSoapResponseKind.TransportFailure, response.Kind);
    }

    [Fact]
    public void ParseSoapResponse_rejects_a_dtd()
    {
        var xml = SuccessEnvelope("GetExternalIPAddress", ("NewExternalIPAddress", "203.0.113.5"))
            .Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\"?><!DOCTYPE s:Envelope [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>");

        var response = UpnpGateway.ParseSoapResponse(xml);

        Assert.Equal(UpnpSoapResponseKind.TransportFailure, response.Kind);
    }

    [Fact]
    public void ParseSoapResponse_returns_transport_failure_when_body_is_missing()
    {
        const string xml = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"></s:Envelope>""";

        var response = UpnpGateway.ParseSoapResponse(xml);

        Assert.Equal(UpnpSoapResponseKind.TransportFailure, response.Kind);
    }

    // --- GetExternalIpAddressAsync ---

    [Fact]
    public async Task GetExternalIpAddressAsync_returns_success_for_a_valid_ip()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(
                new Dictionary<string, string> { ["NewExternalIPAddress"] = "8.8.8.8" })));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Success, result.Outcome);
        Assert.Equal("8.8.8.8", result.ExternalIpAddress);
        Assert.Equal(UpnpExternalAddressKind.Public, result.AddressKind);
    }

    [Theory]
    [InlineData("192.168.10.2", UpnpExternalAddressKind.Private)]
    [InlineData("10.20.30.40", UpnpExternalAddressKind.Private)]
    [InlineData("100.64.0.1", UpnpExternalAddressKind.CarrierGradeNat)]
    [InlineData("100.127.255.254", UpnpExternalAddressKind.CarrierGradeNat)]
    public async Task GetExternalIpAddressAsync_classifies_non_public_addresses(
        string address,
        UpnpExternalAddressKind expectedKind)
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(
                new Dictionary<string, string> { ["NewExternalIPAddress"] = address })));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Success, result.Outcome);
        Assert.Equal(expectedKind, result.AddressKind);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.2")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("2001:db8::1")]
    [InlineData("100::1")]
    [InlineData("100::ffff:ffff:ffff:ffff")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:20::1")]
    [InlineData("2002::1")]
    [InlineData("3fff::1")]
    [InlineData("5f00::1")]
    [InlineData("2606:4700:4700::1111")]
    public async Task GetExternalIpAddressAsync_rejects_non_unicast_or_unusable_addresses(string address)
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(
                new Dictionary<string, string> { ["NewExternalIPAddress"] = address })));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Unavailable, result.Outcome);
        Assert.Null(result.AddressKind);
    }

    [Fact]
    public async Task GetExternalIpAddressAsync_calls_the_correct_action_with_no_arguments()
    {
        string? observedAction = null;
        IReadOnlyList<(string Name, string Value)>? observedArguments = null;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, action, arguments, _) =>
            {
                observedAction = action;
                observedArguments = arguments;
                return Task.FromResult(UpnpSoapResponse.SuccessResponse(
                    new Dictionary<string, string> { ["NewExternalIPAddress"] = "203.0.113.5" }));
            });

        await gateway.GetExternalIpAddressAsync();

        Assert.Equal("GetExternalIPAddress", observedAction);
        Assert.Empty(observedArguments!);
    }

    [Fact]
    public async Task GetExternalIpAddressAsync_returns_unavailable_when_the_response_has_no_valid_ip()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(
                new Dictionary<string, string> { ["NewExternalIPAddress"] = "not-an-ip" })));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task GetExternalIpAddressAsync_returns_fault_outcome_on_a_soap_fault()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.FaultResponse(401, "Invalid Action")));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Fault, result.Outcome);
        Assert.Contains("Invalid Action", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExternalIpAddressAsync_returns_unavailable_when_the_delegate_throws_unexpectedly()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => throw new InvalidOperationException("boom"));

        var result = await gateway.GetExternalIpAddressAsync();

        Assert.Equal(UpnpSoapOutcome.Unavailable, result.Outcome);
    }

    // --- GetExistingPortMappingsAsync ---

    private static UpnpSoapResponse MappingResponse(int externalPort) => UpnpSoapResponse.SuccessResponse(
        new Dictionary<string, string>
        {
            ["NewRemoteHost"] = "",
            ["NewExternalPort"] = externalPort.ToString(),
            ["NewProtocol"] = "TCP",
            ["NewInternalPort"] = externalPort.ToString(),
            ["NewInternalClient"] = "192.168.1.50",
            ["NewEnabled"] = "1",
            ["NewPortMappingDescription"] = "Test mapping",
            ["NewLeaseDuration"] = "0"
        });

    [Fact]
    public async Task GetExistingPortMappingsAsync_enumerates_until_a_fault_and_parses_each_entry()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, arguments, _) =>
            {
                var index = int.Parse(arguments.Single(a => a.Name == "NewPortMappingIndex").Value);
                return Task.FromResult(index < 2 ? MappingResponse(7000 + index) : UpnpSoapResponse.FaultResponse(713, "SpecifiedArrayIndexInvalid"));
            });

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Success, result.Outcome);
        Assert.Equal(2, result.Mappings.Count);
        Assert.Equal(7000, result.Mappings[0].ExternalPort);
        Assert.Equal(7001, result.Mappings[1].ExternalPort);
        Assert.True(result.Mappings[0].Enabled);
        Assert.Null(result.Mappings[0].RemoteHost);
        Assert.Equal("Test mapping", result.Mappings[0].Description);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_stops_on_a_malformed_success_entry_without_throwing()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, arguments, _) =>
            {
                var index = int.Parse(arguments.Single(a => a.Name == "NewPortMappingIndex").Value);
                return Task.FromResult(index == 0
                    ? MappingResponse(7000)
                    : UpnpSoapResponse.SuccessResponse(new Dictionary<string, string>()));
            });

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Single(result.Mappings);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_returns_incomplete_and_preserves_partial_results_on_transport_failure()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, arguments, _) =>
            {
                var index = int.Parse(arguments.Single(a => a.Name == "NewPortMappingIndex").Value);
                return Task.FromResult(index == 0
                    ? MappingResponse(7000)
                    : UpnpSoapResponse.TransportFailure("timed out"));
            });

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Single(result.Mappings);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_returns_unavailable_when_the_very_first_request_fails()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.TransportFailure("timed out")));

        var result = await gateway.GetExistingPortMappingsAsync();

        // Nothing was read, so an unreachable gateway remains distinguishable from a malformed
        // response or a genuinely partial enumeration.
        Assert.Equal(UpnpSoapOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_stops_at_the_enumeration_cap_instead_of_looping_forever()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, arguments, _) =>
            {
                var index = int.Parse(arguments.Single(a => a.Name == "NewPortMappingIndex").Value);
                return Task.FromResult(MappingResponse(1024 + (index % 1000)));
            });

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Equal(512, result.Mappings.Count);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_preserves_already_gathered_entries_when_a_later_fault_is_not_713()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, arguments, _) =>
            {
                var index = int.Parse(arguments.Single(a => a.Name == "NewPortMappingIndex").Value);
                return Task.FromResult(index < 2 ? MappingResponse(7000 + index) : UpnpSoapResponse.FaultResponse(401, "Invalid Action"));
            });

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Equal(2, result.Mappings.Count);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_treats_non_713_fault_as_a_failure()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(
                UpnpSoapResponse.FaultResponse(401, "Invalid Action")));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Fault, result.Outcome);
        Assert.Contains("401", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_has_an_overall_deadline()
    {
        var gateway = new UpnpGateway(
            Descriptor(),
            async (_, _, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MappingResponse(7000);
            },
            TimeSpan.FromMilliseconds(25));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Contains("deadline", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_normalizes_fields_and_preserves_unknown_lease()
    {
        var response = UpnpSoapResponse.SuccessResponse(
            new Dictionary<string, string>
            {
                ["NewExternalPort"] = "7777",
                ["NewProtocol"] = " udp ",
                ["NewInternalPort"] = "7778",
                ["NewInternalClient"] = "192.168.1.50",
                ["NewEnabled"] = "TRUE",
                ["NewLeaseDuration"] = "unknown"
            });
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(
                calls++ == 0 ? response : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal("UDP", mapping.Protocol);
        Assert.True(mapping.Enabled);
        Assert.Null(mapping.LeaseDurationSeconds);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_normalizes_an_ipv4_mapped_internal_client()
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewInternalClient"] = "::ffff:192.168.1.50";
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(
                calls++ == 0
                    ? UpnpSoapResponse.SuccessResponse(arguments)
                    : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal("192.168.1.50", mapping.InternalClient);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_preserves_a_lease_duration_beyond_int_maxvalue()
    {
        var response = UpnpSoapResponse.SuccessResponse(
            new Dictionary<string, string>
            {
                ["NewExternalPort"] = "7777",
                ["NewProtocol"] = "TCP",
                ["NewInternalPort"] = "7777",
                ["NewInternalClient"] = "192.168.1.50",
                ["NewEnabled"] = "1",
                // A valid UPnP ui4 (unsigned 32-bit) value above int.MaxValue (2147483647).
                ["NewLeaseDuration"] = "4294967295"
            });
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(
                calls++ == 0 ? response : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal(4294967295L, mapping.LeaseDurationSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("2")]
    public async Task GetExistingPortMappingsAsync_marks_entry_incomplete_when_enabled_is_missing_or_unparseable(string? enabledValue)
    {
        var arguments = new Dictionary<string, string>
        {
            ["NewExternalPort"] = "7777",
            ["NewProtocol"] = "TCP",
            ["NewInternalPort"] = "7777",
            ["NewInternalClient"] = "192.168.1.50"
        };
        if (enabledValue != null)
        {
            arguments["NewEnabled"] = enabledValue;
        }

        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(arguments)));

        var result = await gateway.GetExistingPortMappingsAsync();

        // A missing/unparseable NewEnabled must not be silently reported as "disabled" - the
        // entry itself is untrustworthy, the same as a bad port/protocol/client.
        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_marks_entry_incomplete_for_an_unsupported_protocol()
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewProtocol"] = "SCTP";
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(arguments)));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Mappings);
    }

    // NewInternalClient's UPnP type permits either an IP address or a DNS host name - a hostname
    // (or any other non-blank value a real gateway reports, since this is only ever displayed, not
    // resolved or contacted) must be preserved, not treated as a malformed entry.
    [Theory]
    [InlineData("desktop-pc.lan")]
    [InlineData("203.0.113.50")]
    [InlineData("127.0.0.1")]
    public async Task GetExistingPortMappingsAsync_preserves_a_non_private_ip_or_hostname_internal_client(string internalClient)
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewInternalClient"] = internalClient;
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(calls++ == 0
                ? UpnpSoapResponse.SuccessResponse(arguments)
                : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal(internalClient, mapping.InternalClient);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("bad_host.lan")]
    [InlineData("-bad.lan")]
    [InlineData("bad-.lan")]
    [InlineData("bad..lan")]
    [InlineData("127.1")]
    [InlineData("192.168.001.50")]
    [InlineData("2001:4860:4860::8888")]
    public async Task GetExistingPortMappingsAsync_rejects_an_invalid_internal_client(string internalClient)
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewInternalClient"] = internalClient;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(arguments)));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Mappings);
    }

    [Theory]
    [InlineData("UDP", UpnpSoapOutcome.Success)]
    [InlineData("TCP", UpnpSoapOutcome.Incomplete)]
    public async Task GetExistingPortMappingsAsync_accepts_broadcast_internal_client_only_for_udp(
        string protocol,
        UpnpSoapOutcome expectedOutcome)
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewProtocol"] = protocol;
        arguments["NewInternalClient"] = "255.255.255.255";
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(calls++ == 0
                ? UpnpSoapResponse.SuccessResponse(arguments)
                : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(expectedOutcome, result.Outcome);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_marks_entry_incomplete_when_internal_client_is_blank()
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewInternalClient"] = "   ";
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(arguments)));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_preserves_a_wildcard_external_port()
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewExternalPort"] = "0";
        var calls = 0;
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(calls++ == 0
                ? UpnpSoapResponse.SuccessResponse(arguments)
                : UpnpSoapResponse.FaultResponse(713, "end")));

        var result = await gateway.GetExistingPortMappingsAsync();

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal(0, mapping.ExternalPort);
    }

    [Fact]
    public async Task GetExistingPortMappingsAsync_marks_entry_incomplete_for_a_negative_external_port()
    {
        var response = MappingResponse(7777);
        var arguments = response.Arguments!.ToDictionary(pair => pair.Key, pair => pair.Value);
        arguments["NewExternalPort"] = "-1";
        var gateway = new UpnpGateway(
            Descriptor(),
            (_, _, _, _, _) => Task.FromResult(UpnpSoapResponse.SuccessResponse(arguments)));

        var result = await gateway.GetExistingPortMappingsAsync();

        Assert.Equal(UpnpSoapOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Mappings);
    }
}
