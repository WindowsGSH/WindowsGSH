using System.Diagnostics;
using System.Text.Json;
using WindowsGSH.Core;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(SteamBranchPasswordStoreTestCollection.Name)]
public sealed class WindowsGsmServerImportServiceTests : IDisposable
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
    public void Copy_failure_cleans_up_target_server_folder()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var targetServersPath = CreateImportTargetRoot();
        var service = new WindowsGsmServerImportService((_, target) =>
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "partial.txt"), "partial");
            throw new IOException("copy exploded");
        }, targetServersPath);

        var exception = Assert.Throws<IOException>(() => service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("copy exploded", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
        Assert.True(Directory.Exists(source.ServerFilesPath));
        Assert.True(File.Exists(Path.Combine(source.ServerFilesPath, TestModule.ExecutablePath)));
    }

    [Fact]
    public void Copy_import_rejects_target_inside_source()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var targetServersPath = Path.Combine(source.ServerFilesPath, "servers");
        var service = new WindowsGsmServerImportService(targetServersPath);

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy));

        Assert.Contains("Copy target must not be inside the source folder", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
    }

    [Fact]
    public void Copy_import_rejects_source_inside_target()
    {
        var targetServersPath = CreateImportTargetRoot();
        var sourceServerFilesPath = Path.Combine(targetServersPath, "1", "files", "nested-source");
        var preview = new WindowsGsmServerImportPreview(
            ModuleId: "test",
            ModuleName: "Test Module",
            SourceGame: "Test Module",
            SourceServerFolder: sourceServerFilesPath,
            SourceServerFilesPath: sourceServerFilesPath,
            SourceConfigPath: Path.Combine(sourceServerFilesPath, "configs", "WindowsGSM.cfg"),
            SteamBranch: "",
            SteamBranchPassword: "",
            Rows: [],
            Warnings: []);

        var exception = Assert.Throws<InvalidOperationException>(() => new WindowsGsmServerImportService(targetServersPath).Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy));

        Assert.Contains("source must not be inside the copy target", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
    }

    [Fact]
    public void Adopt_failure_cleans_target_folder_without_deleting_source_serverfiles()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var targetServersPath = CreateImportTargetRoot();
        var service = new WindowsGsmServerImportService(targetServersPath);

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new ThrowingNameModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Adopt));

        Assert.Equal("name exploded", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(targetServersPath, "1")));
        Assert.True(Directory.Exists(source.ServerFilesPath));
        Assert.True(File.Exists(Path.Combine(source.ServerFilesPath, TestModule.ExecutablePath)));
    }

    [Fact]
    public void Successful_copy_writes_server_config_and_import_metadata()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var result = service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy);

        Assert.Equal("1", result.ServerId);
        Assert.Equal("Copied Server", result.ServerName);
        Assert.True(File.Exists(result.ConfigPath));
        Assert.True(File.Exists(Path.Combine(result.ServerFolder, "import.json")));
        Assert.True(File.Exists(Path.Combine(result.InstallPath, TestModule.ExecutablePath)));

        using var config = JsonDocument.Parse(File.ReadAllText(result.ConfigPath));
        Assert.Equal("files", config.RootElement.GetProperty("installPath").GetString());
        Assert.Equal("test", config.RootElement.GetProperty("moduleId").GetString());

        using var import = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ServerFolder, "import.json")));
        Assert.Equal("windowsgsm-server", import.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("Copy", import.RootElement.GetProperty("mode").GetString());
        AssertImportMetadata(import, preview, result, WindowsGsmServerImportMode.Copy);
    }

    [Fact]
    public void Successful_adopt_writes_server_config_and_import_metadata()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var result = service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Adopt);

        Assert.Equal("1", result.ServerId);
        Assert.Equal("Copied Server", result.ServerName);
        Assert.Equal(source.ServerFilesPath, result.InstallPath);
        Assert.True(File.Exists(result.ConfigPath));
        Assert.True(File.Exists(Path.Combine(result.ServerFolder, "import.json")));
        Assert.True(File.Exists(Path.Combine(source.ServerFilesPath, TestModule.ExecutablePath)));

        using var config = JsonDocument.Parse(File.ReadAllText(result.ConfigPath));
        Assert.Equal(source.ServerFilesPath, config.RootElement.GetProperty("installPath").GetString());
        Assert.Equal("test", config.RootElement.GetProperty("moduleId").GetString());

        using var import = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ServerFolder, "import.json")));
        Assert.Equal("windowsgsm-server", import.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("Adopt", import.RootElement.GetProperty("mode").GetString());
        AssertImportMetadata(import, preview, result, WindowsGsmServerImportMode.Adopt);
    }

    [Fact]
    public void Import_metadata_does_not_store_known_secret_values()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(
            source,
            steamBranchPassword: "steam-secret",
            warnings:
            [
                "rconpassword=super-secret-rcon",
                "servergslt:super-secret-gslt",
                "token = super-secret-token",
                "{\"token\":\"json-secret-token\"}",
                "'password':'json-secret-password'",
                "{\"servergslt\":\"json-secret-gslt\"}"
            ]);
        var service = CreateService();

        var result = service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy);

        var importJson = File.ReadAllText(Path.Combine(result.ServerFolder, "import.json"));
        Assert.DoesNotContain("steam-secret", importJson);
        Assert.DoesNotContain("super-secret-rcon", importJson);
        Assert.DoesNotContain("super-secret-gslt", importJson);
        Assert.DoesNotContain("super-secret-token", importJson);
        Assert.DoesNotContain("json-secret-token", importJson);
        Assert.DoesNotContain("json-secret-password", importJson);
        Assert.DoesNotContain("json-secret-gslt", importJson);
        Assert.Contains("[redacted]", importJson);
    }

    [Fact]
    public void Steam_branch_password_is_stored_outside_server_config()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(
            source,
            steamBranch: "beta",
            steamBranchPassword: "steam-branch-secret");
        var service = CreateService();

        var result = service.Import(
            new SteamTestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy);

        try
        {
            var configJson = File.ReadAllText(result.ConfigPath);
            Assert.DoesNotContain("steam-branch-secret", configJson);

            using var config = JsonDocument.Parse(configJson);
            var steam = config.RootElement.GetProperty("steam");
            Assert.Equal("beta", steam.GetProperty("branch").GetString());
            var reference = steam.GetProperty("branchPasswordRef").GetString();
            Assert.Equal(SteamBranchPasswordStore.CreateReference(result.ServerId), reference);
            Assert.Equal("steam-branch-secret", new SteamBranchPasswordStore().Load(result.ServerId));
        }
        finally
        {
            new SteamBranchPasswordStore().Clear(result.ServerId);
        }
    }

    [Fact]
    public void Steam_branch_password_is_not_stored_for_non_steam_module()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(
            source,
            steamBranch: "beta",
            steamBranchPassword: "orphan-secret");
        var service = CreateService();

        var result = service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy);

        try
        {
            var configJson = File.ReadAllText(result.ConfigPath);
            Assert.DoesNotContain("orphan-secret", configJson);

            using var config = JsonDocument.Parse(configJson);
            Assert.Equal(JsonValueKind.Null, config.RootElement.GetProperty("steam").ValueKind);
            Assert.Equal(string.Empty, new SteamBranchPasswordStore().Load(result.ServerId));
        }
        finally
        {
            new SteamBranchPasswordStore().Clear(result.ServerId);
        }
    }

    [Fact]
    public async Task Preview_requires_selected_folder()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new TestModule(), ""));

        Assert.Equal("Select a WindowsGSM server folder.", exception.Message);
    }

    [Fact]
    public async Task Preview_requires_windowsgsm_config()
    {
        var source = CreateWindowsGsmServer(createConfig: false);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new TestModule(), source.ServerFolder));

        Assert.Contains("WindowsGSM.cfg was not found", exception.Message);
    }

    [Fact]
    public async Task Preview_requires_serverfiles_folder()
    {
        var source = CreateWindowsGsmServer(createServerFiles: false);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new TestModule(), source.ServerFolder));

        Assert.Contains("WindowsGSM serverfiles folder was not found", exception.Message);
    }

    [Fact]
    public async Task Preview_rejects_invalid_install_for_selected_module()
    {
        var source = CreateWindowsGsmServer(createExecutable: false);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(new TestModule(), source.ServerFolder));

        Assert.Contains("does not look valid for Test Module", exception.Message);
    }

    [Fact]
    public async Task Preview_maps_windowsgsm_config_values_to_module_fields()
    {
        var source = CreateWindowsGsmServer("""
            servername=Mapped Server
            serverport=27015
            servergame=Test Module
            """);
        var service = CreateService();

        var preview = await service.PreviewAsync(new TestModule(), source.ServerFolder);

        Assert.Equal("test", preview.ModuleId);
        Assert.Equal("Test Module", preview.ModuleName);
        Assert.Equal("Test Module", preview.SourceGame);
        Assert.Equal("Mapped Server", GetPreviewRow(preview, "server.name").Value);
        Assert.Equal("27015", GetPreviewRow(preview, "network.port").Value);
        Assert.Equal(WindowsGsmImportFieldStatus.Imported, GetPreviewRow(preview, "server.name").Status);
        Assert.Equal(WindowsGsmImportFieldStatus.Imported, GetPreviewRow(preview, "network.port").Status);
    }

    [Fact]
    public async Task Preview_and_import_map_windowsgsm_backup_settings()
    {
        var source = CreateWindowsGsmServer("""
            servername=Mapped Server
            backuponstart=1
            """);
        var destination = Path.Combine(source.Root, "ImportedBackups");
        File.WriteAllText(
            Path.Combine(source.ServerFolder, "configs", "BackupConfig.cfg"),
            $"""
            backuplocation="{destination}"
            saveslocation="Saves|Config"
            fileslocation="server.properties"
            """);
        var service = CreateService();

        var preview = await service.PreviewAsync(new TestModule(), source.ServerFolder);
        var result = service.Import(
            new TestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy);

        Assert.True(preview.BackupOnStart);
        Assert.Equal(destination, preview.BackupDestination);
        Assert.Equal(["Saves", "Config", "server.properties"], preview.ImportedBackupPaths);
        using var config = JsonDocument.Parse(File.ReadAllText(result.ConfigPath));
        var backup = config.RootElement.GetProperty("backup");
        Assert.True(backup.GetProperty("backupOnStart").GetBoolean());
        Assert.Equal(destination, backup.GetProperty("destination").GetString());
        Assert.Equal(3, backup.GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public void Required_missing_field_blocks_import()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new TestModule(),
            preview,
            [new WindowsGsmServerImportFieldValue("network.port", "17777")],
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("Server Name is required.", exception.Message);
    }

    [Fact]
    public void Import_rejects_preview_created_for_different_module()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new AlternateTestModule(),
            preview,
            CreateFieldValues(),
            WindowsGsmServerImportMode.Copy));

        Assert.Contains("not 'other-test'", exception.Message);
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
    public void Boolean_import_values_accept_common_tokens(string value, bool expected)
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var result = service.Import(
            new BooleanNumberTestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "17777"),
                new WindowsGsmServerImportFieldValue("server.enabled", value),
                new WindowsGsmServerImportFieldValue("server.rate", "1.5")
            ],
            WindowsGsmServerImportMode.Copy);

        Assert.Equal(expected, result.Settings["server.enabled"]);
    }

    [Fact]
    public void Invalid_boolean_import_value_is_rejected()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new BooleanNumberTestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "17777"),
                new WindowsGsmServerImportFieldValue("server.enabled", "nope")
            ],
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("Enabled must be true or false.", exception.Message);
    }

    [Fact]
    public void Decimal_port_import_value_is_rejected()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new TestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "27015.5")
            ],
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("Server Port must be a number.", exception.Message);
    }

    [Fact]
    public void Number_import_values_are_converted_as_double()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var result = service.Import(
            new BooleanNumberTestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "17777"),
                new WindowsGsmServerImportFieldValue("server.enabled", "true"),
                new WindowsGsmServerImportFieldValue("server.rate", "1.5")
            ],
            WindowsGsmServerImportMode.Copy);

        Assert.Equal(1.5d, result.Settings["server.rate"]);
    }

    [Fact]
    public void Invalid_port_blocks_import()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new TestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "not-a-port")
            ],
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("Server Port must be a number.", exception.Message);
    }

    [Fact]
    public void Out_of_range_port_blocks_import()
    {
        var source = CreateWindowsGsmServer();
        var preview = CreatePreview(source);
        var service = CreateService();

        var exception = Assert.Throws<InvalidOperationException>(() => service.Import(
            new TestModule(),
            preview,
            [
                new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
                new WindowsGsmServerImportFieldValue("network.port", "70000")
            ],
            WindowsGsmServerImportMode.Copy));

        Assert.Equal("Server Port must be between 1 and 65535.", exception.Message);
    }

    [Fact]
    public async Task Preview_adds_warning_for_source_game_mismatch()
    {
        var source = CreateWindowsGsmServer("""
            servername=Mapped Server
            serverport=27015
            servergame=Different Game
            """);
        var service = CreateService();

        var preview = await service.PreviewAsync(new TestModule(), source.ServerFolder);

        Assert.Contains(preview.Warnings, warning => warning.Contains("Different Game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_adds_warning_when_module_config_read_fails()
    {
        var source = CreateWindowsGsmServer();
        var service = CreateService();

        var preview = await service.PreviewAsync(new ThrowingReadModule(), source.ServerFolder);

        Assert.Contains(preview.Warnings, warning => warning.Contains("Module config read warning: read exploded", StringComparison.Ordinal));
    }

    private static IReadOnlyList<WindowsGsmServerImportFieldValue> CreateFieldValues()
    {
        return
        [
            new WindowsGsmServerImportFieldValue("server.name", "Copied Server"),
            new WindowsGsmServerImportFieldValue("network.port", "17777")
        ];
    }

    private static WindowsGsmServerImportPreview CreatePreview(
        TestWindowsGsmServer source,
        string steamBranch = "",
        string steamBranchPassword = "",
        IReadOnlyList<string>? warnings = null)
    {
        return new WindowsGsmServerImportPreview(
            ModuleId: "test",
            ModuleName: "Test Module",
            SourceGame: "Test Module",
            SourceServerFolder: source.ServerFolder,
            SourceServerFilesPath: source.ServerFilesPath,
            SourceConfigPath: source.ConfigPath,
            SteamBranch: steamBranch,
            SteamBranchPassword: steamBranchPassword,
            Rows: [],
            Warnings: warnings ?? []);
    }

    private static void AssertImportMetadata(
        JsonDocument import,
        WindowsGsmServerImportPreview preview,
        WindowsGsmServerImportResult result,
        WindowsGsmServerImportMode mode)
    {
        var root = import.RootElement;
        Assert.Equal("windowsgsm-server", root.GetProperty("sourceType").GetString());
        Assert.Equal(mode.ToString(), root.GetProperty("mode").GetString());
        Assert.Equal("WindowsGSM", root.GetProperty("importedFrom").GetString());
        Assert.Equal(preview.ModuleId, root.GetProperty("moduleId").GetString());
        Assert.Equal(preview.ModuleName, root.GetProperty("moduleName").GetString());
        Assert.Equal(mode.ToString(), root.GetProperty("importMode").GetString());
        Assert.Equal(preview.SourceServerFolder, root.GetProperty("sourceServerFolder").GetString());
        Assert.Equal(preview.SourceServerFilesPath, root.GetProperty("sourceServerFilesPath").GetString());
        Assert.Equal(preview.SourceConfigPath, root.GetProperty("sourceConfigPath").GetString());
        Assert.Equal(preview.SourceGame, root.GetProperty("sourceGame").GetString());
        Assert.Equal(result.InstallPath, root.GetProperty("copiedOrAdoptedInstallPath").GetString());
        Assert.Equal(mode == WindowsGsmServerImportMode.Copy, root.GetProperty("sourceLeftUnchanged").GetBoolean());
        Assert.True(root.GetProperty("importedUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.False(root.TryGetProperty("SourceType", out _));
        Assert.False(root.TryGetProperty("ImportedFrom", out _));
    }

    private static WindowsGsmServerImportRow GetPreviewRow(WindowsGsmServerImportPreview preview, string key)
    {
        return preview.Rows.Single(row => row.Key == key);
    }

    private WindowsGsmServerImportService CreateService()
    {
        return new WindowsGsmServerImportService(CreateImportTargetRoot());
    }

    private string CreateImportTargetRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var serversPath = Path.Combine(root, "servers");
        _tempRoots.Add(root);
        return serversPath;
    }

    private TestWindowsGsmServer CreateWindowsGsmServer(
        string windowsGsmConfig = "servername=Copied Server",
        bool createConfig = true,
        bool createServerFiles = true,
        bool createExecutable = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        var serverFolder = Path.Combine(root, "WindowsGSM", "servers", "1");
        var configFolder = Path.Combine(serverFolder, "configs");
        var serverFilesPath = Path.Combine(serverFolder, "serverfiles");
        Directory.CreateDirectory(configFolder);
        var configPath = Path.Combine(configFolder, "WindowsGSM.cfg");
        if (createConfig)
        {
            File.WriteAllText(configPath, windowsGsmConfig);
        }

        if (createServerFiles)
        {
            Directory.CreateDirectory(serverFilesPath);
            if (createExecutable)
            {
                var executablePath = Path.Combine(serverFilesPath, TestModule.ExecutablePath);
                Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
                File.WriteAllText(executablePath, "fake executable");
            }
        }

        return new TestWindowsGsmServer(root, serverFolder, serverFilesPath, configPath);
    }

    private sealed record TestWindowsGsmServer(
        string Root,
        string ServerFolder,
        string ServerFilesPath,
        string ConfigPath);

    private class TestModule : IGameServerModule
    {
        public const string ExecutablePath = "GameServer.exe";

        public virtual string Id => "test";

        public string Name => "Test Module";

        public string Version => "1.0.0";

        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: true);

        public ModuleRuntimeDefinition Runtime => new(ExecutablePath, ["GameServer"]);

        public virtual SteamInstallDefinition? SteamInstall => null;

        public virtual IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
        {
            return
            [
                new ConfigFieldDefinition("server.name", "Server Name", ConfigFieldType.Text, Required: true),
                new ConfigFieldDefinition("network.port", "Server Port", ConfigFieldType.Port, 17777, Required: true)
            ];
        }

        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];

        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];

        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId)
        {
            return new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "");
        }

        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public virtual string GetServerName(IReadOnlyDictionary<string, object?> settings)
        {
            return settings.TryGetValue("server.name", out var value) ? value?.ToString() ?? "" : "";
        }

        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
        {
            return new ServerDisplayInfo("127.0.0.1", "17777", "");
        }

        public virtual Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }

        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
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

        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }
    }

    private sealed class ThrowingNameModule : TestModule
    {
        public override string GetServerName(IReadOnlyDictionary<string, object?> settings)
        {
            throw new InvalidOperationException("name exploded");
        }
    }

    private sealed class ThrowingReadModule : TestModule
    {
        public override Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("read exploded");
        }
    }

    private sealed class SteamTestModule : TestModule
    {
        public override SteamInstallDefinition? SteamInstall => new("12345");
    }

    private sealed class BooleanNumberTestModule : TestModule
    {
        public override IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
        {
            return
            [
                new ConfigFieldDefinition("server.name", "Server Name", ConfigFieldType.Text, Required: true),
                new ConfigFieldDefinition("network.port", "Server Port", ConfigFieldType.Port, 17777, Required: true),
                new ConfigFieldDefinition("server.enabled", "Enabled", ConfigFieldType.Boolean),
                new ConfigFieldDefinition("server.rate", "Rate", ConfigFieldType.Number)
            ];
        }
    }

    private sealed class AlternateTestModule : TestModule
    {
        public override string Id => "other-test";
    }
}
