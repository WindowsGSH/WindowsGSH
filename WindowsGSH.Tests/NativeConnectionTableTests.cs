using System.Net;
using System.Net.Sockets;
using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class NativeConnectionTableTests
{
    [Fact]
    public void GetTcpListenersWithOwners_finds_a_real_listener_owned_by_this_process()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var owners = NativeConnectionTable.GetTcpListenersWithOwners();

        // A real API failure here (rather than a genuine miss) would itself be a finding, so assert
        // the lookup actually succeeded (non-null) before asserting on its contents - a null result
        // failing this Assert.NotNull would be a clearer signal than a confusing null-reference
        // failure on the Contains call below.
        Assert.NotNull(owners);
        Assert.Contains(owners, owner =>
            owner.Endpoint.Port == port &&
            owner.OwningProcessId == Environment.ProcessId);
    }

    [Fact]
    public void GetUdpListenersWithOwners_finds_a_real_socket_owned_by_this_process()
    {
        using var client = new UdpClient(0, AddressFamily.InterNetwork);
        var port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var owners = NativeConnectionTable.GetUdpListenersWithOwners();

        Assert.NotNull(owners);
        Assert.Contains(owners, owner =>
            owner.Endpoint.Port == port &&
            owner.OwningProcessId == Environment.ProcessId);
    }
}
