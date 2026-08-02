using WindowsGSH.Core.Operations;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerOperationManagerTests
{
    [Fact]
    public void TryBegin_creates_active_operation_with_default_status_and_blocks_duplicate()
    {
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        var began = manager.TryBegin("1", "Icarus", ServerOperationKind.Start, out var scope, out var existing);
        var duplicateBegan = manager.TryBegin("1", "Icarus", ServerOperationKind.Stop, out var duplicateScope, out var duplicateExisting);

        Assert.True(began);
        Assert.NotNull(scope);
        Assert.Null(existing);
        Assert.False(duplicateBegan);
        Assert.Null(duplicateScope);
        Assert.NotNull(duplicateExisting);
        Assert.Equal(ServerOperationKind.Start, duplicateExisting.Kind);
        Assert.Equal("Starting", duplicateExisting.Status);
        Assert.Equal(ServerOperationStatus.Running, duplicateExisting.State);
        Assert.True(duplicateExisting.IsActive);
        Assert.Empty(history);

        scope.Dispose();
    }

    [Fact]
    public void Scope_dispose_completes_operation_and_records_history()
    {
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        Assert.True(manager.TryBegin("2", "TF2", ServerOperationKind.Update, out var scope, out _));

        scope!.Dispose();

        // Manager keeps the terminal snapshot (inactive) so polling endpoints can read it.
        var completedInMap = manager.Get("2");
        Assert.NotNull(completedInMap);
        Assert.False(completedInMap!.IsActive);
        Assert.Equal("Completed", completedInMap.Status);
        var completed = Assert.Single(history);
        Assert.Equal("2", completed.ServerId);
        Assert.Equal("TF2", completed.ServerName);
        Assert.Equal(ServerOperationKind.Update, completed.Kind);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(ServerOperationStatus.Completed, completed.State);
        Assert.False(completed.IsActive);
        Assert.NotNull(completed.FinishedAt);
        Assert.Equal(completed.FinishedAt, completed.CompletedAt);
        Assert.NotNull(completed.Duration);
        Assert.Null(completed.LastError);
    }

    [Fact]
    public void Fail_marks_operation_inactive_records_error_and_clears_cancellation()
    {
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        Assert.True(manager.TryBegin("3", "Valheim", ServerOperationKind.Backup, out _, out _));

        manager.Fail("3", new InvalidOperationException("backup path missing"));

        var failed = manager.Get("3");
        Assert.NotNull(failed);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(ServerOperationStatus.Failed, failed.State);
        Assert.Equal("backup path missing", failed.LastError);
        Assert.False(failed.IsActive);
        Assert.NotNull(failed.FinishedAt);
        Assert.False(manager.Cancel("3"));
        Assert.Equal("backup path missing", manager.GetLastOperationError("3"));

        var recorded = Assert.Single(history);
        Assert.Equal(failed, recorded);
    }

    [Fact]
    public void Stale_scope_failure_does_not_fail_replacement_operation()
    {
        var manager = new ServerOperationManager(_ => { });
        Assert.True(manager.TryBegin("scope-race", "Server", ServerOperationKind.ConsoleCommand, out var staleScope, out _));

        manager.Complete("scope-race");
        Assert.True(manager.TryBegin("scope-race", "Server", ServerOperationKind.Update, out var replacementScope, out _));

        staleScope!.Fail(new InvalidOperationException("stale failure"));

        var replacement = manager.Get("scope-race");
        Assert.NotNull(replacement);
        Assert.True(replacement!.IsActive);
        Assert.Equal(ServerOperationKind.Update, replacement.Kind);
        Assert.Null(replacement.LastError);
        replacementScope!.Dispose();
    }

    [Fact]
    public void Cancel_marks_operation_cancelling_and_signals_scope_token()
    {
        var manager = new ServerOperationManager(_ => { });

        Assert.True(manager.TryBegin("4", "Minecraft", ServerOperationKind.Restart, out var scope, out _));
        Assert.False(scope!.CancellationToken.IsCancellationRequested);

        var cancelled = manager.Cancel("4");

        Assert.True(cancelled);
        Assert.True(scope.CancellationToken.IsCancellationRequested);
        Assert.Equal("Cancelling", manager.Get("4")?.Status);

        scope.Dispose();
        var recorded = Assert.Single(manager.GetHistory());
        Assert.Equal("Cancelled", recorded.Status);
        Assert.Equal(ServerOperationStatus.Cancelled, recorded.State);
    }

    [Fact]
    public void History_is_capped_to_recent_two_hundred_operations()
    {
        var manager = new ServerOperationManager(_ => { });

        for (var i = 0; i < 205; i++)
        {
            Assert.True(manager.TryBegin(i.ToString(), $"Server {i}", ServerOperationKind.Install, out var scope, out _));
            scope!.Dispose();
        }

        var history = manager.GetHistory(250);

        Assert.Equal(200, history.Count);
        Assert.DoesNotContain(history, operation => operation.ServerId == "0");
        Assert.Contains(history, operation => operation.ServerId == "204");
    }

    [Fact]
    public void Console_command_operation_uses_console_status()
    {
        var manager = new ServerOperationManager(_ => { });

        Assert.True(manager.TryBegin("5", "Paper", ServerOperationKind.ConsoleCommand, out var scope, out _));

        Assert.Equal("Running console command", manager.Get("5")?.Status);

        scope!.Dispose();
    }

    [Fact]
    public void Different_servers_can_run_operations_at_the_same_time()
    {
        var manager = new ServerOperationManager(_ => { });

        var first = manager.TryBegin("1", "One", ServerOperationKind.Start, out var firstScope, out _);
        var second = manager.TryBegin("2", "Two", ServerOperationKind.Update, out var secondScope, out _);

        Assert.True(first);
        Assert.True(second);
        Assert.True(manager.IsOperationRunning("1"));
        Assert.True(manager.IsOperationRunning("2"));
        Assert.True(manager.IsAnyOperationRunning());
        Assert.Equal(2, manager.GetRunningOperations().Count);

        firstScope!.Dispose();
        secondScope!.Dispose();
    }

    [Fact]
    public void Global_operation_blocks_server_operations_and_server_operation_blocks_global_operation()
    {
        var manager = new ServerOperationManager(_ => { });

        Assert.True(manager.TryBeginGlobalOperation(ServerOperationKind.Install, out var globalScope, out var existing, "Installing prerequisites"));
        Assert.Null(existing);
        Assert.False(manager.TryBegin("1", "One", ServerOperationKind.Start, out var blockedScope, out var blockedExisting));
        Assert.Null(blockedScope);
        Assert.NotNull(blockedExisting);
        Assert.Equal("Installing prerequisites", blockedExisting.Description);

        globalScope!.Dispose();

        Assert.True(manager.TryBegin("1", "One", ServerOperationKind.Start, out var serverScope, out _));
        Assert.False(manager.TryBeginGlobalOperation(ServerOperationKind.Update, out var blockedGlobalScope, out var serverExisting));
        Assert.Null(blockedGlobalScope);
        Assert.NotNull(serverExisting);
        Assert.Equal("1", serverExisting.ServerId);

        serverScope!.Dispose();
    }

    [Fact]
    public void Force_stop_interrupts_active_server_operation_and_starts_force_stop()
    {
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        Assert.True(manager.TryBegin("1", "Paper", ServerOperationKind.Start, out var startScope, out _));
        Assert.False(startScope!.CancellationToken.IsCancellationRequested);

        var forceBegan = manager.TryBegin("1", "Paper", ServerOperationKind.ForceStop, out var forceScope, out var existing);

        Assert.True(forceBegan);
        Assert.NotNull(forceScope);
        Assert.Null(existing);
        Assert.True(startScope.CancellationToken.IsCancellationRequested);
        Assert.Equal(ServerOperationKind.ForceStop, manager.Get("1")?.Kind);

        var interrupted = Assert.Single(history);
        Assert.Equal(ServerOperationKind.Start, interrupted.Kind);
        Assert.Equal("Interrupted", interrupted.Status);
        Assert.Equal(ServerOperationStatus.Interrupted, interrupted.State);
        Assert.Equal("Interrupted by force stop.", interrupted.LastError);

        forceScope!.Dispose();
    }

    [Fact]
    public void Interrupted_scope_dispose_does_not_complete_replacement_force_stop_operation()
    {
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        Assert.True(manager.TryBegin("1", "Paper", ServerOperationKind.Update, out var updateScope, out _));
        Assert.True(manager.TryBegin("1", "Paper", ServerOperationKind.ForceStop, out var forceScope, out _));
        Assert.Equal(ServerOperationKind.ForceStop, manager.Get("1")?.Kind);

        updateScope!.Dispose();

        var active = manager.Get("1");
        Assert.NotNull(active);
        Assert.True(active.IsActive);
        Assert.Equal(ServerOperationKind.ForceStop, active.Kind);
        Assert.Equal("Force stopping", active.Status);
        Assert.True(forceScope!.CancellationToken.CanBeCanceled);
        Assert.Single(history);

        forceScope.Dispose();

        var finalSnapshot = manager.Get("1");
        Assert.NotNull(finalSnapshot);
        Assert.False(finalSnapshot!.IsActive);
        Assert.Equal("Completed", finalSnapshot.Status);
        Assert.Equal(2, history.Count);
        Assert.Equal(ServerOperationKind.Update, history[0].Kind);
        Assert.Equal("Interrupted", history[0].Status);
        Assert.Equal(ServerOperationKind.ForceStop, history[1].Kind);
        Assert.Equal("Completed", history[1].Status);
    }

    [Fact]
    public void Completing_an_already_completed_operation_is_a_no_op_not_a_second_completion()
    {
        // Mirrors the real Start flow: ServerLifecycleService.StartAsync's ClearStatus callback
        // completes the operation mid-flow (an explicit Complete() call), then
        // ServerOperationsController's own finally block disposes the scope, which also completes
        // the same operationId - two independent completion paths for one logical operation. Before
        // the fix, this published a second identical ServerOperationChangedEvent and recorded a
        // second history entry, producing the duplicate "Server start completed" Discord alerts a
        // real deployment reported. Only Start has this two-completion-path shape; every other
        // operation kind only ever completes once via the scope's own Dispose().
        var history = new List<ServerOperationSnapshot>();
        var manager = new ServerOperationManager(history.Add);

        Assert.True(manager.TryBegin("1", "Icarus", ServerOperationKind.Start, out var scope, out _));

        var firstCompletion = manager.CompleteOperation("1");
        scope!.Dispose();

        var secondCompletion = manager.Get("1");
        Assert.Single(history);
        Assert.NotNull(firstCompletion);
        Assert.NotNull(secondCompletion);
        Assert.False(secondCompletion!.IsActive);
        Assert.Equal(firstCompletion!.CompletedAt, secondCompletion.FinishedAt);
    }

    [Fact]
    public void Complete_and_fail_return_structured_results()
    {
        var manager = new ServerOperationManager(_ => { });

        Assert.True(manager.TryBegin("1", "Paper", ServerOperationKind.Backup, out _, out _, "Manual backup"));

        var completed = manager.CompleteOperation("1");

        Assert.NotNull(completed);
        Assert.True(completed.Success);
        Assert.Equal(ServerOperationStatus.Completed, completed.Status);
        Assert.Equal("Manual backup", completed.Description);
        Assert.True(completed.Duration >= TimeSpan.Zero);

        Assert.True(manager.TryBegin("1", "Paper", ServerOperationKind.Update, out _, out _));
        var failed = manager.FailOperation("1", new InvalidOperationException("network failed"));

        Assert.NotNull(failed);
        Assert.False(failed.Success);
        Assert.Equal(ServerOperationStatus.Failed, failed.Status);
        Assert.Equal("network failed", failed.LastError);
    }
}
