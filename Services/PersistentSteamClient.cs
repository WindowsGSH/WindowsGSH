using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Steam;
using WindowsGSH.Data.Security;

namespace WindowsGSH.Services;

public sealed class PersistentSteamClient : ISteamCmdClient
{
    private readonly ISteamCmdClient _steamCmd;
    private readonly DepotDownloaderClient _authenticatedClient;

    public PersistentSteamClient(
        ISteamCredentialProvider credentialProvider,
        ISteamGuardCodeProvider? codeProvider,
        Func<bool> persistentSessionsEnabled)
    {
        _steamCmd = new SteamCmdManager(credentialProvider, codeProvider);
        _authenticatedClient = new DepotDownloaderClient(
            credentialProvider,
            codeProvider,
            persistentSessionsEnabled);
    }

    internal PersistentSteamClient(
        ISteamCmdClient steamCmd,
        DepotDownloaderClient authenticatedClient)
    {
        _steamCmd = steamCmd;
        _authenticatedClient = authenticatedClient;
    }

    public Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return _steamCmd.EnsureInstalledAsync(progress, cancellationToken);
    }

    public Task<int> InstallOrUpdateAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Select(definition).InstallOrUpdateAsync(
            installPath,
            definition,
            branch,
            branchPassword,
            progress,
            cancellationToken);
    }

    public Task<int> VerifyAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Select(definition).VerifyAsync(
            installPath,
            definition,
            branch,
            branchPassword,
            progress,
            cancellationToken);
    }

    public Task<string?> GetRemoteBuildIdAsync(
        SteamInstallDefinition definition,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Select(definition).GetRemoteBuildIdAsync(definition, branch, progress, cancellationToken);
    }

    private ISteamCmdClient Select(SteamInstallDefinition definition)
    {
        return definition.LoginAnonymous ? _steamCmd : _authenticatedClient;
    }
}

public sealed class DepotDownloaderClient : ISteamCmdClient
{
    // IsRejectedAccessToken only needs to see the tail of the output, not every progress
    // line of a multi-GB download.
    private const int MaxCapturedOutputLines = 200;
    private static readonly SemaphoreSlim AuthenticatedOperationGate = new(1, 1);
    private readonly ISteamCredentialProvider _credentialProvider;
    private readonly ISteamGuardCodeProvider? _codeProvider;
    private readonly Func<bool> _persistentSessionsEnabled;
    private readonly DepotDownloaderToolManager _toolManager;
    private readonly DepotDownloaderSessionStore _sessionStore;

    public DepotDownloaderClient(
        ISteamCredentialProvider credentialProvider,
        ISteamGuardCodeProvider? codeProvider,
        Func<bool> persistentSessionsEnabled)
    {
        _credentialProvider = credentialProvider;
        _codeProvider = codeProvider;
        _persistentSessionsEnabled = persistentSessionsEnabled;
        _toolManager = new DepotDownloaderToolManager();
        _sessionStore = new DepotDownloaderSessionStore(_toolManager.AccountConfigPath);
    }

    internal DepotDownloaderClient(
        ISteamCredentialProvider credentialProvider,
        ISteamGuardCodeProvider? codeProvider,
        Func<bool> persistentSessionsEnabled,
        DepotDownloaderToolManager toolManager,
        DepotDownloaderSessionStore sessionStore)
    {
        _credentialProvider = credentialProvider;
        _codeProvider = codeProvider;
        _persistentSessionsEnabled = persistentSessionsEnabled;
        _toolManager = toolManager;
        _sessionStore = sessionStore;
    }

