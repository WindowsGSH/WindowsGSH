using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using WindowsGSH.Core.Events;
using WindowsGSH.Data;

namespace WindowsGSH;

public static class AppLogService
{
    // A long-running server-manager session, especially one hitting a persistent, repeating
    // background error, would otherwise grow both Buffer and Messages forever - no existing caller
    // ever trims either. Bounded here the same way ServerConsoleService already bounds its own
    // per-server console lines (maxLines), just app-wide instead of per-server.
    internal const int MaxMessages = 5000;
    private const int PendingDatabaseLogCapacity = 2048;

    // Ingress cap for lines queued to the UI dispatcher, separate from MaxMessages (which bounds
    // the rendered/retained history). Protects against a log storm outrunning a busy UI thread -
    // without this, a burst arriving faster than the dispatcher can drain it would grow the queue
    // without limit.
    private const int MaxPendingLines = 2000;

    private static readonly object PendingLinesLock = new();
    private static readonly Queue<string> PendingLines = new();
    private static bool _dispatcherDrainScheduled;
    private static int _droppedPendingLines;

    private static readonly StringBuilder Buffer = new();
    private static readonly IDisposable EventBridge;
    private static readonly Channel<PendingDatabaseLog> DatabaseLogs =
        Channel.CreateBounded<PendingDatabaseLog>(new BoundedChannelOptions(PendingDatabaseLogCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private static readonly Task DatabaseLogWriter;
    private static long _totalMessagesAdded;
    private static int _pendingDispatcherUpdates;
    private static int _inMemoryMessageCount;

    public static ObservableCollection<string> Messages { get; } = [];

    public static string Text => Buffer.ToString();

    static AppLogService()
    {
        EventBridge = WindowsGshEventLogBridge.Connect(WindowsGshEventBus.Shared, Add);
        DatabaseLogWriter = Task.Run(ProcessDatabaseLogsAsync);
    }

    public static IProgress<string> CreateProgress(string? source = null)
    {
        return new Progress<string>(message => Add(message, source));
    }

    public static void Add(string message, string? source = null)
    {
        Interlocked.Increment(ref _totalMessagesAdded);
        var createdUtc = DateTimeOffset.UtcNow;
        // SQLite opens/inserts can wait on its busy timeout. Never perform that work on whichever
        // thread happened to log (frequently the WPF dispatcher); enqueue it to one serialized
        // background writer instead. The bounded channel prevents a pathological producer from
        // replacing an unbounded memory leak with an unbounded pending-write queue.
        DatabaseLogs.Writer.TryWrite(new PendingDatabaseLog(createdUtc, source, message));
        var sourceText = string.IsNullOrWhiteSpace(source) ? string.Empty : $" [{source}]";
        var line = $"[{DateTime.Now:HH:mm:ss}]{sourceText} {message}";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.CheckAccess())
        {
            AddLine(line);
            return;
        }

        // BeginInvoke, not Invoke: a background thread that's itself being synchronously waited on
        // by the UI thread (e.g. UI thread calls someTask.Wait()/.Result, and that work eventually
        // logs) would deadlock on a synchronous Invoke here - the UI thread can never reach the
        // dispatcher loop to process it while it's blocked waiting for the very background work
        // that's blocked waiting for the UI thread. BeginInvoke queues the update and returns
        // immediately, matching Invoke's effective ordering (both enqueue to the same dispatcher
        // queue) without the caller ever blocking on it.
        //
        // Coalesced, not one BeginInvoke per line: under a log storm, every Add used to queue its
        // own closure - unbounded pending dispatcher operations with a busy UI thread. Now lines
        // are enqueued here and only the Add that finds no drain already scheduled schedules one;
        // every other concurrent Add just enqueues and lets that single scheduled drain pick its
        // line up too, along with everything else queued by the time it runs.
        bool scheduleDrain;
        lock (PendingLinesLock)
        {
            if (PendingLines.Count >= MaxPendingLines)
            {
                PendingLines.Dequeue();
                Interlocked.Decrement(ref _pendingDispatcherUpdates);
                _droppedPendingLines++;
            }

            PendingLines.Enqueue(line);
            Interlocked.Increment(ref _pendingDispatcherUpdates);
            scheduleDrain = !_dispatcherDrainScheduled;
            _dispatcherDrainScheduled = true;
        }

        if (!scheduleDrain)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(DrainPendingLines);
        }
        catch (Exception ex)
        {
            lock (PendingLinesLock)
            {
                _dispatcherDrainScheduled = false;
            }

            // Logging can race application shutdown after the dispatcher has stopped accepting
            // work. The database entry was already queued above; a failed UI update must not
            // break the background server-control path that produced the log message.
            System.Diagnostics.Debug.WriteLine($"App log dispatcher update could not be queued: {ex}");
        }
    }

