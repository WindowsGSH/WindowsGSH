using System.Net;
using WindowsGSH.Core.Health;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class PortDiagnosticsServiceTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static PortDiagnosticsService Build(
        Func<string, FirewallQueryResult> ruleExists,
        IReadOnlyList<IPEndPoint> listeners)
    {
        return new PortDiagnosticsService(
            firewallCheckerFactory: () => new FakeFirewallChecker(ruleExists),
            getTcpListeners: () => listeners);
    }

    private static PortCheckSpec Spec(
        string serverName = "S",
        string serverId = "s1",
        int port = 27015,
        string bindAddress = "",
        bool expectsTcpListener = false)
        => new(serverName, serverId, port, bindAddress, expectsTcpListener);

    private sealed class FakeFirewallChecker(Func<string, FirewallQueryResult> fn) : IPortFirewallChecker
    {
        public FirewallQueryResult CheckRule(string ruleName) => fn(ruleName);
    }

    // ── P2 fix 1: explicit spec list ─────────────────────────────────────────

    [Fact]
    public void Empty_spec_list_returns_no_rows()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []);
        Assert.Empty(svc.CheckPorts([]));
    }

    [Fact]
    public void One_spec_produces_tcp_and_udp_rows()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []);
        var rows = svc.CheckPorts([Spec()]);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Protocol == "TCP");
        Assert.Single(rows, r => r.Protocol == "UDP");
        Assert.All(rows, r => Assert.Equal("S", r.ServerName));
        Assert.All(rows, r => Assert.Equal("27015", r.Port));
    }

    [Fact]
    public void Two_specs_with_different_ports_produce_four_rows()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []);
        var rows = svc.CheckPorts([Spec(port: 27015), Spec(port: 27016)]);

        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Port == "27015"));
        Assert.Equal(2, rows.Count(r => r.Port == "27016"));
    }

    // ── P2 fix 2: listener address matching ──────────────────────────────────

    [Fact]
    public void Wildcard_bind_address_matches_any_listener_on_port()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Loopback, 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(bindAddress: "0.0.0.0", expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Listening", tcp.ListeningStatus);
    }

    [Fact]
    public void Configured_address_matches_exact_listener()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Parse("192.168.1.10"), 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(bindAddress: "192.168.1.10", expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Listening", tcp.ListeningStatus);
        Assert.False(tcp.IsWarning);
    }

    [Fact]
    public void Configured_address_does_not_match_listener_on_different_address()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Parse("10.0.0.1"), 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(bindAddress: "192.168.1.10", expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Not listening", tcp.ListeningStatus);
        Assert.True(tcp.IsWarning);
    }

    [Fact]
    public void Wildcard_listener_covers_any_configured_address()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Any, 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(bindAddress: "192.168.1.10", expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Listening", tcp.ListeningStatus);
        Assert.False(tcp.IsWarning);
    }

    [Fact]
    public void Empty_bind_address_falls_back_to_port_only_match()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Parse("10.5.0.1"), 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(bindAddress: "", expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Listening", tcp.ListeningStatus);
    }

    // ── P2 fix 3: UDP-only / unknown-protocol warning suppression ────────────

    [Fact]
    public void ExpectsTcpListener_false_does_not_warn_when_tcp_not_listening()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []); // firewall OK, no listeners

        var tcp = svc.CheckPorts([Spec(expectsTcpListener: false)])
                     .Single(r => r.Protocol == "TCP");

        Assert.Equal("Not listening", tcp.ListeningStatus);
        Assert.False(tcp.IsWarning); // UDP-only game: no warning even without TCP listener
    }

    [Fact]
    public void ExpectsTcpListener_true_warns_when_tcp_not_listening()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []); // firewall OK, no listeners

        var tcp = svc.CheckPorts([Spec(expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.True(tcp.IsWarning);
    }

    [Fact]
    public void ExpectsTcpListener_true_no_warning_when_listening()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Any, 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var tcp = svc.CheckPorts([Spec(expectsTcpListener: true)])
                     .Single(r => r.Protocol == "TCP");

        Assert.False(tcp.IsWarning);
    }

    // ── firewall status mapping ───────────────────────────────────────────────

    [Fact]
    public void Firewall_rule_exists_maps_to_rule_exists_text()
    {
        var svc = Build(_ => FirewallQueryResult.Exists, []);
        var rows = svc.CheckPorts([Spec()]);
        Assert.All(rows, r => Assert.Equal("Rule exists", r.FirewallStatus));
    }

    [Fact]
    public void Firewall_rule_missing_maps_to_no_rule_and_is_warning()
    {
        var svc = Build(_ => FirewallQueryResult.Missing, []);
        var rows = svc.CheckPorts([Spec()]);
        Assert.All(rows, r => Assert.Equal("No rule", r.FirewallStatus));
        Assert.All(rows, r => Assert.True(r.IsWarning));
    }

    [Fact]
    public void Requires_admin_maps_to_requires_admin_text()
    {
        var svc = Build(_ => FirewallQueryResult.RequiresAdmin, []);
        var rows = svc.CheckPorts([Spec()]);
        Assert.All(rows, r => Assert.Equal("Requires admin", r.FirewallStatus));
    }

    [Fact]
    public void Null_checker_factory_maps_to_unavailable()
    {
        var svc = new PortDiagnosticsService(
            firewallCheckerFactory: () => null,
            getTcpListeners: () => []);

        var rows = svc.CheckPorts([Spec()]);
        Assert.All(rows, r => Assert.Equal("Unavailable", r.FirewallStatus));
    }

    // ── UDP row ───────────────────────────────────────────────────────────────

    [Fact]
    public void Udp_listening_status_is_always_cannot_verify()
    {
        var listeners = new[] { new IPEndPoint(IPAddress.Any, 27015) };
        var svc = Build(_ => FirewallQueryResult.Exists, listeners);

        var udp = svc.CheckPorts([Spec()]).Single(r => r.Protocol == "UDP");

        Assert.Equal("Cannot verify", udp.ListeningStatus);
    }
}
