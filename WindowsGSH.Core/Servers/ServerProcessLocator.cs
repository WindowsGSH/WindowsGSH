using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public static class ServerProcessLocator
{
    private static readonly object PendingProbeSync = new();
    private static readonly Dictionary<string, Task<bool>> PendingProbes =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<Process> FindProcesses(
        IGameServerModule module,
        string installPath,
        Action<string>? log = null)
    {
        var expectedStartPath = Path.GetFullPath(Path.Combine(installPath, module.Runtime.StartPath));
        var expectedInstallPath = Path.GetFullPath(installPath);
        var processes = new List<Process>();
        var seenProcessIds = new HashSet<int>();

        var runtimeProcess = TryGetRuntimeStateProcess(expectedStartPath, expectedInstallPath, log);
        if (runtimeProcess != null)
        {
            processes.Add(runtimeProcess);
            seenProcessIds.Add(runtimeProcess.Id);
        }

        foreach (var processName in module.Runtime.ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (seenProcessIds.Contains(process.Id))
                {
                    process.Dispose();
                }
                else if (IsExpectedProcess(process, expectedStartPath, expectedInstallPath))
                {
                    processes.Add(process);
                    seenProcessIds.Add(process.Id);
                }
                else
                {
                    process.Dispose();
                }
            }
        }

        return processes;
    }

    public static bool IsRunning(IGameServerModule module, string installPath)
    {
        var processes = FindProcesses(module, installPath);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Count > 0;
    }

    // Bounds IsRunning against a broken module's Runtime getter (an arbitrary property read inside
    // FindProcesses) potentially hanging forever - the same hazard InstalledServerLoader.LoadForShutdown
    // already guards against for enumeration. Callers on a safety-critical shutdown path that has
    // already fallen back to a stale snapshot specifically because a fresh enumeration timed out must
    // not then re-probe that same server through this exact unbounded call - doing so would hang the
    // shutdown flow in the very module-failure scenario the fallback exists to survive. Returns null
    // (never a guessed true/false) on timeout or an unrelated fault, so the caller can apply its own
    // conservative handling rather than concluding the server has stopped.
    public static async Task<bool?> TryIsRunningAsync(
        IGameServerModule module,
        string installPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        var probeKey = BuildProbeKey(module, installPath);
        Task<bool> probeTask;
        lock (PendingProbeSync)
        {
            if (!PendingProbes.TryGetValue(probeKey, out probeTask!))
            {
                // The probe is shared across callers, so its lifetime must not be owned by whichever
                // caller happened to arrive first. Each caller's token only bounds its own wait below.
                probeTask = Task.Run(() => IsRunning(module, installPath), CancellationToken.None);
                PendingProbes[probeKey] = probeTask;
                _ = ObserveProbeAsync(probeKey, probeTask);
            }
        }

        try
        {
            return await probeTask
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task ObserveProbeAsync(string probeKey, Task<bool> probeTask)
    {
        try
        {
            await probeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // This diagnostic channel is not included in support bundles. Module exception text is
            // deliberately kept out of user-facing/application logs.
            Debug.WriteLine($"Server process probe for '{probeKey}' failed: {ex}");
        }
        finally
        {
            lock (PendingProbeSync)
            {
                if (PendingProbes.TryGetValue(probeKey, out var current) &&
                    ReferenceEquals(current, probeTask))
                {
                    PendingProbes.Remove(probeKey);
                }
            }
        }
    }

    internal static Task<bool>? GetPendingProbeForTests(IGameServerModule module, string installPath)
    {
        var probeKey = BuildProbeKey(module, installPath);
        lock (PendingProbeSync)
        {
            return PendingProbes.TryGetValue(probeKey, out var task) ? task : null;
        }
    }

    // The dedup key must identify BOTH the install path and the module - installPath alone would let
    // two callers checking the same path with different module.Id values (a misconfigured duplicate
    // server, or any future caller of this now-public method) silently share one probe and hand the
    // second caller the first caller's module's answer. module.Runtime is deliberately NOT read here -
    // that arbitrary property getter is exactly what TryIsRunningAsync exists to protect against
    // hanging, so building the key must never touch it.
    private static string BuildProbeKey(IGameServerModule module, string installPath) =>
        $"{Path.GetFullPath(installPath)}|{module.Id}";

    private static Process? TryGetRuntimeStateProcess(
        string expectedStartPath,
        string expectedInstallPath,
        Action<string>? log)
    {
        var serverFolder = Directory.GetParent(expectedInstallPath)?.FullName;
        if (string.IsNullOrWhiteSpace(serverFolder))
        {
            return null;
        }

        var runtimePath = Path.Combine(serverFolder, "runtime.json");
        if (!File.Exists(runtimePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(runtimePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("pid", out var pidElement) ||
                !pidElement.TryGetInt32(out var processId) ||
                processId <= 0)
            {
                return null;
            }

            var runtimeInstallPath = root.TryGetProperty("installPath", out var installPathElement) &&
                                     installPathElement.ValueKind == JsonValueKind.String
                ? installPathElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(runtimeInstallPath) ||
                !string.Equals(
                    Path.GetFullPath(runtimeInstallPath),
                    expectedInstallPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (IsRuntimePidStale(root, process))
            {
                process.Dispose();
                return null;
            }

            if (!MatchesRuntimeExecutable(root, process, expectedStartPath, expectedInstallPath))
            {
                log?.Invoke(
                    $"Runtime PID {processId} skipped because runtime.json executable did not match and install-folder containment could not be proven.");
                process.Dispose();
                return null;
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRuntimePidStale(JsonElement root, Process process)
    {
        if (!root.TryGetProperty("updatedUtc", out var updatedElement) ||
            updatedElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(updatedElement.GetString(), out var updatedUtc))
        {
            return false;
        }

        try
        {
            var processStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return processStartUtc > updatedUtc.ToUniversalTime().AddSeconds(5);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesRuntimeExecutable(
        JsonElement root,
        Process process,
        string expectedStartPath,
        string expectedInstallPath)
    {
        try
        {
            return MatchesRuntimeExecutable(
                root,
                process.MainModule?.FileName,
                expectedStartPath,
                expectedInstallPath);
        }
        catch
        {
            return false;
        }
    }

    internal static bool MatchesRuntimeExecutable(
        JsonElement root,
        string? actualExecutable,
        string expectedStartPath,
        string expectedInstallPath)
    {
        if (!root.TryGetProperty("executable", out var executableElement) ||
            executableElement.ValueKind != JsonValueKind.String)
        {
            return IsExpectedProcessPath(actualExecutable, expectedStartPath, expectedInstallPath);
        }

        var expectedExecutable = executableElement.GetString();
        if (string.IsNullOrWhiteSpace(expectedExecutable))
        {
            return IsExpectedProcessPath(actualExecutable, expectedStartPath, expectedInstallPath);
        }

        try
        {
            return !string.IsNullOrWhiteSpace(actualExecutable) &&
                string.Equals(
                    Path.GetFullPath(actualExecutable),
                    Path.GetFullPath(expectedExecutable),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExpectedProcess(Process process, string expectedStartPath, string expectedInstallPath)
    {
        try
        {
            if (IsExpectedProcessPath(process.MainModule?.FileName, expectedStartPath, expectedInstallPath))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsExpectedProcessPath(string? processPath, string expectedStartPath, string expectedInstallPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var fullProcessPath = Path.GetFullPath(processPath);
        var fullStartPath = Path.GetFullPath(expectedStartPath);
        var fullInstallPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedInstallPath));
        return string.Equals(fullProcessPath, fullStartPath, StringComparison.OrdinalIgnoreCase) ||
            fullProcessPath.StartsWith(fullInstallPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
