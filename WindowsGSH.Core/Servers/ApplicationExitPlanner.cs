namespace WindowsGSH.Core.Servers;

public enum ApplicationExitChoice
{
    Cancel,
    StopServersAndExit,
    LeaveServersRunningAndExit
}

public enum ApplicationExitSource
{
    User,
    WindowsShutdown,
    WindowsLogoff
}

public sealed record ApplicationExitPlan(
    ApplicationExitSource Source,
    ApplicationExitChoice Choice,
    int RunningServerCount,
    int ActiveOperationCount,
    bool ShouldExit,
    bool ShouldStopServers,
    bool ShouldPrompt);

public sealed class ApplicationExitPlanner
{
    public ApplicationExitPlan PlanUserClose(
        int runningServerCount,
        int activeOperationCount = 0,
        ApplicationExitChoice? choice = null)
    {
        var count = Math.Max(0, runningServerCount);
        var operationCount = Math.Max(0, activeOperationCount);
        if (count == 0 && operationCount == 0)
        {
            return new ApplicationExitPlan(
                ApplicationExitSource.User,
                ApplicationExitChoice.LeaveServersRunningAndExit,
                count,
                operationCount,
                ShouldExit: true,
                ShouldStopServers: false,
                ShouldPrompt: false);
        }

        if (!choice.HasValue)
        {
            return new ApplicationExitPlan(
                ApplicationExitSource.User,
                ApplicationExitChoice.Cancel,
                count,
                operationCount,
                ShouldExit: false,
                ShouldStopServers: false,
                ShouldPrompt: true);
        }

        return choice.Value switch
        {
            ApplicationExitChoice.StopServersAndExit => new ApplicationExitPlan(
                ApplicationExitSource.User,
                choice.Value,
                count,
                operationCount,
                ShouldExit: true,
                ShouldStopServers: true,
                ShouldPrompt: false),
            ApplicationExitChoice.LeaveServersRunningAndExit => new ApplicationExitPlan(
                ApplicationExitSource.User,
                choice.Value,
                count,
                operationCount,
                ShouldExit: true,
                ShouldStopServers: false,
                ShouldPrompt: false),
            _ => new ApplicationExitPlan(
                ApplicationExitSource.User,
                ApplicationExitChoice.Cancel,
                count,
                operationCount,
                ShouldExit: false,
                ShouldStopServers: false,
                ShouldPrompt: false)
        };
    }

    public ApplicationExitPlan PlanWindowsSessionEnding(
        int runningServerCount,
        int activeOperationCount,
        bool isLogoff)
    {
        return new ApplicationExitPlan(
            isLogoff ? ApplicationExitSource.WindowsLogoff : ApplicationExitSource.WindowsShutdown,
            ApplicationExitChoice.StopServersAndExit,
            Math.Max(0, runningServerCount),
            Math.Max(0, activeOperationCount),
            ShouldExit: true,
            ShouldStopServers: runningServerCount > 0,
            ShouldPrompt: false);
    }
}
