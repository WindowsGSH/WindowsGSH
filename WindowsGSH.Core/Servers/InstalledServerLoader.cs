using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Steam;

namespace WindowsGSH.Core.Servers;

public sealed class InstalledServerLoader
{
    private readonly IServerMetricsService _metricsService;
    private readonly IServerStatusService _statusService;
    private readonly ServerStateCache _stateCache = ServerStateCache.Shared;
    private readonly object _moduleRegistryLock = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private ModuleRegistry _moduleRegistry = new();

    // Keyed by server folder path (known before a server's own id is resolved) - tracks a single
    // server's load that is still running past its own timeout. A timeout alone (racing
    // Task.WhenAny against Task.Delay) is not sufficient on its own: it lets THIS LoadAsync call move
    // on, but it cannot terminate the underlying Task.Run worker if the module call inside it is
    // genuinely, synchronously blocked forever - that worker, and the thread it occupies, would just
    // keep running. Without this dictionary, every subsequent LoadAsync call (the periodic refresh
    // timer, a manual refresh, a support-bundle export, etc. all call it) would start ANOTHER
    // Task.Run for the same stuck server, piling up permanently-blocked workers without bound across
    // a process meant to run for a month or more. Checking this dictionary before starting new work,
    // and clearing an entry only once its task is actually known to be done, ensures at most one
    // worker is ever in flight per server folder at a time.
    private readonly ConcurrentDictionary<string, Task<InstalledServer>> _pendingServerLoads = new();
    private readonly TimeSpan _serverLoadTimeout;

    // Marks a folder whose current pending load has ALREADY been observed to time out at least
    // once. Without this, every subsequent LoadAsync call for a KNOWN-hung server would re-wait
    // another full _serverLoadTimeout (20s in production) before returning the same "still in
    // progress" problem card - and since LoadAsync holds _loadLock for its entire duration, that
    // delay applies to every OTHER server in the same batch and every OTHER caller sharing this
    // loader (this app's own 3-second periodic refresh cadence chief among them), not just the one
    // hung server. Once a folder is marked here, a still-running pending task returns its problem
    // card immediately instead of being re-raced against the timeout again. Cleared whenever the
    // corresponding _pendingServerLoads entry is actually removed (see
    // RemovePendingLoadIfStillCurrent), so a genuinely fresh attempt for the same folder always
    // starts with a clean slate.
    private readonly ConcurrentDictionary<string, byte> _confirmedStuckFolders = new();

    // Caches a late-arriving SUCCESSFUL result for a load that had already timed out by the time it
    // finished. Without this, a server whose load consistently takes slightly longer than
    // _serverLoadTimeout would show a timeout card forever, even though every individual attempt
    // actually succeeds shortly afterward - the previous version just discarded the result once it
    // finally arrived and let the next LoadAsync call start the same slow work over again. Consumed
    // (removed) by the very next LoadAsync call that reaches a fresh attempt for that folder, so at
    // most one refresh cycle shows the recovered data before a genuinely new attempt is made again.
    private readonly ConcurrentDictionary<string, InstalledServer> _lateResults = new();

    // The actual per-server load work, factored out as a delegate so tests (via InternalsVisibleTo)
    // can substitute a controllable stand-in for a module hang, instead of needing a real compiled
    // module that blocks inside GetDisplayInfo/IsInstallValid and a real multi-second wait. No
    // production code path ever assigns anything other than TryLoadAsync itself.
    private readonly Func<string, IReadOnlyList<IGameServerModule>, CancellationToken, Task<InstalledServer>> _loadServerAsync;

    // Same idea as _loadServerAsync above, but for LoadForShutdown - a synchronous, whole-batch
    // method whose modules come from the real, non-test-injectable ModuleRegistry, so a test can't
    // otherwise substitute a controllable hanging module into it. No production code path ever
    // assigns anything other than the real LoadForShutdown method group.
    private readonly Func<IReadOnlyList<InstalledServer>> _loadForShutdown;

    public InstalledServerLoader()
        : this(new ServerStatusService(), new ServerMetricsService())
    {
    }

    public InstalledServerLoader(IServerStatusService statusService, IServerMetricsService? metricsService = null)
        : this(statusService, metricsService, null, null)
    {
    }

