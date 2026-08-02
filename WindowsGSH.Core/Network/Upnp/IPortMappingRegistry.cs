namespace WindowsGSH.Core.Network.Upnp;

/// <summary>
/// Durable record of mappings successfully created by WindowsGSH. A mapping's presence on the
/// router, its description text, or a matching port number is never evidence of ownership.
/// </summary>
public interface IPortMappingRegistry
{
    Task<bool> TryRegisterAsync(OwnedPortMapping mapping, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OwnedPortMapping>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<bool> TryRemoveAsync(Guid ownershipId, CancellationToken cancellationToken = default);
}

public sealed record OwnedPortMapping(
    Guid OwnershipId,
    string GatewayId,
    string ServerId,
    // Empty string, not null, means "any remote host" (the common case - matches UPnP's own
    // NewRemoteHost convention). Deliberately non-nullable: PortMappingRegistry's SQL UNIQUE index
    // on (gateway_id, remote_host, external_port, protocol) relies on this - SQLite (like most SQL
    // engines) treats NULL as distinct from every other NULL inside a unique index, so a nullable
    // column here would silently defeat the "can't double-claim the same router mapping" guarantee
    // for every "any host" mapping. UpnpPortMappingEntry.RemoteHost (the read-path type from
    // Tier 5.4b) is nullable and normalizes blank to null - callers correlating an existing router
    // mapping against this registry must normalize that null back to "" before comparing.
    string RemoteHost,
    int ExternalPort,
    string Protocol,
    int InternalPort,
    string InternalClient,
    long LeaseDurationSeconds,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? RefreshDueUtc);
