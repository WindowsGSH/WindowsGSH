namespace WindowsGSH.Core.Windows;

public enum DesktopCommand
{
    ShowWindowsGsh,
    OpenDiscordSettings,
    StopAllServers,
    Exit
}

public sealed class DesktopCommandRouter(
    Action showWindowsGsh,
    Action openDiscordSettings,
    Func<Task> stopAllServers,
    Action exit)
{
    public Task ExecuteAsync(DesktopCommand command)
    {
        switch (command)
        {
            case DesktopCommand.ShowWindowsGsh:
                showWindowsGsh();
                break;
            case DesktopCommand.OpenDiscordSettings:
                openDiscordSettings();
                break;
            case DesktopCommand.StopAllServers:
                return stopAllServers();
            case DesktopCommand.Exit:
                exit();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }

        return Task.CompletedTask;
    }
}
