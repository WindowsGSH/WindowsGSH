using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Steam;
using WindowsGSH.Core.Windows;

namespace WindowsGSH.Core.Health;

public enum ServerHealthSeverity
{
    Pass,
    Info,
    Warning,
    Fail
}

public sealed record ServerHealthCheck(
    string Category,
    string Name,
    ServerHealthSeverity Severity,
    string Message);

public sealed record ServerHealthReport(
    string ServerId,
    string ServerName,
    IReadOnlyList<ServerHealthCheck> Checks)
{
    public string? SourceFolderId { get; init; }

    public ServerHealthSeverity OverallSeverity =>
        Checks.Any(check => check.Severity == ServerHealthSeverity.Fail) ? ServerHealthSeverity.Fail :
        Checks.Any(check => check.Severity == ServerHealthSeverity.Warning) ? ServerHealthSeverity.Warning :
        Checks.Any(check => check.Severity == ServerHealthSeverity.Info) ? ServerHealthSeverity.Info :
        ServerHealthSeverity.Pass;

    public IReadOnlyList<ReadinessCheckResult> ToReadinessResults()
    {
        return Checks.Select(check => new ReadinessCheckResult(
            $"{check.Category}: {check.Name}",
            check.Severity switch
            {
                ServerHealthSeverity.Pass => ReadinessStatus.Pass,
                ServerHealthSeverity.Info => ReadinessStatus.Info,
                ServerHealthSeverity.Warning => ReadinessStatus.Warning,
                _ => ReadinessStatus.Fail
            },
            check.Message)).ToArray();
    }
}

public sealed record ServerHealthRequest(
    InstalledServer Server,
    ModuleDescriptor? ModuleDescriptor,
    IReadOnlyList<InstalledServer> AllServers,
    IReadOnlyList<FirewallRuleStatus>? FirewallRules = null,
    string? FirewallError = null,
    bool PublicIpTrackingEnabled = false,
    string? LastKnownPublicIp = null,
    DateTimeOffset? LastPublicIpCheckedAt = null,
    IReadOnlyList<ModuleDescriptor>? ModuleDescriptors = null,
    // Sourced from WindowsGSH.Data.OperationHistoryRepository.GetRecentForServer, which
    // WindowsGSH.Core cannot reference directly (Data depends on Core, not the other way around) -
    // the caller gathers this the same way it already gathers FirewallRules/FirewallError, mirroring
    // that exact (list, error) shape: GetRecentForServer deliberately does not swallow its own
    // failures (unlike GetRecent/Add/GetServerLifecycleTimes, which exist to guarantee a real server
    // action is never blocked by history trouble) specifically so a database read failure and "no
    // history recorded yet" stay distinguishable here rather than both silently reading as Info.
    IReadOnlyList<ServerOperationSnapshot>? RecentOperations = null,
    string? RecentOperationsError = null);

public sealed class ServerHealthService
{
    private readonly JavaRuntimeManager _javaRuntimeManager;
    private readonly ManagedJavaStore _managedJavaStore;
    private readonly IServerPortResolver _portResolver;
    private readonly Func<string> _steamCmdExePathResolver;
    private readonly Func<string, bool> _steamCmdAvailabilityProbe;
    private readonly Func<string, long?> _freeDiskSpaceProvider;
    private readonly Func<ActiveListeners> _activeListenersProvider;

    public ServerHealthService(
        JavaRuntimeManager? javaRuntimeManager = null,
        ManagedJavaStore? managedJavaStore = null,
        IServerPortResolver? portResolver = null,
        Func<string>? steamCmdExePathResolver = null,
        Func<string, bool>? steamCmdAvailabilityProbe = null,
        Func<string, long?>? freeDiskSpaceProvider = null,
        Func<ActiveListeners>? activeListenersProvider = null)
    {
        _javaRuntimeManager = javaRuntimeManager ?? new JavaRuntimeManager();
        _managedJavaStore = managedJavaStore ?? new ManagedJavaStore();
        _portResolver = portResolver ?? new ServerPortResolver();
        _steamCmdExePathResolver = steamCmdExePathResolver ?? (() => AppPaths.GetPath("steamcmd", "steamcmd.exe"));
        // Must agree with SteamCmdManager.EnsureInstalledAsync's own trust policy (file exists AND
        // passes Valve Authenticode verification), not just File.Exists - EnsureInstalledAsync
        // silently wipes and re-downloads an unsigned/corrupt/tampered steamcmd.exe the next time
        // it runs, so a check that only looked at existence could report "available" for a file
        // the app itself is about to reject and replace.
        _steamCmdAvailabilityProbe = steamCmdAvailabilityProbe ??
            (path => File.Exists(path) && SteamCmdManager.HasValveAuthenticodeSignature(path));
        _freeDiskSpaceProvider = freeDiskSpaceProvider ?? DefaultFreeDiskSpaceProvider;
        _activeListenersProvider = activeListenersProvider ?? DefaultActiveListenersProvider;
    }

    private static long? DefaultFreeDiskSpaceProvider(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.AvailableFreeSpace : null;
    }

