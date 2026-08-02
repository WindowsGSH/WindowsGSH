namespace WindowsGSH.Core.Operations;

public sealed record ServerOperationSnapshot(
    string ServerId,
    string ServerName,
    ServerOperationKind Kind,
    string Status,
    DateTimeOffset StartedAt,
    string? LastError,
    bool IsActive,
    DateTimeOffset? FinishedAt = null,
    string? Description = null,
    string OperationId = "")
{
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartedAt;

    public DateTimeOffset? CompletedAt => FinishedAt;

    public TimeSpan? Duration => FinishedAt.HasValue
        ? FinishedAt.Value - StartedAt
        : null;

    public ServerOperationStatus State => IsActive
        ? ServerOperationStatus.Running
        : Status switch
        {
            "Completed" => ServerOperationStatus.Completed,
            "Failed" => ServerOperationStatus.Failed,
            "Cancelled" => ServerOperationStatus.Cancelled,
            "Interrupted" => ServerOperationStatus.Interrupted,
            _ => LastError == null ? ServerOperationStatus.Completed : ServerOperationStatus.Failed
        };

    public string ElapsedText => FormatElapsed(Elapsed);

    public string DisplayText => LastError == null
        ? $"{Status} {ElapsedText}"
        : $"{Status}: {LastError}";

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}

public enum ServerOperationKind
{
    Install,
    Import,
    Start,
    Stop,
    Restart,
    Update,
    Backup,
    Restore,
    Delete,
    Addon,
    AddonAction = Addon,
    Rcon,
    ConsoleCommand,
    ApiAction,
    ExternalCommand,
    ForceStop,
    Verify
}

public enum ServerOperationStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record ServerOperationResult(
    bool Success,
    ServerOperationKind Kind,
    string? ServerId,
    string? ServerName,
    ServerOperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Description = null,
    string? LastError = null)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public static ServerOperationResult FromSnapshot(ServerOperationSnapshot snapshot)
    {
        var completedAt = snapshot.CompletedAt ?? DateTimeOffset.UtcNow;
        return new ServerOperationResult(
            snapshot.State == ServerOperationStatus.Completed,
            snapshot.Kind,
            snapshot.ServerId,
            snapshot.ServerName,
            snapshot.State,
            snapshot.StartedAt,
            completedAt,
            snapshot.Description,
            snapshot.LastError);
    }
}
