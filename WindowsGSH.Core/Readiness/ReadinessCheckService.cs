using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using WindowsGSH.Core;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using WindowsGSH.Core.Windows;

namespace WindowsGSH.Core.Readiness;

public sealed class ReadinessCheckService
{
    private readonly InstalledServerLoader _serverLoader;

    // Preserves the original parameterless constructor's own metadata signature rather than
    // collapsing it into an optional parameter on the loader-accepting overload below - optional
    // arguments are resolved at compile time on the CALLER's side, not at the callee, so an
    // already-compiled consumer that referenced the old zero-argument constructor would hit a
    // MissingMethodException at runtime against a version of this assembly that no longer actually
    // has one.
    public ReadinessCheckService()
        : this(new InstalledServerLoader())
    {
    }

    // Accepts an existing loader so callers that already own one (MainWindow, in particular) can
    // share it instead of this service creating its own - InstalledServerLoader's per-server hang
    // protection (its _pendingServerLoads dedup dictionary) is scoped to the loader instance, not
    // app-wide, so a separate loader per ReadinessCheckService means a permanently hung module gets
    // its own independent stuck worker in each one.
    public ReadinessCheckService(InstalledServerLoader serverLoader)
    {
        _serverLoader = serverLoader;
    }

    public async Task<IReadOnlyList<ReadinessCheckResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ReadinessCheckResult>();
        var modules = await Task.Run(() => new ModuleRegistry().GetModules().ToArray(), cancellationToken).ConfigureAwait(false);
        var servers = await _serverLoader.LoadAsync(cancellationToken);

        results.Add(CheckWritableFolder("Application folder", AppPaths.AppDirectory, createIfMissing: false));
        results.Add(CheckDpapi());
        CheckSteamCmd(results);
        CheckServerRoot(results);
        CheckModuleStorage(results);
        CheckFirewall(results);
        CheckModules(results, modules);
        CheckDisk(results);
        CheckPorts(results, servers);
        CheckBackupPaths(results, servers, modules);
        CheckJava(results, servers, modules);
        results.AddRange(BuildLocalStateRecoveryResults());
        results.AddRange(await CheckModuleReadinessAsync(servers, modules, cancellationToken));

