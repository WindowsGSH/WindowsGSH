using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WindowsGSH.Core.Network.Upnp;

// SOAP client for a single, already-discovered gateway's ControlUrl. Ownership policy does not
// belong here; UpnpPortMappingService is the only layer that authorizes mutations.
public sealed class UpnpGateway : IUpnpGateway
{
    private const int MaxResponseContentBytes = 64 * 1024;
    private static readonly TimeSpan MappingEnumerationTimeout = TimeSpan.FromSeconds(30);

    // GetGenericPortMappingEntry has no built-in end marker beyond "the gateway returned a fault" -
    // bounds enumeration against a misbehaving or malicious device that never signals end-of-list.
    private const int MaxEnumeratedMappings = 512;

    private static readonly XNamespace SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace SoapEncodingNs = "http://schemas.xmlsoap.org/soap/encoding/";

    private readonly Uri _descriptionLocation;
    private readonly Uri _controlUrl;
    private readonly string _serviceType;
    private readonly TimeSpan _mappingEnumerationTimeout;
    private readonly Func<Uri, string, string, IReadOnlyList<(string Name, string Value)>, CancellationToken, Task<UpnpSoapResponse>> _invokeSoapAction;

    public UpnpGateway(UpnpGatewayDescriptor descriptor)
        : this(descriptor, DefaultInvokeSoapActionAsync)
    {
    }

    internal UpnpGateway(
        UpnpGatewayDescriptor descriptor,
        Func<Uri, string, string, IReadOnlyList<(string Name, string Value)>, CancellationToken, Task<UpnpSoapResponse>> invokeSoapAction,
        TimeSpan? mappingEnumerationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        // Re-checked here as defense-in-depth, matching SsdpUpnpDiscoveryService's own posture - a
        // UpnpGatewayDescriptor should already be safe (SsdpUpnpDiscoveryService only ever produces
        // one whose ControlUrl already passed this same policy), but this constructor must not
        // assume a future caller, or a descriptor cached across a DHCP lease change, still is.
        if (!UpnpAddressPolicy.IsSafeRelatedLanUri(
                descriptor.DescriptionLocation,
                descriptor.ControlUrl))
        {
            throw new ArgumentException(
                "The gateway's control URL is not on the discovered gateway host.",
                nameof(descriptor));
        }

        if (!UpnpAddressPolicy.IsSupportedWanServiceType(descriptor.ServiceType))
        {
            throw new ArgumentException(
                "The gateway's WAN service type is not supported.",
                nameof(descriptor));
        }

        _descriptionLocation = descriptor.DescriptionLocation;
        _controlUrl = descriptor.ControlUrl;
        _serviceType = descriptor.ServiceType;
        _invokeSoapAction = invokeSoapAction;
        _mappingEnumerationTimeout = mappingEnumerationTimeout ?? MappingEnumerationTimeout;
    }

    public async Task<UpnpExternalIpResult> GetExternalIpAddressAsync(CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync("GetExternalIPAddress", [], cancellationToken).ConfigureAwait(false);
        return response.Kind switch
        {
            UpnpSoapResponseKind.Success when
                response.Arguments!.TryGetValue("NewExternalIPAddress", out var ip) &&
                IPAddress.TryParse(ip, out var address) &&
                UpnpAddressPolicy.ClassifyExternalAddress(address) is { } addressKind =>
                UpnpExternalIpResult.Success(address.ToString(), addressKind),
            UpnpSoapResponseKind.Success =>
                UpnpExternalIpResult.Unavailable("The gateway's response did not include a valid external IP address."),
            UpnpSoapResponseKind.Fault => UpnpExternalIpResult.Fault(DescribeFault(response)),
            _ => UpnpExternalIpResult.Unavailable(response.Message ?? "The gateway could not be reached.")
        };
    }

