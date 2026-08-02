namespace WindowsGSH.Core.Network.Upnp;

/// <summary>
/// Enforces WindowsGSH ownership around router mutations. Router description text and matching
/// ports are never treated as ownership evidence; only a durable registry record authorizes delete.
/// </summary>
public sealed class UpnpPortMappingService
{
    private readonly string _gatewayId;
    private readonly IUpnpGateway _gateway;
    private readonly IPortMappingRegistry _registry;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public UpnpPortMappingService(
        string gatewayId,
        IUpnpGateway gateway,
        IPortMappingRegistry registry,
        Func<DateTimeOffset>? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
        {
            throw new ArgumentException("A stable gateway identifier is required.", nameof(gatewayId));
        }

        _gatewayId = gatewayId;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<PortMappingOperationResult> CreateAsync(
        string serverId,
        UpnpPortMappingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("A server identifier is required.", nameof(serverId));
        }

        ArgumentNullException.ThrowIfNull(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _gateway.GetExistingPortMappingsAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Outcome != UpnpSoapOutcome.Success)
            {
                return PortMappingOperationResult.Refused(
                    "The router's complete mapping list could not be verified; no mapping was changed.");
            }

            var protocol = request.Protocol.Trim().ToUpperInvariant();
            if (existing.Mappings.Any(mapping =>
                    string.Equals(mapping.RemoteHost ?? string.Empty, request.RemoteHost, StringComparison.OrdinalIgnoreCase) &&
                    mapping.ExternalPort == request.ExternalPort &&
                    string.Equals(mapping.Protocol, protocol, StringComparison.OrdinalIgnoreCase)))
            {
                return PortMappingOperationResult.Refused(
                    "That external port and protocol already exist on the router; WindowsGSH will not replace them.");
            }

            var ownershipId = Guid.NewGuid();
            var ownedDescription = $"WindowsGSH:{ownershipId:D}";
            var mutationRequest = request with { Protocol = protocol, Description = ownedDescription };
            var addResult = await _gateway.AddPortMappingAsync(mutationRequest, cancellationToken).ConfigureAwait(false);
            if (!addResult.Succeeded)
            {
                return PortMappingOperationResult.Failed(addResult.Message);
            }

            var now = _utcNow();
            DateTimeOffset? refreshDue = request.LeaseDurationSeconds > 0
                ? now.AddSeconds(request.LeaseDurationSeconds * 0.75)
                : null;
            var owned = new OwnedPortMapping(
                ownershipId, _gatewayId, serverId, request.RemoteHost, request.ExternalPort,
                protocol, request.InternalPort, request.InternalClient, request.LeaseDurationSeconds,
                now, refreshDue);
            try
            {
                // Once the router has changed, finish the small local persistence step even if the
                // caller cancels. Honouring cancellation between those two operations would leave
                // a mapping on the router with no ownership record and therefore no safe cleanup.
                if (await _registry.TryRegisterAsync(owned, CancellationToken.None).ConfigureAwait(false))
                {
                    return PortMappingOperationResult.Success(ownershipId, addResult.Message);
                }
            }
            catch
            {
                // The rollback below is the safe recovery for both a rejected row and a local
                // persistence failure. Do not expose the database exception or skip cleanup.
            }

            // Do not leave a successfully-created but untracked mapping behind when persistence
            // rejects the ownership record. Roll back the exact tuple from this serialized create.
            var rollback = await _gateway.DeletePortMappingAsync(
                request.RemoteHost, request.ExternalPort, protocol, CancellationToken.None).ConfigureAwait(false);
            return rollback.Succeeded
                ? PortMappingOperationResult.Failed(
                    "The router accepted the mapping, but ownership could not be recorded; the mapping was rolled back.")
                : PortMappingOperationResult.Failed(
                    "The router accepted the mapping, but ownership could not be recorded and rollback failed. Manual router cleanup may be required.");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PortMappingOperationResult> ReleaseAsync(
        Guid ownershipId,
        CancellationToken cancellationToken = default)
    {
        if (ownershipId == Guid.Empty)
        {
            return PortMappingOperationResult.Refused("A valid WindowsGSH ownership identifier is required.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var owned = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(mapping => mapping.OwnershipId == ownershipId);
            if (owned == null || !string.Equals(owned.GatewayId, _gatewayId, StringComparison.Ordinal))
            {
                return PortMappingOperationResult.Refused(
                    "WindowsGSH has no ownership record for this mapping on this gateway; nothing was removed.");
            }

            var current = await _gateway.GetExistingPortMappingsAsync(cancellationToken).ConfigureAwait(false);
            if (current.Outcome != UpnpSoapOutcome.Success)
            {
                return PortMappingOperationResult.Refused(
                    "The router's complete mapping list could not be verified; nothing was removed.");
            }

            var ownershipMarker = $"WindowsGSH:{owned.OwnershipId:D}";
            var matchingMappings = current.Mappings.Where(mapping =>
                    string.Equals(mapping.RemoteHost ?? string.Empty, owned.RemoteHost, StringComparison.OrdinalIgnoreCase) &&
                    mapping.ExternalPort == owned.ExternalPort &&
                    string.Equals(mapping.Protocol, owned.Protocol, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();

            if (matchingMappings.Length == 0)
            {
                // current.Outcome is a verified-complete enumeration (checked above), so this
                // genuinely means the mapping is gone - a finite lease expired, the router
                // rebooted, or it was removed outside WindowsGSH. There is nothing left on the
                // router to delete; leaving the ownership row behind would just make the
                // registry's unique tuple constraint permanently refuse to let this exact
                // port/protocol be recreated (the next CreateAsync would add it, fail to
                // register against the stale row, and roll itself back). Non-cancelable for the
                // same reason as the delete-then-cleanup path below: this is the local half of
                // reconciling state that has already been confirmed.
                if (!await TryRemoveOwnershipAsync(ownershipId).ConfigureAwait(false))
                {
                    return PortMappingOperationResult.Failed(
                        "The router mapping was already gone, but its ownership record could not be cleared.");
                }

                return PortMappingOperationResult.Success(
                    ownershipId, "The router mapping was already gone; the ownership record was cleared.");
            }

            var stillOwned = matchingMappings.Length == 1 ? matchingMappings[0] : null;
            if (stillOwned == null ||
                stillOwned.InternalPort != owned.InternalPort ||
                !string.Equals(stillOwned.InternalClient, owned.InternalClient, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(stillOwned.Description, ownershipMarker, StringComparison.Ordinal))
            {
                return PortMappingOperationResult.Refused(
                    "The router mapping no longer exactly matches WindowsGSH's ownership record; nothing was removed.");
            }

            var deleteResult = await _gateway.DeletePortMappingAsync(
                owned.RemoteHost, owned.ExternalPort, owned.Protocol, cancellationToken).ConfigureAwait(false);
            if (!deleteResult.Succeeded)
            {
                return PortMappingOperationResult.Failed(deleteResult.Message);
            }

            // Once the router mapping is confirmed gone, finish the local persistence step even
            // if the caller cancels - matching CreateAsync's own reasoning for its registration
            // step. Honoring cancellation here would leave an ownership row for a mapping that no
            // longer exists, hitting the exact same stale-row problem as the branch above.
            if (!await TryRemoveOwnershipAsync(ownershipId).ConfigureAwait(false))
            {
                return PortMappingOperationResult.Failed(
                    "The router mapping was removed, but its ownership record could not be cleared.");
            }

            return PortMappingOperationResult.Success(ownershipId, deleteResult.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<bool> TryRemoveOwnershipAsync(Guid ownershipId)
    {
        try
        {
            return await _registry.TryRemoveAsync(ownershipId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Router state is already confirmed absent when this helper is called. Keep the stale
            // row for a later reconciliation retry, but return the service's normal diagnostic
            // failure instead of leaking a persistence exception through the router-operation API.
            return false;
        }
    }
}

public sealed record PortMappingOperationResult(bool Succeeded, bool WasRefused, Guid? OwnershipId, string Message)
{
    public static PortMappingOperationResult Success(Guid ownershipId, string message) => new(true, false, ownershipId, message);
    public static PortMappingOperationResult Failed(string message) => new(false, false, null, message);
    public static PortMappingOperationResult Refused(string message) => new(false, true, null, message);
}
