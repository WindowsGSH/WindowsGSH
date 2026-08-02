using System.Diagnostics;
using System.Collections.Concurrent;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordConsoleOperationTests
{
    [Fact]
    public async Task Bounded_operation_does_not_run_synchronous_module_work_on_caller_thread()
    {
        using var releaseWorker = new ManualResetEventSlim();
        Task<string>? operationTask = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            operationTask = MainWindow.RunBoundedDiscordConsoleOperationAsync(
                _ =>
                {
                    releaseWorker.Wait();
                    return Task.FromResult("sent");
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(operationTask.IsCompleted);
        }
        finally
        {
            releaseWorker.Set();
            if (operationTask != null)
            {
                await operationTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task Bounded_operation_times_out_when_module_ignores_cancellation()
    {
        var neverCompletes = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MainWindow.RunBoundedDiscordConsoleOperationAsync(
                _ => neverCompletes.Task,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None));
    }

    [Fact]
    public async Task Bounded_operation_cancels_module_token_after_timeout()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MainWindow.RunBoundedDiscordConsoleOperationAsync(
                token =>
                {
                    token.Register(() => cancellationObserved.TrySetResult());
                    return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                },
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Bounded_operation_returns_timeout_while_cancellation_callback_is_blocked()
    {
        using var cancellationStarted = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        var moduleTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var boundedTask = MainWindow.RunBoundedDiscordConsoleOperationAsync(
                token =>
                {
                    token.Register(() =>
                    {
                        cancellationStarted.Set();
                        releaseCancellation.Wait();
                    });
                    return moduleTask.Task;
                },
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);

            await Assert.ThrowsAsync<TimeoutException>(() => boundedTask)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseCancellation.Set();
            moduleTask.TrySetResult("late completion");
        }
    }

    [Fact]
    public async Task Parent_cancellation_returns_while_module_cancellation_callback_is_blocked()
    {
        using var parentCancellation = new CancellationTokenSource();
        using var cancellationStarted = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        var moduleTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lifetimeTask = null;
        try
        {
            var boundedTask = MainWindow.RunBoundedDiscordConsoleOperationAsync(
                token =>
                {
                    token.Register(() =>
                    {
                        cancellationStarted.Set();
                        releaseCancellation.Wait();
                    });
                    return moduleTask.Task;
                },
                TimeSpan.FromSeconds(5),
                parentCancellation.Token,
                started => lifetimeTask = started);

            await Task.Run(parentCancellation.Cancel).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundedTask);
            Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.NotNull(lifetimeTask);
            Assert.False(lifetimeTask!.IsCompleted);
        }
        finally
        {
            moduleTask.TrySetResult("cancelled");
            releaseCancellation.Set();
            if (lifetimeTask != null)
            {
                await lifetimeTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task Pending_lifetime_waits_for_blocked_cancellation_after_module_task_completes()
    {
        using var cancellationStarted = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        var moduleTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lifetimeTask = null;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                MainWindow.RunBoundedDiscordConsoleOperationAsync(
                    token =>
                    {
                        token.Register(() =>
                        {
                            cancellationStarted.Set();
                            moduleTask.TrySetResult("completed during cancellation");
                            releaseCancellation.Wait();
                        });
                        return moduleTask.Task;
                    },
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.None,
                    started => lifetimeTask = started));

            Assert.True(cancellationStarted.Wait(TimeSpan.FromSeconds(2)));
            await moduleTask.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(lifetimeTask);
            Assert.False(lifetimeTask!.IsCompleted);
        }
        finally
        {
            releaseCancellation.Set();
            if (lifetimeTask != null)
            {
                await lifetimeTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task Bounded_operation_exposes_worker_that_remains_incomplete_after_timeout()
    {
        var moduleTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? workerTask = null;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                MainWindow.RunBoundedDiscordConsoleOperationAsync(
                    _ => moduleTask.Task,
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.None,
                    started => workerTask = started));

            Assert.NotNull(workerTask);
            Assert.False(workerTask!.IsCompleted);
        }
        finally
        {
            moduleTask.TrySetResult("late completion");
            if (workerTask != null)
            {
                await workerTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public void Pending_worker_removal_does_not_remove_replacement_task()
    {
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var pendingWorkers = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-1"] = replacement
        };

        var removed = MainWindow.RemovePendingDiscordConsoleWorkerIfCurrent(
            pendingWorkers,
            "server-1",
            original);

        Assert.False(removed);
        Assert.Same(replacement, pendingWorkers["server-1"]);
    }
}
