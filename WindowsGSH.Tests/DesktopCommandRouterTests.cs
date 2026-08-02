using WindowsGSH.Core.Windows;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DesktopCommandRouterTests
{
    [Fact]
    public async Task Routes_commands_without_notification_area()
    {
        var calls = new List<string>();
        var router = new DesktopCommandRouter(
            () => calls.Add("show"),
            () => calls.Add("discord"),
            () =>
            {
                calls.Add("stop");
                return Task.CompletedTask;
            },
            () => calls.Add("exit"));

        await router.ExecuteAsync(DesktopCommand.ShowWindowsGsh);
        await router.ExecuteAsync(DesktopCommand.OpenDiscordSettings);
        await router.ExecuteAsync(DesktopCommand.StopAllServers);
        await router.ExecuteAsync(DesktopCommand.Exit);

        Assert.Equal(["show", "discord", "stop", "exit"], calls);
    }
}
