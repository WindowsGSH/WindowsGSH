using WindowsGSH.Core.Health;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Network;
using Xunit;

namespace WindowsGSH.Tests;

// Exercises ServerInfoWindow.BuildExternalReachabilityChecks directly (internal, via
// InternalsVisibleTo on the WindowsGSH project's own AssemblyInfo.cs) - the outcome-to-
// severity/message mapping for the Tier 5.3c external reachability check, without needing to
// instantiate the WPF window itself.
public sealed class ServerInfoWindowExternalReachabilityTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void Probe_button_requires_settings_consent_and_no_in_flight_check(
        bool enabledInSettings,
        bool consentAcknowledged,
        bool checkInFlight,
        bool expected)
    {
        Assert.Equal(
            expected,
            ServerInfoWindow.CanEnableExternalReachabilityButton(
                enabledInSettings,
                consentAcknowledged,
                checkInFlight));
    }

    [Fact]
    public void Reachable_port_produces_a_Pass_check()
    {
        var result = new ExternalReachabilityCheckResult(
            ExternalReachabilityOutcome.Success,
            "ok",
            [new ExternalPortReachabilityResult(25565, ExternalPortReachability.Reachable, 12)],
            "ipv4");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 1);

        var check = Assert.Single(checks);
        Assert.Equal(ServerHealthSeverity.Pass, check.Severity);
        Assert.Equal("Network", check.Category);
        Assert.Equal("External reachability: port 25565", check.Name);
        Assert.Contains("IPV4", check.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reachable", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ExternalPortReachability.Refused, ServerHealthSeverity.Warning)]
    [InlineData(ExternalPortReachability.TimedOut, ServerHealthSeverity.Warning)]
    [InlineData(ExternalPortReachability.Unavailable, ServerHealthSeverity.Info)]
    public void Non_reachable_statuses_never_report_a_confident_Pass_or_Fail(
        ExternalPortReachability status,
        ServerHealthSeverity expectedSeverity)
    {
        // None of these ever claim the port is definitively closed (Fail) - only Warning
        // (inconclusive/suspicious) or Info (server-side couldn't determine this one port).
        var result = new ExternalReachabilityCheckResult(
            ExternalReachabilityOutcome.Success,
            "ok",
            [new ExternalPortReachabilityResult(25565, status, 12)],
            "ipv4");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 1);

        var check = Assert.Single(checks);
        Assert.Equal(expectedSeverity, check.Severity);
        Assert.NotEqual(ServerHealthSeverity.Fail, check.Severity);
    }

    [Fact]
    public void Truncation_adds_a_separate_informational_check()
    {
        var result = new ExternalReachabilityCheckResult(
            ExternalReachabilityOutcome.Success,
            "ok",
            [new ExternalPortReachabilityResult(80, ExternalPortReachability.Reachable, 1)],
            "ipv4");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: true, totalEligiblePorts: 9);

        Assert.Equal(2, checks.Count);
        var note = checks[^1];
        Assert.Equal(ServerHealthSeverity.Info, note.Severity);
        Assert.Contains("first 1 of 9", note.Message);
        Assert.Contains("limit of 5", note.Message);
    }

    [Fact]
    public void BuildExternalReachabilityPortSelection_tests_only_known_tcp_and_records_exclusions()
    {
        var ports = new List<ResolvedPort>
        {
            new("game", "Game", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25565, 1, true, true),
            new("query", "Query", PortProtocol.Udp, ResolvedPortStatus.Resolved, 27015, 1, true, true),
            new("voice", "Voice", PortProtocol.Both, ResolvedPortStatus.Resolved, 10000, 3, true, true),
            new("web", "Web", PortProtocol.Tcp, ResolvedPortStatus.Unresolved, null, 1, true, true),
            new("alt", "Alt", PortProtocol.Either, ResolvedPortStatus.Resolved, 9000, 2, true, true),
            new("private-rcon", "Private RCON", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25575, 1, true, false),
            new("smtp", "SMTP", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25, 1, true, true),
            new("invalid-range", "Invalid range", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 65535, 2, true, true)
        };

        var selection = ServerInfoWindow.BuildExternalReachabilityPortSelection(ports);

        Assert.Equal([10000, 10001, 10002, 25565], selection.TcpPorts);
        Assert.Equal(2, selection.UdpDeclarations);
        Assert.Equal(1, selection.UnknownTransportDeclarations);
        Assert.Equal(2, selection.UnresolvedDeclarations);
        Assert.Equal(1, selection.BlockedPorts);
    }

    [Fact]
    public void BuildExternalReachabilityPortSelection_prioritizes_required_ports_before_optional_ports()
    {
        var ports = new List<ResolvedPort>
        {
            new("optional-low", "Optional low", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 80, 1, false, true),
            new("required-high", "Required high", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 25565, 1, true, true),
            new("optional-mid", "Optional mid", PortProtocol.Tcp, ResolvedPortStatus.Resolved, 443, 1, false, true)
        };

        var selection = ServerInfoWindow.BuildExternalReachabilityPortSelection(ports);

        Assert.Equal([25565, 80, 443], selection.TcpPorts);
    }

    [Fact]
    public void BuildExternalPortSelectionNotes_explains_udp_unknown_unresolved_and_blocked_ports()
    {
        var selection = new ServerInfoWindow.ExternalReachabilityPortSelection(
            [25565],
            UdpDeclarations: 2,
            UnknownTransportDeclarations: 1,
            UnresolvedDeclarations: 3,
            BlockedPorts: 1);

        var checks = ServerInfoWindow.BuildExternalPortSelectionNotes(selection);

        Assert.Equal(4, checks.Count);
        Assert.Contains(checks, check => check.Name.Contains("UDP") &&
            check.Message.Contains("cannot be reliably determined"));
        Assert.Contains(checks, check => check.Name.Contains("unknown transport"));
        Assert.Contains(checks, check => check.Name.Contains("incomplete port resolution"));
        Assert.Contains(checks, check => check.Name.Contains("blocked port") &&
            check.Message.Contains("25"));
    }

    [Fact]
    public void MergeExternalReachabilityChecks_replaces_old_external_results_but_preserves_local_checks()
    {
        var local = new ServerHealthCheck(
            "Network",
            "Configured port listener",
            ServerHealthSeverity.Pass,
            "Local listener found.");
        var oldExternal = new ServerHealthCheck(
            "Network",
            "External reachability: port 25565",
            ServerHealthSeverity.Warning,
            "Old warning.");
        var newExternal = new ServerHealthCheck(
            "Network",
            "External reachability: port 25565",
            ServerHealthSeverity.Pass,
            "New result.");

        var merged = ServerInfoWindow.MergeExternalReachabilityChecks(
            [local, oldExternal],
            [newExternal]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(local, merged);
        Assert.Contains(newExternal, merged);
        Assert.DoesNotContain(oldExternal, merged);
    }

    [Fact]
    public void Skipped_outcome_produces_a_single_informational_check_with_the_service_message()
    {
        var result = ExternalReachabilityCheckResult.Skipped("External reachability checks are disabled.");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 0);

        var check = Assert.Single(checks);
        Assert.Equal(ServerHealthSeverity.Info, check.Severity);
        Assert.Equal("External reachability checks are disabled.", check.Message);
    }

    [Fact]
    public void NoPortsToTest_outcome_produces_a_single_informational_check()
    {
        var result = ExternalReachabilityCheckResult.NoPortsToTest("No eligible TCP ports were available to test.");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 0);

        var check = Assert.Single(checks);
        Assert.Equal(ServerHealthSeverity.Info, check.Severity);
    }

    [Fact]
    public void Unavailable_outcome_never_claims_the_port_is_closed()
    {
        var result = ExternalReachabilityCheckResult.Unavailable(
            "The external reachability service could not be reached.");

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 1);

        var check = Assert.Single(checks);
        Assert.NotEqual(ServerHealthSeverity.Fail, check.Severity);
        Assert.DoesNotContain("closed", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RateLimited_outcome_includes_the_retry_after_duration_in_the_message()
    {
        var result = ExternalReachabilityCheckResult.RateLimited(
            "The reachability service is rate-limiting requests; try again shortly.",
            TimeSpan.FromSeconds(42));

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 1);

        var check = Assert.Single(checks);
        Assert.Equal(ServerHealthSeverity.Info, check.Severity);
        Assert.Contains("42", check.Message);
    }

    [Fact]
    public void RateLimited_outcome_without_a_retry_after_value_still_produces_a_sensible_message()
    {
        var result = ExternalReachabilityCheckResult.RateLimited(
            "The reachability service is rate-limiting requests; try again shortly.",
            retryAfter: null);

        var checks = ServerInfoWindow.BuildExternalReachabilityChecks(result, truncated: false, totalEligiblePorts: 1);

        var check = Assert.Single(checks);
        Assert.Contains("try again", check.Message, StringComparison.OrdinalIgnoreCase);
    }
}
