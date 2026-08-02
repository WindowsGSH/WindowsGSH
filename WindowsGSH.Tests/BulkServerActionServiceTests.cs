using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class BulkServerActionServiceTests
{
    private readonly BulkServerActionPlanner _planner = new();

    [Fact]
    public void Start_selected_plans_only_selected_startable_idle_servers()
    {
        var plan = _planner.Plan(
            BulkServerAction.StartSelected,
            [
                Candidate("ready", selected: true, canStart: true),
                Candidate("running", selected: true, canStop: true),
                Candidate("busy", selected: true, canStart: true, busy: true),
                Candidate("not-selected", selected: false, canStart: true)
            ]);

        Assert.Equal(["ready"], plan.Executable.Select(item => item.ServerId));
        Assert.Contains(plan.Skipped, item => item.ServerId == "running" && item.Reason.Contains("already running"));
        Assert.Contains(plan.Skipped, item => item.ServerId == "busy" && item.Reason.Contains("operation"));
        Assert.Contains(plan.Skipped, item => item.ServerId == "not-selected" && item.Reason == "Not selected.");
    }

    [Fact]
    public void Start_auto_start_uses_setting_instead_of_selection()
    {
        var plan = _planner.Plan(
            BulkServerAction.StartAutoStart,
            [
                Candidate("auto", selected: false, canStart: true, autoStart: true),
                Candidate("manual", selected: true, canStart: true, autoStart: false)
            ]);

        Assert.Equal(["auto"], plan.Executable.Select(item => item.ServerId));
        Assert.Contains(plan.Skipped, item => item.ServerId == "manual" && item.Reason.Contains("Auto Start"));
    }

    [Fact]
    public void Update_selected_requires_offline_startable_server()
    {
        var plan = _planner.Plan(
            BulkServerAction.UpdateSelectedOffline,
            [
                Candidate("offline", selected: true, canStart: true),
                Candidate("online", selected: true, canStop: true),
                Candidate("broken", selected: true),
                Candidate("unsupported", selected: true, canStart: true, supportsUpdate: false)
            ]);

        Assert.Equal(["offline"], plan.Executable.Select(item => item.ServerId));
        Assert.Contains(plan.Skipped, item => item.ServerId == "online" && item.Reason.Contains("Stop"));
        Assert.Contains(plan.Skipped, item => item.ServerId == "broken" && item.Reason.Contains("cannot"));
        Assert.Contains(plan.Skipped, item => item.ServerId == "unsupported" && item.Reason.Contains("does not support"));
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Backup_selected_requires_backup_capability_and_offline_state()
    {
        var plan = _planner.Plan(
            BulkServerAction.BackupSelected,
            [
                Candidate("ready", selected: true, canStart: true, supportsBackups: true),
                Candidate("online", selected: true, canStop: true, supportsBackups: true),
                Candidate("unsupported", selected: true, canStart: true)
            ]);

        Assert.Equal(["ready"], plan.Executable.Select(item => item.ServerId));
        Assert.Contains(plan.Skipped, item => item.ServerId == "online" && item.Reason.Contains("Stop"));
        Assert.Contains(plan.Skipped, item => item.ServerId == "unsupported" && item.Reason.Contains("does not support"));
    }

    [Fact]
    public async Task Executor_limits_concurrency_and_preserves_skipped_results()
    {
        var plan = _planner.Plan(
            BulkServerAction.StartSelected,
            Enumerable.Range(1, 6)
                .Select(index => Candidate(
                    index.ToString(),
                    selected: index != 6,
                    canStart: true))
                .ToArray());
        var active = 0;
        var maximumActive = 0;
        var executor = new BulkServerActionExecutor();

        var summary = await executor.ExecuteAsync(
            plan,
            maximumConcurrency: 2,
            async (item, token) =>
            {
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                await Task.Delay(25, token);
                Interlocked.Decrement(ref active);
                return new BulkServerActionResult(
                    item.ServerId,
                    item.ServerName,
                    BulkServerActionResultStatus.Succeeded,
                    "Done.");
            });

        Assert.Equal(2, maximumActive);
        Assert.Equal(5, summary.Succeeded);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public async Task Executor_cancels_items_waiting_for_the_concurrency_gate()
    {
        var plan = _planner.Plan(
            BulkServerAction.StartSelected,
            [
                Candidate("first", selected: true, canStart: true),
                Candidate("second", selected: true, canStart: true)
            ]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var executor = new BulkServerActionExecutor();

        var execution = executor.ExecuteAsync(
            plan,
            maximumConcurrency: 1,
            async (item, token) =>
            {
                if (item.ServerId == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return new BulkServerActionResult(
                    item.ServerId,
                    item.ServerName,
                    BulkServerActionResultStatus.Succeeded,
                    "Done.");
            },
            cancellationToken: cancellation.Token);

        await firstStarted.Task;
        cancellation.Cancel();
        releaseFirst.SetResult();
        var summary = await execution;

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Cancelled);
    }

    [Fact]
    public async Task Target_loader_rechecks_cancellation_after_slow_load()
    {
        using var cancellation = new CancellationTokenSource();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var loading = BulkServerActionTargetLoader.LoadAsync<Target>(
            "server-1",
            async _ =>
            {
                loadStarted.SetResult();
                await releaseLoad.Task;
                return new[] { new Target("server-1") };
            },
            target => target.Id,
            cancellation.Token);

        await loadStarted.Task;
        cancellation.Cancel();
        releaseLoad.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => loading);
    }

    [Fact]
    public void Effective_capabilities_include_native_update_interface()
    {
        var capabilities = ModuleDescriptor.GetEffectiveCapabilities(new NativeUpdateModule());

        Assert.True(capabilities.SupportsUpdate);
    }

    private static BulkServerActionCandidate Candidate(
        string id,
        bool selected,
        bool canStart = false,
        bool canStop = false,
        bool busy = false,
        bool autoStart = false,
        bool supportsUpdate = true,
        bool supportsBackups = false)
    {
        return new BulkServerActionCandidate(
            id,
            $"Server {id}",
            selected,
            ConfigExists: true,
            canStart,
            canStop,
            busy,
            autoStart,
            supportsUpdate,
            supportsBackups);
    }

    private sealed record Target(string Id);

    private sealed class NativeUpdateModule : IGameServerModule, IModuleUpdateCapability
    {
        public string Id => "native-update";
        public string Name => "Native Update";
        public string Version => "1.0";
        public ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", []);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<System.Diagnostics.Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<System.Diagnostics.Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => Name;
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<System.Diagnostics.ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<System.Diagnostics.Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ModuleUpdateResult> UpdateAsync(ServerInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new ModuleUpdateResult(true, "Updated."));
    }
}
