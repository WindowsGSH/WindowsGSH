using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleValidatorTests
{
    [Fact]
    public void Valid_manifest_has_no_errors()
    {
        var result = ModuleValidator.Validate(CreateValidManifest());

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Missing_required_identity_and_start_are_errors()
    {
        var result = ModuleValidator.Validate(new ModuleManifest());

        Assert.Contains(result.Errors, error => error.Code == "id.required");
        Assert.Contains(result.Errors, error => error.Code == "name.required");
        Assert.Contains(result.Errors, error => error.Code == "entryPoint.start.required");
    }

    [Theory]
    [InlineData("../server.exe")]
    [InlineData("..\\server.exe")]
    [InlineData("C:\\Game\\server.exe")]
    public void Start_entry_point_must_stay_inside_install_directory(string startPath)
    {
        var manifest = CreateValidManifest();
        manifest.EntryPoints!.Start = startPath;

        var result = ModuleValidator.Validate(manifest);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Path == "entryPoints.start");
    }

    [Fact]
    public void Duplicate_config_keys_are_errors()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.port",
            Label = "Duplicate",
            Type = "Port",
            DefaultValue = 27016
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.key.duplicate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Port_defaults_must_be_valid_ports(int port)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Single(field => field.Key == "network.port").DefaultValue = port;

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.port.default.range");
    }

    [Fact]
    public void Optional_port_field_with_no_default_does_not_warn()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.queryPort",
            Label = "Query Port",
            Type = "Port"
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "config.port.default.missing");
    }

    [Fact]
    public void Required_port_field_with_no_default_still_warns()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.rconPort",
            Label = "RCON Port",
            Type = "Port",
            Required = true
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Warnings, warning => warning.Code == "config.port.default.missing");
    }

    [Fact]
    public void Optional_port_field_with_a_present_non_numeric_default_still_warns()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.queryPort",
            Label = "Query Port",
            Type = "Port",
            DefaultValue = "TBD"
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Warnings, warning => warning.Code == "config.port.default.missing");
    }

    [Fact]
    public void Optional_port_field_with_an_out_of_range_value_is_a_range_error_not_a_missing_warning()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.queryPort",
            Label = "Query Port",
            Type = "Port",
            DefaultValue = 70000
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.port.default.range");
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "config.port.default.missing");
    }

    [Fact]
    public void Optional_port_field_with_a_valid_value_has_no_warning_or_error()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "network.queryPort",
            Label = "Query Port",
            Type = "Port",
            DefaultValue = 27016
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "config.port.default.missing");
        Assert.DoesNotContain(result.Errors, error => error.Code == "config.port.default.range");
    }

    [Fact]
    public void Numeric_minimum_cannot_exceed_maximum()
    {
        var manifest = CreateValidManifest();
        var field = manifest.ConfigFields!.Single(field => field.Key == "server.maxPlayers");
        field.Minimum = 128;
        field.Maximum = 32;

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.range.invalid");
    }

    [Fact]
    public void Select_fields_require_options()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "server.mode",
            Label = "Mode",
            Type = "Select"
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.options.required");
    }

    [Fact]
    public void Invalid_validation_regex_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Single(field => field.Key == "server.name").ValidationPattern = "[";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "config.validationPattern.invalid");
    }

    [Fact]
    public void Valid_ports_have_no_errors()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "network.port", Required = true },
            new ManifestPort { Id = "rcon", Name = "RCON", Protocol = "tcp", FixedValue = 25575 },
            new ManifestPort { Id = "web", Name = "Web Admin", Protocol = "tcp", OffsetFrom = "game", Offset = 1 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Port_missing_id_or_name_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Protocol = "udp", ConfigField = "network.port" }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.id.required");
        Assert.Contains(result.Errors, error => error.Code == "ports.name.required");
    }

    [Fact]
    public void Duplicate_port_ids_are_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "network.port" },
            new ManifestPort { Id = "game", Name = "Duplicate", Protocol = "tcp", FixedValue = 9999 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.id.duplicate");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("carrier-pigeon")]
    [InlineData("99")]
    public void Port_protocol_must_be_tcp_udp_or_both(string? protocol)
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = protocol, ConfigField = "network.port" }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.protocol.unsupported");
    }

    [Fact]
    public void Port_with_no_source_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp" }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.source.exclusive");
    }

    [Fact]
    public void Port_with_more_than_one_source_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "network.port", FixedValue = 27015 }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.source.exclusive");
    }

    [Fact]
    public void Port_referencing_an_unknown_config_field_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "no.such.field" }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.configField.unknown");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Port_fixedValue_out_of_range_is_an_error(int fixedValue)
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "rcon", Name = "RCON", Protocol = "tcp", FixedValue = fixedValue }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.fixedValue.range");
    }

    [Fact]
    public void A_statically_known_fixed_range_extending_past_65535_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "voice", Name = "Voice", Protocol = "udp", FixedValue = 65535, RangeSize = 2 }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.fixedValue.rangeExceeds65535");
    }

    [Fact]
    public void An_extreme_rangeSize_does_not_overflow_past_the_65535_check()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "voice", Name = "Voice", Protocol = "udp", FixedValue = 1, RangeSize = int.MaxValue }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.fixedValue.rangeExceeds65535");
    }

    [Fact]
    public void An_extreme_rangeSize_does_not_overflow_past_the_static_overlap_check()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "voice", Name = "Voice", Protocol = "udp", FixedValue = 1, RangeSize = int.MaxValue },
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", FixedValue = 7777 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.fixedValue.overlap");
    }

    [Fact]
    public void Whitespace_padded_configField_and_offsetFrom_still_match_their_targets()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = " network.port " },
            new ManifestPort { Id = "web", Name = "Web Admin", Protocol = "tcp", OffsetFrom = " game ", Offset = 1 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void ToPorts_trims_configField_and_offsetFrom_so_resolver_lookups_still_match()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = " network.port " },
            new ManifestPort { Id = "web", Name = "Web Admin", Protocol = "tcp", OffsetFrom = " game ", Offset = 1 }
        ];

        var ports = manifest.ToPorts();

        var game = ports.Single(port => port.Id == "game");
        var web = ports.Single(port => port.Id == "web");
        Assert.Equal("network.port", game.ConfigField);
        Assert.Equal("game", web.OffsetFrom);

        var resolver = new ServerPortResolver();
        var result = resolver.Resolve(ports, new Dictionary<string, object?> { ["network.port"] = 7777 });

        Assert.All(result, port => Assert.Equal(ResolvedPortStatus.Resolved, port.Status));
    }

    [Fact]
    public void Two_fixed_ports_on_the_same_number_and_a_shared_protocol_statically_overlap()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", FixedValue = 7777 },
            new ManifestPort { Id = "voice", Name = "Voice", Protocol = "udp", FixedValue = 7777 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.fixedValue.overlap");
    }

    [Fact]
    public void Two_fixed_ports_on_the_same_number_but_disjoint_protocols_do_not_statically_overlap()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", FixedValue = 7777 },
            new ManifestPort { Id = "web", Name = "Web Admin", Protocol = "tcp", FixedValue = 7777 }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, error => error.Code == "ports.fixedValue.overlap");
    }

    [Fact]
    public void Port_offset_from_itself_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", OffsetFrom = "game", Offset = 1 }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.offsetFrom.selfReference");
    }

    [Fact]
    public void Port_offset_from_an_unknown_port_id_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "web", Name = "Web Admin", Protocol = "tcp", OffsetFrom = "no-such-port", Offset = 1 }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.offsetFrom.unknown");
    }

    [Fact]
    public void Port_rangeSize_below_one_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Ports = [new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "network.port", RangeSize = 0 }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "ports.rangeSize.invalid");
    }

    [Fact]
    public void ToPorts_converts_manifest_ports_into_domain_definitions()
    {
        var manifest = CreateValidManifest();
        manifest.Ports =
        [
            new ManifestPort { Id = "game", Name = "Game Port", Protocol = "udp", ConfigField = "network.port", Required = true },
            new ManifestPort { Id = "rcon", Name = "RCON", Protocol = "tcp", FixedValue = 25575, OpenExternally = false }
        ];

        var ports = manifest.ToPorts();

        Assert.Equal(2, ports.Count);
        var game = ports.Single(port => port.Id == "game");
        Assert.Equal(PortSource.ConfigField, game.Source);
        Assert.True(game.Required);
        var rcon = ports.Single(port => port.Id == "rcon");
        Assert.Equal(PortSource.Fixed, rcon.Source);
        Assert.False(rcon.OpenExternally);
    }

    [Fact]
    public void Backup_paths_cannot_escape_install_directory()
    {
        var manifest = CreateValidManifest();
        manifest.BackupTargets = [new ManifestBackupTarget { Key = "saves", Label = "Saves", Path = "../saves" }];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "path.escape");
    }

    [Fact]
    public void Unsupported_query_protocol_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.QueryProtocol = "MagicQuery";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "runtime.queryProtocol.unsupported");
    }

    [Fact]
    public void Unsupported_console_strategy_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.ConsoleStrategy = "Telepathy";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "runtime.consoleStrategy.unsupported");
    }

    [Theory]
    [InlineData("Redirected", ConsoleInputStrategy.Redirected)]
    [InlineData("WindowMessage", ConsoleInputStrategy.WindowMessage)]
    [InlineData("RconPreferred", ConsoleInputStrategy.RconPreferred)]
    [InlineData("LogTailOnly", ConsoleInputStrategy.LogTailOnly)]
    [InlineData("None", ConsoleInputStrategy.None)]
    public void Manifest_console_strategy_maps_to_runtime(string value, ConsoleInputStrategy expected)
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.ConsoleStrategy = value;

        Assert.Equal(expected, manifest.ToRuntime().EffectiveConsoleStrategy);
    }

    [Fact]
    public void Unknown_launch_placeholders_are_errors()
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.DefaultArguments = "-port {network.port} -missing {server.missing}";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "runtime.arguments.placeholder.unknown");
    }

    [Fact]
    public void Conditional_and_quoted_launch_placeholders_are_validated()
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.DefaultArguments = "{?server.public:-name {quote:server.name}} -port {network.port}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, error => error.Code == "runtime.arguments.placeholder.unknown");
    }

    [Fact]
    public void Nested_conditional_launch_placeholders_are_validated()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "network.overridePublicEndpoint", Label = "Override Public Endpoint", Type = "Boolean", DefaultValue = false });
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "network.publicIp", Label = "Public IP", Type = "Text" });
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "network.publicPort", Label = "Public Port", Type = "Port", DefaultValue = 27016 });
        manifest.Runtime!.DefaultArguments =
            "{?server.public:-publiclobby {?network.overridePublicEndpoint:{?network.publicIp:-publicip={quote:network.publicIp}} {?network.publicPort:-publicport={network.publicPort}}}}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Errors, error => error.Code == "runtime.arguments.placeholder.unknown");
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Password")]
    [InlineData("Path")]
    public void Raw_free_form_field_placeholder_in_launch_arguments_warns(string type)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "server.motd", Label = "MOTD", Type = type });
        manifest.Runtime!.DefaultArguments += " -motd {server.motd}";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Warnings, warning =>
            warning.Code == "runtime.arguments.placeholder.unquoted" &&
            warning.Message.Contains("server.motd", StringComparison.Ordinal));
    }

    [Fact]
    public void Quoted_free_form_field_placeholder_does_not_warn()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "server.motd", Label = "MOTD", Type = "Text" });
        manifest.Runtime!.DefaultArguments += " -motd {quote:server.motd}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "runtime.arguments.placeholder.unquoted");
    }

    [Theory]
    [InlineData("Number")]
    [InlineData("Boolean")]
    [InlineData("Port")]
    [InlineData("CommandLine")]
    public void Raw_constrained_field_placeholder_does_not_warn(string type)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "server.value", Label = "Value", Type = type, DefaultValue = type == "Port" ? 27020 : null });
        manifest.Runtime!.DefaultArguments += " -value {server.value}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "runtime.arguments.placeholder.unquoted");
    }

    [Fact]
    public void Raw_commandLine_field_other_than_additionalArguments_does_not_warn()
    {
        // Mirrors GenericWrapperModule.LaunchArgumentsKey ("launch.arguments"): a CommandLine field
        // is pre-composed, splice-in-as-is text by convention, not just server.additionalArguments
        // specifically. Warning here would push authors toward {quote:key}, which would merge
        // multiple intended arguments (e.g. "-foo -bar") into one and break the launch command.
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField { Key = "launch.arguments", Label = "Launch Arguments", Type = "CommandLine", DefaultValue = "" });
        manifest.Runtime!.DefaultArguments += " {launch.arguments}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "runtime.arguments.placeholder.unquoted");
    }

    [Fact]
    public void Raw_server_additionalArguments_placeholder_does_not_warn()
    {
        var manifest = CreateValidManifest();
        manifest.Runtime!.DefaultArguments += " {server.additionalArguments}";

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "runtime.arguments.placeholder.unquoted");
    }

    [Fact]
    public void Steam_customArguments_rejects_additional_commands()
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = "12345", CustomArguments = "+app_set_config 12345 mod foo" };

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "steam.customArguments.command");
    }

    [Theory]
    [InlineData("-beta unity")]
    [InlineData("-BETA experimental_branch-2")]
    public void Steam_safe_static_beta_argument_does_not_warn(string customArguments)
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = "12345", CustomArguments = customArguments };

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "steam.customArguments.present");
    }

    [Theory]
    [InlineData("-beta \"unity branch\"")]
    [InlineData("-betapassword secret")]
    public void Steam_nontrivial_beta_arguments_still_warn(string customArguments)
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = "12345", CustomArguments = customArguments };

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Warnings, warning => warning.Code == "steam.customArguments.present");
    }

    [Fact]
    public void Steam_custom_arguments_reject_quit_command()
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = "12345", CustomArguments = "-beta unity +quit" };

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "steam.customArguments.command");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("123 +quit")]
    [InlineData("+quit")]
    public void Steam_app_id_must_be_positive_numeric(string appId)
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = appId };

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "steam.appId.invalid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Steam_without_customArguments_does_not_warn(string? customArguments)
    {
        var manifest = CreateValidManifest();
        manifest.Steam = new ManifestSteam { AppId = "12345", CustomArguments = customArguments };

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "steam.customArguments.present");
    }

    [Fact]
    public void Manifest_without_a_steam_section_does_not_warn()
    {
        var manifest = CreateValidManifest();
        manifest.Steam = null;

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "steam.customArguments.present");
    }

    [Fact]
    public void Password_fields_are_valid_without_plaintext_storage_warning()
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "rcon.password",
            Label = "RCON Password",
            Type = "Password"
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.False(result.HasErrors);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "config.secret.plaintext");
    }

    [Theory]
    [InlineData("apiToken")]
    [InlineData("server.gslt")]
    [InlineData("rcon.password")]
    [InlineData("clientSecret")]
    [InlineData("apiKey")]
    public void Secret_like_text_fields_keep_plaintext_storage_warning(string key)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = key,
            Label = "Secret-like value",
            Type = "Text"
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.False(result.HasErrors);
        Assert.Contains(
            result.Warnings,
            warning => warning.Code == "config.secret.plaintext" &&
                warning.Path.EndsWith(".type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Path")]
    [InlineData("Cron")]
    [InlineData("CommandLine")]
    public void Secret_like_text_like_type_fields_keep_plaintext_storage_warning(string type)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "server.apiToken",
            Label = "API Token",
            Type = type,
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(
            result.Warnings,
            warning => warning.Code == "config.secret.plaintext" &&
                warning.Path.EndsWith(".type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Number")]
    [InlineData("Port")]
    [InlineData("Select")]
    [InlineData("MultiSelect")]
    public void Secret_like_non_text_fields_do_not_trigger_plaintext_storage_warning(string type)
    {
        var manifest = CreateValidManifest();
        manifest.ConfigFields!.Add(new ManifestConfigField
        {
            Key = "server.needPassword",
            Label = "Password required",
            Type = type,
            Options = type is "Select" or "MultiSelect"
                ? ["yes"]
                : null,
        });

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "config.secret.plaintext");
    }

    [Fact]
    public void Missing_module_api_version_is_legacy_warning_not_error()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = null;

        var result = ModuleValidator.Validate(manifest);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Warnings, warning => warning.Code == "compat.moduleApiVersion.missing");
    }

    [Fact]
    public void Current_module_api_version_is_compatible()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = ModuleCompatibility.CurrentModuleApiVersion;

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Messages, message => message.Code.StartsWith("compat.", StringComparison.Ordinal));
    }

    [Fact]
    public void Future_module_api_version_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = "999.0";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "compat.moduleApiVersion.future");
    }

    [Fact]
    public void Malformed_module_api_version_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = "tomorrow";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "compat.moduleApiVersion.invalid");
    }

    [Fact]
    public void Minimum_windows_gsh_version_above_current_is_an_error()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = ModuleCompatibility.CurrentModuleApiVersion;
        manifest.MinimumWindowsGshVersion = "999.0";

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "compat.minimumWindowsGshVersion.unsupported");
    }

    [Fact]
    public void Supported_windows_gsh_versions_must_include_current_version()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = ModuleCompatibility.CurrentModuleApiVersion;
        manifest.SupportedWindowsGshVersions = ["999.*"];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "compat.supportedWindowsGshVersions.unsupported");
    }

    [Fact]
    public void Major_only_supported_windows_gsh_wildcard_matches_all_minor_versions()
    {
        var manifest = CreateValidManifest();
        manifest.ModuleApiVersion = ModuleCompatibility.CurrentModuleApiVersion;
        manifest.SupportedWindowsGshVersions = ["1.*"];

        var compatibility = ModuleCompatibility.Evaluate(manifest, new Version(1, 2, 0));

        Assert.True(compatibility.IsCompatible);
        Assert.DoesNotContain(compatibility.Messages, message => message.Code == "compat.supportedWindowsGshVersions.unsupported");
    }

    [Fact]
    public void Module_manifest_validate_throws_on_errors()
    {
        var manifest = CreateValidManifest();
        manifest.EntryPoints!.Start = "../server.exe";

        var exception = Assert.Throws<InvalidOperationException>(() => manifest.Validate());

        Assert.Contains("Module manifest validation failed", exception.Message);
        Assert.Contains("entryPoints.start", exception.Message);
    }

    [Fact]
    public void ToCapabilities_includes_console_command_capability()
    {
        var manifest = CreateValidManifest();
        manifest.Capabilities = new ManifestCapabilities { ConsoleCommands = true };

        var capabilities = manifest.ToCapabilities();

        Assert.True(capabilities.SupportsConsoleCommands);
    }

    [Fact]
    public void ToCapabilities_includes_java_requirement_metadata()
    {
        var manifest = CreateValidManifest();
        manifest.Capabilities = new ManifestCapabilities
        {
            RequiresJava = true,
            MinimumJavaMajor = 21
        };

        var capabilities = manifest.ToCapabilities();

        Assert.True(capabilities.RequiresJava);
        Assert.Equal(21, capabilities.MinimumJavaMajor);
    }

    [Fact]
    public void Automated_addon_package_maps_to_definition()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "plugin",
                Name = "Plugin",
                SourceName = "Plugin Catalog",
                SourceVersion = "1.2",
                Package = new ManifestAddonPackage
                {
                    Kind = "File",
                    SourceUrl = "https://example.invalid/plugin.jar",
                    InstallPath = "plugins",
                    FileName = "plugin.jar",
                    ExpectedSha256 = "abc123"
                }
            }
        ];

        var addon = Assert.Single(manifest.ToAddons());

        Assert.Equal(AddonPackageKind.File, addon.Package!.Kind);
        Assert.Equal("plugins", addon.Package.InstallPath);
        Assert.Equal("Plugin Catalog", addon.SourceName);
        Assert.Equal("1.2", addon.SourceVersion);
        Assert.Equal("abc123", addon.Package.ExpectedSha256);
    }

    [Fact]
    public void Addon_package_paths_cannot_escape_install_directory()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "bad",
                Name = "Bad",
                Package = new ManifestAddonPackage
                {
                    Kind = "Zip",
                    SourceUrl = "https://example.invalid/addon.zip",
                    InstallPath = "../outside"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Path == "addons[0].package.installPath");
    }

    [Fact]
    public void Addon_package_requires_install_path()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "bad",
                Name = "Bad",
                Package = new ManifestAddonPackage
                {
                    Kind = "Zip",
                    SourceUrl = "https://example.invalid/addon.zip"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error =>
            error.Code == "addon.package.installPath.required" &&
            error.Path == "addons[0].package.installPath");
    }

    [Fact]
    public void Addon_package_source_url_must_be_https()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "bad",
                Name = "Bad",
                Package = new ManifestAddonPackage
                {
                    Kind = "Zip",
                    SourceUrl = "http://example.invalid/addon.zip",
                    InstallPath = "addons"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error =>
            error.Code == "addon.package.sourceUrl.invalid" &&
            error.Path == "addons[0].package.sourceUrl");
    }

    [Fact]
    public void Addon_package_without_expected_hash_produces_a_warning()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "plugin",
                Name = "Plugin",
                Package = new ManifestAddonPackage
                {
                    Kind = "Zip",
                    SourceUrl = "https://example.invalid/addon.zip",
                    InstallPath = "addons"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Warnings, warning =>
            warning.Code == "addon.package.expectedSha256.missing" &&
            warning.Path == "addons[0].package.expectedSha256");
    }

    [Fact]
    public void Addon_package_with_expected_hash_does_not_warn()
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "plugin",
                Name = "Plugin",
                Package = new ManifestAddonPackage
                {
                    Kind = "Zip",
                    SourceUrl = "https://example.invalid/addon.zip",
                    InstallPath = "addons",
                    ExpectedSha256 = "abc123"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == "addon.package.expectedSha256.missing");
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public void Addon_package_kind_must_be_a_defined_enum_name(string kind)
    {
        var manifest = CreateValidManifest();
        manifest.Addons =
        [
            new ManifestAddon
            {
                Id = "bad",
                Name = "Bad",
                Package = new ManifestAddonPackage
                {
                    Kind = kind,
                    SourceUrl = "https://example.invalid/addon.zip",
                    InstallPath = "addons"
                }
            }
        ];

        var result = ModuleValidator.Validate(manifest);

        Assert.Contains(result.Errors, error =>
            error.Code == "addon.package.kind.invalid" &&
            error.Path == "addons[0].package.kind");
    }

    private static ModuleManifest CreateValidManifest()
    {
        return new ModuleManifest
        {
            Id = "test-module",
            Name = "Test Module",
            Version = "1.0.0",
            ModuleApiVersion = ModuleCompatibility.CurrentModuleApiVersion,
            EntryPoints = new ManifestEntryPoints
            {
                Start = "server.exe",
                ProcessName = "server"
            },
            Runtime = new ManifestRuntime
            {
                QueryProtocol = "A2S",
                PortIncrements = 1,
                DefaultArguments = "-name {quote:server.name} -port {network.port} {?server.public:-public}"
            },
            ConfigFields =
            [
                new ManifestConfigField
                {
                    Key = "server.name",
                    Label = "Server Name",
                    Type = "Text",
                    DefaultValue = "Test Server",
                    Required = true
                },
                new ManifestConfigField
                {
                    Key = "network.port",
                    Label = "Server Port",
                    Type = "Port",
                    DefaultValue = 27015,
                    Minimum = 1,
                    Maximum = 65535
                },
                new ManifestConfigField
                {
                    Key = "server.maxPlayers",
                    Label = "Max Players",
                    Type = "Number",
                    DefaultValue = 16,
                    Minimum = 1,
                    Maximum = 128
                },
                new ManifestConfigField
                {
                    Key = "server.public",
                    Label = "Public Server",
                    Type = "Boolean",
                    DefaultValue = true
                }
            ],
            BackupTargets =
            [
                new ManifestBackupTarget
                {
                    Key = "saves",
                    Label = "Saves",
                    Path = "saves",
                    Type = "directory"
                }
            ]
        };
    }
}
