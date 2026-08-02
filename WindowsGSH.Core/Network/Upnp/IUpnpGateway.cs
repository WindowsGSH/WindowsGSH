namespace WindowsGSH.Core.Network.Upnp;

/// <summary>
/// SOAP operations against a single, already-discovered gateway.
/// </summary>
public interface IUpnpGateway
{
    Task<UpnpExternalIpResult> GetExternalIpAddressAsync(CancellationToken cancellationToken = default);

    Task<UpnpPortMappingsResult> GetExistingPortMappingsAsync(CancellationToken cancellationToken = default);

    Task<UpnpMutationResult> AddPortMappingAsync(UpnpPortMappingRequest request, CancellationToken cancellationToken = default);

    Task<UpnpMutationResult> DeletePortMappingAsync(string remoteHost, int externalPort, string protocol, CancellationToken cancellationToken = default);
}

public sealed record UpnpPortMappingRequest(
    string RemoteHost,
    int ExternalPort,
    string Protocol,
    int InternalPort,
    string InternalClient,
    string Description,
    long LeaseDurationSeconds);

public sealed record UpnpMutationResult(UpnpSoapOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == UpnpSoapOutcome.Success;

    public static UpnpMutationResult Success(string message) => new(UpnpSoapOutcome.Success, message);
    public static UpnpMutationResult Fault(string message) => new(UpnpSoapOutcome.Fault, message);
    public static UpnpMutationResult Unavailable(string message) => new(UpnpSoapOutcome.Unavailable, message);
}

public enum UpnpSoapOutcome
{
    /// <summary>The gateway answered with a well-formed, successful response.</summary>
    Success,

    /// <summary>A transport-level problem (couldn't connect, timeout, malformed response) - not a real answer from the gateway.</summary>
    Unavailable,

    /// <summary>The gateway answered with a well-formed SOAP fault (e.g. the action isn't supported, or an argument was rejected).</summary>
    Fault,

    /// <summary>The complete mapping list could not be proven; any returned entries are non-authoritative partial data.</summary>
    Incomplete
}

public enum UpnpExternalAddressKind
{
    Public,
    Private,
    CarrierGradeNat
}

public sealed record UpnpExternalIpResult(
    UpnpSoapOutcome Outcome,
    string? ExternalIpAddress,
    string Message,
    UpnpExternalAddressKind? AddressKind = null)
{
    public static UpnpExternalIpResult Unavailable(string message) => new(UpnpSoapOutcome.Unavailable, null, message);

    public static UpnpExternalIpResult Fault(string message) => new(UpnpSoapOutcome.Fault, null, message);

    public static UpnpExternalIpResult Success(
        string externalIpAddress,
        UpnpExternalAddressKind addressKind) =>
        new(
            UpnpSoapOutcome.Success,
            externalIpAddress,
            addressKind == UpnpExternalAddressKind.Public
                ? "The gateway reported a public external IP address."
                : addressKind == UpnpExternalAddressKind.CarrierGradeNat
                    ? "The gateway reported a Carrier-Grade NAT address; inbound forwarding may not be reachable from the internet."
                    : "The gateway reported a private external address, which may indicate double NAT.",
            addressKind);
}

public sealed record UpnpPortMappingEntry(
    string? RemoteHost,
    int ExternalPort,
    string Protocol,
    int InternalPort,
    string InternalClient,
    bool Enabled,
    string? Description,
    // long, not int: NewLeaseDuration is a ui4 (unsigned 32-bit) per the UPnP spec, so a valid
    // value can exceed int.MaxValue.
    long? LeaseDurationSeconds);

public sealed record UpnpPortMappingsResult(UpnpSoapOutcome Outcome, IReadOnlyList<UpnpPortMappingEntry> Mappings, string Message)
{
    public static UpnpPortMappingsResult Unavailable(string message) => new(UpnpSoapOutcome.Unavailable, [], message);

    public static UpnpPortMappingsResult Fault(string message) => new(UpnpSoapOutcome.Fault, [], message);

    public static UpnpPortMappingsResult Incomplete(
        IReadOnlyList<UpnpPortMappingEntry> mappings,
        string message) =>
        new(UpnpSoapOutcome.Incomplete, mappings, message);

    public static UpnpPortMappingsResult Success(IReadOnlyList<UpnpPortMappingEntry> mappings) =>
        new(UpnpSoapOutcome.Success, mappings, $"Found {mappings.Count} existing port mapping(s).");
}
