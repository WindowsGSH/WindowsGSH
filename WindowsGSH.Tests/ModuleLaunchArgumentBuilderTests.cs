using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleLaunchArgumentBuilderTests
{
    [Fact]
    public void Raw_placeholders_preserve_existing_unquoted_behavior()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-name {server.name}", Settings(("server.name", "My Server")));

        Assert.Equal("-name My Server", args);
    }

    [Fact]
    public void Quoted_placeholders_quote_values_with_spaces()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-name {quote:server.name}", Settings(("server.name", "My Server")));

        Assert.Equal("-name \"My Server\"", args);
    }

    [Fact]
    public void Quoted_placeholders_escape_embedded_quotes()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-name {quote:server.name}", Settings(("server.name", "Bob \"The Boss\"")));

        Assert.Equal("-name \"Bob \\\"The Boss\\\"\"", args);
    }

    [Fact]
    public void Empty_quoted_placeholders_emit_empty_quoted_argument()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-password {quote:rcon.password}", Settings(("rcon.password", "")));

        Assert.Equal("-password \"\"", args);
    }

    [Fact]
    public void Additional_arguments_are_appended_when_template_does_not_place_them()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-port {network.port}", Settings(
            ("network.port", "27015"),
            ("server.additionalArguments", "-debug -tickrate 66")));

        Assert.Equal("-port 27015 -debug -tickrate 66", args);
    }

    [Fact]
    public void Additional_arguments_are_not_appended_when_template_places_them()
    {
        var args = ModuleLaunchArgumentBuilder.Build("{server.additionalArguments} -port {network.port}", Settings(
            ("network.port", "27015"),
            ("server.additionalArguments", "-debug")));

        Assert.Equal("-debug -port 27015", args);
    }

    [Fact]
    public void Conditional_placeholders_render_when_value_is_truthy()
    {
        var args = ModuleLaunchArgumentBuilder.Build("{?server.public:-public}", Settings(("server.public", true)));

        Assert.Equal("-public", args);
    }

    [Fact]
    public void Conditional_placeholders_are_removed_when_value_is_false()
    {
        var args = ModuleLaunchArgumentBuilder.Build("{?server.public:-public}", Settings(("server.public", false)));

        Assert.Equal("", args);
    }

    [Fact]
    public void Conditional_placeholders_can_contain_quoted_placeholders()
    {
        var args = ModuleLaunchArgumentBuilder.Build(
            "{?server.name:-name {quote:server.name}}",
            Settings(("server.name", "My Server")));

        Assert.Equal("-name \"My Server\"", args);
    }

    [Fact]
    public void False_conditional_with_quoted_placeholder_does_not_leave_closing_brace()
    {
        var args = ModuleLaunchArgumentBuilder.Build(
            "{?network.publicIp:-publicip={quote:network.publicIp}} -port={network.port}",
            Settings(("network.publicIp", ""), ("network.port", "8213")));

        Assert.Equal(" -port=8213", args);
    }

    [Fact]
    public void Palworld_template_removes_empty_public_ip_conditional_cleanly()
    {
        var args = ModuleLaunchArgumentBuilder.Build(
            "{?server.publicLobby:-publiclobby} {?network.publicIp:-publicip={quote:network.publicIp}} -port={network.port}",
            Settings(("server.publicLobby", true), ("network.publicIp", ""), ("network.port", "8213")));

        Assert.DoesNotContain("}", args);
        Assert.Equal("-publiclobby  -port=8213", args);
    }

    [Fact]
    public void Conditional_placeholders_treat_numeric_zero_strings_as_false()
    {
        var args = ModuleLaunchArgumentBuilder.Build(
            "-port={network.port} {?launch.workerThreads:-NumberOfWorkerThreadsServer={launch.workerThreads}}",
            Settings(("network.port", "8211"), ("launch.workerThreads", "0")));

        Assert.Equal("-port=8211 ", args);
    }

    [Fact]
    public void Nested_conditionals_only_render_when_outer_and_inner_values_are_truthy()
    {
        var template = "{?server.publicLobby:-publiclobby {?network.overridePublicEndpoint:{?network.publicIp:-publicip={quote:network.publicIp}} {?network.publicPort:-publicport={network.publicPort}}}}";
        var args = ModuleLaunchArgumentBuilder.Build(
            template,
            Settings(
                ("server.publicLobby", true),
                ("network.overridePublicEndpoint", false),
                ("network.publicIp", ""),
                ("network.publicPort", "8211")));

        Assert.Equal("-publiclobby ", args);
    }

    [Fact]
    public void Nested_conditionals_render_configured_public_endpoint_values()
    {
        var template = "{?server.publicLobby:-publiclobby {?network.overridePublicEndpoint:{?network.publicIp:-publicip={quote:network.publicIp}} {?network.publicPort:-publicport={network.publicPort}}}}";
        var args = ModuleLaunchArgumentBuilder.Build(
            template,
            Settings(
                ("server.publicLobby", true),
                ("network.overridePublicEndpoint", true),
                ("network.publicIp", "203.0.113.10"),
                ("network.publicPort", "8211")));

        Assert.Equal("-publiclobby -publicip=203.0.113.10 -publicport=8211", args);
    }

    [Fact]
    public void Quoting_preserves_trailing_backslashes()
    {
        var args = ModuleLaunchArgumentBuilder.Build("-path {quote:install.path}", Settings(("install.path", "C:\\Servers\\My Game\\")));

        Assert.Equal("-path \"C:\\Servers\\My Game\\\\\"", args);
    }

    private static Dictionary<string, object?> Settings(params (string Key, object? Value)[] values)
    {
        return values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
