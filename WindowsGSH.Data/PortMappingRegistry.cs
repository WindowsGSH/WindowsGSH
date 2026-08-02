using Microsoft.Data.Sqlite;
using WindowsGSH.Core.Network.Upnp;

namespace WindowsGSH.Data;

public sealed class PortMappingRegistry(string? databasePath = null) : IPortMappingRegistry
{
    public async Task<bool> TryRegisterAsync(
        OwnedPortMapping mapping,
        CancellationToken cancellationToken = default)
    {
        Validate(mapping);
        await using var connection = Open();
        using var transaction = connection.BeginTransaction();

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT OR IGNORE INTO owned_port_mappings
                    (ownership_id, gateway_id, server_id, remote_host, external_port, protocol,
                     internal_port, internal_client, lease_duration_seconds, created_utc, refresh_due_utc)
                VALUES
                    ($ownershipId, $gatewayId, $serverId, $remoteHost, $externalPort, $protocol,
                     $internalPort, $internalClient, $lease, $createdUtc, $refreshDueUtc);
                """;
            insertCommand.Parameters.AddWithValue("$ownershipId", mapping.OwnershipId.ToString("D"));
            insertCommand.Parameters.AddWithValue("$gatewayId", mapping.GatewayId);
            insertCommand.Parameters.AddWithValue("$serverId", mapping.ServerId);
            insertCommand.Parameters.AddWithValue("$remoteHost", mapping.RemoteHost);
            insertCommand.Parameters.AddWithValue("$externalPort", mapping.ExternalPort);
            insertCommand.Parameters.AddWithValue("$protocol", mapping.Protocol);
            insertCommand.Parameters.AddWithValue("$internalPort", mapping.InternalPort);
            insertCommand.Parameters.AddWithValue("$internalClient", mapping.InternalClient);
            insertCommand.Parameters.AddWithValue("$lease", mapping.LeaseDurationSeconds);
            insertCommand.Parameters.AddWithValue("$createdUtc", mapping.CreatedUtc.ToString("O"));
            insertCommand.Parameters.AddWithValue(
                "$refreshDueUtc",
                mapping.RefreshDueUtc is { } refresh ? refresh.ToString("O") : DBNull.Value);

            if (await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                transaction.Commit();
                return true;
            }
        }

        // The router-identity tuple (gateway/host/port/protocol) already has a row. That's still a
        // real conflict when a DIFFERENT ownership id holds it - but re-registering the exact same
        // (ownershipId, tuple) pair - an idempotent retry after, say, a crash before the caller
        // could persist confirmation of its own earlier success - must read as success too, not as
        // an unexplained failure indistinguishable from someone else already owning this mapping.
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText = """
                SELECT ownership_id FROM owned_port_mappings
                WHERE gateway_id = $gatewayId AND remote_host = $remoteHost
                  AND external_port = $externalPort AND protocol = $protocol;
                """;
            selectCommand.Parameters.AddWithValue("$gatewayId", mapping.GatewayId);
            selectCommand.Parameters.AddWithValue("$remoteHost", mapping.RemoteHost);
            selectCommand.Parameters.AddWithValue("$externalPort", mapping.ExternalPort);
            selectCommand.Parameters.AddWithValue("$protocol", mapping.Protocol);

            var existingOwnershipId = (string?)await selectCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return existingOwnershipId != null && Guid.Parse(existingOwnershipId) == mapping.OwnershipId;
        }
    }

    public async Task<IReadOnlyList<OwnedPortMapping>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ownership_id, gateway_id, server_id, remote_host, external_port, protocol,
                   internal_port, internal_client, lease_duration_seconds, created_utc, refresh_due_utc
            FROM owned_port_mappings
            ORDER BY created_utc, ownership_id;
            """;
        var result = new List<OwnedPortMapping>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OwnedPortMapping(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetInt64(8),
                DateTimeOffset.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10))));
        }

        return result;
    }

    public async Task<bool> TryRemoveAsync(Guid ownershipId, CancellationToken cancellationToken = default)
    {
        if (ownershipId == Guid.Empty)
        {
            return false;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM owned_port_mappings WHERE ownership_id = $ownershipId;";
        command.Parameters.AddWithValue("$ownershipId", ownershipId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private SqliteConnection Open() =>
        databasePath == null ? AppDatabase.OpenConnection() : AppDatabase.OpenConnection(databasePath);

    private static void Validate(OwnedPortMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.OwnershipId == Guid.Empty ||
            string.IsNullOrWhiteSpace(mapping.GatewayId) ||
            string.IsNullOrWhiteSpace(mapping.ServerId) ||
            // Unlike GatewayId/ServerId/InternalClient, blank is a valid, expected RemoteHost value
            // (it means "any remote host," per UPnP's own convention - see OwnedPortMapping's own
            // doc comment for why this must stay non-null, not just non-blank). Only null itself is
            // rejected here, so a null that slips past the compiler's nullable-reference-type
            // checking fails cleanly with ArgumentException instead of an opaque exception from
            // Microsoft.Data.Sqlite's own parameter binding (confirmed directly: AddWithValue with a
            // literal null throws there, before ever reaching the NOT NULL column constraint).
            mapping.RemoteHost is null ||
            mapping.ExternalPort is < 0 or > 65535 ||
            mapping.InternalPort is < 1 or > 65535 ||
            mapping.Protocol is not ("TCP" or "UDP") ||
            string.IsNullOrWhiteSpace(mapping.InternalClient) ||
            mapping.LeaseDurationSeconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentException("The owned port mapping is invalid.", nameof(mapping));
        }
    }
}
