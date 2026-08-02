namespace WindowsGSH.Core.Servers;

public sealed record ServerCrashSummary(
    string ServerName,
    string ModuleId,
    string ModuleName,
    DateTimeOffset DetectedAt,
    int? ExitCode,
    string? RecentLogExcerpt,
    string? ReportPath,
    string? ServerFolder);
