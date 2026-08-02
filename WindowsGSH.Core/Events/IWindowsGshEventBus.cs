namespace WindowsGSH.Core.Events;

public interface IWindowsGshEventBus
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler);

    void Publish<TEvent>(TEvent message);
}

public interface IWindowsGshEvent
{
    DateTimeOffset OccurredUtc { get; }

    string? ServerId { get; }

    string? ModuleId { get; }
}

public sealed record ServerLogEvent(
    DateTimeOffset OccurredUtc,
    string? ServerId,
    string? ModuleId,
    WindowsGshEventSeverity Severity,
    string Category,
    string Message,
    string? ExceptionText = null) : IWindowsGshEvent;

public sealed record ServerOperationChangedEvent(
    DateTimeOffset OccurredUtc,
    string ServerId,
    string? ModuleId,
    string ServerName,
    Operations.ServerOperationKind Kind,
    Operations.ServerOperationStatus Status,
    string DisplayText,
    string? Description,
    string? LastError) : IWindowsGshEvent;

public sealed record ServerStatusChangedEvent(
    DateTimeOffset OccurredUtc,
    string ServerId,
    string ModuleId,
    string ServerName,
    Servers.ServerRuntimeStatus Status,
    bool IsProcessRunning,
    bool Queried,
    string StatusText,
    string? Message) : IWindowsGshEvent;

public sealed record ServerCrashDetectedEvent(
    DateTimeOffset OccurredUtc,
    string ServerId,
    string ModuleId,
    string ServerName,
    int? ProcessId,
    string? ReportPath,
    string? Message) : IWindowsGshEvent
{
    public Servers.ServerCrashSummary? Summary { get; init; }
}

public sealed record ServerMetricSampledEvent(
    DateTimeOffset OccurredUtc,
    string ServerId,
    string ModuleId,
    string ServerName,
    int? ProcessId,
    string? ProcessName,
    double? CpuPercent,
    long? MemoryBytes,
    TimeSpan? Uptime,
    int? ThreadCount) : IWindowsGshEvent;

public sealed record PublicIpChangedEvent(
    DateTimeOffset OccurredUtc,
    string PreviousIp,
    string CurrentIp) : IWindowsGshEvent
{
    public string? ServerId => null;

    public string? ModuleId => null;
}

public enum WindowsGshEventSeverity
{
    Trace,
    Info,
    Warning,
    Error
}
