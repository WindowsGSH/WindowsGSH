using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ApplicationExitPlannerTests
{
    private readonly ApplicationExitPlanner _planner = new();

    [Fact]
    public void User_close_without_running_servers_exits_without_prompt_or_stop()
    {
        var plan = _planner.PlanUserClose(0);

        Assert.True(plan.ShouldExit);
        Assert.False(plan.ShouldPrompt);
        Assert.False(plan.ShouldStopServers);
    }

    [Fact]
    public void User_close_with_running_servers_requires_explicit_choice()
    {
        var plan = _planner.PlanUserClose(2);

        Assert.False(plan.ShouldExit);
        Assert.True(plan.ShouldPrompt);
        Assert.False(plan.ShouldStopServers);
    }

    [Theory]
    [InlineData(ApplicationExitChoice.Cancel, false, false)]
    [InlineData(ApplicationExitChoice.StopServersAndExit, true, true)]
    [InlineData(ApplicationExitChoice.LeaveServersRunningAndExit, true, false)]
    public void User_choice_maps_to_expected_action(
        ApplicationExitChoice choice,
        bool shouldExit,
        bool shouldStopServers)
    {
        var plan = _planner.PlanUserClose(3, activeOperationCount: 0, choice: choice);

        Assert.Equal(shouldExit, plan.ShouldExit);
        Assert.Equal(shouldStopServers, plan.ShouldStopServers);
        Assert.False(plan.ShouldPrompt);
    }

    [Theory]
    [InlineData(false, ApplicationExitSource.WindowsShutdown)]
    [InlineData(true, ApplicationExitSource.WindowsLogoff)]
    public void Windows_session_ending_stops_servers_without_prompt(
        bool isLogoff,
        ApplicationExitSource expectedSource)
    {
        var plan = _planner.PlanWindowsSessionEnding(2, activeOperationCount: 1, isLogoff: isLogoff);

        Assert.Equal(expectedSource, plan.Source);
        Assert.True(plan.ShouldExit);
        Assert.True(plan.ShouldStopServers);
        Assert.False(plan.ShouldPrompt);
        Assert.Equal(1, plan.ActiveOperationCount);
    }

    [Fact]
    public void Active_operation_requires_prompt_even_when_no_server_is_running()
    {
        var plan = _planner.PlanUserClose(0, activeOperationCount: 1);

        Assert.True(plan.ShouldPrompt);
        Assert.False(plan.ShouldExit);
        Assert.Equal(1, plan.ActiveOperationCount);
    }
}
