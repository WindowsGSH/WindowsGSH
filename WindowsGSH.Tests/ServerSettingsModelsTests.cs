using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.Scheduling;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerSettingsModelsTests
{
    [Fact]
    public void FromConfigJson_reads_typed_app_owned_sections()
    {
        var settings = ServerConfigAppSettings.FromConfigJson("""
            {
              "automation": {
                "autoStart": true,
                "autoRestart": true,
                "autoUpdate": true,
                "updateOnStart": true,
                "restartOnCrash": true,
                "restartDelaySeconds": 45,
                "maxRestartAttempts": 5,
                "lastAutoRestartAt": "2026-05-24T10:00:00+00:00",
                "lastCrashDetectedAt": "2026-05-24T09:00:00+00:00"
              },
              "schedules": {
                "restart": { "enabled": true, "cron": "0 5 * * *" }
              },
              "backup": {
                "backupOnStart": true,
                "paths": [ "world", "server.properties" ],
                "destination": "D:\\Backups\\Paper"
              },
              "discord": {
                "channel": "server-alerts",
                "includeOnDashboard": false,
                "useWebhookOverride": true,
                "showMap": false,
                "showPlayerList": false
              },
              "runtimeSettings": {
                "argumentsOverride": "-nogui",
                "processPriorityClass": "AboveNormal",
                "cpuAffinityMask": "3",
                "applyPriorityOnStart": true,
                "applyAffinityOnStart": true,
                "allowEmbeddedConsole": true,
                "showExternalConsole": true,
                "workingDirectoryOverride": "custom"
              },
              "java": {
                "runtimePath": "C:\\Java\\bin\\java.exe",
                "memoryPreset": "8 GB",
                "initialMemoryMb": 2048,
                "maximumMemoryMb": 8192,
                "additionalJvmArguments": "-XX:+UseG1GC"
              },
              "network": {
                "lastKnownPublicIp": "203.0.113.10",
                "lastPublicIpCheckedAt": "2026-05-24T11:00:00+00:00"
              }
            }
            """);

        Assert.True(settings.Automation.AutoStart);
        Assert.True(settings.Automation.BackupOnStart);
        Assert.True(settings.Automation.RestartOnCrash);
        Assert.Equal(45, settings.Automation.RestartDelaySeconds);
        Assert.Equal(5, settings.Automation.MaxRestartAttempts);
        Assert.Equal(new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero), settings.Automation.LastAutoRestartAt);
        Assert.True(settings.Schedules.Restart.Enabled);
        Assert.Equal("0 5 * * *", settings.Schedules.Restart.Cron);
        Assert.Equal(["world", "server.properties"], settings.Backup.Paths);
        Assert.Equal(@"D:\Backups\Paper", settings.Backup.Destination);
        Assert.Equal("server-alerts", settings.Discord.Channel);
        Assert.False(settings.Discord.IncludeOnDashboard);
        Assert.True(settings.Discord.UseWebhookOverride);
        Assert.False(settings.Discord.ShowMap);
        Assert.False(settings.Discord.ShowPlayerList);
        Assert.True(settings.Discord.ShowQuerySummary);
        Assert.Equal("-nogui", settings.Runtime.ArgumentsOverride);
        Assert.Equal("AboveNormal", settings.Runtime.ProcessPriorityClass);
        Assert.Equal("3", settings.Runtime.CpuAffinityMask);
        Assert.True(settings.Runtime.ApplyPriorityOnStart);
        Assert.True(settings.Runtime.ApplyAffinityOnStart);
        Assert.True(settings.Runtime.AllowEmbeddedConsole);
        Assert.True(settings.Runtime.ShowExternalConsole);
        Assert.Equal("custom", settings.Runtime.WorkingDirectoryOverride);
        Assert.Equal(@"C:\Java\bin\java.exe", settings.Java.RuntimePath);
        Assert.Equal("8 GB", settings.Java.MemoryPreset);
        Assert.Equal(2048, settings.Java.InitialMemoryMb);
        Assert.Equal(8192, settings.Java.MaximumMemoryMb);
        Assert.Equal("-XX:+UseG1GC", settings.Java.AdditionalJvmArguments);
        Assert.Equal("203.0.113.10", settings.Network.LastKnownPublicIp);
        Assert.Equal(new DateTimeOffset(2026, 5, 24, 11, 0, 0, TimeSpan.Zero), settings.Network.LastPublicIpCheckedAt);
    }

    [Fact]
    public void FromConfigJson_uses_defaults_for_missing_sections()
    {
        var settings = ServerConfigAppSettings.FromConfigJson("""{ "name": "Server" }""");

        Assert.Equal(ServerAutomationSettings.Default, settings.Automation);
        Assert.Equal(ServerScheduleConfig.Empty, settings.Schedules);
        Assert.Equal(ServerBackupSettings.Default, settings.Backup);
        Assert.Equal(ServerDiscordSettings.Default, settings.Discord);
        Assert.Equal(ServerRuntimeSettings.Default, settings.Runtime);
        Assert.Equal(ServerJavaSettings.Default, settings.Java);
        Assert.Equal(ServerNetworkSettings.Default, settings.Network);
    }

    [Fact]
    public void Java_settings_migrate_legacy_module_fields()
    {
        var settings = ServerConfigAppSettings.FromConfigJson("""
            {
              "settings": {
                "java.path": "C:\\LegacyJava",
                "java.xms": "2G",
                "java.xmx": "6144M",
                "server.additionalJvmArgs": "-XX:+UseZGC"
              }
            }
            """);

        Assert.Equal(@"C:\LegacyJava", settings.Java.RuntimePath);
        Assert.Equal(2048, settings.Java.InitialMemoryMb);
        Assert.Equal(6144, settings.Java.MaximumMemoryMb);
        Assert.Equal(ServerJavaSettings.CustomPreset, settings.Java.MemoryPreset);
        Assert.Equal("-XX:+UseZGC", settings.Java.AdditionalJvmArguments);
    }

    [Fact]
    public void ApplyTo_preserves_unknown_root_and_module_settings()
    {
        var root = JsonNode.Parse("""
            {
              "id": "1",
              "name": "Paper",
              "settings": {
                "minecraft.version": "1.21.1"
              },
              "customSection": {
                "keep": true
              }
            }
            """)!.AsObject();
        var settings = ServerConfigAppSettings.Empty with
        {
            Automation = ServerAutomationSettings.Default with
            {
                AutoStart = true,
                BackupOnStart = true
            },
            Schedules = ServerScheduleConfig.Empty with
            {
                Start = new CronActionSchedule(true, "0 9 * * *")
            },
            Backup = ServerBackupSettings.Default with
            {
                Paths = ["world"],
                Destination = "archives"
            },
            Discord = ServerDiscordSettings.Default with
            {
                Channel = "alerts",
                UseWebhookOverride = true,
                ShowMap = false
            }
        };

        settings.ApplyTo(root);

        Assert.True(root["customSection"]!["keep"]!.GetValue<bool>());
        Assert.Equal("1.21.1", root["settings"]!["minecraft.version"]!.GetValue<string>());
        Assert.True(root["automation"]!["autoStart"]!.GetValue<bool>());
        Assert.True(root["backup"]!["backupOnStart"]!.GetValue<bool>());
        Assert.Equal("world", root["backup"]!["paths"]![0]!.GetValue<string>());
        Assert.Equal("archives", root["backup"]!["destination"]!.GetValue<string>());
        Assert.Equal("0 9 * * *", root["schedules"]!["start"]!["cron"]!.GetValue<string>());
        Assert.Equal("alerts", root["discord"]!["channel"]!.GetValue<string>());
        Assert.True(root["discord"]!["useWebhookOverride"]!.GetValue<bool>());
        Assert.False(root["discord"]!["showMap"]!.GetValue<bool>());
        Assert.True(root["discord"]!["showPlayerList"]!.GetValue<bool>());
        Assert.Null(root["java"]);
    }

    [Fact]
    public void Module_settings_preserve_existing_value_types()
    {
        using var document = JsonDocument.Parse("""
            {
              "settings": {
                "server.name": "Paper",
                "server.port": 25565,
                "rcon.enabled": true,
                "memory": 1.5,
                "objectValue": { "ignored": true }
              }
            }
            """);

        var moduleSettings = ServerModuleSettings.FromSettingsElement(document.RootElement.GetProperty("settings"));

        Assert.Equal("Paper", moduleSettings.Values["server.name"]);
        Assert.Equal(25565, moduleSettings.Values["server.port"]);
        Assert.Equal(true, moduleSettings.Values["rcon.enabled"]);
        Assert.Equal(1.5d, moduleSettings.Values["memory"]);
        Assert.Contains("ignored", moduleSettings.Values["objectValue"]?.ToString());
    }
}
