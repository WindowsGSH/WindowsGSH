using WindowsGSH.Core.Readiness;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class FirstRunChecklistComposerTests
{
    [Fact]
    public void Compose_without_completed_readiness_shows_checking_states()
    {
        var items = new FirstRunChecklistComposer().Compose(
            [],
            discordEnabled: false,
            discordTokenStored: false);

        Assert.Contains(items, item =>
            item.Label == "Modules" &&
            item.State == FirstRunChecklistState.Unknown &&
            item.StatusText == "Checking...");
        Assert.Contains(items, item =>
            item.Label == "SteamCMD" &&
            item.State == FirstRunChecklistState.Unknown);
        Assert.Contains(items, item =>
            item.Label == "Discord" &&
            item.State == FirstRunChecklistState.Optional);
    }

    [Fact]
    public void Compose_maps_readiness_and_optional_discord()
    {
        var readiness = new[]
        {
            ReadinessCheckResult.Pass("Modules", "Loaded 2 modules."),
            ReadinessCheckResult.Warning("SteamCMD", "Will be installed when needed."),
            ReadinessCheckResult.Info("Java", "Java is not currently required.")
        };

        var items = new FirstRunChecklistComposer().Compose(
            readiness,
            discordEnabled: false,
            discordTokenStored: false);

        Assert.Contains(items, item => item.Label == "Modules" && item.State == FirstRunChecklistState.Ready);
        Assert.Contains(items, item => item.Label == "SteamCMD" && item.State == FirstRunChecklistState.Attention);
        Assert.Contains(items, item => item.Label == "Java" && item.State == FirstRunChecklistState.Optional);
        Assert.Contains(items, item => item.Label == "Discord" && item.State == FirstRunChecklistState.Optional);
    }

    [Fact]
    public void Compose_uses_worst_java_result_and_configured_discord()
    {
        var readiness = new[]
        {
            ReadinessCheckResult.Pass("Modules", "Loaded."),
            ReadinessCheckResult.Pass("SteamCMD", "Ready."),
            ReadinessCheckResult.Pass("Java", "Java found."),
            ReadinessCheckResult.Fail("Paper Java readiness", "Java 21 is missing.")
        };

        var items = new FirstRunChecklistComposer().Compose(
            readiness,
            discordEnabled: true,
            discordTokenStored: true);

        Assert.Contains(items, item => item.Label == "Java" && item.State == FirstRunChecklistState.ActionRequired);
        Assert.Contains(items, item => item.Label == "Discord" && item.State == FirstRunChecklistState.Ready);
    }
}
