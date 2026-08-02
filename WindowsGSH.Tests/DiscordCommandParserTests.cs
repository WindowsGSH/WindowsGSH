using WindowsGSH.Core.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordCommandParserTests
{
    [Fact]
    public void Parse_simple_command()
    {
        var result = DiscordCommandParser.Parse("status 12");

        Assert.True(result.IsValid);
        Assert.Equal("status", result.Command);
        Assert.Equal(["12"], result.Arguments);
    }

    [Fact]
    public void Parse_keeps_quoted_arguments_together()
    {
        var result = DiscordCommandParser.Parse("send 1 say \"hello everyone\"");

        Assert.True(result.IsValid);
        Assert.Equal("send", result.Command);
        Assert.Equal(["1", "say", "hello everyone"], result.Arguments);
    }

    [Fact]
    public void Parse_supports_escaped_quotes_inside_quoted_arguments()
    {
        var result = DiscordCommandParser.Parse("send 1 say \"hello \\\"team\\\"\"");

        Assert.True(result.IsValid);
        Assert.Equal(["1", "say", "hello \"team\""], result.Arguments);
    }

    [Fact]
    public void Parse_reports_unclosed_quotes()
    {
        var result = DiscordCommandParser.Parse("send 1 say \"hello everyone");

        Assert.False(result.IsValid);
        Assert.Equal("Command has an unclosed quote.", result.ErrorMessage);
    }

    [Fact]
    public void Parse_empty_input_defaults_to_help()
    {
        var result = DiscordCommandParser.Parse("");

        Assert.True(result.IsValid);
        Assert.Equal("help", result.Command);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void Parse_whitespace_input_defaults_to_help()
    {
        var result = DiscordCommandParser.Parse(" \t  ");

        Assert.True(result.IsValid);
        Assert.Equal("help", result.Command);
        Assert.Empty(result.Arguments);
    }

    [Fact]
    public void Parse_send_keeps_quoted_command_payload_together()
    {
        var result = DiscordCommandParser.Parse("send 1 \"say hello world\"");

        Assert.True(result.IsValid);
        Assert.Equal("send", result.Command);
        Assert.Equal(["1", "say hello world"], result.Arguments);
    }

    [Fact]
    public void Parse_sendr_keeps_quoted_command_payload_together()
    {
        var result = DiscordCommandParser.Parse("sendr 1 \"say hello world\"");

        Assert.True(result.IsValid);
        Assert.Equal("sendr", result.Command);
        Assert.Equal(["1", "say hello world"], result.Arguments);
    }

    [Fact]
    public void Parse_ignores_extra_whitespace_between_arguments()
    {
        var result = DiscordCommandParser.Parse("  send   1   \"say hello world\"   ");

        Assert.True(result.IsValid);
        Assert.Equal("send", result.Command);
        Assert.Equal(["1", "say hello world"], result.Arguments);
    }
}
