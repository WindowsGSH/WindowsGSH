using System.Diagnostics;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleRconAvailabilityTests
{
    [Fact]
    public void CanUseRcon_when_module_declares_rcon_fields_and_rcon_is_enabled()
    {
        var module = new TestModule(
            supportsRcon: false,
            fields:
            [
                new ConfigFieldDefinition("rcon.enabled", "Enable RCON", ConfigFieldType.Boolean),
                new ConfigFieldDefinition("rcon.port", "RCON Port", ConfigFieldType.Port),
                new ConfigFieldDefinition("rcon.password", "RCON Password", ConfigFieldType.Password)
            ]);
        var instance = CreateInstance(("rcon.enabled", true));

        Assert.True(ModuleRconAvailability.CanUseRcon(module, instance));
    }

    [Fact]
    public void CanUseRcon_is_false_when_rcon_is_disabled()
    {
        var module = new TestModule(
            supportsRcon: true,
            fields: [new ConfigFieldDefinition("rcon.enabled", "Enable RCON", ConfigFieldType.Boolean)]);
        var instance = CreateInstance(("rcon.enabled", false));

        Assert.False(ModuleRconAvailability.CanUseRcon(module, instance));
    }

    [Fact]
    public void IsRconEnabled_defaults_true_when_module_has_no_enabled_field()
    {
        var module = new TestModule(supportsRcon: true, fields: []);
        var instance = CreateInstance();

        Assert.True(ModuleRconAvailability.IsRconEnabled(module, instance.Settings));
    }

    private static ServerInstance CreateInstance(params (string Key, object? Value)[] settings)
    {
        return new ServerInstance(
            "server-1",
            "Server",
            "test",
            "",
            "",
            "",
            settings.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class TestModule(bool supportsRcon, IReadOnlyList<ConfigFieldDefinition> fields) : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0.0";
        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: supportsRcon,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", []);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => fields;
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, IsInstalled: false, IsEnabled: false, StatusText: "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
