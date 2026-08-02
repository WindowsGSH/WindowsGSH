using WindowsGSH.Core.Diagnostics;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void Instance_name_is_stable_and_does_not_expose_source_key()
    {
        const string key = @"D:\Private\WindowsGSH|private-user";

        var first = SingleInstanceCoordinator.CreateInstanceName(key);
        var second = SingleInstanceCoordinator.CreateInstanceName(key);

        Assert.Equal(first, second);
        Assert.StartsWith("WindowsGSH-", first);
        Assert.DoesNotContain("Private", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-user", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Second_instance_is_rejected_and_can_signal_primary()
    {
        var key = "WindowsGSH.Tests." + Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator(key, activated.Set);
        using var secondary = new SingleInstanceCoordinator(key);

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);

        secondary.SignalPrimaryInstance();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Disposing_primary_allows_crash_replacement_to_become_primary()
    {
        var key = "WindowsGSH.Tests." + Guid.NewGuid().ToString("N");
        var primary = new SingleInstanceCoordinator(key);
        Assert.True(primary.IsPrimaryInstance);

        primary.Dispose();
        using var replacement = new SingleInstanceCoordinator(key);

        Assert.True(replacement.IsPrimaryInstance);
    }
}
