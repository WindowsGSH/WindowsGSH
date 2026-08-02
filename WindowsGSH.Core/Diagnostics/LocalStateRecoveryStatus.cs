namespace WindowsGSH.Core.Diagnostics;

public static class LocalStateRecoveryStatus
{
    private static readonly object Sync = new();
    private static readonly List<LocalStateRecoveryEvent> Events = [];

    public static void Report(string area, string message, string? recoveryPath = null, bool isFailure = false)
    {
        lock (Sync)
        {
            Events.Add(new LocalStateRecoveryEvent(
                DateTimeOffset.UtcNow,
                area,
                message,
                recoveryPath,
                isFailure));
        }
    }

    public static IReadOnlyList<LocalStateRecoveryEvent> Snapshot()
    {
        lock (Sync)
        {
            return Events.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Events.Clear();
        }
    }
}

public sealed record LocalStateRecoveryEvent(
    DateTimeOffset CreatedUtc,
    string Area,
    string Message,
    string? RecoveryPath,
    bool IsFailure);
