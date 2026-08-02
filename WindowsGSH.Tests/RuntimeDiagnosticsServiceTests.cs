using System.Windows.Threading;
using WindowsGSH.Services;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class RuntimeDiagnosticsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "WindowsGSH-runtime-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dispatcher _dispatcher;
    private readonly Thread _dispatcherThread;

    public RuntimeDiagnosticsServiceTests()
    {
        Directory.CreateDirectory(_directory);
        (_dispatcher, _dispatcherThread) = StartDispatcherThread();
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _dispatcherThread.Join(TimeSpan.FromSeconds(5));

        // RuntimeDiagnosticsService.Dispose() only cancels - by design, it does not wait for an
        // in-flight sample write to finish (see the class's own Dispose()). So the background
        // task can still hold the diagnostics file open for a brief moment after each test's
        // service.Dispose() call returns; retry the cleanup instead of failing on that harmless
        // race.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public async Task Prunes_expired_files_on_first_sample_but_keeps_files_within_retention()
    {
        var referenceDay = new DateTimeOffset(2024, 1, 20, 9, 0, 0, TimeSpan.Zero);
        var expiredPath = Path.Combine(_directory, "runtime-2023-12-01.jsonl");
        var freshPath = Path.Combine(_directory, "runtime-2024-01-19.jsonl");
        File.WriteAllText(expiredPath, "{}");
        File.WriteAllText(freshPath, "{}");
        File.SetLastWriteTimeUtc(expiredPath, new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(freshPath, referenceDay.UtcDateTime.AddDays(-1));

        var service = new RuntimeDiagnosticsService(
            _dispatcher,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            directory: _directory,
            now: () => referenceDay);

        service.SetEnabled(true);
        try
        {
            var pruned = await WaitUntilTrueAsync(() => !File.Exists(expiredPath), TimeSpan.FromSeconds(5));
            Assert.True(pruned, "Expected the file older than the retention window to be pruned.");
            Assert.True(File.Exists(freshPath), "Expected the file within the retention window to survive.");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Re_prunes_once_the_day_rolls_over_instead_of_only_at_startup()
    {
        // Regression guard: PruneOldFiles used to run exactly once, at task startup, and never
        // again for the life of the sampling loop - on an app instance left running for weeks
        // without the setting being toggled, the 14-day retention silently stopped being enforced
        // after the very first prune. now() is injected so the "day" can be advanced deterministically
        // instead of waiting on the real clock to cross midnight.
        var day1 = new DateTimeOffset(2024, 1, 20, 23, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var currentDayTicks = day1.UtcTicks;

        // Already outside day1's retention window (cutoff 2024-01-06) - proves the first prune runs.
        var expiredOnDay1 = Path.Combine(_directory, "runtime-2023-12-01.jsonl");
        // Inside day1's retention window but falls outside day2's (cutoff 2024-01-07) - proves
        // pruning actually re-runs once the day changes, not only once at startup.
        var expiresOnlyOnDay2 = Path.Combine(_directory, "runtime-2024-01-06.jsonl");
        File.WriteAllText(expiredOnDay1, "{}");
        File.WriteAllText(expiresOnlyOnDay2, "{}");
        File.SetLastWriteTimeUtc(expiredOnDay1, new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(expiresOnlyOnDay2, new DateTime(2024, 1, 6, 0, 0, 0, DateTimeKind.Utc));

        var service = new RuntimeDiagnosticsService(
            _dispatcher,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            directory: _directory,
            now: () => new DateTimeOffset(Interlocked.Read(ref currentDayTicks), TimeSpan.Zero));

        service.SetEnabled(true);
        try
        {
            var prunedOnDay1 = await WaitUntilTrueAsync(() => !File.Exists(expiredOnDay1), TimeSpan.FromSeconds(5));
            Assert.True(prunedOnDay1, "Expected the file already expired on day 1 to be pruned at startup.");
            Assert.True(File.Exists(expiresOnlyOnDay2), "This file should still be within day 1's retention window.");

            Interlocked.Exchange(ref currentDayTicks, day2.UtcTicks);

            var prunedOnDay2 = await WaitUntilTrueAsync(() => !File.Exists(expiresOnlyOnDay2), TimeSpan.FromSeconds(5));
            Assert.True(prunedOnDay2, "Expected the file to be pruned once the day rolled over and pruning re-ran.");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Sampling_survives_a_write_failure_and_keeps_retrying()
    {
        // Regression guard: a single failed sample write (a transient file lock, a full disk, etc.)
        // used to propagate out of the sampling loop's outer try and stop diagnostics collection for
        // the rest of the process's life. Pre-creating a directory at the exact path the service
        // will try to write to deterministically forces every sample write to fail, so we can assert
        // the loop keeps retrying rather than giving up after the first failure.
        var referenceDay = new DateTimeOffset(2024, 1, 20, 9, 0, 0, TimeSpan.Zero);
        var blockedFilePath = Path.Combine(_directory, $"runtime-{referenceDay:yyyy-MM-dd}.jsonl");
        Directory.CreateDirectory(blockedFilePath);

        var service = new RuntimeDiagnosticsService(
            _dispatcher,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            directory: _directory,
            now: () => referenceDay);

        service.SetEnabled(true);
        try
        {
            // Allow at least one write attempt to hit the deliberately blocked path, then remove
            // the obstruction. Successful file creation proves the same sampling loop recovered
            // and retried without requiring repeated failure messages in the app log.
            await Task.Delay(100);
            Directory.Delete(blockedFilePath);
            var recovered = await WaitUntilTrueAsync(
                () => File.Exists(blockedFilePath) && new FileInfo(blockedFilePath).Length > 0,
                TimeSpan.FromSeconds(5));

            Assert.True(
                recovered,
                "Expected diagnostics sampling to recover and write a sample after the obstruction was removed.");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void Cannot_be_reenabled_after_disposal()
    {
        var service = new RuntimeDiagnosticsService(
            _dispatcher,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            directory: _directory);

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.SetEnabled(true));
    }

    [Fact]
    public async Task Rapid_disable_and_reenable_resumes_sampling()
    {
        var service = new RuntimeDiagnosticsService(
            _dispatcher,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            directory: _directory);

        service.SetEnabled(true);
        service.SetEnabled(false);
        service.SetEnabled(true);
        try
        {
            var resumed = await WaitUntilTrueAsync(
                () => Directory.EnumerateFiles(_directory, "runtime-*.jsonl")
                    .Any(path => new FileInfo(path).Length > 0),
                TimeSpan.FromSeconds(5));

            Assert.True(resumed, "Expected sampling to resume after a rapid disable/re-enable.");
        }
        finally
        {
            service.Dispose();
        }
    }

    private static async Task<bool> WaitUntilTrueAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static (Dispatcher Dispatcher, Thread Thread) StartDispatcherThread()
    {
        var startup = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startupCancelled = 0;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            startup.TrySetResult(dispatcher);
            if (Volatile.Read(ref startupCancelled) == 0)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!startup.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            Interlocked.Exchange(ref startupCancelled, 1);
            if (startup.Task.IsCompletedSuccessfully)
            {
                startup.Task.Result.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            thread.Join(TimeSpan.FromSeconds(5));
            throw new TimeoutException("The test dispatcher thread did not initialize within five seconds.");
        }
        return (startup.Task.Result, thread);
    }
}
