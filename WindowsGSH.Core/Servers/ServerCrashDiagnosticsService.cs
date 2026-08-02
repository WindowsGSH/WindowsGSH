using System.Diagnostics;
using System.IO;
using System.Text;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public sealed class ServerCrashDiagnosticsService
{
    public string ReadGameLogExcerpt(IGameServerModule module, ServerInstance instance, int maxLines = 160)
        => ReadRecentGameLog(module.GetConsoleLogPath(instance), maxLines);

    public string WriteUnexpectedExitReport(
        InstalledServer server,
        IGameServerModule module,
        ServerInstance instance,
        Process process,
        string commandLine,
        string recentConsoleOutput,
        string? preReadGameLog = null)
    {
        var crashDirectory = Path.Combine(server.ServerFolder, "CrashLogs");
        Directory.CreateDirectory(crashDirectory);

        var timestamp = DateTimeOffset.Now;
        var processId = SafeGet(() => process.Id.ToString(), "unknown");
        var reportPath = Path.Combine(crashDirectory, $"server-crash-{timestamp:yyyyMMdd-HHmmss}-pid-{processId}.log");

        var report = new StringBuilder();
        report.AppendLine("WindowsGSH Server Crash Report");
        report.AppendLine("==========================");
        report.AppendLine($"Created: {timestamp:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Server ID: {server.Id}");
        report.AppendLine($"Server Name: {server.Name}");
        report.AppendLine($"Module: {module.Id} ({module.Name})");
        report.AppendLine($"Install Path: {server.InstallPath}");
        report.AppendLine();

        report.AppendLine("Process");
        report.AppendLine("-------");
        report.AppendLine($"PID: {processId}");
        report.AppendLine($"Executable: {SafeGet(() => process.StartInfo.FileName, "(unknown)")}");
        report.AppendLine($"Working Directory: {SafeGet(() => process.StartInfo.WorkingDirectory, "(unknown)")}");
        report.AppendLine($"Arguments: {SafeGet(() => process.StartInfo.Arguments, "(unknown)")}");
        report.AppendLine($"Command Line: {commandLine}");
        report.AppendLine($"Start Time: {SafeGet(() => process.StartTime.ToString("yyyy-MM-dd HH:mm:ss zzz"), "(unknown)")}");
        report.AppendLine($"Exit Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Exit Code: {SafeGet(() => process.ExitCode.ToString(), "(unknown)")}");
        report.AppendLine();

        AppendSection(report, "Recent Console Output", recentConsoleOutput);
        AppendSection(report, "Recent Game Log", preReadGameLog ?? ReadRecentGameLog(module.GetConsoleLogPath(instance), 160));

        File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        return reportPath;
    }

    private static void AppendSection(StringBuilder report, string title, string content)
    {
        report.AppendLine(title);
        report.AppendLine(new string('-', title.Length));
        report.AppendLine(string.IsNullOrWhiteSpace(content) ? "(none captured)" : content);
        report.AppendLine();
    }

    public ServerCrashSummary BuildSummary(
        InstalledServer server,
        IGameServerModule module,
        ServerInstance instance,
        Process process,
        string recentConsoleOutput,
        string? reportPath,
        string? preReadGameLog = null)
    {
        string logExcerpt;
        if (preReadGameLog != null)
        {
            var lines = preReadGameLog
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(30);
            logExcerpt = string.Join(Environment.NewLine, lines);
        }
        else
        {
            logExcerpt = ReadRecentGameLog(module.GetConsoleLogPath(instance), 30);
        }

        if (string.IsNullOrWhiteSpace(logExcerpt) && !string.IsNullOrWhiteSpace(recentConsoleOutput))
        {
            var lines = recentConsoleOutput
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(20);
            logExcerpt = string.Join(Environment.NewLine, lines);
        }

        return new ServerCrashSummary(
            ServerName: server.Name,
            ModuleId: module.Id,
            ModuleName: module.Name,
            DetectedAt: DateTimeOffset.UtcNow,
            ExitCode: SafeGetExitCode(process),
            RecentLogExcerpt: string.IsNullOrWhiteSpace(logExcerpt) ? null : logExcerpt,
            ReportPath: reportPath,
            ServerFolder: server.ServerFolder);
    }

    private static string ReadRecentGameLog(string? path, int maxLines)
    {
        var resolvedPath = ResolveLogPath(path);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            return string.Empty;
        }

        try
        {
            var lines = File.ReadLines(resolvedPath).TakeLast(maxLines);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Could not read game log '{resolvedPath}': {ex.Message}";
        }
    }

    private static string? ResolveLogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(path, "*.log")
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        return files.FirstOrDefault(file => file.Length > 0)?.FullName
            ?? files.FirstOrDefault()?.FullName;
    }

    private static int? SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeGet(Func<string?> valueFactory, string fallback)
    {
        try
        {
            return valueFactory() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
