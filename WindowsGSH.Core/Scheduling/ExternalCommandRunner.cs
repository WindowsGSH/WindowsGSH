using System.Diagnostics;

namespace WindowsGSH.Core.Scheduling;

public sealed record ExternalCommandRunRequest(
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    int TimeoutSeconds,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string>? RedactedValues = null,
    Action<string>? StandardOutputSink = null,
    Action<string>? StandardErrorSink = null);

public sealed record ExternalCommandRunResult(
    int ExitCode,
    bool TimedOut,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError);

public sealed class ExternalCommandRunner
{
    public const int MaxBufferedLinesPerStream = 500;

    private static readonly HashSet<string> BlockedShellHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe"
    };

    public async Task<ExternalCommandRunResult> RunAsync(
        ExternalCommandRunRequest request,
        CancellationToken cancellationToken)
    {
        var executable = ValidateExecutable(request.ExecutablePath, request.AllowedPaths);
        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, 86400);
        var workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory, executable);
        var stdout = new BoundedLineBuffer(MaxBufferedLinesPerStream);
        var stderr = new BoundedLineBuffer(MaxBufferedLinesPerStream);
        var redactedValues = request.RedactedValues ?? [];

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = request.Arguments ?? string.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                var line = Redact(eventArgs.Data, redactedValues);
                stdout.Add(line);
                TryWriteLine(request.StandardOutputSink, line);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
            {
                var line = Redact(eventArgs.Data, redactedValues);
                stderr.Add(line);
                TryWriteLine(request.StandardErrorSink, line);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("External command process did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ExternalCommandRunResult(process.ExitCode, false, stdout.Snapshot(), stderr.Snapshot());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return new ExternalCommandRunResult(-1, true, stdout.Snapshot(), stderr.Snapshot());
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
    }

    public static string ValidateExecutable(string executablePath, IReadOnlyList<string> allowedPaths)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("External command executable path is required.");
        }

        var executable = Path.GetFullPath(executablePath.Trim());
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"External command executable does not exist: {executable}");
        }

        if (BlockedShellHosts.Contains(Path.GetFileName(executable)))
        {
            throw new InvalidOperationException("Shell hosts cannot be used for external scheduled commands.");
        }

        if (!(allowedPaths ?? []).Any(allowed => IsAllowed(executable, allowed)))
        {
            throw new InvalidOperationException($"External command executable is not allowlisted: {executable}");
        }

        return executable;
    }

    public static IReadOnlyList<string> ExtractSecretLikeValues(string configJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(configJson);
        var values = new HashSet<string>(StringComparer.Ordinal);
        CollectSecrets(document.RootElement, null, values);
        return values.Where(value => value.Length >= 4).OrderByDescending(value => value.Length).ToArray();
    }

    public static string Redact(string value, IReadOnlyList<string> redactedValues)
    {
        var result = value;
        foreach (var secret in redactedValues.Where(secret => !string.IsNullOrWhiteSpace(secret)).OrderByDescending(secret => secret.Length))
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return result;
    }

    private static bool IsAllowed(string executable, string allowedPath)
    {
        if (string.IsNullOrWhiteSpace(allowedPath))
        {
            return false;
        }

        var allowed = Path.GetFullPath(allowedPath.Trim());
        if (File.Exists(allowed))
        {
            return string.Equals(executable, allowed, StringComparison.OrdinalIgnoreCase);
        }

        if (Directory.Exists(allowed))
        {
            var directory = Path.TrimEndingDirectorySeparator(allowed);
            return executable.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ResolveWorkingDirectory(string configured, string executable)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetDirectoryName(executable)!;
        }

        var workingDirectory = Path.GetFullPath(configured.Trim());
        if (!Directory.Exists(workingDirectory))
        {
            throw new InvalidOperationException($"External command working directory does not exist: {workingDirectory}");
        }

        return workingDirectory;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch
        {
        }
    }

    private static void TryWriteLine(Action<string>? sink, string line)
    {
        try
        {
            sink?.Invoke(line);
        }
        catch
        {
            // A logging sink must not terminate the external process reader.
        }
    }

    private sealed class BoundedLineBuffer(int capacity)
    {
        private readonly object _sync = new();
        private readonly Queue<string> _lines = new();
        private int _droppedCount;

        public void Add(string line)
        {
            lock (_sync)
            {
                if (_lines.Count == capacity)
                {
                    _lines.Dequeue();
                    _droppedCount++;
                }

                _lines.Enqueue(line);
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_sync)
            {
                return _droppedCount == 0
                    ? _lines.ToArray()
                    : [$"[{_droppedCount} earlier output line(s) omitted from the bounded result buffer.]", .. _lines];
            }
        }
    }

    private static void CollectSecrets(
        System.Text.Json.JsonElement element,
        string? propertyName,
        ISet<string> values)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectSecrets(property.Value, property.Name, values);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectSecrets(item, propertyName, values);
                }
                break;
            case System.Text.Json.JsonValueKind.String when IsSecretLike(propertyName):
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
                break;
        }
    }

    private static bool IsSecretLike(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("apiKey", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }
}
