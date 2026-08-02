namespace WindowsGSH.Core.Scheduling;

public sealed class CronActionReservationService
{
    private readonly HashSet<string> _reservedRunKeys = [];

    public void BeginMinute(string currentMinute)
    {
        _reservedRunKeys.RemoveWhere(key => !key.StartsWith(currentMinute + "|", StringComparison.Ordinal));
    }

    public void MarkAttempted(string currentMinute, string serverId, string actionKey)
    {
        _reservedRunKeys.Add(BuildRunKey(currentMinute, serverId, actionKey));
    }

    public CronActionReservationResult TryReserve(CronActionReservationRequest request)
    {
        var runKey = BuildRunKey(request.CurrentMinute, request.ServerId, request.ActionKey);
        if (_reservedRunKeys.Contains(runKey))
        {
            return CronActionReservationResult.Skip();
        }

        _reservedRunKeys.Add(runKey);
        if (request.IsConfigOpen && !request.CanStop)
        {
            return CronActionReservationResult.Skip(
                $"Cron {request.ActionLabel} skipped for {request.ServerName}: CFG window is open while the server is offline.");
        }

        if (!string.IsNullOrWhiteSpace(request.ActiveOperationDisplayText))
        {
            return CronActionReservationResult.Skip(
                $"Cron {request.ActionLabel} skipped for {request.ServerName}: {request.ActiveOperationDisplayText}.");
        }

        return CronActionReservationResult.Run();
    }

    private static string BuildRunKey(string currentMinute, string serverId, string actionKey)
    {
        return $"{currentMinute}|{serverId}|{actionKey}";
    }
}

public sealed record CronActionReservationRequest(
    string CurrentMinute,
    string ServerId,
    string ServerName,
    string ActionKey,
    string ActionLabel,
    bool IsConfigOpen,
    bool CanStop,
    string? ActiveOperationDisplayText);

public sealed record CronActionReservationResult(bool ShouldRun, string? LogMessage)
{
    public static CronActionReservationResult Run() => new(ShouldRun: true, LogMessage: null);

    public static CronActionReservationResult Skip(string? logMessage = null) => new(ShouldRun: false, logMessage);
}