    public Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        return _toolManager.EnsureInstalledAsync(progress, cancellationToken);
    }

    public Task<int> InstallOrUpdateAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            installPath,
            definition,
            branch,
            branchPassword,
            validate: definition.ValidateByDefault,
            progress,
            cancellationToken);
    }

    public Task<int> VerifyAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            installPath,
            definition,
            branch,
            branchPassword,
            validate: true,
            progress,
            cancellationToken);
    }

    public Task<string?> GetRemoteBuildIdAsync(
        SteamInstallDefinition definition,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Remote build pre-check is skipped for authenticated Steam updates; the downloader will resolve the current branch manifest.");
        return Task.FromResult<string?>(null);
    }

    private async Task<int> RunAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        bool validate,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateDefinition(definition);
        var credentials = _credentialProvider.Load()
            ?? throw new InvalidOperationException(
                "This server requires an authenticated Steam account. Save the Steam username and password in WindowsGSH Config > Steam.");

        await AuthenticatedOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _toolManager.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(installPath);

            var arguments = BuildArguments(
                installPath,
                definition.AppId,
                credentials.Username,
                branch,
                branchPassword,
                validate);
            progress?.Report("Running authenticated Steam downloader:");
            progress?.Report(BuildSafeCommandDisplay(_toolManager.ExecutablePath, arguments, credentials.Username, branchPassword));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_sessionStore.Restore())
                {
                    progress?.Report("Restored the Windows-encrypted Steam session for this update.");
                }
                else
                {
                    progress?.Report("No remembered Steam session exists yet. Steam Guard approval should only be required for this first authenticated run.");
                }

                DepotDownloaderRunResult result;
                try
                {
                    result = await RunProcessAsync(
                        _toolManager.ExecutablePath,
                        arguments,
                        credentials,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (_sessionStore.CaptureAndProtect())
                        {
                            progress?.Report("Saved the reusable Steam session with Windows user encryption.");
                        }
                        else
                        {
                            progress?.Report("No reusable Steam session was issued. A future update may require Steam Guard approval again.");
                        }
                    }
                    catch (Exception ex) when (
                        ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                        System.Security.Cryptography.CryptographicException)
                    {
                        progress?.Report($"Could not protect the reusable Steam session: {ex.Message}");
                    }
                }

                if (result.ExitCode == 0)
                {
                    return 0;
                }

                if (attempt == 0 && IsRejectedAccessToken(result.Output))
                {
                    _sessionStore.Clear();
                    progress?.Report("Steam rejected the remembered session. Retrying once with the saved password so a new Steam Guard approval can replace it.");
                    continue;
                }

                progress?.Report($"Authenticated Steam downloader failed with exit code {result.ExitCode}.");
                return result.ExitCode;
            }

            return 1;
        }
        finally
        {
            AuthenticatedOperationGate.Release();
        }
    }

    private async Task<DepotDownloaderRunResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        SteamCredentials credentials,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inputGate = new SemaphoreSlim(1, 1);
        var passwordSubmitted = 0;
        var challengeActive = 0;
        var challengeChannel = Channel.CreateUnbounded<SteamGuardChallengeKind>();
        var operationId = Guid.NewGuid().ToString("N");
        var output = new BoundedOutputBuffer(MaxCapturedOutputLines);

        void HandleOutput(string line)
        {
            // Called from the stdout and stderr pump tasks concurrently.
            output.AppendLine(line);
            var safeLine = SteamCmdPolicy.MaskSensitiveText(
                line,
                credentials.Username,
                credentials.Password);
            progress?.Report(safeLine);

            if (IsDepotDownloaderPasswordPrompt(line) &&
                Interlocked.Exchange(ref passwordSubmitted, 1) == 0)
            {
                _ = WriteInputAsync(process, inputGate, credentials.Password, operationCts.Token);
            }

            var challengeKind = SteamCmdPolicy.ClassifyChallengeLine(line);
            if (challengeKind.HasValue &&
                Interlocked.Exchange(ref challengeActive, 1) == 0)
            {
                challengeChannel.Writer.TryWrite(challengeKind.Value);
            }
        }

        var outputTask = PumpProcessStreamAsync(process.StandardOutput, HandleOutput, operationCts.Token);
        var errorTask = PumpProcessStreamAsync(process.StandardError, HandleOutput, operationCts.Token);
        var challengeTask = HandleChallengesAsync(
            challengeChannel.Reader,
            process,
            inputGate,
            operationId,
            credentials.Username,
            () => Interlocked.Exchange(ref challengeActive, 0),
            operationCts);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new DepotDownloaderRunResult(process.ExitCode, output.ToText());
        }
        catch (OperationCanceledException)
        {
            operationCts.Cancel();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            challengeChannel.Writer.TryComplete();
            operationCts.Cancel();
            try
            {
                await challengeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            process.StandardInput.Close();
            inputGate.Dispose();
        }
    }

    private async Task HandleChallengesAsync(
        ChannelReader<SteamGuardChallengeKind> reader,
        Process process,
        SemaphoreSlim inputGate,
        string operationId,
        string username,
        Action releaseChallenge,
        CancellationTokenSource operationCts)
    {
        await foreach (var kind in reader.ReadAllAsync(operationCts.Token).ConfigureAwait(false))
        {
            if (_codeProvider == null)
            {
                releaseChallenge();
                continue;
            }

            var request = new SteamGuardChallengeRequest(
                operationId,
                kind,
                username,
                DateTimeOffset.UtcNow);
            var code = await _codeProvider.RequestCodeAsync(request, operationCts.Token).ConfigureAwait(false);
            releaseChallenge();
            if (code == null)
            {
                operationCts.Cancel();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(code))
            {
                await WriteInputAsync(process, inputGate, code, operationCts.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteInputAsync(
        Process process,
        SemaphoreSlim inputGate,
        string value,
        CancellationToken cancellationToken)
    {
        await inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process.HasExited)
            {
                return;
            }

            await process.StandardInput.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            inputGate.Release();
        }
    }

    private static async Task PumpProcessStreamAsync(
        StreamReader reader,
        Action<string> reportLine,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        Task<int>? pendingRead = null;

        while (true)
        {
            pendingRead ??= reader.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask();
            int read;
            if (line.Length > 0)
            {
                try
                {
                    // DepotDownloader writes password and Steam Guard prompts without
                    // terminating them with a newline. Flush partial text promptly so
                    // authentication input can be supplied while the child is waiting.
                    read = await pendingRead.WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                    pendingRead = null;
                }
                catch (TimeoutException)
                {
                    ReportBufferedLine(line, reportLine);
                    continue;
                }
            }
            else
            {
                read = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
            }

            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    ReportBufferedLine(line, reportLine);
                }
                else
                {
                    line.Append(character);
                }
            }
        }

        ReportBufferedLine(line, reportLine);
    }

    private static void ReportBufferedLine(StringBuilder line, Action<string> reportLine)
    {
        if (line.Length == 0)
        {
            return;
        }

        var value = line.ToString().TrimEnd();
        line.Clear();
        if (!string.IsNullOrWhiteSpace(value))
        {
            reportLine(value);
        }
    }

    internal static IReadOnlyList<string> BuildArguments(
        string installPath,
        string appId,
        string username,
        string branch,
        string branchPassword,
        bool validate)
    {
        var arguments = new List<string>
        {
            "-app", appId,
            "-dir", Path.GetFullPath(installPath),
            "-os", "windows",
            "-username", username,
            "-remember-password"
        };

        if (!string.IsNullOrWhiteSpace(branch) &&
            !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-branch");
            arguments.Add(branch.Trim());
        }

        if (!string.IsNullOrWhiteSpace(branchPassword))
        {
            arguments.Add("-branchpassword");
            arguments.Add(branchPassword);
        }

        if (validate)
        {
            arguments.Add("-validate");
        }

        return arguments;
    }

    internal static bool IsDepotDownloaderPasswordPrompt(string line)
    {
        return Regex.IsMatch(
            line,
            @"^\s*Enter\s+account\s+password\s+for\b.*:\s*$",
            RegexOptions.IgnoreCase);
    }

    internal static bool IsRejectedAccessToken(string output)
    {
        return Regex.IsMatch(
            output,
            @"Access token was rejected\s*\((?:InvalidPassword|InvalidSignature|AccessDenied|Expired|Revoked)\)",
            RegexOptions.IgnoreCase);
    }

    private static string BuildSafeCommandDisplay(
        string executablePath,
        IReadOnlyList<string> arguments,
        string username,
        string branchPassword)
    {
        var display = string.Join(
            " ",
            arguments.Select(argument =>
                argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument));
        return SteamCmdPolicy.MaskSensitiveText(
            $"{executablePath} {display}",
            username,
            branchPassword);
    }

    private void EnsureEnabled()
    {
        if (!_persistentSessionsEnabled())
        {
            throw new InvalidOperationException(
                "This server requires Steam authentication. Enable persistent authenticated Steam updates in WindowsGSH Config > Steam first.");
        }
    }

    private static void ValidateDefinition(SteamInstallDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.ModName) ||
            !string.IsNullOrWhiteSpace(definition.CustomArguments))
        {
            throw new NotSupportedException(
                "This authenticated Steam module uses SteamCMD-specific install arguments that the persistent downloader cannot safely translate.");
        }
    }

    private sealed record DepotDownloaderRunResult(int ExitCode, string Output);

    // Thread-safe: HandleOutput calls AppendLine from the stdout and stderr pump tasks
    // concurrently, unlike a plain StringBuilder.
    private sealed class BoundedOutputBuffer(int capacity)
    {
        private readonly object _sync = new();
        private readonly Queue<string> _lines = new();
        private int _droppedCount;

        public void AppendLine(string line)
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

        public string ToText()
        {
            lock (_sync)
            {
                var prefix = _droppedCount == 0
                    ? []
                    : new[] { $"[{_droppedCount} earlier output line(s) omitted.]" };
                return string.Join(Environment.NewLine, prefix.Concat(_lines));
            }
        }
    }
}
