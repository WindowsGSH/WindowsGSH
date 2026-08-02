using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public interface IServerConsoleService
{
    Task<string> ExecuteModuleCommandAsync(
        IGameServerModule module,
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken);

    ObservableCollection<string> GetLog(string serverId);

    IReadOnlyList<string> GetLogSnapshot(string serverId);

    IReadOnlyList<ServerConsoleLine> GetLines(string serverId);

    string GetText(string serverId);

    string GetRecentText(string serverId, int maxLines);

    void Attach(string serverId, Process process);

    bool CanSendCommand(string serverId);

    bool IsConsoleInputDisabled(string serverId);

    void SendCommand(string serverId, string command);

    void AttachLogFile(string serverId, string path, CancellationToken cancellationToken);

    void Add(string serverId, string message);

    void Add(string serverId, string message, ServerConsoleStream stream);

    void RemoveServerState(string serverId);
}

public sealed class ServerConsoleService : IServerConsoleService
{
    private const int DefaultMaxLines = 500;

    private static readonly ServerConsoleService SharedInstance = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, ObservableCollection<string>> _logs = [];
    private readonly Dictionary<string, List<ServerConsoleLine>> _lines = [];
    private readonly Dictionary<string, Process> _attachedProcesses = [];
    private readonly Dictionary<string, string> _activeLogTails = [];
    private readonly Dictionary<string, RepeatedConsoleLine> _repeatedLines = [];
    private readonly Dictionary<string, ConsoleInputAttachment> _consoleInputAttachments = [];

    private readonly int _maxLines;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _sendCommandTimeout;
    private readonly IConsoleInputWriter _writer;

    public ServerConsoleService(int maxLines = DefaultMaxLines, Func<DateTimeOffset>? now = null, TimeSpan? sendCommandTimeout = null)
        : this(maxLines, now, sendCommandTimeout, DefaultConsoleInputWriter.Instance)
    {
    }

    // Test-only seam: WindowsGSH.Tests (via InternalsVisibleTo) can substitute a controlled writer
    // to deterministically observe and hold an in-flight write, instead of inferring "the write is
    // currently blocked" from wall-clock timing and payload size - both of which proved unreliable
    // under real CI load (see ServerConsoleServiceTests's own history for this exact test). No
    // production code path reaches this constructor; the public one above always passes
    // DefaultConsoleInputWriter.Instance, so production behaviour is unchanged.
    internal ServerConsoleService(int maxLines, Func<DateTimeOffset>? now, TimeSpan? sendCommandTimeout, IConsoleInputWriter writer)
    {
        _maxLines = Math.Max(1, maxLines);
        _now = now ?? (() => DateTimeOffset.Now);
        // Deliberately inlined rather than referencing a separate static readonly default field:
        // SharedInstance below is itself a static field initializer that runs this constructor, and
        // C# initializes static fields in textual declaration order - a static default field
        // declared after SharedInstance in this file would still be its type's zero-value (i.e.
        // TimeSpan.Zero) at the moment SharedInstance's own initializer runs, silently giving the
        // shared instance (used by every static convenience method - the one production code
        // actually calls) an effective zero-second timeout on every SendCommand. Caught by
        // Attach_captures_output_and_sends_stdin_commands failing immediately instead of a
        // deliberately slow negative test - confirmed by moving this to a static field and watching
        // that test fail in ~20ms instead of succeeding.
        _sendCommandTimeout = sendCommandTimeout ?? TimeSpan.FromSeconds(5);
        _writer = writer;
    }

    // Isolates the one actual I/O call SendCommand makes so tests can substitute a controlled,
    // deterministic stand-in for it (see the internal constructor above) instead of racing wall-
    // clock timing against a real OS pipe write.
    internal interface IConsoleInputWriter
    {
        void Write(Process process, string command);
    }

    // Internal rather than private specifically so WindowsGSH.Tests can exercise it directly and in
    // isolation, proving this seam is transparent to production behaviour - not just incidentally
    // covered by a broader SendCommand-level test.
    internal sealed class DefaultConsoleInputWriter : IConsoleInputWriter
    {
        public static readonly DefaultConsoleInputWriter Instance = new();

