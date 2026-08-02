using WindowsGSH.Core.Scheduling;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class CronActionReservationServiceTests
{
    [Fact]
    public void TryReserve_allows_first_action_in_minute()
    {
        var service = new CronActionReservationService();

        service.BeginMinute("202605161420");
        var result = service.TryReserve(Request("202605161420"));

        Assert.True(result.ShouldRun);
        Assert.Null(result.LogMessage);
    }

    [Fact]
    public void TryReserve_skips_duplicate_action_without_log_noise()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");

        _ = service.TryReserve(Request("202605161420"));
        var duplicate = service.TryReserve(Request("202605161420"));

        Assert.False(duplicate.ShouldRun);
        Assert.Null(duplicate.LogMessage);
    }

    [Fact]
    public void BeginMinute_expires_previous_minute_reservations()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");
        _ = service.TryReserve(Request("202605161420"));

        service.BeginMinute("202605161421");
        var result = service.TryReserve(Request("202605161421"));

        Assert.True(result.ShouldRun);
    }

    [Fact]
    public void TryReserve_skips_offline_server_with_config_open()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");

        var result = service.TryReserve(Request(
            "202605161420",
            isConfigOpen: true,
            canStop: false));

        Assert.False(result.ShouldRun);
        Assert.Equal(
            "Cron start skipped for Icarus: CFG window is open while the server is offline.",
            result.LogMessage);
    }

    [Fact]
    public void TryReserve_allows_running_server_with_config_open()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");

        var result = service.TryReserve(Request(
            "202605161420",
            isConfigOpen: true,
            canStop: true));

        Assert.True(result.ShouldRun);
    }

    [Fact]
    public void TryReserve_skips_active_operation()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");

        var result = service.TryReserve(Request(
            "202605161420",
            activeOperationDisplayText: "Updating 00:12"));

        Assert.False(result.ShouldRun);
        Assert.Equal("Cron start skipped for Icarus: Updating 00:12.", result.LogMessage);
    }

    [Fact]
    public void MarkAttempted_reserves_invalid_cron_action_key()
    {
        var service = new CronActionReservationService();
        service.BeginMinute("202605161420");

        service.MarkAttempted("202605161420", "1", "start");
        var result = service.TryReserve(Request("202605161420"));

        Assert.False(result.ShouldRun);
    }

    private static CronActionReservationRequest Request(
        string currentMinute,
        bool isConfigOpen = false,
        bool canStop = false,
        string? activeOperationDisplayText = null)
    {
        return new CronActionReservationRequest(
            currentMinute,
            ServerId: "1",
            ServerName: "Icarus",
            ActionKey: "start",
            ActionLabel: "start",
            isConfigOpen,
            canStop,
            activeOperationDisplayText);
    }
}
