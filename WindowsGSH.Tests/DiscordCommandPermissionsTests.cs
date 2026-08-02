using WindowsGSH.Core.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordCommandPermissionsTests
{
    [Fact]
    public void Logs_without_server_id_requires_global_admin()
    {
        Assert.False(DiscordCommandPermissions.HasPermission(["3"], "logs", null));
        Assert.True(DiscordCommandPermissions.HasPermission(["0"], "logs", null));
    }

    [Fact]
    public void Logs_with_server_id_requires_that_server_or_global_admin()
    {
        Assert.True(DiscordCommandPermissions.HasPermission(["3"], "logs", "3"));
        Assert.True(DiscordCommandPermissions.HasPermission(["0"], "logs", "3"));
        Assert.False(DiscordCommandPermissions.HasPermission(["2"], "logs", "3"));
    }

    [Theory]
    [InlineData("send")]
    [InlineData("sendr")]
    [InlineData("restart")]
    [InlineData("stop")]
    public void Server_actions_require_target_server_access(string command)
    {
        Assert.True(DiscordCommandPermissions.HasPermission(["2"], command, "2"));
        Assert.False(DiscordCommandPermissions.HasPermission(["1"], command, "2"));
    }

    [Fact]
    public void Stopall_requires_global_admin()
    {
        Assert.False(DiscordCommandPermissions.HasPermission(["1", "2"], "stopall", null));
        Assert.True(DiscordCommandPermissions.HasPermission(["0"], "stopall", null));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("update")]
    [InlineData("backup")]
    [InlineData("send")]
    [InlineData("sendr")]
    [InlineData("stopall")]
    public void Destructive_commands_are_blocked_when_remote_control_is_disabled(string command)
    {
        Assert.True(DiscordCommandPermissions.IsDestructiveCommand(command));
        Assert.False(DiscordCommandPermissions.IsAllowedByRemoteControlSetting(command, allowDestructiveCommands: false));
        Assert.True(DiscordCommandPermissions.IsAllowedByRemoteControlSetting(command, allowDestructiveCommands: true));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("check")]
    [InlineData("list")]
    [InlineData("status")]
    [InlineData("stats")]
    [InlineData("logs")]
    public void Read_only_commands_are_allowed_when_remote_control_is_disabled(string command)
    {
        Assert.False(DiscordCommandPermissions.IsDestructiveCommand(command));
        Assert.True(DiscordCommandPermissions.IsAllowedByRemoteControlSetting(command, allowDestructiveCommands: false));
    }

    [Theory]
    [InlineData("logs", new[] { "3" }, "3")]
    [InlineData("send", new[] { "1", "say", "hello" }, "1")]
    [InlineData("stopall", new string[0], null)]
    public void Target_server_id_is_extracted_for_server_scoped_commands(string command, string[] args, string? expected)
    {
        Assert.Equal(expected, DiscordCommandPermissions.GetTargetServerId(command, args));
    }
}