    private static ActiveListeners DefaultActiveListenersProvider()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return new ActiveListeners(
            properties.GetActiveTcpListeners(),
            properties.GetActiveUdpListeners(),
            NativeConnectionTable.GetTcpListenersWithOwners(),
            NativeConnectionTable.GetUdpListenersWithOwners());
    }

    // A record (reference type), not a value tuple - TryInvokeModule<T>'s `out T? result` relies on
    // T? meaning "ordinary nullable reference" the same way it does for every other injected
    // delegate in this file (_steamCmdExePathResolver, _steamCmdAvailabilityProbe); a value-tuple T
    // would make T? a Nullable<ValueTuple<...>>, which doesn't deconstruct the same simple way at
    // the call site. Public, not internal - it appears in this class's own public constructor
    // signature (activeListenersProvider), which can't reference a less-visible type.
    //
    // TcpOwners/UdpOwners are the PID-ownership extension anticipated by this record's own prior
    // comment - optional trailing parameters (default null) so every existing 2-arg call site (test
    // doubles that only care about "is anything listening") keeps compiling unchanged. Null
    // specifically means "ownership data unavailable" (the lookup itself failed, or a test double
    // never supplied it) - never "nothing is owned." An empty, non-null list means the lookup
    // succeeded and genuinely found nothing. AddLocalPortListeningChecks/GetPortRangeOwnershipStatus
    // must keep this distinction: a null owners list downgrades a port to "ownership unknown," the
    // same conservative fallback used before this field existed, not to a false "not owned" claim.
    public sealed record ActiveListeners(
        IReadOnlyList<IPEndPoint> TcpListeners,
        IReadOnlyList<IPEndPoint> UdpListeners,
        IReadOnlyList<NativeConnectionTable.OwnedEndpoint>? TcpOwners = null,
        IReadOnlyList<NativeConnectionTable.OwnedEndpoint>? UdpOwners = null);

    public async Task<ServerHealthReport> EvaluateAsync(
        ServerHealthRequest request,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ServerHealthCheck>();
        var server = request.Server;
        var descriptor = request.ModuleDescriptor;
        var module = descriptor?.Module;

        AddModuleChecks(checks, server, descriptor);
        var instance = AddConfigChecks(checks, server, module);
        AddInstallChecks(checks, server, module, instance);
        var processCheck = AddProcessChecks(checks, module, instance);
        AddCrashHistoryChecks(checks, server);
        AddOperationHistoryChecks(checks, request.RecentOperations, request.RecentOperationsError);
        var portInspection = AddPortChecks(checks, server, instance, module, request.AllServers, request.ModuleDescriptors);
        AddLocalPortListeningChecks(checks, portInspection, processCheck);
        AddDiskSpaceChecks(checks, server);
        AddFirewallChecks(checks, request.FirewallRules, request.FirewallError);
        AddQueryChecks(checks, server, module);
        AddBackupChecks(checks, module, instance);
        AddJavaChecks(checks, module, instance);
        AddSteamCmdChecks(checks, module, instance);
        AddPublicIpChecks(checks, request);
        await AddModuleReadinessChecksAsync(checks, module, instance, cancellationToken).ConfigureAwait(false);

        return new ServerHealthReport(server.Id, server.Name, checks);
    }

    public string BuildSupportSummary(ServerHealthRequest request, ServerHealthReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"WindowsGSH server health: {report.ServerName} ({report.ServerId})");
        builder.AppendLine($"Module: {request.ModuleDescriptor?.Name ?? request.Server.ModuleId}");
        builder.AppendLine($"Overall: {report.OverallSeverity}");
        builder.AppendLine($"Install path: {request.Server.InstallPath}");
        builder.AppendLine($"Address: {request.Server.IpAddress}:{request.Server.Port}");
        builder.AppendLine();
        foreach (var check in report.Checks)
        {
            builder.AppendLine($"[{check.Severity}] {check.Category} / {check.Name}: {check.Message}");
        }

        if (File.Exists(request.Server.ConfigPath))
        {
            builder.AppendLine();
            builder.AppendLine("Redacted config:");
            try
            {
                // module.GetConfigFields() is the same arbitrary compiled-module call already made
                // exception-safe (and never message-leaking) elsewhere in this file - it deserves
                // identical treatment here. The catch below deliberately isn't narrowed to specific
                // exception types (IOException/JsonException/InvalidOperationException, as it used
                // to be): a module throwing anything else (ArgumentException,
                // UnauthorizedAccessException, a custom module exception) would otherwise propagate
                // out of BuildSupportSummary entirely, discarding the checks list already built
                // above it - this section is optional (the raw config redaction), not worth losing
                // the rest of the summary over.
                var passwordKeys = request.ModuleDescriptor?.Module.GetConfigFields()
                    .Where(field => field.Type == ConfigFieldType.Password)
                    .Select(field => field.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                builder.AppendLine(RedactConfigJson(File.ReadAllText(request.Server.ConfigPath), passwordKeys));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never ex.Message - GetConfigFields() and the config file itself are both outside
                // this method's control, and either could surface a real setting value (a module
                // exception embedding it, or a config path/permission error echoing file content) in
                // free text a "known secret keys" redaction pass like RedactConfigJson can't reach.
                builder.AppendLine("[Config unavailable.]");
            }
        }

        return builder.ToString();
    }

    public static string RedactConfigJson(string configJson)
    {
        return RedactConfigJson(configJson, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public static string RedactConfigJson(string configJson, IReadOnlySet<string> additionalSecretKeys)
    {
        var root = JsonNode.Parse(configJson);
        RedactNode(root, additionalSecretKeys);
        return root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
    }

    private static void AddModuleChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server,
        ModuleDescriptor? descriptor)
    {
        if (descriptor == null)
        {
            checks.Add(Fail("Module", "Loaded", $"Module '{server.ModuleId}' is not loaded."));
            return;
        }

        checks.Add(Pass("Module", "Loaded", $"{descriptor.Name} {descriptor.Version} is loaded."));
        checks.Add(Pass("Module", "Compatibility", "The module loaded under the current module API."));
        checks.Add(!string.IsNullOrWhiteSpace(descriptor.Provenance.Warning)
            ? Warning("Module", "Provenance", descriptor.Provenance.Warning)
            : Pass("Module", "Provenance", "Module files match their import state."));
        foreach (var warning in descriptor.ValidationWarnings)
        {
            checks.Add(Warning("Module", warning.Code, warning.Message));
        }
    }

    private static ServerInstance? AddConfigChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server,
        IGameServerModule? module)
    {
        if (!File.Exists(server.ConfigPath))
        {
            checks.Add(Fail("Configuration", "Config file", $"Config file is missing: {server.ConfigPath}"));
            return null;
        }

        ServerInstance instance;
        try
        {
            instance = ServerInstanceFactory.Load(server);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // ServerInstanceFactory.Load only ever touches the config file itself (missing/
            // malformed JSON, bad paths) - no module code runs here, so its exception messages
            // are safe to show directly, same as they always have been.
            checks.Add(Fail("Configuration", "Config file", $"Configuration could not be read: {ex.Message}"));
            return null;
        }

        checks.Add(Pass("Configuration", "Config file", "Server configuration parsed successfully."));

        if (module == null)
        {
            return instance;
        }

        // module.GetConfigFields() is arbitrary compiled module code, and IReadOnlyList<T> only
        // guarantees Count/indexer/GetEnumerator - nothing stops a module from returning a
        // lazily-evaluated (or otherwise stateful) collection whose enumeration itself throws
        // (e.g. backed by deferred I/O), even though the GetConfigFields() call that produced it
        // returns successfully. Materializing it to an array (.ToArray()) inside this same guarded
        // lambda, and returning that trusted snapshot rather than the original module-controlled
        // reference, matters for two separate reasons: it forces the only enumeration to happen
        // here (where a thrown exception is caught and never surfaced), and it stops
        // ValidateSettings below from ever touching the module's own collection a second time - a
        // stateful/deferred collection could succeed on a first enumeration (this one) and still
        // throw on a second, and that second throw would land in the catch below, which trusts
        // InvalidOperationException as safe-to-display because it normally only ever comes from
        // ConfigFieldValidationService's own label-only exceptions, not from arbitrary module code.
        // Called as a standalone step, separately from ValidateSettings below, specifically so that
        // call's own (verified-safe, label-only) exceptions can't be conflated with this one just
        // because both happen to throw the same common exception type.
        if (!TryInvokeModule(() =>
            {
                var materialized = module.GetConfigFields().ToArray();
                if (materialized.Any(field => field == null))
                {
                    throw new InvalidOperationException("Module returned malformed configuration fields.");
                }

                return materialized;
            }, out var fields))
        {
            checks.Add(Fail("Configuration", "Module fields", "This module's configuration fields could not be determined due to an internal error."));
            return instance;
        }

        try
        {
            // fields is a materialized array snapshot, non-null and entry-clean - TryInvokeModule
            // only returns true when the lambda above completed without throwing, and that lambda
            // itself throws instead of returning on a null GetConfigFields() result, a null entry,
            // or a failed enumeration.
            ConfigFieldValidationService.ValidateSettings(fields!, instance.Settings);
            checks.Add(Pass("Configuration", "Module fields", "Module configuration fields are valid."));
        }
        catch (InvalidOperationException ex)
        {
            // ConfigFieldValidationService's own exceptions only ever reference a field's Label
            // (module manifest metadata) or its own author-written ValidationMessage - never the
            // actual setting value - so these are safe to surface directly.
            checks.Add(Fail("Configuration", "Module fields", ex.Message));
        }

        return instance;
    }

    private static void AddInstallChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server,
        IGameServerModule? module,
        ServerInstance? instance)
    {
        checks.Add(Directory.Exists(server.InstallPath)
            ? Pass("Installation", "Install folder", $"Install folder exists: {server.InstallPath}")
            : Fail("Installation", "Install folder", $"Install folder is missing: {server.InstallPath}"));
        if (module == null || instance == null)
        {
            return;
        }

        // module.Runtime is a property getter, not a field - for a manifest-backed module it
        // re-parses the manifest's runtime section on every access, and any module's own custom
        // getter logic could throw for any reason. Path.Combine/Path.GetFullPath on the result
        // (StartPath) can also throw on genuinely malformed path data, so both are covered by the
        // same guarded call.
        if (!TryInvokeModule(() => Path.GetFullPath(Path.Combine(instance.InstallPath, module.Runtime.StartPath)), out var executable))
        {
            checks.Add(Fail("Installation", "Executable", "This module's runtime/executable path could not be determined due to an internal error."));
            return;
        }

        bool installValid;
        try
        {
            installValid = module.IsInstallValid(instance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // module.IsInstallValid(instance) is arbitrary compiled module code with access to
            // instance.Settings - never surface its raw exception message, the same as every other
            // arbitrary module call in this file.
            checks.Add(Fail("Installation", "Module validation", "This module's install validation could not be completed due to an internal error."));
            return;
        }

        checks.Add(File.Exists(executable)
            ? Pass("Installation", "Executable", $"Executable exists: {executable}")
            : installValid
                ? Pass("Installation", "Executable", "The module resolved its configured launch target successfully; the manifest start path is a placeholder.")
                : Fail("Installation", "Executable", $"Executable is missing: {executable}"));
        checks.Add(installValid
            ? Pass("Installation", "Module validation", "The module considers this installation valid.")
            : Fail("Installation", "Module validation", "The module reports that this installation is invalid."));
    }

    private const double LowDiskSpaceThresholdGb = 5;

    // Free disk space for the drive containing this server's own install path - a full disk is a
    // real, silent cause of failed installs/updates/backups. Doesn't depend on the module or a
    // parsed ServerInstance at all (unlike AddJavaChecks/AddSteamCmdChecks), so unlike those this
    // always runs as long as the server itself is known - mirrors AddInstallChecks' own
    // unconditional "Install folder" check just above, which needs nothing but server.InstallPath
    // either. _freeDiskSpaceProvider is injected/replaceable, so it goes through TryInvokeModule
    // like every other dependency call in this file, even though the real default implementation
    // has no module involvement.
    private void AddDiskSpaceChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server)
    {
        if (!TryInvokeModule(() => _freeDiskSpaceProvider(server.InstallPath), out var freeBytes))
        {
            checks.Add(Fail("System", "Disk space", "Could not determine free disk space due to an internal error."));
            return;
        }

        if (freeBytes == null)
        {
            checks.Add(Warning("System", "Disk space", $"Could not determine free disk space for '{server.InstallPath}'."));
            return;
        }

        var freeGb = freeBytes.Value / 1024d / 1024d / 1024d;
        checks.Add(freeGb < LowDiskSpaceThresholdGb
            ? Warning("System", "Disk space", $"Only {freeGb:0.0} GB free on the drive containing this server's install path.")
            : Pass("System", "Disk space", $"{freeGb:0.0} GB free on the drive containing this server's install path."));
    }

    // Deliberately a fresh, on-demand re-check via ServerProcessLocator rather than reusing
    // InstalledServer's own IsProcessRunning-derived fields (Status/CanStop/ProcessId) - those
    // reflect whatever the last periodic refresh happened to observe, which can be stale by the
    // time a user actually opens Server Doctor. "One-click diagnostics" (this tier's own stated
    // goal) means re-verifying now, not redisplaying a cached answer.
    //
    // Process names that are only ever a shell/launcher, never the actual game/runtime process
    // itself - cmd.exe, powershell.exe, pwsh.exe. GenericWrapperModule's Batch/PowerShell launch
    // modes start one of these as the direct child .NET process (ServerRuntimeTracker.WriteRuntimeState
    // records *that* process's own pid/executable in runtime.json), while the real game process (a
    // JVM, a downloaded server binary, etc.) is spawned as its descendant and is never itself found by
    // ServerProcessLocator.FindProcesses unless it happens to live under the server's own install
    // path - Java in particular almost never does. When every process this check can confirm running
    // is one of these launchers, the true owning pid for any listening port is genuinely unknown, not
    // absent - see MatchedOnlyLauncherProcesses below.
    private static readonly HashSet<string> LauncherOnlyProcessNames =
        new(StringComparer.OrdinalIgnoreCase) { "cmd", "powershell", "pwsh" };

    // ProcessIds: the confirmed-running process ids (empty when running status genuinely couldn't be
    // confirmed - missing module/instance, or the internal error path below, not just when it's
    // confirmed stopped), so AddLocalPortListeningChecks can both gate on "is anything running" and
    // check whether a local listener is actually owned by one of these specific PIDs, instead of
    // asking ServerProcessLocator a second time (the exact "resolve once, reuse the result"
    // discipline AddPortChecks' own resolved-ports handling already established). A false positive
    // here would report configured ports as unexpectedly "not listening"/"not owned" for a server
    // whose actual running state is simply unknown, so an unconfirmed state must never be
    // represented as a non-empty PID set.
    //
    // MatchedOnlyLauncherProcesses: true when every confirmed-running process is a bare shell/launcher
    // (see LauncherOnlyProcessNames) - AddLocalPortListeningChecks must not attempt PID-ownership
    // matching against ProcessIds in that case, since none of them can be the real listening process.
    internal readonly record struct ProcessCheckResult(IReadOnlySet<int> ProcessIds, bool MatchedOnlyLauncherProcesses);

    private static ProcessCheckResult AddProcessChecks(
        ICollection<ServerHealthCheck> checks,
        IGameServerModule? module,
        ServerInstance? instance)
    {
        if (module == null || instance == null)
        {
            return new ProcessCheckResult(new HashSet<int>(), MatchedOnlyLauncherProcesses: false);
        }

        // ServerProcessLocator.FindProcesses reads module.Runtime (StartPath/ProcessNames) to
        // decide what to look for - like every other module property access in this file, that
        // can throw for a manifest-backed module that fails to re-parse, or a compiled module's
        // own custom getter. The list FindProcesses itself returns is this app's own first-party,
        // well-defined contract (always a real list, never null, no null entries - confirmed by
        // reading its implementation), unlike the IGameServerModule-implemented methods elsewhere
        // in this file, so this only needs isolation around the module-property-reading part of
        // the call, not the malformed-result defensiveness the other checks need for genuinely
        // third-party-implemented data. FindProcesses also already does the "matches expected
        // server" matching itself (start path/install path/runtime.json PID validation), so
        // nothing here has to re-implement that.
        if (!TryInvokeModule(() => ServerProcessLocator.FindProcesses(module, instance.InstallPath), out var processes))
        {
            checks.Add(Fail("Process", "Running", "This module's running-process check could not be completed due to an internal error."));
            return new ProcessCheckResult(new HashSet<int>(), MatchedOnlyLauncherProcesses: false);
        }

        try
        {
            if (processes!.Count == 0)
            {
                // Not every server is expected to be running all the time - a deliberately
                // stopped server isn't a health problem, matching how AddQueryChecks already
                // treats "offline" as Info rather than Fail.
                checks.Add(Info("Process", "Running", "No matching process is currently running for this server."));
                return new ProcessCheckResult(new HashSet<int>(), MatchedOnlyLauncherProcesses: false);
            }

            var pids = string.Join(", ", processes.Select(process => process.Id));
            checks.Add(Pass(
                "Process",
                "Running",
                processes.Count == 1
                    ? $"Process is running (PID {pids})."
                    : $"{processes.Count} matching processes are running (PIDs {pids})."));
            var matchedOnlyLaunchers = processes.All(process => IsLauncherOnlyProcess(process, instance.InstallPath));
            return new ProcessCheckResult(processes.Select(process => process.Id).ToHashSet(), matchedOnlyLaunchers);
        }
        finally
        {
            foreach (var process in processes!)
            {
                process.Dispose();
            }
        }
    }

    // Name alone isn't enough: this file's own RealProcessWithPortsModule/RealProcessHealthModule
    // test doubles use a real powershell.exe as a stand-in "real server process" and deliberately
    // point the server's install path at powershell.exe's own folder specifically so
    // ServerProcessLocator's install-path-containment check confirms it - the same mechanism that
    // would legitimately confirm a genuinely embedded interpreter/runtime shipped inside a server's
    // own folder. What actually makes the real bug scenario untrustworthy is that the system cmd.exe/
    // powershell.exe ServerProcessLocator.TryGetRuntimeStateProcess finds via runtime.json is
    // physically located outside the install directory entirely (e.g. C:\Windows\System32\cmd.exe) -
    // nothing about *that* path independently confirms it's the real server, unlike a shell whose own
    // executable genuinely lives under the server's install path.
    private static bool IsLauncherOnlyProcess(Process process, string installPath)
    {
        try
        {
            if (!LauncherOnlyProcessNames.Contains(process.ProcessName))
            {
                return false;
            }

            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                // Can't verify containment either way - conservatively treat as untrustworthy, since
                // the failure mode of wrongly forcing "ownership unknown" is far milder than the
                // failure mode of wrongly trusting a shell's own pid as the real listener's owner.
                return true;
            }

            var fullExecutablePath = Path.GetFullPath(executablePath);
            var fullInstallPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
            return !fullExecutablePath.StartsWith(fullInstallPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A process that can no longer be queried (exited between FindProcesses returning it and
            // this check running) can't be confirmed either way - same conservative default as above.
            return true;
        }
    }

    // ServerCrashDiagnosticsService.WriteUnexpectedExitReport writes one file per unexpected exit
    // to server.ServerFolder/CrashLogs/server-crash-{timestamp}-pid-{pid}.log and nothing ever
    // prunes that folder - pure local file I/O, no external data source, so (unlike operation
    // history below) this needs no new ServerHealthRequest field, matching how AddInstallChecks/
    // AddBackupChecks already read files directly rather than having them supplied externally.
    private static readonly TimeSpan RecentCrashWindow = TimeSpan.FromDays(7);
    private const string CrashReportFilePrefix = "server-crash-";
    private const string CrashReportTimestampFormat = "yyyyMMdd-HHmmss";

    private static void AddCrashHistoryChecks(ICollection<ServerHealthCheck> checks, InstalledServer server)
    {
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        DateTimeOffset[] crashTimestamps;
        try
        {
            crashTimestamps = Directory.Exists(crashDirectory)
                ? Directory.EnumerateFiles(crashDirectory, "server-crash-*.log")
                    .Select(GetCrashReportTimestamp)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same isolation as every other file-system check in this file (AddInstallChecks'
            // executable check, AddBackupChecks' target existence check) - a permission error or
            // similar reading this one server's own folder must not abort the rest of the report.
            checks.Add(Fail("Stability", "Crash history", "This server's crash report history could not be read due to an internal error."));
            return;
        }

        if (crashTimestamps.Length == 0)
        {
            checks.Add(Pass("Stability", "Crash history", "No crash reports have been recorded for this server."));
            return;
        }

        var mostRecent = crashTimestamps.Max();
        // A negative age (a future-dated timestamp - clock skew, a restored backup, a manually
        // touched file) must not count as "recent" at all, let alone forever: age >= TimeSpan.Zero
        // excludes it from the count entirely rather than trivially satisfying "<= 7 days" the way
        // any negative TimeSpan otherwise would.
        var recentCount = crashTimestamps.Count(timestamp =>
        {
            var age = DateTimeOffset.UtcNow - timestamp;
            return age >= TimeSpan.Zero && age <= RecentCrashWindow;
        });

        checks.Add(recentCount > 0
            ? Warning(
                "Stability",
                "Crash history",
                $"{recentCount} crash report(s) in the last 7 days (most recent: {FormatLocalTimestamp(mostRecent)}). See this server's CrashLogs folder or the crash summary shown at the time for details.")
            : Info(
                "Stability",
                "Crash history",
                $"{crashTimestamps.Length} historical crash report(s) on record; none in the last 7 days (most recent: {FormatLocalTimestamp(mostRecent)})."));
    }

    // The file's own LastWriteTimeUtc can be reset by anything that touches the file without
    // changing its content - a folder copy, a backup restore, an antivirus scan - making an old,
    // already-investigated crash look freshly "recent" days or months after the fact. The
    // timestamp embedded in the filename by ServerCrashDiagnosticsService.WriteUnexpectedExitReport
    // itself is controlled by nothing but this app's own writer, so it is used as the primary
    // source of truth; LastWriteTimeUtc is only a fallback for a file that doesn't match this app's
    // own naming convention (e.g. manually created or renamed).
    private static DateTimeOffset GetCrashReportTimestamp(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith(CrashReportFilePrefix, StringComparison.Ordinal) &&
            fileName.Length >= CrashReportFilePrefix.Length + CrashReportTimestampFormat.Length)
        {
            var candidate = fileName.Substring(CrashReportFilePrefix.Length, CrashReportTimestampFormat.Length);
            // WriteUnexpectedExitReport formats this from DateTimeOffset.Now (local time, not
            // UTC) - AssumeLocal + ToUniversalTime() converts it back consistently for comparison
            // against DateTimeOffset.UtcNow above.
            if (DateTime.TryParseExact(
                    candidate,
                    CrashReportTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsedLocal))
            {
                return new DateTimeOffset(parsedLocal.ToUniversalTime(), TimeSpan.Zero);
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero);
    }

    // recentOperations/error mirror AddFirewallChecks' (rules, error) shape exactly - gathered by
    // the caller (WindowsGSH.Data.OperationHistoryRepository.GetRecentForServer, which
    // WindowsGSH.Core cannot reference), an error string standing in for the list when the lookup
    // itself failed (that method deliberately does not swallow its own exceptions - see
    // ServerHealthRequest's own comment on this field for why).
    private static void AddOperationHistoryChecks(
        ICollection<ServerHealthCheck> checks,
        IReadOnlyList<ServerOperationSnapshot>? recentOperations,
        string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            checks.Add(Warning("Operations", "Recent history", error));
            return;
        }

        if (recentOperations == null || recentOperations.Count == 0)
        {
            checks.Add(Info("Operations", "Recent history", "No recorded operation history for this server yet."));
            return;
        }

        // LastError on a failed operation can be an arbitrary module exception's own Message (see
        // ServerOperationManager.FailOperation) - exactly as untrusted as every other module-
        // originated exception text this file already refuses to surface verbatim elsewhere. Only
        // the operation's Kind/State/timestamp (all safe, structured fields) are ever shown here;
        // the message points at Operation History, which already has its own dedicated, appropriate
        // place to show LastError, rather than duplicating it into this report/support summary.
        var mostRecent = recentOperations[0];
        var when = FormatLocalTimestamp(mostRecent.CompletedAt ?? mostRecent.StartedAt);
        var failureCount = recentOperations.Count(operation =>
            operation.State is ServerOperationStatus.Failed or ServerOperationStatus.Interrupted);

        // Severity must reflect the whole sampled batch, not just the newest entry - a server whose
        // last 9 operations failed and 10th happened to succeed is not a clean bill of health just
        // because the most recent one worked. Any Failed/Interrupted entry anywhere in the batch
        // forces at least a Warning, regardless of what the most recent operation's own outcome was;
        // only the message wording still depends on whether the most recent one specifically failed.
        if (failureCount > 0)
        {
            var failureNote = $"{failureCount} of the last {recentOperations.Count} recorded operations did not complete successfully.";
            checks.Add(mostRecent.State switch
            {
                ServerOperationStatus.Failed => Warning(
                    "Operations",
                    "Recent history",
                    $"The most recent operation ({mostRecent.Kind}, {when}) failed. {failureNote} Check Operation History for details."),
                ServerOperationStatus.Interrupted => Warning(
                    "Operations",
                    "Recent history",
                    $"The most recent operation ({mostRecent.Kind}, {when}) was interrupted before it finished. {failureNote} Check Operation History for details."),
                _ => Warning(
                    "Operations",
                    "Recent history",
                    $"The most recent operation ({mostRecent.Kind}, {when}) completed, but {failureNote} Check Operation History for details.")
            });
            return;
        }

        checks.Add(mostRecent.State == ServerOperationStatus.Cancelled
            ? Info("Operations", "Recent history", $"The most recent operation ({mostRecent.Kind}, {when}) was cancelled.")
            : Pass("Operations", "Recent history", $"Most recent operation ({mostRecent.Kind}) completed successfully ({when})."));
    }

    private static string FormatLocalTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    // Returns the same ConfiguredPort list it computes internally for conflict detection, so
    // AddLocalPortListeningChecks can check "is it actually listening" against the identical,
    // already-resolved port set rather than re-resolving (this method's own docs already call out
    // why resolving twice within one report is a real risk, not a style preference).
    private PortInspectionResult AddPortChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server,
        ServerInstance? instance,
        IGameServerModule? module,
        IReadOnlyList<InstalledServer> servers,
        IReadOnlyList<ModuleDescriptor>? descriptors)
    {
        // A module's own declared ports (IServerPortResolver, Tier 5.1) take priority once it has
        // any - they carry structure (protocol, required, ranges/offsets) the old scan never had,
        // including same-server overlap detection the resolver already performs internally. Not
        // every module declares ports yet (the shipped module catalog is still being migrated),
        // so this still falls back to scanning every
        // ConfigFieldType.Port field for anything that hasn't adopted the new model, rather than
        // reporting "no ports found" for the vast majority of servers today.
        //
        // Resolved exactly once for the selected server and reused for both the Invalid-port
        // checks and the ConfiguredPort list below - resolving twice (as an earlier version of
        // this method did) risked a stateful module or a transient resolver failure producing two
        // different answers to the same question within a single report: succeeding the first call
        // but throwing the second could show no "Port declarations" failure while still reporting
        // "no valid configured ports were found," which is self-contradictory.
        var resolved = TryResolveDeclaredPorts(module, instance, out var resolutionError);
        AddDeclaredPortValidationChecks(checks, resolved, resolutionError);

        var currentPorts = BuildConfiguredPorts(server, resolved, resolutionError, instance, module, out var selectedInspectionIncomplete);
        if (currentPorts.Count == 0)
        {
            checks.Add(Fail("Network", "Ports", "No valid configured ports were found."));
            return new PortInspectionResult(currentPorts, IsComplete: false);
        }

        // A required-but-missing/out-of-range declared port surfaces its own "Port: <name>" Fail
        // above via AddDeclaredPortValidationChecks - but BuildConfiguredPorts only ever includes
        // Resolved-status ports in currentPorts, so a different declared port resolving fine (or
        // the legacy Display-port fallback) could still leave currentPorts non-empty and
        // selectedInspectionIncomplete false, producing a flat "Configured ports are valid" Pass
        // sitting right next to that Fail - the exact same self-contradiction earlier rounds fixed
        // for resolution failures/null modules/null instances, just for this fourth root cause.
        var hasInvalidDeclaredPort = resolved != null && resolved.Any(port => port.Status == ResolvedPortStatus.Invalid);

        // When declared-port resolution failed, or the legacy config-field scan itself threw,
        // BuildConfiguredPorts deliberately produces only the legacy "Display port" fallback (never
        // both) - a flat Pass here would read as "ports are fine" right next to the Fail this same
        // evaluation already added above for the actual failure, which undersells how incomplete
        // this check really was.
        checks.Add(selectedInspectionIncomplete
            ? Warning(
                "Network",
                "Ports",
                $"Only the legacy display port could be validated; this module's full port information could not be determined: {string.Join(", ", currentPorts.Select(FormatPort))}.")
            : hasInvalidDeclaredPort
                ? Warning(
                    "Network",
                    "Ports",
                    $"One or more of this module's declared ports are invalid; only the successfully resolved ports could be validated: {string.Join(", ", currentPorts.Select(FormatPort))}.")
                : Pass(
                    "Network",
                    "Ports",
                    $"Configured ports are valid: {string.Join(", ", currentPorts.Select(FormatPort))}."));

        var descriptorById = (descriptors ?? [])
            .GroupBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();
        var uninspectedServers = new List<string>();
        foreach (var other in servers.Where(other => !string.Equals(other.Id, server.Id, StringComparison.Ordinal)))
        {
            descriptorById.TryGetValue(other.ModuleId, out var otherDescriptor);
            ServerInstance? otherInstance = null;
            // Missing, unreadable, or malformed config for an unrelated other server is a reason
            // that server can only be partially inspected, not a reason to silently pretend it
            // contributed no ports at all with no trace of why - tracked the same way a module/
            // resolver failure is below, so the honest "N server(s) could not be inspected" warning
            // covers this case too, not just the ones where a module actually threw.
            var otherConfigLoadFailed = false;
            try
            {
                if (File.Exists(other.ConfigPath))
                {
                    otherInstance = ServerInstanceFactory.Load(other);
                }
                else
                {
                    otherConfigLoadFailed = true;
                }
            }
            catch
            {
                otherConfigLoadFailed = true;
            }

            // A broken module belonging to an unrelated OTHER server must not abort evaluation of
            // the server actually being diagnosed - TryResolveDeclaredPorts and BuildConfiguredPorts
            // already isolate that. But silently treating "couldn't inspect this other server" the
            // same as "this other server genuinely has no ports" would let a real conflict go
            // undetected without any trace of why - tracked separately so the final conflicts check
            // can say so honestly instead of falsely claiming a clean, fully-verified "no conflicts."
            var otherModule = otherDescriptor?.Module;
            var otherResolved = TryResolveDeclaredPorts(otherModule, otherInstance, out var otherError);
            var otherPorts = BuildConfiguredPorts(other, otherResolved, otherError, otherInstance, otherModule, out var otherInspectionIncomplete);

            // The same gap AddPortChecks already closed for the selected server's own aggregate
            // "Ports" message (hasInvalidDeclaredPort above) exists here too - BuildConfiguredPorts
            // only ever includes Resolved-status ports in otherPorts, so a required-but-invalid
            // declared port on this OTHER server is silently dropped from conflict comparison
            // entirely, with no trace that it was ever there. Left untracked, a real conflict on
            // exactly that missing port could go undetected while this server still counts as fully
            // "inspected" for the final Pass/Warning decision below.
            var otherHasInvalidDeclaredPort = otherResolved != null && otherResolved.Any(port => port.Status == ResolvedPortStatus.Invalid);
            if (otherConfigLoadFailed || otherInspectionIncomplete || otherHasInvalidDeclaredPort)
            {
                uninspectedServers.Add(other.Name);
            }

            foreach (var currentPort in currentPorts)
            {
                foreach (var otherPort in otherPorts.Where(candidate => PortsCouldConflict(candidate, currentPort)))
                {
                    conflicts.Add($"{FormatPort(currentPort)} conflicts with {other.Name} ({FormatPort(otherPort)})");
                }
            }
        }

        if (conflicts.Count > 0)
        {
            // A real conflict does not make an unrelated, separately-uninspectable third server any
            // less worth mentioning - dropping that note here just because a different problem was
            // also found would hide it just as effectively as never tracking it in the first place.
            var message = string.Join("; ", conflicts.Distinct(StringComparer.OrdinalIgnoreCase));
            if (uninspectedServers.Count > 0)
            {
                message += $" Additionally, {uninspectedServers.Count} server(s) could not be inspected: {string.Join(", ", uninspectedServers.Distinct(StringComparer.OrdinalIgnoreCase))}.";
            }

            checks.Add(Warning("Network", "Port conflicts", message));
        }
        else if (uninspectedServers.Count > 0)
        {
            checks.Add(Warning(
                "Network",
                "Port conflicts",
                $"No conflicts found among inspected servers; {uninspectedServers.Count} server(s) could not be inspected: {string.Join(", ", uninspectedServers.Distinct(StringComparer.OrdinalIgnoreCase))}."));
        }
        else
        {
            checks.Add(Pass("Network", "Port conflicts", "No other configured server uses any configured port."));
        }

        return new PortInspectionResult(
            currentPorts,
            IsComplete: !selectedInspectionIncomplete && !hasInvalidDeclaredPort);
    }

    // Tier 5.3's own distinction: AddPortChecks above only validates configured/declared port
    // *values* (do they parse, overlap, conflict with another server) - none of that proves the
    // server is actually bound to any of them right now. This is the first "is it really listening"
    // stage from that tier's own plan text ("process running -> port listening locally -> firewall
    // permits access -> ..."), local-only and with no external dependency - opt-in external
    // reachability is deliberately out of scope here and left for a later, separate piece.
    private void AddLocalPortListeningChecks(
        ICollection<ServerHealthCheck> checks,
        PortInspectionResult portInspection,
        ProcessCheckResult processCheck)
    {
        // Ports excluded from listener inspection still participate in declaration validation and
        // cross-server conflict detection. CheckLocalListener controls only this OS-listener stage.
        var configuredPorts = portInspection.Ports
            .Where(port => port.CheckLocalListener)
            .ToArray();
        if (configuredPorts.Length == 0)
        {
            // A module may deliberately model a logical/configuration port that the server process
            // does not bind directly. There is no local endpoint to inspect in that case.
            return;
        }

        var runningProcessIds = processCheck.ProcessIds;
        if (runningProcessIds.Count == 0)
        {
            // Reuses AddProcessChecks' own answer instead of asking ServerProcessLocator again -
            // and treats "not confirmed running" (which also covers the module/instance-missing and
            // internal-error cases, not just a genuinely stopped server) as equally unable to
            // support a meaningful listening check, matching AddQueryChecks/AddProcessChecks' own
            // "a stopped server isn't a health problem" Info-not-Fail treatment.
            checks.Add(Info("Network", "Listening", "Skipped because no matching process is confirmed running for this server."));
            return;
        }

        if (!TryInvokeModule(_activeListenersProvider, out var listeners) || listeners == null)
        {
            checks.Add(Warning("Network", "Listening", "Local listener state could not be enumerated due to an internal error."));
            return;
        }

        var tcpPorts = listeners.TcpListeners.Select(endpoint => endpoint.Port).ToHashSet();
        var udpPorts = listeners.UdpListeners.Select(endpoint => endpoint.Port).ToHashSet();
        var notListening = configuredPorts
            .Where(port => !IsPortRangeListening(port.Port, port.RangeSize, port.Protocol, tcpPorts, udpPorts))
            .ToArray();

        if (notListening.Length > 0)
        {
            checks.Add(Warning(
                "Network",
                "Listening",
                $"No matching local listener was found for the following configured ports even though a matching process is running: {string.Join(", ", notListening.Select(FormatPort))}." +
                (portInspection.IsComplete
                    ? string.Empty
                    : " The module's full port configuration was also unavailable, so this result is incomplete.")));
            return;
        }

        if (!portInspection.IsComplete)
        {
            checks.Add(Warning(
                "Network",
                "Listening",
                $"Local listeners were found for the ports that could be inspected, but the module's full port configuration was unavailable: {string.Join(", ", configuredPorts.Select(FormatPort))}."));
            return;
        }

        // Every confirmed-running process is a bare shell/launcher (cmd/powershell/pwsh) - the real
        // game/runtime process (a JVM, a downloaded server binary launched by that shell's script) is
        // an untracked descendant with its own, different pid. Matching ports against the launcher's
        // own pid would confidently report "not owned" for an otherwise perfectly healthy server, the
        // exact false positive this check exists to avoid - fall back to the same disclaimer used
        // when ownership data is unavailable for any other reason, rather than attempt a PID match
        // that's already known to compare against the wrong process.
        if (processCheck.MatchedOnlyLauncherProcesses)
        {
            checks.Add(Info(
                "Network",
                "Listening",
                $"Local listeners were found for every configured port: {string.Join(", ", configuredPorts.Select(FormatPort))}. " +
                "This server launches through a shell (cmd/PowerShell) that WindowsGSH cannot trace to the actual game process, so ownership could not be confirmed."));
            return;
        }

        // Ipv4-scoped presence sets: NativeConnectionTable's owner lookup is IPv4-only (documented on
        // the type itself), so a port whose only real listener is IPv6 can never be found in the
        // owner tables below - that must read as "ownership unknown," not "not owned," the same as a
        // failed lookup. Built from the full endpoint list (not the bare port-number sets used for
        // presence above), since only the endpoint carries address-family information.
        var tcpIpv4Ports = listeners.TcpListeners
            .Where(endpoint => endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        var udpIpv4Ports = listeners.UdpListeners
            .Where(endpoint => endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(endpoint => endpoint.Port)
            .ToHashSet();

        var tcpContext = new ProtocolOwnershipContext(tcpPorts, tcpIpv4Ports, BuildOwnersByPort(listeners.TcpOwners));
        var udpContext = new ProtocolOwnershipContext(udpPorts, udpIpv4Ports, BuildOwnersByPort(listeners.UdpOwners));

        var ownership = configuredPorts
            .Select(port => (
                Port: port,
                Status: GetPortRangeOwnershipStatus(port.Port, port.RangeSize, port.Protocol, tcpContext, udpContext, runningProcessIds)))
            .ToArray();
        var notOwned = ownership.Where(result => result.Status == PortOwnershipStatus.NotOwned).Select(result => result.Port).ToArray();

        if (notOwned.Length > 0)
        {
            checks.Add(Warning(
                "Network",
                "Listening",
                $"A local listener exists for every configured port, but the following are not confirmed as owned by this server's own process - another process on this machine may be using them: {string.Join(", ", notOwned.Select(FormatPort))}."));
            return;
        }

        // At least one port's ownership genuinely could not be determined (lookup failure, no
        // ownership data supplied at all, or an IPv4-only lookup facing an IPv6-only listener) -
        // falls back to the original, ownership-unconfirmed wording rather than claiming a full
        // confirmation that wasn't actually possible for every port.
        if (ownership.Any(result => result.Status == PortOwnershipStatus.Unknown))
        {
            checks.Add(Info(
                "Network",
                "Listening",
                $"Local listeners were found for every configured port: {string.Join(", ", configuredPorts.Select(FormatPort))}. " +
                "WindowsGSH cannot yet confirm that these endpoints are owned by the matching server process."));
            return;
        }

        checks.Add(Pass(
            "Network",
            "Listening",
            $"Local listeners for every configured port are confirmed owned by this server's own process: {string.Join(", ", configuredPorts.Select(FormatPort))}."));
    }

    // Null in, null out - a null owners list means the underlying lookup failed for that protocol,
    // and that must keep propagating as "no data" rather than collapsing into an empty-but-present
    // dictionary that GetPortRangeOwnershipStatus would otherwise read as "successfully checked, no
    // owner found here" (i.e. not owned) instead of "could not check this at all" (unknown).
    private static Dictionary<int, HashSet<int>>? BuildOwnersByPort(
        IReadOnlyList<NativeConnectionTable.OwnedEndpoint>? owners)
    {
        if (owners == null)
        {
            return null;
        }

        var byPort = new Dictionary<int, HashSet<int>>();
        foreach (var owner in owners)
        {
            if (!byPort.TryGetValue(owner.Endpoint.Port, out var pids))
            {
                pids = [];
                byPort[owner.Endpoint.Port] = pids;
            }

            pids.Add(owner.OwningProcessId);
        }

        return byPort;
    }

    internal enum PortOwnershipStatus
    {
        Owned,
        NotOwned,
        Unknown
    }

    // Bundles what GetPortRangeOwnershipStatus needs for one protocol: which ports have any presence
    // listener at all (ListenerPorts, address-blind - matches IsPortRangeListening's own input),
    // which of those are actually reachable by the IPv4-only owner lookup (Ipv4ListenerPorts), and
    // the owner-PID-by-port lookup itself (OwnersByPort, null when that protocol's lookup failed).
    internal readonly record struct ProtocolOwnershipContext(
        IReadOnlySet<int> ListenerPorts,
        IReadOnlySet<int> Ipv4ListenerPorts,
        IReadOnlyDictionary<int, HashSet<int>>? OwnersByPort);

    // Mirrors IsPortRangeListening's exact range/protocol semantics (every number in the range must
    // satisfy the declared transport) but additionally requires the satisfying listener(s) to be
    // owned by one of the server's own confirmed-running process ids - a stronger, PID-based
    // confirmation layered on top of the address-blind presence check. The presence check stays
    // address-blind while ownership is confirmed where connection-table data is available.
    // Returns NotOwned only when ownership was actually checked and definitively failed somewhere in
    // the range; a number whose only presence listener is unreachable by this IPv4-only lookup (IPv6-
    // only), or whose protocol's owner data is missing entirely, contributes Unknown instead - it
    // must never be reported as "confirmed not owned" just because it couldn't be inspected.
    internal static PortOwnershipStatus GetPortRangeOwnershipStatus(
        int startPort,
        int rangeSize,
        PortProtocol? protocol,
        ProtocolOwnershipContext tcp,
        ProtocolOwnershipContext udp,
        IReadOnlySet<int> candidatePids)
    {
        var sawUnknown = false;
        for (var number = startPort; number < startPort + rangeSize; number++)
        {
            var tcpOwned = GetOwnership(number, tcp, candidatePids);
            var udpOwned = GetOwnership(number, udp, candidatePids);
            var owned = protocol switch
            {
                PortProtocol.Tcp => tcpOwned,
                PortProtocol.Udp => udpOwned,
                PortProtocol.Both => CombineRequiringBoth(tcpOwned, udpOwned),
                PortProtocol.Either or null => CombineRequiringEither(tcpOwned, udpOwned),
                _ => (bool?)null
            };

            if (owned == false)
            {
                return PortOwnershipStatus.NotOwned;
            }

            if (owned == null)
            {
                sawUnknown = true;
            }
        }

        return sawUnknown ? PortOwnershipStatus.Unknown : PortOwnershipStatus.Owned;
    }

    // null means "not checkable for this protocol/number" - either this protocol's owner data is
    // missing entirely, or the only presence listener here is IPv6, which the IPv4-only owner tables
    // can never see. A number with no presence listener for this protocol at all is definitively
    // false (nothing to own), not unknown - that case matches what IsPortRangeListening itself would
    // already have rejected, so it's included here only for this method's own standalone testability.
    private static bool? GetOwnership(int number, ProtocolOwnershipContext context, IReadOnlySet<int> candidatePids)
    {
        if (!context.ListenerPorts.Contains(number))
        {
            return false;
        }

        if (context.OwnersByPort == null || !context.Ipv4ListenerPorts.Contains(number))
        {
            return null;
        }

        return context.OwnersByPort.TryGetValue(number, out var pids) && pids.Overlaps(candidatePids);
    }

    // Both requires ownership of both protocols. A definitive non-owner on either side fails the
    // whole requirement regardless of the other side's availability; otherwise, any side that
    // couldn't be checked makes the combined result unknown rather than a false confirmation.
    private static bool? CombineRequiringBoth(bool? tcpOwned, bool? udpOwned)
    {
        if (tcpOwned == false || udpOwned == false)
        {
            return false;
        }

        return tcpOwned == null || udpOwned == null ? null : true;
    }

    // Either is satisfied the moment one protocol is confirmed owned, regardless of whether the
    // other could be checked at all. Only unknown when neither side is confirmed owned and at least
    // one side couldn't be checked; definitively not owned only when both sides were checked and
    // neither owns it.
    private static bool? CombineRequiringEither(bool? tcpOwned, bool? udpOwned)
    {
        if (tcpOwned == true || udpOwned == true)
        {
            return true;
        }

        return tcpOwned == null || udpOwned == null ? null : false;
    }

    // RangeSize declares a consecutive block occupied by the server, so every number in the range
    // must have the required transport. Both requires TCP and UDP on every number. Either (used by
    // the generic wrapper when transport is unknown) and the legacy null protocol require at least
    // one of TCP or UDP on each number.
    // ServerDisplayInfo.IpAddress is display/connection metadata, not a reliable local bind-address
    // contract: some modules expose a proxy or public address, and GenericWrapperModule explicitly
    // does not rewrite the underlying server configuration. Until port declarations carry an
    // authoritative bind address, this check deliberately answers only whether a system-wide local
    // endpoint exists for the configured port/protocol. The user-facing result also states that
    // endpoint ownership by the matching server process is not yet available.
    internal static bool IsPortRangeListening(
        int startPort,
        int rangeSize,
        PortProtocol? protocol,
        IReadOnlySet<int> tcpPorts,
        IReadOnlySet<int> udpPorts)
    {
        for (var number = startPort; number < startPort + rangeSize; number++)
        {
            var matchesTcp = tcpPorts.Contains(number);
            var matchesUdp = udpPorts.Contains(number);
            var isListening = protocol switch
            {
                PortProtocol.Tcp => matchesTcp,
                PortProtocol.Udp => matchesUdp,
                PortProtocol.Both => matchesTcp && matchesUdp,
                PortProtocol.Either or null => matchesTcp || matchesUdp,
                _ => false
            };

            if (!isListening)
            {
                return false;
            }
        }

        return true;
    }

    // Shared, reusable version of the same isolation TryResolveDeclaredPorts already applies to
    // GetPorts()/Resolve() - a growing number of individual module-property/method call sites
    // across this file (module.Runtime, module.Capabilities, module.GetBackupTargets(), and more
    // as new checks are added) each needed the identical "arbitrary module code, never surface
    // ex.Message, don't let one bad module abort the rest of the report" treatment. Scattering a
    // bespoke try/catch at every call site is exactly how several of them were missed across
    // multiple review rounds; a single helper closes that gap for every future call site too, not
    // just the ones already known about.
    private static bool TryInvokeModule<T>(Func<T> moduleCall, out T? result)
    {
        try
        {
            result = moduleCall();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = default;
            return false;
        }
    }

    // module.GetPorts() calls into arbitrary compiled module code, and _portResolver is an
    // injectable dependency - either can throw for reasons entirely outside this service's
    // control. A crash from a single module/server must not cancel the rest of the evaluation
    // (Tier 5.2's own "one check failing doesn't abort the rest" requirement) - this is the one
    // place both GetPorts() and Resolve() are actually invoked, so every caller below goes through
    // it rather than calling either directly. Returns null with a non-null error on failure, null
    // with a null error when the module simply hasn't declared any ports (GetPorts() defaults to
    // an empty list), so callers can tell "nothing to resolve" apart from "resolution broke."
    private IReadOnlyList<ResolvedPort>? TryResolveDeclaredPorts(
        IGameServerModule? module,
        ServerInstance? instance,
        out string? error)
    {
        error = null;
        if (module == null || instance == null)
        {
            return null;
        }

        try
        {
            var declaredPorts = module.GetPorts();
            if (declaredPorts.Count == 0)
            {
                return null;
            }

            var resolved = _portResolver.Resolve(declaredPorts, instance.Settings);

            // _portResolver is an injectable dependency, not just the built-in ServerPortResolver -
            // a custom implementation returning null (instead of an empty list), a list containing
            // a null entry, or a well-formed-looking but internally inconsistent ResolvedPort (e.g.
            // Status: Resolved with Port: null - BuildConfiguredPorts's port.Port!.Value would throw
            // on exactly that shape) is exactly as untrusted as the module code this method already
            // guards against with the try/catch below. Treating every one of these shapes as a
            // resolution failure here means every caller downstream (AddDeclaredPortValidationChecks's
            // foreach, BuildConfiguredPorts's port.Port!.Value in particular) never has to defend
            // against a malformed result itself.
            //
            // None of the checks above validate the collection as a whole against what was actually
            // asked for - a resolver silently dropping one declared port's result (an empty list, or
            // one short), or substituting one declaration's metadata for another's while keeping
            // every id in place, passes every per-entry shape check and would otherwise be
            // indistinguishable from "this module simply hasn't declared any ports"/a fully
            // successful, faithful resolution. A missing required declared port then never gets a
            // "Port: <name>" Fail at all, the Display-port fallback can produce a misleading Ports:
            // Pass, and cross-server conflict detection silently never checks that port (or checks
            // it under the wrong protocol/required-ness) either. ResolvedPortsMatchDeclaredPorts
            // closes that by requiring the resolved ports to match the declared ports one-to-one,
            // full metadata included, not just ids.
            if (resolved == null ||
                resolved.Any(port => port == null || !IsWellFormedResolvedPort(port)) ||
                !ResolvedPortsMatchDeclaredPorts(declaredPorts, resolved))
            {
                error = "Port resolution returned an unexpected result.";
                return null;
            }

            return resolved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            return null;
        }
    }

    // A ResolvedPort's own type doesn't guarantee internal consistency - nothing stops a custom
    // IServerPortResolver from returning Status: Resolved with Port: null (the built-in
    // ServerPortResolver never does, but this is exactly the same "runtime can violate what the
    // compiler can't enforce" risk the null/null-entry checks above already treat as untrusted).
    // Mirrors ServerPortResolver.BuildResult's own bounds (1-65535, range not extending past
    // 65535) so a custom resolver can't slip a shape past this boundary that the built-in one
    // would never itself produce.
    private static bool IsWellFormedResolvedPort(ResolvedPort port)
    {
        // Protocol is checked unconditionally, not only when Status == Resolved - an undefined
        // PortProtocol value would otherwise sail past ProtocolsCouldConflict's own a == b/Both
        // comparisons as "distinct" from every real protocol, potentially hiding a genuine
        // same-port conflict against another server instead of being conservatively treated as
        // one.
        if (!Enum.IsDefined(port.Status) || !Enum.IsDefined(port.Protocol) || port.RangeSize < 1)
        {
            return false;
        }

        // ServerPortResolver.BuildResult never returns Unresolved for a Required port - a required
        // port with nothing to resolve is always Invalid there (it knows this is a real problem,
        // not merely "nothing to resolve yet"). A custom resolver reporting Required: true,
        // Status: Unresolved is exactly the same "runtime can violate what the built-in resolver's
        // own contract would never produce" risk every other check in this method already guards
        // against - left unchecked, a required-but-missing declared port produced no "Port: <name>"
        // Fail at all, and the aggregate "Ports" check could still Pass if a different declared
        // port happened to resolve.
        if (port.Required && port.Status == ResolvedPortStatus.Unresolved)
        {
            return false;
        }

        if (port.Status != ResolvedPortStatus.Resolved)
        {
            return true;
        }

        // long, not int, matching the Tier 5.1 review rounds' own fix for the identical class of
        // bug in ServerPortResolver.BuildResult - RangeSize has no upper bound, so a value like
        // int.MaxValue could wrap Port + RangeSize - 1 negative in int arithmetic, sliding straight
        // past the <= 65535 check instead of being caught by it.
        return port.Port is >= 1 and <= 65535 && (long)port.Port.Value + port.RangeSize - 1 <= 65535;
    }

    // IsWellFormedResolvedPort only ever inspects one ResolvedPort in isolation - it can't catch a
    // resolver that silently drops a declared port's result entirely (an empty list, or one short),
    // returns a duplicate result for the same id, returns a result for an id that was never
    // declared, or - the part an id-only comparison still missed - leaves every id in place while
    // silently substituting a declaration's other fields. BuildResult always passes Name, Protocol,
    // RangeSize, Required, OpenExternally, and CheckLocalListener through from the declaration unchanged (confirmed by
    // reading every ResolvedPort constructor call in ServerPortResolver.BuildResult/
    // BuildInvariantFailure), so a custom resolver changing any of them is exactly as untrusted as
    // one that changes ids: turning a Required port non-Required silently drops what should have
    // been its own "Port: <name>" Fail into nothing at all, and swapping Tcp for Udp (or vice
    // versa) can hide a real cross-server conflict from ProtocolsCouldConflict. Compared as a full
    // signature multiset (counted, not just a set) rather than positionally or by id alone, so a
    // module that legitimately declares a duplicate or blank id still has to get exactly that
    // duplicate/blank signature reflected back - matching the built-in ServerPortResolver's own
    // contract of exactly one ResolvedPort per declared ServerPortDefinition, unchanged in every
    // field but Status/Port/Error.
    private static bool ResolvedPortsMatchDeclaredPorts(IReadOnlyList<ServerPortDefinition> declaredPorts, IReadOnlyList<ResolvedPort> resolved)
    {
        var declaredSignatureCounts = CountBySignature(declaredPorts.Select(port =>
            Signature(port.Id, port.Name, port.Protocol, port.RangeSize, port.Required, port.OpenExternally, port.CheckLocalListener)));
        var resolvedSignatureCounts = CountBySignature(resolved.Select(port =>
            Signature(port.Id, port.Name, port.Protocol, port.RangeSize, port.Required, port.OpenExternally, port.CheckLocalListener)));
        return declaredSignatureCounts.Count == resolvedSignatureCounts.Count &&
            declaredSignatureCounts.All(pair => resolvedSignatureCounts.TryGetValue(pair.Key, out var count) && count == pair.Value);

        // A declared port's own Id/Name can themselves be null/blank (a malformed compiled C#
        // module - ServerPortResolver.ValidateOwnInvariants handles this on the resolver side, but
        // nothing stops it from reaching here) - both are normalized to "" rather than left null,
        // since Dictionary<TKey, TValue> throws on a null key and a null tuple element would
        // otherwise make an intentionally-blank Name indistinguishable from a genuinely-missing one
        // in the failure case. Id is uppercased (ordinal) so the comparison stays case-insensitive,
        // matching every other id comparison in this file/ServerPortResolver.cs; Name is compared
        // as-is (case-sensitive) since it's a display label, not a lookup key, and nothing else in
        // this codebase treats port names case-insensitively.
        static (string Id, string Name, PortProtocol Protocol, int RangeSize, bool Required, bool OpenExternally, bool CheckLocalListener) Signature(
            string? id, string? name, PortProtocol protocol, int rangeSize, bool required, bool openExternally, bool checkLocalListener) =>
            ((id ?? string.Empty).ToUpperInvariant(), name ?? string.Empty, protocol, rangeSize, required, openExternally, checkLocalListener);

        static Dictionary<(string Id, string Name, PortProtocol Protocol, int RangeSize, bool Required, bool OpenExternally, bool CheckLocalListener), int> CountBySignature(
            IEnumerable<(string Id, string Name, PortProtocol Protocol, int RangeSize, bool Required, bool OpenExternally, bool CheckLocalListener)> signatures) =>
            signatures.GroupBy(signature => signature).ToDictionary(group => group.Key, group => group.Count());
    }

    // Surfaces resolver-level problems (required-but-missing, invalid range, same-server overlap,
    // circular offset references) that the old config-field scan below had no way to see at all -
    // it only ever looked at whether a value parsed as a number in range, never at whether the
    // module's own port *declarations* made sense together. No-op for a module that hasn't
    // declared any ports yet. Takes an already-resolved result rather than resolving itself, so the
    // caller can resolve once and reuse it - see AddPortChecks's own comment for why resolving
    // twice was a real problem, not just an inefficiency.
    private static void AddDeclaredPortValidationChecks(
        ICollection<ServerHealthCheck> checks,
        IReadOnlyList<ResolvedPort>? resolved,
        string? resolutionError)
    {
        if (resolutionError != null)
        {
            // Deliberately never includes resolutionError (the raw exception message) - GetPorts()
            // and the injected IServerPortResolver are both arbitrary code that receives this
            // server's real settings, including passwords/RCON credentials/tokens, so a poorly
            // written or malicious implementation's exception message could embed one. This
            // check's Message ends up verbatim in BuildSupportSummary's output below, which users
            // copy into GitHub issues/Discord for support - exactly what this app's own secret-
            // redaction rules exist to prevent elsewhere, so a fixed, generic message is used here
            // regardless of what the real exception actually said.
            checks.Add(Fail("Network", "Port declarations", "This module's declared ports could not be evaluated due to an internal error."));
            return;
        }

        if (resolved == null)
        {
            return;
        }

        foreach (var port in resolved)
        {
            if (port.Status != ResolvedPortStatus.Invalid)
            {
                continue;
            }

            // Deliberately never uses port.Error (the resolver's own free-text explanation) -
            // _portResolver is an injectable dependency, exactly as untrusted as module code
            // elsewhere in this file, and ServerPortResolver.ResolveFromConfigField's own "invalid
            // value" message embeds the raw config field value verbatim - a user who accidentally
            // pasted a credential/token into a port field would have it echoed straight into this
            // check, and from there verbatim into BuildSupportSummary's output below. A fixed
            // message (port name only, never resolver-supplied text) closes that off regardless of
            // what any resolver implementation's Error text actually contains - but a single
            // wording for every cause loses real diagnostic value and steers users toward the
            // module author when the actual problem is usually their own configuration.
            // port.Required is a safe field, not free text, and the built-in ServerPortResolver's
            // own contract makes it meaningful: a required port is only ever Invalid when its value
            // is missing or could not be resolved (IsWellFormedResolvedPort above now rejects the
            // Required+Unresolved shape a broken resolver could otherwise hide behind), while an
            // optional port only reaches Invalid when a configured value was present but rejected -
            // a missing optional value resolves to Unresolved, never Invalid. Neither wording claims
            // more certainty than that: a required port could still be Invalid due to a broken
            // Fixed/Offset declaration rather than a missing setting, so both mention configuration
            // and declaration rather than blaming just one.
            var message = port.Required
                ? $"Port {port.Name} is required, but a valid value could not be resolved. Check this server's configuration and the module's port declaration for {port.Name}."
                : $"Port {port.Name}'s configured value is invalid. Check this server's configuration and the module's port declaration for {port.Name}.";
            checks.Add(port.Required
                ? Fail("Network", $"Port: {port.Name}", message)
                : Warning("Network", $"Port: {port.Name}", message));
        }
    }

    // Pure builder - takes an already-resolved result (or null-plus-error) rather than resolving
    // itself, so a caller evaluating several servers (the selected one, plus every other server
    // during cross-server conflict detection) can control exactly when TryResolveDeclaredPorts is
    // called, instead of this method silently calling it again for a server that was already
    // resolved moments earlier.
    private static IReadOnlyList<ConfiguredPort> BuildConfiguredPorts(
        InstalledServer server,
        IReadOnlyList<ResolvedPort>? resolved,
        string? resolutionError,
        ServerInstance? instance,
        IGameServerModule? module,
        out bool inspectionIncomplete)
    {
        // A null module (its descriptor missing/unresolvable - a different failure than a broken
        // config load, which the caller already tracks separately) means there is no schema to
        // read real ports from at all, regardless of whether resolutionError itself is set -
        // TryResolveDeclaredPorts returns null-with-no-error for a null module, since "nothing to
        // resolve" and "resolution broke" are different things, but from this method's point of
        // view a null module is just as much an incomplete inspection as either one. A null
        // instance (the server's own config missing or malformed) is the same story: there are no
        // real settings to resolve declared ports against or scan via the legacy config-field path,
        // so anything reported below can only ever be the crude cached Display port fallback.
        inspectionIncomplete = resolutionError != null || module == null || instance == null;
        var ports = new List<ConfiguredPort>();
        if (resolved != null)
        {
            ports.AddRange(resolved
                .Where(port => port.Status == ResolvedPortStatus.Resolved)
                .Select(port => new ConfiguredPort(
                    port.Name,
                    port.Port!.Value,
                    port.RangeSize,
                    port.Protocol,
                    port.CheckLocalListener)));
        }
        else if (resolutionError == null && instance != null && module != null)
        {
            // Only reached when the module genuinely has no declared ports (resolved is null with
            // no error) - if resolution itself threw, this deliberately does NOT fall back to the
            // config-field scan, since a module whose own port declarations are broken shouldn't
            // have that failure silently papered over by a different, possibly inconsistent code
            // path.
            //
            // module.GetConfigFields() is another call into arbitrary compiled module code - the
            // exact same exception-safety requirement TryResolveDeclaredPorts already applies to
            // GetPorts()/Resolve() applies here too, just for this legacy fallback path instead of
            // the new declared-ports one. Letting this throw uncaught would abort the entire
            // EvaluateAsync report for whichever server it happened on - including an unrelated
            // OTHER server during cross-server conflict detection, aborting diagnostics for the
            // server actually being evaluated.
            try
            {
                foreach (var field in module.GetConfigFields().Where(field => field.Type == ConfigFieldType.Port))
                {
                    if (instance.Settings.TryGetValue(field.Key, out var value) &&
                        int.TryParse(value?.ToString(), out var port) &&
                        port is >= 1 and <= 65535)
                    {
                        // No protocol information exists at the ConfigFieldType.Port level - left
                        // null (unknown), which PortsCouldConflict treats conservatively as "could
                        // conflict with anything," preserving this scan's original always-numeric-
                        // only behaviour for conflict purposes.
                        ports.Add(new ConfiguredPort(field.Label, port));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                inspectionIncomplete = true;
            }
        }

        // A range-sized declaration (e.g. UDP 27015-27018) already covers a display port that falls
        // anywhere inside it, not just an exact match on the range's start - comparing against Port
        // alone would add a redundant, protocol-unknown "Display port" duplicate for e.g. 27016,
        // which PortsCouldConflict/ProtocolsCouldConflict would then treat as conflicting with an
        // unrelated TCP-only port on another server, producing a false cross-server warning.
        var displayPortCoveredByDeclaration = int.TryParse(server.Port, out var declaredDisplayPort) &&
            resolved?.Any(port =>
                port.Status == ResolvedPortStatus.Resolved &&
                port.Port.HasValue &&
                declaredDisplayPort >= port.Port.Value &&
                declaredDisplayPort <= port.Port.Value + port.RangeSize - 1) == true;

        if (int.TryParse(server.Port, out var displayPort) &&
            displayPort is >= 1 and <= 65535 &&
            !displayPortCoveredByDeclaration &&
            ports.All(port => displayPort < port.Port || displayPort > port.Port + port.RangeSize - 1))
        {
            ports.Add(new ConfiguredPort("Display port", displayPort));
        }

        // Distinct by (port, protocol), not port alone - two genuinely different declarations that
        // happen to share a number but not a protocol (TCP 7777 and UDP 7777) are not duplicates
        // and must not silently drop one before conflict detection ever sees it.
        return ports
            .DistinctBy(port => (port.Port, port.Protocol))
            .OrderBy(port => port.Port)
            .ToArray();
    }

    // Two ports conflict only if their effective ranges overlap AND they share a protocol (or
    // either is Both or Either). Unknown protocol (the legacy config-field scan and the "Display port"
    // fallback carry none at all) is treated conservatively as potentially conflicting with
    // anything - erring toward a false-positive warning rather than silently missing a real
    // conflict for a module that hasn't adopted declared ports yet.
    private static bool PortsCouldConflict(ConfiguredPort a, ConfiguredPort b)
    {
        if (!ProtocolsCouldConflict(a.Protocol, b.Protocol))
        {
            return false;
        }

        var aEnd = a.Port + a.RangeSize - 1;
        var bEnd = b.Port + b.RangeSize - 1;
        return a.Port <= bEnd && b.Port <= aEnd;
    }

    private static bool ProtocolsCouldConflict(PortProtocol? a, PortProtocol? b)
    {
        if (a == null || b == null)
        {
            return true;
        }

        return a is PortProtocol.Both or PortProtocol.Either ||
               b is PortProtocol.Both or PortProtocol.Either ||
               a == b;
    }

    private static string FormatPort(ConfiguredPort port) =>
        port.RangeSize > 1
            ? $"{port.Label}={port.Port}-{port.Port + port.RangeSize - 1}"
            : $"{port.Label}={port.Port}";

    private static void AddFirewallChecks(
        ICollection<ServerHealthCheck> checks,
        IReadOnlyList<FirewallRuleStatus>? rules,
        string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            checks.Add(Warning("Firewall", "Rule status", error));
            return;
        }

        if (rules == null || rules.Count == 0)
        {
            checks.Add(Info("Firewall", "Rules", "No managed firewall rules are required or status was not supplied."));
            return;
        }

        var missing = rules.Where(rule => !rule.Exists).ToArray();
        checks.Add(missing.Length == 0
            ? Pass("Firewall", "Rules", $"All {rules.Count} managed firewall rules are present.")
            : Warning("Firewall", "Rules", $"{missing.Length} managed rule(s) are missing: {string.Join(", ", missing.Select(rule => rule.Label + " " + rule.Protocol))}."));
    }

    private static void AddQueryChecks(
        ICollection<ServerHealthCheck> checks,
        InstalledServer server,
        IGameServerModule? module)
    {
        if (module == null)
        {
            checks.Add(Info("Query", "Support", "This module does not declare query support."));
            return;
        }

        // module.Capabilities is a property getter - a manifest-backed module recomputes it from
        // the manifest on every access, and any module's own custom getter logic could throw.
        if (!TryInvokeModule(() => module.Capabilities, out var capabilities))
        {
            checks.Add(Fail("Query", "Support", "This module's capabilities could not be determined due to an internal error."));
            return;
        }

        if (capabilities?.SupportsQuery != true)
        {
            checks.Add(Info("Query", "Support", "This module does not declare query support."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(server.QueryDetailMessage))
        {
            checks.Add(Warning("Query", "Latest result", server.QueryDetailMessage));
        }
        else if (server.Status == ServerRuntimeStatus.Running)
        {
            checks.Add(Pass("Query", "Latest result", server.QueryMessage ?? "The latest server query succeeded."));
        }
        else
        {
            checks.Add(Info("Query", "Latest result", "Server is offline; query health is not currently available."));
        }
    }

    private static void AddBackupChecks(
        ICollection<ServerHealthCheck> checks,
        IGameServerModule? module,
        ServerInstance? instance)
    {
        if (module == null || instance == null)
        {
            checks.Add(Info("Backup", "Targets", "This module does not declare backup support."));
            return;
        }

        if (!TryInvokeModule(() => module.Capabilities, out var capabilities))
        {
            checks.Add(Fail("Backup", "Targets", "This module's capabilities could not be determined due to an internal error."));
            return;
        }

        if (capabilities?.SupportsBackups != true)
        {
            checks.Add(Info("Backup", "Targets", "This module does not declare backup support."));
            return;
        }

        // Materialized inside the guarded lambda, not just enumerated once out here - a
        // stateful/deferred GetBackupTargets() result could succeed on the .Any() null-check below
        // and still throw on a second enumeration, and an unguarded second enumeration (the Select
        // that used to follow this block) would then abort EvaluateAsync entirely instead of
        // producing the intended "could not be determined" failure. This mirrors the
        // GetConfigFields() guard above.
        if (!TryInvokeModule(() =>
            {
                var materialized = module.GetBackupTargets()?.ToArray();
                if (materialized == null || materialized.Any(target => target == null))
                {
                    throw new InvalidOperationException("Module returned malformed backup targets.");
                }

                return materialized;
            }, out var moduleTargets))
        {
            checks.Add(Fail("Backup", "Targets", "This module's backup targets could not be determined due to an internal error."));
            return;
        }

        // moduleTargets is a materialized array snapshot, non-null and entry-clean here - see the
        // guarded lambda above, which mirrors the GetConfigFields() guard's own reasoning.
        var targets = moduleTargets!
            .Select(target => (Label: target.Label, RelativePath: target.RelativePath, IsRequired: target.IsRequired))
            .Concat(instance.AppSettings.Backup.Paths.Select(path => (Label: "Custom", RelativePath: path, IsRequired: false)))
            .ToArray();
        if (targets.Length == 0)
        {
            checks.Add(Warning("Backup", "Targets", "Backup support is enabled but no targets are configured."));
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                if (Path.IsPathRooted(target.RelativePath) || target.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
                {
                    checks.Add(Fail("Backup", target.Label, $"Backup target must stay inside the install folder: {target.RelativePath}"));
                    continue;
                }

                var path = Path.GetFullPath(Path.Combine(instance.InstallPath, target.RelativePath));
                var exists = File.Exists(path) || Directory.Exists(path);
                checks.Add(exists
                    ? Pass("Backup", target.Label, $"Target exists: {path}")
                    : target.IsRequired
                        ? Fail("Backup", target.Label, $"Required target is missing: {path}")
                        : Warning("Backup", target.Label, $"Target is currently missing: {path}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // target.RelativePath ultimately came from module.GetBackupTargets() - a malformed
                // path (invalid characters, etc.) can make Path.Combine/Path.GetFullPath/Split
                // throw. One bad target must not abort the rest of the loop or the whole report;
                // target.Label is safe to use here (it's the module's own display string via the
                // normal data contract, not exception text).
                checks.Add(Fail("Backup", target.Label, "This backup target's path could not be evaluated due to an internal error."));
            }
        }
    }

    private void AddJavaChecks(
        ICollection<ServerHealthCheck> checks,
        IGameServerModule? module,
        ServerInstance? instance)
    {
        if (module == null || instance == null)
        {
            return;
        }

        if (!TryInvokeModule(() => module.Capabilities, out var capabilities))
        {
            checks.Add(Fail("Java", "Runtime", "This module's capabilities could not be determined due to an internal error."));
            return;
        }

        if (capabilities?.RequiresJava != true)
        {
            return;
        }

        var validation = _javaRuntimeManager.Validate(
            instance.AppSettings.Java,
            capabilities.MinimumJavaMajor ?? 1,
            _managedJavaStore);
        checks.Add(validation.IsValid
            ? Pass("Java", "Runtime", validation.Message)
            : Fail("Java", "Runtime", validation.Message));
    }

    // Mirrors AddJavaChecks: a check is only emitted at all when the module actually declares a
    // Steam-based install (module.GetSteamInstall() != null), matching how RequiresJava gates the
    // Java check above - a module that doesn't use SteamCMD shouldn't get Pass/Fail noise about it.
    // GetSteamInstall(), the path resolver, and the availability probe are all arbitrary/injected
    // code, so all three go through TryInvokeModule, same as every other module/dependency call in
    // this file.
    private void AddSteamCmdChecks(
        ICollection<ServerHealthCheck> checks,
        IGameServerModule? module,
        ServerInstance? instance)
    {
        if (module == null || instance == null)
        {
            return;
        }

        if (!TryInvokeModule(() => module.GetSteamInstall(), out var steamInstall))
        {
            checks.Add(Fail("SteamCMD", "Availability", "Could not determine whether this server requires SteamCMD due to an internal error."));
            return;
        }

        if (steamInstall == null)
        {
            return;
        }

        // Services.PersistentSteamClient.Select (the real ISteamCmdClient this app installs/
        // updates/verifies through) routes a definition with LoginAnonymous == false to
        // DepotDownloaderClient instead of SteamCmdManager - an authenticated Steam server never
        // touches steamcmd.exe at all, so a missing/untrusted steamcmd.exe is not this server's
        // problem and must not fail its health report. WindowsGSH.Core has no reference to the
        // WindowsGSH app project where PersistentSteamClient/DepotDownloaderToolManager live (the
        // same one-way dependency direction already noted for OperationHistoryRepository/
        // WindowsGSH.Data elsewhere in this file), so reporting the authenticated downloader's own
        // availability here isn't possible without new plumbing; gating on LoginAnonymous is the
        // minimal fix that stops the false failure without that.
        if (!steamInstall.LoginAnonymous)
        {
            return;
        }

        if (!TryInvokeModule(_steamCmdExePathResolver, out var exePath) || string.IsNullOrWhiteSpace(exePath))
        {
            checks.Add(Fail("SteamCMD", "Availability", "Could not determine the SteamCMD executable path due to an internal error."));
            return;
        }

        if (!TryInvokeModule(() => _steamCmdAvailabilityProbe(exePath), out var isAvailable))
        {
            checks.Add(Fail("SteamCMD", "Availability", "Could not verify SteamCMD's availability due to an internal error."));
            return;
        }

        if (isAvailable)
        {
            checks.Add(Pass("SteamCMD", "Availability", "SteamCMD is installed and available."));
            return;
        }

        // File.Exists here is purely to choose the more accurate message - the probe result above
        // (not this) is what actually decided Pass vs Fail, so this can't disagree with it.
        checks.Add(File.Exists(exePath)
            ? Fail(
                "SteamCMD",
                "Availability",
                $"The SteamCMD executable at '{exePath}' failed verification and will be reinstalled the next time it's needed. " +
                "If this persists, delete the 'steamcmd' folder and try installing or updating this server again.")
            : Fail(
                "SteamCMD",
                "Availability",
                $"SteamCMD was not found at '{exePath}'. This server's module requires SteamCMD to install or update files. " +
                "WindowsGSH normally downloads it automatically the first time it's needed - if this persists, check your " +
                "internet connection and try installing or updating this server again."));
    }

    private static void AddPublicIpChecks(
        ICollection<ServerHealthCheck> checks,
        ServerHealthRequest request)
    {
        if (!request.PublicIpTrackingEnabled)
        {
            checks.Add(Info("Network", "Public IP", "Public IP tracking is disabled."));
            return;
        }

        var value = request.LastKnownPublicIp;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out _))
        {
            checks.Add(Warning("Network", "Public IP", "Public IP tracking is enabled, but no valid address is known."));
            return;
        }

        var checkedText = request.LastPublicIpCheckedAt.HasValue
            ? $" Last checked {request.LastPublicIpCheckedAt.Value.LocalDateTime:g}."
            : string.Empty;
        checks.Add(Pass("Network", "Public IP", $"Last known public IP: {value}.{checkedText}"));
    }

    private static async Task AddModuleReadinessChecksAsync(
        ICollection<ServerHealthCheck> checks,
        IGameServerModule? module,
        ServerInstance? instance,
        CancellationToken cancellationToken)
    {
        if (module is not IModuleReadinessCapability readiness || instance == null)
        {
            return;
        }

        try
        {
            foreach (var result in await readiness.CheckReadinessAsync(instance, cancellationToken).ConfigureAwait(false))
            {
                checks.Add(new ServerHealthCheck(
                    "Module readiness",
                    result.Name,
                    result.Status switch
                    {
                        ReadinessStatus.Pass => ServerHealthSeverity.Pass,
                        ReadinessStatus.Info => ServerHealthSeverity.Info,
                        ReadinessStatus.Warning => ServerHealthSeverity.Warning,
                        _ => ServerHealthSeverity.Fail
                    },
                    result.Message));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // readiness.CheckReadinessAsync(instance, ...) is arbitrary compiled module code with
            // access to instance.Settings - never surface its raw exception message, the same as
            // every other arbitrary module call in this file. (Each individual ReadinessCheckResult
            // .Message inside the loop above is different: that's the module's own intentional,
            // designed user-facing text, the same category as any other module-authored check
            // message elsewhere in this app - not an accidental exception leak.)
            checks.Add(Fail("Module readiness", "Check", "This module's readiness checks could not be completed due to an internal error."));
        }
    }

    private static void RedactNode(JsonNode? node, IReadOnlySet<string> additionalSecretKeys)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToArray())
            {
                if (IsSecretLike(key) || additionalSecretKeys.Contains(key))
                {
                    obj[key] = "[REDACTED]";
                }
                else
                {
                    RedactNode(obj[key], additionalSecretKeys);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RedactNode(item, additionalSecretKeys);
            }
        }
    }

    private static bool IsSecretLike(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("webhook", StringComparison.OrdinalIgnoreCase);

    private static ServerHealthCheck Pass(string category, string name, string message) => new(category, name, ServerHealthSeverity.Pass, message);
    private static ServerHealthCheck Info(string category, string name, string message) => new(category, name, ServerHealthSeverity.Info, message);
    private static ServerHealthCheck Warning(string category, string name, string message) => new(category, name, ServerHealthSeverity.Warning, message);
    private static ServerHealthCheck Fail(string category, string name, string message) => new(category, name, ServerHealthSeverity.Fail, message);
    private sealed record ConfiguredPort(
        string Label,
        int Port,
        int RangeSize = 1,
        PortProtocol? Protocol = null,
        bool CheckLocalListener = true);
    private sealed record PortInspectionResult(IReadOnlyList<ConfiguredPort> Ports, bool IsComplete);
}
