namespace WindowsGSH.Core.Servers;

public static class ServerActionMessageFormatter
{
    public static string ServerNotFound(string serverId) => $"Server `{serverId}` was not found.";

    public static string UnknownAction(string action) => $"Unknown action `{action}`.";

    public static string CannotStart(string serverName, string statusText) =>
        $"{serverName} cannot be started while it is {statusText}.";

    public static string AlreadyBusy(string serverName) => $"{serverName} is already busy.";

    public static string AlreadyOffline(string serverName) => $"{serverName} is already offline.";

    public static string IsOffline(string serverName) => $"{serverName} is offline.";

    public static string StopBeforeUpdating(string serverName) => $"Stop {serverName} before updating it.";

    public static string StopBeforeBackup(string serverName) => $"Stop {serverName} before creating a backup.";

    public static string BackupsUnsupported(string serverName) => $"{serverName} does not support backups.";

    public static string StartCommandSent(string serverName) => $"{serverName} start command sent.";

    public static string Stopped(string serverName) => $"{serverName} stopped.";

    public static string RestartCommandSent(string serverName) => $"{serverName} restart command sent.";

    public static string UpdateCompleted(string serverName) => $"{serverName} update completed.";

    public static string BackupCreated(string serverName, string backupFileName) =>
        $"{serverName} backup created: {backupFileName}";

    public static string RconCommandSent(string serverName) => $"{serverName} command sent.";

    public static string ConsoleCommandSent(string serverName) => $"{serverName} console command sent.";

    public static string SendUsage(string prefix, string serverId, string action = "send") =>
        $"Usage: `{prefix}wgsh {action} {serverId} <command>`";

    public static string FailedToStart(string serverName, string error) => $"Failed to start {serverName}: {error}";

    public static string FailedToStop(string serverName, string error) => $"Failed to stop {serverName}: {error}";

    public static string FailedToRestart(string serverName, string error) => $"Failed to restart {serverName}: {error}";

    public static string FailedToUpdate(string serverName, string error) => $"Failed to update {serverName}: {error}";

    public static string FailedToBackUp(string serverName, string error) => $"Failed to back up {serverName}: {error}";

    public static string FailedToSendCommand(string serverName, string error) =>
        $"Failed to send command to {serverName}: {error}";

    public static string ConsoleCommandTimedOut(string serverName) =>
        $"Failed to send command to {serverName}: the console command timed out.";

    public static string ConsoleCommandFailed(string serverName) =>
        $"Failed to send command to {serverName} due to an internal error.";

    public static string ConsoleCommandStillRunning(string serverName) =>
        $"A previous console command for {serverName} is still running.";
}
