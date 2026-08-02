using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleDevelopmentDiagnosticsServiceTests
{
    [Fact]
    public void ValidateFolderReportsMissingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var report = new ModuleDevelopmentDiagnosticsService().ValidateFolder(path);

        Assert.False(report.Success);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "folder.missing");
    }

    [Fact]
    public void ValidateFolderReportsMissingManifest()
    {
        using var temp = new TemporaryDirectory();
        var report = new ModuleDevelopmentDiagnosticsService().ValidateFolder(temp.Path);

        Assert.False(report.Success);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "manifest.missing");
    }

    [Fact]
    public void ValidateFolderReportsJsonModulePreview()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "module.json"), """
{
  "id": "test-module",
  "name": "Test Module",
  "version": "1.2.3",
  "entryPoints": {
    "start": "server.exe",
    "processName": "server"
  },
  "runtime": {
    "queryProtocol": "A2S"
  },
  "steam": {
    "appId": "123"
  },
  "backupTargets": [
    {
      "key": "saves",
      "label": "Saves",
      "path": "saves",
      "type": "directory"
    }
  ]
}
""");

        var report = new ModuleDevelopmentDiagnosticsService().ValidateFolder(temp.Path);

        Assert.True(report.Success);
        Assert.Equal("Test Module", report.Name);
        Assert.Equal("test-module", report.Id);
        Assert.Equal("1.2.3", report.Version);
        Assert.Equal("JSON module", report.ModuleType);
        Assert.True(report.Capabilities?.SupportsInstall);
        Assert.True(report.Capabilities?.SupportsQuery);
        Assert.True(report.Capabilities?.SupportsBackups);
    }

    [Fact]
    public void CSharpLoaderPreservesExplicitLegacyCapabilityImplementations()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "module.json"), """
{
  "id": "legacy-explicit",
  "name": "Legacy Explicit",
  "version": "1.0.0",
  "entry": "LegacyExplicitModule",
  "entryPoints": {
    "start": "server.exe",
    "processName": "server"
  }
}
""");
        File.WriteAllText(Path.Combine(temp.Path, "LegacyExplicitModule.cs"), """
using System.Diagnostics;
using WindowsGSH.Core.Modules;

public sealed class LegacyExplicitModule : IGameServerModule
{
    public string Id => "legacy-explicit";
    public string Name => "Legacy Explicit";
    public string Version => "1.0.0";
    public ModuleCapabilities Capabilities => new(
        SupportsInstall: false,
        SupportsUpdate: false,
        SupportsQuery: false,
        SupportsRcon: false,
        SupportsConsoleCommands: false,
        SupportsApiActions: false,
        SupportsBackups: false,
        SupportsDirectConnection: false);
    public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"], false, 1);

    public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;
    public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "16");
    public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo());
    public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
    public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
    public bool IsInstallValid(ServerInstance instance) => true;
    public string? GetConsoleLogPath(ServerInstance instance) => null;

    IReadOnlyList<ConfigFieldDefinition> IModuleConfigCapability.GetConfigFields() =>
    [
        new("server.name", "Server Name", ConfigFieldType.Text, "Legacy", true)
    ];

    Task<IReadOnlyDictionary<string, object?>> IModuleConfigCapability.ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());

    Task IModuleConfigCapability.WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;

    IReadOnlyList<ServerBackupTargetDefinition> IModuleBackupCapability.GetBackupTargets() =>
    [
        new("saves", "Saves", "saves", true, false)
    ];
}
""");

        var module = new CSharpModuleLoader().Load(temp.Path);

        Assert.Equal("legacy-explicit", module.Id);
        Assert.Single(module.GetConfigFields());
        Assert.Single(module.GetBackupTargets());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
