using WindowsGSH.Core.Health;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

// Exercises ServerInfoWindow.BuildPortForwardingChecks directly (internal, via InternalsVisibleTo
// on the WindowsGSH project's own AssemblyInfo.cs) - the Tier 5.5 manual port-forwarding
// assistant's port-to-instruction mapping, without needing to instantiate the WPF window or touch
// real network interfaces (localIp is supplied by the caller in production too - see
// ServerInfoWindow.GetLocalIPv4).
public sealed class ServerInfoWindowPortForwardingTests
{
    private const string LocalIp = "192.168.1.20";

    private static ResolvedPort Port(
        string id,
        string name,
        PortProtocol protocol,
        ResolvedPortStatus status,
        int? port,
        int rangeSize = 1,
        bool required = true,
        bool openExternally = true,
        string? error = null,
        PortResolutionFailureReason failureReason = PortResolutionFailureReason.None) =>
        new(id, name, protocol, status, port, rangeSize, required, openExternally, error,
            FailureReason: failureReason);

    [Fact]
    public void Resolved_tcp_port_produces_the_expected_forward_instruction()
    {
        var ports = new[] { Port("game", "Game Port", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25565) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Game Port");
        Assert.Equal(ServerHealthSeverity.Info, check.Severity);
        Assert.Contains("Forward TCP 25565 from your router to 192.168.1.20:25565", check.Message);
    }

    [Fact]
    public void Resolved_udp_port_mentions_udp_specifically()
    {
        var ports = new[] { Port("query", "Query Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, 7777) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Query Port");
        Assert.Contains("Forward UDP 7777", check.Message);
        Assert.DoesNotContain("TCP", check.Message.Replace("Forward UDP", ""));
    }

    [Fact]
    public void Resolved_both_protocol_port_mentions_tcp_and_udp()
    {
        var ports = new[] { Port("voice", "Voice Port", PortProtocol.Both, ResolvedPortStatus.Resolved, 10000) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Voice Port");
        Assert.Contains("TCP+UDP", check.Message);
    }

    [Fact]
    public void Resolved_either_protocol_port_flags_the_transport_as_unknown()
    {
        var ports = new[] { Port("alt", "Alt Port", PortProtocol.Either, ResolvedPortStatus.Resolved, 9000) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Alt Port");
        Assert.Equal(ServerHealthSeverity.Warning, check.Severity);
        Assert.Contains("TCP or UDP", check.Message);
        Assert.Contains("unknown", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("forward only", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forward both", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_port_range_is_formatted_as_a_range_not_a_single_port()
    {
        var ports = new[] { Port("voice", "Voice Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, 7777, rangeSize: 3) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Voice Port");
        Assert.Contains("7777-7779", check.Message);
        Assert.Contains(LocalIp, check.Message);
    }

    [Fact]
    public void A_management_only_port_is_warned_against_instead_of_given_a_forward_instruction()
    {
        var ports = new[]
        {
            Port("rcon", "RCON", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25575, openExternally: false)
        };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: RCON");
        Assert.Equal(ServerHealthSeverity.Warning, check.Severity);
        Assert.Contains("should not be forwarded", check.Message);
        Assert.DoesNotContain("Forward", check.Message);
    }

    [Fact]
    public void An_invalid_port_uses_a_sanitized_warning_without_the_resolver_error()
    {
        var ports = new[]
        {
            Port("game", "Game Port", PortProtocol.Tcp, ResolvedPortStatus.Invalid, null,
                error: "Port game has an invalid value: apiToken=hunter2-test-secret.")
        };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Game Port");
        Assert.Equal(ServerHealthSeverity.Warning, check.Severity);
        Assert.Contains("valid value could not be resolved", check.Message);
        Assert.DoesNotContain("hunter2-test-secret", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overlapping_port_gets_a_targeted_safe_conflict_warning()
    {
        var ports = new[]
        {
            Port("game", "Game Port", PortProtocol.Tcp, ResolvedPortStatus.Invalid, null,
                error: "Port game overlaps secret-shaped-id=hunter2.",
                failureReason: PortResolutionFailureReason.Overlap)
        };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Game Port");
        Assert.Equal(ServerHealthSeverity.Warning, check.Severity);
        Assert.Contains("overlaps another port", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_port_produces_an_informational_no_action_needed_check()
    {
        var ports = new[] { Port("query", "Query Port", PortProtocol.Udp, ResolvedPortStatus.Unresolved, null, required: false) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        var check = Assert.Single(checks, c => c.Name == "Port forwarding: Query Port");
        Assert.Equal(ServerHealthSeverity.Info, check.Severity);
        Assert.Contains("not currently configured", check.Message);
    }

    [Fact]
    public void General_guidance_notes_are_always_present_alongside_per_port_checks()
    {
        var ports = new[] { Port("game", "Game Port", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25565) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, LocalIp);

        Assert.Contains(checks, c => c.Name == "Port forwarding: dynamic IP addresses");
        Assert.Contains(checks, c => c.Name == "Port forwarding: CGNAT and double NAT");
        Assert.Contains(checks, c => c.Name == "Port forwarding: VPN adapters");
        Assert.Contains(checks, c => c.Name == "Port forwarding: RCON and admin interfaces");
        Assert.Contains(checks, c => c.Name == "Port forwarding: WindowsGSH's own web dashboard");
        Assert.Contains(checks, c => c.Name == "Port forwarding: other configured servers");
        Assert.Contains(checks, c =>
            c.Name == "Port forwarding: RCON and admin interfaces" &&
            c.Severity == ServerHealthSeverity.Info);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("100.64.1.2")]
    [InlineData("8.8.8.8")]
    [InlineData("::1")]
    public void Unusable_destination_does_not_generate_a_copyable_forward_instruction(string localIp)
    {
        var ports = new[] { Port("game", "Game Port", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25565) };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, localIp);

        Assert.Contains(checks, c =>
            c.Name == "Port forwarding: destination address" &&
            c.Severity == ServerHealthSeverity.Warning);
        var portCheck = Assert.Single(checks, c => c.Name == "Port forwarding: Game Port");
        Assert.DoesNotContain("Forward TCP", portCheck.Message);
        Assert.DoesNotContain($"{localIp}:25565", portCheck.Message);
    }

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("172.16.0.5", "172.16.0.5")]
    [InlineData("172.31.255.254", "172.31.255.254")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    public void Private_lan_addresses_are_accepted_and_normalized(string value, string expected)
    {
        Assert.True(ServerInfoWindow.TryNormalizePrivateLanIPv4(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Management_only_ports_do_not_require_a_forwarding_destination()
    {
        var ports = new[]
        {
            Port("rcon", "RCON", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25575, openExternally: false)
        };

        var checks = ServerInfoWindow.BuildPortForwardingChecks(ports, "Unknown");

        Assert.DoesNotContain(checks, c => c.Name == "Port forwarding: destination address");
        Assert.Contains(checks, c =>
            c.Name == "Port forwarding: RCON" &&
            c.Severity == ServerHealthSeverity.Warning);
    }

    [Fact]
    public void MergePortForwardingChecks_replaces_old_port_forwarding_results_but_preserves_other_checks()
    {
        var local = new ServerHealthCheck("Network", "Port conflicts", ServerHealthSeverity.Pass, "No conflicts.");
        var externalReachability = new ServerHealthCheck("Network", "External reachability", ServerHealthSeverity.Info, "Skipped.");
        var oldPortForwarding = new ServerHealthCheck("Network", "Port forwarding: Game Port", ServerHealthSeverity.Info, "Old instruction.");
        var newPortForwarding = new ServerHealthCheck("Network", "Port forwarding: Game Port", ServerHealthSeverity.Info, "New instruction.");

        var merged = ServerInfoWindow.MergePortForwardingChecks(
            [local, externalReachability, oldPortForwarding],
            [newPortForwarding]);

        Assert.Equal(3, merged.Count);
        Assert.Contains(local, merged);
        Assert.Contains(externalReachability, merged);
        Assert.Contains(newPortForwarding, merged);
        Assert.DoesNotContain(oldPortForwarding, merged);
    }

    [Fact]
    public void Health_tab_excludes_results_owned_by_the_networking_tab()
    {
        var network = new ServerHealthCheck(
            "Network",
            "Port forwarding: Game Port",
            ServerHealthSeverity.Info,
            "Forward TCP port 25565.");
        var stability = new ServerHealthCheck(
            "Stability",
            "Recent crashes",
            ServerHealthSeverity.Pass,
            "No recent crashes.");
        var module = new ServerHealthCheck(
            "Module",
            "Configuration",
            ServerHealthSeverity.Warning,
            "A configuration value needs attention.");

        var healthChecks = ServerInfoWindow.SelectHealthTabChecks([network, stability, module]);

        Assert.Equal([stability, module], healthChecks);
        Assert.DoesNotContain(network, healthChecks);
    }

    [Fact]
    public void Networking_summary_contains_copyable_results()
    {
        var network = new ServerHealthCheck(
            "Network",
            "Port forwarding: Game Port",
            ServerHealthSeverity.Info,
            "Forward TCP 25565 from your router to 192.168.1.20:25565.");

        var summary = ServerInfoWindow.BuildNetworkingSummary("Test Server", [network]);

        Assert.Contains("WindowsGSH Networking Summary - Test Server", summary);
        Assert.Contains("[INFO] Port forwarding: Game Port", summary);
        Assert.Contains("192.168.1.20:25565", summary);
    }
}
