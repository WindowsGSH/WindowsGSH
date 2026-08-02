using System.Collections.Concurrent;
using Xunit;

namespace WindowsGSH.Tests;

// Regression guard for a P2 finding against MainWindow.BuildSupportBundleHealthReportsAsync: calling
// an async method directly still runs synchronously on the caller's thread up until its first
// genuine await point. If that synchronous prefix itself blocks (e.g. a module's
// GetConfigFields/GetPorts/IsInstallValid hangs before ServerHealthService.EvaluateAsync ever reaches
// an internal await), the call never even returns a task - the Task.WhenAny timeout race set up
// around it is never reached, and the advertised 20-second bound never applies. MainWindow.xaml.cs
// isn't something this suite can instantiate directly (it's a WPF Window with UI-only dependencies),
// so these tests prove the underlying TPL mechanism the fix (wrapping the call in Task.Run) relies on
// instead, using a minimal, self-contained stand-in for a synchronously-hanging module call.
public sealed class SupportBundleTimeoutMechanismTests
{
    private const int BlockMilliseconds = 300;
    private static readonly TimeSpan TaskRunReturnTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Direct_call_to_a_synchronously_blocking_async_method_defeats_a_timeout_race()
    {
        var before = DateTime.UtcNow;
        var directTask = BlockThenCompleteAsync();
        var elapsedBeforeTaskExisted = DateTime.UtcNow - before;

        // The synchronous prefix inside BlockThenCompleteAsync already ran to completion on this
        // thread before the line above even returned a task - by the time a caller could set up a
        // Task.WhenAny(directTask, Task.Delay(shortTimeout)) race, the "hang" already fully elapsed.
        Assert.True(
            elapsedBeforeTaskExisted >= TimeSpan.FromMilliseconds(BlockMilliseconds * 0.9),
            $"Expected the direct call to block for close to {BlockMilliseconds}ms before returning a task; only blocked for {elapsedBeforeTaskExisted.TotalMilliseconds}ms.");

        await directTask;
    }

    [Fact]
    public async Task Wrapping_the_same_call_in_TaskRun_lets_a_timeout_race_work_correctly()
    {
        using var releaseWorker = new ManualResetEventSlim(false);
        var before = DateTime.UtcNow;
        var wrappedTask = Task.Run(() => releaseWorker.Wait());
        var elapsedBeforeTaskExisted = DateTime.UtcNow - before;

        // Task.Run schedules the blocking call on a different thread pool thread and returns near-
        // instantly - the synchronous block happens over there, not here, so a real task exists to
        // race immediately.
        try
        {
            Assert.True(
                elapsedBeforeTaskExisted < TaskRunReturnTimeout,
                $"Expected Task.Run to return promptly; took {elapsedBeforeTaskExisted.TotalMilliseconds}ms.");

            var shortTimeout = TimeSpan.FromMilliseconds(BlockMilliseconds / 3);
            var completed = await Task.WhenAny(wrappedTask, Task.Delay(shortTimeout));

            // The worker cannot complete until this test releases it, so scheduler saturation cannot
            // make the assertion depend on whether a blocking sleep or timer callback happens first.
            Assert.NotSame(wrappedTask, completed);
        }
        finally
        {
            releaseWorker.Set();
            await wrappedTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task BlockThenCompleteAsync()
    {
        Thread.Sleep(BlockMilliseconds);
        await Task.Yield();
    }

    // Regression guard for a follow-up P2 finding: the Task.Run wrap above lets one export's loop
    // move past a hung server within the timeout, but it can't actually terminate the thread-pool
    // worker underneath a synchronously-blocked module - that worker may still be running (and
    // consuming a thread) forever. Without tracking that, every repeated export against the same
    // stuck server would start yet another worker that also never returns - an unbounded leak over a
    // process that may run for a month or more. MainWindow's actual fix keys a
    // ConcurrentDictionary<string, Task<ServerHealthReport>> by server id and skips starting a new
    // Task.Run while an entry for that id is still present; this proves that exact
    // check-then-register-then-clear pattern actually prevents a duplicate start and still allows a
    // fresh attempt once the earlier one clears, without needing to instantiate MainWindow itself.
    [Fact]
    public async Task Dictionary_guard_prevents_a_second_worker_for_the_same_key_while_one_is_still_pending()
    {
        var pending = new ConcurrentDictionary<string, Task<int>>();
        using var releaseWorker = new ManualResetEventSlim(false);
        var starts = 0;

        Task<int>? StartOrSkip(string key)
        {
            if (pending.ContainsKey(key))
            {
                return null;
            }

            Interlocked.Increment(ref starts);
            var task = Task.Run(() =>
            {
                releaseWorker.Wait();
                return 1;
            });
            pending[key] = task;
            return task;
        }

        Task<int>? first = null;
        Task<int>? third = null;
        try
        {
            first = StartOrSkip("server-a");
            // Simulates a second support-bundle export starting while the first server's evaluation is
            // still stuck from the earlier one.
            var second = StartOrSkip("server-a");

            Assert.NotNull(first);
            Assert.Null(second);
            Assert.Equal(1, starts);

            releaseWorker.Set();
            await first;
            pending.TryRemove("server-a", out _);

            // Simulates a later export, started only after the earlier stuck entry finally cleared.
            third = StartOrSkip("server-a");
            Assert.NotNull(third);
            Assert.Equal(2, starts);
            await third;
        }
        finally
        {
            releaseWorker.Set();
            var workers = new[] { first, third }.Where(task => task != null).Cast<Task>();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