    public async Task<UpnpPortMappingsResult> GetExistingPortMappingsAsync(CancellationToken cancellationToken = default)
    {
        var mappings = new List<UpnpPortMappingEntry>();
        using var timeoutCts = new CancellationTokenSource(_mappingEnumerationTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        try
        {
            for (var index = 0; index < MaxEnumeratedMappings; index++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var response = await InvokeAsync(
                    "GetGenericPortMappingEntry",
                    [("NewPortMappingIndex", index.ToString(CultureInfo.InvariantCulture))],
                    linkedCts.Token).ConfigureAwait(false);

                if (response.Kind == UpnpSoapResponseKind.Fault)
                {
                    // 713 (SpecifiedArrayIndexInvalid) is the standard end-of-list signal.
                    // Every other fault means enumeration itself failed and absence cannot be
                    // trusted by later ownership/mutation logic - but any entries already
                    // gathered before the fault are still real data, not nothing. Discarding them
                    // here would be inconsistent with how a transport failure or a malformed entry
                    // mid-enumeration are already handled below: both preserve partial results as
                    // Incomplete rather than reporting zero mappings when some were genuinely found.
                    if (response.FaultCode == 713)
                    {
                        return UpnpPortMappingsResult.Success(mappings);
                    }

                    return mappings.Count > 0
                        ? UpnpPortMappingsResult.Incomplete(mappings, DescribeFault(response))
                        : UpnpPortMappingsResult.Fault(DescribeFault(response));
                }

                if (response.Kind == UpnpSoapResponseKind.TransportFailure)
                {
                    // A transport failure on the very first request is an unreachable gateway,
                    // while a later failure interrupted an enumeration that produced partial,
                    // explicitly non-authoritative data.
                    var message = response.Message ?? "Mapping enumeration was interrupted.";
                    return mappings.Count > 0
                        ? UpnpPortMappingsResult.Incomplete(mappings, message)
                        : UpnpPortMappingsResult.Unavailable(message);
                }

                var entry = TryParseMappingEntry(response.Arguments!);
                if (entry == null)
                {
                    return UpnpPortMappingsResult.Incomplete(
                        mappings,
                        "The gateway returned a malformed mapping entry, so the complete mapping list could not be verified.");
                }

                mappings.Add(entry);
            }
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return UpnpPortMappingsResult.Incomplete(
                mappings,
                "Mapping enumeration exceeded its 30-second safety deadline.");
        }

        return UpnpPortMappingsResult.Incomplete(
            mappings,
            $"The gateway exposed at least {MaxEnumeratedMappings} mappings; enumeration was capped before completeness could be verified.");
    }

    public async Task<UpnpMutationResult> AddPortMappingAsync(
        UpnpPortMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var protocol = NormalizeMutationArguments(
            request.RemoteHost,
            request.ExternalPort,
            request.Protocol,
            request.InternalPort,
            request.InternalClient,
            request.Description,
            request.LeaseDurationSeconds);
        // Cancellation is only honored up to this point. Once the SOAP request below is
        // dispatched, the router may already act on it before or while a cancellation arrives -
        // throwing at that point would skip returning a result to the caller entirely, leaving
        // UpnpPortMappingService.CreateAsync unable to either register ownership or roll back a
        // mapping the router may have actually created. The gateway's own HTTP client timeout
        // still bounds how long this can run.
        cancellationToken.ThrowIfCancellationRequested();
        var response = await InvokeAsync(
            "AddPortMapping",
            [
                ("NewRemoteHost", request.RemoteHost),
                ("NewExternalPort", request.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", protocol),
                ("NewInternalPort", request.InternalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewInternalClient", request.InternalClient),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", request.Description),
                ("NewLeaseDuration", request.LeaseDurationSeconds.ToString(CultureInfo.InvariantCulture))
            ],
            CancellationToken.None).ConfigureAwait(false);
        return ToMutationResult(response, _serviceType, "AddPortMapping", "The port mapping was created.");
    }

    public async Task<UpnpMutationResult> DeletePortMappingAsync(
        string remoteHost,
        int externalPort,
        string protocol,
        CancellationToken cancellationToken = default)
    {
        var normalizedProtocol = NormalizeMutationArguments(
            remoteHost, externalPort, protocol, 1, "192.168.0.1", "delete", 0,
            validateAddOnlyArguments: false);
        // See AddPortMappingAsync: cancellation is only honored up to this point, not once the
        // request is actually dispatched.
        cancellationToken.ThrowIfCancellationRequested();
        var response = await InvokeAsync(
            "DeletePortMapping",
            [
                ("NewRemoteHost", remoteHost),
                ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", normalizedProtocol)
            ],
            CancellationToken.None).ConfigureAwait(false);
        return ToMutationResult(response, _serviceType, "DeletePortMapping", "The port mapping was removed.");
    }

    private static string NormalizeMutationArguments(
        string remoteHost,
        int externalPort,
        string protocol,
        int internalPort,
        string internalClient,
        string description,
        long leaseDurationSeconds,
        bool validateAddOnlyArguments = true)
    {
        if (remoteHost is null || externalPort is < 1 or > 65535 || !TryNormalizeProtocol(protocol, out var normalizedProtocol))
        {
            throw new ArgumentException("The port-mapping identity is invalid.");
        }

        if (validateAddOnlyArguments &&
            (internalPort is < 1 or > 65535 ||
             !IPAddress.TryParse(internalClient, out var clientAddress) ||
             !UpnpAddressPolicy.IsPrivateLanUnicastIPv4(clientAddress) ||
             string.IsNullOrWhiteSpace(description) || description.Length > 128 ||
             leaseDurationSeconds is < 0 or > uint.MaxValue))
        {
            throw new ArgumentException("The port-mapping request is invalid.");
        }

        return normalizedProtocol;
    }

    // A SOAP body whose child isn't a Fault is otherwise unconditionally treated as success (see
    // ParseSoapResponse) - fine for the read-only calls, where a wrong/unexpected element just
    // yields missing arguments that TryParseMappingEntry already rejects. For a mutation, that
    // gap is a correctness risk: a malformed-but-not-Fault response from a buggy gateway stack
    // must not be reported as "the mapping was created/removed" when it wasn't - CreateAsync would
    // persist ownership for a mapping that doesn't exist, or ReleaseAsync would erase ownership
    // while the router mapping is still there. Requires the exact expected "{action}Response"
    // element in the selected WAN service namespace. Accepting the expected local name in an
    // unrelated namespace would still allow a malformed response to be mistaken for confirmation
    // that a router mutation completed.
    private static UpnpMutationResult ToMutationResult(
        UpnpSoapResponse response,
        string serviceType,
        string actionName,
        string successMessage) =>
        response switch
        {
            {
                Kind: UpnpSoapResponseKind.Success,
                ActionResponseElementName: var name,
                ActionResponseNamespace: var responseNamespace
            } when name == $"{actionName}Response" && responseNamespace == serviceType =>
                UpnpMutationResult.Success(successMessage),
            { Kind: UpnpSoapResponseKind.Success } =>
                UpnpMutationResult.Fault($"The gateway's response was not recognized as a valid {actionName}Response."),
            { Kind: UpnpSoapResponseKind.Fault } => UpnpMutationResult.Fault(DescribeFault(response)),
            _ => UpnpMutationResult.Unavailable(response.Message ?? "The gateway could not be reached.")
        };

    private async Task<UpnpSoapResponse> InvokeAsync(
        string actionName,
        IReadOnlyList<(string Name, string Value)> arguments,
        CancellationToken cancellationToken)
    {
        if (!UpnpAddressPolicy.IsSafeRelatedLanUri(_descriptionLocation, _controlUrl) ||
            !UpnpAddressPolicy.IsSupportedWanServiceType(_serviceType))
        {
            return UpnpSoapResponse.TransportFailure(
                "The gateway endpoint is no longer considered safe.");
        }

        try
        {
            return await _invokeSoapAction(_controlUrl, _serviceType, actionName, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A future caller invoking GetExternalIpAddressAsync/GetExistingPortMappingsAsync for
            // several gateways concurrently must not have one gateway's unexpected failure take the
            // others down with it - the same isolation lesson SsdpUpnpDiscoveryService's own
            // Task.WhenAll review round already established.
            return UpnpSoapResponse.TransportFailure("The gateway could not be reached.");
        }
    }

    private static UpnpPortMappingEntry? TryParseMappingEntry(IReadOnlyDictionary<string, string> arguments)
    {
        if (!arguments.TryGetValue("NewExternalPort", out var externalPortText) ||
            // 0 is a legitimate ui2 wildcard value ("any external port," used by some IGD2
            // gateways) - not a malformed entry. Consumers can distinguish it from a real,
            // assigned port by checking for 0 explicitly.
            !int.TryParse(externalPortText, out var externalPort) || externalPort is < 0 or > 65535 ||
            !arguments.TryGetValue("NewProtocol", out var protocol) ||
            !TryNormalizeProtocol(protocol, out var normalizedProtocol) ||
            !arguments.TryGetValue("NewInternalPort", out var internalPortText) ||
            !int.TryParse(internalPortText, out var internalPort) || internalPort is < 1 or > 65535 ||
            // NewInternalClient's UPnP type is a plain string permitting either an IP address or a
            // DNS host name - requiring it to parse as a private-LAN IP literal rejected otherwise
            // valid entries from gateways that report a hostname instead, and (worse) silently hid
            // every later-indexed mapping too, since a malformed entry here previously stopped the
            // whole enumeration. The value is only ever reported, never resolved or contacted, so
            // it may be either the specified IPv4 form or a syntactically valid DNS hostname.
            !arguments.TryGetValue("NewInternalClient", out var internalClient) ||
            !TryNormalizeInternalClient(internalClient, normalizedProtocol, out var normalizedInternalClient) ||
            // NewEnabled is a required boolean argument - a missing or unparseable value means the
            // entry itself is malformed, the same as a bad port/protocol above, not a signal to
            // silently guess "disabled." The result model has no "unknown" state for this field, so
            // a guessed value would misrepresent the mapping's real state to any later caller.
            !arguments.TryGetValue("NewEnabled", out var enabledText) ||
            !TryParseEnabled(enabledText, out var enabled))
        {
            return null;
        }

        arguments.TryGetValue("NewRemoteHost", out var remoteHost);
        arguments.TryGetValue("NewPortMappingDescription", out var description);
        // NewLeaseDuration is a ui4 (unsigned 32-bit) per the UPnP spec, so its valid range extends
        // past int.MaxValue (e.g. 4294967295) - long.TryParse plus an explicit uint.MaxValue bound
        // captures the full valid range instead of int.TryParse silently failing, and therefore
        // losing, any value above 2147483647.
        long? leaseDuration = arguments.TryGetValue("NewLeaseDuration", out var leaseText) &&
            long.TryParse(leaseText, out var lease) && lease >= 0 && lease <= uint.MaxValue
                ? lease
                : null;

        return new UpnpPortMappingEntry(
            string.IsNullOrWhiteSpace(remoteHost) ? null : remoteHost,
            externalPort,
            normalizedProtocol,
            internalPort,
            normalizedInternalClient,
            enabled,
            string.IsNullOrWhiteSpace(description) ? null : description,
            leaseDuration);
    }

    private static bool TryNormalizeInternalClient(
        string value,
        string protocol,
        out string normalized)
    {
        normalized = string.Empty;
        var candidate = value.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(candidate, out var address))
        {
            var isMappedIpv4 = address.IsIPv4MappedToIPv6;
            address = UpnpAddressPolicy.NormalizeAddress(address);
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                (!isMappedIpv4 && !IsCanonicalDottedDecimalIpv4(candidate)) ||
                (address.Equals(IPAddress.Broadcast) && protocol != "UDP"))
            {
                return false;
            }

            normalized = address.ToString();
            return true;
        }

        // InternalClient permits a DNS host name. Keep validation local and resolution-free:
        // resolving router-provided input would create another network trust boundary.
        var hostname = candidate.EndsWith(".", StringComparison.Ordinal)
            ? candidate[..^1]
            : candidate;
        if (hostname.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in hostname.Split('.'))
        {
            if (label.Length is 0 or > 63 ||
                label[0] == '-' ||
                label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }

    private static bool IsCanonicalDottedDecimalIpv4(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 4 &&
            parts.All(part =>
                byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) &&
                part == octet.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParseEnabled(string value, out bool enabled)
    {
        if (value is "1" or "0")
        {
            enabled = value == "1";
            return true;
        }

        return bool.TryParse(value, out enabled);
    }

    private static bool TryNormalizeProtocol(string value, out string normalized)
    {
        normalized = value.Trim().ToUpperInvariant();
        return normalized is "TCP" or "UDP";
    }

    private static string DescribeFault(UpnpSoapResponse response) =>
        string.IsNullOrWhiteSpace(response.Message)
            ? $"The gateway rejected the request (error {response.FaultCode})."
            : $"The gateway rejected the request: {response.Message} (error {response.FaultCode}).";

    private static async Task<UpnpSoapResponse> DefaultInvokeSoapActionAsync(
        Uri controlUrl,
        string serviceType,
        string actionName,
        IReadOnlyList<(string Name, string Value)> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!UpnpAddressPolicy.IsSafeLanUri(controlUrl) ||
                !UpnpAddressPolicy.IsSupportedWanServiceType(serviceType))
            {
                return UpnpSoapResponse.TransportFailure(
                    "The gateway endpoint is not considered safe.");
            }

            var requestBody = BuildSoapRequestBody(serviceType, actionName, arguments);
            using var request = new HttpRequestMessage(HttpMethod.Post, controlUrl)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{actionName}\"");

            using var response = await UpnpHttpClient.Shared
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A SOAP fault is conventionally delivered as HTTP 500 with a Fault body that must
            // still be parsed - only other, non-success statuses are a genuine transport failure.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.InternalServerError)
            {
                return UpnpSoapResponse.TransportFailure($"The gateway returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseContentBytes)
            {
                return UpnpSoapResponse.TransportFailure("The gateway returned an unexpectedly large response.");
            }

            await response.Content.LoadIntoBufferAsync(MaxResponseContentBytes, cancellationToken).ConfigureAwait(false);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseSoapResponse(xml);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Covers HttpRequestException, a client-side timeout, and any other transport failure -
            // one unreachable/misbehaving gateway is reported, not thrown.
            return UpnpSoapResponse.TransportFailure("The gateway could not be reached.");
        }
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise envelope
    // construction directly, without a real HTTP call.
    internal static string BuildSoapRequestBody(
        string serviceType, string actionName, IReadOnlyList<(string Name, string Value)> arguments)
    {
        XNamespace actionNs = serviceType;
        var actionElement = new XElement(
            actionNs + actionName,
            // UPnP qualifies only the action element itself (conventionally via a "u:" prefix) -
            // argument elements are left unqualified (no namespace at all). Declaring xmlns:u as a
            // PREFIXED namespace here (not a default xmlns=) means these unqualified children
            // serialize with no namespace, matching real UPnP SOAP requests, rather than silently
            // inheriting a default namespace from the action element.
            new XAttribute(XNamespace.Xmlns + "u", actionNs.NamespaceName),
            arguments.Select(argument => new XElement(argument.Name, argument.Value)));

        var envelope = new XElement(
            SoapEnvelopeNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", SoapEnvelopeNs.NamespaceName),
            new XAttribute(SoapEnvelopeNs + "encodingStyle", SoapEncodingNs.NamespaceName),
            new XElement(SoapEnvelopeNs + "Body", actionElement));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), envelope).ToString(SaveOptions.DisableFormatting);
    }

    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise response
    // parsing directly against sample XML, without a real HTTP call.
    internal static UpnpSoapResponse ParseSoapResponse(string xml)
    {
        XDocument document;
        try
        {
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseContentBytes,
                MaxCharactersFromEntities = 0
            });
            document = XDocument.Load(xmlReader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return UpnpSoapResponse.TransportFailure("The gateway returned a malformed SOAP response.");
        }

        var body = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        if (body == null)
        {
            return UpnpSoapResponse.TransportFailure("The gateway's SOAP response had no Body element.");
        }

        var fault = body.Elements().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault != null)
        {
            var detail = fault.Elements().FirstOrDefault(e => e.Name.LocalName == "detail");
            var upnpError = detail?.Elements().FirstOrDefault(e => e.Name.LocalName == "UPnPError");
            var errorCodeText = upnpError?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var errorDescription = upnpError?.Elements().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value
                ?? fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
            int.TryParse(errorCodeText, out var errorCode);
            return UpnpSoapResponse.FaultResponse(errorCode, errorDescription);
        }

        var actionResponse = body.Elements().FirstOrDefault();
        if (actionResponse == null)
        {
            return UpnpSoapResponse.TransportFailure("The gateway's SOAP response body was empty.");
        }

        var argumentsDictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in actionResponse.Elements())
        {
            argumentsDictionary[element.Name.LocalName] = element.Value;
        }

