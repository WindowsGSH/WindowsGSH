using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Diagnostics;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Query;

public sealed class SourceA2sClient
{
    private static readonly byte[] InfoQuery = Encoding.ASCII.GetBytes("TSource Engine Query\0");
    internal static readonly TimeSpan MaximumPlayerQueryTimeout = TimeSpan.FromMilliseconds(750);

    public async Task<SourceA2sInfo> QueryInfoAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new UdpClient();
        var endpoint = new IPEndPoint(ResolveHost(host), port);

        SourceA2sInfo info;
        using (var infoTimeout = new CancellationTokenSource(timeout))
        using (var infoCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, infoTimeout.Token))
        {
            var request = BuildRequest(null);
            await client.SendAsync(request, request.Length, endpoint);
            var response = await ReceiveAsync(client, infoCancellation.Token);

            if (IsChallengeResponse(response, out var challenge))
            {
                request = BuildRequest(challenge);
                await client.SendAsync(request, request.Length, endpoint);
                response = await ReceiveAsync(client, infoCancellation.Token);
            }

            info = ParseInfo(response);
        }

        IReadOnlyList<QueryPlayer> players = [];
        string? playerQueryMessage = null;
        using var playerTimeout = new CancellationTokenSource(GetPlayerQueryTimeout(timeout));
        using var playerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, playerTimeout.Token);
        try
        {
            players = await QueryPlayersAsync(client, endpoint, playerCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            playerQueryMessage = $"A2S_PLAYER unavailable: timed out after {GetPlayerQueryTimeout(timeout).TotalMilliseconds:0} ms.";
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            playerQueryMessage = "A2S_PLAYER unavailable: " + ex.Message;
        }

        stopwatch.Stop();
        return info with
        {
            QueryDurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            PlayerRows = players,
            DetailMessage = playerQueryMessage
        };
    }

    internal static TimeSpan GetPlayerQueryTimeout(TimeSpan infoTimeout)
    {
        if (infoTimeout <= TimeSpan.Zero)
        {
            return MaximumPlayerQueryTimeout;
        }

        return infoTimeout < MaximumPlayerQueryTimeout ? infoTimeout : MaximumPlayerQueryTimeout;
    }

    private static async Task<byte[]> ReceiveAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var result = await client.ReceiveAsync(cancellationToken);
        return result.Buffer;
    }

    private static byte[] BuildRequest(int? challenge)
    {
        var length = 4 + InfoQuery.Length + (challenge.HasValue ? 4 : 0);
        var request = new byte[length];
        request[0] = 0xFF;
        request[1] = 0xFF;
        request[2] = 0xFF;
        request[3] = 0xFF;
        Buffer.BlockCopy(InfoQuery, 0, request, 4, InfoQuery.Length);

        if (challenge.HasValue)
        {
            BitConverter.GetBytes(challenge.Value).CopyTo(request, 4 + InfoQuery.Length);
        }

        return request;
    }

    private static async Task<IReadOnlyList<QueryPlayer>> QueryPlayersAsync(
        UdpClient client,
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var request = BuildPlayerRequest(unchecked((int)0xFFFFFFFF));
        await client.SendAsync(request, request.Length, endpoint);
        var response = await ReceiveAsync(client, cancellationToken);
        if (IsChallengeResponse(response, out var challenge))
        {
            request = BuildPlayerRequest(challenge);
            await client.SendAsync(request, request.Length, endpoint);
            response = await ReceiveAsync(client, cancellationToken);
        }

        return ParsePlayers(response);
    }

    private static byte[] BuildPlayerRequest(int challenge)
    {
        var request = new byte[9];
        request[0] = 0xFF;
        request[1] = 0xFF;
        request[2] = 0xFF;
        request[3] = 0xFF;
        request[4] = 0x55;
        BitConverter.GetBytes(challenge).CopyTo(request, 5);
        return request;
    }

    private static IPAddress ResolveHost(string host)
    {
        if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out var address)
            ? address
            : Dns.GetHostAddresses(host).First(address => address.AddressFamily == AddressFamily.InterNetwork);
    }

    private static bool IsChallengeResponse(byte[] response, out int challenge)
    {
        challenge = 0;
        if (response.Length < 9 || !HasSimplePacketHeader(response) || response[4] != 0x41)
        {
            return false;
        }

        challenge = BitConverter.ToInt32(response, 5);
        return true;
    }

    internal static SourceA2sInfo ParseInfo(byte[] response)
    {
        if (response.Length < 6 || !HasSimplePacketHeader(response) || response[4] != 0x49)
        {
            throw new InvalidDataException("A2S_INFO returned an unexpected response.");
        }

        var offset = 5;
        offset++; // protocol
        var name = ReadNullTerminatedString(response, ref offset);
        var map = ReadNullTerminatedString(response, ref offset);
        var folder = ReadNullTerminatedString(response, ref offset);
        var game = ReadNullTerminatedString(response, ref offset);
        offset += 2; // app id

        if (offset + 3 > response.Length)
        {
            throw new InvalidDataException("A2S_INFO response was truncated.");
        }

        var players = response[offset++];
        var maxPlayers = response[offset++];
        offset++; // bots
        offset += 4; // server type, environment, visibility, VAC
        var version = offset < response.Length
            ? ReadNullTerminatedString(response, ref offset)
            : null;

        return new SourceA2sInfo(name, map, folder, game, players, maxPlayers, version);
    }

    internal static IReadOnlyList<QueryPlayer> ParsePlayers(byte[] response)
    {
        if (HasSplitPacketHeader(response))
        {
            throw new InvalidDataException("A2S_PLAYER returned a split packet response, which is not supported yet.");
        }

        if (response.Length < 6 || !HasSimplePacketHeader(response) || response[4] != 0x44)
        {
            throw new InvalidDataException("A2S_PLAYER returned an unexpected response.");
        }

        var offset = 5;
        var count = response[offset++];
        var players = new List<QueryPlayer>(count);
        for (var index = 0; index < count; index++)
        {
            if (offset >= response.Length)
            {
                throw new InvalidDataException("A2S_PLAYER response was truncated.");
            }

            var playerIndex = response[offset++];
            var name = ReadNullTerminatedString(response, ref offset);
            if (offset + 8 > response.Length)
            {
                throw new InvalidDataException("A2S_PLAYER response was truncated.");
            }

            var score = BitConverter.ToInt32(response, offset);
            offset += 4;
            var duration = BitConverter.ToSingle(response, offset);
            offset += 4;
            players.Add(new QueryPlayer(name, score, duration, Id: playerIndex.ToString()));
        }

        return players;
    }

    private static bool HasSimplePacketHeader(byte[] response)
    {
        return response.Length >= 4 &&
               response[0] == 0xFF &&
               response[1] == 0xFF &&
               response[2] == 0xFF &&
               response[3] == 0xFF;
    }

    private static bool HasSplitPacketHeader(byte[] response)
    {
        return response.Length >= 4 &&
               response[0] == 0xFE &&
               response[1] == 0xFF &&
               response[2] == 0xFF &&
               response[3] == 0xFF;
    }

    private static string ReadNullTerminatedString(byte[] buffer, ref int offset)
    {
        var start = offset;
        while (offset < buffer.Length && buffer[offset] != 0)
        {
            offset++;
        }

        var value = Encoding.UTF8.GetString(buffer, start, offset - start);
        if (offset < buffer.Length)
        {
            offset++;
        }

        return value;
    }
}

public sealed record SourceA2sInfo(
    string Name,
    string Map,
    string Folder,
    string Game,
    int Players,
    int MaxPlayers,
    string? Version,
    double? QueryDurationMilliseconds = null,
    IReadOnlyList<QueryPlayer>? PlayerRows = null,
    string? DetailMessage = null);
