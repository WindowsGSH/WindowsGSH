using System.Diagnostics;
using System.Text.Json;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ExistingServerImportServiceTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Preview_requires_import_capability()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new NoImportModule(), CreateExistingServer().SourcePath));

        Assert.Contains("does not support", ex.Message);
    }

    [Fact]
    public async Task Preview_rejects_invalid_folder()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new TestModule(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public async Task Preview_uses_module_probe_settings()
    {
        var source = CreateExistingServer();
        var preview = await CreateService().PreviewAsync(new TestModule(), source.SourcePath);

        Assert.Equal("test", preview.ModuleId);
        Assert.Equal(source.SourcePath, preview.SourcePath);
        Assert.Equal(source.InstallPath, preview.InstallPath);
        Assert.Equal("Imported Existing Server", preview.SourceName);
        Assert.Equal("Imported Existing Server", GetPreviewRow(preview, "server.name").Value);
        Assert.Equal(ExistingServerImportFieldStatus.Imported, GetPreviewRow(preview, "server.name").Status);
    }

    [Fact]
    public async Task Preview_resolves_relative_probe_install_path_against_source_folder()
    {
        var source = CreateExistingServer();
        var preview = await CreateService().PreviewAsync(new TestModule { UseRelativeInstallPath = true }, source.SourcePath);

        Assert.Equal(source.InstallPath, preview.InstallPath);
    }

    [Fact]
    public async Task Copy_import_copies_files_and_writes_config_and_metadata()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var result = service.Import(module, preview, CreateFieldValues(), ExistingServerImportMode.Copy);

        Assert.Equal("1", result.ServerId);
        Assert.True(File.Exists(Path.Combine(result.InstallPath, TestModule.ExecutablePath)));
        Assert.NotEqual(source.InstallPath, result.InstallPath);
        using var config = JsonDocument.Parse(File.ReadAllText(result.ConfigPath));
        Assert.Equal("files", config.RootElement.GetProperty("installPath").GetString());
        Assert.Equal("test", config.RootElement.GetProperty("moduleId").GetString());

        using var import = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ServerFolder, "import.json")));
        Assert.Equal("existing-server", import.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("Existing Server", import.RootElement.GetProperty("sourcePathHint").GetString());
        Assert.True(import.RootElement.GetProperty("sourceLeftUnchanged").GetBoolean());
        Assert.False(import.RootElement.GetProperty("sourcePathHint").GetString()!.Contains(Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Copy_import_rejects_target_inside_source()
    {
        var source = CreateExistingServer();
        var targetServersPath = Path.Combine(source.InstallPath, "servers");
        var service = new ExistingServerImportService(targetServersPath);
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Import(module, preview, CreateFieldValues(), ExistingServerImportMode.Copy));

        Assert.Contains("Copy target must not be inside the source folder", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
    }

    [Fact]
    public void Copy_import_rejects_source_inside_target()
    {
        var targetServersPath = CreateImportTargetRoot();
        var sourceInstallPath = Path.Combine(targetServersPath, "1", "files", "nested-source");
        var preview = new ExistingServerImportPreview(
            ModuleId: "test",
            ModuleName: "Test Module",
            SourceName: "Nested Source",
            SourcePath: sourceInstallPath,
            InstallPath: sourceInstallPath,
            Rows: [],
            Warnings: []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ExistingServerImportService(targetServersPath).Import(
                new TestModule(),
                preview,
                CreateFieldValues(),
                ExistingServerImportMode.Copy));

        Assert.Contains("source must not be inside the copy target", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
    }

    [Fact]
    public async Task Copy_import_reports_preparing_and_complete_progress()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);
        var updates = new List<ExistingServerImportProgress>();

        service.Import(
            module,
            preview,
            CreateFieldValues(),
            ExistingServerImportMode.Copy,
            new InlineProgress<ExistingServerImportProgress>(updates.Add));

        Assert.NotEmpty(updates);
        Assert.Equal("Preparing copy", updates.First().Phase);
        Assert.Equal("Copy complete", updates.Last().Phase);
    }

    [Fact]
    public async Task Adopt_import_points_at_source_install_path()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var result = service.Import(module, preview, CreateFieldValues(), ExistingServerImportMode.Adopt);

        Assert.Equal(source.InstallPath, result.InstallPath);
        using var config = JsonDocument.Parse(File.ReadAllText(result.ConfigPath));
        Assert.Equal(source.InstallPath, config.RootElement.GetProperty("installPath").GetString());
        Assert.True(File.Exists(Path.Combine(source.InstallPath, TestModule.ExecutablePath)));
        using var import = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ServerFolder, "import.json")));
        Assert.False(import.RootElement.GetProperty("sourceLeftUnchanged").GetBoolean());
    }

    [Fact]
    public async Task Required_missing_field_blocks_import()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Import(
                module,
                preview,
                [new ExistingServerImportFieldValue("network.port", "27015")],
                ExistingServerImportMode.Copy));

        Assert.Equal("Server Name is required.", ex.Message);
    }

    [Fact]
    public async Task Import_rejects_preview_created_for_different_module()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var preview = await service.PreviewAsync(new TestModule(), source.SourcePath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Import(new AlternateTestModule(), preview, CreateFieldValues(), ExistingServerImportMode.Copy));

        Assert.Contains("not 'other-test'", ex.Message);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public async Task Boolean_import_values_accept_common_tokens(string value, bool expected)
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new BooleanNumberTestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var result = service.Import(
            module,
            preview,
            [
                new ExistingServerImportFieldValue("server.name", "Imported Existing Server"),
                new ExistingServerImportFieldValue("network.port", "27015"),
                new ExistingServerImportFieldValue("server.enabled", value),
                new ExistingServerImportFieldValue("server.rate", "1.5")
            ],
            ExistingServerImportMode.Copy);

        Assert.Equal(expected, result.Settings["server.enabled"]);
    }

    [Fact]
    public async Task Invalid_boolean_import_value_is_rejected()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new BooleanNumberTestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Import(
                module,
                preview,
                [
                    new ExistingServerImportFieldValue("server.name", "Imported Existing Server"),
                    new ExistingServerImportFieldValue("network.port", "27015"),
                    new ExistingServerImportFieldValue("server.enabled", "nope")
                ],
                ExistingServerImportMode.Copy));

        Assert.Equal("Enabled must be true or false.", ex.Message);
    }

    [Fact]
    public async Task Decimal_port_import_value_is_rejected()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.Import(
                module,
                preview,
                [
                    new ExistingServerImportFieldValue("server.name", "Imported Existing Server"),
                    new ExistingServerImportFieldValue("network.port", "27015.5")
                ],
                ExistingServerImportMode.Copy));

        Assert.Equal("Server Port must be a number.", ex.Message);
    }

    [Fact]
    public void Decimal_port_row_is_marked_for_review()
    {
        var row = new WindowsGSH.ExistingImportRowViewModel(new ExistingServerImportRow(
            "network.port",
            "Server Port",
            "Port",
            Required: true,
            "27015.5",
            "Existing server",
            ExistingServerImportFieldStatus.Imported));

        Assert.Equal(ExistingServerImportFieldStatus.Review, row.Status);
    }

    [Fact]
    public async Task Number_import_values_are_converted_as_double()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new BooleanNumberTestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var result = service.Import(
            module,
            preview,
            [
                new ExistingServerImportFieldValue("server.name", "Imported Existing Server"),
                new ExistingServerImportFieldValue("network.port", "27015"),
                new ExistingServerImportFieldValue("server.enabled", "true"),
                new ExistingServerImportFieldValue("server.rate", "1.5")
            ],
            ExistingServerImportMode.Copy);

        Assert.Equal(1.5d, result.Settings["server.rate"]);
    }

    [Fact]
    public void Boolean_row_marks_unknown_text_for_review()
    {
        var row = new WindowsGSH.ExistingImportRowViewModel(new ExistingServerImportRow(
            "server.enabled",
            "Enabled",
            "Boolean",
            Required: false,
            "maybe",
            "Existing server",
            ExistingServerImportFieldStatus.Imported));

        Assert.Equal(ExistingServerImportFieldStatus.Review, row.Status);
    }

    [Fact]
    public async Task Copy_failure_cleans_up_target_server_folder()
    {
        var source = CreateExistingServer();
        var targetServersPath = CreateImportTargetRoot();
        var service = new ExistingServerImportService((_, target, _) =>
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "partial.txt"), "partial");
            throw new IOException("copy failed");
        }, targetServersPath);
        var module = new TestModule();
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var ex = Assert.Throws<IOException>(() =>
            service.Import(module, preview, CreateFieldValues(), ExistingServerImportMode.Copy));

        Assert.Equal("copy failed", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
        Assert.True(File.Exists(Path.Combine(source.InstallPath, TestModule.ExecutablePath)));
    }

    [Fact]
    public async Task Import_metadata_redacts_warning_secret_values()
    {
        var source = CreateExistingServer();
        var service = CreateService();
        var module = new TestModule
        {
            Warnings =
            [
                "rconpassword=super-secret",
                "token: secret-token"
            ]
        };
        var preview = await service.PreviewAsync(module, source.SourcePath);

        var result = service.Import(module, preview, CreateFieldValues(), ExistingServerImportMode.Copy);

        var importJson = File.ReadAllText(Path.Combine(result.ServerFolder, "import.json"));
        Assert.DoesNotContain("super-secret", importJson);
        Assert.DoesNotContain("secret-token", importJson);
        Assert.Contains("[redacted]", importJson);
    }

    private static IReadOnlyList<ExistingServerImportFieldValue> CreateFieldValues()
    {
        return
        [
            new ExistingServerImportFieldValue("server.name", "Imported Existing Server"),
            new ExistingServerImportFieldValue("network.port", "27015")
        ];
    }

    private static ExistingServerImportRow GetPreviewRow(ExistingServerImportPreview preview, string key)
    {
        return preview.Rows.Single(row => row.Key == key);
    }

    private ExistingServerImportService CreateService()
    {
        return new ExistingServerImportService(CreateImportTargetRoot());
    }

    private string CreateImportTargetRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var serversPath = Path.Combine(root, "servers");
        _tempRoots.Add(root);
        return serversPath;
    }

    private TestExistingServer CreateExistingServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        var sourcePath = Path.Combine(root, "Existing Server");
        var installPath = Path.Combine(sourcePath, "serverfiles");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, TestModule.ExecutablePath), "fake executable");
        File.WriteAllText(Path.Combine(installPath, "server.cfg"), "hostname Imported Existing Server");
        return new TestExistingServer(sourcePath, installPath);
    }

    private sealed record TestExistingServer(string SourcePath, string InstallPath);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    private class TestModule : IGameServerModule, IModuleExistingServerImportCapability
    {
        public const string ExecutablePath = "server.exe";

        public IReadOnlyList<string> Warnings { get; init; } = [];

        public bool UseRelativeInstallPath { get; init; }

        public virtual string Id => "test";

        public string Name => "Test Module";

        public string Version => "1.0";

        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, true);

        public ModuleRuntimeDefinition Runtime => new(ExecutablePath, ["server"]);

        public SteamInstallDefinition? SteamInstall => null;

        public bool CanImport(string path)
        {
            return Directory.Exists(Path.Combine(path, "serverfiles"));
        }

        public Task<ModuleExistingServerImportProbe> PreviewImportAsync(string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModuleExistingServerImportProbe(
                "Imported Existing Server",
                UseRelativeInstallPath ? "serverfiles" : Path.Combine(path, "serverfiles"),
                new Dictionary<string, object?>
                {
                    ["server.name"] = "Imported Existing Server",
                    ["network.port"] = 27015
                },
                Warnings));
        }

        public virtual IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
        {
            return
            [
                new ConfigFieldDefinition("server.name", "Server Name", ConfigFieldType.Text, Required: true),
                new ConfigFieldDefinition("network.port", "Server Port", ConfigFieldType.Port, Required: true)
            ];
        }

        public string GetServerName(IReadOnlyDictionary<string, object?> settings)
        {
            return settings.TryGetValue("server.name", out var value) ? value?.ToString() ?? "" : "";
        }

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "");

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProcessStartInfo(Path.Combine(instance.InstallPath, ExecutablePath)));
        }

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<Process?>(null);
        }

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool IsInstallValid(ServerInstance instance)
        {
            return File.Exists(Path.Combine(instance.InstallPath, ExecutablePath));
        }

        public string? GetConsoleLogPath(ServerInstance instance) => null;

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

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult(new QueryResult(ModuleServerStatus.Unknown));
        }

        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
        {
            return Task.FromResult(string.Empty);
        }

        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];

        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];

        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");

        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }
    }

    private sealed class BooleanNumberTestModule : TestModule
    {
        public override IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
        {
            return
            [
                new ConfigFieldDefinition("server.name", "Server Name", ConfigFieldType.Text, Required: true),
                new ConfigFieldDefinition("network.port", "Server Port", ConfigFieldType.Port, Required: true),
                new ConfigFieldDefinition("server.enabled", "Enabled", ConfigFieldType.Boolean),
                new ConfigFieldDefinition("server.rate", "Rate", ConfigFieldType.Number)
            ];
        }
    }

    private sealed class AlternateTestModule : TestModule
    {
        public override string Id => "other-test";
    }

    private sealed class NoImportModule : IGameServerModule
    {
        public string Id => "no-import";

        public string Name => "No Import Module";

        public string Version => "1.0";

        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);

        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);

        public SteamInstallDefinition? SteamInstall => null;

        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];

        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "", "");

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo());

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);

        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool IsInstallValid(ServerInstance instance) => true;

        public string? GetConsoleLogPath(ServerInstance instance) => null;

        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }

        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new QueryResult(ModuleServerStatus.Unknown));

        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];

        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];

        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");

        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }
    }
}
