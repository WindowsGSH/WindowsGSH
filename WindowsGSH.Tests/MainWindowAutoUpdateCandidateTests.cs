using Xunit;

namespace WindowsGSH.Tests;

public sealed class MainWindowAutoUpdateCandidateTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Scheduled_auto_updates_are_awaited_only_when_global_serialization_is_enabled(
        bool parallelServerOperations,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldAwaitScheduledAutoUpdates(parallelServerOperations));
    }

    [Fact]
    public void Loaded_module_remains_usable_when_installation_cannot_start_or_stop()
    {
        // InstalledServerLoader sets CanEditConfig directly from module != null. Start/stop state
        // is deliberately absent from this decision so an update can repair an invalid install.
        Assert.True(MainWindow.HasUsableModuleForAutoUpdate(canEditConfig: true));
    }

    [Fact]
    public void Problem_or_timeout_card_is_not_usable()
    {
        Assert.False(MainWindow.HasUsableModuleForAutoUpdate(canEditConfig: false));
    }

    [Fact]
    public void TryReadAutoUpdateEnabled_returns_false_instead_of_throwing_for_malformed_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windowsgsh-autoupdate-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            var result = MainWindow.TryReadAutoUpdateEnabled(path);

            Assert.False(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadAutoUpdateEnabled_returns_false_when_the_file_does_not_exist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windowsgsh-autoupdate-missing-{Guid.NewGuid():N}.json");

        var result = MainWindow.TryReadAutoUpdateEnabled(path);

        Assert.False(result);
    }

    [Fact]
    public void TryReadAutoUpdateEnabled_does_not_swallow_unexpected_argument_errors()
    {
        Assert.Throws<ArgumentNullException>(() => MainWindow.TryReadAutoUpdateEnabled(null!));
    }

    [Theory]
    [InlineData("""{"automation":{"autoUpdate":true}}""", true)]
    [InlineData("""{"automation":{"autoUpdate":false}}""", false)]
    [InlineData("""{"automation":{}}""", false)]
    [InlineData("""{}""", false)]
    public void TryReadAutoUpdateEnabled_reads_the_real_value_for_well_formed_config(string json, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"windowsgsh-autoupdate-valid-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);

            var result = MainWindow.TryReadAutoUpdateEnabled(path);

            Assert.Equal(expected, result);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
