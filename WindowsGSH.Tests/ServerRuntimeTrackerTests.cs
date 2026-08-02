using System.Diagnostics;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerRuntimeTrackerTests
{
    [Fact]
    public void GetLiveMonitoredProcessId_returns_a_running_process()
    {
        var tracker = CreateTracker();
        var processId = Environment.ProcessId;
        tracker.MarkProcessMonitored("server-1", processId);

        Assert.Equal(processId, tracker.GetLiveMonitoredProcessId("server-1"));
    }

    [Fact]
    public void GetLiveMonitoredProcessId_prunes_a_missing_process()
    {
        var tracker = CreateTracker();
        var missingProcessId = FindMissingProcessId();
        tracker.MarkProcessMonitored("server-1", missingProcessId);

        Assert.Null(tracker.GetLiveMonitoredProcessId("server-1"));
        Assert.False(tracker.HasLiveMonitoredProcess("server-1"));
    }

    private static ServerRuntimeTracker CreateTracker() => new(
        ServerConsoleService.Shared,
        new ServerCrashDiagnosticsService(),
        new ServerStatusComposer(TimeSpan.FromMinutes(10)),
        () => false,
        (_, _) => { },
        (_, _) => Task.CompletedTask,
        () => Task.CompletedTask);

    private static int FindMissingProcessId()
    {
        var candidate = int.MaxValue;
        while (candidate > 0)
        {
            try
            {
                using var process = Process.GetProcessById(candidate);
                candidate--;
            }
            catch
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find an unused process identifier for the test.");
    }
}
