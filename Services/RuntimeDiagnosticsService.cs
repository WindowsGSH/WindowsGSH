using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using WindowsGSH.Core;

namespace WindowsGSH.Services;

internal sealed class RuntimeDiagnosticsService : IDisposable
{
    private const int RetentionDays = 14;
    private static readonly TimeSpan FailureReminderInterval = TimeSpan.FromMinutes(15);
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _sampleInterval;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _samplingTask;
    private bool _enabled;
    private bool _disposed;
    private long _lastTotalMessages;
    private DateTimeOffset _lastSampledAt;

    public RuntimeDiagnosticsService(
        Dispatcher dispatcher,
        TimeSpan? sampleInterval = null,
        string? directory = null,
        Func<DateTimeOffset>? now = null)
    {
        _dispatcher = dispatcher;
        _sampleInterval = sampleInterval ?? TimeSpan.FromMinutes(1);
        _directory = directory ?? AppPaths.GetPath("logs", "diagnostics");
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                var cancellationToStop = _cancellation;
                _cancellation = null;
                cancellationToStop?.Cancel();
                return;
            }

            var newSamplingCancellation = new CancellationTokenSource();
            var previousTask = _samplingTask;
            _cancellation = newSamplingCancellation;
            _samplingTask = RunAfterPreviousAsync(previousTask, newSamplingCancellation);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _enabled = false;
            var cancellation = _cancellation;
            _cancellation = null;
            cancellation?.Cancel();
        }
    }

    // For best-effort shutdown: Dispose() above only cancels, by design (see its own history in
    // RuntimeDiagnosticsServiceTests), so a caller that wants the last in-flight sample to actually
    // finish writing before the app exits needs a way to wait for it. Just an accessor for whatever
    // task is currently running - callers are expected to call Dispose() first to request the
    // cancellation this then waits on.
    public Task WaitForStopAsync()
    {
        lock (_sync)
        {
            return _samplingTask ?? Task.CompletedTask;
        }
    }

    private async Task RunAfterPreviousAsync(
        Task? previousTask,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (previousTask != null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Previous runtime diagnostics task failed: {ex}");
                }
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastPrunedDate = DateTime.MinValue;
        string? lastFailureKey = null;
        var lastFailureReportedAt = default(DateTimeOffset);
        using var timer = new PeriodicTimer(_sampleInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var today = _now().UtcDateTime.Date;
                if (today != lastPrunedDate)
                {
                    PruneOldFiles();
                    lastPrunedDate = today;
                }

                await WriteSampleAsync(cancellationToken).ConfigureAwait(false);
                if (lastFailureKey != null)
                {
                    ReportRecovery();
                    lastFailureKey = null;
                    lastFailureReportedAt = default;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var now = _now();
                var failureKey = ex.GetType().FullName ?? ex.GetType().Name;
                if (!string.Equals(lastFailureKey, failureKey, StringComparison.Ordinal) ||
                    now - lastFailureReportedAt >= FailureReminderInterval)
                {
                    ReportSampleFailure(ex);
                    lastFailureKey = failureKey;
                    lastFailureReportedAt = now;
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private static void ReportSampleFailure(Exception exception)
    {
        Debug.WriteLine($"Runtime diagnostics sample failed: {exception}");
        try
        {
            AppLogService.Add(
                $"Runtime diagnostics sample failed ({exception.GetType().Name}); collection will retry.");
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine($"Could not report runtime diagnostics failure: {loggingException}");
        }
    }

    private static void ReportRecovery()
    {
        try
        {
            AppLogService.Add("Runtime diagnostics sampling recovered.");
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine($"Could not report runtime diagnostics recovery: {loggingException}");
        }
    }

    private async Task WriteSampleAsync(CancellationToken cancellationToken)
    {
        var dispatcherQueuedAt = Stopwatch.GetTimestamp();
        await _dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Background,
            cancellationToken);
        var dispatcherLatency = Stopwatch.GetElapsedTime(dispatcherQueuedAt);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var now = _now();
        var appLog = AppLogService.GetDiagnosticsSnapshot();
        var elapsed = _lastSampledAt == default ? TimeSpan.Zero : now - _lastSampledAt;
        var addedSinceLastSample = Math.Max(0, appLog.TotalMessagesAdded - _lastTotalMessages);
        var logMessagesPerMinute = elapsed.TotalSeconds > 0
            ? addedSinceLastSample / elapsed.TotalMinutes
            : 0;
        var gcInfo = GC.GetGCMemoryInfo();
        var sample = new RuntimeDiagnosticSample(
            TimestampUtc: now,
            ProcessUptimeSeconds: (now - process.StartTime.ToUniversalTime()).TotalSeconds,
            WorkingSetBytes: process.WorkingSet64,
            PrivateMemoryBytes: process.PrivateMemorySize64,
            VirtualMemoryBytes: process.VirtualMemorySize64,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            GcHeapSizeBytes: gcInfo.HeapSizeBytes,
            GcFragmentedBytes: gcInfo.FragmentedBytes,
            TotalAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            HandleCount: process.HandleCount,
            ThreadCount: process.Threads.Count,
            AppLogMessagesInMemory: appLog.InMemoryMessageCount,
            AppLogMessagesPerMinute: logMessagesPerMinute,
            PendingAppLogDispatcherUpdates: appLog.PendingDispatcherUpdates,
            PendingDatabaseLogWrites: appLog.PendingDatabaseWrites,
            UiDispatcherLatencyMilliseconds: dispatcherLatency.TotalMilliseconds);

        var path = Path.Combine(_directory, $"runtime-{now:yyyy-MM-dd}.jsonl");
        var json = JsonSerializer.Serialize(sample);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
        _lastTotalMessages = appLog.TotalMessagesAdded;
        _lastSampledAt = now;
    }

    private void PruneOldFiles()
    {
        var cutoff = _now().UtcDateTime.Date.AddDays(-RetentionDays);
        foreach (var path in Directory.EnumerateFiles(_directory, "runtime-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not prune runtime diagnostic file: {ex.Message}");
            }
        }
    }
}

internal sealed record RuntimeDiagnosticSample(
    DateTimeOffset TimestampUtc,
    double ProcessUptimeSeconds,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long VirtualMemoryBytes,
    long ManagedHeapBytes,
    long GcHeapSizeBytes,
    long GcFragmentedBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int HandleCount,
    int ThreadCount,
    int AppLogMessagesInMemory,
    double AppLogMessagesPerMinute,
    int PendingAppLogDispatcherUpdates,
    int? PendingDatabaseLogWrites,
    double UiDispatcherLatencyMilliseconds);
