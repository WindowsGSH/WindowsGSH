namespace WindowsGSH.Core.Web.Api;

public enum WebControlKind { Start, Stop, Restart, ForceStop, Update, Cancel }

public enum WebControlStatus
{
    Accepted,
    Conflict,
    NotFound,
    NothingToCancel,
    Unavailable,
}

public sealed record WebControlOutcome(WebControlStatus Status, string? ConflictingKind = null)
{
    public static WebControlOutcome Accepted() => new(WebControlStatus.Accepted);
    public static WebControlOutcome Conflict(string? kind) => new(WebControlStatus.Conflict, kind);
    public static WebControlOutcome NotFound() => new(WebControlStatus.NotFound);
    public static WebControlOutcome NothingToCancel() => new(WebControlStatus.NothingToCancel);
    public static WebControlOutcome Unavailable() => new(WebControlStatus.Unavailable);
}

/// <summary>
/// Bridge between the ASP.NET Core request thread and the WPF-layer operation controller.
/// The dispatch delegate is registered once at app startup by MainWindow.
/// </summary>
public static class WebServerControl
{
    private static Func<string, WebControlKind, string, WebControlOutcome>? _dispatch;

    public static void SetDispatch(Func<string, WebControlKind, string, WebControlOutcome> dispatch)
        => _dispatch = dispatch;

    public static WebControlOutcome TryDispatch(string serverId, WebControlKind kind, string webUsername)
        => _dispatch?.Invoke(serverId, kind, webUsername) ?? WebControlOutcome.NotFound();
}
