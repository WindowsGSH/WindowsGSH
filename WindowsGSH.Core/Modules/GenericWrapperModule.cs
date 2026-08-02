using System.Diagnostics;
using System.IO;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Modules;

public sealed class GenericWrapperModule :
    IGameServerModule,
    IModuleExistingServerImportCapability,
    IModuleGracefulStopCapability,
    IModuleConsoleCommandCapability,
    IModuleReadinessCapability
{
    public const string ModuleId = "generic-wrapper";
    public const string LaunchPathKey = "launch.path";
    public const string LaunchArgumentsKey = "launch.arguments";
    public const string WorkingDirectoryKey = "launch.workingDirectory";
    public const string LaunchModeKey = "launch.mode";
    public const string RedirectConsoleKey = "launch.redirectConsole";
    public const string StopModeKey = "stop.mode";
    public const string StopCommandKey = "stop.command";
    public const string StopWaitSecondsKey = "stop.waitSeconds";

    private static readonly IReadOnlyList<ConfigFieldDefinition> Fields =
    [
        new("server.name", "Server Name", ConfigFieldType.Text, "Generic Server", Required: true),
        new("network.ip", "Server IP", ConfigFieldType.Text, "0.0.0.0", Description: "Used for display and firewall planning. The wrapper does not rewrite your server config."),
        new("network.port", "Server Port", ConfigFieldType.Port, 25565, Required: true, Minimum: 1, Maximum: 65535),
        // Deliberately blank by default, not a number at all - two earlier attempts both had real
        // problems: matching network.port's default avoided a firewall-rule regression (see
        // GetPorts() below) but made this field indistinguishable from "not actually a separate
        // port," and giving it its own distinct default silently doubled a fresh wrapper server's
        // firewall exposure (WindowsFirewallService.GetRequiredRules derives an inbound TCP+UDP
        // rule pair from every ConfigFieldType.Port field regardless of key) to a port nothing is
        // listening on. Blank sidesteps both: TryCreatePortRule/TryParsePort-style callers across
        // the codebase already skip a field whose value doesn't parse as a number, so an unfilled
        // query port asks for no firewall/health coverage at all - exactly like not declaring it -
        // while still leaving GetPorts() free to declare it as a real, optional port that becomes
        // live the moment a user actually fills in a value for a server that has one.
        new("network.queryPort", "Query Port", ConfigFieldType.Port, null, Minimum: 1, Maximum: 65535, Description: "Optional. Only set this if your server answers queries on a different port from the game port above."),
        new("server.maxPlayers", "Max Players", ConfigFieldType.Number, 20, Minimum: 1, Maximum: 10000),
        new(LaunchPathKey, "Launch Target", ConfigFieldType.Path, "start.bat", Required: true, Description: "Relative to the imported server folder, or an absolute path to an executable/script."),
        new(LaunchArgumentsKey, "Launch Arguments", ConfigFieldType.CommandLine, "", Description: "Extra arguments passed to the launch target."),
        new(WorkingDirectoryKey, "Working Directory", ConfigFieldType.Path, ".", Description: "Relative to the imported server folder, or an absolute folder path."),
        new(LaunchModeKey, "Launch Mode", ConfigFieldType.Select, "Auto", Required: true, Options: ["Auto", "Direct", "Batch", "PowerShell", "Shell"], Description: "Auto chooses Direct for executables, Batch for .bat/.cmd, and PowerShell for .ps1."),
        new(RedirectConsoleKey, "Embedded Console", ConfigFieldType.Boolean, true, Description: "Capture stdout/stderr and allow stdin commands when the selected launch mode supports it."),
        new(StopModeKey, "Stop Mode", ConfigFieldType.Select, "ConsoleCommandThenKill", Required: true, Options: ["ConsoleCommandThenKill", "CloseWindowThenKill", "KillOnly"], Description: "Minecraft-style servers usually use ConsoleCommandThenKill with the command stop."),
        new(StopCommandKey, "Stop Command", ConfigFieldType.Text, "stop", Description: "Command sent to stdin when Stop Mode is ConsoleCommandThenKill."),
        new(StopWaitSecondsKey, "Stop Wait Seconds", ConfigFieldType.Number, 20, Minimum: 0, Maximum: 300)
    ];

    public GenericWrapperModule()
    {
        ModulePortSnapshotStore.Register(this, GetPorts());
    }

    public string Id => ModuleId;

    public string Name => "Generic Server Wrapper";

    public string Version => "1.0.0";

    public ModuleCapabilities Capabilities => new(
        SupportsInstall: false,
        SupportsUpdate: false,
        SupportsQuery: false,
        SupportsRcon: false,
        SupportsConsoleCommands: true,
        SupportsApiActions: false,
        SupportsBackups: true,
        SupportsDirectConnection: true);

    public ModuleRuntimeDefinition Runtime => new(
        "start.bat",
        ["cmd", "powershell", "pwsh", "java", "javaw"],
        AllowsEmbeddedConsole: true,
        PortIncrements: 1,
        QueryProtocol: "process",
        ConsoleStrategy: ConsoleInputStrategy.Redirected);

    public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => Fields;

    // "query" is declared here too now (it wasn't for a while - see network.queryPort's own
    // comment above for why that was itself a problem: ServerHealthService/WindowsFirewallService
    // still treat every ConfigFieldType.Port field as authoritative regardless of GetPorts(), so
    // omitting it here just meant two disagreeing sources of truth once something actually
    // migrates onto IServerPortResolver instead of that older scanning). Not Required, and its
    // configField has no default - a fresh server leaves it Unresolved (not Invalid, not
    // overlapping game), and it only starts resolving to a real port once a user who actually has
    // a distinct query listener fills the field in.
    public IReadOnlyList<ServerPortDefinition> GetPorts() =>
    [
        // Imported generic servers may use TCP or UDP and the wrapper has no authoritative
        // transport setting. Either means one transport is sufficient; Both remains reserved for
        // modules that explicitly require simultaneous TCP and UDP listeners.
        new("game", "Server Port", PortProtocol.Either, ConfigField: "network.port", Required: true),
        new("query", "Query Port", PortProtocol.Udp, ConfigField: "network.queryPort")
    ];

    public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() =>
    [
        new("server-files", "Server files", ".", IsDirectory: true)
    ];

    public string GetServerName(IReadOnlyDictionary<string, object?> settings)
    {
        return GetSetting(settings, "server.name", "Generic Server");
    }

    public ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
    {
        return new ServerDisplayInfo(
            GetSetting(instance, "network.ip", "0.0.0.0"),
            GetSetting(instance, "network.port", ""),
            GetSetting(instance, "server.maxPlayers", ""));
    }

    public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        return Task.FromException<InstallPlan>(
            new NotSupportedException("Generic Server Wrapper imports existing server folders instead of installing server files."));
    }

    public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var launch = ResolveLaunchTarget(instance);
        var workingDirectory = ResolveWorkingDirectory(instance);
        var arguments = GetSetting(instance, LaunchArgumentsKey, "");
        var mode = ResolveLaunchMode(GetSetting(instance, LaunchModeKey, "Auto"), launch);
        var redirect = GetBool(instance, RedirectConsoleKey, defaultValue: true) && mode != LaunchMode.Shell;

        var startInfo = mode switch
        {
            LaunchMode.Batch => CreateBatchStartInfo(launch, arguments),
            LaunchMode.PowerShell => CreatePowerShellStartInfo(launch, arguments),
            LaunchMode.Shell => CreateTokenizedStartInfo(launch, arguments, useShellExecute: true),
            LaunchMode.Direct => CreateTokenizedStartInfo(launch, arguments, useShellExecute: false),
            _ => throw new InvalidOperationException($"Unsupported wrapper launch mode: {mode}.")
        };

        startInfo.WorkingDirectory = workingDirectory;
        if (!startInfo.UseShellExecute)
        {
            startInfo.RedirectStandardInput = redirect;
            startInfo.RedirectStandardOutput = redirect;
            startInfo.RedirectStandardError = redirect;
        }

        return Task.FromResult(startInfo);
    }

    public async Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!IsInstallValid(instance))
        {
            throw new FileNotFoundException("Wrapper launch target was not found.", GetSetting(instance, LaunchPathKey, ""));
        }

        var process = new Process
        {
            StartInfo = await CreateStartInfoAsync(instance, cancellationToken).ConfigureAwait(false),
            EnableRaisingEvents = true
        };
        process.Start();
        return process;
    }

    public async Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var processes = ServerProcessLocator.FindProcesses(this, instance.InstallPath);
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                await StopProcessAsync(process, instance, cancellationToken, allowKill: true).ConfigureAwait(false);
            }
        }
    }

    public async Task StopGracefullyAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var processes = ServerProcessLocator.FindProcesses(this, instance.InstallPath);
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                await StopProcessAsync(process, instance, cancellationToken, allowKill: false).ConfigureAwait(false);
            }
        }
    }

    public bool IsInstallValid(ServerInstance instance)
    {
        try
        {
            return File.Exists(ResolveLaunchTarget(instance));
        }
        catch
        {
            return false;
        }
    }

    public string? GetConsoleLogPath(ServerInstance instance)
    {
        var logPath = GetSetting(instance, "logs.path", "");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return null;
        }

        return ResolvePath(instance.InstallPath, logPath, requireInsideInstallPath: false);
    }

    public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var status = ServerProcessLocator.IsRunning(this, instance.InstallPath)
            ? ModuleServerStatus.Online
            : ModuleServerStatus.Offline;
        return Task.FromResult(new QueryResult(status, Message: "Process status only."));
    }

    public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
    {
        return Task.FromException<string>(new NotSupportedException("Generic Server Wrapper does not provide RCON automation."));
    }

    public Task<string> ExecuteConsoleCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
    {
        ServerConsoleService.SendCommand(instance.Id, command);
        return Task.FromResult("Console command sent.");
    }

    public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];

    public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) =>
        new(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Manual addon");

    public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Generic Server Wrapper does not provide addon installation automation."));

    public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Generic Server Wrapper does not provide addon removal automation."));

    public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Process>>([]);

    public bool CanImport(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    public Task<ModuleExistingServerImportProbe> PreviewImportAsync(string path, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(path);
        var launchTarget = FindDefaultLaunchTarget(source);
        var settings = Fields.ToDictionary(
            field => field.Key,
            field => field.DefaultValue,
            StringComparer.OrdinalIgnoreCase);

        settings["server.name"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (!string.IsNullOrWhiteSpace(launchTarget))
        {
            settings[LaunchPathKey] = Path.GetRelativePath(source, launchTarget);
        }

        var warnings = new List<string>
        {
            "Generic imports do not understand game-specific config files. Review ports, launch arguments, and stop behavior before starting."
        };
        if (string.IsNullOrWhiteSpace(launchTarget))
        {
            warnings.Add("No obvious launch target was found. Add the launch target manually before importing.");
        }

        return Task.FromResult(new ModuleExistingServerImportProbe(
            SourceName: Path.GetFileName(Path.TrimEndingDirectorySeparator(source)),
            InstallPath: source,
            Settings: settings,
            Warnings: warnings));
    }

    public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheckResult>();
        try
        {
            var launch = ResolveLaunchTarget(instance);
            checks.Add(File.Exists(launch)
                ? new ReadinessCheckResult("Wrapper launch target", ReadinessStatus.Pass, $"Launch target exists: {launch}")
                : new ReadinessCheckResult("Wrapper launch target", ReadinessStatus.Fail, $"Launch target is missing: {launch}"));

            var workingDirectory = ResolveWorkingDirectory(instance);
            checks.Add(Directory.Exists(workingDirectory)
                ? new ReadinessCheckResult("Wrapper working directory", ReadinessStatus.Pass, $"Working directory exists: {workingDirectory}")
                : new ReadinessCheckResult("Wrapper working directory", ReadinessStatus.Fail, $"Working directory is missing: {workingDirectory}"));
        }
        catch (Exception ex)
        {
            checks.Add(new ReadinessCheckResult("Wrapper configuration", ReadinessStatus.Fail, ex.Message));
        }

        return Task.FromResult<IReadOnlyList<ReadinessCheckResult>>(checks);
    }

    private static ProcessStartInfo CreateBatchStartInfo(string launch, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        // Batch mode is an explicitly selected privileged local configuration. cmd.exe must
        // receive one command string to support .bat/.cmd semantics; the target is validated as
        // an existing file and quoted, while the configured command-line field remains intentional.
        startInfo.ArgumentList.Add($"{WindowsCommandLineEscaper.Quote(launch)}{AppendArguments(arguments)}");
        return startInfo;
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string launch, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(launch);
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    private static ProcessStartInfo CreateTokenizedStartInfo(string launch, string arguments, bool useShellExecute)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launch,
            UseShellExecute = useShellExecute,
            CreateNoWindow = !useShellExecute,
            WindowStyle = useShellExecute ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
        AddArguments(startInfo, arguments);
        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, string arguments)
    {
        foreach (var argument in WindowsCommandLineParser.Split(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string AppendArguments(string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim();

    private static string ResolveLaunchTarget(ServerInstance instance)
    {
        var launchPath = GetSetting(instance, LaunchPathKey, "");
        if (string.IsNullOrWhiteSpace(launchPath))
        {
            throw new InvalidOperationException("Wrapper launch target is required.");
        }

        var resolved = ResolvePath(instance.InstallPath, launchPath, requireInsideInstallPath: false);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("Wrapper launch target was not found.", resolved);
        }

        return resolved;
    }

    private static string ResolveWorkingDirectory(ServerInstance instance)
    {
        var configured = GetSetting(instance, WorkingDirectoryKey, ".");
        if (string.IsNullOrWhiteSpace(configured) || configured == ".")
        {
            var installPath = Path.GetFullPath(instance.InstallPath);
            if (!Directory.Exists(installPath))
            {
                throw new DirectoryNotFoundException($"Wrapper working directory was not found: {installPath}");
            }

            return installPath;
        }

        var resolved = ResolvePath(instance.InstallPath, configured, requireInsideInstallPath: false);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Wrapper working directory was not found: {resolved}");
        }

        return resolved;
    }

    private static string ResolvePath(string installPath, string path, bool requireInsideInstallPath)
    {
        var candidate = path.Trim().Trim('"');
        var fullPath = Path.GetFullPath(Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(installPath, candidate));

        if (requireInsideInstallPath && !ModuleImportPathPlanner.IsPathInsideDirectory(installPath, fullPath))
        {
            throw new InvalidOperationException($"Path must stay inside the server install folder: {path}");
        }

        return fullPath;
    }

    private static LaunchMode ResolveLaunchMode(string configured, string launchTarget)
    {
        if (!string.Equals(configured, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<LaunchMode>(configured, ignoreCase: true, out var parsed) &&
                parsed != LaunchMode.Auto)
            {
                return parsed;
            }

            throw new InvalidOperationException($"Unsupported wrapper launch mode: {configured}.");
        }

        return Path.GetExtension(launchTarget).ToLowerInvariant() switch
        {
            ".bat" or ".cmd" => LaunchMode.Batch,
            ".ps1" => LaunchMode.PowerShell,
            ".exe" or ".com" => LaunchMode.Direct,
            _ => LaunchMode.Shell
        };
    }

    private async Task StopProcessAsync(
        Process process,
        ServerInstance instance,
        CancellationToken cancellationToken,
        bool allowKill)
    {
        var stopMode = GetSetting(instance, StopModeKey, "ConsoleCommandThenKill");
        var wait = TimeSpan.FromSeconds(Math.Clamp(GetInt(instance, StopWaitSecondsKey, 20), 0, 300));

        if (string.Equals(stopMode, "ConsoleCommandThenKill", StringComparison.OrdinalIgnoreCase))
        {
            await TrySendStopCommandAsync(process, GetSetting(instance, StopCommandKey, "stop"), cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(stopMode, "CloseWindowThenKill", StringComparison.OrdinalIgnoreCase))
        {
            TryCloseMainWindow(process);
        }

        if (wait > TimeSpan.Zero && !process.HasExited &&
            !string.Equals(stopMode, "KillOnly", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        if (!allowKill || process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TrySendStopCommandAsync(Process process, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch
        {
        }

        TryCloseMainWindow(process);
    }

    private static void TryCloseMainWindow(Process process)
    {
        try
        {
            process.CloseMainWindow();
        }
        catch
        {
        }
    }

    private static string? FindDefaultLaunchTarget(string source)
    {
        var preferredNames = new[]
        {
            "start.bat",
            "run.bat",
            "server.bat",
            "start.cmd",
            "run.cmd",
            "server.cmd",
            "server.exe",
            "bedrock_server.exe"
        };

        foreach (var name in preferredNames)
        {
            var path = Path.Combine(source, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => IsLaunchCandidate(path));
    }

    private static bool IsLaunchCandidate(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1";
    }

    private static string GetSetting(ServerInstance instance, string key, string fallback) =>
        GetSetting(instance.Settings, key, fallback);

    private static string GetSetting(IReadOnlyDictionary<string, object?> settings, string key, string fallback)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())
            ? value.ToString()!.Trim()
            : fallback;
    }

    private static bool GetBool(ServerInstance instance, string key, bool defaultValue)
    {
        if (!instance.Settings.TryGetValue(key, out var value) || value == null)
        {
            return defaultValue;
        }

        return value is bool typed
            ? typed
            : bool.TryParse(value.ToString(), out var parsed)
                ? parsed
                : defaultValue;
    }

    private static int GetInt(ServerInstance instance, string key, int defaultValue)
    {
        if (!instance.Settings.TryGetValue(key, out var value) || value == null)
        {
            return defaultValue;
        }

        return value is int typed
            ? typed
            : int.TryParse(value.ToString(), out var parsed)
                ? parsed
                : defaultValue;
    }

    private enum LaunchMode
    {
        Auto,
        Direct,
        Batch,
        PowerShell,
        Shell
    }
}
