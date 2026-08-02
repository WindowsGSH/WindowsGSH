using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using WindowsGSH.Core.Query;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Modules;

public sealed class JsonGameServerModule :
    IGameServerModule,
    IModuleApiActionCapability,
    IModuleGracefulStopCapability
{
    private readonly ModuleManifest _manifest;
    private readonly IReadOnlyList<ConfigFieldDefinition> _configFields;
    private readonly IReadOnlyList<ServerBackupTargetDefinition> _backupTargets;
    private readonly IReadOnlyList<ServerPortDefinition> _ports;
    private readonly IReadOnlyList<ServerAddonDefinition> _addons;
    private readonly AddonPackageService _addonPackageService = new();

    private JsonGameServerModule(ModuleManifest manifest, string sourcePath)
    {
        _manifest = manifest;
        SourcePath = sourcePath;
        Id = manifest.Id;
        Name = manifest.Name;
        Version = manifest.Version;
        SteamInstall = manifest.ToSteamInstall();
        Runtime = manifest.ToRuntime();
        Capabilities = manifest.ToCapabilities(
            supportsQuery: IsQuerySupported(Runtime.QueryProtocol),
            supportsRcon: false);
        _configFields = manifest.ToConfigFields();
        _backupTargets = manifest.ToBackupTargets();
        _ports = manifest.ToPorts();
        _addons = manifest.ToAddons();
        ModulePortSnapshotStore.Register(this, _ports);
    }

    public string SourcePath { get; }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    public ModuleCapabilities Capabilities { get; }

    public SteamInstallDefinition? SteamInstall { get; }

    public ModuleRuntimeDefinition Runtime { get; }

    public static JsonGameServerModule Load(string moduleJsonPath, Action<ModuleValidationMessage>? warningSink = null)
    {
        return new JsonGameServerModule(ModuleManifest.Load(moduleJsonPath, warningSink), moduleJsonPath);
    }

    public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => _configFields;

    public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => _addons;

    public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => _backupTargets;

    public IReadOnlyList<ServerPortDefinition> GetPorts() => _ports;

    public ModuleApiConnectionDefinition? GetApiConnection() => _manifest.ToApiConnection();

    public IReadOnlyList<ModuleApiActionDefinition> GetApiActions() => _manifest.ToApiActions();

    public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId)
    {
        var addon = GetAddon(addonId);
        return addon.Package == null
            ? new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Manual addon")
            : _addonPackageService.GetStatus(instance, addon);
    }

    public async Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        var addon = GetAddon(addonId);
        await _addonPackageService.InstallAsync(instance, addon, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        return _addonPackageService.RemoveAsync(instance, addonId, cancellationToken);
    }

    private ServerAddonDefinition GetAddon(string addonId) =>
        _addons.FirstOrDefault(addon => string.Equals(addon.Id, addonId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Addon '{addonId}' is not defined by {Name}.");

    public string GetServerName(IReadOnlyDictionary<string, object?> settings)
    {
        return GetSetting(settings, "server.name", $"{Name} Server");
    }

    public ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
    {
        return new ServerDisplayInfo(
            GetSetting(instance.Settings, "network.proxyAddress", "0.0.0.0"),
            GetSetting(instance.Settings, "network.port", GetSetting(instance.Settings, "network.directConnectionPort", "")),
            GetSetting(instance.Settings, "server.maxPlayers", ""));
    }

    public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (SteamInstall == null)
        {
            throw new NotSupportedException("This module does not define a SteamCMD install.");
        }

        var plan = new InstallPlan(
            "steamcmd",
            $"+force_install_dir \"{instance.InstallPath}\" +login anonymous +app_update {SteamInstall.AppId} validate +quit",
            instance.InstallPath);
        return Task.FromResult(plan);
    }

    public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var executable = ModuleLaunchPolicy.ResolveExecutableInsideInstallRoot(instance, Runtime.StartPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = instance.InstallPath,
            UseShellExecute = !ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            RedirectStandardOutput = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime),
            RedirectStandardError = ConsoleInputStrategyPolicy.UsesRedirectedStreams(Runtime)
        };
        ModuleLaunchPolicy.AddCompatibilityArguments(startInfo, BuildArguments(instance.Settings));

        return Task.FromResult(startInfo);
    }

    public async Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!IsInstallValid(instance))
        {
            throw new FileNotFoundException("Server executable was not found.", Path.Combine(instance.InstallPath, Runtime.StartPath));
        }

        var process = new Process
        {
            StartInfo = await CreateStartInfoAsync(instance, cancellationToken),
            EnableRaisingEvents = true
        };
        process.Start();
        return process;
    }

    public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Process>>([]);
    }

    public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return ModuleStopStrategyRunner.StopAsync(this, _manifest, instance, cancellationToken);
    }

    public Task StopGracefullyAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return ModuleStopStrategyRunner.StopAsync(
            this,
            _manifest,
            instance,
            cancellationToken,
            allowKill: false);
    }

    public bool IsInstallValid(ServerInstance instance)
    {
        try
        {
            return File.Exists(ModuleLaunchPolicy.ResolveExecutableInsideInstallRoot(instance, Runtime.StartPath));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    public string? GetConsoleLogPath(ServerInstance instance)
    {
        var logPath = _manifest.Runtime?.LogPath;
        return string.IsNullOrWhiteSpace(logPath)
            ? null
            : Path.Combine(instance.InstallPath, NormalizePath(logPath));
    }

    public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("This module does not provide RCON automation.");
    }

    public async Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (string.Equals(Runtime.QueryProtocol, "A2S", StringComparison.OrdinalIgnoreCase))
        {
            return await QueryA2sAsync(instance, cancellationToken);
        }

        if (string.Equals(Runtime.QueryProtocol, "EOS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Runtime.QueryProtocol, "EOS/A2S", StringComparison.OrdinalIgnoreCase))
        {
            var eosResult = await QueryEosAsync(instance, cancellationToken);
            if (eosResult.Status == ModuleServerStatus.Online ||
                !string.Equals(Runtime.QueryProtocol, "EOS/A2S", StringComparison.OrdinalIgnoreCase))
            {
                return eosResult;
            }

            var a2sResult = await QueryA2sAsync(instance, cancellationToken);
            if (a2sResult.Status == ModuleServerStatus.Online)
            {
                return a2sResult;
            }

            return eosResult with
            {
                Message = CombineQueryMessages(eosResult.Message, a2sResult.Message)
            };
        }

        if (IsFiveMQuery(Runtime.QueryProtocol))
        {
            return await QueryFiveMAsync(instance, cancellationToken);
        }

        if (IsUnreal2Query(Runtime.QueryProtocol))
        {
            return await QueryUnreal2Async(instance, cancellationToken);
        }

        var status = ServerProcessLocator.IsRunning(this, instance.InstallPath)
            ? ModuleServerStatus.Online
            : ModuleServerStatus.Offline;
        return new QueryResult(status, Message: "Process status only.");
    }

    private static async Task<QueryResult> QueryA2sAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var host = GetSetting(instance.Settings, "network.ip", "127.0.0.1");
        if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            host = "127.0.0.1";
        }

        var port = int.TryParse(GetSetting(instance.Settings, "network.queryPort", ""), out var parsedPort)
            ? parsedPort
            : int.TryParse(GetSetting(instance.Settings, "network.port", ""), out var gamePort)
                ? gamePort
                : 27015;

        try
        {
            var info = await new SourceA2sClient().QueryInfoAsync(host, port, TimeSpan.FromSeconds(2), cancellationToken);
            return new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: info.Players,
                MaxPlayers: info.MaxPlayers,
                Version: info.Version,
                Map: info.Map,
                Game: info.Game,
                QueryDurationMilliseconds: info.QueryDurationMilliseconds,
                Players: info.PlayerRows,
                DetailMessage: info.DetailMessage,
                Protocol: "A2S",
                Message: string.IsNullOrWhiteSpace(info.Map)
                    ? $"A2S responded from {host}:{port}."
                    : $"A2S responded from {host}:{port}. Map: {info.Map}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"A2S query to {host}:{port} timed out.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"A2S query to {host}:{port} failed: {ex.Message}");
        }
    }

    private static async Task<QueryResult> QueryEosAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var host = GetConnectableHost(GetSetting(instance.Settings, "network.ip", "127.0.0.1"));
        var port = int.TryParse(GetSetting(instance.Settings, "network.queryPort", ""), out var parsedPort)
            ? parsedPort
            : int.TryParse(GetSetting(instance.Settings, "network.port", ""), out var gamePort)
                ? gamePort
                : 27015;
        var deploymentId = GetSetting(instance.Settings, "eos.deploymentId", "");
        var clientId = GetSetting(instance.Settings, "eos.clientId", "");
        var clientSecret = GetSetting(instance.Settings, "eos.clientSecret", "");

        try
        {
            var info = await new EosQueryClient().QueryInfoAsync(
                host,
                port,
                deploymentId,
                clientId,
                clientSecret,
                TimeSpan.FromSeconds(5),
                cancellationToken);

            return new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: info.Players,
                MaxPlayers: info.MaxPlayers,
                Version: info.Version,
                Map: info.Map,
                Game: "EOS",
                QueryDurationMilliseconds: info.QueryDurationMilliseconds,
                Players: [],
                DetailMessage: info.DetailMessage,
                Protocol: "EOS",
                Message: string.IsNullOrWhiteSpace(info.Map)
                    ? $"EOS responded from {host}:{port}."
                    : $"EOS responded from {host}:{port}. Map: {info.Map}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"EOS query to {host}:{port} timed out.");
        }
        catch (Exception ex)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"EOS query to {host}:{port} failed: {ex.Message}");
        }
    }

    private static async Task<QueryResult> QueryFiveMAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var host = GetConnectableHost(GetSetting(instance.Settings, "network.ip", "127.0.0.1"));
        var port = int.TryParse(GetSetting(instance.Settings, "network.queryPort", ""), out var parsedPort)
            ? parsedPort
            : int.TryParse(GetSetting(instance.Settings, "network.port", ""), out var gamePort)
                ? gamePort
                : 30120;

        try
        {
            var info = await new FiveMQueryClient().QueryInfoAsync(host, port, TimeSpan.FromSeconds(5), cancellationToken);
            return new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: info.Players,
                MaxPlayers: info.MaxPlayers,
                Version: info.Version,
                Map: info.Map,
                Game: "FiveM",
                QueryDurationMilliseconds: info.QueryDurationMilliseconds,
                Players: info.PlayerRows,
                DetailMessage: info.DetailMessage,
                Protocol: "FiveM",
                Message: string.IsNullOrWhiteSpace(info.Map)
                    ? $"FiveM responded from {host}:{port}."
                    : $"FiveM responded from {host}:{port}. Map: {info.Map}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"FiveM query to {host}:{port} timed out.");
        }
        catch (Exception ex)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"FiveM query to {host}:{port} failed: {ex.Message}");
        }
    }

    private static async Task<QueryResult> QueryUnreal2Async(ServerInstance instance, CancellationToken cancellationToken)
    {
        var host = GetConnectableHost(GetSetting(instance.Settings, "network.ip", "127.0.0.1"));
        var port = int.TryParse(GetSetting(instance.Settings, "network.queryPort", ""), out var parsedPort)
            ? parsedPort
            : int.TryParse(GetSetting(instance.Settings, "network.port", ""), out var gamePort)
                ? gamePort
                : 27015;

        try
        {
            var info = await new Unreal2QueryClient().QueryInfoAsync(host, port, TimeSpan.FromSeconds(3), cancellationToken);
            return new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: info.Players,
                MaxPlayers: info.MaxPlayers,
                Version: info.Version,
                Map: info.Map,
                Game: info.Game,
                QueryDurationMilliseconds: info.QueryDurationMilliseconds,
                Players: info.PlayerRows,
                DetailMessage: info.DetailMessage,
                Protocol: "Unreal2",
                Message: string.IsNullOrWhiteSpace(info.Map)
                    ? $"Unreal2/UT3 responded from {host}:{port}."
                    : $"Unreal2/UT3 responded from {host}:{port}. Map: {info.Map}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"Unreal2/UT3 query to {host}:{port} timed out.");
        }
        catch (Exception ex)
        {
            return new QueryResult(ModuleServerStatus.Offline, Message: $"Unreal2/UT3 query to {host}:{port} failed: {ex.Message}");
        }
    }

    private static bool IsQuerySupported(string? queryProtocol)
    {
        return string.Equals(queryProtocol, "A2S", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(queryProtocol, "EOS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(queryProtocol, "EOS/A2S", StringComparison.OrdinalIgnoreCase) ||
            IsFiveMQuery(queryProtocol) ||
            IsUnreal2Query(queryProtocol);
    }

    private static string CombineQueryMessages(params string?[] messages)
    {
        return string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    private static bool IsFiveMQuery(string? queryProtocol)
    {
        return string.Equals(queryProtocol, "FiveM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(queryProtocol, "FIVEM", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnreal2Query(string? queryProtocol)
    {
        return string.Equals(queryProtocol, "UT3", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(queryProtocol, "Unreal2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(queryProtocol, "Unreal 2", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetConnectableHost(string host)
    {
        return string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
    }

    private string BuildArguments(IReadOnlyDictionary<string, object?> settings)
    {
        return ModuleLaunchArgumentBuilder.Build(_manifest.GetDefaultArguments(), settings);
    }

    private static string GetSetting(IReadOnlyDictionary<string, object?> settings, string key, string fallback)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())
            ? value.ToString()!.Trim()
            : fallback;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }
}
