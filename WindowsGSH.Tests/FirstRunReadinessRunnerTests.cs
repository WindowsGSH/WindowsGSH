using WindowsGSH.Core.Readiness;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class FirstRunReadinessRunnerTests
{
    [Fact]
    public async Task RunAsync_still_runs_readiness_when_setup_fails()
    {
        var readinessRun = false;
        Exception? setupFailure = null;
        var expected = new[]
        {
            ReadinessCheckResult.Warning("SteamCMD", "SteamCMD is not installed.")
        };
        var runner = new FirstRunReadinessRunner(
            () =>
            {
                readinessRun = true;
                return Task.FromResult<IReadOnlyList<ReadinessCheckResult>>(expected);
            },
            ex => setupFailure = ex);

        var results = await runner.RunAsync(() => throw new IOException("Network unavailable."));

        Assert.True(readinessRun);
        Assert.Same(expected, results);
        Assert.IsType<IOException>(setupFailure);
    }

    [Fact]
    public async Task RunAsync_returns_fresh_results_after_readiness_state_changes()
    {
        IReadOnlyList<ReadinessCheckResult> current =
        [
            ReadinessCheckResult.Warning("Modules", "No modules loaded.")
        ];
        var runner = new FirstRunReadinessRunner(() => Task.FromResult(current));

        var beforeImport = await runner.RunAsync();
        current =
        [
            ReadinessCheckResult.Pass("Modules", "Loaded 2 modules.")
        ];
        var afterImport = await runner.RunAsync();

        Assert.Equal(ReadinessStatus.Warning, Assert.Single(beforeImport).Status);
        Assert.Equal(ReadinessStatus.Pass, Assert.Single(afterImport).Status);
    }
}
