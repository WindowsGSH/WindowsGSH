using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModuleStopStrategyRunnerTests
{
    [Fact]
    public void Graceful_only_mode_disables_default_kill_after_timeout()
    {
        var strategy = new ManifestStopStrategy();

        Assert.True(strategy.KillAfterTimeout);
        Assert.False(ModuleStopStrategyRunner.ShouldKillAfterTimeout(strategy, allowKill: false));
        Assert.False(ModuleStopStrategyRunner.ShouldExecuteKillStep(allowKill: false));
    }

    [Fact]
    public void Normal_stop_mode_preserves_manifest_kill_behavior()
    {
        var strategy = new ManifestStopStrategy { KillAfterTimeout = true };

        Assert.True(ModuleStopStrategyRunner.ShouldKillAfterTimeout(strategy, allowKill: true));
        Assert.True(ModuleStopStrategyRunner.ShouldExecuteKillStep(allowKill: true));
    }
}
