using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerActionMessageFormatterTests
{
    [Theory]
    [InlineData("Icarus", "Booting", "Icarus cannot be started while it is Booting.")]
    [InlineData("Valheim", "Running", "Valheim cannot be started while it is Running.")]
    public void CannotStart_includes_current_status(string serverName, string statusText, string expected)
    {
        Assert.Equal(expected, ServerActionMessageFormatter.CannotStart(serverName, statusText));
    }

    [Theory]
    [InlineData("Icarus", "Icarus start command sent.")]
    [InlineData("Valheim", "Valheim start command sent.")]
    public void StartCommandSent_formats_success(string serverName, string expected)
    {
        Assert.Equal(expected, ServerActionMessageFormatter.StartCommandSent(serverName));
    }

    [Fact]
    public void Stop_messages_cover_precondition_and_success()
    {
        Assert.Equal("Icarus is already offline.", ServerActionMessageFormatter.AlreadyOffline("Icarus"));
        Assert.Equal("Icarus stopped.", ServerActionMessageFormatter.Stopped("Icarus"));
    }

    [Fact]
    public void RestartCommandSent_formats_success()
    {
        Assert.Equal("Icarus restart command sent.", ServerActionMessageFormatter.RestartCommandSent("Icarus"));
    }

    [Fact]
    public void Update_messages_cover_precondition_and_success()
    {
        Assert.Equal("Stop Icarus before updating it.", ServerActionMessageFormatter.StopBeforeUpdating("Icarus"));
        Assert.Equal("Icarus update completed.", ServerActionMessageFormatter.UpdateCompleted("Icarus"));
    }

    [Fact]
    public void Backup_messages_cover_preconditions_and_success()
    {
        Assert.Equal("Stop Icarus before creating a backup.", ServerActionMessageFormatter.StopBeforeBackup("Icarus"));
        Assert.Equal("Icarus does not support backups.", ServerActionMessageFormatter.BackupsUnsupported("Icarus"));
        Assert.Equal("Icarus backup created: backup.zip", ServerActionMessageFormatter.BackupCreated("Icarus", "backup.zip"));
    }

    [Fact]
    public void Rcon_messages_cover_usage_offline_success_and_failure()
    {
        Assert.Equal("Icarus is offline.", ServerActionMessageFormatter.IsOffline("Icarus"));
        Assert.Equal("Usage: `!wgsh send 1 <command>`", ServerActionMessageFormatter.SendUsage("!", "1"));
        Assert.Equal("Usage: `!wgsh sendr 1 <command>`", ServerActionMessageFormatter.SendUsage("!", "1", "sendr"));
        Assert.Equal("Icarus command sent.", ServerActionMessageFormatter.RconCommandSent("Icarus"));
        Assert.Equal("Icarus console command sent.", ServerActionMessageFormatter.ConsoleCommandSent("Icarus"));
        Assert.Equal(
            "Failed to send command to Icarus: the console command timed out.",
            ServerActionMessageFormatter.ConsoleCommandTimedOut("Icarus"));
        Assert.Equal(
            "Failed to send command to Icarus due to an internal error.",
            ServerActionMessageFormatter.ConsoleCommandFailed("Icarus"));
        Assert.Equal(
            "A previous console command for Icarus is still running.",
            ServerActionMessageFormatter.ConsoleCommandStillRunning("Icarus"));
        Assert.Equal(
            "Failed to send command to Icarus: timeout",
            ServerActionMessageFormatter.FailedToSendCommand("Icarus", "timeout"));
    }

    [Theory]
    [InlineData("start", "Failed to start Icarus: boom")]
    [InlineData("stop", "Failed to stop Icarus: boom")]
    [InlineData("restart", "Failed to restart Icarus: boom")]
    [InlineData("update", "Failed to update Icarus: boom")]
    [InlineData("backup", "Failed to back up Icarus: boom")]
    public void Failure_messages_match_action(string action, string expected)
    {
        var actual = action switch
        {
            "start" => ServerActionMessageFormatter.FailedToStart("Icarus", "boom"),
            "stop" => ServerActionMessageFormatter.FailedToStop("Icarus", "boom"),
            "restart" => ServerActionMessageFormatter.FailedToRestart("Icarus", "boom"),
            "update" => ServerActionMessageFormatter.FailedToUpdate("Icarus", "boom"),
            "backup" => ServerActionMessageFormatter.FailedToBackUp("Icarus", "boom"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Lookup_messages_quote_user_input()
    {
        Assert.Equal("Server `abc` was not found.", ServerActionMessageFormatter.ServerNotFound("abc"));
        Assert.Equal("Unknown action `dance`.", ServerActionMessageFormatter.UnknownAction("dance"));
        Assert.Equal("Icarus is already busy.", ServerActionMessageFormatter.AlreadyBusy("Icarus"));
    }
}
