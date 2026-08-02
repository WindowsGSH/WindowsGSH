using System.Diagnostics;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerConsoleServiceTests : IDisposable
{
    private readonly List<(int Id, DateTime StartTime)> _startedProcesses = [];
    private const string PowerShellPath = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe";

    [Fact]
    public async Task ExecuteAsync_sends_trimmed_command_to_console_capable_module()
    {
        var module = new ConsoleModule();
        var instance = CreateInstance();

        var result = await ServerConsoleService.ExecuteModuleCommandAsync(
            module,
            instance,
            " say hello ",
            CancellationToken.None);

        Assert.Equal("accepted:say hello", result);
        Assert.Equal("say hello", module.LastCommand);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_blank_commands()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ServerConsoleService.ExecuteModuleCommandAsync(
                new ConsoleModule(),
                CreateInstance(),
                "   ",
                CancellationToken.None));

        Assert.Equal("command", exception.ParamName);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_modules_without_console_capability()
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ServerConsoleService.ExecuteModuleCommandAsync(
                new NoConsoleModule(),
                CreateInstance(),
                "say hello",
                CancellationToken.None));

        Assert.Contains("does not expose console input", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_rcon_preferred_console_commands()
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ServerConsoleService.ExecuteModuleCommandAsync(
                new RconPreferredModule(),
                CreateInstance(),
                "status",
                CancellationToken.None));

        Assert.Contains("uses RCON", exception.Message);
    }

    [Fact]
    public async Task Attach_captures_output_and_sends_stdin_commands()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-attach-" + Guid.NewGuid().ToString("N");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"$line = [Console]::In.ReadLine(); Write-Output \\\"command:$line\\\"\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(process);
        ServerConsoleService.Attach(serverId, process);

        Assert.True(ServerConsoleService.CanSendCommand(serverId));
        ServerConsoleService.SendCommand(serverId, "save-all");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // WaitForExitAsync does not guarantee OutputDataReceived events have all fired when
        // BeginOutputReadLine is active. WaitForExit() (no timeout) flushes remaining events.
        process.WaitForExit();

        Assert.Contains("command:save-all", ServerConsoleService.GetText(serverId));
        Assert.False(ServerConsoleService.CanSendCommand(serverId));
    }

    [Fact]
    public void SendCommand_times_out_instead_of_blocking_forever_when_the_process_never_reads_stdin()
    {
        // Regression guard for a real bug: WriteLine/Flush on a redirected child's stdin had no
        // timeout at all - if the child stops reading (hung, deadlocked, or simply never consuming
        // input, as this test's own child process deliberately does by sleeping instead of reading),
        // the write blocks forever. In production that freezes whatever thread called SendCommand,
        // including the UI thread via ExecuteModuleCommandAsync's Redirected-strategy branch (there
        // was no await before that call). Uses a real child process and a payload large enough to
        // actually fill the OS pipe buffer, matching this file's existing Attach test's own use of a
        // real process rather than a mock for stdin/stdout behaviour.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-timeout-" + Guid.NewGuid().ToString("N");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(process);
        IServerConsoleService service = new ServerConsoleService(sendCommandTimeout: TimeSpan.FromMilliseconds(200));
        service.Attach(serverId, process);
        Assert.True(service.CanSendCommand(serverId));

        // The child never reads stdin at all, so once the OS pipe buffer fills, the underlying
        // write blocks - a large payload guarantees that happens well before the 200ms timeout.
        var largeCommand = new string('x', 5 * 1024 * 1024);

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<TimeoutException>(() => service.SendCommand(serverId, largeCommand));
        stopwatch.Stop();

        Assert.Contains("Timed out", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the short injected timeout to apply, took {stopwatch.Elapsed}.");

        process.Kill(entireProcessTree: true);
    }

    [Fact]
    public void SendCommand_disables_console_input_after_a_timeout_so_further_commands_fail_fast_instead_of_blocking_again()
    {
        // Regression guard for a real bug: a timed-out write left the underlying thread pool worker
        // blocked inside WriteLine/Flush forever (Task.Wait timing out does not cancel the task) -
        // and with nothing tracking that a server's console input had already proven unresponsive,
        // every retry would spawn another Task.Run against the same non-thread-safe StreamWriter,
        // leaking one more blocked thread per retry and risking two writes racing the same stream at
        // once. This test proves the actual fix: after one timeout, CanSendCommand reports false and
        // a second SendCommand call fails immediately (not after another full timeout wait) instead
        // of attempting - and potentially also hanging - another write.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-disable-" + Guid.NewGuid().ToString("N");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(process);
        IServerConsoleService service = new ServerConsoleService(sendCommandTimeout: TimeSpan.FromMilliseconds(200));
        service.Attach(serverId, process);

        var largeCommand = new string('x', 5 * 1024 * 1024);
        Assert.Throws<TimeoutException>(() => service.SendCommand(serverId, largeCommand));

        Assert.False(service.CanSendCommand(serverId));
        Assert.True(service.IsConsoleInputDisabled(serverId));

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<InvalidOperationException>(() => service.SendCommand(serverId, "save-all"));
        stopwatch.Stop();

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        // The whole point of the fix is that this second call does not attempt another write (and
        // therefore does not wait out another timeout) - it must fail close to instantly.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Expected the second call to fail fast without retrying the write, took {stopwatch.Elapsed}.");

        process.Kill(entireProcessTree: true);
    }

    [Fact]
    public async Task SendCommand_becomes_usable_again_once_a_new_process_is_attached_after_a_timeout()
    {
        // Complements the fail-fast test above: console input being unusable is a property of the
        // specific process that stopped responding, not a permanent property of the server id - a
        // restart (a new process attached under the same id) must restore normal SendCommand
        // behaviour, not stay poisoned forever because of what an earlier, now-gone process did.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-recover-" + Guid.NewGuid().ToString("N");
        using var hungProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(hungProcess);
        // Longer than the sibling test's 200ms: this test's second half writes a tiny command to a
        // freshly-started, healthy process, purely to prove the poison state cleared - unlike the
        // first assertion below, that write's success does not depend on the timeout being short,
        // only on the timeout being long enough to survive ordinary thread-pool scheduling jitter on
        // a loaded CI runner. The first assertion is unaffected by widening this: the hung process
        // below is asleep for 30 seconds and never reads at all, so a 5MB write against it genuinely
        // blocks (the OS pipe buffer fills almost immediately) regardless of how long the timeout is.
        IServerConsoleService service = new ServerConsoleService(sendCommandTimeout: TimeSpan.FromSeconds(2));
        service.Attach(serverId, hungProcess);

        var largeCommand = new string('x', 5 * 1024 * 1024);
        Assert.Throws<TimeoutException>(() => service.SendCommand(serverId, largeCommand));
        Assert.False(service.CanSendCommand(serverId));
        hungProcess.Kill(entireProcessTree: true);
        await hungProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Attaching a new process clears the poison state itself (see Attach), so this doesn't
        // need to wait for the old process's Exited event (which also clears it, via DetachProcess,
        // but is raised asynchronously by the framework on its own schedule) to fire first.
        using var readingProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"$line = [Console]::In.ReadLine(); Write-Output \\\"command:$line\\\"\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(readingProcess);
        service.Attach(serverId, readingProcess);

        Assert.True(service.CanSendCommand(serverId));
        Assert.False(service.IsConsoleInputDisabled(serverId));
        service.SendCommand(serverId, "save-all");
        await readingProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        readingProcess.WaitForExit();

        Assert.Contains("command:save-all", service.GetText(serverId));
    }

    [Fact]
    public async Task Old_process_exit_does_not_clear_the_replacement_processes_disabled_console_state()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-old-exit-" + Guid.NewGuid().ToString("N");
        using var oldProcess = CreateSleepingProcess();
        using var replacementProcess = CreateSleepingProcess();
        StartTracked(oldProcess);
        StartTracked(replacementProcess);

        IServerConsoleService service = new ServerConsoleService(sendCommandTimeout: TimeSpan.FromMilliseconds(200));
        service.Attach(serverId, oldProcess);
        service.Attach(serverId, replacementProcess);

        Assert.Throws<TimeoutException>(() =>
            service.SendCommand(serverId, new string('x', 5 * 1024 * 1024)));
        Assert.False(service.CanSendCommand(serverId));

        // The old process's Exited callback is deliberately late: it runs only after the
        // replacement is already attached and poisoned. It must not clear replacement state.
        oldProcess.Kill(entireProcessTree: true);
        await oldProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => service.GetText(serverId).Contains($"Process {oldProcess.Id} exited.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        Assert.False(service.CanSendCommand(serverId));

        replacementProcess.Kill(entireProcessTree: true);
    }

    [Fact]
    public async Task Command_waiting_on_an_old_attachment_does_not_poison_or_write_to_its_replacement()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-replaced-write-" + Guid.NewGuid().ToString("N");
        using var oldProcess = CreateSleepingProcess();
        StartTracked(oldProcess);
        using var replacementProcess = CreateSingleLineReaderProcess();
        StartTracked(replacementProcess);

        // Redesigned to remove wall-clock racing entirely. Two prior attempts each independently
        // failed under real CI load: process-start timing on the critical path, then (after fixing
        // that) a wider fixed timeout still raced Task.Delay/Task.Run scheduling jitter - and one
        // run failed with "no exception was thrown at all," proving the stale write isn't reliably
        // still in-flight after any fixed delay chosen in advance. ControlledConsoleInputWriter
        // intercepts only the old attachment's write and blocks it on a real synchronization
        // primitive, so "the write has started and is currently blocked" is an observed fact, not a
        // guess.
        //
        // The only timing-sensitive step left is Attach (below) landing before SendCommand's own
        // internal timeout - now an in-memory dictionary/lock operation on the same, already-running
        // thread that WriteStarted.Wait() just returned on, not an unpredictable OS process spawn or
        // thread-pool dispatch delay. That said, it is still not a hard guarantee: a GC pause or OS
        // scheduler preemption between WriteStarted returning and Attach executing could still, in
        // principle, take longer than a short timeout under genuine CI thread-pool starvation. A
        // deliberately generous timeout costs nothing here - staleCommand is awaited directly rather
        // than raced against a matching test-side delay, so a larger value only means this one test
        // takes a little longer to resolve, never a correctness risk - so there is no reason to keep
        // it tight now that the blocked-write condition itself is deterministic.
        var controlledWriter = new ControlledConsoleInputWriter(oldProcess);
        IServerConsoleService service = new ServerConsoleService(500, null, TimeSpan.FromSeconds(5), controlledWriter);
        service.Attach(serverId, oldProcess);

        var staleCommand = Task.Run(() => service.SendCommand(serverId, "stale-command"));
        try
        {
            Assert.True(controlledWriter.WriteStarted.Wait(TimeSpan.FromSeconds(10)), "The stale write never started.");

            // Fast, synchronous, in-memory swap.
            service.Attach(serverId, replacementProcess);

            // staleCommand resolves entirely on its own, driven by SendCommand's internal
            // writeTask.Wait(sendCommandTimeout) - the abandoned write (still blocked on
            // AllowWriteToFinish, released in the finally below) never has to actually complete for
            // this to happen. Since Attach above already ran before this timeout could fire, the
            // current attachment no longer matches the stale one by the time
            // MarkConsoleInputUnusable checks - making InvalidOperationException (not
            // TimeoutException) a guaranteed outcome here, not an incidental one.
            var staleException = await Assert.ThrowsAsync<InvalidOperationException>(() => staleCommand);
            Assert.Contains("process changed", staleException.Message, StringComparison.OrdinalIgnoreCase);

            // The invariant that actually matters: the stale command's outcome never poisoned the
            // replacement, which has its own independent attachment and write lock.
            Assert.True(service.CanSendCommand(serverId));

            service.SendCommand(serverId, "save-all");
            await replacementProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            replacementProcess.WaitForExit();
            Assert.Contains("command:save-all", service.GetText(serverId));
        }
        finally
        {
            // Must run even if an assertion above fails - the Task.Run worker executing the
            // intercepted write is otherwise blocked forever on AllowWriteToFinish.Wait() (which has
            // no timeout of its own), leaking a thread-pool worker into every subsequent test in
            // this process. Killing oldProcess cannot substitute for this: the blocked thread is
            // waiting on this test-owned event, not on any I/O tied to the process.
            controlledWriter.AllowWriteToFinish.Set();
        }

        if (!oldProcess.HasExited)
        {
            oldProcess.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Default_console_input_writer_still_performs_a_real_WriteLine_and_Flush()
    {
        // Regression guard for the IConsoleInputWriter seam introduced above: proves the seam is
        // transparent to production - the default writer used by every public ServerConsoleService
        // constructor still does exactly what SendCommand wrote inline before this seam existed.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-default-writer-" + Guid.NewGuid().ToString("N");
        using var process = CreateSingleLineReaderProcess();
        StartTracked(process);

        ServerConsoleService.DefaultConsoleInputWriter.Instance.Write(process, "save-all");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.WaitForExit();

        var output = process.StandardOutput.ReadToEnd();
        Assert.Contains("command:save-all", output);
    }

    [Fact]
    public async Task ExecuteAsync_sends_command_through_redirected_stdin_when_a_process_is_attached()
    {
        // Coverage for ExecuteModuleCommandAsync's Redirected-strategy branch, which had no test
        // at all before this change (the existing ExecuteAsync_sends_trimmed_command... test above
        // uses ConsoleModule, a WindowMessage-strategy module, so it never exercises this branch).
        // Directly relevant here since this branch is exactly what was changed to run SendCommand
        // via Task.Run instead of calling it inline - this proves that wrapping still returns the
        // expected result and the child process still actually receives the command.
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "server-1";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"$line = [Console]::In.ReadLine(); Write-Output \\\"command:$line\\\"\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(process);
        IServerConsoleService service = new ServerConsoleService();
        service.Attach(serverId, process);

        var result = await service.ExecuteModuleCommandAsync(
            new RedirectedConsoleModule(),
            CreateInstance(),
            "save-all",
            CancellationToken.None);

        Assert.Equal("Console command sent.", result);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.WaitForExit();

        Assert.Contains("command:save-all", service.GetText(serverId));
    }

    [Fact]
    public void Add_records_structured_lines_and_trims_to_configured_limit()
    {
        var now = new DateTimeOffset(2026, 5, 24, 17, 0, 0, TimeSpan.Zero);
        IServerConsoleService service = new ServerConsoleService(maxLines: 2, now: () => now);

        service.Add("server", "first", ServerConsoleStream.Stdout);
        service.Add("server", "second", ServerConsoleStream.Stderr);
        service.Add("server", "third", ServerConsoleStream.Log);

        var lines = service.GetLines("server");
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, line => line.Text == "first");
        Assert.Equal(ServerConsoleStream.Stderr, lines[0].Stream);
        Assert.Equal(ServerConsoleStream.Log, lines[1].Stream);
        Assert.Contains("[stderr] second", service.GetText("server"));
        Assert.Contains("[log] third", service.GetText("server"));
    }

    [Fact]
    public void Add_collapses_consecutive_duplicate_lines()
    {
        IServerConsoleService service = new ServerConsoleService(maxLines: 10);

        service.Add("server", "CreateBoundSocket: ::bind couldn't find an open port between 27015 and 27015", ServerConsoleStream.Stderr);
        service.Add("server", "CreateBoundSocket: ::bind couldn't find an open port between 27015 and 27015", ServerConsoleStream.Stderr);
        service.Add("server", "CreateBoundSocket: ::bind couldn't find an open port between 27015 and 27015", ServerConsoleStream.Stderr);
        service.Add("server", "Running Palworld dedicated server on :8211", ServerConsoleStream.Stdout);

        var lines = service.GetLines("server");
        Assert.Equal(3, lines.Count);
        Assert.Equal("CreateBoundSocket: ::bind couldn't find an open port between 27015 and 27015", lines[0].Text);
        Assert.Equal("Previous console line repeated 2 more time(s).", lines[1].Text);
        Assert.Equal("Running Palworld dedicated server on :8211", lines[2].Text);
    }

    [Fact]
    public void Repeated_line_count_is_visible_in_snapshots_before_next_different_line()
    {
        IServerConsoleService service = new ServerConsoleService(maxLines: 10);

        service.Add("server", "error line", ServerConsoleStream.Stderr);
        service.Add("server", "error line", ServerConsoleStream.Stderr);
        service.Add("server", "error line", ServerConsoleStream.Stderr);

        var lines = service.GetLines("server");
        Assert.Equal(2, lines.Count);
        Assert.Equal("error line", lines[0].Text);
        Assert.Contains("repeated 2 more time(s)", lines[1].Text);
        Assert.Equal(ServerConsoleStream.System, lines[1].Stream);

        Assert.Contains("repeated 2 more time(s)", service.GetText("server"));

        var snapshot = service.GetLogSnapshot("server");
        Assert.Equal(2, snapshot.Count);
        Assert.Contains("repeated 2 more time(s)", snapshot[1]);
    }

    [Fact]
    public async Task AttachLogFile_tails_new_lines_and_stops_after_cancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "server.log");
        await File.WriteAllTextAsync(logPath, "existing" + Environment.NewLine);
        IServerConsoleService service = new ServerConsoleService();
        using var cancellation = new CancellationTokenSource();

        service.AttachLogFile("server", logPath, cancellation.Token);
        await WaitUntilAsync(() => service.GetText("server").Contains("existing", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        await File.AppendAllTextAsync(logPath, "new-line" + Environment.NewLine);
        await WaitUntilAsync(() => service.GetText("server").Contains("new-line", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var before = service.GetText("server");
        await File.AppendAllTextAsync(logPath, "after-cancel" + Environment.NewLine);
        await Task.Delay(1200);

        Assert.Contains("new-line", before);
        Assert.DoesNotContain("after-cancel", service.GetText("server"));
    }

    [Fact]
    public async Task Attach_marks_stderr_lines_distinctly()
    {
        if (!File.Exists(PowerShellPath))
        {
            return;
        }

        var serverId = "console-stderr-" + Guid.NewGuid().ToString("N");
        IServerConsoleService service = new ServerConsoleService();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"[Console]::Error.WriteLine('bad-line')\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        StartTracked(process);
        service.Attach(serverId, process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => service.GetText(serverId).Contains("bad-line", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        Assert.Contains(service.GetLines(serverId), line => line.Stream == ServerConsoleStream.Stderr && line.Text == "bad-line");
        Assert.Contains("[stderr] bad-line", service.GetText(serverId));
    }

    private static ServerInstance CreateInstance()
    {
        return new ServerInstance(
            Id: "server-1",
            Name: "Test Server",
            ModuleId: "test",
            ServerFolder: "",
            InstallPath: "",
            ConfigPath: "",
            Settings: new Dictionary<string, object?>());
    }

    private static Process CreateSleepingProcess() =>
        new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

    private static Process CreateSingleLineReaderProcess() =>
        new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                Arguments = "-NoProfile -NonInteractive -Command \"$line = [Console]::In.ReadLine(); Write-Output \\\"command:$line\\\"\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

    // Intercepts writes to exactly one process, blocking on a real synchronization primitive
    // instead of the caller having to infer "the write is in-flight and blocked" from wall-clock
    // timing or payload size - both proved unreliable under real CI load for
    // Command_waiting_on_an_old_attachment_does_not_poison_or_write_to_its_replacement above. Any
    // OTHER process (e.g. a replacement attached mid-test) is written through immediately via the
    // same real WriteLine/Flush the default writer uses, so it is never affected by whether the
    // intercepted write has been released yet.
    private sealed class ControlledConsoleInputWriter(Process interceptedProcess) : ServerConsoleService.IConsoleInputWriter
    {
        public ManualResetEventSlim WriteStarted { get; } = new(initialState: false);

        public ManualResetEventSlim AllowWriteToFinish { get; } = new(initialState: false);

        public void Write(Process process, string command)
        {
            if (!ReferenceEquals(process, interceptedProcess))
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
                return;
            }

            WriteStarted.Set();
            AllowWriteToFinish.Wait();
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private void StartTracked(Process process)
    {
        process.Start();
        _startedProcesses.Add((process.Id, process.StartTime));
    }

    public void Dispose()
    {
        foreach (var started in _startedProcesses)
        {
            try
            {
                using var process = Process.GetProcessById(started.Id);
                if (process.StartTime != started.StartTime)
                {
                    continue;
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Best-effort failure-path cleanup. The test assertion remains the primary failure.
            }
        }
    }

    private class NoConsoleModule : IGameServerModule
    {
        public string Id => "test";
        public string Name => "Test Module";
        public string Version => "1.0.0";
        public virtual ModuleCapabilities Capabilities => new(
            SupportsInstall: false,
            SupportsUpdate: false,
            SupportsQuery: false,
            SupportsRcon: false,
            SupportsConsoleCommands: false,
            SupportsApiActions: false,
            SupportsBackups: false,
            SupportsDirectConnection: false);
        public virtual ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => [];
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, IsInstalled: false, IsEnabled: false, StatusText: "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("", "", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ConsoleModule : NoConsoleModule, IModuleConsoleCommandCapability
    {
        public override ModuleCapabilities Capabilities => base.Capabilities with { SupportsConsoleCommands = true };
        public override ModuleRuntimeDefinition Runtime =>
            new("server.exe", ["server"], ConsoleStrategy: ConsoleInputStrategy.WindowMessage);

        public string? LastCommand { get; private set; }

        public Task<string> ExecuteConsoleCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult($"accepted:{command}");
        }
    }

    private sealed class RedirectedConsoleModule : NoConsoleModule
    {
        public override ModuleRuntimeDefinition Runtime =>
            new("server.exe", ["server"], ConsoleStrategy: ConsoleInputStrategy.Redirected);
    }

    private sealed class RconPreferredModule : NoConsoleModule, IModuleConsoleCommandCapability
    {
        public override ModuleCapabilities Capabilities => base.Capabilities with { SupportsConsoleCommands = true };
        public override ModuleRuntimeDefinition Runtime =>
            new("server.exe", ["server"], ConsoleStrategy: ConsoleInputStrategy.RconPreferred);

        public Task<string> ExecuteConsoleCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) =>
            Task.FromResult("should not run");
    }
}
