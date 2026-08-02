using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerListViewModelTests
{
    [Fact]
    public void Update_builds_summary_issues_and_log_filters()
    {
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), _ => null);
        var online = CreateServer("1", "Alpha", ServerRuntimeStatus.Running, "Online");
        var offline = CreateServer("2", "Beta", ServerRuntimeStatus.Offline, "Offline");
        var warning = CreateServer("3", "Gamma", ServerRuntimeStatus.Warning, "Starting", statusText: "Warning", lastError: "port busy", hasUpdate: true);

        var state = model.Update([warning, online, offline], selectedLogSource: "2");

        Assert.True(state.Changed);
        Assert.False(state.IsEmpty);
        Assert.Equal(1, state.OnlineCount);
        Assert.Equal(1, state.OfflineCount);
        Assert.Equal(1, state.WarningCount);
        var issue = Assert.Single(state.IssueRows);
        Assert.Equal("Gamma", issue.Name);
        Assert.Equal("port busy", issue.Issue);
        Assert.True(issue.CanIgnoreBuild);
        Assert.Equal(["All logs", "Web", "[1] Alpha", "[2] Beta", "[3] Gamma"], state.LogFilters.Select(item => item.Label));
        Assert.Equal("2", state.SelectedLogFilter.Source);
    }

    [Fact]
    public void Update_applies_running_operation_state()
    {
        var operation = new ServerOperationSnapshot(
            "1",
            "Alpha",
            ServerOperationKind.Start,
            "Booting",
            DateTimeOffset.UtcNow,
            LastError: null,
            IsActive: true);
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), id => id == "1" ? operation : null);

        var state = model.Update([CreateServer("1", "Alpha", ServerRuntimeStatus.Offline, "Offline")], selectedLogSource: null);
        var server = Assert.Single(state.VisibleServers);

        Assert.True(server.IsOperationRunning);
        Assert.Equal("Booting", server.CurrentStatusText);
        Assert.False(server.CanStart);
        Assert.True(server.CanStop);
    }

    [Fact]
    public void Update_reports_unchanged_when_visible_servers_match_previous_update()
    {
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), _ => null);
        var server = CreateServer("1", "Alpha", ServerRuntimeStatus.Running, "Online");

        var first = model.Update([server], selectedLogSource: null);
        var second = model.Update([server], selectedLogSource: null);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Same(first.VisibleServers[0], model.LastVisibleServers[0]);
    }

    [Fact]
    public void LastKnownGoodShutdownSnapshot_is_null_before_any_clean_update()
    {
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), _ => null);

        Assert.Null(model.LastKnownGoodShutdownSnapshot);
    }

    [Fact]
    public void LastKnownGoodShutdownSnapshot_updates_when_every_server_loads_cleanly()
    {
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), _ => null);
        var server = CreateServer("1", "Alpha", ServerRuntimeStatus.Running, "Online");

        model.Update([server], selectedLogSource: null);

        var snapshot = Assert.Single(model.LastKnownGoodShutdownSnapshot!);
        Assert.Equal("1", snapshot.Id);
    }

    [Fact]
    public void LastKnownGoodShutdownSnapshot_is_not_overwritten_by_an_update_containing_a_problem_card()
    {
        // Regression guard for a P1 finding: LastVisibleServers (the OLD shutdown fallback source)
        // is simply "whatever the most recent refresh produced" - it gets overwritten even when the
        // new list contains a problem card (e.g. from a hung per-server load) for a server that is
        // genuinely still running. LastKnownGoodShutdownSnapshot must only ever reflect a refresh
        // where every server's own load succeeded cleanly, so a later refresh with a problem card
        // must leave the previously-good snapshot untouched rather than replacing it with tainted data.
        var model = new ServerListViewModel(new ServerStatusComposer(TimeSpan.FromMinutes(10)), _ => null);
        var healthyFirstLoad = CreateServer("1", "Alpha", ServerRuntimeStatus.Running, "Online");
        model.Update([healthyFirstLoad], selectedLogSource: null);
        var goodSnapshotAfterFirstUpdate = model.LastKnownGoodShutdownSnapshot;

        var problemCard = CreateServer("1", "Alpha", ServerRuntimeStatus.Warning, "Needs attention", lastError: "This server's load timed out.");
        model.Update([problemCard], selectedLogSource: null);

        Assert.Same(goodSnapshotAfterFirstUpdate, model.LastKnownGoodShutdownSnapshot);
        // LastVisibleServers, by contrast, DOES reflect the tainted problem card - proving the two
        // properties are genuinely tracking different things, not the same data under two names.
        var visible = Assert.Single(model.LastVisibleServers);
        Assert.NotNull(visible.LastOperationError);
    }

    private static InstalledServer CreateServer(
        string id,
        string name,
        ServerRuntimeStatus status,
        string currentStatus,
        string? statusText = null,
        string? lastError = null,
        bool hasUpdate = false)
    {
        return new InstalledServer(
            id,
            name,
            "test",
            "server.exe",
            Path.Combine(Path.GetTempPath(), id),
            Path.Combine(Path.GetTempPath(), id, "files"),
            Path.Combine(Path.GetTempPath(), id, "ServerConfig.json"),
            "127.0.0.1",
            "27015",
            "",
            "public",
            "20",
            "",
            "--",
            "--",
            "--",
            currentStatus,
            "--",
            false,
            "",
            lastError,
            true,
            status,
            statusText ?? currentStatus,
            "Brush",
            hasUpdate,
            "",
            "",
            "",
            true,
            true,
            true,
            false);
    }
}
