using System.Collections.Concurrent;
using WindowsGSH.Core;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

// Regression guard for a real, root-cause stability hazard: InstalledServerLoader.LoadAsync holds a
// single shared _loadLock semaphore for the entire duration of its Task.WhenAll over every installed
// server's per-server load. A module whose GetDisplayInfo/IsInstallValid call (invoked synchronously,
// not awaited, inside TryLoadAsync) hangs forever previously blocked that Task.WhenAll - and therefore
// _loadLock - forever, freezing every other caller of LoadAsync app-wide (the periodic refresh timer,
// manual refresh, support-bundle export, etc.), not just the one hung server. A per-call timeout
// alone is not sufficient: Task.WhenAny can make ONE LoadAsync call move on, but it cannot terminate
// the underlying Task.Run worker if the module call inside it is genuinely, synchronously blocked -
// that worker (and its thread) just keeps running, and every subsequent LoadAsync call would start
// ANOTHER one for the same stuck server, leaking thread-pool workers without bound.
//
// These tests use the internal (InternalsVisibleTo) constructor seam to substitute a controllable
// per-server load delegate instead of a real compiled module, so the hang/recovery timing is fully
// deterministic rather than depending on a real 20-second wait against real module code.
public sealed class InstalledServerLoaderHangProtectionTests : IDisposable
{
    // LoadAsync always scans the real AppPaths.GetPath("servers") directory - there is no way to
    // redirect it to an isolated temp directory without changing its signature (same constraint
    // InstalledServerLoaderLoadAsyncTests documents). Only empty folders are needed here: the
    // substituted load delegate never reads ServerConfig.json at all, unlike the real TryLoadAsync.
    private readonly List<string> _serverFolders = [];
    private readonly string _serversRoot = AppPaths.GetPath("servers");
    private readonly ConcurrentDictionary<string, TaskCompletionSource<InstalledServer>> _hangGates = new();
    private readonly ConcurrentDictionary<string, int> _invocationCounts = new();
    private readonly ConcurrentDictionary<string, Func<Task<InstalledServer>>> _customResults = new();
    private static readonly TimeSpan TestServerLoadTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TestShutdownTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan NeverHangAssertionBound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    // Regression guard for a P2 finding: several tests used a fixed sleep to assume a background
    // observer had finished its work by then - fine on a quiet machine, but a real flake risk on a
    // loaded CI runner where the observer's continuation might simply not have been scheduled yet.
    // Polls an explicit, directly-observable piece of state instead, with a generous overall bound
    // (failing loudly with a clear message if that bound is exceeded, rather than proceeding with a
    // false assumption and producing a confusing downstream assertion failure).
    private static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + PollBound;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(timeoutMessage);
            }

            await Task.Delay(PollInterval);
        }
    }

    [Fact]
    public async Task LoadAsync_releases_its_lock_after_a_server_load_times_out()
    {
        var hungFolder = CreateHungServerFolder("hang-lock-release");
        var loader = CreateLoader();

        var firstLoad = await loader.LoadAsync();
        AssertTimedOutProblemCard(firstLoad, hungFolder);

        // If _loadLock were never released because the first call's Task.WhenAll never actually
        // completed, this second call would hang forever waiting on the semaphore. Bounding it with
        // a real, generous assertion timeout (far larger than TestServerLoadTimeout) proves it wasn't
        // still held, without the test itself ever hanging if the fix regresses.
        var secondLoadTask = loader.LoadAsync();
        var completed = await Task.WhenAny(secondLoadTask, Task.Delay(NeverHangAssertionBound));
        Assert.Same(secondLoadTask, completed);
    }

    [Fact]
    public async Task LoadAsync_still_loads_unaffected_servers_when_one_server_hangs()
    {
        var hungFolder = CreateHungServerFolder("hang-others-healthy-hung");
        var healthyId = CreateHealthyServerFolder("hang-others-healthy-ok");
        var loader = CreateLoader();

        var servers = await loader.LoadAsync();

        var healthy = servers.FirstOrDefault(server => server.Id == healthyId);
        Assert.NotNull(healthy);
        Assert.Null(healthy!.LastOperationError);

        AssertTimedOutProblemCard(servers, hungFolder);
    }

    [Fact]
    public async Task LoadAsync_does_not_start_duplicate_work_for_a_still_hung_server_across_repeated_calls()
    {
        var hungFolder = CreateHungServerFolder("hang-no-duplicate");
        var folderName = Path.GetFileName(hungFolder);
        var loader = CreateLoader();

        await loader.LoadAsync();
        await loader.LoadAsync();
        await loader.LoadAsync();

        // The load delegate must have been invoked exactly once for this server, no matter how many
        // times LoadAsync was called while it was still stuck - proving the pending-load dictionary
        // actually prevented a second (or third) Task.Run worker from ever starting.
        Assert.Equal(1, _invocationCounts.GetValueOrDefault(folderName));
    }

    [Fact]
    public async Task LoadAsync_retries_and_returns_the_real_result_once_a_stuck_load_finally_completes()
    {
        var hungFolder = CreateHungServerFolder("hang-retry");
        var folderName = Path.GetFileName(hungFolder);
        var loader = CreateLoader();

        var firstLoad = await loader.LoadAsync();
        AssertTimedOutProblemCard(firstLoad, hungFolder);

        // Simulate the module finally unblocking, well after the timeout already elapsed.
        _hangGates[folderName].SetResult(CreateFakeServer(folderName, "Recovered Server"));

        var secondLoad = await loader.LoadAsync();
        var recovered = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(recovered);
        Assert.Equal("Recovered Server", recovered!.Name);
        Assert.Null(recovered.LastOperationError);

        // Not asserted: an exact invocation count here. Once the original attempt is genuinely done
        // (its result already set before this second call), whether it's reused directly or a fresh
        // call is made against the now-already-completed gate is an inherent, harmless race between
        // this synchronous path and the background observer that also clears the pending entry -
        // both outcomes correctly return the real result immediately, with no new hang either way.
        // What actually matters (never piling up a SECOND *simultaneously stuck* worker while the
        // first is still genuinely blocked) is covered by the repeated-calls-while-still-hung test.
    }

    [Fact]
    public void RemovePendingLoadIfStillCurrent_does_not_remove_a_newer_task_inserted_under_the_same_key()
    {
        // Regression guard for a P2 finding: a plain TryRemove(key, out _) removes whatever is
        // currently stored under a key regardless of which task instance it is. The real race this
        // guards against: an old, timed-out task's late background observer (ObserveLatePendingLoadAsync)
        // finally gets around to removing "its" entry, but by then a completely different, newer
        // attempt has already been inserted under the same folder key (because the old task itself
        // completed and was separately consumed/removed by a concurrent LoadAsync call in the
        // meantime). The fix must only remove an entry if it still holds the EXACT task instance
        // being compared against - proven directly here via the internal test seams, since forcing
        // this exact interleaving through real background-task scheduling isn't reliable to arrange.
        var loader = CreateLoader();
        const string folderKey = "pending-load-race-folder";
        var oldTask = Task.FromResult(CreateFakeServer("old", "Old"));
        var newTask = Task.FromResult(CreateFakeServer("new", "New"));

        loader.SetPendingServerLoadForTests(folderKey, oldTask);
        loader.SetPendingServerLoadForTests(folderKey, newTask);

        // The losing removal attempt must report failure - this return value is what
        // TryCacheLateResultAsync below gates its cache write on.
        Assert.False(loader.RemovePendingLoadIfStillCurrent(folderKey, oldTask));

        Assert.True(loader.TryGetPendingServerLoadForTests(folderKey, out var stillPending));
        Assert.Same(newTask, stillPending);

        Assert.True(loader.RemovePendingLoadIfStillCurrent(folderKey, newTask));
        Assert.False(loader.TryGetPendingServerLoadForTests(folderKey, out _));
    }

    [Fact]
    public async Task LoadAsync_responds_to_cancellation_promptly_instead_of_waiting_out_the_full_timeout()
    {
        // Regression guard for a P2 finding: both timeout races used Task.Delay(_serverLoadTimeout)
        // without the caller's cancellation token - Task.Delay on its own doesn't observe an unrelated
        // token, so cancelling LoadAsync while a pending module call ignores its own token would still
        // wait out the entire timeout window before noticing. Uses a long (5s) timeout specifically so
        // a regression (cancellation ignored, falling through to the timeout instead) would show up as
        // a slow test rather than a silent pass.
        var hungFolder = CreateHungServerFolder("cancel-promptly");
        var folderName = Path.GetFileName(hungFolder);
        var loader = new InstalledServerLoader(new NeverCalledStatusService(), null, LoadServerAsync, TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();

        var loadTask = loader.LoadAsync(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var before = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loadTask);
        var elapsed = DateTime.UtcNow - before;

        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"Expected cancellation to be observed well before the 5s timeout; took {elapsed}.");

        // Regression guard for a related P2 finding: this call's own cancellation, while the
        // underlying task was still genuinely running, must not leave that task unobserved forever -
        // a follow-up, uncancelled LoadAsync call must still get a sane outcome once the module
        // finally responds, not a hang or a repeat of the earlier cancellation.
        _hangGates[folderName].SetResult(CreateFakeServer(folderName, "Recovered After Cancellation"));
        var secondLoad = await loader.LoadAsync();
        var secondCard = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(secondCard);
        Assert.Equal("Recovered After Cancellation", secondCard!.Name);
        Assert.Null(secondCard.LastOperationError);
    }

    [Fact]
    public async Task LoadAsync_cleans_up_and_retries_after_an_already_canceled_load_task()
    {
        // Regression guard for a P2 finding: if the underlying load task itself becomes canceled
        // independently of this call's own cancellationToken (e.g. Task.Run's token already canceled
        // at schedule time, or the load delegate itself returning a canceled task), WaitAsync throws
        // OperationCanceledException too - indistinguishable by exception type alone from this call's
        // own cancellation. The old code only cleaned up/attached an observer on TimeoutException, so
        // this exact case left a permanently-terminal task sitting in the pending dictionary forever -
        // every future LoadAsync call would find it and immediately rethrow that same stale exception.
        var folder = CreateCancelingThenRecoveringServerFolder("cancel-then-recover");
        var folderName = Path.GetFileName(folder);
        var loader = CreateLoader();

        // First call: the load task itself is already-canceled - this call's own token was never
        // canceled, so it must not throw uncaught; it must return a bounded, generic problem card.
        var firstLoad = await loader.LoadAsync();
        var firstCard = firstLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(firstCard);
        Assert.NotNull(firstCard!.LastOperationError);

        // Second call: the stale entry must have been cleaned up, so this starts a fresh attempt and
        // gets the real, recovered result - not a repeat of the same stale cancellation.
        var secondLoad = await loader.LoadAsync();
        var recovered = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(recovered);
        Assert.Equal("Recovered After Cancellation", recovered!.Name);
        Assert.Null(recovered.LastOperationError);
        Assert.Equal(2, _invocationCounts.GetValueOrDefault(folderName));
    }

    [Fact]
    public async Task LoadAsync_cleans_up_and_retries_after_an_already_faulted_load_task()
    {
        // Same guard as above, for a genuinely faulted (not canceled) load task - TryLoadAsync's own
        // catch normally prevents a real fault from reaching this far in production, but must not be
        // trusted to guarantee it; one server's unexpected fault must never permanently poison its own
        // retry slot, nor abort the whole LoadAsync batch for every other server.
        var folder = CreateFaultingThenRecoveringServerFolder("fault-then-recover");
        var folderName = Path.GetFileName(folder);
        var loader = CreateLoader();

        var firstLoad = await loader.LoadAsync();
        var firstCard = firstLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(firstCard);
        Assert.NotNull(firstCard!.LastOperationError);

        var secondLoad = await loader.LoadAsync();
        var recovered = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(recovered);
        Assert.Equal("Recovered After Fault", recovered!.Name);
        Assert.Null(recovered.LastOperationError);
        Assert.Equal(2, _invocationCounts.GetValueOrDefault(folderName));
    }

    [Fact]
    public async Task LoadAsync_returns_immediately_for_a_confirmed_stuck_server_instead_of_re_waiting_the_full_timeout()
    {
        // Regression guard for a P2 finding: once a server's pending load has ALREADY been observed
        // to time out once, every subsequent LoadAsync call used to re-race the same still-running
        // task against another full _serverLoadTimeout before returning the same "still in progress"
        // problem card - and since LoadAsync holds _loadLock for its entire duration, that delay
        // applied to every other server in the batch and every other caller sharing this loader too
        // (this app's own 3-second periodic refresh cadence chief among them), not just the one hung
        // server. A confirmed-stuck server must return its problem card near-instantly instead.
        //
        // Uses a dedicated loader with a much longer (5s) configured timeout, rather than the shared
        // 300ms TestServerLoadTimeout, specifically so the "near-instantly" assertion below can use a
        // generous 2-second bound instead of a fraction of a 300ms window - proving the short-circuit
        // did not wait anywhere close to the full timeout without requiring near-real-time scheduling
        // (real directory scanning and module discovery for the second call still take some real,
        // if small, time on a loaded CI runner).
        var confirmedStuckTimeout = TimeSpan.FromSeconds(5);
        var hungFolder = CreateHungServerFolder("confirmed-stuck-fast");
        var loader = new InstalledServerLoader(new NeverCalledStatusService(), null, LoadServerAsync, confirmedStuckTimeout);

        var firstLoad = await loader.LoadAsync();
        AssertTimedOutProblemCard(firstLoad, hungFolder);

        var before = DateTime.UtcNow;
        var secondLoad = await loader.LoadAsync();
        var elapsed = DateTime.UtcNow - before;

        // The second call goes through the "already confirmed stuck" short-circuit, which reuses the
        // "still in progress from an earlier attempt" wording (not "timed out", which only the first,
        // fresh-work attempt's own timeout message uses) - so this checks for that text directly
        // rather than reusing AssertTimedOutProblemCard.
        var folderName = Path.GetFileName(hungFolder);
        var problemCard = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(problemCard);
        Assert.NotNull(problemCard!.LastOperationError);
        Assert.Contains("still in progress from an earlier attempt", problemCard.LastOperationError, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"Expected the second call to return well before the {confirmedStuckTimeout} timeout since the server is already confirmed stuck; took {elapsed}.");
    }

    [Fact]
    public async Task LoadAsync_preserves_a_late_successful_result_after_the_original_attempt_timed_out()
    {
        // Regression guard for a P2 finding: when a legitimate server load consistently takes
        // slightly longer than the timeout, ObserveLatePendingLoadAsync used to just discard the
        // eventual successful result and immediately remove the pending entry - the next refresh
        // would start the same slow work again, time out again, and could show a timeout card
        // forever even though every individual attempt actually succeeds shortly afterward. The late
        // result must be cached and consumed by the next call that reaches a fresh attempt for this
        // folder, instead of being thrown away.
        //
        // The delegate deliberately makes any RESTARTED attempt (invocation 2+) hang forever too, not
        // just the first one - this server's load is consistently slow on every attempt, not merely
        // slow once. Without that, a naive test could pass even without the fix: if a fresh restart
        // happened to complete immediately (as it would against an already-set, one-shot gate), that
        // would mask the bug rather than prove the cache is what's actually being used.
        var folder = CreateEmptyServerFolder("late-success-consistent");
        var folderName = Path.GetFileName(folder);
        var invocationCount = 0;
        var firstAttemptGate = new TaskCompletionSource<InstalledServer>();
        _customResults[folderName] = () =>
        {
            var attempt = Interlocked.Increment(ref invocationCount);
            return attempt == 1
                ? firstAttemptGate.Task
                : new TaskCompletionSource<InstalledServer>().Task;
        };
        var loader = CreateLoader();

        var firstLoad = await loader.LoadAsync();
        var firstCard = firstLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(firstCard);
        Assert.NotNull(firstCard!.LastOperationError);

        // Simulate the slow-but-legitimate load finally succeeding, shortly after the timeout.
        firstAttemptGate.SetResult(CreateFakeServer(folderName, "Recovered Late"));

        // The background observer's own continuation needs a moment to run; poll with a generous
        // bound rather than depending on exact scheduling timing. Each poll either sees the
        // still-pending (now confirmed-stuck, so near-instant per the earlier fix) entry, or the
        // cached late result once the observer has finished.
        InstalledServer? recovered = null;
        for (var attempt = 0; attempt < 20 && recovered == null; attempt++)
        {
            var load = await loader.LoadAsync();
            var candidate = load.FirstOrDefault(server => server.Id == folderName);
            if (candidate is { LastOperationError: null })
            {
                recovered = candidate;
            }
            else
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(recovered);
        Assert.Equal("Recovered Late", recovered!.Name);
        // Only the cache should have produced this - a second, restarted attempt would have hung
        // forever (per the delegate above) and never resolved in time.
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task LoadAsync_does_not_cache_a_late_problem_card_as_a_successful_recovery()
    {
        // Regression guard for a P2 finding: TryLoadAsync catches its own exceptions (a cancelled
        // status query, among others) and returns a problem card as an ordinary, successful Task
        // result rather than throwing. ObserveLatePendingLoadAsync used to cache every late result
        // unconditionally - so a late-arriving problem card would get replayed by the next
        // LoadAsync call as if it were a genuinely healthy "recovered" result, permanently skipping
        // any further real retry for that server.
        var folder = CreateEmptyServerFolder("late-problem-card");
        var folderName = Path.GetFileName(folder);
        var invocationCount = 0;
        var firstAttemptGate = new TaskCompletionSource<InstalledServer>();
        _customResults[folderName] = () =>
        {
            var attempt = Interlocked.Increment(ref invocationCount);
            return attempt == 1
                ? firstAttemptGate.Task
                : Task.FromResult(CreateFakeServer(folderName, "Recovered On Retry"));
        };
        var loader = CreateLoader();

        var firstLoad = await loader.LoadAsync();
        var firstCard = firstLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(firstCard);
        Assert.NotNull(firstCard!.LastOperationError);

        // Simulate TryLoadAsync's own catch: the first attempt completes late (after the timeout
        // already elapsed), but with a problem card returned NORMALLY rather than by throwing -
        // exactly as TryLoadAsync does for a cancelled status query.
        firstAttemptGate.SetResult(CreateProblemLikeServer(folderName));

        // Wait BEFORE making any further LoadAsync call, rather than racing one immediately: the
        // background observer (ObserveLatePendingLoadAsync) needs a moment to run its continuation
        // over the now-completed loadTask. Calling LoadAsync again right away could instead race
        // ahead of the observer and consume the still-present _pendingServerLoads entry directly
        // (correctly returning that exact completed task's result either way) - which would exercise
        // a harmless, unrelated race rather than the caching decision this test targets. Polling the
        // pending-load dictionary directly (rather than a fixed sleep) proves the observer has
        // actually finished - and, under the bug, already cached the problem card into _lateResults -
        // before this test's own single follow-up call is made, without a flake risk on a loaded CI
        // runner.
        await WaitUntilAsync(
            () => !loader.TryGetPendingServerLoadForTests(folderName, out _),
            $"Expected the background observer to clear the pending-load entry for '{folderName}' within {PollBound}.");

        var secondLoad = await loader.LoadAsync();
        var secondCard = secondLoad.FirstOrDefault(server => server.Id == folderName);

        // Under the bug, the cached problem card is returned here directly via _lateResults with no
        // fresh attempt (invocationCount stays 1). The fix must instead ignore the cached problem
        // card and start a genuinely fresh attempt, which (per the delegate above) succeeds
        // immediately.
        Assert.Equal(2, invocationCount);
        Assert.NotNull(secondCard);
        Assert.Equal("Recovered On Retry", secondCard!.Name);
        Assert.Null(secondCard.LastOperationError);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_returns_null_within_the_timeout_when_enumeration_hangs()
    {
        // Regression guard for a P1 finding: LoadRunningServersForExitAsync used to call the
        // synchronous LoadForShutdown() directly with no timeout at all. LoadForShutdown
        // deliberately avoids GetDisplayInfo/IsInstallValid, but ServerProcessLocator.IsRunning
        // still reads the arbitrary module.Runtime getter, so a broken module can still hang it -
        // and every caller of this path (close, tray Stop All, Windows session ending) runs on the
        // UI thread. Proves the bounded, asynchronous wrapper actually returns (with null, not a
        // thrown exception or an indefinite block) once its own timeout elapses, instead of
        // depending on the underlying synchronous enumeration ever finishing.
        var gate = new TaskCompletionSource<IReadOnlyList<InstalledServer>>();
        using var releaseGate = new CallbackOnDispose(() => gate.TrySetResult([]));
        var invocationCount = 0;
        var loader = CreateLoaderWithShutdownDelegate(() =>
        {
            Interlocked.Increment(ref invocationCount);
            return gate.Task.GetAwaiter().GetResult();
        });

        var result = await loader.TryLoadForShutdownAsync(TestShutdownTimeout);

        Assert.Null(result);
        Assert.Equal(1, invocationCount);

        // Let the abandoned worker finish so it doesn't leak past the end of this test.
        gate.SetResult([]);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_does_not_start_a_second_worker_while_one_is_still_pending()
    {
        // Regression guard for a P1 finding: "Avoid starting unlimited abandoned shutdown tasks on
        // repeated close attempts." Proves that calling TryLoadForShutdownAsync again while an
        // earlier enumeration is still stuck reuses that same in-flight attempt instead of starting
        // another abandoned Task.Run worker for every repeated close/tray-stop-all/session-ending
        // attempt.
        var gate = new TaskCompletionSource<IReadOnlyList<InstalledServer>>();
        using var releaseGate = new CallbackOnDispose(() => gate.TrySetResult([]));
        var invocationCount = 0;
        var loader = CreateLoaderWithShutdownDelegate(() =>
        {
            Interlocked.Increment(ref invocationCount);
            return gate.Task.GetAwaiter().GetResult();
        });

        var firstCall = loader.TryLoadForShutdownAsync(TestShutdownTimeout);
        var secondCall = loader.TryLoadForShutdownAsync(TestShutdownTimeout);
        var results = await Task.WhenAll(firstCall, secondCall);

        Assert.Null(results[0]);
        Assert.Null(results[1]);
        Assert.Equal(1, invocationCount);

        gate.SetResult([]);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_does_not_let_one_callers_cancellation_cancel_shared_work()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<InstalledServer>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseGate = new CallbackOnDispose(() => gate.TrySetResult([]));
        var invocationCount = 0;
        var loader = CreateLoaderWithShutdownDelegate(() =>
        {
            Interlocked.Increment(ref invocationCount);
            return gate.Task.GetAwaiter().GetResult();
        });
        using var firstCallerCancellation = new CancellationTokenSource();

        var firstCall = loader.TryLoadForShutdownAsync(
            TimeSpan.FromSeconds(5),
            firstCallerCancellation.Token);
        await WaitUntilAsync(
            () => Volatile.Read(ref invocationCount) == 1,
            $"Expected the shared shutdown enumeration to start within {PollBound}.");

        var secondCall = loader.TryLoadForShutdownAsync(TimeSpan.FromSeconds(5));
        firstCallerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCall);
        Assert.False(secondCall.IsCompleted);
        Assert.Equal(1, invocationCount);

        IReadOnlyList<InstalledServer> expected =
            [CreateFakeServer("shutdown-shared", "Shutdown Shared")];
        gate.SetResult(expected);

        var secondResult = await secondCall;
        Assert.Same(expected, secondResult);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_returns_the_real_result_when_enumeration_completes_within_the_timeout()
    {
        IReadOnlyList<InstalledServer> expected = [CreateFakeServer("shutdown-ok", "Shutdown OK")];
        var loader = CreateLoaderWithShutdownDelegate(() => expected);

        // This case verifies the successful result, not the deliberately-short timeout boundary.
        // A saturated full-suite/CI worker can take longer than 300 ms merely to schedule Task.Run.
        var result = await loader.TryLoadForShutdownAsync(PollBound);

        Assert.NotNull(result);
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_recovers_after_the_underlying_enumeration_faults()
    {
        // Regression guard for a P2 finding: TryLoadForShutdownAsync's own try/catch only caught
        // TimeoutException - a genuine fault (or a cancellation unrelated to this call's own
        // cancellationToken) reaching loadTask.WaitAsync propagated straight out of this method
        // uncaught, to a safety-critical shutdown/exit caller, AND (since neither the success path
        // nor the TimeoutException path ran) left _pendingShutdownLoad holding that same
        // permanently-terminal, already-faulted task forever - every subsequent call would just
        // re-await and rethrow the exact same exception, never actually retrying.
        var invocationCount = 0;
        var loader = CreateLoaderWithShutdownDelegate(() =>
        {
            var attempt = Interlocked.Increment(ref invocationCount);
            if (attempt == 1)
            {
                throw new InvalidOperationException("Simulated shutdown enumeration fault.");
            }

            return [CreateFakeServer("shutdown-recovered", "Shutdown Recovered")];
        });

        var firstResult = await loader.TryLoadForShutdownAsync(TestShutdownTimeout);
        Assert.Null(firstResult);

        // Wait for the single observer (attached once, at task-creation time) to clear the pending
        // slot before making the next call - otherwise this call could race ahead and reuse the same
        // already-faulted task directly via the dedup path, which would also correctly return null
        // but wouldn't prove a genuinely fresh retry actually happens afterward. Polled directly
        // rather than a fixed sleep, to avoid a flake risk on a loaded CI runner.
        await WaitUntilAsync(
            () => loader.GetPendingShutdownLoadForTests() == null,
            $"Expected the background observer to clear the pending shutdown-load slot within {PollBound}.");

        var secondResult = await loader.TryLoadForShutdownAsync(TestShutdownTimeout);

        Assert.NotNull(secondResult);
        var recovered = Assert.Single(secondResult!);
        Assert.Equal("Shutdown Recovered", recovered.Name);
        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public async Task TryLoadForShutdownAsync_clears_the_pending_slot_cleanly_after_repeated_timeouts()
    {
        // Regression guard for the other half of the same P2 finding: the old code attached a NEW
        // observer continuation to the same still-pending task on EVERY timeout, rather than exactly
        // once when the task was first created - so N repeated timeout calls against one stuck task
        // accumulated N redundant, wasteful observers. That accumulation has no separately-observable
        // WRONG end state on its own (each redundant observer's clear/log actions are idempotent), so
        // this test instead proves the practically important guarantee: repeated timeout calls
        // against the same stuck task don't leave the loader in a broken state once the work finally
        // completes - the pending slot ends up cleanly null (checked directly, not inferred), and the
        // next call starts a genuinely fresh attempt. "Exactly one observer is attached" itself is
        // structurally guaranteed by TryLoadForShutdownAsync only ever calling ObserveShutdownLoadAsync
        // from the single "no existing pending task" branch - confirmed by direct code reading.
        var gate = new TaskCompletionSource<IReadOnlyList<InstalledServer>>();
        using var releaseGate = new CallbackOnDispose(() => gate.TrySetResult([]));
        var invocationCount = 0;
        var loader = CreateLoaderWithShutdownDelegate(() =>
        {
            Interlocked.Increment(ref invocationCount);
            return gate.Task.GetAwaiter().GetResult();
        });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await loader.TryLoadForShutdownAsync(TestShutdownTimeout);
            Assert.Null(result);
        }

        Assert.Equal(1, invocationCount);

        gate.SetResult([CreateFakeServer("shutdown-late", "Shutdown Late")]);
        await WaitUntilAsync(
            () => loader.GetPendingShutdownLoadForTests() == null,
            $"Expected the pending shutdown-load slot to clear within {PollBound} once the underlying work completed.");

        var freshResult = await loader.TryLoadForShutdownAsync(TestShutdownTimeout);
        Assert.NotNull(freshResult);
        Assert.Equal(2, invocationCount);
    }

    [Fact]
    public async Task TryCacheLateResult_does_not_cache_when_a_newer_attempt_already_superseded_the_task()
    {
        // Regression guard for a P2 finding: ObserveLatePendingLoadAsync used to write to _lateResults
        // unconditionally whenever the awaited task completed successfully - it never checked whether
        // that task still actually owned the pending slot. A concurrent LoadAsync call can consume and
        // remove a timed-out task's entry directly (once it completes), and a fresh, genuinely newer
        // attempt can then be inserted under the same folder key before this observer gets around to
        // running. Caching the OLD task's result in that case would let a later refresh replay stale,
        // superseded data (including CanStop) instead of whatever the newer attempt actually found.
        var loader = CreateLoader();
        const string folderKey = "late-cache-superseded-folder";
        var oldTask = Task.FromResult(CreateFakeServer("old", "Old"));
        var newTask = Task.FromResult(CreateFakeServer("new", "New"));
        var healthyResult = CreateFakeServer(folderKey, "Should Not Be Cached");

        loader.SetPendingServerLoadForTests(folderKey, oldTask);
        loader.SetPendingServerLoadForTests(folderKey, newTask);

        var cached = await loader.TryCacheLateResultAsync(folderKey, oldTask, healthyResult);

        Assert.False(cached);
        // The newer task's ownership of the slot must be left untouched by the losing attempt.
        Assert.True(loader.TryGetPendingServerLoadForTests(folderKey, out var stillPending));
        Assert.Same(newTask, stillPending);
    }

    [Fact]
    public async Task TryCacheLateResult_caches_when_the_task_still_owns_the_pending_slot()
    {
        var folder = CreateEmptyServerFolder("late-cache-still-owned");
        var folderName = Path.GetFileName(folder);
        var task = Task.FromResult(CreateFakeServer(folderName, "Owned"));
        var healthyResult = CreateFakeServer(folderName, "Should Be Cached");
        var loader = CreateLoader();
        loader.SetPendingServerLoadForTests(folder, task);

        var cached = await loader.TryCacheLateResultAsync(folder, task, healthyResult);

        Assert.True(cached);
        Assert.False(loader.TryGetPendingServerLoadForTests(folder, out _));

        // End-to-end confirmation: a subsequent LoadAsync call for this folder must consume the
        // cached result directly (via _lateResults) without starting any fresh work at all.
        var load = await loader.LoadAsync();
        var found = load.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(found);
        Assert.Equal("Should Be Cached", found!.Name);
        Assert.Equal(0, _invocationCounts.GetValueOrDefault(folderName));
    }

    [Fact]
    public async Task LoadAsync_does_not_let_one_callers_cancellation_affect_a_shared_load_reused_by_another_caller()
    {
        // Regression guard for a P2 finding: the shared Task.Run backing a deduplicated per-server load
        // used to be created with (and passed) whichever caller's own token started it - so if that
        // specific caller's token was later canceled (e.g. a ServerInfoWindow health-refresh being
        // closed), the shared work itself would be torn down purely because of that unrelated
        // cancellation. This delegate directly observes whatever cancellationToken the shared work is
        // actually given: it hangs forever if given CancellationToken.None (the fix), or faults the
        // instant callerACts is canceled if given caller A's own token (the bug). Under the bug, that
        // fault reaches the background observer attached from caller A's own earlier timeout, which
        // then retires the pending slot (and clears the "confirmed stuck" flag) as an ordinary terminal
        // outcome - so caller B's later call finds NO pending entry at all and starts a whole new,
        // independent attempt from scratch, needlessly repeating work and re-waiting a full timeout,
        // even though caller B's own request was never canceled and nothing about the server itself
        // actually changed. Under the fix, the shared work is never affected by caller A's
        // cancellation, so caller B correctly finds it still genuinely pending and gets the fast
        // "confirmed stuck" short-circuit instead.
        var folder = CreateEmptyServerFolder("cross-caller-cancel");
        var folderName = Path.GetFileName(folder);

        async Task<InstalledServer> HangUntilCanceled(
            string serverFolder,
            IReadOnlyList<IGameServerModule> modules,
            CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CreateFakeServer(folderName, "Unreachable");
        }

        var loader = new InstalledServerLoader(new NeverCalledStatusService(), null, HangUntilCanceled, TestServerLoadTimeout);
        using var callerACts = new CancellationTokenSource();

        var firstLoad = await loader.LoadAsync(callerACts.Token);
        AssertTimedOutProblemCard(firstLoad, folder);

        // Simulate caller A's own token being canceled AFTER its call already returned (e.g. its
        // window closing) - the shared work, if wrongly tied to caller A's token, would fault almost
        // instantly; if correctly independent of it (the fix), nothing happens at all. There is no
        // state to poll for here - under the fix, the shared work never reacts to this cancellation,
        // so a bounded, generous fixed wait is the only way to give a (hypothetical) fault a fair
        // chance to propagate before checking caller B's behaviour.
        callerACts.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Caller B's own token is never canceled and must see the shared work exactly as if caller A's
        // cancellation had never happened: still genuinely pending, hitting the fast "confirmed stuck"
        // short-circuit - not a fresh "timed out" card from an unnecessary brand-new attempt.
        var secondLoad = await loader.LoadAsync(CancellationToken.None);
        var secondCard = secondLoad.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(secondCard);
        Assert.NotNull(secondCard!.LastOperationError);
        Assert.Contains(
            "still in progress from an earlier attempt",
            secondCard.LastOperationError,
            StringComparison.OrdinalIgnoreCase);
    }

    private InstalledServerLoader CreateLoader()
    {
        return new InstalledServerLoader(new NeverCalledStatusService(), null, LoadServerAsync, TestServerLoadTimeout);
    }

    private InstalledServerLoader CreateLoaderWithShutdownDelegate(Func<IReadOnlyList<InstalledServer>> loadForShutdown)
    {
        return new InstalledServerLoader(new NeverCalledStatusService(), null, LoadServerAsync, TestServerLoadTimeout, loadForShutdown);
    }

    private Task<InstalledServer> LoadServerAsync(
        string serverFolder,
        IReadOnlyList<IGameServerModule> modules,
        CancellationToken cancellationToken)
    {
        var folderName = Path.GetFileName(serverFolder);
        if (_customResults.TryGetValue(folderName, out var factory))
        {
            _invocationCounts.AddOrUpdate(folderName, 1, (_, count) => count + 1);
            return factory();
        }

        if (_hangGates.TryGetValue(folderName, out var gate))
        {
            _invocationCounts.AddOrUpdate(folderName, 1, (_, count) => count + 1);
            return gate.Task;
        }

        return Task.FromResult(CreateFakeServer(folderName, $"Server {folderName}"));
    }

    private static void AssertTimedOutProblemCard(IReadOnlyList<InstalledServer> servers, string hungFolder)
    {
        var folderName = Path.GetFileName(hungFolder);
        var problemCard = servers.FirstOrDefault(server => server.Id == folderName);
        Assert.NotNull(problemCard);
        Assert.NotNull(problemCard!.LastOperationError);
        Assert.Contains("timed out", problemCard.LastOperationError, StringComparison.OrdinalIgnoreCase);
    }

    private static InstalledServer CreateFakeServer(string id, string name)
    {
        return new InstalledServer(
            id,
            name,
            "unknown",
            "native",
            "",
            "",
            "",
            "--",
            "--",
            "",
            "public",
            "--",
            "--",
            "--",
            "--",
            "--",
            "Offline",
            "--",
            false,
            "",
            null,
            true,
            ServerRuntimeStatus.Offline,
            "Offline",
            "MutedBrush",
            false,
            "",
            "",
            "",
            true,
            true,
            true,
            false);
    }

    // Mirrors CreateFakeServer, but with a non-null LastOperationError - a stand-in for what
    // TryLoadAsync's own catch returns for a cancelled status query or similar failure, i.e. a card
    // that completed its Task normally rather than by throwing, but is not a healthy result.
    private static InstalledServer CreateProblemLikeServer(string id)
    {
        return CreateFakeServer(id, "Problem Card") with { LastOperationError = "Simulated late-arriving problem." };
    }

    private string CreateHealthyServerFolder(string name)
    {
        var folder = CreateEmptyServerFolder(name);
        return Path.GetFileName(folder);
    }

    private string CreateHungServerFolder(string name)
    {
        var folder = CreateEmptyServerFolder(name);
        _hangGates[Path.GetFileName(folder)] = new TaskCompletionSource<InstalledServer>();
        return folder;
    }

    // The load delegate returns an already-canceled task on its first invocation (simulating
    // Task.Run's own token already being canceled at schedule time, or the delegate itself returning
    // a canceled task - independent of the calling LoadAsync's own cancellationToken) and a real,
    // successful result on every subsequent invocation, so a test can prove both "doesn't throw
    // uncaught / gets a bounded problem card" and "actually retries and recovers."
    private string CreateCancelingThenRecoveringServerFolder(string name)
    {
        var folder = CreateEmptyServerFolder(name);
        var folderName = Path.GetFileName(folder);
        var callCount = 0;
        _customResults[folderName] = () => Interlocked.Increment(ref callCount) == 1
            ? Task.FromCanceled<InstalledServer>(new CancellationToken(canceled: true))
            : Task.FromResult(CreateFakeServer(folderName, "Recovered After Cancellation"));
        return folder;
    }

    // Same idea as above, but for a genuine fault (not a cancellation) on the first invocation.
    private string CreateFaultingThenRecoveringServerFolder(string name)
    {
        var folder = CreateEmptyServerFolder(name);
        var folderName = Path.GetFileName(folder);
        var callCount = 0;
        _customResults[folderName] = () => Interlocked.Increment(ref callCount) == 1
            ? Task.FromException<InstalledServer>(new InvalidOperationException("Simulated module fault."))
            : Task.FromResult(CreateFakeServer(folderName, "Recovered After Fault"));
        return folder;
    }

    private string CreateEmptyServerFolder(string name)
    {
        var folder = Path.Combine(_serversRoot, "hang-protection-test-" + name + "-" + Guid.NewGuid().ToString("N"));
        _serverFolders.Add(folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Dispose()
    {
        foreach (var (folderName, gate) in _hangGates)
        {
            gate.TrySetResult(CreateFakeServer(folderName, "Released during test cleanup"));
        }

        foreach (var folder in _serverFolders)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private sealed class CallbackOnDispose(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

    // The substituted load delegate replaces TryLoadAsync entirely for every folder this loader
    // scans, so IServerStatusService.GetStatusAsync (only ever called from inside the real
    // TryLoadAsync) must never be reached - if it were, that would mean the test seam isn't actually
    // bypassing the real per-server load path as intended.
    private sealed class NeverCalledStatusService : IServerStatusService
    {
        public Task<ServerStatusSnapshot> GetStatusAsync(
            IGameServerModule? module,
            ServerInstance instance,
            bool hasUpdateAvailable,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("GetStatusAsync should not be reached when the load delegate is substituted.");
        }

        public ServerStatusSnapshot? GetCachedStatus(string serverId) => null;
    }
}