        return UpnpSoapResponse.SuccessResponse(
            argumentsDictionary,
            actionResponse.Name.LocalName,
            actionResponse.Name.NamespaceName);
    }
}

internal enum UpnpSoapResponseKind
{
    Success,
    Fault,
    TransportFailure
}

internal sealed record UpnpSoapResponse(
    UpnpSoapResponseKind Kind,
    IReadOnlyDictionary<string, string>? Arguments,
    int FaultCode,
    string? Message,
    // Optional: only ToMutationResult (mutation calls) needs this; the read-only calls' existing
    // tests construct plenty of fake successes without it.
    string? ActionResponseElementName = null,
    string? ActionResponseNamespace = null)
{
    public static UpnpSoapResponse SuccessResponse(
        IReadOnlyDictionary<string, string> arguments,
        string? actionResponseElementName = null,
        string? actionResponseNamespace = null) =>
        new(UpnpSoapResponseKind.Success, arguments, 0, null, actionResponseElementName, actionResponseNamespace);

    public static UpnpSoapResponse FaultResponse(int errorCode, string? errorDescription) =>
        new(UpnpSoapResponseKind.Fault, null, errorCode, errorDescription);

    public static UpnpSoapResponse TransportFailure(string message) =>
        new(UpnpSoapResponseKind.TransportFailure, null, 0, message);
}
