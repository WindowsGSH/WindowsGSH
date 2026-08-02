using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsGSH.Core.Rcon;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SourceRconClientTests
{
    [Fact]
    public async Task ExecuteAsync_authenticates_sends_command_and_reads_response()
    {
        using var server = new FakeRconServer("secret", "saved");
        var client = new SourceRconClient();

        var response = await client.ExecuteAsync(
            "127.0.0.1",
            server.Port,
            "secret",
            "save-all",
            CancellationToken.None);

        Assert.Equal("saved", response);
        Assert.Equal("save-all", await server.CommandTask);
    }

    [Fact]
    public async Task ExecuteAsync_accepts_a_single_auth_response_packet()
    {
        using var server = new FakeRconServer("secret", "No Players Connected...", sendSingleAuthPacket: true);
        var client = new SourceRconClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var response = await client.ExecuteAsync(
            "127.0.0.1",
            server.Port,
            "secret",
            "ListPlayers",
            timeout.Token);

        Assert.Equal("No Players Connected...", response);
        Assert.Equal("ListPlayers", await server.CommandTask);
    }

    [Fact]
    public async Task ExecuteAsync_reads_response_when_terminator_arrives_before_body()
    {
        using var server = new FakeRconServer("secret", "No Players Connected...", sendTerminatorBeforeResponse: true);
        var client = new SourceRconClient();

        var response = await client.ExecuteAsync(
            "127.0.0.1",
            server.Port,
            "secret",
            "ListPlayers",
            CancellationToken.None);

        Assert.Equal("No Players Connected...", response);
        Assert.Equal("ListPlayers", await server.CommandTask);
    }

    [Fact]
    public async Task ExecuteAsync_returns_no_response_when_command_has_no_body()
    {
        using var server = new FakeRconServer("secret", string.Empty, sendTerminatorBeforeResponse: true);
        var client = new SourceRconClient();

        var response = await client.ExecuteAsync(
            "127.0.0.1",
            server.Port,
            "secret",
            "SaveWorld",
            CancellationToken.None);

        Assert.Equal("(no response)", response);
        Assert.Equal("SaveWorld", await server.CommandTask);
    }

    private sealed class FakeRconServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _password;
        private readonly string _response;
        private readonly bool _sendTerminatorBeforeResponse;
        private readonly bool _sendSingleAuthPacket;
        private readonly Task _serverTask;
        private readonly TaskCompletionSource<string> _command = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRconServer(
            string password,
            string response,
            bool sendTerminatorBeforeResponse = false,
            bool sendSingleAuthPacket = false)
        {
            _password = password;
            _response = response;
            _sendTerminatorBeforeResponse = sendTerminatorBeforeResponse;
            _sendSingleAuthPacket = sendSingleAuthPacket;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = RunAsync();
        }

        public int Port { get; }

        public Task<string> CommandTask => _command.Task;

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        private async Task RunAsync()
        {
            try
            {
                using var tcpClient = await _listener.AcceptTcpClientAsync();
                await using var stream = tcpClient.GetStream();

                var auth = await ReadPacketAsync(stream);
                if (auth.Type != 3 || auth.Body != _password)
                {
                    await WritePacketAsync(stream, -1, 2, string.Empty);
                    _command.SetException(new InvalidOperationException("Unexpected auth packet."));
                    return;
                }

                if (!_sendSingleAuthPacket)
                {
                    await WritePacketAsync(stream, auth.Id, 0, string.Empty);
                }

                await WritePacketAsync(stream, auth.Id, 2, string.Empty);

                var command = await ReadPacketAsync(stream);
                _command.SetResult(command.Body);

                var terminator = await ReadPacketAsync(stream);
                if (_sendTerminatorBeforeResponse)
                {
                    await WritePacketAsync(stream, terminator.Id, 0, string.Empty);
                    if (!string.IsNullOrEmpty(_response))
                    {
                        await WritePacketAsync(stream, command.Id, 0, _response);
                    }

                    return;
                }

                await WritePacketAsync(stream, command.Id, 0, _response);
                await WritePacketAsync(stream, terminator.Id, 0, string.Empty);
            }
            catch (Exception ex)
            {
                _command.TrySetException(ex);
            }
        }

        private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var size = 4 + 4 + bodyBytes.Length + 2;
            var packet = new byte[4 + size];
            WriteInt(packet, 0, size);
            WriteInt(packet, 4, id);
            WriteInt(packet, 8, type);
            Buffer.BlockCopy(bodyBytes, 0, packet, 12, bodyBytes.Length);
            await stream.WriteAsync(packet);
        }

        private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream)
        {
            var sizeBytes = await ReadExactlyAsync(stream, 4);
            var size = BitConverter.ToInt32(sizeBytes);
            var packetBytes = await ReadExactlyAsync(stream, size);
            var id = BitConverter.ToInt32(packetBytes, 0);
            var type = BitConverter.ToInt32(packetBytes, 4);
            var body = Encoding.UTF8.GetString(packetBytes, 8, Math.Max(0, size - 10)).TrimEnd('\0');
            return new RconPacket(id, type, body);
        }

        private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
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
}
