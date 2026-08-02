using System.Net.WebSockets;
using WindowsGSH.Core.Web;

namespace WindowsGSH.Tests;

internal static class WebTestExtensions
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public static string LastStartErrorForTests(this WebHostService _) =>
        WebHostService.LastStartError ?? "Web host did not start and supplied no error.";

    public static async Task ConnectWithTimeoutAsync(ClientWebSocket socket, Uri uri)
    {
        using var timeout = new CancellationTokenSource(ConnectionTimeout);
        await socket.ConnectAsync(uri, timeout.Token);
    }

    public static async Task StopWithTimeoutForTestsAsync(this WebHostService host)
    {
        using var timeout = new CancellationTokenSource(ConnectionTimeout);
        await host.StopAsync(timeout.Token);
    }
}