    // Test-only seam: WindowsGSH.Tests (via InternalsVisibleTo) can substitute a controlled
    // per-server load delegate and a short timeout to deterministically exercise the hang-protection
    // logic in LoadAsync, instead of inferring "a module is hung" from a real 20-second wait on a
    // real compiled module. No production code path reaches this constructor; the public ones above
    // always pass null for both, falling back to the real TryLoadAsync and a real 20-second timeout.
    internal InstalledServerLoader(
        IServerStatusService statusService,
        IServerMetricsService? metricsService,
        Func<string, IReadOnlyList<IGameServerModule>, CancellationToken, Task<InstalledServer>>? loadServerAsync,
        TimeSpan? serverLoadTimeout,
        Func<IReadOnlyList<InstalledServer>>? loadForShutdown = null)
    {
        _statusService = statusService;
        _metricsService = metricsService ?? new ServerMetricsService();
        _loadServerAsync = loadServerAsync ?? TryLoadAsync;
        _serverLoadTimeout = serverLoadTimeout ?? TimeSpan.FromSeconds(20);
        _loadForShutdown = loadForShutdown ?? LoadForShutdown;
    }

    public IServerMetricsService MetricsService => _metricsService;

    public void ReloadModules()
    {
        lock (_moduleRegistryLock)
        {
            ModuleRegistry.InvalidateCache();
            _moduleRegistry = new ModuleRegistry();
        }
    }

    public async Task<IReadOnlyList<InstalledServer>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var serversPath = AppPaths.GetPath("servers");
            if (!Directory.Exists(serversPath))
            {
                return [];
            }

