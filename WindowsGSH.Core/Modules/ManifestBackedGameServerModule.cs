using System.Diagnostics;
using System.IO;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Modules;

public abstract class ManifestBackedGameServerModule :
    IGameServerModule,
    IManifestBackedModule,
    IModuleSteamInstallCapability,
    IModuleConfigCapability,
    IModuleGracefulStopCapability,
    IModuleBackupCapability,
    IModulePortCapability,
    IModuleAddonCapability,
    IModuleApiActionCapability
{
    private ModuleManifest? _manifest;
    private readonly AddonPackageService _addonPackageService = new();

    protected ModuleManifest Manifest => _manifest ??= ModuleManifest.Load(Path.Combine(ModuleDirectory, "module.json"));

    protected string ModuleDirectory { get; private set; } = AppContext.BaseDirectory;

    public virtual string Id => Manifest.Id;

    public virtual string Name => Manifest.Name;

    public virtual string Version => Manifest.Version;

    public virtual ModuleCapabilities Capabilities => Manifest.ToCapabilities();

    public virtual SteamInstallDefinition? SteamInstall => Manifest.ToSteamInstall();

    public virtual ModuleRuntimeDefinition Runtime => Manifest.ToRuntime();

    public virtual void Configure(ModuleManifest manifest, string moduleDirectory)
    {
        _manifest = manifest;
        ModuleDirectory = moduleDirectory;
    }

    public virtual IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
    {
        return Manifest.ToConfigFields();
    }

    public virtual IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets()
    {
        return Manifest.ToBackupTargets();
    }

    public virtual IReadOnlyList<ServerPortDefinition> GetPorts()
    {
        return Manifest.ToPorts();
    }

    public virtual IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions()
    {
        return Manifest.ToAddons();
    }

    public virtual ModuleApiConnectionDefinition? GetApiConnection()
    {
        return Manifest.ToApiConnection();
    }

    public virtual IReadOnlyList<ModuleApiActionDefinition> GetApiActions()
    {
        return Manifest.ToApiActions();
    }

    public virtual ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId)
    {
        var addon = GetAddonDefinition(addonId);
        return addon.Package == null
            ? new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Manual addon")
            : _addonPackageService.GetStatus(instance, addon);
    }

    public virtual async Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        await _addonPackageService.InstallAsync(instance, GetAddonDefinition(addonId), cancellationToken).ConfigureAwait(false);
    }

    public virtual Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        return _addonPackageService.RemoveAsync(instance, addonId, cancellationToken);
    }

    private ServerAddonDefinition GetAddonDefinition(string addonId) =>
        GetAddonDefinitions().FirstOrDefault(addon => string.Equals(addon.Id, addonId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Addon '{addonId}' is not defined by {Name}.");

    public virtual Task<IReadOnlyList<Process>> StartAddonProcessesAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Process>>([]);
    }

    public virtual string GetServerName(IReadOnlyDictionary<string, object?> settings)
    {
        return GetSetting(settings, "server.name", $"{Name} Server");
    }

    public virtual ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
    {
        return new ServerDisplayInfo(
            IpAddress: GetSetting(instance.Settings, "network.ip", "0.0.0.0"),
            Port: GetSetting(instance.Settings, "network.port", GetSetting(instance.Settings, "network.directConnectionPort", "")),
            MaxPlayers: GetSetting(instance.Settings, "server.maxPlayers", ""));
    }

    public virtual Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    public virtual Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (SteamInstall == null)
        {
            throw new NotSupportedException($"{Name} does not define a SteamCMD install.");
        }

        return Task.FromResult(new InstallPlan(
            "steamcmd",
            $"+force_install_dir \"{instance.InstallPath}\" +login anonymous +app_update {SteamInstall.AppId} validate +quit",
            instance.InstallPath));
    }

    public virtual Task<ProcessStartInfo> CreateStartInfoAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
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
        ModuleLaunchPolicy.AddCompatibilityArguments(startInfo, BuildLaunchArguments(instance));

        return Task.FromResult(startInfo);
    }

    public virtual async Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!IsInstallValid(instance))
        {
            throw new FileNotFoundException("Server executable was not found.", Path.Combine(instance.InstallPath, Runtime.StartPath));
        }

        await OnBeforeStartAsync(instance, cancellationToken).ConfigureAwait(false);
        var process = new Process
        {
            StartInfo = await CreateStartInfoAsync(instance, cancellationToken).ConfigureAwait(false),
            EnableRaisingEvents = true
        };
        process.Start();
        return process;
    }

    public virtual Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return ModuleStopStrategyRunner.StopAsync(this, Manifest, instance, cancellationToken);
    }

    public Task StopGracefullyAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return ModuleStopStrategyRunner.StopAsync(
            this,
            Manifest,
            instance,
            cancellationToken,
            allowKill: false);
    }

    public virtual bool IsInstallValid(ServerInstance instance)
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

    public virtual string? GetConsoleLogPath(ServerInstance instance)
    {
        var logPath = Manifest.Runtime?.LogPath;
        return string.IsNullOrWhiteSpace(logPath)
            ? null
            : Path.Combine(instance.InstallPath, NormalizePath(logPath));
    }

    protected virtual Task OnBeforeStartAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return WriteConfigFileSettingsAsync(instance, cancellationToken);
    }

    protected virtual string BuildLaunchArguments(ServerInstance instance)
    {
        return ModuleLaunchArgumentBuilder.Build(Manifest.GetDefaultArguments(), instance.Settings);
    }

    protected static string GetSetting(ServerInstance instance, string key, string fallback)
    {
        return GetSetting(instance.Settings, key, fallback);
    }

    protected static string GetSetting(IReadOnlyDictionary<string, object?> settings, string key, string fallback)
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
