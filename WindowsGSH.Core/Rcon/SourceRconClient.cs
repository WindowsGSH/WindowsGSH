using System.Net.Sockets;
using System.Text;
using System.IO;

namespace WindowsGSH.Core.Rcon;

public sealed class SourceRconClient
{
    private const int ServerDataResponseValue = 0;
    private const int ServerDataExecCommand = 2;
    private const int ServerDataAuth = 3;

    public async Task<string> ExecuteAsync(string host, int port, string password, string command, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();

        await WritePacketAsync(stream, 1, ServerDataAuth, password, cancellationToken);
        using var authTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var authCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authTimeout.Token);
        var authenticated = false;
        try
        {
            // Valve servers commonly send an empty RESPONSE_VALUE followed by AUTH_RESPONSE.
            // ASA sends only AUTH_RESPONSE, so requiring exactly two packets delays every command.
            for (var packetIndex = 0; packetIndex < 2; packetIndex++)
            {
                var packet = await ReadPacketAsync(stream, authCancellation.Token);
                if (packet.Id == -1)
                {
                    throw new InvalidOperationException("RCON authentication failed.");
                }

                if (packet.Id == 1 && packet.Type == ServerDataExecCommand)
                {
                    authenticated = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (authTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("RCON authentication response timed out.");
        }

        if (!authenticated)
        {
            throw new InvalidDataException("RCON server returned an unexpected authentication response.");
        }

        await WritePacketAsync(stream, 2, ServerDataExecCommand, command, cancellationToken);
        await WritePacketAsync(stream, 3, ServerDataResponseValue, string.Empty, cancellationToken);

        var output = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(stream, linked.Token);
                if (packet.Id == 3 && output.Length > 0)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(packet.Body))
                {
                    output.Append(packet.Body);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        catch (IOException) when (output.Length > 0 || !cancellationToken.IsCancellationRequested)
        {
        }

        return output.Length == 0 ? "(no response)" : output.ToString();
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var packetSize = 4 + 4 + bodyBytes.Length + 2;
        var packet = new byte[4 + packetSize];

        WriteInt(packet, 0, packetSize);
        WriteInt(packet, 4, id);
        WriteInt(packet, 8, type);
        Buffer.BlockCopy(bodyBytes, 0, packet, 12, bodyBytes.Length);

        await stream.WriteAsync(packet, cancellationToken);
    }

    private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sizeBuffer = await ReadExactlyAsync(stream, 4, cancellationToken);
        var size = BitConverter.ToInt32(sizeBuffer, 0);
        var packetBuffer = await ReadExactlyAsync(stream, size, cancellationToken);

        var id = BitConverter.ToInt32(packetBuffer, 0);
        var type = BitConverter.ToInt32(packetBuffer, 4);
        var bodyLength = Math.Max(0, size - 10);
        var body = Encoding.UTF8.GetString(packetBuffer, 8, bodyLength).TrimEnd('\0');
        return new RconPacket(id, type, body);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("RCON connection closed.");
            }

            offset += read;
        }

        return buffer;
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
    }

    private sealed record RconPacket(int Id, int Type, string Body);
}
