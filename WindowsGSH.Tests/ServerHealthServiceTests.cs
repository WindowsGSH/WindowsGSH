using System.Diagnostics;
using System.Net;
using System.Text.Json;
using WindowsGSH.Core.Health;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerHealthServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.HealthTests", Guid.NewGuid().ToString("N"));
    private const string PowerShellPath = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe";

    [Fact]
    public async Task Evaluate_reports_cross_category_failures_and_warnings()
    {
        var server = CreateServer("health-module", port: "25565", status: ServerRuntimeStatus.Running) with
        {
            QueryDetailMessage = "Query timed out."
        };
        WriteConfig(server, """{"settings":{},"backup":{"paths":[]},"java":{"runtimePath":"C:\\missing\\java.exe"}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: true);
        var conflict = CreateServer("other", port: "25565");
        var java = new JavaRuntimeManager(new JavaRuntimeLocator(
            _ => false,
            _ => null,
            _ => throw new InvalidOperationException()));
        var service = new ServerHealthService(java);

        var report = await service.EvaluateAsync(new ServerHealthRequest(
            server,
            descriptor,
            [server, conflict],
            [new FirewallRuleStatus("rule", "port", "Game port", 25565, FirewallProtocol.Tcp, Exists: false)],
            PublicIpTrackingEnabled: true));

        Assert.Equal(ServerHealthSeverity.Fail, report.OverallSeverity);
        Assert.Contains(report.Checks, check => check.Name == "Executable" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Provenance" && check.Severity == ServerHealthSeverity.Warning);
        Assert.Contains(report.Checks, check => check.Name == "Port conflicts" && check.Severity == ServerHealthSeverity.Warning);
        Assert.Contains(report.Checks, check => check.Category == "Firewall" && check.Severity == ServerHealthSeverity.Warning);
        Assert.Contains(report.Checks, check => check.Category == "Query" && check.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Checks, check => check.Category == "Backup" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Java" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Public IP" && check.Severity == ServerHealthSeverity.Warning);
        Assert.Contains(report.Checks, check => check.Category == "Module readiness");
    }

    [Fact]
    public async Task Evaluate_reports_missing_module_and_invalid_config_without_throwing()
    {
        var server = CreateServer("missing-module", port: "not-a-port");
        Directory.CreateDirectory(server.ServerFolder);
        File.WriteAllText(server.ConfigPath, "{ invalid");

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, null, [server]));

        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Fail);
    }

    [Fact]
    public async Task Healthy_server_produces_passes_and_known_public_ip()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{},"backup":{"paths":[]} }""");
        Directory.CreateDirectory(server.InstallPath);
        File.WriteAllText(Path.Combine(server.InstallPath, "server.exe"), "test");
        Directory.CreateDirectory(Path.Combine(server.InstallPath, "world"));
        var module = new HealthModule(requiresJava: false);
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            server,
            descriptor,
            [server],
            [
                new FirewallRuleStatus("tcp", "port", "Game port", 25565, FirewallProtocol.Tcp, true),
                new FirewallRuleStatus("udp", "port", "Game port", 25565, FirewallProtocol.Udp, true)
            ],
            PublicIpTrackingEnabled: true,
            LastKnownPublicIp: "203.0.113.10",
            LastPublicIpCheckedAt: DateTimeOffset.UtcNow));

        Assert.DoesNotContain(report.Checks, check => check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Executable" && check.Severity == ServerHealthSeverity.Pass);
        Assert.Contains(report.Checks, check => check.Category == "Firewall" && check.Severity == ServerHealthSeverity.Pass);
        Assert.Contains(report.Checks, check => check.Name == "Public IP" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public void Support_summary_redacts_secret_like_config_values()
    {
        var server = CreateServer("health-module");
        WriteConfig(server, """
            {
              "settings": {
                "password": "hunter2",
                "apiToken": "token-value",
                "name": "Visible"
              },
              "discord": {
                "webhookUrl": "https://secret.invalid/hook"
              }
            }
            """);
        var request = new ServerHealthRequest(server, null, [server]);
        var report = new ServerHealthReport(server.Id, server.Name, []);

        var summary = new ServerHealthService().BuildSupportSummary(request, report);

        Assert.DoesNotContain("hunter2", summary);
        Assert.DoesNotContain("token-value", summary);
        Assert.DoesNotContain("secret.invalid", summary);
        Assert.Contains("Visible", summary);
        Assert.Contains("[REDACTED]", summary);
    }

    [Fact]
    public void Support_summary_redacts_module_password_fields_regardless_of_key_name()
    {
        var server = CreateServer("health-module");
        WriteConfig(server, """{"settings":{"adminCode":"should-not-leak","name":"Visible"}}""");
        var module = new HealthModule(requiresJava: false);
        var request = new ServerHealthRequest(server, Descriptor(module, changed: false), [server]);

        var summary = new ServerHealthService().BuildSupportSummary(
            request,
            new ServerHealthReport(server.Id, server.Name, []));

        Assert.DoesNotContain("should-not-leak", summary);
        Assert.Contains("\"adminCode\": \"[REDACTED]\"", summary);
        Assert.Contains("Visible", summary);
    }

    [Fact]
    public async Task Port_conflicts_include_additional_module_port_fields()
    {
        var first = CreateServer("health-module", port: "25565");
        var second = CreateServer("health-module", port: "25566");
        WriteConfig(first, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(second, """{"settings":{"network.port":25566,"network.queryPort":27015}}""");
        Directory.CreateDirectory(first.InstallPath);
        File.WriteAllText(Path.Combine(first.InstallPath, "server.exe"), "test");
        var module = new HealthModule(requiresJava: false);
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            first,
            descriptor,
            [first, second],
            ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("Query port", StringComparison.OrdinalIgnoreCase) &&
            check.Message.Contains("27015", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_uses_declared_ports_when_the_module_declares_them()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        // IServerPortResolver's own Name ("Game Port"/"Query Port"), not the config field's Label
        // ("Game port"/"Query port") - confirms the resolver path actually ran, not the fallback.
        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("Game Port=25565", StringComparison.Ordinal) &&
            check.Message.Contains("Query Port=27015", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Checks, check => check.Category == "Network" && check.Name.StartsWith("Port: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_surfaces_an_invalid_declared_port_as_its_own_check()
    {
        // network.port is Required by both the config field and the "game" ServerPortDefinition,
        // but is missing from settings entirely - the resolver reports this as Invalid, which
        // should surface as its own structured check, not just silently fall through to "no valid
        // configured ports were found" the way the old config-field-only scan would have.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port: Game Port" &&
            check.Severity == ServerHealthSeverity.Fail);
    }

    [Fact]
    public async Task Port_conflicts_are_detected_between_a_declared_port_module_and_a_config_field_only_module()
    {
        // Cross-server conflict detection has to keep working across the two port models while the
        // real module catalog migrates gradually - one server using the new declared-ports path,
        // the other still on the old config-field scan, sharing a port number.
        var declared = CreateServer("port-aware-module", port: "25565");
        var legacy = CreateServer("health-module", port: "25566");
        WriteConfig(declared, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(legacy, """{"settings":{"network.port":25565,"network.queryPort":27016}}""");
        var declaredModule = new PortAwareHealthModule();
        var legacyModule = new HealthModule(requiresJava: false);
        var declaredDescriptor = Descriptor(declaredModule, changed: false);
        var legacyDescriptor = Descriptor(legacyModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            declared,
            declaredDescriptor,
            [declared, legacy],
            ModuleDescriptors: [declaredDescriptor, legacyDescriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("25565", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Different_protocol_declared_ports_on_the_same_number_do_not_conflict()
    {
        var tcpServer = CreateServer("tcp-conflict-module", port: "7777");
        var udpServer = CreateServer("udp-conflict-module", port: "7777");
        WriteConfig(tcpServer, """{"settings":{"game.port":7777}}""");
        WriteConfig(udpServer, """{"settings":{"game.port":7777}}""");
        var tcpModule = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Tcp, ConfigField: "game.port", Required: true)], "tcp-conflict-module");
        var udpModule = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true)], "udp-conflict-module");
        var tcpDescriptor = Descriptor(tcpModule, changed: false);
        var udpDescriptor = Descriptor(udpModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            tcpServer,
            tcpDescriptor,
            [tcpServer, udpServer],
            ModuleDescriptors: [tcpDescriptor, udpDescriptor]));

        Assert.Contains(report.Checks, check => check.Name == "Port conflicts" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Ports_excluded_from_listener_checks_still_participate_in_conflict_detection()
    {
        var selected = CreateServer("excluded-listener-port-module", port: "7777");
        var other = CreateServer("other-conflict-module", port: "7777");
        WriteConfig(selected, """{"settings":{"game.port":7777}}""");
        WriteConfig(other, """{"settings":{"game.port":7777}}""");
        var selectedModule = new ConfigurablePortsModule(
            [new(
                "game",
                "Logical Game Port",
                PortProtocol.Udp,
                ConfigField: "game.port",
                Required: true,
                CheckLocalListener: false)],
            "excluded-listener-port-module");
        var otherModule = new ConfigurablePortsModule(
            [new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true)],
            "other-conflict-module");
        var selectedDescriptor = Descriptor(selectedModule, changed: false);
        var otherDescriptor = Descriptor(otherModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            selectedDescriptor,
            [selected, other],
            ModuleDescriptors: [selectedDescriptor, otherDescriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("7777", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("7777", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ranged_declared_port_conflicts_with_another_server_using_a_port_inside_that_range()
    {
        // 7777 with a 4-port range covers 7777-7780; the other server's single port 7779 falls
        // inside it. The old comparison (start value only) would have missed this entirely.
        var rangedServer = CreateServer("ranged-conflict-module", port: "7777");
        var otherServer = CreateServer("other-conflict-module", port: "7779");
        WriteConfig(rangedServer, """{"settings":{"game.port":7777}}""");
        WriteConfig(otherServer, """{"settings":{"game.port":7779}}""");
        var rangedModule = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true, RangeSize: 4)], "ranged-conflict-module");
        var otherModule = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true)], "other-conflict-module");
        var rangedDescriptor = Descriptor(rangedModule, changed: false);
        var otherDescriptor = Descriptor(otherModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            rangedServer,
            rangedDescriptor,
            [rangedServer, otherServer],
            ModuleDescriptors: [rangedDescriptor, otherDescriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("7779", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_ports_pass_message_shows_the_full_range_for_ranged_ports()
    {
        var server = CreateServer("ranged-display-module", port: "7777");
        WriteConfig(server, """{"settings":{"game.port":7777}}""");
        var module = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true, RangeSize: 4)], "ranged-display-module");
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("Game Port=7777-7780", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_display_port_inside_an_already_declared_range_is_not_duplicated_as_a_separate_entry()
    {
        // Regression guard for a real bug: BuildConfiguredPorts's Display-port fallback only
        // compared against a declared port's range START (port.Port != displayPort), not the whole
        // range - a display port inside a multi-port range (UDP 7777-7780 declared, display port
        // 7778) slipped through as a second, protocol-unknown "Display port" entry, which
        // ProtocolsCouldConflict then treated as capable of conflicting with an unrelated TCP-only
        // port on another server, producing a false cross-server conflict warning.
        var server = CreateServer("ranged-display-inside-module", port: "7778");
        WriteConfig(server, """{"settings":{"game.port":7777}}""");
        var module = new ConfigurablePortsModule([new("game", "Game Port", PortProtocol.Udp, ConfigField: "game.port", Required: true, RangeSize: 4)], "ranged-display-inside-module");
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        var portsCheck = Assert.Single(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
        Assert.Contains("Game Port=7777-7780", portsCheck.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Display port", portsCheck.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_server_ports_on_the_same_number_but_different_protocols_are_not_deduplicated_away()
    {
        var server = CreateServer("dual-protocol-module", port: "7777");
        WriteConfig(server, """{"settings":{"tcp.port":7777,"udp.port":7777}}""");
        var module = new ConfigurablePortsModule(
        [
            new("tcp", "TCP Port", PortProtocol.Tcp, ConfigField: "tcp.port", Required: true),
            new("udp", "UDP Port", PortProtocol.Udp, ConfigField: "udp.port", Required: true)
        ], "dual-protocol-module");
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        var portsCheck = Assert.Single(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
        Assert.Contains("TCP Port=7777", portsCheck.Message, StringComparison.Ordinal);
        Assert.Contains("UDP Port=7777", portsCheck.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_selected_servers_module_throws_from_GetPorts()
    {
        var server = CreateServer("throwing-ports-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565}}""");
        var module = new ThrowingPortsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        // The raw exception message never appears anywhere in the report - GetPorts()/Resolve()
        // are arbitrary code that receives this server's real settings, so their exception text
        // could embed a secret; the check must use a fixed, generic message instead.
        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("exploded", StringComparison.OrdinalIgnoreCase));
        // The exception did not abort EvaluateAsync entirely - unrelated checks still ran.
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_a_different_servers_module_throws_from_GetPorts()
    {
        var selected = CreateServer("port-aware-module", port: "25565");
        var broken = CreateServer("throwing-ports-module", port: "25566");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(broken, """{"settings":{"network.port":25566}}""");
        var selectedModule = new PortAwareHealthModule();
        var brokenModule = new ThrowingPortsModule();
        var selectedDescriptor = Descriptor(selectedModule, changed: false);
        var brokenDescriptor = Descriptor(brokenModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            selectedDescriptor,
            [selected, broken],
            ModuleDescriptors: [selectedDescriptor, brokenDescriptor]));

        // The selected server's own report is unaffected by an unrelated OTHER server's broken
        // module - the exception itself never surfaces here, and this server's own Ports check is
        // untouched. But "Port conflicts" must not falsely claim a fully-verified "no conflicts" -
        // it should honestly say one server could not be inspected, not stay silent about it.
        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("exploded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_throws()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new ThrowingPortResolver());

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("exploded", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_null()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new NullResultPortResolver());

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_a_null_entry()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new NullEntryPortResolver());

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_resolved_status_with_a_null_port()
    {
        // Regression guard for a real bug: BuildConfiguredPorts's port.Port!.Value assumed every
        // Status: Resolved entry has a non-null Port - true for the built-in ServerPortResolver,
        // but nothing in ResolvedPort's own type enforces it for a custom implementation.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, null, 1, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_an_undefined_status()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", PortProtocol.Udp, (ResolvedPortStatus)99, 7777, 1, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_a_non_positive_range_size()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, 7777, 0, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_a_range_extending_past_65535()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, 65535, 2, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_a_range_size_that_would_overflow_int_arithmetic()
    {
        // Regression guard for a real bug: IsWellFormedResolvedPort's Port + RangeSize - 1 <= 65535
        // check ran in int arithmetic - Port: 65535, RangeSize: int.MaxValue wraps the sum negative,
        // which is trivially <= 65535, so the malformed range slid straight past the check instead
        // of being caught by it. Mirrors the identical overflow class already fixed in
        // ServerPortResolver.BuildResult during the Tier 5.1 review rounds.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", PortProtocol.Udp, ResolvedPortStatus.Resolved, 65535, int.MaxValue, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_injected_resolver_returns_an_undefined_protocol()
    {
        // Regression guard for a real bug: IsWellFormedResolvedPort validated Status but not
        // Protocol - an undefined PortProtocol value was accepted and flowed into ConfiguredPort,
        // where ProtocolsCouldConflict's a == b/Both comparisons would treat it as distinct from
        // every real protocol (including the one it was actually meant to represent), potentially
        // hiding a genuine same-port conflict against another server instead of being conservatively
        // treated as one.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var malformed = new ResolvedPort("game", "Game Port", (PortProtocol)99, ResolvedPortStatus.Resolved, 7777, 1, true, true);
        var service = new ServerHealthService(portResolver: new MalformedResolvedPortResolver(malformed));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_and_flags_resolution_failure_when_the_resolver_drops_a_declared_ports_result()
    {
        // Regression guard for a real bug: TryResolveDeclaredPorts only validated each returned
        // ResolvedPort's own shape, never that the collection as a whole corresponds to what was
        // actually declared - a resolver silently dropping one declared port's result entirely (an
        // empty list, or one short) passed every per-entry check. The missing required "game" port
        // then never got its own "Port: <name>" Fail, the Display-port fallback could produce a
        // misleading "Ports: Pass", and cross-server conflict detection would silently never check
        // that port either.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new DroppingIdPortResolver("game"));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_and_flags_resolution_failure_when_the_resolver_substitutes_a_declared_ports_protocol()
    {
        // Regression guard for a real bug: the id-multiset completeness check confirmed only that
        // ids matched, not the rest of a declaration's metadata - a resolver that keeps every id in
        // place while silently swapping a declared port's Protocol passed cleanly. PortAwareHealthModule
        // declares "game" as Udp; substituting it to Tcp means ProtocolsCouldConflict would compare
        // this port against another server's real Udp listener as non-conflicting, potentially hiding
        // a genuine port conflict.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new SubstitutingFieldPortResolver("game", port => port with { Protocol = PortProtocol.Tcp }));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_and_flags_resolution_failure_when_the_resolver_substitutes_a_declared_ports_required_status()
    {
        // Regression guard for a real bug: a resolver that keeps every id in place while silently
        // turning a declared Required port into a non-Required one passed the id-only completeness
        // check cleanly - the downgrade would have silently dropped what should have been its own
        // "Port: <name>" Fail the moment that port genuinely failed to resolve, instead of merely
        // changing which check catches the substitution itself.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new SubstitutingFieldPortResolver("game", port => port with { Required = false }));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_and_flags_resolution_failure_when_the_resolver_reports_a_required_port_as_unresolved()
    {
        // Regression guard for a real bug: IsWellFormedResolvedPort accepted any non-Resolved
        // status without checking Required against it - ServerPortResolver.BuildResult's own
        // contract never returns Unresolved for a Required port (a required port with nothing to
        // resolve is always Invalid there), so a custom resolver reporting Required: true,
        // Status: Unresolved is exactly as untrusted as any other shape that violates the built-in
        // resolver's own invariants. Left unchecked, the missing required "game" port produced no
        // "Port: Game Port" Fail at all, and the aggregate "Ports" check could still Pass because
        // the optional "query" port resolved fine.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new SubstitutingFieldPortResolver(
            "game", port => port with { Status = ResolvedPortStatus.Unresolved }));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.DoesNotContain(report.Checks, check =>
            check.Category == "Network" && check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Invalid_declared_port_message_distinguishes_a_missing_required_value_from_an_invalid_configured_value()
    {
        // Regression guard for a real bug: every Invalid declared port got the identical generic
        // "has an invalid declaration" message regardless of cause - a missing required setting and
        // a present-but-malformed configured value both got worded as if the module's own
        // declaration were broken, steering users toward the module author instead of their own
        // server configuration. port.Error itself must stay suppressed (that's still why this
        // doesn't just surface the resolver's raw text), but Required is a safe, already-known field
        // that lets the two most common real causes be told apart without touching anything
        // untrusted. Uses the built-in ServerPortResolver (no injected fake) for both cases.
        var requiredMissingServer = CreateServer("port-aware-module", port: "25565");
        WriteConfig(requiredMissingServer, """{"settings":{"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var requiredMissingReport = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(requiredMissingServer, descriptor, [requiredMissingServer], ModuleDescriptors: [descriptor]));

        Assert.Contains(requiredMissingReport.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port: Game Port" &&
            check.Severity == ServerHealthSeverity.Fail &&
            check.Message.Contains("required", StringComparison.OrdinalIgnoreCase) &&
            !check.Message.Contains("has an invalid declaration", StringComparison.OrdinalIgnoreCase));

        var invalidValueServer = CreateServer("port-aware-module", port: "25565");
        WriteConfig(invalidValueServer, """{"settings":{"network.port":25565,"network.queryPort":"not-a-number"}}""");

        var invalidValueReport = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(invalidValueServer, descriptor, [invalidValueServer], ModuleDescriptors: [descriptor]));

        Assert.Contains(invalidValueReport.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port: Query Port" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
            !check.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_resolves_the_selected_servers_declared_ports_exactly_once()
    {
        // Regression guard for a real bug: an earlier version of AddPortChecks resolved the
        // selected server's ports twice (once for the Invalid-port checks, once again for the
        // ConfiguredPort list) - a stateful module or a resolver that fails intermittently could
        // then produce two different answers to the same question within a single report.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var counting = new CountingPortResolver(new ServerPortResolver());
        var service = new ServerHealthService(portResolver: counting);

        await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Equal(1, counting.CallCount);
    }

    [Fact]
    public async Task BuildSupportSummary_never_includes_raw_exception_text_even_if_it_contains_a_setting_value()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565,"network.queryPort":27015,"rcon.password":"hunter2-fake-secret"}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(portResolver: new LeakyExceptionPortResolver());
        var request = new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]);

        var report = await service.EvaluateAsync(request);
        var summary = service.BuildSupportSummary(request, report);

        Assert.DoesNotContain("hunter2-fake-secret", summary);
        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port declarations" &&
            check.Severity == ServerHealthSeverity.Fail &&
            !check.Message.Contains("hunter2-fake-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_declared_port_check_never_includes_the_resolvers_raw_error_text()
    {
        // ServerPortResolver.ResolveFromConfigField's own "invalid value" error embeds the raw
        // config field value verbatim - a user who accidentally pasted a credential/token into a
        // port field would have it echoed straight into the "Port: <name>" check message, and from
        // there into BuildSupportSummary's output (which copies every check message verbatim).
        // Uses the built-in ServerPortResolver (no injected fake) specifically to prove the fix
        // holds even for the resolver this app actually ships, not just a hypothetical malicious
        // custom implementation. Deliberately does not assert on the full support summary here -
        // BuildSupportSummary's separate raw-config-dump section legitimately echoes any config
        // value under a non-secret-like key (RedactConfigJson only redacts recognised secret key
        // names), so asserting the secret is absent from the whole summary would fail for a reason
        // unrelated to this fix.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":"hunter2-fake-secret","network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port: Game Port" &&
            check.Severity == ServerHealthSeverity.Fail &&
            !check.Message.Contains("hunter2-fake-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_does_not_pass_the_aggregate_ports_check_when_a_declared_port_is_invalid()
    {
        // Regression guard for a real bug: the required "game" port is missing (Invalid, its own
        // Fail check added by AddDeclaredPortValidationChecks), but the optional "query" port still
        // resolves, so currentPorts is non-empty and selectedInspectionIncomplete stays false - the
        // aggregate "Ports" check must not then report a flat "Configured ports are valid" Pass
        // sitting right next to that Fail.
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.queryPort":27015}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Port: Game Port" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning);
        Assert.DoesNotContain(report.Checks, check => check.Category == "Network" && check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Selected_server_with_missing_config_produces_a_warning_not_a_misleading_pass()
    {
        // Config file does not exist at all - AddConfigChecks already reports its own Fail for
        // this, but server.Port (25565) still parses, so the Display port fallback alone used to
        // be enough for a flat "Ports: Pass" right alongside that Fail.
        var server = CreateServer("port-aware-module", port: "25565");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("display port", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Selected_server_with_malformed_config_produces_a_warning_not_a_misleading_pass()
    {
        var server = CreateServer("port-aware-module", port: "25565");
        WriteConfig(server, "{ this is not valid json");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("display port", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Selected_server_port_declaration_failure_produces_a_warning_not_a_misleading_pass()
    {
        // server.Port (25565) still parses, so BuildConfiguredPorts's "Display port" fallback
        // kicks in even though the module's own declared-port resolution failed - the Ports check
        // must reflect that this is a degraded, fallback-only result, not a clean Pass sitting
        // right next to the Fail this same evaluation already reports for the real failure.
        var server = CreateServer("throwing-ports-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565}}""");
        var module = new ThrowingPortsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("display port", StringComparison.OrdinalIgnoreCase) &&
            check.Message.Contains("could not be determined", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Selected_server_port_declaration_failure_with_no_display_port_fallback_is_a_plain_failure()
    {
        // Unlike the previous test, server.Port itself doesn't parse here, so there is no fallback
        // at all - this should stay the existing "No valid configured ports were found" Fail, not
        // gain a separate misleading Ports Warning/Pass alongside it.
        var server = CreateServer("throwing-ports-module", port: "not-a-port");
        WriteConfig(server, """{"settings":{"network.port":25565}}""");
        var module = new ThrowingPortsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Fail &&
            check.Message.Contains("No valid configured ports", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check =>
            check.Name == "Ports" &&
            (check.Severity == ServerHealthSeverity.Pass || check.Severity == ServerHealthSeverity.Warning));
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_the_legacy_config_field_scan_throws()
    {
        // Distinct from the earlier GetPorts()-throws tests: this module declares NO ports at all
        // (GetPorts() returns []), so BuildConfiguredPorts falls through to the legacy
        // module.GetConfigFields() scan - which is the thing that throws here. That scan had no
        // exception handling at all before this fix and would have aborted the whole report.
        var server = CreateServer("throwing-legacy-scan-module", port: "25565");
        WriteConfig(server, """{"settings":{"network.port":25565}}""");
        var module = new ThrowingLegacyPortScanModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        // Unrelated checks still ran - the exception did not abort EvaluateAsync entirely.
        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Name == "Loaded");
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file" && check.Severity == ServerHealthSeverity.Pass);
        // Only the Display port fallback survives - a degraded Warning, not a misleading flat Pass.
        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("display port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_different_servers_unreadable_config_marks_it_as_uninspected_not_a_silent_clean_pass()
    {
        // The other server's ServerConfig.json is deliberately malformed - ServerInstanceFactory.
        // Load throws, otherInstance stays null, and the old code treated that identically to "this
        // server genuinely has no ports," letting the final conflicts check claim a fully-verified
        // "no conflicts" even though the other server's real ports (query/RCON/etc.) were never
        // actually examined.
        var selected = CreateServer("port-aware-module", port: "25565");
        var brokenConfig = CreateServer("port-aware-module", port: "25566");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        Directory.CreateDirectory(brokenConfig.ServerFolder);
        File.WriteAllText(brokenConfig.ConfigPath, "{ this is not valid json");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            descriptor,
            [selected, brokenConfig],
            ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_different_servers_missing_config_file_marks_it_as_uninspected_not_a_silent_clean_pass()
    {
        // Same concern as the malformed-JSON case above, but for a config file that doesn't exist
        // at all rather than one that fails to parse.
        var selected = CreateServer("port-aware-module", port: "25565");
        var missingConfig = CreateServer("port-aware-module", port: "25566");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        Directory.CreateDirectory(missingConfig.ServerFolder);
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            descriptor,
            [selected, missingConfig],
            ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Selected_server_with_no_module_produces_a_warning_not_a_misleading_pass()
    {
        // No descriptor at all - module is null. server.Port still parses, so the Display port
        // fallback alone used to be enough to produce a flat "Ports: Pass" right alongside the
        // separate "Module: Loaded: Fail" check, even though nothing about this server's real
        // game/query/RCON ports was ever actually determined - a missing module is just as much an
        // incomplete inspection as a resolver/config failure, and needs the same Warning treatment.
        var server = CreateServer("missing-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, null, [server]));

        Assert.Contains(report.Checks, check => check.Category == "Module" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check =>
            check.Name == "Ports" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("display port", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check => check.Name == "Ports" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task A_different_servers_missing_module_marks_it_as_uninspected_not_a_silent_clean_pass()
    {
        // The other server's config loads fine, but no descriptor exists for its ModuleId - a
        // different failure mode than a broken config load, and one BuildConfiguredPorts previously
        // treated as a fully, successfully inspected server with simply "no ports."
        var selected = CreateServer("port-aware-module", port: "25565");
        var noModule = CreateServer("missing-module", port: "25566");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(noModule, """{"settings":{}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            descriptor,
            [selected, noModule],
            ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_different_servers_invalid_declared_port_marks_it_as_uninspected_not_a_silent_clean_pass()
    {
        // Regression guard for a real bug: BuildConfiguredPorts only ever includes Resolved-status
        // ports, so an OTHER server's required-but-invalid declared port was silently dropped from
        // conflict comparison with no trace at all - that server still counted as fully inspected
        // (no config-load failure, no null module/instance, no resolution error), so a real conflict
        // on exactly that missing port could go undetected while the aggregate check reported a
        // fully-verified "No other configured server uses any configured port" Pass.
        var selected = CreateServer("port-aware-module", port: "25565");
        var otherInvalid = CreateServer("port-aware-module", port: "25566");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(otherInvalid, """{"settings":{"network.queryPort":27016}}""");
        var module = new PortAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            descriptor,
            [selected, otherInvalid],
            ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check => check.Name == "Port conflicts" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Conflict_warning_also_mentions_additional_uninspectable_servers()
    {
        // Three servers: one genuinely conflicts with the selected server, another can't be
        // inspected at all (no module descriptor) - a detected conflict must not make the report
        // silently drop the fact that a third, unrelated server couldn't be checked either.
        var selected = CreateServer("port-aware-module", port: "25565");
        var conflicting = CreateServer("port-aware-module-2", port: "25567");
        var uninspectable = CreateServer("missing-module", port: "25568");
        WriteConfig(selected, """{"settings":{"network.port":25565,"network.queryPort":27015}}""");
        WriteConfig(conflicting, """{"settings":{"network.port":25565,"network.queryPort":27016}}""");
        WriteConfig(uninspectable, """{"settings":{}}""");
        var selectedModule = new PortAwareHealthModule();
        var conflictingModule = new ConfigurablePortsModule(
            [new("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port", Required: true)],
            "port-aware-module-2");
        var selectedDescriptor = Descriptor(selectedModule, changed: false);
        var conflictingDescriptor = Descriptor(conflictingModule, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            selected,
            selectedDescriptor,
            [selected, conflicting, uninspectable],
            ModuleDescriptors: [selectedDescriptor, conflictingDescriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Port conflicts" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("25565", StringComparison.Ordinal) &&
            check.Message.Contains("could not be inspected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dynamic_module_launch_target_does_not_require_manifest_placeholder_file()
    {
        var server = CreateServer("dynamic-module");
        WriteConfig(server, """{"settings":{"server.jar":"paper-versioned.jar"}}""");
        Directory.CreateDirectory(server.InstallPath);
        File.WriteAllText(Path.Combine(server.InstallPath, "paper-versioned.jar"), "test");
        var module = new DynamicLaunchModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Name == "Executable" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check =>
            check.Name == "Executable" &&
            check.Severity == ServerHealthSeverity.Fail);
    }

    [Fact]
    public void Health_report_converts_to_readiness_results()
    {
        var report = new ServerHealthReport(
            "1",
            "Server",
            [
                new ServerHealthCheck("Install", "Executable", ServerHealthSeverity.Fail, "Missing"),
                new ServerHealthCheck("Network", "Port", ServerHealthSeverity.Pass, "Ready")
            ]);

        var readiness = report.ToReadinessResults();

        Assert.Contains(readiness, result => result.Status == ReadinessStatus.Fail && result.Name.Contains("Executable"));
        Assert.Contains(readiness, result => result.Status == ReadinessStatus.Pass && result.Name.Contains("Port"));
    }

    [Fact]
    public void Support_summary_handles_malformed_config()
    {
        var server = CreateServer("health-module");
        WriteConfig(server, "{ invalid");
        var request = new ServerHealthRequest(server, null, [server]);

        var summary = new ServerHealthService().BuildSupportSummary(
            request,
            new ServerHealthReport(server.Id, server.Name, []));

        Assert.Contains("Config unavailable", summary);
    }

    [Fact]
    public void Support_summary_never_includes_raw_exception_text_from_GetConfigFields_even_if_it_contains_a_secret()
    {
        var server = CreateServer("throwing-config-fields-module");
        WriteConfig(server, """{"settings":{"password":"visible-fake-secret"}}""");
        var module = new ThrowingConfigFieldsModule(new InvalidOperationException("Failed near password=hunter2-fake-secret"));
        var descriptor = Descriptor(module, changed: false);
        var request = new ServerHealthRequest(server, descriptor, [server]);

        var summary = new ServerHealthService().BuildSupportSummary(
            request,
            new ServerHealthReport(server.Id, server.Name, []));

        Assert.DoesNotContain("hunter2-fake-secret", summary);
        Assert.Contains("Config unavailable", summary);
    }

    [Fact]
    public void Support_summary_does_not_throw_for_an_exception_type_outside_the_old_narrow_filter()
    {
        // ArgumentException is not IOException/JsonException/InvalidOperationException - the old
        // catch filter would have let this propagate out of BuildSupportSummary entirely,
        // discarding the whole summary (including the checks list already built above this
        // section) over what should be an optional, best-effort config redaction step.
        var server = CreateServer("throwing-config-fields-module");
        WriteConfig(server, """{"settings":{}}""");
        var module = new ThrowingConfigFieldsModule(new ArgumentException("Unexpected argument."));
        var descriptor = Descriptor(module, changed: false);
        var request = new ServerHealthRequest(server, descriptor, [server]);

        var summary = new ServerHealthService().BuildSupportSummary(
            request,
            new ServerHealthReport(server.Id, server.Name, [new ServerHealthCheck("Module", "Loaded", ServerHealthSeverity.Pass, "ok")]));

        Assert.Contains("Config unavailable", summary);
        Assert.Contains("Module / Loaded", summary);
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_GetConfigFields_during_config_checks()
    {
        // Distinct from the earlier BuildSupportSummary tests - this exercises AddConfigChecks's
        // own, separate call to module.GetConfigFields() (during EvaluateAsync, not the support
        // summary), which was never touched by either of those fixes.
        var server = CreateServer("throwing-config-fields-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        var module = new ThrowingConfigFieldsModule(new InvalidOperationException("Failed near password=hunter2-fake-secret"));
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "Configuration" &&
            check.Name == "Module fields" &&
            check.Severity == ServerHealthSeverity.Fail);
        // The config file itself still parsed fine - only the module-fields step failed.
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_GetConfigFields_returns_null_without_throwing()
    {
        // A module violating its own nullable-reference contract (returning null instead of
        // throwing) is not caught by the try/catch above it - ConfigFieldValidationService's
        // foreach would throw a NullReferenceException, which isn't an InvalidOperationException,
        // so it would escape unnoticed and abort EvaluateAsync entirely.
        var server = CreateServer("null-config-fields-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        var module = new NullReturningConfigFieldsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Configuration" &&
            check.Name == "Module fields" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_config_fields_throw_during_enumeration_rather_than_the_call_itself()
    {
        // A module can return a collection from GetConfigFields() that succeeds as a method call
        // but throws when actually enumerated (e.g. lazily backed by deferred I/O) - the null/
        // null-entry shape check has to run inside the same guarded call as GetConfigFields()
        // itself, or this exact failure mode escapes both catches and aborts EvaluateAsync.
        var server = CreateServer("deferred-throwing-config-fields-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        var module = new DeferredThrowingConfigFieldsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Configuration" &&
            check.Name == "Module fields" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_never_surfaces_a_module_exception_from_a_second_enumeration_of_config_fields()
    {
        // Regression guard for a real bug: the shape-check lambda returned the module's own
        // collection reference after enumerating it once for the null-entry check - a stateful/
        // deferred collection that succeeds on that first enumeration but throws on a second would
        // then throw again inside ValidateSettings's own enumeration below, landing in the
        // catch (InvalidOperationException) block that trusts InvalidOperationException as safe to
        // display verbatim (true for ConfigFieldValidationService's own exceptions, false for
        // arbitrary module-controlled ones). Fixed by materializing to an array inside the guarded
        // lambda and validating only that trusted snapshot, so ValidateSettings never touches the
        // module's original collection at all.
        var server = CreateServer("second-enumeration-throwing-config-fields-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        var module = new SecondEnumerationThrowingConfigFieldsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Configuration" &&
            check.Name == "Module fields" &&
            !check.Message.Contains("hunter2-fake-secret", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check => check.Category == "Configuration" && check.Name == "Config file" && check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_still_shows_the_specific_message_when_ConfigFieldValidationService_itself_rejects_a_value()
    {
        // The other side of the previous test: ConfigFieldValidationService's own exceptions only
        // ever reference a field's Label (module manifest metadata, not a real setting value), so
        // splitting AddConfigChecks into two try blocks must not have swept this one into the new
        // generic "internal error" message meant only for GetConfigFields() itself.
        var server = CreateServer("required-field-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        var module = new RequiredFieldModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Configuration" &&
            check.Name == "Module fields" &&
            check.Severity == ServerHealthSeverity.Fail &&
            check.Message.Contains("Server Name is required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_IsInstallValid()
    {
        var server = CreateServer("throwing-install-validation-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new ThrowingInstallValidationModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-install", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "Installation" &&
            check.Name == "Module validation" &&
            check.Severity == ServerHealthSeverity.Fail);
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_module_readiness_checks()
    {
        var server = CreateServer("throwing-readiness-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new ThrowingReadinessModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-readiness", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "Module readiness" &&
            check.Name == "Check" &&
            check.Severity == ServerHealthSeverity.Fail);
    }

    [Fact]
    public async Task Evaluate_never_completes_when_module_readiness_check_hangs_so_callers_must_apply_an_external_timeout()
    {
        // Regression guard for the support-bundle P2 finding: AddModuleReadinessChecksAsync awaits
        // whatever task a module's readiness check returns with no internal bound of its own, so a
        // module that never completes that task (or ignores cancellation) blocks EvaluateAsync
        // forever. BuildSupportBundleHealthReportsAsync (MainWindow.xaml.cs) now races this exact
        // call against Task.Delay via Task.WhenAny to recover from that. This test proves the
        // underlying hazard is real - EvaluateAsync alone never times out - and that the same
        // Task.WhenAny/Task.Delay race the fix relies on does escape it in bounded time.
        var server = CreateServer("hanging-readiness-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HangingReadinessModule();
        var descriptor = Descriptor(module, changed: false);

        var evaluateTask = new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        var completed = await Task.WhenAny(evaluateTask, Task.Delay(TimeSpan.FromMilliseconds(300)));

        Assert.NotSame(evaluateTask, completed);
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_module_Capabilities_and_still_runs_later_checks()
    {
        // Capabilities is read independently by AddQueryChecks, AddBackupChecks, and AddJavaChecks -
        // each must be isolated so a throw from one doesn't abort the pipeline before Public IP and
        // module readiness checks (which run after all three) ever execute.
        var server = CreateServer("throwing-capabilities-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        File.WriteAllText(Path.Combine(server.InstallPath, "server.exe"), "test");
        var module = new ThrowingCapabilitiesModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(new ServerHealthRequest(
            server,
            descriptor,
            [server],
            ModuleDescriptors: [descriptor],
            PublicIpTrackingEnabled: true,
            LastKnownPublicIp: "203.0.113.10",
            LastPublicIpCheckedAt: DateTimeOffset.UtcNow));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-capabilities", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check => check.Category == "Query" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Backup" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Java" && check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Public IP");
        Assert.Contains(report.Checks, check => check.Category == "Module readiness" && check.Name == "Readiness ran");
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_module_Runtime()
    {
        var server = CreateServer("throwing-runtime-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new ThrowingRuntimeModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-runtime", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "Installation" &&
            check.Name == "Executable" &&
            check.Severity == ServerHealthSeverity.Fail);
        // AddProcessChecks also reads module.Runtime (via ServerProcessLocator.FindProcesses) -
        // same isolation requirement as every other module-property access in this file.
        Assert.Contains(report.Checks, check =>
            check.Category == "Process" &&
            check.Name == "Running" &&
            check.Severity == ServerHealthSeverity.Fail);
        // Proves the pipeline kept going past both the Installation and Process stages instead of
        // aborting.
        Assert.Contains(report.Checks, check => check.Name == "Ports");
    }

    [Fact]
    public async Task Evaluate_reports_no_running_process_as_info_not_fail()
    {
        // A server the user deliberately stopped isn't a health problem - Info, not Fail, matching
        // how AddQueryChecks already treats "offline" for the same reason. HealthModule's own
        // Runtime ("server.exe"/"server") never matches a real running process, so this is already
        // the natural "not running" case without needing a dedicated module double.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Process" &&
            check.Name == "Running" &&
            check.Severity == ServerHealthSeverity.Info &&
            check.Message.Contains("No matching process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_running_process_with_its_pid()
    {
        // ServerProcessLocator.FindProcesses matches on the real process's actual executable path
        // (module.Runtime.StartPath resolved against instance.InstallPath), so this needs a real
        // process whose real .exe path is what the module declares - overriding just InstallPath
        // via `with` on the record CreateServer returns (WriteConfig/ServerFolder/ConfigPath are
        // unaffected, since they're driven by the server's own synthetic temp folder, not
        // InstallPath) rather than trying to fake path matching.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer("real-process-module", port: "25565") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var module = new RealProcessHealthModule();
        var descriptor = Descriptor(module, changed: false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            // Regression guard for a real CI-only flake: ServerProcessLocator.IsExpectedProcess
            // accesses process.MainModule, which can throw ("Unable to enumerate the process
            // modules") if called before the OS has finished the new process's own early
            // initialization - a real, usually very brief race that this test's own
            // zero-delay-after-Start() pattern can lose under real CI resource contention (observed:
            // this test failed in a real GitHub Actions run with the check reporting "No matching
            // process" - Info, not Pass - while passing reliably on an idle local machine every
            // time). Poll until the process is actually discoverable through the same production
            // code path this test exercises, rather than assuming Start() returning means it's
            // immediately visible to it.
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await new ServerHealthService().EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            // Matches on this test's own real PID rather than an exact process count or a fixed
            // "PID N" substring - confirmed by running this against a machine with several other
            // real powershell.exe processes already alive (other tests in this suite spawn them
            // too, and FindProcesses matches by real executable path, so unrelated concurrent
            // powershell.exe processes legitimately match as well): the message then reads "N
            // matching processes ... (PIDs A, B, C)", not "PID N", so a plain "PID {id}" substring
            // check fails even though the process was correctly found. A word-boundary regex
            // matches the PID in both the singular and plural message shapes without risking a
            // false positive from one PID being a numeric substring of another.
            Assert.Contains(report.Checks, check =>
                check.Category == "Process" &&
                check.Name == "Running" &&
                check.Severity == ServerHealthSeverity.Pass &&
                System.Text.RegularExpressions.Regex.IsMatch(check.Message, $@"\b{process.Id}\b"));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_skips_local_port_listening_check_when_no_process_is_confirmed_running()
    {
        // ConfigurablePortsModule's own Runtime ("server.exe"/"server") never matches a real
        // running process, so this is already the natural "not confirmed running" case - a
        // deliberately stopped server isn't a health problem, matching AddProcessChecks/
        // AddQueryChecks' own Info-not-Fail treatment for the same situation.
        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new ConfigurablePortsModule(ports, "configurable-ports-module");
        var server = CreateServer(module.Id, port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Network" &&
            check.Name == "Listening" &&
            check.Severity == ServerHealthSeverity.Info &&
            check.Message.Contains("Skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_listening_pass_when_a_running_servers_configured_port_is_actually_bound()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        // port matches the declared Fixed port (12345) so BuildConfiguredPorts' legacy "Display
        // port" fallback recognises server.Port as already covered by the declared port's range and
        // doesn't add a second, separately-tracked entry - otherwise that unrelated entry would
        // also need a listening/not-listening answer, muddying what these tests are actually about.
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([new IPEndPoint(IPAddress.Loopback, 12345)], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            // Same CI-flake guard as Evaluate_reports_a_running_process_with_its_pid above - poll
            // until the process is actually discoverable through the same production code path
            // this test exercises, rather than assuming Start() returning means it's immediately
            // visible to ServerProcessLocator.
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Info &&
                check.Message.Contains("cannot yet confirm", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_listening_warning_when_a_running_servers_configured_port_is_not_bound()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        // port matches the declared Fixed port (12345) so BuildConfiguredPorts' legacy "Display
        // port" fallback recognises server.Port as already covered by the declared port's range and
        // doesn't add a second, separately-tracked entry - otherwise that unrelated entry would
        // also need a listening/not-listening answer, muddying what these tests are actually about.
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("Game Port=12345", StringComparison.Ordinal));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_does_not_report_a_missing_listener_for_a_logical_port_excluded_from_local_inspection()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[]
        {
            new(
                "game",
                "Game Port",
                PortProtocol.Udp,
                FixedValue: 12345,
                Required: true,
                CheckLocalListener: false)
        };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.DoesNotContain(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning);
            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Ports" &&
                check.Severity == ServerHealthSeverity.Pass &&
                check.Message.Contains("Game Port=12345", StringComparison.Ordinal));
            Assert.DoesNotContain(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Ports" &&
                check.Severity == ServerHealthSeverity.Fail);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_a_warning_when_the_active_listeners_provider_throws()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        // port matches the declared Fixed port (12345) so BuildConfiguredPorts' legacy "Display
        // port" fallback recognises server.Port as already covered by the declared port's range and
        // doesn't add a second, separately-tracked entry - otherwise that unrelated entry would
        // also need a listening/not-listening answer, muddying what these tests are actually about.
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => throw new InvalidOperationException("hunter2-fake-secret-listeners"));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-listeners", StringComparison.Ordinal));
            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning);
            Assert.Contains(report.Checks, check => check.Category == "Module");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_a_udp_only_declared_port_as_not_listening_when_only_tcp_is_bound()
    {
        // Explicit UDP-vs-TCP regression: a UDP-declared port must not be satisfied by a TCP
        // listener on the same number - the two protocols are answered by entirely different
        // sockets, matching how PortDiagnosticsService's own TCP/UDP rows are never conflated.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Udp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([new IPEndPoint(IPAddress.Loopback, 12345)], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("Game Port=12345", StringComparison.Ordinal));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_a_both_protocol_declared_port_as_not_listening_when_only_tcp_is_bound()
    {
        // Regression guard for a real review finding: PortProtocol.Both fell through to the
        // "unknown protocol" fallback (matchesTcp || matchesUdp), so a TCP-only listener
        // incorrectly satisfied a declaration requiring both protocols - the exact shape of
        // GenericWrapperModule's own real, shipped game port declaration. Both must require
        // matchesTcp AND matchesUdp, not either.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Both, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([new IPEndPoint(IPAddress.Loopback, 12345)], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("Game Port=12345", StringComparison.Ordinal));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_a_both_protocol_declared_port_as_listening_when_tcp_and_udp_are_both_bound()
    {
        // Positive counterpart to the Both-protocol regression above - confirms the fix didn't
        // just make Both always fail, it correctly passes once both protocols are genuinely bound.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Both, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                [new IPEndPoint(IPAddress.Loopback, 12345)],
                [new IPEndPoint(IPAddress.Loopback, 12345)]));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Info &&
                check.Message.Contains("cannot yet confirm", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_a_ranged_port_as_not_listening_when_any_number_is_missing()
    {
        // RangeSize is a consecutive occupied block. Listeners on only the first TCP and last UDP
        // numbers must not satisfy the four-port Both declaration.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Both, FixedValue: 12345, Required: true, RangeSize: 4) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                [new IPEndPoint(IPAddress.Loopback, 12345)],
                [new IPEndPoint(IPAddress.Loopback, 12348)]));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("Game Port=12345-12348", StringComparison.Ordinal));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Port_range_requires_the_declared_transport_on_every_number()
    {
        var tcpPorts = new HashSet<int>();
        var partialUdpPorts = new HashSet<int> { 24000 };
        var completeUdpPorts = new HashSet<int> { 24000, 24001, 24002, 24003 };

        Assert.False(ServerHealthService.IsPortRangeListening(
            24000,
            4,
            PortProtocol.Udp,
            tcpPorts,
            partialUdpPorts));
        Assert.True(ServerHealthService.IsPortRangeListening(
            24000,
            4,
            PortProtocol.Udp,
            tcpPorts,
            completeUdpPorts));
    }

    [Fact]
    public void Either_transport_accepts_tcp_or_udp_while_both_requires_both()
    {
        var tcpPorts = new HashSet<int> { 25565 };
        var udpPorts = new HashSet<int>();

        Assert.True(ServerHealthService.IsPortRangeListening(
            25565,
            1,
            PortProtocol.Either,
            tcpPorts,
            udpPorts));
        Assert.False(ServerHealthService.IsPortRangeListening(
            25565,
            1,
            PortProtocol.Both,
            tcpPorts,
            udpPorts));
    }

    [Fact]
    public async Task Evaluate_reports_partial_listener_result_when_declared_port_inspection_is_incomplete()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[]
        {
            new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true),
            new("broken", "Broken Port", PortProtocol.Udp, FixedValue: 70000, Required: true)
        };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                [new IPEndPoint(IPAddress.Loopback, 12345)],
                []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("full port configuration was unavailable", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(report.Checks, check =>
                check.Name == "Listening" &&
                check.Message.Contains("every configured port", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static ServerHealthService.ProtocolOwnershipContext Context(
        IEnumerable<int> presentPorts,
        Dictionary<int, HashSet<int>>? ownersByPort,
        IEnumerable<int>? ipv4Ports = null)
    {
        var present = presentPorts.ToHashSet();
        return new ServerHealthService.ProtocolOwnershipContext(present, (ipv4Ports ?? present).ToHashSet(), ownersByPort);
    }

    [Fact]
    public void Ownership_is_confirmed_only_when_the_satisfying_listener_belongs_to_a_candidate_pid()
    {
        var tcp = Context([7777], new Dictionary<int, HashSet<int>> { [7777] = [111] });
        var udp = Context([7777], new Dictionary<int, HashSet<int>> { [7777] = [222] });

        Assert.Equal(ServerHealthService.PortOwnershipStatus.Owned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 111 }));
        Assert.Equal(ServerHealthService.PortOwnershipStatus.NotOwned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 999 }));
        // Both requires the candidate to own the TCP listener AND the UDP listener - owning only one
        // of the two protocols must not satisfy a Both declaration, the same distinction
        // IsPortRangeListening already draws between presence of one protocol and presence of both.
        Assert.Equal(ServerHealthService.PortOwnershipStatus.NotOwned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Both, tcp, udp, new HashSet<int> { 111 }));
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Owned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Both, tcp, udp, new HashSet<int> { 111, 222 }));
        // Either is satisfied by ownership of just one of the two protocols.
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Owned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Either, tcp, udp, new HashSet<int> { 111 }));
    }

    [Fact]
    public void Ownership_over_a_range_requires_every_number_to_be_owned_by_a_candidate_pid()
    {
        var tcpOwners = new Dictionary<int, HashSet<int>>
        {
            [24000] = [111],
            [24001] = [111],
            [24002] = [999], // a different process owns this one number in the range
            [24003] = [111]
        };
        var tcp = Context([24000, 24001, 24002, 24003], tcpOwners);
        var udp = Context([], new Dictionary<int, HashSet<int>>());

        Assert.Equal(ServerHealthService.PortOwnershipStatus.NotOwned, ServerHealthService.GetPortRangeOwnershipStatus(
            24000, 4, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 111 }));

        tcpOwners[24002] = [111];
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Owned, ServerHealthService.GetPortRangeOwnershipStatus(
            24000, 4, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 111 }));
    }

    [Fact]
    public void Ownership_is_unknown_rather_than_not_owned_when_the_lookup_itself_failed()
    {
        // Regression guard: a null OwnersByPort (the native lookup failed, or a caller never
        // supplied ownership data) must never be treated the same as "checked, and nothing owns it."
        var tcp = Context([7777], null);
        var udp = Context([], new Dictionary<int, HashSet<int>>());

        Assert.Equal(ServerHealthService.PortOwnershipStatus.Unknown, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 111 }));

        // Both: one side unknown, the other confirmed owned - still unknown overall, not a false Pass.
        var udpOwned = Context([7777], new Dictionary<int, HashSet<int>> { [7777] = [111] });
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Unknown, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Both, tcp, udpOwned, new HashSet<int> { 111 }));

        // Both: one side unknown, the other confirmed NOT owned - definitively fails regardless,
        // since Both can never be satisfied once either side is a confirmed non-owner.
        var udpNotOwned = Context([7777], new Dictionary<int, HashSet<int>> { [7777] = [999] });
        Assert.Equal(ServerHealthService.PortOwnershipStatus.NotOwned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Both, tcp, udpNotOwned, new HashSet<int> { 111 }));

        // Either: one side unknown, the other confirmed owned - satisfied regardless of the unknown side.
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Owned, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Either, tcp, udpOwned, new HashSet<int> { 111 }));

        // Either: one side unknown, the other confirmed not owned - still unknown, not a false NotOwned.
        Assert.Equal(ServerHealthService.PortOwnershipStatus.Unknown, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Either, tcp, udpNotOwned, new HashSet<int> { 111 }));
    }

    [Fact]
    public void Ownership_is_unknown_for_a_port_whose_only_presence_listener_is_ipv6()
    {
        // Regression guard: NativeConnectionTable is IPv4-only by design. A port with a real presence
        // listener that isn't in the IPv4-scoped set (i.e. it's only reachable via IPv6) must read as
        // unknown, not "not owned," even though the (IPv4-only) owner dictionary has no entry for it.
        var tcp = new ServerHealthService.ProtocolOwnershipContext(
            ListenerPorts: new HashSet<int> { 7777 },
            Ipv4ListenerPorts: new HashSet<int>(),
            OwnersByPort: new Dictionary<int, HashSet<int>>());
        var udp = Context([], new Dictionary<int, HashSet<int>>());

        Assert.Equal(ServerHealthService.PortOwnershipStatus.Unknown, ServerHealthService.GetPortRangeOwnershipStatus(
            7777, 1, PortProtocol.Tcp, tcp, udp, new HashSet<int> { 111 }));
    }

    [Fact]
    public async Task Evaluate_reports_pass_when_the_listener_is_confirmed_owned_by_the_running_process()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            // Deliberately uses the real spawned process's own PID as the owner - proves the PID
            // returned by AddProcessChecks (via ServerProcessLocator) is the same value threaded
            // through to the ownership check, not just a shape/type match.
            var service = new ServerHealthService(
                activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                    [new IPEndPoint(IPAddress.Loopback, 12345)],
                    [],
                    [new NativeConnectionTable.OwnedEndpoint(new IPEndPoint(IPAddress.Loopback, 12345), process.Id)],
                    []));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Pass &&
                check.Message.Contains("confirmed owned", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_warning_when_the_listener_is_not_owned_by_the_running_process()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            // The owner PID (this test process's own PID, via Environment.ProcessId) is guaranteed
            // real but guaranteed different from the spawned child process's own PID being checked.
            var service = new ServerHealthService(
                activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                    [new IPEndPoint(IPAddress.Loopback, 12345)],
                    [],
                    [new NativeConnectionTable.OwnedEndpoint(new IPEndPoint(IPAddress.Loopback, 12345), Environment.ProcessId)],
                    []));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning &&
                check.Message.Contains("not confirmed as owned", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_ownership_unknown_when_only_a_shell_launcher_process_is_confirmed_running()
    {
        // Regression guard: GenericWrapperModule's Batch/PowerShell launch modes start cmd.exe/
        // powershell.exe as the direct tracked process - ServerRuntimeTracker records *that*
        // process's own pid/executable in runtime.json, while the real game/Java process it spawns
        // is an untracked descendant with a different pid. Matching a port's owner against the
        // launcher's own pid would confidently (and wrongly) report "not owned" for an otherwise
        // perfectly healthy server. Confirmed only via runtime.json here (self-consistent pid +
        // executable), the same mechanism ServerRuntimeTracker actually uses for a launched server -
        // deliberately NOT pointing InstallPath at PowerShell's own folder (unlike this file's other
        // RealProcessWithPortsModule tests), since the real bug only happens when the launcher's own
        // executable lives outside the install directory, exactly like a real system cmd.exe/
        // powershell.exe.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var server = CreateServer(module.Id, port: "12345");
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            Directory.CreateDirectory(server.ServerFolder);
            var runtimeState = new Dictionary<string, object?>
            {
                ["pid"] = process.Id,
                ["executable"] = PowerShellPath,
                ["installPath"] = server.InstallPath,
                ["attached"] = true,
                ["sessionId"] = Environment.ProcessId,
                ["updatedUtc"] = DateTimeOffset.UtcNow
            };
            File.WriteAllText(
                Path.Combine(server.ServerFolder, "runtime.json"),
                JsonSerializer.Serialize(runtimeState));

            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            // An owner PID that matches nothing - if the fix didn't short-circuit before ownership
            // matching, this would otherwise produce a "not confirmed as owned" Warning, which is
            // exactly the false result this test proves does NOT happen.
            var service = new ServerHealthService(
                activeListenersProvider: () => new ServerHealthService.ActiveListeners(
                    [new IPEndPoint(IPAddress.Loopback, 12345)],
                    [],
                    [new NativeConnectionTable.OwnedEndpoint(new IPEndPoint(IPAddress.Loopback, 12345), 999999)],
                    []));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Info &&
                check.Message.Contains("launches through a shell", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(report.Checks, check =>
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Warning);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_does_not_treat_display_address_as_an_authoritative_local_bind_address()
    {
        // ServerDisplayInfo.IpAddress can be a proxy/public/display address rather than the local
        // socket bind address. Until modules expose an authoritative bind-address contract, local
        // endpoint presence must not be rejected solely because its address differs from this
        // display value.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var ports = new ServerPortDefinition[] { new("game", "Game Port", PortProtocol.Tcp, FixedValue: 12345, Required: true) };
        var module = new RealProcessWithPortsModule(ports);
        var installPath = Path.GetDirectoryName(PowerShellPath)!;
        var server = CreateServer(module.Id, port: "12345") with { InstallPath = installPath, IpAddress = "192.168.50.5" };
        WriteConfig(server, """{"settings":{}}""");
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            activeListenersProvider: () => new ServerHealthService.ActiveListeners([new IPEndPoint(IPAddress.Loopback, 12345)], []));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            await WaitUntilAsync(() =>
            {
                var found = ServerProcessLocator.FindProcesses(module, server.InstallPath);
                var matched = found.Any(candidate => candidate.Id == process.Id);
                foreach (var candidate in found)
                {
                    candidate.Dispose();
                }

                return matched;
            }, TimeSpan.FromSeconds(10));

            var report = await service.EvaluateAsync(
                new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

            Assert.Contains(report.Checks, check =>
                check.Category == "Network" &&
                check.Name == "Listening" &&
                check.Severity == ServerHealthSeverity.Info &&
                check.Message.Contains("cannot yet confirm", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Evaluate_reports_no_crash_history_as_pass_when_the_CrashLogs_folder_is_absent()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Stability" &&
            check.Name == "Crash history" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("No crash reports", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_recent_crash_report_as_warning()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        Directory.CreateDirectory(crashDirectory);
        // Timestamp is embedded in the filename and read from there (not the file's own mtime),
        // so it must actually be current, not just a fixed-looking recent-ish string.
        File.WriteAllText(Path.Combine(crashDirectory, $"server-crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-pid-123.log"), "crash report contents");
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Stability" &&
            check.Name == "Crash history" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("1 crash report", StringComparison.OrdinalIgnoreCase) &&
            check.Message.Contains("last 7 days", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_only_old_crash_reports_as_info_not_warning()
    {
        // Regression guard for a real design requirement, not just a shape check: a crash report
        // from months ago that was already investigated and fixed must not keep permanently
        // warning on every single Server Doctor run for the rest of the server's lifetime - only
        // reports within RecentCrashWindow (7 days) should surface as Warning; anything older is
        // historical context, Info at most.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        Directory.CreateDirectory(crashDirectory);
        var oldReportPath = Path.Combine(crashDirectory, "server-crash-20250101-000000-pid-123.log");
        File.WriteAllText(oldReportPath, "old crash report contents");
        File.SetLastWriteTimeUtc(oldReportPath, DateTimeOffset.UtcNow.AddDays(-30).UtcDateTime);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Stability" &&
            check.Name == "Crash history" &&
            check.Severity == ServerHealthSeverity.Info &&
            check.Message.Contains("1 historical crash report", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Checks, check =>
            check.Category == "Stability" && check.Name == "Crash history" && check.Severity == ServerHealthSeverity.Warning);
    }

    [Fact]
    public async Task Evaluate_does_not_treat_a_future_dated_crash_report_as_recent()
    {
        // Regression guard for a real finding: age = UtcNow - timestamp is negative for a future-
        // dated report (clock skew, a restored backup, a manually touched/renamed file) - without a
        // non-negative age guard, a negative TimeSpan trivially satisfies "<= 7 days" and the report
        // would count as recent forever, no matter how far in the future it's dated or how much real
        // time passes.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        Directory.CreateDirectory(crashDirectory);
        File.WriteAllText(Path.Combine(crashDirectory, "server-crash-20991231-235959-pid-123.log"), "future-dated crash report");
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Stability" &&
            check.Name == "Crash history" &&
            check.Severity == ServerHealthSeverity.Info);
        Assert.DoesNotContain(report.Checks, check =>
            check.Category == "Stability" && check.Name == "Crash history" && check.Severity == ServerHealthSeverity.Warning);
    }

    [Fact]
    public async Task Evaluate_uses_the_crash_report_filenames_own_timestamp_not_the_file_systems_mtime()
    {
        // Regression guard for a real finding: LastWriteTimeUtc can be reset by anything that
        // merely touches the file - a folder copy, a backup restore, an antivirus scan - without it
        // actually being a recent crash. The timestamp embedded in the filename by
        // ServerCrashDiagnosticsService's own writer is controlled by nothing else, so it must win
        // over a stale/reset mtime, not the other way around.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        Directory.CreateDirectory(crashDirectory);
        var reportPath = Path.Combine(crashDirectory, $"server-crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-pid-123.log");
        File.WriteAllText(reportPath, "crash report with a stale mtime");
        // If the check were still using mtime instead of the filename, this would incorrectly read
        // as historical/Info instead of recent/Warning.
        File.SetLastWriteTimeUtc(reportPath, DateTimeOffset.UtcNow.AddDays(-30).UtcDateTime);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Stability" &&
            check.Name == "Crash history" &&
            check.Severity == ServerHealthSeverity.Warning);
    }

    [Fact]
    public async Task Evaluate_reports_no_operation_history_as_info()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Info &&
            check.Message.Contains("No recorded operation history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_warning_when_operation_history_could_not_be_read()
    {
        // Regression guard for a real finding: OperationHistoryRepository.GetRecentForServer used
        // to swallow every failure and return an empty list, indistinguishable from "no history
        // recorded yet" - a locked/corrupt/inaccessible database silently read as Info instead of
        // surfacing that the check itself couldn't run. RecentOperationsError is what
        // ServerInfoWindow.xaml.cs now populates when that call throws (mirroring FirewallError).
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(
                server,
                descriptor,
                [server],
                ModuleDescriptors: [descriptor],
                RecentOperationsError: "Recent operation history could not be read: database is locked"));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_successful_most_recent_operation_as_pass()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var recentOperations = new[]
        {
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Start, "Completed", DateTimeOffset.UtcNow.AddMinutes(-5), null, IsActive: false, DateTimeOffset.UtcNow.AddMinutes(-4))
        };

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor], RecentOperations: recentOperations));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Pass &&
            check.Message.Contains("Start", StringComparison.Ordinal) &&
            check.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_failed_most_recent_operation_as_warning_without_the_raw_error_text()
    {
        // Regression guard matching this file's established policy for every other module/resolver-
        // originated exception text: LastError on a Failed operation is exactly as untrusted, since
        // ServerOperationManager.FailOperation sets it directly from an arbitrary module call's own
        // exception Message (e.g. a Start operation's CreateStartInfoAsync/StartAsync throwing).
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var recentOperations = new[]
        {
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Start, "Failed", DateTimeOffset.UtcNow.AddMinutes(-5), "password=hunter2-fake-secret-operation-error", IsActive: false, DateTimeOffset.UtcNow.AddMinutes(-4))
        };

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor], RecentOperations: recentOperations));

        var operationsCheck = Assert.Single(report.Checks, check => check.Category == "Operations" && check.Name == "Recent history");
        Assert.Equal(ServerHealthSeverity.Warning, operationsCheck.Severity);
        Assert.Contains("failed", operationsCheck.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2-fake-secret", operationsCheck.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evaluate_reports_an_interrupted_most_recent_operation_as_warning()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var recentOperations = new[]
        {
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Update, "Interrupted", DateTimeOffset.UtcNow.AddMinutes(-5), null, IsActive: false, DateTimeOffset.UtcNow.AddMinutes(-4))
        };

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor], RecentOperations: recentOperations));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_cancelled_most_recent_operation_as_info()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var recentOperations = new[]
        {
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Backup, "Cancelled", DateTimeOffset.UtcNow.AddMinutes(-5), null, IsActive: false, DateTimeOffset.UtcNow.AddMinutes(-4))
        };

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor], RecentOperations: recentOperations));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Info &&
            check.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_warning_when_the_most_recent_operation_succeeded_but_earlier_ones_failed()
    {
        // Regression guard for a real finding: severity used to be based solely on the newest
        // operation, so a server whose last 9 operations failed and 10th happened to succeed still
        // read as a clean Pass, with the failure count buried in a sentence nobody would read after
        // seeing "Pass" - directly contradicting Server Doctor's purpose of surfacing recent
        // failures. Any Failed/Interrupted entry anywhere in the sampled batch must force at least
        // a Warning, regardless of what the most recent operation's own outcome was.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var recentOperations = new[]
        {
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Start, "Completed", DateTimeOffset.UtcNow.AddMinutes(-1), null, IsActive: false, DateTimeOffset.UtcNow),
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Update, "Failed", DateTimeOffset.UtcNow.AddHours(-1), "fake error", IsActive: false, DateTimeOffset.UtcNow.AddHours(-1)),
            new ServerOperationSnapshot(server.Id, server.Name, ServerOperationKind.Restart, "Failed", DateTimeOffset.UtcNow.AddHours(-2), "fake error", IsActive: false, DateTimeOffset.UtcNow.AddHours(-2))
        };

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor], RecentOperations: recentOperations));

        Assert.Contains(report.Checks, check =>
            check.Category == "Operations" &&
            check.Name == "Recent history" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("2 of the last 3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_disk_space_pass_when_free_space_is_above_the_threshold()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(freeDiskSpaceProvider: _ => 10L * 1024 * 1024 * 1024);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "System" &&
            check.Name == "Disk space" &&
            check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_reports_disk_space_warning_when_free_space_is_below_the_threshold()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(freeDiskSpaceProvider: _ => 1L * 1024 * 1024 * 1024);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "System" &&
            check.Name == "Disk space" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("1.0 GB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_reports_a_warning_when_free_disk_space_could_not_be_determined()
    {
        // A null result (e.g. an unready/disconnected drive) is a real diagnostic gap, but not
        // itself evidence of a problem the way a low-space number is - Warning, not Fail, mirrors
        // how AddOperationHistoryChecks/AddPortChecks treat "could not be inspected" elsewhere.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(freeDiskSpaceProvider: _ => null);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "System" &&
            check.Name == "Disk space" &&
            check.Severity == ServerHealthSeverity.Warning &&
            check.Message.Contains("Could not determine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_failure_when_the_free_disk_space_provider_throws()
    {
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            freeDiskSpaceProvider: _ => throw new InvalidOperationException("hunter2-fake-secret-disk-space"));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-disk-space", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "System" &&
            check.Name == "Disk space" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module");
    }

    [Fact]
    public async Task Evaluate_reports_disk_space_via_the_real_default_provider_without_throwing()
    {
        // No freeDiskSpaceProvider override - exercises the real DriveInfo-backed default against
        // this test's own real, on-disk InstallPath, proving the production path itself works
        // rather than only the injected-override paths above.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "System" &&
            check.Name == "Disk space" &&
            check.Severity is ServerHealthSeverity.Pass or ServerHealthSeverity.Warning);
    }

    [Fact]
    public async Task Evaluate_does_not_report_steamcmd_availability_when_the_module_does_not_use_it()
    {
        // Mirrors AddJavaChecks' own "no RequiresJava, no check at all" behaviour - a module that
        // never declares a Steam install (the default HealthModule here) shouldn't get any
        // SteamCMD Pass/Fail noise, regardless of whether steamcmd.exe happens to exist.
        var server = CreateServer("health-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new HealthModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Category == "SteamCMD");
    }

    [Fact]
    public async Task Evaluate_does_not_report_steamcmd_availability_for_an_authenticated_steam_install()
    {
        // Regression guard for a real review finding: Services.PersistentSteamClient.Select routes
        // a SteamInstallDefinition with LoginAnonymous == false to DepotDownloaderClient instead of
        // SteamCmdManager - an authenticated Steam server never touches steamcmd.exe at all, so a
        // missing/untrusted steamcmd.exe must not fail its health report just because it's unused.
        // steamCmdAvailabilityProbe is stubbed to always fail, proving the check is skipped
        // entirely rather than merely happening to pass.
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule(loginAnonymous: false);
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(steamCmdAvailabilityProbe: _ => false);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Category == "SteamCMD");
    }

    [Fact]
    public async Task Evaluate_reports_steamcmd_available_when_the_probe_confirms_a_trusted_executable()
    {
        // The default probe (File.Exists + SteamCmdManager.HasValveAuthenticodeSignature) can't
        // pass in a unit test without a real Valve-signed steamcmd.exe on disk, so this injects
        // the probe directly to prove the Pass path - mirroring how SteamCmdPolicyTests itself
        // injects signatureVerifier: _ => true to bypass real Authenticode verification.
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var exePath = Path.Combine(_root, "steamcmd.exe");
        var service = new ServerHealthService(
            steamCmdExePathResolver: () => exePath,
            steamCmdAvailabilityProbe: _ => true);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Pass);
    }

    [Fact]
    public async Task Evaluate_reports_steamcmd_missing_when_the_executable_is_not_found_at_the_resolved_path()
    {
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var exePath = Path.Combine(_root, "missing-steamcmd", "steamcmd.exe");
        var service = new ServerHealthService(steamCmdExePathResolver: () => exePath);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Fail &&
            check.Message.Contains(exePath, StringComparison.Ordinal) &&
            check.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_steamcmd_unavailable_when_the_default_probe_rejects_an_untrusted_executable()
    {
        // Regression guard for a real review finding: the first version of this check only did
        // File.Exists, so a corrupt/fake/malicious steamcmd.exe would report Pass even though
        // SteamCmdManager.EnsureInstalledAsync (SteamCmdManager.cs) would silently reject and
        // reinstall it - a false "available" result. This uses the real default probe (no
        // steamCmdAvailabilityProbe override), only the path resolver is pointed at a fake file,
        // to prove production behaviour actually rejects it rather than just trusting an override.
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var exePath = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(exePath, "fake steamcmd, not a real signed executable");
        var service = new ServerHealthService(steamCmdExePathResolver: () => exePath);

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Fail &&
            check.Message.Contains("failed verification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Evaluate_reports_a_failure_when_the_steamcmd_availability_probe_throws()
    {
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(
            steamCmdExePathResolver: () => Path.Combine(_root, "steamcmd.exe"),
            steamCmdAvailabilityProbe: _ => throw new InvalidOperationException("hunter2-fake-secret-probe"));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-probe", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module");
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_GetSteamInstall()
    {
        var server = CreateServer("throwing-steam-install-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new ThrowingSteamInstallModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-steam-install", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Fail);
        // Proves the pipeline kept going past the SteamCMD stage instead of aborting.
        Assert.Contains(report.Checks, check => check.Category == "Module");
    }

    [Fact]
    public async Task Evaluate_reports_a_failure_when_the_steamcmd_path_resolver_throws()
    {
        var server = CreateServer("steam-aware-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SteamAwareHealthModule();
        var descriptor = Descriptor(module, changed: false);
        var service = new ServerHealthService(steamCmdExePathResolver: () => throw new InvalidOperationException("hunter2-fake-secret-path"));

        var report = await service.EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-path", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "SteamCMD" &&
            check.Name == "Availability" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Category == "Module");
    }

    [Fact]
    public async Task Evaluate_never_includes_raw_exception_text_from_GetBackupTargets()
    {
        var server = CreateServer("throwing-backup-targets-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new ThrowingBackupTargetsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.DoesNotContain(report.Checks, check => check.Message.Contains("hunter2-fake-secret-backup-targets", StringComparison.Ordinal));
        Assert.Contains(report.Checks, check =>
            check.Category == "Backup" &&
            check.Name == "Targets" &&
            check.Severity == ServerHealthSeverity.Fail);
        // Proves the pipeline kept going past the Backup stage instead of aborting - Java/module
        // readiness are both conditional on module capabilities this module doesn't declare, but
        // Public IP always adds a check regardless.
        Assert.Contains(report.Checks, check => check.Name == "Public IP");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_GetBackupTargets_returns_null_without_throwing()
    {
        // TryInvokeModule only reports failure when GetBackupTargets() throws - a module
        // returning null instead (without throwing) would otherwise reach the Select in
        // AddBackupChecks and throw an uncaught exception, aborting EvaluateAsync entirely.
        var server = CreateServer("null-backup-targets-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new NullReturningBackupTargetsModule(nullEntryInsteadOfNullList: false);
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Backup" &&
            check.Name == "Targets" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Public IP");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_a_backup_target_in_the_list_is_null()
    {
        // Distinct from the previous test: here GetBackupTargets() itself returns a non-null
        // list, but one entry in it is null - the Select in AddBackupChecks would dereference
        // that entry's Label/RelativePath/IsRequired and throw before the per-target try/catch
        // (which only guards path processing, not the initial projection) ever gets a chance.
        var server = CreateServer("null-backup-target-entry-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new NullReturningBackupTargetsModule(nullEntryInsteadOfNullList: true);
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check =>
            check.Category == "Backup" &&
            check.Name == "Targets" &&
            check.Severity == ServerHealthSeverity.Fail);
        Assert.Contains(report.Checks, check => check.Name == "Public IP");
    }

    [Fact]
    public async Task Evaluate_does_not_abort_when_backup_targets_are_enumerated_only_once()
    {
        // Regression guard for a real bug: AddBackupChecks used to enumerate module.GetBackupTargets()
        // twice - once via .Any(target => target == null), once via the later .Select(...).ToArray()
        // projection - with nothing guarding that second pass. A stateful/deferred result that only
        // broke on its second enumeration would throw straight out of AddBackupChecks and abort the
        // rest of EvaluateAsync (Java checks, Public IP, module readiness) instead of producing the
        // intended Backup/Targets result. The fix materializes the list once, inside the guarded
        // TryInvokeModule call, so nothing ever enumerates the module's own result a second time.
        var server = CreateServer("second-enumeration-backup-module", port: "25565");
        WriteConfig(server, """{"settings":{}}""");
        Directory.CreateDirectory(server.InstallPath);
        var module = new SecondEnumerationThrowingBackupTargetsModule();
        var descriptor = Descriptor(module, changed: false);

        var report = await new ServerHealthService().EvaluateAsync(
            new ServerHealthRequest(server, descriptor, [server], ModuleDescriptors: [descriptor]));

        Assert.Contains(report.Checks, check => check.Category == "Backup" && check.Name == "Data");
        Assert.Contains(report.Checks, check => check.Name == "Public IP");
    }

    private ModuleDescriptor Descriptor(IGameServerModule module, bool changed)
    {
        return ModuleDescriptor.Create(
            module,
            Path.Combine(_root, "module"),
            "test",
            new ModuleProvenanceSnapshot(
                null,
                "HASH",
                changed,
                changed ? "Module files changed since import." : null));
    }

    private InstalledServer CreateServer(
        string moduleId,
        string port = "25565",
        ServerRuntimeStatus status = ServerRuntimeStatus.Offline)
    {
        var serverFolder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        return new InstalledServer(
            "server-" + Guid.NewGuid().ToString("N"),
            "Test Server",
            moduleId,
            "test",
            serverFolder,
            Path.Combine(serverFolder, "files"),
            Path.Combine(serverFolder, "ServerConfig.json"),
            "0.0.0.0",
            port,
            "",
            "public",
            "20",
            "--",
            "--",
            "--",
            "--",
            status == ServerRuntimeStatus.Running ? "Running" : "Offline",
            "--",
            false,
            "",
            null,
            true,
            status,
            status.ToString(),
            "TextBrush",
            false,
            "",
            "",
            "",
            true,
            true,
            true,
            status == ServerRuntimeStatus.Running);
    }

    private static void WriteConfig(InstalledServer server, string json)
    {
        Directory.CreateDirectory(server.ServerFolder);
        File.WriteAllText(server.ConfigPath, json);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class HealthModule(bool requiresJava = true) : IGameServerModule, IModuleReadinessCapability
    {
        public string Id => "health-module";
        public string Name => "Health Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(
            false, false, true, false, false, false, true, true,
            RequiresJava: requiresJava,
            MinimumJavaMajor: requiresJava ? 21 : null);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
        [
            new("network.port", "Game port", ConfigFieldType.Port, 25565),
            new("network.queryPort", "Query port", ConfigFieldType.Port, 27015),
            new("adminCode", "Admin code", ConfigFieldType.Password, "", Required: false)
        ];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => File.Exists(Path.Combine(instance.InstallPath, Runtime.StartPath));
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() =>
            [new("world", "World", "world", IsDirectory: true, IsRequired: true)];
        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadinessCheckResult>>([ReadinessCheckResult.Pass("Custom check", "Module-specific readiness passed.")]);
    }

    private sealed class SteamAwareHealthModule(bool hasSteamInstall = true, bool loginAnonymous = true) : IGameServerModule
    {
        public string Id => "steam-aware-module";
        public string Name => "Steam Aware Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => hasSteamInstall ? new SteamInstallDefinition("12345", LoginAnonymous: loginAnonymous) : null;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Steam Aware Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class ThrowingSteamInstallModule : IGameServerModule
    {
        // ModuleDescriptor.Create's own GetEffectiveCapabilities already calls
        // module.GetSteamInstall() once, unguarded, to compute SupportsVerify - so a module whose
        // SteamInstall getter always throws would blow up during descriptor creation in the test's
        // own Descriptor() helper, never reaching ServerHealthService at all (a pre-existing gap in
        // module-loading, out of scope for this check). Succeeding on the first call and throwing
        // only afterwards - the same shape ThrowingCapabilitiesModule already uses for Capabilities
        // - reaches AddSteamCmdChecks' own call instead.
        private int _steamInstallCallCount;

        public string Id => "throwing-steam-install-module";
        public string Name => "Throwing Steam Install Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall
        {
            get
            {
                _steamInstallCallCount++;
                return _steamInstallCallCount == 1
                    ? null
                    : throw new InvalidOperationException("hunter2-fake-secret-steam-install");
            }
        }
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Steam Install Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class PortAwareHealthModule : IGameServerModule
    {
        public string Id => "port-aware-module";
        public string Name => "Port Aware Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        // network.port is Required at the ServerPortDefinition level below, but deliberately NOT
        // Required at the config-field level - that lets a test drive "the resolver considers this
        // port required and missing" independently of ConfigFieldValidationService's own, separate
        // required-field check (which runs earlier, in AddConfigChecks, and would otherwise fail
        // the whole config load before the resolver-based port check ever got a chance to run).
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
        [
            new("network.port", "Game port", ConfigFieldType.Port),
            new("network.queryPort", "Query port", ConfigFieldType.Port)
        ];
        public IReadOnlyList<ServerPortDefinition> GetPorts() =>
        [
            new("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port", Required: true),
            new("query", "Query Port", PortProtocol.Udp, ConfigField: "network.queryPort")
        ];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Port Aware Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Declares whatever ports a test passes in, deriving matching config fields automatically -
    // avoids a proliferation of near-identical single-purpose module classes for each
    // protocol/range conflict-detection scenario. Config fields are never Required (regardless of
    // the port declaration's own Required flag) for the same reason PortAwareHealthModule's are
    // not: ConfigFieldValidationService's own required-field check runs first and would otherwise
    // short-circuit config loading before these tests ever reach port resolution.
    private sealed class ConfigurablePortsModule(IReadOnlyList<ServerPortDefinition> ports, string id) : IGameServerModule
    {
        public string Id => id;
        public string Name => "Configurable Ports Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
            ports.Where(port => port.ConfigField != null)
                .Select(port => new ConfigFieldDefinition(port.ConfigField!, port.Name, ConfigFieldType.Port))
                .ToArray();
        public IReadOnlyList<ServerPortDefinition> GetPorts() => ports;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Configurable Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class ThrowingPortsModule : IGameServerModule
    {
        public string Id => "throwing-ports-module";
        public string Name => "Throwing Ports Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [new("network.port", "Game port", ConfigFieldType.Port, 25565)];
        public IReadOnlyList<ServerPortDefinition> GetPorts() => throw new InvalidOperationException("Module GetPorts() exploded.");
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // GetPorts() is deliberately empty (not throwing) - this module has NOT adopted declared ports
    // at all, so BuildConfiguredPorts falls all the way through to the legacy module.GetConfigFields()
    // scan, which is what actually throws here. GetConfigFields() itself only throws from its
    // SECOND call onward - its first call happens earlier, inside AddConfigChecks's own
    // ConfigFieldValidationService.ValidateSettings call, and has to succeed there so config
    // loading completes and instance is populated; otherwise this test would only exercise
    // AddConfigChecks's own, already-existing exception handling instead of the new one this
    // finding is actually about.
    private sealed class ThrowingLegacyPortScanModule : IGameServerModule
    {
        private int _getConfigFieldsCallCount;

        public string Id => "throwing-legacy-scan-module";
        public string Name => "Throwing Legacy Scan Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
        {
            _getConfigFieldsCallCount++;
            return _getConfigFieldsCallCount == 1
                ? []
                : throw new InvalidOperationException("Module GetConfigFields() exploded on the port scan.");
        }
        public IReadOnlyList<ServerPortDefinition> GetPorts() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Legacy Scan Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // GetConfigFields() throws whatever the test hands it - used to exercise BuildSupportSummary's
    // own, separate call to module.GetConfigFields() (for the password-key redaction pass), not
    // EvaluateAsync's.
    private sealed class ThrowingConfigFieldsModule(Exception exceptionToThrow) : IGameServerModule
    {
        public string Id => "throwing-config-fields-module";
        public string Name => "Throwing Config Fields Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => throw exceptionToThrow;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Config Fields Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Doesn't throw - returns null instead, violating its own nullable-reference contract, the
    // way a compiled third-party module still can regardless of what the interface declares.
    private sealed class NullReturningConfigFieldsModule : IGameServerModule
    {
        public string Id => "null-config-fields-module";
        public string Name => "Null Config Fields Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => null!;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Null Config Fields Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Backs DeferredThrowingConfigFieldsModule below - a collection whose GetConfigFields() call
    // itself succeeds (returns this instance without throwing) but whose enumeration throws,
    // simulating a lazily-evaluated result backed by deferred I/O or similar.
    private sealed class LazilyThrowingConfigFieldList : IReadOnlyList<ConfigFieldDefinition>
    {
        public ConfigFieldDefinition this[int index] => throw new InvalidOperationException("Enumeration failed.");
        public int Count => 1;
        public IEnumerator<ConfigFieldDefinition> GetEnumerator() => throw new InvalidOperationException("Enumeration failed.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DeferredThrowingConfigFieldsModule : IGameServerModule
    {
        public string Id => "deferred-throwing-config-fields-module";
        public string Name => "Deferred Throwing Config Fields Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => new LazilyThrowingConfigFieldList();
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Deferred Throwing Config Fields Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Backs SecondEnumerationThrowingConfigFieldsModule below - succeeds on its first enumeration
    // (so the guarded shape-check lambda's own materialization sees a normal result) but throws a
    // fake-secret-bearing exception on every enumeration after that, simulating a stateful/deferred
    // collection whose later access can fail independently of its first.
    private sealed class ThrowsOnSecondEnumerationConfigFieldList(ConfigFieldDefinition[] fields) : IReadOnlyList<ConfigFieldDefinition>
    {
        private int _enumerationCount;
        public ConfigFieldDefinition this[int index] => fields[index];
        public int Count => fields.Length;
        public IEnumerator<ConfigFieldDefinition> GetEnumerator()
        {
            _enumerationCount++;
            if (_enumerationCount > 1)
            {
                throw new InvalidOperationException("Failed while looking at rcon.password=hunter2-fake-secret");
            }

            return ((IEnumerable<ConfigFieldDefinition>)fields).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SecondEnumerationThrowingConfigFieldsModule : IGameServerModule
    {
        public string Id => "second-enumeration-throwing-config-fields-module";
        public string Name => "Second Enumeration Throwing Config Fields Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
            new ThrowsOnSecondEnumerationConfigFieldList([new("server.name", "Server Name", ConfigFieldType.Text, "")]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Second Enumeration Throwing Config Fields Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class ThrowingInstallValidationModule : IGameServerModule
    {
        public string Id => "throwing-install-validation-module";
        public string Name => "Throwing Install Validation Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Install Validation Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => throw new InvalidOperationException("Install check failed near rcon.password=hunter2-fake-secret-install");
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class ThrowingReadinessModule : IGameServerModule, IModuleReadinessCapability
    {
        public string Id => "throwing-readiness-module";
        public string Name => "Throwing Readiness Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Readiness Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Readiness check failed near apiToken=hunter2-fake-secret-readiness");
    }

    // Capabilities is a single property shared by Query/Backup/Java checks - throwing here affects
    // all three at once, which doubles as the "later checks still run after an earlier one fails"
    // proof: Java (after Backup) and module readiness (last of all) must still execute normally.
    // Capabilities only throws from its SECOND call onward - its first call happens earlier,
    // inside ModuleDescriptor.Create's own unguarded GetEffectiveCapabilities call (which runs
    // during test setup via the Descriptor() helper, not inside EvaluateAsync), and has to
    // succeed there or the descriptor could never be built at all.
    // Deliberately never completes and ignores the cancellation token entirely - simulates the
    // exact hazard reported against BuildSupportBundleHealthReportsAsync (MainWindow.xaml.cs): a
    // module readiness check that hangs forever with no internal bound of its own.
    private sealed class HangingReadinessModule : IGameServerModule, IModuleReadinessCapability
    {
        public string Id => "hanging-readiness-module";
        public string Name => "Hanging Readiness Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Hanging Readiness Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            new TaskCompletionSource<IReadOnlyList<ReadinessCheckResult>>().Task;
    }

    private sealed class ThrowingCapabilitiesModule : IGameServerModule, IModuleReadinessCapability
    {
        private int _capabilitiesCallCount;

        public string Id => "throwing-capabilities-module";
        public string Name => "Throwing Capabilities Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities
        {
            get
            {
                _capabilitiesCallCount++;
                return _capabilitiesCallCount == 1
                    ? new(false, false, false, false, false, false, false, false)
                    : throw new InvalidOperationException("Capabilities failed near password=hunter2-fake-secret-capabilities");
            }
        }
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Capabilities Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadinessCheckResult>>([ReadinessCheckResult.Pass("Readiness ran", "Confirms EvaluateAsync did not abort before reaching module readiness.")]);
    }

    private sealed class ThrowingRuntimeModule : IGameServerModule
    {
        public string Id => "throwing-runtime-module";
        public string Name => "Throwing Runtime Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => throw new InvalidOperationException("Runtime failed near password=hunter2-fake-secret-runtime");
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Runtime Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // StartPath/ProcessNames deliberately match a real, spawnable executable (powershell.exe) so
    // ServerProcessLocator.FindProcesses' real path-matching logic can find an actually-running
    // process in a test, rather than needing to fake or bypass that matching.
    private sealed class RealProcessHealthModule : IGameServerModule
    {
        public string Id => "real-process-module";
        public string Name => "Real Process Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("powershell.exe", ["powershell"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Real Process Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Same real-process shape as RealProcessHealthModule, plus a fixed declared port - needed to
    // exercise AddLocalPortListeningChecks' "process confirmed running" branch, which
    // RealProcessHealthModule itself can't reach (it declares no ports at all, so AddPortChecks
    // finds nothing and the listening check never gets past its own empty-ports early return).
    private sealed class RealProcessWithPortsModule(IReadOnlyList<ServerPortDefinition> ports) : IGameServerModule
    {
        public string Id => "real-process-with-ports-module";
        public string Name => "Real Process With Ports Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("powershell.exe", ["powershell"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public IReadOnlyList<ServerPortDefinition> GetPorts() => ports;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Real Process With Ports Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    // Capabilities.SupportsBackups succeeds (true) but GetBackupTargets() itself throws - isolates
    // the "clearest reproducible case" the finding called out specifically, distinct from a
    // Capabilities-access failure.
    private sealed class ThrowingBackupTargetsModule : IGameServerModule
    {
        public string Id => "throwing-backup-targets-module";
        public string Name => "Throwing Backup Targets Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, true, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Throwing Backup Targets Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() =>
            throw new InvalidOperationException("Backup targets failed near password=hunter2-fake-secret-backup-targets");
    }

    // Doesn't throw - either returns null outright, or a non-null list containing a null entry,
    // depending on the constructor flag. Both are nullable-reference-contract violations a
    // compiled third-party module can still commit despite what the interface declares.
    private sealed class NullReturningBackupTargetsModule(bool nullEntryInsteadOfNullList) : IGameServerModule
    {
        public string Id => "null-backup-targets-module";
        public string Name => "Null Backup Targets Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, true, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Null Backup Targets Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() =>
            nullEntryInsteadOfNullList ? [null!] : null!;
    }

    // Succeeds on its first enumeration (the materializing ToArray() inside AddBackupChecks'
    // guarded lambda) but throws on any second one - simulates a stateful/deferred module result
    // (e.g. backed by lazy I/O) that a naive "enumerate once to validate, enumerate again to
    // project" implementation would only discover was broken on the second, unguarded pass.
    private sealed class SecondEnumerationThrowingBackupTargetsModule : IGameServerModule
    {
        public string Id => "second-enumeration-throwing-backup-targets-module";
        public string Name => "Second Enumeration Throwing Backup Targets Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, true, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Second Enumeration Throwing Backup Targets Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => new ThrowsOnSecondEnumerationList();

        private sealed class ThrowsOnSecondEnumerationList : IReadOnlyList<ServerBackupTargetDefinition>
        {
            private static readonly ServerBackupTargetDefinition[] Items = [new("data", "Data", "data", false, true)];
            private int _enumerationCount;

            public ServerBackupTargetDefinition this[int index] => Items[index];
            public int Count => Items.Length;

            public IEnumerator<ServerBackupTargetDefinition> GetEnumerator()
            {
                if (Interlocked.Increment(ref _enumerationCount) > 1)
                {
                    throw new InvalidOperationException("Backup targets enumerated more than once near password=hunter2-fake-secret-backup-targets");
                }

                return ((IEnumerable<ServerBackupTargetDefinition>)Items).GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    // One Required field, with no port/other complications - used to confirm
    // ConfigFieldValidationService's own safe, label-only exception messages still show through
    // specifically after AddConfigChecks was split into two try blocks, rather than being swept
    // into the new generic "internal error" message meant only for GetConfigFields() itself.
    private sealed class RequiredFieldModule : IGameServerModule
    {
        public string Id => "required-field-module";
        public string Name => "Required Field Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
            [new("server.name", "Server Name", ConfigFieldType.Text, "", Required: true)];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Required Field Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class ThrowingPortResolver : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            throw new InvalidOperationException("Resolver exploded.");
        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            throw new InvalidOperationException("Resolver exploded.");
    }

    // Simulates a poorly-written (not necessarily malicious) IServerPortResolver whose exception
    // message accidentally embeds a real setting value - it has access to the full settings
    // dictionary, including anything secret-like, and nothing stops it from putting that in an
    // error message the way a careless "failed while processing {value}" might.
    private sealed class LeakyExceptionPortResolver : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings)
        {
            settings.TryGetValue("rcon.password", out var secret);
            throw new InvalidOperationException($"Resolver failed while looking at rcon.password={secret}");
        }

        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            throw new InvalidOperationException("Resolver exploded.");
    }

    // Simulates a custom IServerPortResolver that violates its own contract by returning null
    // instead of an empty list - the interface's nullable annotations are compile-time only, so
    // nothing stops a third-party implementation from doing this at runtime.
    private sealed class NullResultPortResolver : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            null!;
        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            null!;
    }

    // Simulates a custom IServerPortResolver that returns a list containing a null entry -
    // AddDeclaredPortValidationChecks's foreach over the resolved list must not crash on this.
    private sealed class NullEntryPortResolver : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            [null!];
        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            [null!];
    }

    // Simulates a custom IServerPortResolver returning one caller-supplied, possibly internally
    // inconsistent ResolvedPort - e.g. Status: Resolved with Port: null, an undefined Status, or a
    // non-positive RangeSize. Nothing about ResolvedPort's own type prevents a third-party
    // implementation from doing this; only TryResolveDeclaredPorts's shape validation does.
    private sealed class MalformedResolvedPortResolver(ResolvedPort malformedPort) : IServerPortResolver
    {
        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            [malformedPort];
        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            [malformedPort];
    }

    // Wraps a real ServerPortResolver but silently drops the result for one specific declared port
    // id - simulates a resolver that doesn't produce a result for every declared port, without
    // needing to hand-construct every other ResolvedPort field for the ones that should still
    // resolve normally.
    private sealed class DroppingIdPortResolver(string idToDrop) : IServerPortResolver
    {
        private readonly ServerPortResolver _inner = new();

        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            _inner.Resolve(ports, settings).Where(port => !string.Equals(port.Id, idToDrop, StringComparison.OrdinalIgnoreCase)).ToArray();

        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            Resolve(module.GetPorts(), instance.Settings);
    }

    // Wraps a real ServerPortResolver but replaces one specific declared port's result with an
    // otherwise-identical ResolvedPort that has one field substituted - simulates a resolver that
    // keeps every id in place while silently changing a declaration's metadata (protocol, required,
    // etc.) rather than dropping the result outright.
    private sealed class SubstitutingFieldPortResolver(string idToSubstitute, Func<ResolvedPort, ResolvedPort> substitute) : IServerPortResolver
    {
        private readonly ServerPortResolver _inner = new();

        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings) =>
            _inner.Resolve(ports, settings)
                .Select(port => string.Equals(port.Id, idToSubstitute, StringComparison.OrdinalIgnoreCase) ? substitute(port) : port)
                .ToArray();

        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance) =>
            Resolve(module.GetPorts(), instance.Settings);
    }

    private sealed class CountingPortResolver(IServerPortResolver inner) : IServerPortResolver
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<ResolvedPort> Resolve(IReadOnlyList<ServerPortDefinition> ports, IReadOnlyDictionary<string, object?> settings)
        {
            CallCount++;
            return inner.Resolve(ports, settings);
        }

        public IReadOnlyList<ResolvedPort> Resolve(IGameServerModule module, ServerInstance instance)
        {
            CallCount++;
            return inner.Resolve(module, instance);
        }
    }

    private sealed class DynamicLaunchModule : IGameServerModule
    {
        public string Id => "dynamic-module";
        public string Name => "Dynamic Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("paper.jar", ["java"]);
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
            [new("server.jar", "Server Jar", ConfigFieldType.Text, "paper.jar")];
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Dynamic";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("0.0.0.0", "25565", "20");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) =>
            instance.Settings.TryGetValue("server.jar", out var value) &&
            File.Exists(Path.Combine(instance.InstallPath, value?.ToString() ?? ""));
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}
