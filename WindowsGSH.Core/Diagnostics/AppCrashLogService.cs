using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsGSH.Core.Diagnostics;

public sealed class AppCrashLogService
{
    private readonly Func<DateTimeOffset> _now;
    private readonly string _crashDirectory;

    public AppCrashLogService(string? crashDirectory = null, Func<DateTimeOffset>? now = null)
    {
        _crashDirectory = crashDirectory ?? AppPaths.GetPath("logs", "crashes");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public AppCrashLogResult Write(AppCrashLogRequest request)
    {
        Directory.CreateDirectory(_crashDirectory);
        var timestamp = _now();
        var path = Path.Combine(_crashDirectory, $"app-crash-{timestamp:yyyyMMdd-HHmmssfff}.log");
        File.WriteAllText(path, BuildLog(request, timestamp), Encoding.UTF8);
        return new AppCrashLogResult(path);
    }

    private static string BuildLog(AppCrashLogRequest request, DateTimeOffset timestamp)
    {
        var process = Process.GetCurrentProcess();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppCrashLogService).Assembly;
        var builder = new StringBuilder();
        builder.AppendLine("WindowsGSH App Crash Report");
        builder.AppendLine("===========================");
        builder.AppendLine($"Created Local: {timestamp:O}");
        builder.AppendLine($"Created UTC: {timestamp.ToUniversalTime():O}");
        builder.AppendLine($"Context: {request.Context}");
        builder.AppendLine($"Fatal: {request.IsFatal}");
        builder.AppendLine($"App Version: {assembly.GetName().Version}");
        builder.AppendLine($"Process ID: {process.Id}");
        builder.AppendLine($"Process Name: {process.ProcessName}");
        builder.AppendLine($"Process Path: {Environment.ProcessPath ?? "(unknown)"}");
        builder.AppendLine($"Command Line: {Sanitize(Environment.CommandLine)}");
        builder.AppendLine($"Base Directory: {AppContext.BaseDirectory}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"Machine: {Environment.MachineName}");
        builder.AppendLine($"User Interactive: {Environment.UserInteractive}");
        builder.AppendLine();
        builder.AppendLine("Exception");
        builder.AppendLine("---------");
        builder.AppendLine(request.Exception == null
            ? request.Details ?? "(no exception details)"
            : FormatException(request.Exception));
        builder.AppendLine();
        builder.AppendLine("Recent App Log");
        builder.AppendLine("--------------");
        builder.AppendLine(string.IsNullOrWhiteSpace(request.RecentAppLog)
            ? "(none captured)"
            : SupportBundleService.RedactText(request.RecentAppLog));
        return builder.ToString();
    }

    public static string FormatException(Exception exception)
    {
        var builder = new StringBuilder();
        for (var current = exception; current != null; current = current.InnerException)
        {
            builder
                .AppendLine(current.GetType().FullName)
                .AppendLine(current.Message)
                .AppendLine(current.StackTrace ?? "(no stack trace)")
                .AppendLine();
        }

        return builder.ToString();
    }

    internal static string Sanitize(string value)
    {
        // keyword=value and keyword: value styles
        value = Regex.Replace(
            value,
            @"(?i)\b(token|password|secret|api[_-]?key|gslt)\b\s*[:=]\s*\S+",
            "$1=<redacted>");
        // --keyword value and /keyword value (space-separated argument styles).
        // Quoted values such as --password "alpha beta" are matched as a unit before
        // falling back to a single unquoted word, preventing partial redaction.
        value = Regex.Replace(
            value,
            @"(?i)(--|/)(token|password|secret|api[_-]?key|gslt)\s+(?:""[^""]*""|\S+)",
            "$1$2 <redacted>");
        return value;
    }
}

public sealed record AppCrashLogRequest(
    string Context,
    bool IsFatal,
    Exception? Exception = null,
    string? Details = null,
    string? RecentAppLog = null);

public sealed record AppCrashLogResult(string Path);
