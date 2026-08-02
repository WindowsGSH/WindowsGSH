using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.SecretTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Protect_replaces_password_fields_with_references_and_resolves_them()
    {
        var config = JsonNode.Parse("""
            {
              "settings": {
                "adminCode": "module-secret",
                "name": "Visible"
              },
              "addons": {
                "plugin": {
                  "settings": {
                    "accessCode": "addon-secret"
                  }
                }
              }
            }
            """)!.AsObject();
        var store = new ServerSecretStore();
        var module = new SecretModule();

        var changed = store.ProtectConfigSecrets(module, _root, config);

        Assert.True(changed);
        Assert.True(ServerSecretStore.IsReference(config["settings"]!["adminCode"]!.GetValue<string>()));
        Assert.True(ServerSecretStore.IsReference(config["addons"]!["plugin"]!["settings"]!["accessCode"]!.GetValue<string>()));
        Assert.Equal("Visible", config["settings"]!["name"]!.GetValue<string>());
        var serialized = config.ToJsonString();
        Assert.DoesNotContain("module-secret", serialized);
        Assert.DoesNotContain("addon-secret", serialized);
        Assert.True(File.Exists(Path.Combine(_root, "server-secrets.json")));

        store.ResolveConfigSecrets(module, _root, config);

        Assert.Equal("module-secret", config["settings"]!["adminCode"]!.GetValue<string>());
        Assert.Equal("addon-secret", config["addons"]!["plugin"]!["settings"]!["accessCode"]!.GetValue<string>());
    }

    [Fact]
    public void Server_instance_factory_resolves_protected_settings()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "ServerConfig.json");
        var config = JsonNode.Parse("""
            {
              "id": "1",
              "name": "Server",
              "moduleId": "secret-module",
              "installPath": "files",
              "settings": {
                "adminCode": "module-secret"
              }
            }
            """)!.AsObject();
        var store = new ServerSecretStore();
        store.ProtectConfigSecrets(new SecretModule(), _root, config);
        File.WriteAllText(configPath, config.ToJsonString());
        var server = CreateServer(configPath);

        var instance = ServerInstanceFactory.Load(server);

        Assert.Equal("module-secret", instance.Settings["adminCode"]);
    }

    [Fact]
    public void Server_instance_factory_fails_when_protected_secret_is_missing()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "ServerConfig.json");
        File.WriteAllText(configPath, """
            {
              "id": "1",
              "name": "Server",
              "moduleId": "secret-module",
              "installPath": "files",
              "settings": {
                "adminCode": "dpapi://server-secret/settings.adminCode"
              }
            }
            """);
        var server = CreateServer(configPath);

        var exception = Assert.Throws<InvalidOperationException>(() => ServerInstanceFactory.Load(server));

        Assert.Contains("settings.adminCode", exception.Message);
        Assert.Contains("unavailable", exception.Message);
        Assert.Contains("server-secrets.json", exception.Message);
    }

    [Fact]
    public void Resolve_config_fails_when_reference_targets_another_setting()
    {
        var config = JsonNode.Parse("""
            {
              "settings": {
                "adminCode": "dpapi://server-secret/settings.otherCode"
              }
            }
            """)!.AsObject();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServerSecretStore().ResolveConfigSecrets(new SecretModule(), _root, config));

        Assert.Contains("settings.otherCode", exception.Message);
        Assert.Contains("settings.adminCode", exception.Message);
    }

    private InstalledServer CreateServer(string configPath)
    {
        return new InstalledServer(
            "1", "Server", "secret-module", "native", _root, Path.Combine(_root, "files"), configPath,
            "0.0.0.0", "25565", "", "public", "20", "--", "--", "--", "--", "Offline", "--",
            false, "", null, true, ServerRuntimeStatus.Offline, "Offline", "TextBrush", false, "", "", "",
            true, true, true, false);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class SecretModule : IGameServerModule
    {
        public string Id => "secret-module";
        public string Name => "Secret Module";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("0.0.0.0", "25565", "20");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => File.Exists(Path.Combine(instance.InstallPath, "server.exe"));
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() =>
        [
            new("adminCode", "Admin code", ConfigFieldType.Password),
            new("name", "Name", ConfigFieldType.Text)
        ];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() =>
        [
            new(
                "plugin",
                "Plugin",
                "",
                Capabilities,
                [new ConfigFieldDefinition("accessCode", "Access code", ConfigFieldType.Password)])
        ];
    }
}
