using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerPortResolverTests
{
    [Fact]
    public void Resolve_preserves_check_local_listener_metadata()
    {
        var ports = new[]
        {
            new ServerPortDefinition(
                "logical-game",
                "Logical Game Port",
                PortProtocol.Udp,
                FixedValue: 17777,
                CheckLocalListener: false)
        };

        var resolved = new ServerPortResolver().Resolve(
            ports,
            new Dictionary<string, object?>());

        Assert.Single(resolved);
        Assert.False(resolved[0].CheckLocalListener);
    }

    [Fact]
    public void Invariant_failure_preserves_check_local_listener_metadata()
    {
        var ports = new[]
        {
            new ServerPortDefinition(
                "invalid-logical",
                "Invalid Logical Port",
                PortProtocol.Udp,
                RangeSize: 0,
                CheckLocalListener: false),
            new ServerPortDefinition(
                "valid",
                "Valid Port",
                PortProtocol.Tcp,
                FixedValue: 25565)
        };

        var resolved = new ServerPortResolver().Resolve(
            ports,
            new Dictionary<string, object?>());

        Assert.Equal(2, resolved.Count);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved[0].Status);
        Assert.False(resolved[0].CheckLocalListener);
        Assert.Equal(ResolvedPortStatus.Resolved, resolved[1].Status);
    }

    private readonly ServerPortResolver _resolver = new();

    [Fact]
    public void Fixed_port_resolves_to_its_literal_value()
    {
        var ports = new[] { new ServerPortDefinition("rcon", "RCON", PortProtocol.Tcp, FixedValue: 25575) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Resolved, resolved.Status);
        Assert.Equal(25575, resolved.Port);
    }

    [Fact]
    public void Config_derived_port_resolves_when_the_reference_casing_differs_from_the_settings_key()
    {
        // ModuleValidator matches configField against declared config field keys case-
        // insensitively, so a manifest referencing "NETWORK.PORT" for a field keyed "network.port"
        // passes validation cleanly - but a real settings dictionary (InstallServerWindow.
        // ReadSettings builds a plain case-sensitive Dictionary<string, object?>, keyed by the
        // field's own exact declared casing) would silently miss that reference without this fix.
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "NETWORK.PORT", Required: true) };
        var settings = new Dictionary<string, object?> { ["network.port"] = 27015 };

        var result = _resolver.Resolve(ports, settings);

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Resolved, resolved.Status);
        Assert.Equal(27015, resolved.Port);
    }

    [Fact]
    public void Config_derived_port_resolves_from_the_server_settings_value()
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port", Required: true) };
        var settings = new Dictionary<string, object?> { ["network.port"] = 27015 };

        var result = _resolver.Resolve(ports, settings);

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Resolved, resolved.Status);
        Assert.Equal(27015, resolved.Port);
    }

    [Fact]
    public void Optional_config_derived_port_with_no_value_is_unresolved_not_invalid()
    {
        var ports = new[] { new ServerPortDefinition("query", "Query Port", PortProtocol.Udp, ConfigField: "network.queryPort") };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Unresolved, resolved.Status);
        Assert.Null(resolved.Port);
        Assert.Null(resolved.Error);
    }

    [Fact]
    public void Required_config_derived_port_with_no_value_is_invalid()
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port", Required: true) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
        Assert.NotNull(resolved.Error);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData(0)]
    [InlineData(65536)]
    public void Config_derived_port_with_a_malformed_or_out_of_range_value_is_invalid(object value)
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port") };
        var settings = new Dictionary<string, object?> { ["network.port"] = value };

        var result = _resolver.Resolve(ports, settings);

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
    }

    [Fact]
    public void Ranged_port_exposes_the_full_consecutive_range()
    {
        var ports = new[] { new ServerPortDefinition("voice", "Voice Channels", PortProtocol.Udp, FixedValue: 24000, RangeSize: 4) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Resolved, resolved.Status);
        Assert.Equal([24000, 24001, 24002, 24003], resolved.PortRange);
    }

    [Fact]
    public void Ranged_port_that_would_extend_past_65535_is_invalid()
    {
        var ports = new[] { new ServerPortDefinition("voice", "Voice Channels", PortProtocol.Udp, FixedValue: 65534, RangeSize: 4) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
        Assert.Empty(resolved.PortRange);
    }

    [Fact]
    public void Offset_port_resolves_relative_to_its_base_port()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port"),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: 1)
        };
        var settings = new Dictionary<string, object?> { ["network.port"] = 7777 };

        var result = _resolver.Resolve(ports, settings);

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Resolved, web.Status);
        Assert.Equal(7778, web.Port);
    }

    [Fact]
    public void Offset_port_resolves_even_when_declared_before_its_base_in_the_list()
    {
        var ports = new[]
        {
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: 1),
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Resolved, web.Status);
        Assert.Equal(7778, web.Port);
    }

    [Fact]
    public void Chained_offsets_resolve_through_multiple_hops()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: 1),
            new ServerPortDefinition("rest", "REST API", PortProtocol.Tcp, OffsetFrom: "web", Offset: 1)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var rest = result.Single(port => port.Id == "rest");
        Assert.Equal(ResolvedPortStatus.Resolved, rest.Status);
        Assert.Equal(7779, rest.Port);
    }

    [Fact]
    public void An_offset_port_that_depends_on_an_unresolved_optional_base_is_unresolved_not_a_false_cycle()
    {
        var ports = new[]
        {
            new ServerPortDefinition("query", "Query Port", PortProtocol.Udp, ConfigField: "network.queryPort"),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "query", Offset: 1)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Unresolved, web.Status);
        Assert.Null(web.Error);
    }

    [Fact]
    public void Mutually_offsetting_ports_are_reported_as_a_circular_reference_not_left_silently_unresolved()
    {
        var ports = new[]
        {
            new ServerPortDefinition("a", "Port A", PortProtocol.Tcp, OffsetFrom: "b", Offset: 1),
            new ServerPortDefinition("b", "Port B", PortProtocol.Tcp, OffsetFrom: "a", Offset: 1)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port =>
        {
            Assert.Equal(ResolvedPortStatus.Invalid, port.Status);
            Assert.NotNull(port.Error);
        });
    }

    [Fact]
    public void GenericWrapperModule_declares_working_ports_resolvable_from_its_own_config_fields()
    {
        var module = new GenericWrapperModule();
        var settings = module.GetConfigFields()
            .ToDictionary(field => field.Key, field => field.DefaultValue, StringComparer.OrdinalIgnoreCase);
        var instance = new ServerInstance(
            "wrapper-test",
            "Wrapper Test",
            module.Id,
            "server-folder",
            "install-path",
            "ServerConfig.json",
            settings);

        var result = _resolver.Resolve(module, instance);

        var game = result.Single(port => port.Id == "game");
        Assert.Equal(ResolvedPortStatus.Resolved, game.Status);
        Assert.Equal(25565, game.Port);
        Assert.Equal(PortProtocol.Either, game.Protocol);
    }

    [Fact]
    public void GenericWrapperModule_untouched_default_settings_resolve_without_overlap()
    {
        // A brand-new server created from this module and never edited must not start out
        // "invalid." GenericWrapperModule declares both "game" and "query" ports (so the query
        // port isn't a second, disconnected source of truth from ServerHealthService/
        // WindowsFirewallService - see network.queryPort's own comment), but network.queryPort has
        // no default value, so an untouched server's query port comes back Unresolved, not
        // Invalid and not overlapping the game port.
        var module = new GenericWrapperModule();
        var settings = module.GetConfigFields()
            .ToDictionary(field => field.Key, field => field.DefaultValue, StringComparer.OrdinalIgnoreCase);
        var instance = new ServerInstance(
            "wrapper-default-test",
            "Wrapper Default Test",
            module.Id,
            "server-folder",
            "install-path",
            "ServerConfig.json",
            settings);

        var result = _resolver.Resolve(module, instance);

        var game = result.Single(port => port.Id == "game");
        Assert.Equal(ResolvedPortStatus.Resolved, game.Status);
        Assert.Equal(25565, game.Port);
        var query = result.Single(port => port.Id == "query");
        Assert.Equal(ResolvedPortStatus.Unresolved, query.Status);
        Assert.Null(query.Error);
    }

    [Fact]
    public void GenericWrapperModule_query_port_config_field_has_no_default_value()
    {
        // Regression guard, not a resolver behaviour test: network.queryPort must stay blank by
        // default. WindowsFirewallService.GetRequiredRules derives an inbound TCP+UDP rule pair
        // from every ConfigFieldType.Port field, skipping only fields whose value doesn't parse as
        // a number - a real numeric default here (whether matching network.port's or its own
        // distinct one) was tried twice already and reverted both times, because either choice
        // gave WindowsGSH a real, non-blank value to build a firewall rule from for a query
        // listener that, for a fresh, unconfigured server, doesn't actually exist yet.
        var module = new GenericWrapperModule();
        var queryPortField = module.GetConfigFields().Single(field => field.Key == "network.queryPort");

        Assert.Null(queryPortField.DefaultValue);
    }

    [Fact]
    public void An_offset_that_would_drive_the_result_below_1_is_invalid_not_a_negative_port()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 10),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: -20)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Invalid, web.Status);
        Assert.Null(web.Port);
    }

    [Fact]
    public void A_large_offset_does_not_silently_overflow_into_an_in_range_port()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 100),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: int.MaxValue)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Invalid, web.Status);
        Assert.Null(web.Port);
    }

    [Fact]
    public void Errors_propagate_to_a_dependent_declared_before_its_broken_base_in_the_array()
    {
        // Declaration order matters here: "child" comes first and offsets from "parent," which
        // itself offsets from an id that doesn't exist. The bug this guards against: the
        // fixed-point loop's very first pass visits child before parent has been marked invalid
        // (parent is later in iteration order), so child would see "not yet resolved, not yet
        // errored" and skip for that pass - if error-recording didn't also count as loop
        // progress, the loop could exit right there, permanently leaving child as merely
        // Unresolved instead of correctly Invalid.
        var ports = new[]
        {
            new ServerPortDefinition("child", "Child", PortProtocol.Tcp, OffsetFrom: "parent", Offset: 1),
            new ServerPortDefinition("parent", "Parent", PortProtocol.Tcp, OffsetFrom: "no-such-port", Offset: 1)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var child = result.Single(port => port.Id == "child");
        Assert.Equal(ResolvedPortStatus.Invalid, child.Status);
        Assert.NotNull(child.Error);
    }

    [Fact]
    public void Two_resolved_ports_on_the_same_number_and_a_shared_protocol_are_flagged_as_overlapping()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("voice", "Voice", PortProtocol.Udp, FixedValue: 7777)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port =>
        {
            Assert.Equal(ResolvedPortStatus.Invalid, port.Status);
            Assert.Equal(PortResolutionFailureReason.Overlap, port.FailureReason);
            Assert.Contains("overlap", port.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Ports_on_the_same_number_but_disjoint_protocols_do_not_overlap()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, FixedValue: 7777)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port => Assert.Equal(ResolvedPortStatus.Resolved, port.Status));
    }

    [Fact]
    public void A_both_protocol_port_overlaps_even_a_single_protocol_port_on_the_same_number()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Both, FixedValue: 7777),
            new ServerPortDefinition("query", "Query", PortProtocol.Udp, FixedValue: 7777)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port => Assert.Equal(ResolvedPortStatus.Invalid, port.Status));
    }

    [Fact]
    public void Three_way_overlap_does_not_throw_and_flags_every_participant()
    {
        var ports = new[]
        {
            new ServerPortDefinition("a", "A", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("b", "B", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("c", "C", PortProtocol.Udp, FixedValue: 7777)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port => Assert.Equal(ResolvedPortStatus.Invalid, port.Status));
    }

    // ModuleValidator only ever sees ports that came from a JSON manifest - a compiled C# module's
    // IGameServerModule.GetPorts() override (GenericWrapperModule is a real example) can return
    // ServerPortDefinition objects directly, with no validation step in between at all. These cases
    // reproduce constructing that record with invariants a JSON manifest could never get past
    // ModuleValidator with, calling Resolve() directly the same way a real caller would - the point
    // is that none of these throw, and all of them come back Invalid with a useful message instead.

    [Fact]
    public void Duplicate_ids_from_a_direct_C_sharp_module_do_not_throw_and_are_both_invalid()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("game", "Duplicate", PortProtocol.Tcp, FixedValue: 9999)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        Assert.All(result, port => Assert.Equal(ResolvedPortStatus.Invalid, port.Status));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_id_from_a_direct_C_sharp_module_does_not_throw_and_is_invalid(string? id)
    {
        var ports = new[] { new ServerPortDefinition(id!, "No Id", PortProtocol.Udp, FixedValue: 7777) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_from_a_direct_C_sharp_module_does_not_throw_and_is_invalid(string? name)
    {
        // ModuleValidator requires a non-blank name for the JSON manifest path
        // (ports.name.required); a direct C# module has no equivalent gate, so a blank name would
        // otherwise flow all the way through to a Resolved result and render as a blank label in
        // Server Doctor/forwarding instructions.
        var ports = new[] { new ServerPortDefinition("game", name!, PortProtocol.Udp, FixedValue: 7777) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_rangeSize_from_a_direct_C_sharp_module_does_not_throw_and_is_invalid(int rangeSize)
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7777, RangeSize: rangeSize) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
        Assert.Empty(resolved.PortRange);
    }

    [Fact]
    public void An_undefined_protocol_value_from_a_direct_C_sharp_module_does_not_throw_and_is_invalid()
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", (PortProtocol)99, FixedValue: 7777) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
    }

    [Fact]
    public void No_source_at_all_from_a_direct_C_sharp_module_does_not_throw_and_is_invalid()
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
    }

    [Fact]
    public void Multiple_sources_from_a_direct_C_sharp_module_do_not_silently_pick_one_and_are_invalid()
    {
        var ports = new[] { new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, ConfigField: "network.port", FixedValue: 7777) };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?> { ["network.port"] = 27015 });

        var resolved = Assert.Single(result);
        Assert.Equal(ResolvedPortStatus.Invalid, resolved.Status);
        // The bug this guards against: ServerPortDefinition.Source picks FixedValue by priority
        // when more than one source is set, so a naive fix might still "successfully" resolve to
        // 7777 (the fixedValue) instead of rejecting the ambiguous declaration outright.
        Assert.Null(resolved.Port);
    }

    [Fact]
    public void An_otherwise_invalid_port_does_not_stop_other_ports_from_resolving_normally()
    {
        var ports = new[]
        {
            new ServerPortDefinition("", "Broken", PortProtocol.Udp, FixedValue: 7777),
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, FixedValue: 7778)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var game = result.Single(port => port.Id == "game");
        Assert.Equal(ResolvedPortStatus.Resolved, game.Status);
        Assert.Equal(7778, game.Port);
    }

    [Fact]
    public void An_offsetFrom_pointing_at_a_port_that_failed_its_own_invariants_does_not_throw_and_is_invalid()
    {
        var ports = new[]
        {
            new ServerPortDefinition("game", "Game Port", PortProtocol.Udp, RangeSize: 0),
            new ServerPortDefinition("web", "Web Admin", PortProtocol.Tcp, OffsetFrom: "game", Offset: 1)
        };

        var result = _resolver.Resolve(ports, new Dictionary<string, object?>());

        var web = result.Single(port => port.Id == "web");
        Assert.Equal(ResolvedPortStatus.Invalid, web.Status);
    }
}
