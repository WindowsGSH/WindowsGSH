using System.Net;
using WindowsGSH;
using WindowsGSH.Core.Network;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class AppSettingsViewPublicIpValidationTests
{
    [Fact]
    public void External_reachability_consent_decline_does_not_enable_or_acknowledge()
    {
        var result = AppSettingsView.ResolveExternalReachabilityConsent(
            requestedEnable: true,
            acknowledged: false,
            confirm: () => false);

        Assert.False(result.Enabled);
        Assert.False(result.Acknowledged);
    }

    [Fact]
    public void External_reachability_consent_acceptance_enables_and_acknowledges()
    {
        var result = AppSettingsView.ResolveExternalReachabilityConsent(
            requestedEnable: true,
            acknowledged: false,
            confirm: () => true);

        Assert.True(result.Enabled);
        Assert.True(result.Acknowledged);
    }

    [Fact]
    public void Configure_preserves_the_original_public_signature()
    {
        var configureOverloads = typeof(AppSettingsView)
            .GetMethods()
            .Where(method => method.Name == nameof(AppSettingsView.Configure))
            .ToArray();

        Assert.Contains(configureOverloads, method => method.GetParameters().Length == 14);
        Assert.Contains(configureOverloads, method => method.GetParameters().Length == 15);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    public void IsNonPublicAddress_flags_loopback_private_and_link_local(string address)
    {
        // ::ffff:x.x.x.x is an IPv4-mapped IPv6 address. IsLoopback/IsIPv6LinkLocal etc. don't
        // recognize the mapped form on their own, so IsNonPublicAddress must unwrap it with
        // MapToIPv4() first or these bypass the check entirely (::ffff:169.254.169.254 in
        // particular is the mapped form of a common cloud metadata endpoint).
        Assert.True(PublicIpEndpointPolicy.IsNonPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsNonPublicAddress_allows_public_addresses(string address)
    {
        Assert.False(PublicIpEndpointPolicy.IsNonPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://[::1]/")]
    [InlineData("https://[fc00::1]/")]
    [InlineData("https://[fe80::1]/")]
    public void IsAllowedEndpoint_rejects_bracketed_ipv6_literals_for_non_public_hosts(string url)
    {
        // Regression test: Uri.Host keeps the brackets for IPv6 literals (e.g. "[::1]"), which
        // would make a naive IPAddress.TryParse(uri.Host, ...) call skip the non-public check
        // entirely for these hosts. IsAllowedEndpoint must use DnsSafeHost instead so this
        // still gets caught.
        Assert.False(PublicIpEndpointPolicy.IsAllowedEndpoint(new Uri(url)));
    }

    [Theory]
    [InlineData("https://api.ipify.org/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    public void IsAllowedEndpoint_allows_public_https_hosts(string url)
    {
        Assert.True(PublicIpEndpointPolicy.IsAllowedEndpoint(new Uri(url)));
    }

    [Fact]
    public void IsAllowedEndpoint_rejects_non_https_scheme()
    {
        Assert.False(PublicIpEndpointPolicy.IsAllowedEndpoint(new Uri("http://api.ipify.org/")));
    }

    [Fact]
    public async Task IsAllowedEndpointAsync_allows_literal_public_ip_hosts()
    {
        Assert.True(await PublicIpEndpointPolicy.IsAllowedEndpointAsync(new Uri("https://[2606:4700:4700::1111]/")));
        Assert.False(await PublicIpEndpointPolicy.IsAllowedEndpointAsync(new Uri("https://[::1]/")));
    }

    [Fact]
    public async Task IsAllowedEndpointAsync_rejects_dns_name_resolving_to_loopback()
    {
        // "localhost" resolves to 127.0.0.1/::1 on every machine without a real network lookup,
        // so this exercises the DNS-resolution bypass (P3-04 follow-up) deterministically: a DNS
        // name host (not a literal IP) that resolves internally must still be rejected, which the
        // literal-IP-only IsAllowedEndpoint check would have missed.
        Assert.False(await PublicIpEndpointPolicy.IsAllowedEndpointAsync(new Uri("https://localhost/")));
    }

    [Fact]
    public async Task IsAllowedEndpointAsync_rejects_non_https_scheme()
    {
        Assert.False(await PublicIpEndpointPolicy.IsAllowedEndpointAsync(new Uri("http://api.ipify.org/")));
    }

    [Fact]
    public async Task IsAllowedEndpointAsync_propagates_caller_cancellation_for_dns_names()
    {
        // A pre-cancelled caller token must still surface as a cancellation, not be swallowed as
        // "false" — only the internal DNS-resolution timeout is meant to fail closed silently.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublicIpEndpointPolicy.IsAllowedEndpointAsync(new Uri("https://api.ipify.org/"), cts.Token));
    }
}
