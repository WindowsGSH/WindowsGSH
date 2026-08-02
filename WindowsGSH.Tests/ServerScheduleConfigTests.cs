using System.Text.Json;
using WindowsGSH.Core.Scheduling;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerScheduleConfigTests
{
    [Fact]
    public void FromConfigJson_reads_action_command_and_api_schedules()
    {
        var config = ServerScheduleConfig.FromConfigJson("""
            {
              "schedules": {
                "start": { "enabled": true, "cron": "0 9 * * *" },
                "stop": { "enabled": false, "cron": "0 2 * * *" },
                "rconCommands": [
                  { "enabled": true, "cron": "55 23 * * *", "command": "save-all" }
                ],
                "consoleCommands": [
                  { "enabled": true, "cron": "*/30 * * * *", "command": "say Scheduled message" }
                ],
                "apiActions": [
                  {
                    "enabled": true,
                    "cron": "*/15 * * * *",
                    "action": "broadcast",
                    "parameters": { "message": "Restart soon" }
                  }
                ],
                "externalCommands": [
                  {
                    "enabled": true,
                    "cron": "0 * * * *",
                    "executable": "C:\\Tools\\sync.exe",
                    "arguments": "--server one",
                    "workingDirectory": "C:\\Tools",
                    "timeoutSeconds": 90
                  }
                ]
              }
            }
            """);

        Assert.True(config.Start.Enabled);
        Assert.Equal("0 9 * * *", config.Start.Cron);
        Assert.False(config.Stop.Enabled);
        Assert.Single(config.RconCommands);
        Assert.Equal("save-all", config.RconCommands[0].Command);
        Assert.Single(config.ConsoleCommands);
        Assert.Equal("say Scheduled message", config.ConsoleCommands[0].Command);
        Assert.Single(config.ApiActions);
        Assert.Equal("broadcast", config.ApiActions[0].ActionKey);
        Assert.Contains("Restart soon", config.ApiActions[0].ParametersJson);
        var external = Assert.Single(config.ExternalCommands);
        Assert.Equal(@"C:\Tools\sync.exe", external.ExecutablePath);
        Assert.Equal("--server one", external.Arguments);
        Assert.Equal(90, external.TimeoutSeconds);
    }

    [Fact]
    public void Console_command_schedules_round_trip()
    {
        var original = new ServerScheduleConfig(
            new CronActionSchedule(true, "0 9 * * *"),
            CronActionSchedule.Empty,
            CronActionSchedule.Empty,
            CronActionSchedule.Empty,
            new CronActionSchedule(true, "0 5 * * *"),
            [new ScheduledCommand(true, "10 * * * *", "rcon command")],
            [new ScheduledCommand(true, "55 23 * * *", "say Server restarting in 5 minutes")],
            [new ScheduledApiAction(true, "*/15 * * * *", "broadcast", """{ "message": "Restart soon" }""")],
            [new ScheduledExternalCommand(true, "0 * * * *", @"C:\Tools\sync.exe", "--sync", @"C:\Tools", 120)]);

        var roundTripped = ServerScheduleConfig.FromSchedulesJson(original.ToJsonObject().ToJsonString());

        Assert.Equal(original.Start, roundTripped.Start);
        Assert.Equal(original.Restart, roundTripped.Restart);
        Assert.Equal(original.RconCommands, roundTripped.RconCommands);
        Assert.Equal(original.ConsoleCommands, roundTripped.ConsoleCommands);
        Assert.Equal(original.ApiActions[0].Enabled, roundTripped.ApiActions[0].Enabled);
        Assert.Equal(original.ApiActions[0].Cron, roundTripped.ApiActions[0].Cron);
        Assert.Equal(original.ApiActions[0].ActionKey, roundTripped.ApiActions[0].ActionKey);
        Assert.Equal(
            JsonDocument.Parse(original.ApiActions[0].ParametersJson).RootElement.GetProperty("message").GetString(),
            JsonDocument.Parse(roundTripped.ApiActions[0].ParametersJson).RootElement.GetProperty("message").GetString());
        Assert.Equal(original.ExternalCommands, roundTripped.ExternalCommands);
    }

    [Fact]
    public void Missing_schedules_returns_empty_config()
    {
        var config = ServerScheduleConfig.FromConfigJson("""{ "name": "Server" }""");

        Assert.Equal(CronActionSchedule.Empty, config.Start);
        Assert.Empty(config.RconCommands);
        Assert.Empty(config.ConsoleCommands);
        Assert.Empty(config.ApiActions);
        Assert.Empty(config.ExternalCommands);
    }

    [Fact]
    public void Command_schedules_preserve_disabled_and_blank_rows_for_stable_indexes()
    {
        var config = ServerScheduleConfig.FromSchedulesJson("""
            {
              "consoleCommands": [
                { "enabled": false, "cron": "*/5 * * * *", "command": "say disabled" },
                { "enabled": true, "cron": "", "command": "say blank cron" },
                { "enabled": true, "cron": "*/10 * * * *", "command": "say active" }
              ]
            }
            """);

        Assert.Equal(3, config.ConsoleCommands.Count);
        Assert.False(config.ConsoleCommands[0].Enabled);
        Assert.Equal("", config.ConsoleCommands[1].Cron);
        Assert.Equal("say active", config.ConsoleCommands[2].Command);
    }

    [Fact]
    public void Api_schedules_use_empty_object_when_parameters_are_missing_or_hidden()
    {
        var config = ServerScheduleConfig.FromSchedulesJson("""
            {
              "apiActions": [
                { "enabled": true, "cron": "*/15 * * * *", "action": "broadcast" },
                { "enabled": true, "cron": "*/30 * * * *", "action": "restart", "parameters": ["ignored"] }
              ]
            }
            """);

        Assert.Equal(2, config.ApiActions.Count);
        Assert.Equal("{}", config.ApiActions[0].ParametersJson);
        Assert.Equal("{}", config.ApiActions[1].ParametersJson);
    }

    [Fact]
    public void Api_parameters_must_be_json_object_when_serializing()
    {
        var config = ServerScheduleConfig.Empty with
        {
            ApiActions = [new ScheduledApiAction(true, "* * * * *", "bad", """["not", "object"]""")]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => config.ToJsonObject());

        Assert.Contains("Scheduled API action parameters must be a JSON object", exception.Message);
    }

    [Theory]
    [InlineData("\"300\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void External_command_timeout_uses_default_for_non_number_values(string timeoutJson)
    {
        var config = ServerScheduleConfig.FromSchedulesJson(
            $$"""
            {
              "externalCommands": [
                {
                  "enabled": true,
                  "cron": "0 * * * *",
                  "executable": "C:\\Tools\\sync.exe",
                  "timeoutSeconds": {{timeoutJson}}
                }
              ]
            }
            """);

        Assert.Equal(300, Assert.Single(config.ExternalCommands).TimeoutSeconds);
    }
}
