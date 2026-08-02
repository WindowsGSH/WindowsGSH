using System.Diagnostics;

namespace WindowsGSH.Core.Servers;

public static class ServerProcessLogFormatter
{
    public static string Describe(Process process)
    {
        return $"processName={ReadProcessName(process)}, processId={ReadProcessId(process)}, executablePath={ReadExecutablePath(process)}";
    }

    public static string ExitResult(Process process)
    {
        if (!TryHasExited(process, out var hasExited))
        {
            return "exitStatus=unknown";
        }

        if (!hasExited)
        {
            return "exitStatus=still-running";
        }

        return TryReadExitCode(process, out var exitCode)
            ? $"exitStatus=exited, exitCode={exitCode}"
            : "exitStatus=exited, exitCode=unknown";
    }

    private static string ReadProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string ReadProcessId(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string ReadExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "unknown";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static bool TryHasExited(Process process, out bool hasExited)
    {
        try
        {
            hasExited = process.HasExited;
            return true;
        }
        catch
        {
            hasExited = false;
            return false;
        }
    }

    private static bool TryReadExitCode(Process process, out int exitCode)
    {
        try
        {
            exitCode = process.ExitCode;
            return true;
        }
        catch
        {
            exitCode = 0;
            return false;
        }
    }
}