        public void Write(Process process, string command)
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }
    }

    public static IServerConsoleService Shared => SharedInstance;

    /// <summary>
    /// Fired (outside the internal gate lock) whenever a console line is added to any server.
    /// Subscribers receive the server ID and the new line; unsubscribe when done to avoid leaks.
    /// </summary>
    public static event Action<string, ServerConsoleLine>? NewLineAdded;

    public static Task<string> ExecuteModuleCommandAsync(
        IGameServerModule module,
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken)
    {
        return ((IServerConsoleService)SharedInstance).ExecuteModuleCommandAsync(module, instance, command, cancellationToken);
    }

    public static ObservableCollection<string> GetLog(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).GetLog(serverId);
    }

    public static IReadOnlyList<string> GetLogSnapshot(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).GetLogSnapshot(serverId);
    }

    public static IReadOnlyList<ServerConsoleLine> GetLines(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).GetLines(serverId);
    }

    public static string GetText(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).GetText(serverId);
    }

    public static string GetRecentText(string serverId, int maxLines)
    {
        return ((IServerConsoleService)SharedInstance).GetRecentText(serverId, maxLines);
    }

    public static void Attach(string serverId, Process process)
    {
        ((IServerConsoleService)SharedInstance).Attach(serverId, process);
    }

    public static bool CanSendCommand(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).CanSendCommand(serverId);
    }

    public static bool IsConsoleInputDisabled(string serverId)
    {
        return ((IServerConsoleService)SharedInstance).IsConsoleInputDisabled(serverId);
    }

    public static void SendCommand(string serverId, string command)
    {
        ((IServerConsoleService)SharedInstance).SendCommand(serverId, command);
    }

    public static void AttachLogFile(string serverId, string path, CancellationToken cancellationToken)
    {
        ((IServerConsoleService)SharedInstance).AttachLogFile(serverId, path, cancellationToken);
    }

    public static void Add(string serverId, string message)
    {
        ((IServerConsoleService)SharedInstance).Add(serverId, message);
    }

    public static void Add(string serverId, string message, ServerConsoleStream stream)
    {
        ((IServerConsoleService)SharedInstance).Add(serverId, message, stream);
    }

    async Task<string> IServerConsoleService.ExecuteModuleCommandAsync(
        IGameServerModule module,
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Console command cannot be blank.", nameof(command));
        }

        var strategy = module.Runtime.EffectiveConsoleStrategy;
        if (!ConsoleInputStrategyPolicy.SupportsConsoleCommandInput(module))
        {
            throw new NotSupportedException(ConsoleInputStrategyPolicy.GetCommandUnavailableMessage(module));
        }

        var trimmedCommand = command.Trim();
        if (strategy == ConsoleInputStrategy.Redirected &&
            ((IServerConsoleService)this).CanSendCommand(instance.Id))
        {
            // This method is async, but a caller awaiting it directly from a UI thread (e.g.
            // ServerInfoWindow's console command handler) would otherwise still run this branch
            // synchronously inline, since there's no genuine await before it - SendCommand's own
            // bounded wait (see its comment) prevents an indefinite freeze, but the calling thread
            // would still block for up to that timeout. Task.Run moves even that bounded wait off
            // the calling thread.
            await Task.Run(() => ((IServerConsoleService)this).SendCommand(instance.Id, trimmedCommand), cancellationToken).ConfigureAwait(false);
            return "Console command sent.";
        }

        if (module is not IModuleConsoleCommandCapability consoleModule)
        {
            throw new NotSupportedException(ConsoleInputStrategyPolicy.GetCommandUnavailableMessage(module));
        }

        return await consoleModule.ExecuteConsoleCommandAsync(instance, trimmedCommand, cancellationToken).ConfigureAwait(false);
    }

    ObservableCollection<string> IServerConsoleService.GetLog(string serverId)
    {
        lock (_gate)
        {
            return GetOrCreateLog(serverId);
        }
    }

    IReadOnlyList<string> IServerConsoleService.GetLogSnapshot(string serverId)
    {
        lock (_gate)
        {
            return _logs.TryGetValue(serverId, out var log) ? log.ToArray() : [];
        }
    }

    IReadOnlyList<ServerConsoleLine> IServerConsoleService.GetLines(string serverId)
    {
        lock (_gate)
        {
            return _lines.TryGetValue(serverId, out var lines)
                ? lines.ToArray()
                : [];
        }
    }

    string IServerConsoleService.GetText(string serverId)
    {
        lock (_gate)
        {
            // log is already the formatted, capped, up-to-date line buffer (same FormatLine
            // output, same trimming, same in-place repeat-summary rewrite as _lines) - no need
            // to maintain a second, redundant StringBuilder copy alongside it.
            return _logs.TryGetValue(serverId, out var log) && log.Count > 0
                ? string.Join(Environment.NewLine, log) + Environment.NewLine
                : string.Empty;
        }
    }

    string IServerConsoleService.GetRecentText(string serverId, int maxLines)
    {
        if (maxLines <= 0)
        {
            return string.Empty;
        }

        lock (_gate)
        {
            if (!_lines.TryGetValue(serverId, out var lines))
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, lines.TakeLast(maxLines).Select(FormatLine));
        }
    }

    void IServerConsoleService.Attach(string serverId, Process process)
    {
        AddCore(serverId, $"Attached to process {process.Id}.", ServerConsoleStream.System);
        if (process.StartInfo.RedirectStandardInput)
        {
            lock (_gate)
            {
                _attachedProcesses[serverId] = process;
                // Write serialization and poison state belong to this exact process attachment,
                // not merely to the reusable server id. A delayed Exited callback from an older
                // process must never clear or poison the replacement process's console state.
                _consoleInputAttachments[serverId] = new ConsoleInputAttachment(process);
            }
        }

        if (process.StartInfo.RedirectStandardOutput)
        {
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    AddCore(serverId, args.Data, ServerConsoleStream.Stdout);
                }
            };
            process.BeginOutputReadLine();
        }

        if (process.StartInfo.RedirectStandardError)
        {
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    AddCore(serverId, args.Data, ServerConsoleStream.Stderr);
                }
            };
            process.BeginErrorReadLine();
        }

        process.Exited += (_, _) =>
        {
            DetachProcess(serverId, process);
            AddCore(serverId, $"Process {process.Id} exited.", ServerConsoleStream.System);
        };
    }

    bool IServerConsoleService.CanSendCommand(string serverId)
    {
        lock (_gate)
        {
            return _attachedProcesses.TryGetValue(serverId, out var process) &&
                !process.HasExited &&
                process.StartInfo.RedirectStandardInput &&
                _consoleInputAttachments.TryGetValue(serverId, out var attachment) &&
                ReferenceEquals(attachment.Process, process) &&
                !attachment.IsUnusable;
        }
    }

    void IServerConsoleService.SendCommand(string serverId, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        Process process;
        ConsoleInputAttachment attachment;
        lock (_gate)
        {
            if (!_attachedProcesses.TryGetValue(serverId, out process!) ||
                process.HasExited ||
                !process.StartInfo.RedirectStandardInput)
            {
                throw new InvalidOperationException("No writable embedded console is attached for this server.");
            }

            if (!_consoleInputAttachments.TryGetValue(serverId, out attachment!) ||
                !ReferenceEquals(attachment.Process, process))
            {
                throw new InvalidOperationException("No writable embedded console is attached for this server.");
            }

            if (attachment.IsUnusable)
            {
                throw new InvalidOperationException("Console input for this server stopped responding to a previous command and has been disabled. Restart the server to restore console input.");
            }
        }

        // StreamWriter is not safe for concurrent access - without serializing here, two commands
        // sent close together could interleave their bytes on the wire even in the normal (fast,
        // non-hung) case. Waiting for the lock also counts against the overall timeout budget
        // below, so a queued command doesn't get an unbounded extra wait stacked on top of it.
        if (!attachment.WriteLock.Wait(_sendCommandTimeout))
        {
            if (!MarkConsoleInputUnusable(serverId, attachment))
            {
                throw new InvalidOperationException("The server process changed while the console command was waiting to be sent.");
            }

            throw new TimeoutException("Timed out waiting for a previous command to finish writing to the server's console input. The server may not be reading its console; console input has been disabled for this server.");
        }

        try
        {
            // The process can exit and a replacement can attach while this command waits for the
            // per-attachment semaphore. Revalidate after acquiring it so a queued command never
            // writes to, or poisons state on behalf of, a stale process.
            lock (_gate)
            {
                if (!_consoleInputAttachments.TryGetValue(serverId, out var currentAttachment) ||
                    !ReferenceEquals(currentAttachment, attachment) ||
                    !_attachedProcesses.TryGetValue(serverId, out var currentProcess) ||
                    !ReferenceEquals(currentProcess, process))
                {
                    throw new InvalidOperationException("The server process changed while the console command was waiting to be sent.");
                }

                if (attachment.IsUnusable)
                {
                    throw new InvalidOperationException("Console input for this server stopped responding to a previous command and has been disabled. Restart the server to restore console input.");
                }
            }

            // WriteLine/Flush on a redirected child process's stdin can block indefinitely if the
            // child has stopped reading from the pipe (hung, deadlocked, or simply never consuming
            // input) - and this method is called synchronously by callers that don't expect it to
            // block at all (ExecuteModuleCommandAsync's Redirected-strategy branch below calls it
            // with no await beforehand, and it's reachable directly from a UI thread). Running the
            // write on a background thread with a bounded wait keeps the common (fast) case exactly
            // as before while turning "freezes forever" into "fails after a few seconds" for the
            // pathological case, for every caller of this method, not just the UI one.
            var writeTask = Task.Run(() => _writer.Write(process, command));

            if (!writeTask.Wait(_sendCommandTimeout))
            {
                // Task.Wait timing out does not cancel the underlying write - the thread pool
                // worker stays blocked inside WriteLine/Flush until the process either starts
                // reading again or exits (which breaks the pipe and unblocks it with an
                // IOException that nobody observes directly, surfacing instead through
                // TaskScheduler.UnobservedTaskException). Left alone, every retry would leak
                // another blocked thread and risk two writes racing the same StreamWriter at once.
                // Marking this server's console input permanently unusable (until the process
                // exits/detaches - see DetachProcess) means this can only happen ONCE per hung
                // process: every future SendCommand call fails fast in the checks above, before
                // ever reaching this Task.Run or the semaphore.
                // Closing stdin below normally releases the abandoned write with an IOException.
                // Observe that expected late fault explicitly so it does not surface later through
                // TaskScheduler.UnobservedTaskException as misleading crash-log noise.
                _ = writeTask.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                if (!MarkConsoleInputUnusable(serverId, attachment))
                {
                    throw new InvalidOperationException("The server process changed while the console command was being sent.");
                }

                throw new TimeoutException("Timed out writing the command to the server's console input. The server may not be reading its console; console input has been disabled for this server.");
            }
        }
        finally
        {
            attachment.WriteLock.Release();
        }
    }

    // Called once, from SendCommand, the moment a write (or the wait for a previous write) times
    // out. Two things: mark the server so every future SendCommand/CanSendCommand fails fast
    // instead of attempting another write against a process that has already proven unresponsive,
    // and make a best-effort attempt to actively unblock the thread still stuck in the abandoned
    // write - closing the stream from another thread can release a pending write on the underlying
    // pipe handle. Not guaranteed to succeed for every I/O implementation (the process may have
    // already exited, or the abandoned write may complete on its own first) - this is a mitigation
    // on top of the fail-fast poison state, which is the actual fix.
    private bool MarkConsoleInputUnusable(string serverId, ConsoleInputAttachment attachment)
    {
        lock (_gate)
        {
            if (!_consoleInputAttachments.TryGetValue(serverId, out var currentAttachment) ||
                !ReferenceEquals(currentAttachment, attachment))
            {
                return false;
            }

            attachment.IsUnusable = true;
        }

        try
        {
            attachment.Process.StandardInput.Close();
        }
        catch
        {
        }

        return true;
    }

    void IServerConsoleService.AttachLogFile(string serverId, string path, CancellationToken cancellationToken)
    {
        var tailPath = NormalizeLogTailPath(path);
        lock (_gate)
        {
            if (_activeLogTails.TryGetValue(serverId, out var activePath) &&
                string.Equals(activePath, tailPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeLogTails[serverId] = tailPath;
        }

        AddCore(serverId, Directory.Exists(path) ? $"Tailing newest log file in: {path}" : $"Tailing log file: {path}", ServerConsoleStream.System);
        _ = Task.Run(async () =>
        {
            try
            {
                var position = 0L;
                string? activePath = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var currentPath = ResolveLogPath(path);
                        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
                        {
                            if (!string.Equals(activePath, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                activePath = currentPath;
                                position = 0;
                                AddCore(serverId, $"Now tailing: {currentPath}", ServerConsoleStream.System);
                            }

                            using var stream = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            if (stream.Length < position)
                            {
                                position = 0;
                            }

                            stream.Seek(position, SeekOrigin.Begin);
                            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                            while (!cancellationToken.IsCancellationRequested)
                            {
                                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                                if (line == null)
                                {
                                    break;
                                }

                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    AddCore(serverId, line, ServerConsoleStream.Log);
                                }
                            }

                            position = stream.Position;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AddCore(serverId, "Log tail failed: " + ex.Message, ServerConsoleStream.Stderr);
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (_activeLogTails.TryGetValue(serverId, out var activePath) &&
                        string.Equals(activePath, tailPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeLogTails.Remove(serverId);
                    }
                }
            }
        }, cancellationToken);
    }

    void IServerConsoleService.Add(string serverId, string message)
    {
        AddCore(serverId, message, ServerConsoleStream.System);
    }

    void IServerConsoleService.Add(string serverId, string message, ServerConsoleStream stream)
    {
        AddCore(serverId, message, stream);
    }

    // Mirrors ServerMetricsService.RemoveSeries - called from the delete flow so a deleted
    // server's console/log-tail state doesn't sit in these dictionaries for the rest of the
    // process lifetime. By the time delete runs the server must already be stopped, so
    // _attachedProcesses/_consoleInputAttachments should already be empty for it (DetachProcess
    // clears them on process exit) - removed here too, defensively, in case that ever isn't true.
    void IServerConsoleService.RemoveServerState(string serverId)
    {
        lock (_gate)
        {
            _logs.Remove(serverId);
            _lines.Remove(serverId);
            _repeatedLines.Remove(serverId);
            _activeLogTails.Remove(serverId);
            _attachedProcesses.Remove(serverId);
            _consoleInputAttachments.Remove(serverId);
        }
    }

    // Does not dispose `process`: ServerRuntimeTracker.MonitorServerProcess subscribes its own
    // Exited handler after this class's (see AttachStartedProcess), so it always runs last for
    // a given process and owns disposal there, after every consumer (including this one) has
    // read what it needs. Does not dispose the removed attachment's WriteLock either: SendCommand
    // calls WriteLock.Wait() outside of _gate, so a command already past the attachment lookup
    // above could still be about to wait on it when this runs; disposing here could turn a clean
    // TimeoutException into an unhandled ObjectDisposedException for that caller. WriteLock never
    // touches AvailableWaitHandle, so leaving it undisposed does not leak a real OS handle.
    private void DetachProcess(string serverId, Process process)
    {
        lock (_gate)
        {
            if (_attachedProcesses.TryGetValue(serverId, out var attachedProcess) &&
                ReferenceEquals(attachedProcess, process))
            {
                _attachedProcesses.Remove(serverId);
                if (_consoleInputAttachments.TryGetValue(serverId, out var attachment) &&
                    ReferenceEquals(attachment.Process, process))
                {
                    _consoleInputAttachments.Remove(serverId);
                }
            }
        }
    }

    bool IServerConsoleService.IsConsoleInputDisabled(string serverId)
    {
        lock (_gate)
        {
            return _consoleInputAttachments.TryGetValue(serverId, out var attachment) &&
                attachment.IsUnusable;
        }
    }

    private sealed class ConsoleInputAttachment(Process process)
    {
        public Process Process { get; } = process;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public bool IsUnusable { get; set; }
    }

    private void AddCore(string serverId, string message, ServerConsoleStream stream)
    {
        var line = new ServerConsoleLine(_now(), stream, message);
        ServerConsoleLine? lineToFire;
        lock (_gate)
        {
            var log = GetOrCreateLog(serverId);
            var lines = GetOrCreateLines(serverId);

            if (_repeatedLines.TryGetValue(serverId, out var repeated) &&
                repeated.Stream == stream &&
                string.Equals(repeated.Text, message, StringComparison.Ordinal))
            {
                var newCount = repeated.Count + 1;
                _repeatedLines[serverId] = repeated with { Count = newCount };
                var summaryText = $"Previous console line repeated {newCount - 1} more time(s).";

                if (newCount == 2)
                {
                    // First repeat: append a new summary entry and let it flow through the event system.
                    var summaryLine = new ServerConsoleLine(_now(), ServerConsoleStream.System, summaryText);
                    lines.Add(summaryLine);
                    log.Add(FormatLine(summaryLine));
                    while (lines.Count > _maxLines) lines.RemoveAt(0);
                    while (log.Count > _maxLines) log.RemoveAt(0);
                    lineToFire = summaryLine;
                }
                else
                {
                    // Subsequent repeats: update the summary entry already in place (last item).
                    // ObservableCollection indexer fires a Replace CollectionChanged so the WPF UI refreshes.
                    if (lines.Count > 0)
                    {
                        var updated = lines[^1] with { Text = summaryText };
                        lines[^1] = updated;
                        log[^1] = FormatLine(updated);
                    }

                    lineToFire = null;
                }
            }
            else
            {
                // Unique line: previous repeat run (if any) already wrote its summary in place — just add the new line.
                _repeatedLines[serverId] = new RepeatedConsoleLine(stream, message, 1);
                lines.Add(line);
                log.Add(FormatLine(line));
                while (lines.Count > _maxLines) lines.RemoveAt(0);
                while (log.Count > _maxLines) log.RemoveAt(0);
                lineToFire = line;
            }
        }

        // Fire outside the lock so subscribers cannot deadlock against internal state.
        if (lineToFire != null && ReferenceEquals(this, SharedInstance))
        {
            NewLineAdded?.Invoke(serverId, lineToFire);
        }
    }

    private ObservableCollection<string> GetOrCreateLog(string serverId)
    {
        if (!_logs.TryGetValue(serverId, out var log))
        {
            log = [];
            _logs[serverId] = log;
        }

        return log;
    }

    private List<ServerConsoleLine> GetOrCreateLines(string serverId)
    {
        if (!_lines.TryGetValue(serverId, out var lines))
        {
            lines = [];
            _lines[serverId] = lines;
        }

        return lines;
    }

    private static string? ResolveLogPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(path, "*.log")
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        return files.FirstOrDefault(file => file.Length > 0)?.FullName
            ?? files.FirstOrDefault()?.FullName;
    }

    private static string NormalizeLogTailPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string FormatLine(ServerConsoleLine line)
    {
        var prefix = line.Stream switch
        {
            ServerConsoleStream.Stderr => " [stderr]",
            ServerConsoleStream.Input => " [input]",
            ServerConsoleStream.Log => " [log]",
            _ => string.Empty
        };

        return $"[{line.Timestamp:HH:mm:ss}]{prefix} {line.Text}";
    }
}

public sealed record ServerConsoleLine(
    DateTimeOffset Timestamp,
    ServerConsoleStream Stream,
    string Text);

public enum ServerConsoleStream
{
    System,
    Stdout,
    Stderr,
    Input,
    Log
}

internal sealed record RepeatedConsoleLine(
    ServerConsoleStream Stream,
    string Text,
    int Count);
