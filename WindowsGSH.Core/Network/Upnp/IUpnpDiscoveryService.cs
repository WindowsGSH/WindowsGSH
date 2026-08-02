namespace WindowsGSH.Core.Network.Upnp;

public interface IUpnpDiscoveryService
{
    /// <summary>
    /// Discovers Internet Gateway Devices (IGDs) on the local network via SSDP and reads each one's
    /// device description to locate its WAN connection service. Read-only: no SOAP action is ever
    /// invoked and no port mapping is created, changed, or removed by this service.
    /// </summary>
    Task<IReadOnlyList<UpnpGatewayDescriptor>> DiscoverGatewaysAsync(
        TimeSpan searchTimeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A discovered gateway that advertises a WAN IP or PPP connection service. <see cref="ControlUrl"/>
/// is captured for a later sub-chunk (actual port mapping) to use - this service itself never calls it.
/// </summary>
public sealed record UpnpGatewayDescriptor(
    Uri DescriptionLocation,
    string? FriendlyName,
    string? Manufacturer,
    string? ModelName,
    string DeviceType,
    string ServiceType,
    Uri ControlUrl,
    Uri? EventSubUrl,
    string? Usn);
