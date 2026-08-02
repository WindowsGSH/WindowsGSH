using System.Diagnostics;

namespace WindowsGSH.Core.Java;

public sealed class JavaRuntimeLocator
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, string> _runVersionCommand;

    public JavaRuntimeLocator()
        : this(File.Exists, Environment.GetEnvironmentVariable, RunJavaVersionCommand)
    {
    }

    public JavaRuntimeLocator(
        Func<string, bool> fileExists,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string> runVersionCommand)
    {
        _fileExists = fileExists;
        _getEnvironmentVariable = getEnvironmentVariable;
        _runVersionCommand = runVersionCommand;
    }

    public JavaRuntimeInfo Locate(string? configuredJavaPath = null)
    {
        var candidates = GetCandidates(configuredJavaPath)
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!_fileExists(candidate.Path))
            {
                if (candidate.IsConfigured)
                {
                    return JavaRuntimeInfo.Missing($"Configured Java path was not found: {candidate.Path}");
                }

                continue;
            }

            try
            {
                var versionText = _runVersionCommand(candidate.Path);
                return new JavaRuntimeInfo(
                    candidate.Path,
                    JavaVersionParser.ParseMajorVersion(versionText),
                    versionText,
                    JavaVersionParser.ParseVendor(versionText),
                    ErrorMessage: null);
            }
            catch (Exception ex)
            {
                var failure = $"{candidate.Path}: {ex.Message}";
                if (candidate.IsConfigured)
                {
                    return new JavaRuntimeInfo(candidate.Path, null, string.Empty, string.Empty, $"Configured Java path failed: {failure}");
                }

                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            return JavaRuntimeInfo.Missing("No working java.exe was found. Checked candidates failed: " + string.Join("; ", failures));
        }

        return JavaRuntimeInfo.Missing("java.exe was not found in configured path, JAVA_HOME, or PATH.");
    }

    public IReadOnlyList<JavaRuntimeInfo> Discover(IEnumerable<string>? knownRuntimePaths = null)
    {
        var candidates = (knownRuntimePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new JavaCandidate(NormalizeExecutablePath(path), IsConfigured: false))
            .Concat(GetCandidates(configuredJavaPath: null))
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase);
        var runtimes = new List<JavaRuntimeInfo>();
        foreach (var candidate in candidates)
        {
            if (!_fileExists(candidate.Path))
            {
                continue;
            }

            try
            {
                var versionText = _runVersionCommand(candidate.Path);
                runtimes.Add(new JavaRuntimeInfo(
                    candidate.Path,
                    JavaVersionParser.ParseMajorVersion(versionText),
                    versionText,
                    JavaVersionParser.ParseVendor(versionText),
                    ErrorMessage: null));
            }
            catch (Exception ex)
            {
                runtimes.Add(new JavaRuntimeInfo(candidate.Path, null, string.Empty, string.Empty, ex.Message));
            }
        }

        return runtimes;
    }

    private IEnumerable<JavaCandidate> GetCandidates(string? configuredJavaPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredJavaPath))
        {
            yield return new JavaCandidate(NormalizeExecutablePath(configuredJavaPath), IsConfigured: true);
            yield break;
        }

        var javaHome = _getEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return new JavaCandidate(Path.Combine(javaHome, "bin", "java.exe"), IsConfigured: false);
        }

        var path = _getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return new JavaCandidate(Path.Combine(folder, "java.exe"), IsConfigured: false);
        }
    }

    public static string NormalizeExecutablePath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var fileName = Path.GetFileName(trimmed);
        if (!fileName.Equals("java.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(trimmed, "java.exe");
        }

        return trimmed;
    }

    private static string RunJavaVersionCommand(string javaPath)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = "-version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new InvalidOperationException("java -version timed out.");
        }

        var versionText = string.IsNullOrWhiteSpace(error) ? output : error;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            throw new InvalidOperationException("java -version returned no version text.");
        }

        return versionText.Trim();
    }

    private sealed record JavaCandidate(string Path, bool IsConfigured);
}
