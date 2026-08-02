using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class ModuleRegistryTests : IDisposable
{
    private readonly string _moduleId = "registry-tamper-test-" + Guid.NewGuid().ToString("N");
    private readonly string _modulePath;
    private readonly List<string> _moduleLog = [];
    private readonly Action<string, string?>? _previousSink;

    public ModuleRegistryTests()
    {
        ModuleStoragePaths.EnsureCreated();
        _modulePath = Path.Combine(ModuleStoragePaths.InstalledModules, _moduleId);
        _previousSink = ModuleLog.Sink;
        ModuleLog.Sink = (message, source) => _moduleLog.Add(message);
        ModuleRegistry.InvalidateCache();
    }

    public void Dispose()
    {
        ModuleLog.Sink = _previousSink;
        ModuleRegistry.InvalidateCache();

        try
        {
            if (Directory.Exists(_modulePath))
            {
                Directory.Delete(_modulePath, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void GetModuleDescriptors_includes_builtin_generic_wrapper()
    {
        var descriptors = new ModuleRegistry().GetModuleDescriptors();

        var wrapper = Assert.Single(descriptors, descriptor => descriptor.Id == GenericWrapperModule.ModuleId);
        Assert.Equal("Built-in module", wrapper.ModuleType);
        Assert.IsType<GenericWrapperModule>(wrapper.Module);
    }

    [Fact]
    public void GetModuleDescriptors_blocks_imported_csharp_module_after_hash_change()
    {
        CreateCSharpModule();
        new ModuleProvenanceService().WriteImportMetadata(
            _modulePath,
            "test",
            "module",
            ModuleManifestProvenance.Empty);
        ModuleRegistry.InvalidateCache();

        var beforeChange = new ModuleRegistry().GetModuleDescriptors();

        Assert.Contains(beforeChange, descriptor => descriptor.Id == _moduleId);

        File.AppendAllText(Path.Combine(_modulePath, "TamperTestModule.cs"), Environment.NewLine + "// changed after import");
        ModuleRegistry.InvalidateCache();

        var afterChange = new ModuleRegistry().GetModuleDescriptors();

        Assert.DoesNotContain(afterChange, descriptor => descriptor.Id == _moduleId);
        Assert.Contains(
            _moduleLog,
            message => message.Contains(_moduleId, StringComparison.OrdinalIgnoreCase) &&
                message.Contains("changed since it was imported", StringComparison.OrdinalIgnoreCase));
    }

    private void CreateCSharpModule()
    {
        Directory.CreateDirectory(_modulePath);
        File.WriteAllText(Path.Combine(_modulePath, "module.json"), $$"""
        {
          "id": "{{_moduleId}}",
          "name": "Registry Tamper Test",
          "version": "1.0.0",
          "entry": "TamperTestModule",
          "entryPoints": {
            "start": "server.exe",
            "processName": "server"
          }
        }
        """);
        File.WriteAllText(Path.Combine(_modulePath, "TamperTestModule.cs"), $$"""
        using System.Diagnostics;
        using WindowsGSH.Core.Modules;

        public sealed class TamperTestModule : IGameServerModule
        {
            public string Id => "{{_moduleId}}";
            public string Name => "Registry Tamper Test";
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
        }
        """);
    }
}