    private static void DrainPendingLines()
    {
        List<string> batch;
        int droppedCount;
        lock (PendingLinesLock)
        {
            batch = [.. PendingLines];
            PendingLines.Clear();
            droppedCount = _droppedPendingLines;
            _droppedPendingLines = 0;
            _dispatcherDrainScheduled = false;
        }

        if (droppedCount > 0)
        {
            TryAddUiLine($"[{droppedCount} earlier app log line(s) dropped - the UI dispatcher could not keep up.]");
        }

        foreach (var line in batch)
        {
            try
            {
                TryAddUiLine(line);
            }
            finally
            {
                // Keep diagnostics truthful even if a third-party CollectionChanged subscriber
                // throws while this particular line is being published. One bad subscriber must
                // not strand the remaining queue count or prevent later lines being attempted.
                Interlocked.Decrement(ref _pendingDispatcherUpdates);
            }
        }
    }

    private static void TryAddUiLine(string line)
    {
        try
        {
            AddLine(line);
        }
        catch (Exception ex)
        {
            // Logging must remain best-effort: ObservableCollection invokes external subscribers
            // synchronously, and one faulty UI subscriber must not break the coalesced drain.
            System.Diagnostics.Debug.WriteLine($"App log UI line could not be published: {ex}");
        }
    }

    public static string GetText(string? source = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Text;
        }

        var tag = $"[{source}]";
        var builder = new StringBuilder();
        foreach (var message in Messages.Where(message => message.Contains(tag, StringComparison.Ordinal)))
        {
            builder.AppendLine(message);
        }

        return builder.ToString();
    }

    public static string GetRecentText(int maxLines)
    {
        if (maxLines <= 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, Messages.TakeLast(maxLines));
    }

    internal static AppLogDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        return new AppLogDiagnosticsSnapshot(
            Interlocked.Read(ref _totalMessagesAdded),
            Volatile.Read(ref _pendingDispatcherUpdates),
            Volatile.Read(ref _inMemoryMessageCount),
            DatabaseLogs.Reader.CanCount ? DatabaseLogs.Reader.Count : null);
    }

    // internal (not private) so the trimming behaviour can be tested directly, without paying the
    // cost of AppLogRepository.Add's real SQLite write on every one of the several thousand calls
    // needed to actually exercise the MaxMessages boundary.
    internal static void AddLine(string line)
    {
        Buffer.AppendLine(line);
        Messages.Add(line);

        while (Messages.Count > MaxMessages)
        {
            var removed = Messages[0];
            Messages.RemoveAt(0);

            // Buffer is trimmed in lockstep so Text (Buffer.ToString()) stays consistent with
            // Messages rather than retaining the full untrimmed history forever regardless of the
            // cap above. AppendLine always writes text + Environment.NewLine, so removing that same
            // length from the front removes exactly the line that was just dropped from Messages.
            var removedLength = removed.Length + Environment.NewLine.Length;
            if (removedLength <= Buffer.Length)
            {
                Buffer.Remove(0, removedLength);
            }
        }

        Volatile.Write(ref _inMemoryMessageCount, Messages.Count);
    }

    public static async Task FlushAndStopAsync(TimeSpan timeout)
    {
        DatabaseLogs.Writer.TryComplete();
        await DatabaseLogWriter.WaitAsync(timeout).ConfigureAwait(false);
    }

    private static async Task ProcessDatabaseLogsAsync()
    {
        await foreach (var entry in DatabaseLogs.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            AppLogRepository.Add(entry.CreatedUtc, entry.Source, entry.Message);
        }
    }

    private sealed record PendingDatabaseLog(DateTimeOffset CreatedUtc, string? Source, string Message);
}

internal sealed record AppLogDiagnosticsSnapshot(
    long TotalMessagesAdded,
    int PendingDispatcherUpdates,
    int InMemoryMessageCount,
    int? PendingDatabaseWrites);
