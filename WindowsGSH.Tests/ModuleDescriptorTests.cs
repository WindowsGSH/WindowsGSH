using System.Diagnostics;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleDescriptorTests
{
    [Fact]
    public void CreateAcceptsMinimalGameServerModule()
    {
        var module = new MinimalModule();
        var provenance = new ModuleProvenanceSnapshot(null, "ABC", HasChangedSinceImport: false, Warning: null);

        var descriptor = ModuleDescriptor.Create(module, @"C:\modules\minimal", "C#", provenance);

        Assert.Equal("minimal", descriptor.Id);
        Assert.False(descriptor.OptionalInterfaces.HasUpdate);
        Assert.False(descriptor.OptionalInterfaces.HasApiAction);
        Assert.False(descriptor.EffectiveCapabilities.SupportsBackups);
    }

    [Fact]
    public void CreateDetectsOptionalInterfacesAndEffectiveCapabilities()
    {
        var module = new DescriptorTestModule();
        var provenance = new ModuleProvenanceSnapshot(null, "ABC", HasChangedSinceImport: false, Warning: null);

        var descriptor = ModuleDescriptor.Create(module, @"C:\modules\test", "C#", provenance);

        Assert.Equal(module, descriptor.Module);
        Assert.Equal("descriptor-test", descriptor.Id);
        Assert.Equal("Descriptor Test", descriptor.Name);
        Assert.Equal("C#", descriptor.ModuleType);
        Assert.True(descriptor.OptionalInterfaces.HasUpdate);
        Assert.True(descriptor.OptionalInterfaces.HasApiAction);
        Assert.True(descriptor.EffectiveCapabilities.SupportsUpdate);
        Assert.True(descriptor.EffectiveCapabilities.SupportsApiActions);
        Assert.True(descriptor.EffectiveCapabilities.SupportsBackups);
        Assert.Equal(provenance, descriptor.Provenance);
    }

    [Fact]
    public void LegacyPublicCapabilityMethodsStillContributeEffectiveCapabilities()
    {
        var module = new DescriptorTestModule();
        var provenance = new ModuleProvenanceSnapshot(null, "ABC", HasChangedSinceImport: false, Warning: null);

        var descriptor = ModuleDescriptor.Create(module, @"C:\modules\test", "C#", provenance);

        Assert.True(descriptor.EffectiveCapabilities.SupportsBackups);
        Assert.Single(module.GetBackupTargets());
    }

    private sealed class MinimalModule : IGameServerModule
    {
        public string Id => "minimal";

        public string Name => "Minimal";

        public string Version => "1.0.0";

        public ModuleCapabilities Capabilities { get; } = new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);

        public ModuleRuntimeDefinition Runtime { get; } = new("server.exe", ["server"], AllowsEmbeddedConsole: false, PortIncrements: 1);

        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "16");

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProcessStartInfo());
        }

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<Process?>(null);
        }

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool IsInstallValid(ServerInstance instance) => true;

        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }

    private sealed class DescriptorTestModule : IGameServerModule, IModuleUpdateCapability, IModuleApiActionCapability
    {
        public string Id => "descriptor-test";

        public string Name => "Descriptor Test";

        public string Version => "1.0.0";

        public ModuleCapabilities Capabilities { get; } = new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);

        public SteamInstallDefinition? SteamInstall => null;

        public ModuleRuntimeDefinition Runtime { get; } = new("server.exe", ["server"], AllowsEmbeddedConsole: false, PortIncrements: 1);

        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];

        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];

        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() =>
        [
            new("saves", "Saves", "saves", IsDirectory: true, IsRequired: false)
        ];

        public ModuleApiConnectionDefinition? GetApiConnection() => null;

        public IReadOnlyList<ModuleApiActionDefinition> GetApiActions() =>
        [
            new("status", "Status", "GET", "/status", Destructive: false, null, null, null, [])
        ];

        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "16");

        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }

        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProcessStartInfo());
        }

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<Process?>(null);
        }

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool IsInstallValid(ServerInstance instance) => true;

        public string? GetConsoleLogPath(ServerInstance instance) => null;

        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }

        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new QueryResult(ModuleServerStatus.Unknown));
        }

        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId)
        {
            return new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Not installed");
        }

        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ModuleUpdateResult> UpdateAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModuleUpdateResult(true, "Updated."));
        }
    }
}