            ModuleRegistry moduleRegistry;
            lock (_moduleRegistryLock)
            {
                moduleRegistry = _moduleRegistry;
            }
            var modules = await Task.Run(() => moduleRegistry.GetModules(), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // TryLoadAsync's own dominant cost, per server, is IServerStatusService.GetStatusAsync -
            // for a running, query-capable server this is a real network query bounded by a
            // multi-second timeout (see ServerStatusService's default _queryTimeout). Loading
            // servers one at a time meant that timeout was paid once per running server, in
            // sequence - a handful of running servers could add up to several seconds of startup
            // delay on their own. TryLoadAsync already catches every exception itself and returns a
            // problem-card InstalledServer rather than throwing (see its own catch block), so none
            // of these tasks can actually fault - Task.WhenAll here is purely about overlapping
            // their I/O wait time, not about handling per-server failures.
            //
            // LoadServerWithTimeoutAsync (rather than TryLoadAsync directly) additionally bounds a
            // single server's load and de-duplicates against a still-running attempt from an earlier
            // LoadAsync call - see its own comment and _pendingServerLoads above for why a plain
            // per-call timeout is not enough on its own. Every one of its returned tasks is itself
            // guaranteed to complete within _serverLoadTimeout, so this Task.WhenAll as a whole is
            // bounded too - one stuck server's module call can no longer hold _loadLock (and every
            // other LoadAsync caller waiting on it) indefinitely.
            var servers = await Task.WhenAll(
                Directory.EnumerateDirectories(serversPath)
                    .Select(serverFolder => LoadServerWithTimeoutAsync(serverFolder, modules, cancellationToken)));

            var result = servers
                .OrderBy(server => int.TryParse(server.Id, out var id) ? id : int.MaxValue)
                .ThenBy(server => server.Name)
                .ToArray();
            _stateCache.Update(result);
            return result;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<InstalledServer> LoadForShutdown()
    {
        var serversPath = AppPaths.GetPath("servers");
        if (!Directory.Exists(serversPath))
        {
            return [];
        }

        ModuleRegistry moduleRegistry;
        lock (_moduleRegistryLock)
        {
            moduleRegistry = _moduleRegistry;
        }

        var modules = moduleRegistry.GetModules();
        return Directory.EnumerateDirectories(serversPath)
            .Select(serverFolder => TryLoadForShutdown(serverFolder, modules))
            .Where(server => server != null)
            .Cast<InstalledServer>()
            .OrderBy(server => int.TryParse(server.Id, out var id) ? id : int.MaxValue)
            .ThenBy(server => server.Name)
            .ToArray();
    }

    // Bounds and deduplicates LoadForShutdown itself. LoadForShutdown deliberately avoids
    // GetDisplayInfo/IsInstallValid, but ServerProcessLocator.IsRunning still reads module.Runtime -
    // an arbitrary property getter - so a broken module can still hang it. This method exists because
    // its callers are all safety-critical shutdown/exit flows that would otherwise call
    // LoadForShutdown synchronously (often on the UI thread): an unbounded hang there would freeze
    // the whole app with no way to even cancel. At most one LoadForShutdown Task.Run is ever in
    // flight at a time (guarded by _shutdownLoadGate) - repeated close attempts while one is stuck
    // reuse that same in-flight attempt instead of starting another abandoned worker each time.
    // Returns null on timeout, fault, or an unrelated cancellation of the underlying work (never an
    // empty list, which would look like "confirmed zero servers") so the caller can apply its own
    // conservative fallback rather than concluding no servers are running.
    private readonly SemaphoreSlim _shutdownLoadGate = new(1, 1);
    private Task<IReadOnlyList<InstalledServer>>? _pendingShutdownLoad;

    public async Task<IReadOnlyList<InstalledServer>?> TryLoadForShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<InstalledServer>> loadTask;
        await _shutdownLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingShutdownLoad is { } existing)
            {
                loadTask = existing;
            }
            else
            {
                // This task is shared by every concurrent caller. Its lifetime therefore cannot
                // belong to whichever caller happened to create it first; each caller applies its
                // own cancellation token only to its WaitAsync below.
                loadTask = Task.Run(_loadForShutdown, CancellationToken.None);
                _pendingShutdownLoad = loadTask;
                // Exactly one observer per underlying Task.Run, attached here at creation time -
                // never inside the catch blocks below. Attaching a fresh observer on every timeout
                // (the previous approach) meant N repeated timeout calls against the same still-stuck
                // task accumulated N redundant continuations all awaiting the identical result. This
                // single observer clears the pending slot on ANY terminal outcome (success, fault, or
                // an unrelated cancellation), regardless of how many callers race/time out against it
                // in the meantime.
                _ = ObserveShutdownLoadAsync(loadTask);
            }
        }
        finally
        {
            _shutdownLoadGate.Release();
        }

        try
        {
            return await loadTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // WaitAsync's timeout is handled above; reaching here means loadTask itself completed
            // some other way - faulted, or canceled for a reason unrelated to this call's own
            // cancellationToken (e.g. Task.Run's token already canceled at schedule time). Without
            // this, that exception would propagate straight out of TryLoadForShutdownAsync to a
            // safety-critical shutdown/exit caller, AND (before this fix) leave _pendingShutdownLoad
            // holding a permanently terminal task that every subsequent call would just re-await and
            // rethrow forever. ObserveShutdownLoadAsync (attached once, above) already logs the
            // fault and clears the slot, so a future call starts a genuinely fresh attempt. From this
            // caller's perspective, "the underlying work didn't produce a trustworthy result" is the
            // same actionable outcome whether it's a timeout or a fault - both simply return null.
            return null;
        }
        // A genuine cancellationToken.IsCancellationRequested case falls through uncaught, propagating
        // as OperationCanceledException - this call was itself told to stop, so it should not silently
        // report null as if it were merely a timeout.
    }

    private async Task ClearPendingShutdownLoadIfCurrentAsync(Task<IReadOnlyList<InstalledServer>> task)
    {
        await _shutdownLoadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_pendingShutdownLoad, task))
            {
                _pendingShutdownLoad = null;
            }
        }
        finally
        {
            _shutdownLoadGate.Release();
        }
    }

    private async Task ObserveShutdownLoadAsync(Task<IReadOnlyList<InstalledServer>> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Same reasoning as ObserveLatePendingLoadAsync below: never surface a raw exception
            // anywhere a user or an exported artifact might see it.
            Debug.WriteLine($"Shutdown server enumeration failed: {ex}");
        }
        finally
        {
            await ClearPendingShutdownLoadIfCurrentAsync(task).ConfigureAwait(false);
        }
    }

    // Test-only accessor: lets tests directly confirm the pending-shutdown-load slot is clear (or
    // still holding a specific task) without depending on indirect, timing-sensitive signals like a
    // subsequent call's invocation count.
    internal Task<IReadOnlyList<InstalledServer>>? GetPendingShutdownLoadForTests() => _pendingShutdownLoad;

    public IReadOnlyList<InstalledServer> LoadInitialCards()
    {
        var serversPath = AppPaths.GetPath("servers");
        if (!Directory.Exists(serversPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(serversPath)
            .Select(TryLoadInitialCard)
            .OrderBy(server => int.TryParse(server.Id, out var id) ? id : int.MaxValue)
            .ThenBy(server => server.Name)
            .ToArray();
    }

    internal static InstalledServer TryLoadInitialCard(string serverFolder)
    {
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        if (!File.Exists(configPath))
        {
            return CreateProblemServer(
                serverFolder,
                configPath,
                "ServerConfig.json is missing. The install may not have completed.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var id = GetString(root, "id", Path.GetFileName(serverFolder));
            var name = GetString(root, "name", $"Server {id}");
            var moduleId = GetString(root, "moduleId", "unknown");
            var runtime = GetString(root, "runtime", "native");
            var relativeInstallPath = GetString(root, "installPath", "files");
            var installPath = Path.GetFullPath(Path.Combine(serverFolder, relativeInstallPath));

            return new InstalledServer(
                id,
                name,
                moduleId,
                runtime,
                serverFolder,
                installPath,
                configPath,
                "--",
                "--",
                "",
                "public",
                "--",
                "--",
                "--",
                "--",
                "--",
                "Loading",
                "--",
                false,
                "",
                null,
                Directory.Exists(installPath),
                ServerRuntimeStatus.Warning,
                "Loading status…",
                "WarnBrush",
                false,
                "",
                "",
                "",
                false,
                false,
                false,
                false);
        }
        catch (Exception ex)
        {
            return CreateProblemServer(
                serverFolder,
                configPath,
                $"ServerConfig.json could not be read: {ex.Message}");
        }
    }

    internal static InstalledServer? TryLoadForShutdown(
        string serverFolder,
        IReadOnlyList<IGameServerModule> modules)
    {
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var id = GetString(root, "id", Path.GetFileName(serverFolder));
            var name = GetString(root, "name", $"Server {id}");
            var moduleId = GetString(root, "moduleId", "unknown");
            var runtime = GetString(root, "runtime", "native");
            var relativeInstallPath = GetString(root, "installPath", "files");
            var installPath = Path.GetFullPath(Path.Combine(serverFolder, relativeInstallPath));
            var module = modules.FirstOrDefault(item =>
                string.Equals(item.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            var isRunning = module != null && ServerProcessLocator.IsRunning(module, installPath);

            return new InstalledServer(
                id,
                name,
                moduleId,
                runtime,
                serverFolder,
                installPath,
                configPath,
                "--",
                "--",
                "",
                "public",
                "--",
                "--",
                "--",
                "--",
                "--",
                isRunning ? "Running" : "Offline",
                "--",
                false,
                "",
                null,
                Directory.Exists(installPath),
                isRunning ? ServerRuntimeStatus.Running : ServerRuntimeStatus.Offline,
                isRunning ? "Running" : "Offline",
                isRunning ? "GoodBrush" : "BadBrush",
                false,
                "",
                "",
                "",
                isRunning,
                module != null,
                !isRunning && module != null,
                isRunning && module != null);
        }
        catch
        {
            return null;
        }
    }

    private async Task<InstalledServer> TryLoadAsync(
        string serverFolder,
        IReadOnlyList<IGameServerModule> modules,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(serverFolder, "ServerConfig.json");
        if (!File.Exists(configPath))
        {
            return CreateProblemServer(serverFolder, configPath, "ServerConfig.json is missing. The install may not have completed.");
        }

        try
        {
            var configJson = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;

            var id = GetString(root, "id", Path.GetFileName(serverFolder));
            var name = GetString(root, "name", $"Server {id}");
            var moduleId = GetString(root, "moduleId", "unknown");
            var module = modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            if (module != null)
            {
                var mutableConfig = JsonNode.Parse(configJson)?.AsObject();
                if (mutableConfig != null &&
                    new ServerSecretStore().ProtectConfigSecrets(module, serverFolder, mutableConfig))
                {
                    AtomicFile.WriteAllText(configPath, mutableConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    configJson = mutableConfig.ToJsonString();
                }
            }

            using var resolvedDocument = JsonDocument.Parse(configJson);
            root = resolvedDocument.RootElement;
            var runtime = GetString(root, "runtime", "native");
            var relativeInstallPath = GetString(root, "installPath", "files");
            var installPath = Path.GetFullPath(Path.Combine(serverFolder, relativeInstallPath));

            var settingsElement = root.TryGetProperty("settings", out var foundSettingsElement)
                ? foundSettingsElement
                : default;
            var moduleSettings = ServerModuleSettings.FromSettingsElement(settingsElement);
            var settings = new ServerSecretStore().ResolveSettings(serverFolder, moduleSettings.Values);
            var appSettings = ServerConfigAppSettings.FromRootElement(root);

            var steam = root.TryGetProperty("steam", out var steamElement)
                ? steamElement
                : default;

            var steamAppId = GetString(steam, "appId", "");
            var steamBranch = GetString(steam, "branch", "");
            var localBuildId = SteamAppManifestReader.ReadBuildId(installPath, steamAppId);
            var remoteBuildId = GetString(steam, "lastRemoteBuildId", "");
            var ignoredBuildId = GetString(steam, "ignoredBuildId", "");
            var hasUpdateAvailable = IsUpdateAvailable(localBuildId, remoteBuildId, ignoredBuildId);
            var instance = new ServerInstance(id, name, moduleId, serverFolder, installPath, configPath, settings, appSettings, new ServerModuleSettings(settings));
            var displayInfo = module?.GetDisplayInfo(instance) ?? new ServerDisplayInfo("0.0.0.0", "--", "--");
            var statusSnapshot = await _statusService.GetStatusAsync(module, instance, hasUpdateAvailable, cancellationToken);
            if (statusSnapshot is { ShouldLogMessage: true, LogMessage: not null })
            {
                ModuleLog.Add(statusSnapshot.LogMessage, instance.Id);
            }

            var queryResult = statusSnapshot.ToQueryResult();
            var metrics = _metricsService.Sample(module, instance, queryResult?.MaxPlayers ?? displayInfo.MaxPlayersNumber);
            var playerCount = statusSnapshot.OnlinePlayers.HasValue
                ? $"{statusSnapshot.OnlinePlayers.Value} / {statusSnapshot.MaxPlayers?.ToString() ?? displayInfo.MaxPlayers}"
                : metrics.PlayersText;
            var isInstalled = module?.IsInstallValid(instance) == true
                || Directory.Exists(installPath);

            return new InstalledServer(
                id,
                name,
                moduleId,
                runtime,
                serverFolder,
                installPath,
                configPath,
                displayInfo.IpAddress,
                displayInfo.Port,
                steamAppId,
                string.IsNullOrWhiteSpace(steamBranch) ? "public" : steamBranch,
                displayInfo.MaxPlayers,
                metrics.ProcessIdText,
                metrics.CpuText,
                metrics.MemoryText,
                playerCount,
                statusSnapshot.CurrentStatusText,
                metrics.UptimeText,
                false,
                "",
                null,
                isInstalled,
                statusSnapshot.Status,
                hasUpdateAvailable
                    ? $"Update available: local {FormatBuildId(localBuildId)}, remote {FormatBuildId(remoteBuildId)}"
                    : statusSnapshot.StatusText,
                statusSnapshot.StatusBrushKey,
                hasUpdateAvailable,
                localBuildId,
                remoteBuildId,
                ignoredBuildId,
                statusSnapshot.IsProcessRunning,
                module != null,
                !statusSnapshot.IsProcessRunning && isInstalled && module != null,
                statusSnapshot.IsProcessRunning && module != null,
                statusSnapshot.Map,
                statusSnapshot.Game,
                statusSnapshot.Version,
                statusSnapshot.Protocol,
                statusSnapshot.QueryDurationMilliseconds,
                statusSnapshot.Players,
                statusSnapshot.Message,
                statusSnapshot.DetailMessage,
                SupportsVerify: module != null && ModuleDescriptor.GetEffectiveCapabilities(module).SupportsVerify,
                AllowsOnlineVerification: module is IModuleVerifyCapability onlineCheck && onlineCheck.AllowsOnlineVerification);
        }
        catch (Exception ex)
        {
            return CreateProblemServer(serverFolder, configPath, $"ServerConfig.json could not be read: {ex.Message}");
        }
    }

    // Bounds a single server's load to _serverLoadTimeout AND ensures at most one worker is ever in
    // flight for a given server folder at a time, across repeated LoadAsync calls. A load that times
    // out here degrades to a problem card for THIS round - releasing _loadLock and letting every
    // other server/caller continue - but its underlying task is left tracked in _pendingServerLoads
    // rather than abandoned outright, so a later LoadAsync call for the same folder reuses/awaits it
    // instead of starting a duplicate worker on top of it.
    private async Task<InstalledServer> LoadServerWithTimeoutAsync(
        string serverFolder,
        IReadOnlyList<IGameServerModule> modules,
        CancellationToken cancellationToken)
    {
        if (_pendingServerLoads.TryGetValue(serverFolder, out var pendingTask))
        {
            if (!pendingTask.IsCompleted && _confirmedStuckFolders.ContainsKey(serverFolder))
            {
                // Already confirmed hung by an earlier timeout on this exact task - don't make this
                // call (and, since LoadAsync holds _loadLock for its entire duration, every other
                // caller queued behind it) wait out another full timeout window for a task that's
                // already known to be stuck. A task that HAS completed by now still falls through to
                // the normal await below instead of this branch, which resolves immediately and picks
                // up the real result rather than a stale "still stuck" card.
                return CreateProblemServer(
                    serverFolder,
                    Path.Combine(serverFolder, "ServerConfig.json"),
                    "This server's load is still in progress from an earlier attempt; it will be retried automatically once that attempt finishes.");
            }

            try
            {
                // WaitAsync(timeout, cancellationToken) - not a manual Task.WhenAny(task,
                // Task.Delay(timeout)) - so a genuine caller cancellation is distinguishable from a
                // timeout (OperationCanceledException vs TimeoutException) and isn't stuck waiting out
                // the full timeout window first: Task.Delay on its own doesn't observe cancellationToken,
                // so if the caller of LoadAsync cancels while a pending module call ignores its own
                // token, a plain WhenAny/Delay race would still wait up to _serverLoadTimeout before
                // noticing. Only TimeoutException is caught below; every other outcome (this call's own
                // cancellation, or the awaited task itself completing some other way) is handled by
                // HandleNonTimeoutExit.
                var result = await pendingTask.WaitAsync(_serverLoadTimeout, cancellationToken).ConfigureAwait(false);
                RemovePendingLoadIfStillCurrent(serverFolder, pendingTask);
                return result;
            }
            catch (TimeoutException)
            {
                _confirmedStuckFolders[serverFolder] = 1;
                return CreateProblemServer(
                    serverFolder,
                    Path.Combine(serverFolder, "ServerConfig.json"),
                    "This server's load is still in progress from an earlier attempt; it will be retried automatically once that attempt finishes.");
            }
            catch (Exception ex)
            {
                return HandleNonTimeoutExit(serverFolder, pendingTask, cancellationToken, ex);
            }
        }

        // A late-arriving successful result from a previous attempt that timed out but eventually
        // succeeded - use it directly instead of starting the same (apparently just-slow-but-working)
        // load all over again, which would very likely just time out again for a server whose load is
        // consistently slightly slower than _serverLoadTimeout.
        if (_lateResults.TryRemove(serverFolder, out var lateResult))
        {
            return lateResult;
        }

        // Task.Run schedules the whole per-server load, synchronous module-call prefix included
        // (module.GetDisplayInfo/IsInstallValid inside TryLoadAsync, called directly rather than
        // awaited), on an independently running task immediately. Calling _loadServerAsync directly
        // here would instead run synchronously on this thread up until its first genuine await point -
        // a module hang before that point would mean this method itself never returns a task to race
        // in the first place.
        //
        // Deliberately CancellationToken.None here, NOT this call's own cancellationToken: this task is
        // shared via _pendingServerLoads - any later LoadAsync call for the same folder (from a
        // completely different caller, possibly with its own unrelated cancellation source, e.g. a
        // ServerInfoWindow health-refresh token) can reuse it while it's still in flight. If the very
        // FIRST caller's token were baked into the shared work and that caller later canceled it (their
        // window closing, their own refresh being superseded, etc.), every OTHER caller currently
        // awaiting this same task would see that unrelated cancellation surface as their own result -
        // reaching HandleNonTimeoutExit's "not this call's own cancellation" branch and returning a
        // spurious "internal error" problem card, even though that other caller's own request was never
        // canceled. Each caller's own token is still applied to ITS OWN wait below
        // (pendingTask.WaitAsync(_serverLoadTimeout, cancellationToken)), which is the only place an
        // individual caller's cancellation should be able to take effect.
        var loadTask = Task.Run(() => _loadServerAsync(serverFolder, modules, CancellationToken.None), CancellationToken.None);
        _pendingServerLoads[serverFolder] = loadTask;

        try
        {
            var result = await loadTask.WaitAsync(_serverLoadTimeout, cancellationToken).ConfigureAwait(false);
            // Completed within the timeout window - no longer "in flight," regardless of whether
            // awaiting its result above already threw (a fault surfaces via this same WaitAsync call).
            RemovePendingLoadIfStillCurrent(serverFolder, loadTask);
            return result;
        }
        catch (TimeoutException)
        {
            // Marks this folder as confirmed-stuck so a subsequent LoadAsync call's "already
            // pending" check above returns immediately instead of re-waiting another full timeout
            // window for the same known-hung task.
            _confirmedStuckFolders[serverFolder] = 1;
            // WaitAsync's timeout does not cancel or abandon loadTask - it may still complete (or
            // fault) later. Observe it so a late fault becomes a diagnostic log entry instead of an
            // unobserved task exception, a late SUCCESS is cached rather than discarded, and so the
            // entry above is eventually cleared once it actually finishes (letting a future
            // LoadAsync call retry this server); deliberately not awaited here, since the whole point
            // is to stop waiting on it now.
            _ = ObserveLatePendingLoadAsync(serverFolder, loadTask);
            return CreateProblemServer(
                serverFolder,
                Path.Combine(serverFolder, "ServerConfig.json"),
                $"This server's load timed out after {_serverLoadTimeout.TotalSeconds:0}s; it will be retried automatically.");
        }
        catch (Exception ex)
        {
            return HandleNonTimeoutExit(serverFolder, loadTask, cancellationToken, ex);
        }
    }

    // Handles every way WaitAsync can exit other than a clean timeout: this call's own
    // cancellationToken firing, or the awaited task itself completing some other way (canceled or
    // faulted independently of this call - e.g. Task.Run's token already canceled at schedule time,
    // or the load delegate itself throwing instead of returning a problem card). Without this, either
    // exit left the dictionary entry untouched and no observer attached - a task that finished (however
    // it finished) stayed tracked forever with nobody ever noticing, and a task still genuinely running
    // when only the caller gave up had nobody watching it either. Both cases meant a future LoadAsync
    // call could find and re-await the exact same stale entry - either re-throwing a terminal task's
    // original exception forever, or re-waiting a hung task under a fresh timeout forever, neither of
    // which is what "retried automatically" is supposed to mean.
    private InstalledServer HandleNonTimeoutExit(
        string serverFolder,
        Task<InstalledServer> task,
        CancellationToken cancellationToken,
        Exception ex)
    {
        if (task.IsCompleted)
        {
            // The task itself reached a terminal state (canceled or faulted - a successful WaitAsync
            // wouldn't have thrown) - no longer "in flight" regardless of why this call stopped
            // waiting for it.
            RemovePendingLoadIfStillCurrent(serverFolder, task);
        }
        else
        {
            // Still genuinely running - only this call gave up (its own token fired). Make sure it's
            // still being watched so a future LoadAsync call can retry once it finishes rather than
            // finding a permanently orphaned entry; redundant with an already-attached observer is
            // harmless, since RemovePendingLoadIfStillCurrent is an idempotent compare-and-remove.
            _ = ObserveLatePendingLoadAsync(serverFolder, task);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation of this specific call - preserve and rethrow, matching how
            // cancellation is already handled everywhere else in this method (_loadLock.WaitAsync,
            // the ThrowIfCancellationRequested check before the per-server loop).
            ExceptionDispatchInfo.Capture(ex).Throw();
            throw ex; // unreachable - Throw() always throws; satisfies the compiler's flow analysis.
        }

        // Not this call's own cancellation - the underlying task faulted or was canceled for an
        // unrelated reason. TryLoadAsync's own catch normally prevents this in production, but must
        // not be trusted to guarantee it - one server's unexpected failure must never abort the whole
        // LoadAsync batch (Task.WhenAll would otherwise fail every other server's result too).
        Debug.WriteLine($"Server load for '{serverFolder}' failed unexpectedly: {ex}");
        return CreateProblemServer(
            serverFolder,
            Path.Combine(serverFolder, "ServerConfig.json"),
            "This server's load failed due to an internal error; it will be retried automatically.");
    }

    private async Task ObserveLatePendingLoadAsync(string serverFolder, Task<InstalledServer> loadTask)
    {
        InstalledServer? healthyLateResult = null;
        try
        {
            var result = await loadTask.ConfigureAwait(false);
            // TryLoadAsync catches its own exceptions (a cancelled status query, among others) and
            // returns a problem card as an ordinary, successful Task result rather than throwing - so
            // "the task completed without faulting" does NOT mean "this is a genuinely healthy
            // result." Caching a problem card here would let it get replayed as if it were a late
            // success on the very next LoadAsync call, silently skipping a real retry. Only a result
            // with no LastOperationError is a load that actually reached its normal, successful return
            // path - only that is worth preserving so the next fresh attempt for this folder can use
            // it immediately instead of starting the same (apparently just-slow-but-working) load all
            // over again and very likely hitting the same timeout a second time.
            if (result.LastOperationError == null)
            {
                healthyLateResult = result;
            }
        }
        catch (Exception ex)
        {
            // loadTask ultimately runs TryLoadAsync (or, in tests, a substituted delegate) - its own
            // catch already turns ordinary failures into a problem card rather than throwing, so a
            // fault reaching here is already an unexpected, internal condition; use a generic message
            // for anything logged, and keep the real exception in a non-exported diagnostic channel
            // (matching the same posture established for the support-bundle export's own timeout
            // handling), rather than surfacing raw exception text anywhere a user or an exported
            // artifact might see it.
            Debug.WriteLine($"Server load for '{serverFolder}', abandoned earlier after timing out, later failed: {ex}");
        }
        finally
        {
            await TryCacheLateResultAsync(serverFolder, loadTask, healthyLateResult).ConfigureAwait(false);
        }
    }

    // LoadServerWithTimeoutAsync always runs while LoadAsync owns _loadLock. Taking that same lock
    // here makes "confirm ownership -> retire pending task -> publish late result" one atomic sequence
    // relative to every possible new load attempt. Merely doing a conditional dictionary removal
    // followed by a separate _lateResults write leaves a gap where a newer task can start between
    // those operations and then be shadowed by the stale result.
    internal async Task<bool> TryCacheLateResultAsync(
        string serverFolder,
        Task<InstalledServer> task,
        InstalledServer? healthyResult)
    {
        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!RemovePendingLoadIfStillCurrent(serverFolder, task) || healthyResult == null)
            {
                return false;
            }

            _lateResults[serverFolder] = healthyResult;
            return true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    // Atomic compare-and-remove: only removes the dictionary entry for serverFolder if it still
    // holds EXACTLY this task instance, returning whether it did. A plain TryRemove(key, out _) removes
    // whatever is currently stored under that key regardless of which task it is - a real race the
    // earlier version had: an old, timed-out task's late observer (or a concurrent LoadAsync call
    // racing the same pending entry) could remove a NEWER task's entry if a fresh attempt got inserted
    // for the same folder in between, letting yet another LoadAsync call start a genuinely duplicate
    // worker on top of it. The bool return additionally lets ObserveLatePendingLoadAsync above gate its
    // _lateResults cache write on "did I actually still own this slot," closing a related race where a
    // stale result could otherwise be cached after being superseded.
    // Internal (not private) so WindowsGSH.Tests (via InternalsVisibleTo) can exercise this exact
    // compare-and-remove race directly and deterministically - forcing the real timing through the
    // full LoadAsync/ObserveLatePendingLoadAsync background-task flow isn't reliably schedulable from
    // a test, the same reasoning DiscordPanelService's ComputeEmbedFingerprint/DecidePanelRefresh
    // etc. were extracted as internal for.
    internal bool RemovePendingLoadIfStillCurrent(string serverFolder, Task<InstalledServer> task)
    {
        var removed = ((ICollection<KeyValuePair<string, Task<InstalledServer>>>)_pendingServerLoads)
            .Remove(new KeyValuePair<string, Task<InstalledServer>>(serverFolder, task));
        if (removed)
        {
            // The task this stuck-flag was tracking is now retired - a future attempt for this
            // folder starts a genuinely new task and deserves its own, fresh timeout window rather
            // than being immediately short-circuited by a flag left over from this one. Only cleared
            // when the removal above actually succeeded (i.e. this really was the task the flag, if
            // any, was tracking) - a stale/losing removal attempt for an already-replaced task must
            // not clear a flag that may now belong to a newer task under the same folder key.
            _confirmedStuckFolders.TryRemove(serverFolder, out _);
        }

        return removed;
    }

    // Test-only seams (WindowsGSH.Tests, via InternalsVisibleTo) for directly populating/inspecting
    // the pending-load dictionary - lets a test set up the exact "a newer task was inserted before an
    // older one's stale removal fires" race precisely, without depending on real background-task
    // scheduling timing. No production code path calls either of these.
    internal void SetPendingServerLoadForTests(string serverFolder, Task<InstalledServer> task) =>
        _pendingServerLoads[serverFolder] = task;

    internal bool TryGetPendingServerLoadForTests(string serverFolder, out Task<InstalledServer>? task) =>
        _pendingServerLoads.TryGetValue(serverFolder, out task);

    private static InstalledServer CreateProblemServer(string serverFolder, string configPath, string issue)
    {
        var id = Path.GetFileName(serverFolder);
        var installPath = Path.Combine(serverFolder, "files");
        return new InstalledServer(
            id,
            $"Server {id}",
            "unknown",
            "unknown",
            serverFolder,
            installPath,
            configPath,
            "--",
            "--",
            "",
            "public",
            "--",
            "--",
            "--",
            "--",
            "--",
            "Needs attention",
            "--",
            false,
            "",
            issue,
            Directory.Exists(installPath),
            ServerRuntimeStatus.Warning,
            "Setup issue",
            "WarnBrush",
            false,
            "",
            "",
            "",
            false,
            false,
            false,
            false);
    }

    private static string GetString(JsonElement element, string propertyName, string fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    private static bool IsUpdateAvailable(string localBuildId, string remoteBuildId, string ignoredBuildId)
    {
        return !string.IsNullOrWhiteSpace(localBuildId) &&
            !string.IsNullOrWhiteSpace(remoteBuildId) &&
            !string.Equals(localBuildId, remoteBuildId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(remoteBuildId, ignoredBuildId, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBuildId(string buildId)
    {
        return string.IsNullOrWhiteSpace(buildId) ? "unknown" : buildId;
    }
}