        return results;
    }

    public static IReadOnlyList<ReadinessCheckResult> BuildLocalStateRecoveryResults()
    {
        var events = LocalStateRecoveryStatus.Snapshot();
        if (events.Count == 0)
        {
            return
            [
                ReadinessCheckResult.Pass(
                    "Local state recovery",
                    "No settings or database recovery events were reported this session.")
            ];
        }

        return events.Select(item =>
        {
            var path = string.IsNullOrWhiteSpace(item.RecoveryPath)
                ? string.Empty
                : $" Recovery location: {item.RecoveryPath}";
            return item.IsFailure
                ? ReadinessCheckResult.Warning(item.Area, item.Message + path)
                : ReadinessCheckResult.Info(item.Area, item.Message + path);
        }).ToArray();
    }

    public IReadOnlyList<ReadinessCheckResult> CheckServerStartSafety(InstalledServer server)
    {
        var results = new List<ReadinessCheckResult>();
        CheckPortIsAvailable(results, server, includeServerName: true);
        return results;
    }

    private static void CheckSteamCmd(List<ReadinessCheckResult> results)
    {
        var steamCmd = new SteamCmdManager();
        var folderResult = CheckWritableFolder("SteamCMD folder", Path.GetDirectoryName(steamCmd.ExePath)!);
        results.Add(folderResult.Status == ReadinessStatus.Fail
            ? folderResult with { Action = ReadinessAction.OpenSteamCmdFolder, ActionLabel = "Open folder" }
            : folderResult);
        results.Add(File.Exists(steamCmd.ExePath)
            ? ReadinessCheckResult.Pass("SteamCMD", $"Found: {steamCmd.ExePath}")
            : ReadinessCheckResult.Warning(
                "SteamCMD",
                "SteamCMD is not installed yet. WindowsGSH can download it when needed; network availability is checked during the actual download.",
                ReadinessAction.RetrySteamCmdSetup,
                "Retry setup"));
    }

    private static void CheckServerRoot(List<ReadinessCheckResult> results)
    {
        var result = CheckWritableFolder("Servers folder", AppPaths.GetPath("servers"));
        results.Add(result.Status == ReadinessStatus.Fail
            ? result with { Action = ReadinessAction.OpenServersFolder, ActionLabel = "Open folder" }
            : result);
    }

    private static void CheckModuleStorage(List<ReadinessCheckResult> results)
    {
        results.Add(CheckModuleStoragePaths(
        [
            ModuleStoragePaths.InstalledModules,
            ModuleStoragePaths.DisabledModules,
            ModuleStoragePaths.ImportInbox,
            ModuleStoragePaths.ImportRejected,
            ModuleStoragePaths.ImportArchive
        ]));
    }

    internal static ReadinessCheckResult CheckModuleStoragePaths(IReadOnlyList<string> paths)
    {
        var failures = paths
            .Select(path => new
            {
                Path = path,
                Result = CheckWritableFolder("Module storage", path)
            })
            .Where(item => item.Result.Status == ReadinessStatus.Fail)
            .ToArray();

        return failures.Length == 0
            ? ReadinessCheckResult.Pass("Module storage", $"All {paths.Count} module storage folders are writable.")
            : ReadinessCheckResult.Fail(
                "Module storage",
                string.Join(" ", failures.Select(item => item.Result.Message)),
                ReadinessAction.OpenModuleStorage,
                "Open folder") with
            {
                ActionPath = failures[0].Path
            };
    }

    private static void CheckFirewall(List<ReadinessCheckResult> results)
    {
        var availability = new WindowsFirewallService().CheckAvailability();
        results.Add(availability.IsAvailable
            ? ReadinessCheckResult.Pass("Windows Firewall", availability.Message)
            : ReadinessCheckResult.Warning(
                "Windows Firewall",
                $"Firewall automation is unavailable. Servers can still run, but rules may need to be managed manually. {availability.Message}",
                ReadinessAction.OpenWindowsFirewall,
                "Open Firewall"));
    }

    public static ReadinessCheckResult CheckWritableFolder(
        string name,
        string path,
        bool createIfMissing = true)
    {
        try
        {
            if (createIfMissing)
            {
                Directory.CreateDirectory(path);
            }
            else if (!Directory.Exists(path))
            {
                return ReadinessCheckResult.Fail(name, $"Folder does not exist: {path}");
            }

            var probe = Path.Combine(path, $".windowsgsh-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, "ok");
            }
            finally
            {
                File.Delete(probe);
            }

            return ReadinessCheckResult.Pass(name, $"Writable: {path}");
        }
        catch (Exception ex)
        {
            return ReadinessCheckResult.Fail(name, $"Cannot write to {path}: {ex.Message}");
        }
    }

    public static ReadinessCheckResult CheckDpapi()
    {
        try
        {
            var plaintext = RandomNumberGenerator.GetBytes(32);
            var entropy = RandomNumberGenerator.GetBytes(16);
            var encrypted = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            var decrypted = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            return CryptographicOperations.FixedTimeEquals(plaintext, decrypted)
                ? ReadinessCheckResult.Pass("Windows user encryption", "DPAPI protect/unprotect round-trip succeeded for the current Windows user.")
                : ReadinessCheckResult.Fail("Windows user encryption", "DPAPI returned different data after a protect/unprotect round-trip.");
        }
        catch (Exception ex)
        {
            return ReadinessCheckResult.Fail(
                "Windows user encryption",
                $"DPAPI is unavailable for the current Windows user, so protected credentials cannot be stored: {ex.Message}");
        }
    }

    private static void CheckModules(List<ReadinessCheckResult> results, IReadOnlyList<IGameServerModule> modules)
    {
        results.Add(modules.Count > 0
            ? ReadinessCheckResult.Pass("Modules", $"Loaded {modules.Count} module(s): {string.Join(", ", modules.Select(module => module.Name))}")
            : ReadinessCheckResult.Fail("Modules", "No modules loaded.", ReadinessAction.OpenModuleManagement, "Manage modules"));
    }

    private static void CheckDisk(List<ReadinessCheckResult> results)
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.AppDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                results.Add(ReadinessCheckResult.Warning("Disk space", "Could not resolve application drive."));
                return;
            }

            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var result = freeGb < 5
                ? ReadinessCheckResult.Warning("Disk space", $"{freeGb:0.0} GB free on {drive.Name}.")
                : ReadinessCheckResult.Pass("Disk space", $"{freeGb:0.0} GB free on {drive.Name}.");
            results.Add(result);
        }
        catch (Exception ex)
        {
            results.Add(ReadinessCheckResult.Warning("Disk space", $"Could not check disk space: {ex.Message}"));
        }
    }

    private static void CheckPorts(List<ReadinessCheckResult> results, IReadOnlyList<InstalledServer> servers)
    {
        var duplicatePorts = servers
            .Where(server => int.TryParse(server.Port, out _))
            .GroupBy(server => server.Port)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in duplicatePorts)
        {
            results.Add(ReadinessCheckResult.Warning(
                "Port collision",
                $"Port {group.Key} is configured by: {string.Join(", ", group.Select(server => server.Name))}."));
        }

        foreach (var server in servers.Where(server => !server.CanStop))
        {
            CheckPortIsAvailable(results, server, includeServerName: true);
        }

        if (duplicatePorts.Length == 0)
        {
            results.Add(ReadinessCheckResult.Pass("Port collision", "No duplicate configured server ports found."));
        }
    }

    private static void CheckPortIsAvailable(List<ReadinessCheckResult> results, InstalledServer server, bool includeServerName)
    {
        if (!int.TryParse(server.Port, out var port) || port <= 0 || port > 65535)
        {
            results.Add(ReadinessCheckResult.Warning("Port check", $"{server.Name} has no valid direct port to check."));
            return;
        }

        var activeTcp = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        var activeUdp = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(endpoint => endpoint.Port == port);
        if (activeTcp || activeUdp)
        {
            var owner = includeServerName ? $"{server.Name}: " : string.Empty;
            results.Add(ReadinessCheckResult.Fail("Port check", $"{owner}port {port} is already active on this machine."));
            return;
        }

        results.Add(ReadinessCheckResult.Pass("Port check", $"{server.Name}: port {port} appears available."));
    }

    private static void CheckBackupPaths(List<ReadinessCheckResult> results, IReadOnlyList<InstalledServer> servers, IReadOnlyList<IGameServerModule> modules)
    {
        foreach (var server in servers)
        {
            var module = modules.FirstOrDefault(item => string.Equals(item.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
            if (module?.Capabilities.SupportsBackups != true)
            {
                continue;
            }

            var invalid = module.GetBackupTargets()
                .Select(target => target.RelativePath)
                .Where(path => Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
                .ToArray();
            results.Add(invalid.Length == 0
                ? ReadinessCheckResult.Pass("Backup paths", $"{server.Name}: module backup paths are relative.")
                : ReadinessCheckResult.Fail("Backup paths", $"{server.Name}: invalid module backup paths: {string.Join(", ", invalid)}"));
        }
    }

    private static void CheckJava(
        List<ReadinessCheckResult> results,
        IReadOnlyList<InstalledServer> servers,
        IReadOnlyList<IGameServerModule> modules)
    {
        var javaServers = servers
            .Select(server => new
            {
                Server = server,
                Module = modules.FirstOrDefault(module => string.Equals(module.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Module?.Capabilities.RequiresJava == true)
            .ToArray();

        if (javaServers.Length == 0)
        {
            var runtime = new JavaRuntimeLocator().Locate();
            if (!runtime.Found)
            {
                results.Add(ReadinessCheckResult.Info("Java", "Java was not found. This is only needed for Java-based server modules."));
                return;
            }

            var versionText = runtime.MajorVersion.HasValue ? $"Java {runtime.MajorVersion}" : "Java version unknown";
            results.Add(ReadinessCheckResult.Pass("Java", $"{versionText} found: {runtime.ExecutablePath}"));
            return;
        }

        var manager = new JavaRuntimeManager();
        var managedStore = new ManagedJavaStore();
        foreach (var item in javaServers)
        {
            ServerInstance instance;
            try
            {
                instance = ServerInstanceFactory.Load(item.Server);
            }
            catch (Exception ex)
            {
                results.Add(ReadinessCheckResult.Fail("Java", $"{item.Server.Name}: could not read Java settings: {ex.Message}"));
                continue;
            }

            var java = instance.AppSettings.Java;
            var requiredMajor = item.Module!.Capabilities.MinimumJavaMajor ?? 1;
            var validation = manager.Validate(java, requiredMajor, managedStore);
            var runtime = validation.Runtime;
            if (!validation.IsValid)
            {
                results.Add(ReadinessCheckResult.Fail("Java", $"{item.Server.Name}: {validation.Message}", ReadinessAction.OpenJavaSettings, "Java settings"));
                continue;
            }

            var source = !string.IsNullOrWhiteSpace(java.ManagedRuntimeId)
                ? $"managed ({java.ManagedRuntimeId})"
                : string.IsNullOrWhiteSpace(java.RuntimePath) ? "auto-detected" : "configured";
            results.Add(ReadinessCheckResult.Pass("Java", $"{item.Server.Name}: Java {runtime.MajorVersion!.Value} found ({source}): {runtime.ExecutablePath}"));
        }
    }

    public static async Task<IReadOnlyList<ReadinessCheckResult>> CheckModuleReadinessAsync(
        IReadOnlyList<InstalledServer> servers,
        IReadOnlyList<IGameServerModule> modules,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ReadinessCheckResult>();
        foreach (var server in servers)
        {
            var module = modules.FirstOrDefault(item => string.Equals(item.Id, server.ModuleId, StringComparison.OrdinalIgnoreCase));
            if (module is not IModuleReadinessCapability readinessModule)
            {
                continue;
            }

            try
            {
                var instance = ServerInstanceFactory.Load(server);
                results.AddRange(await readinessModule.CheckReadinessAsync(instance, cancellationToken));
            }
            catch (Exception ex)
            {
                var moduleName = module?.Name ?? server.ModuleId;
                results.Add(ReadinessCheckResult.Fail($"{moduleName} readiness", $"{server.Name}: module readiness check failed: {ex.Message}"));
            }
        }

        return results;
    }
}

public sealed record ReadinessCheckResult(
    string Name,
    ReadinessStatus Status,
    string Message,
    ReadinessAction? Action = null,
    string? ActionLabel = null)
{
    public ReadinessCheckResult(string name, ReadinessStatus status, string message)
        : this(name, status, message, null, null)
    {
    }

    public string? ActionPath { get; init; }

    public bool HasAction => Action.HasValue;

    public static ReadinessCheckResult Pass(string name, string message) => new(name, ReadinessStatus.Pass, message);

    public static ReadinessCheckResult Info(string name, string message) => new(name, ReadinessStatus.Info, message);

    public static ReadinessCheckResult Warning(string name, string message) => new(name, ReadinessStatus.Warning, message);

    public static ReadinessCheckResult Fail(string name, string message) => new(name, ReadinessStatus.Fail, message);

    public static ReadinessCheckResult Warning(string name, string message, ReadinessAction action, string actionLabel = "Fix")
        => new(name, ReadinessStatus.Warning, message, action, actionLabel);

    public static ReadinessCheckResult Fail(string name, string message, ReadinessAction action, string actionLabel = "Fix")
        => new(name, ReadinessStatus.Fail, message, action, actionLabel);
}

public enum ReadinessStatus
{
    Pass,
    Info,
    Warning,
    Fail
}

public enum ReadinessAction
{
    OpenServersFolder,
    OpenModuleStorage,
    OpenSteamCmdFolder,
    OpenWindowsFirewall,
    OpenJavaSettings,
    OpenModuleManagement,
    RetrySteamCmdSetup,
}

