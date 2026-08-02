using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Modules;

public static class ModuleStopStrategyRunner
{
    public static async Task StopAsync(
        IGameServerModule module,
        ModuleManifest manifest,
        ServerInstance instance,
        CancellationToken cancellationToken,
        bool allowKill = true)
    {
        var strategy = manifest.Runtime?.Stop ?? new ManifestStopStrategy { Strategy = "closeWindow" };
        var processes = ServerProcessLocator.FindProcesses(module, instance.InstallPath);
        ModuleLog.Add(
            $"Stop strategy scan for {instance.Name} (serverId={instance.Id}, moduleId={module.Id}, installPath={instance.InstallPath}, strategy={strategy.Strategy ?? "closeWindow"}, killAfterTimeout={strategy.KillAfterTimeout}). MatchedProcesses={processes.Count}.",
            instance.Id);

        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                ModuleLog.Add($"Applying stop strategy to process: {ServerProcessLogFormatter.Describe(process)}.", instance.Id);
                await ApplyStrategyAsync(process, strategy, instance, cancellationToken, allowKill);

                if (!process.HasExited && ShouldKillAfterTimeout(strategy, allowKill))
                {
                    try
                    {
                        ModuleLog.Add($"Stop strategy timeout; killing process tree: {ServerProcessLogFormatter.Describe(process)}; gracefulStopAttempted=True; killEntireProcessTree=True.", instance.Id);
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(cancellationToken);
                        ModuleLog.Add($"Stop strategy kill result: {ServerProcessLogFormatter.Describe(process)}; {ServerProcessLogFormatter.ExitResult(process)}.", instance.Id);
                    }
                    catch (Exception ex)
                    {
                        ModuleLog.Add(
                            $"Stop strategy kill failed: {ServerProcessLogFormatter.Describe(process)}; exception={ex}.",
                            instance.Id);
                        throw;
                    }
                }
            }
        }
    }

    private static async Task ApplyStrategyAsync(
        Process process,
        ManifestStopStrategy strategy,
        ServerInstance instance,
        CancellationToken cancellationToken,
        bool allowKill)
    {
        var wait = Math.Max(0, strategy.WaitMilliseconds);
        switch ((strategy.Strategy ?? "closeWindow").Trim().ToLowerInvariant())
        {
            case "steps":
                await ApplyStepsAsync(process, strategy.Steps ?? [], instance, cancellationToken, allowKill);
                wait = 0;
                break;

            case "consolecommand":
            case "consolecommands":
                await SendConsoleCommandsAsync(process, strategy.Commands ?? [], cancellationToken);
                break;

            case "windowcommand":
            case "windowcommands":
                SendWindowCommands(process, strategy.Commands ?? []);
                break;

            case "ctrlc":
                TrySendCtrlC(process);
                break;

            case "kill":
                wait = 0;
                break;

            case "closewindow":
            default:
                TryCloseMainWindow(process);
                break;
        }

        if (wait > 0 && !process.HasExited)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    private static async Task ApplyStepsAsync(
        Process process,
        IReadOnlyList<ManifestStopStep> steps,
        ServerInstance instance,
        CancellationToken cancellationToken,
        bool allowKill)
    {
        foreach (var step in steps)
        {
            if (process.HasExited)
            {
                return;
            }

            switch ((step.Type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "wait":
                    await Task.Delay(Math.Max(0, step.WaitMilliseconds), cancellationToken);
                    break;

                case "ctrlc":
                    TrySendCtrlC(process);
                    break;

                case "closewindow":
                    TryCloseMainWindow(process);
                    break;

                case "kill":
                    if (ShouldExecuteKillStep(allowKill))
                    {
                        await KillProcessAsync(process, instance, "Stop step requested kill", cancellationToken);
                    }
                    break;

                case "consolecommand":
                    await SendConsoleCommandsAsync(process, [step.Value ?? string.Empty], cancellationToken);
                    break;

                case "windowcommand":
                    SendWindowCommands(process, [step.Value ?? string.Empty]);
                    break;
            }
        }
    }

    internal static bool ShouldKillAfterTimeout(
        ManifestStopStrategy strategy,
        bool allowKill)
    {
        return allowKill && strategy.KillAfterTimeout;
    }

    internal static bool ShouldExecuteKillStep(bool allowKill)
    {
        return allowKill;
    }

    private static async Task KillProcessAsync(
        Process process,
        ServerInstance instance,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            ModuleLog.Add($"{reason}; killing process tree: {ServerProcessLogFormatter.Describe(process)}; gracefulStopAttempted=True; killEntireProcessTree=True.", instance.Id);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            ModuleLog.Add($"Stop strategy kill result: {ServerProcessLogFormatter.Describe(process)}; {ServerProcessLogFormatter.ExitResult(process)}.", instance.Id);
        }
        catch (Exception ex)
        {
            ModuleLog.Add(
                $"Stop strategy kill failed: {ServerProcessLogFormatter.Describe(process)}; exception={ex}.",
                instance.Id);
            throw;
        }
    }

    private static async Task SendConsoleCommandsAsync(Process process, IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        if (commands.Count == 0)
        {
            return;
        }

        if (!TryHasRedirectedStandardInput(process))
        {
            TryCloseMainWindow(process);
            return;
        }

        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
    }

    private static void TryCloseMainWindow(Process process)
    {
        try
        {
            process.CloseMainWindow();
        }
        catch
        {
        }
    }

    private static void SendWindowCommands(Process process, IReadOnlyList<string> commands)
    {
        // Some dedicated servers only support graceful shutdown through focused-window keystrokes.
        // Keep SendKeys as a supported fallback for those module stop strategies.
        TryFocusMainWindow(process);
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            if (string.Equals(command, "{ENTER}", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
            else
            {
                System.Windows.Forms.SendKeys.SendWait(command);
            }
        }
    }

    private static void TryFocusMainWindow(Process process)
    {
        try
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void TrySendCtrlC(Process process)
    {
        if (TryHasRedirectedStandardInput(process))
        {
            try
            {
                process.StandardInput.WriteLine("\u0003");
                process.StandardInput.Flush();
                return;
            }
            catch
            {
            }
        }

        TryCloseMainWindow(process);
    }

    private static bool TryHasRedirectedStandardInput(Process process)
    {
        try
        {
            return process.StartInfo.RedirectStandardInput;
        }
        catch
        {
            return false;
        }
    }
}
