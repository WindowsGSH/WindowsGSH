using WindowsGSH.Core.Network.Upnp;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class UpnpHttpClientTests
{
    [Fact]
    public void Shared_client_never_uses_a_system_or_configured_proxy()
    {
        Assert.False(UpnpHttpClient.UsesProxyForTesting);
    }
}
