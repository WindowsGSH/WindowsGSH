using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class WindowsFirewallServiceTests
{
    [Fact]
    public void CheckAvailability_reports_available_store_without_mutating_rules()
    {
        var store = new FakeFirewallStore();
        var service = new WindowsFirewallService(() => store);

        var result = service.CheckAvailability();

        Assert.True(result.IsAvailable);
        Assert.Empty(store.Added);
    }

    [Fact]
    public void CheckAvailability_reports_store_creation_failure()
    {
        var service = new WindowsFirewallService(() => throw new InvalidOperationException("COM unavailable."));

        var result = service.CheckAvailability();

        Assert.False(result.IsAvailable);
        Assert.Contains("COM unavailable", result.Message);
    }

    [Fact]
    public void BuildRuleName_includes_server_identity_protocol_and_port()
    {
        var name = WindowsFirewallService.BuildRuleName("12", "Bad:/Name", 27015, FirewallProtocol.Udp);

        Assert.Equal("WindowsGSH 12 Bad--Name Udp 27015", name);
    }

    [Fact]
    public void GetRequiredRules_extracts_visible_unique_port_fields()
    {
        var service = new WindowsFirewallService(() => new FakeFirewallStore());
        var module = new TestModule([
            Port("game", "Game Port"),
            Port("query", "Query Port"),
            Port("duplicate", "Duplicate Port"),
            new ConfigFieldDefinition("hidden", "Hidden Port", ConfigFieldType.Port, VisibleWhen: new FieldVisibilityCondition("enabled", IsTruthy: true))
        ]);
        var instance = CreateInstance(new Dictionary<string, object?>
        {
            ["game"] = 27015,
            ["query"] = "27016",
            ["duplicate"] = 27015,
            ["hidden"] = 27017,
            ["enabled"] = false
        });

        var rules = service.GetRequiredRules(instance, module);

        Assert.Equal(4, rules.Count);
        Assert.Equal([27015, 27015, 27016, 27016], rules.Select(rule => rule.Port));
        Assert.Equal([FirewallProtocol.Tcp, FirewallProtocol.Udp, FirewallProtocol.Tcp, FirewallProtocol.Udp], rules.Select(rule => rule.Protocol));
    }

    [Fact]
    public void CreateOrUpdateRules_skips_existing_rules_and_creates_missing()
    {
        var store = new FakeFirewallStore();
        var service = new WindowsFirewallService(() => store);
        var module = new TestModule([Port("game", "Game Port")]);
        var instance = CreateInstance(new Dictionary<string, object?> { ["game"] = 27015 });
        store.Existing.Add(WindowsFirewallService.BuildRuleName("1", "Paper", 27015, FirewallProtocol.Tcp));

        var result = service.CreateOrUpdateRules(new FirewallRuleApplyRequest(instance, module));

        Assert.True(result.Success);
        Assert.Single(result.CreatedRules);
        Assert.Equal(FirewallProtocol.Udp, result.CreatedRules[0].Protocol);
        Assert.Contains(result.Steps, step => step.WasSkipped && step.Rule?.Protocol == FirewallProtocol.Tcp);
        Assert.Contains(store.Added, rule => rule.Rule.Protocol == FirewallProtocol.Udp);
    }

    [Fact]
    public void RemoveRules_removes_existing_managed_rules()
    {
        var store = new FakeFirewallStore();
        var service = new WindowsFirewallService(() => store);
        var module = new TestModule([Port("game", "Game Port")]);
        var server = CreateServer();
        store.Existing.Add(WindowsFirewallService.BuildRuleName("1", "Paper", 27015, FirewallProtocol.Tcp));
        store.Existing.Add(WindowsFirewallService.BuildRuleName("1", "Paper", 27015, FirewallProtocol.Udp));

        var result = service.RemoveRules(new FirewallRuleRemoveRequest(server, module));

        Assert.True(result.Success);
        Assert.Equal(2, result.RemovedRules.Count);
        Assert.Empty(store.Existing);
    }

    [Fact]
    public void CreateOrUpdateRules_maps_permission_failures()
    {
        var service = new WindowsFirewallService(() => new FakeFirewallStore
        {
            AddFailure = new UnauthorizedAccessException("denied")
        });
        var module = new TestModule([Port("game", "Game Port")]);
        var instance = CreateInstance(new Dictionary<string, object?> { ["game"] = 27015 });

        var result = service.CreateOrUpdateRules(new FirewallRuleApplyRequest(instance, module));

        Assert.False(result.Success);
        Assert.Contains(result.Steps, step =>
            step.Error?.Contains("Administrator rights", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static ConfigFieldDefinition Port(string key, string label)
    {
        return new ConfigFieldDefinition(key, label, ConfigFieldType.Port);
    }

    private static ServerInstance CreateInstance(IReadOnlyDictionary<string, object?> settings)
    {
        return new ServerInstance(
            "1",
            "Paper",
            "test",
            @"C:\servers\1",
            @"C:\servers\1\files",
            @"C:\servers\1\ServerConfig.json",
            settings);
    }

    private static InstalledServer CreateServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "ServerConfig.json");
        File.WriteAllText(configPath, """{ "settings": { "game": 27015 } }""");
        return new InstalledServer(
            Id: "1",
            Name: "Paper",
            ModuleId: "test",
            Runtime: "test",
            ServerFolder: @"C:\servers\1",
            InstallPath: @"C:\servers\1\files",
            ConfigPath: configPath,
            IpAddress: "127.0.0.1",
            Port: "27015",
            SteamAppId: string.Empty,
            SteamBranch: string.Empty,
            MaxPlayers: "8",
            ProcessId: "--",
            CpuUsage: "--",
            MemoryUsage: "--",
            PlayerCount: "--",
            CurrentStatusText: "Offline",
            Uptime: "--",
            IsOperationRunning: false,
            OperationText: string.Empty,
            LastOperationError: null,
            IsInstalled: true,
            Status: ServerRuntimeStatus.Offline,
            StatusText: "Offline",
            StatusBrushKey: "StatusBrush",
            HasUpdateAvailable: false,
            LocalBuildId: string.Empty,
            RemoteBuildId: string.Empty,
            IgnoredBuildId: string.Empty,
            CanShowInfo: true,
            CanEditConfig: true,
            CanStart: true,
            CanStop: false);
    }

    private sealed class FakeFirewallStore : IWindowsFirewallRuleStore
    {
        public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);
        public List<FirewallRuleDefinition> Added { get; } = [];
        public Exception? AddFailure { get; init; }

        public bool RuleExists(string name) => Existing.Contains(name);

        public void AddRule(FirewallRuleDefinition rule)
        {
            if (AddFailure != null)
            {
                throw AddFailure;
            }

            Existing.Add(rule.Rule.Name);
            Added.Add(rule);
        }

        public void RemoveRule(string name)
        {
            Existing.Remove(name);
        }
    }

    private sealed class TestModule(IReadOnlyList<ConfigFieldDefinition> fields) : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => fields;
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
    }
}
