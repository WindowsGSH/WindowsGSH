using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using WindowsGSH.Core;
using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Steam;

public sealed class SteamCmdManager : ISteamCmdClient
{
    private const int MaxCapturedOutputCharacters = 4 * 1024 * 1024;
    private const int CapturedOutputPrefixCharacters = 512 * 1024;
    private const int CapturedOutputTrimTargetCharacters = 3 * 1024 * 1024;
    private const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private const uint WinTrustActionVerify = 1;
    private const uint WinTrustActionClose = 2;
    private const uint WinTrustNoUi = 2;
    private const uint WinTrustUnionChoiceFile = 1;
    private const uint WinTrustRevocationWholeChain = 1;           // WTD_REVOKE_WHOLECHAIN
    private const uint WinTrustRevocationCheckChainExcludeRoot = 0x80; // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly ModuleZipImportLimits SteamCmdZipLimits = new(
        MaxZipFileBytes: 50L * 1024 * 1024,
        MaxExtractedBytes: 200L * 1024 * 1024,
        MaxEntries: 1000);
    private static readonly TimeSpan BranchCacheMaxAge = TimeSpan.FromHours(6);
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly string _installPath;
    private readonly string _exePath;
    private readonly ISteamCredentialProvider? _credentialProvider;
    private readonly ISteamGuardCodeProvider? _codeProvider;
    private readonly SteamCmdProcessRunner _processRunner;
    private readonly Func<string, bool> _signatureVerifier;
    private readonly SteamCmdArchiveDownloader _archiveDownloader;
    private readonly Func<CancellationToken, Task>? _afterExistingInstallMoved;

    public SteamCmdManager(
        ISteamCredentialProvider? credentialProvider = null,
        ISteamGuardCodeProvider? codeProvider = null)
        : this(AppPaths.GetPath("steamcmd"), credentialProvider, codeProvider, processRunner: null, signatureVerifier: null)
    {
    }

    internal SteamCmdManager(
        string steamCmdPath,
        ISteamCredentialProvider? credentialProvider,
        SteamCmdProcessRunner? processRunner,
        Func<string, bool>? signatureVerifier = null,
        SteamCmdArchiveDownloader? archiveDownloader = null,
        Func<CancellationToken, Task>? afterExistingInstallMoved = null)
        : this(steamCmdPath, credentialProvider, codeProvider: null, processRunner, signatureVerifier, archiveDownloader, afterExistingInstallMoved)
    {
    }

    internal SteamCmdManager(
        string steamCmdPath,
        ISteamCredentialProvider? credentialProvider,
        ISteamGuardCodeProvider? codeProvider,
        SteamCmdProcessRunner? processRunner,
        Func<string, bool>? signatureVerifier = null,
        SteamCmdArchiveDownloader? archiveDownloader = null,
        Func<CancellationToken, Task>? afterExistingInstallMoved = null)
    {
        _credentialProvider = credentialProvider;
        _codeProvider = codeProvider;
        _installPath = Path.GetFullPath(steamCmdPath);
        _exePath = Path.Combine(_installPath, "steamcmd.exe");
        _processRunner = processRunner ?? RunProcessAsync;
        // When a processRunner is injected the instance is operating as a test double;
        // skip Authenticode verification unless the caller explicitly provides a verifier.
        _signatureVerifier = signatureVerifier ?? (processRunner != null ? _ => true : HasValveAuthenticodeSignature);
        _archiveDownloader = archiveDownloader ?? DownloadSteamCmdArchiveAsync;
        _afterExistingInstallMoved = afterExistingInstallMoved;
    }

    public string ExePath => _exePath;

    public async Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(_exePath))
        {
            if (_signatureVerifier(_exePath))
            {
                progress?.Report($"SteamCMD found: {_exePath}");
                return;
            }

            // Existing exe failed signature check — fall through to re-download.
            // The install directory is wiped before the verified exe is placed so that
            // stale companion files cannot survive the reinstall.
            progress?.Report("Existing steamcmd.exe failed Authenticode verification. Re-downloading...");
        }

        // ZIP and staging live in the parent directory so that the wipe of _installPath
        // during reinstall cannot destroy in-progress work.
        var parentDirectory = Path.GetDirectoryName(_installPath)
            ?? throw new InvalidOperationException("SteamCMD install path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var zipPath = Path.Combine(parentDirectory, $".steamcmd-{Guid.NewGuid():N}.zip");
        progress?.Report("Downloading SteamCMD...");

        await _archiveDownloader(zipPath, cancellationToken).ConfigureAwait(false);

        // Both paths are pre-declared before the try block so the finally clause can
        // always reach them regardless of where an exception is thrown.
        var oldInstallPath = _installPath + ".old-" + Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(parentDirectory, $".steamcmd-staging-{Guid.NewGuid():N}");
        try
        {
            progress?.Report("Verifying and extracting SteamCMD...");
            ExtractArchiveSafely(zipPath, stagingPath);
            TryDeleteFile(zipPath);

            var stagedExePath = Path.Combine(stagingPath, "steamcmd.exe");
            if (!File.Exists(stagedExePath) || !_signatureVerifier(stagedExePath))
            {
                throw new InvalidDataException(
                    "SteamCMD executable is not signed by Valve. SteamCMD was not installed.");
            }

            // Move the existing install directory aside before placing the verified exe.
            // Directory.Move is a metadata-only rename on the same volume and typically
            // succeeds even when individual files inside are open, unlike a recursive delete.
            // This guarantees the verified exe lands in a directory that contains no prior
            // companions. If the move fails, the exception propagates — we do not install.
            if (Directory.Exists(_installPath))
            {
                try
                {
                    Directory.Move(_installPath, oldInstallPath);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        $"Cannot move the existing SteamCMD directory aside before reinstall. " +
                        $"Close any applications using files in '{_installPath}' and retry.", ex);
                }
            }

            if (_afterExistingInstallMoved != null)
            {
                await _afterExistingInstallMoved(cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(_installPath);
            File.Move(stagedExePath, _exePath, overwrite: false);

            progress?.Report("Bootstrapping SteamCMD...");
            await _processRunner(new SteamCmdRunRequest(
                ["+quit"],
                _installPath,
                progress,
                cancellationToken,
                OutputCapture: null,
                EchoOutput: true,
                TailConsoleLog: false)).ConfigureAwait(false);

            // Bootstrap succeeded — now safe to discard the backup of the old install.
            TryDeleteDirectory(oldInstallPath);
            progress?.Report("SteamCMD is ready.");
        }
        catch
        {
            // Any failure after the rename (bootstrap cancel, network error, etc.) —
            // remove the incomplete new install, then restore the previous working install.
            TryDeleteFile(_exePath);
            TryDeleteDirectory(_installPath);
            RestorePreviousInstall(oldInstallPath, _installPath, progress);
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
            TryDeleteFile(zipPath);
        }
    }

    public async Task<int> InstallOrUpdateAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SteamCmdPolicy.ValidateAppId(definition.AppId);
        await EnsureInstalledAsync(null, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(Path.Combine(installPath, "steamapps"));

        var credentials = _credentialProvider?.Load();
        var safeProgress = CreateRedactingProgress(progress, credentials, branchPassword);
        var manifest = SteamAppManifestReader.Inspect(installPath, definition.AppId);
        if (manifest.State == SteamAppManifestState.Corrupt)
        {
            safeProgress?.Report("Detected a corrupt Steam appmanifest. Removing it before running SteamCMD.");
            DeleteAppManifest(installPath, definition.AppId, safeProgress);
        }

        var arguments = SteamCmdPolicy.BuildInstallArgumentList(installPath, definition, branch, branchPassword, credentials);
        progress?.Report("Running SteamCMD:");
        safeProgress?.Report($"{_exePath} {SteamCmdPolicy.FormatArgumentsForDiagnostics(arguments, credentials?.Username, branchPassword)}");

        var output = new StringBuilder();
        var exitCode = await RunCommandAsync(
            arguments,
            safeProgress,
            cancellationToken,
            output,
            tailConsoleLog: true,
            credentials: definition.LoginAnonymous ? null : credentials).ConfigureAwait(false);
        if (exitCode == 0)
        {
            return exitCode;
        }

        var classification = SteamCmdPolicy.ClassifyFailure(output.ToString(), exitCode);
        safeProgress?.Report(classification.Message);
        if (classification.RequiresSteamGuard)
        {
            safeProgress?.Report("Steam Guard is required. Run SteamCMD once in a normal console with this account, complete the Steam Guard prompt, then retry in WindowsGSH.");
        }

        if (classification.ShouldRetryAfterManifestDelete)
        {
            DeleteAppManifest(installPath, definition.AppId, safeProgress);
            safeProgress?.Report("Retrying SteamCMD once with validation after stale appmanifest recovery.");
            var retryDefinition = definition with { ValidateByDefault = true };
            var retryArguments = SteamCmdPolicy.BuildInstallArgumentList(installPath, retryDefinition, branch, branchPassword, credentials);
            output.Clear();
            exitCode = await RunCommandAsync(
                retryArguments,
                safeProgress,
                cancellationToken,
                output,
                tailConsoleLog: true,
                credentials: retryDefinition.LoginAnonymous ? null : credentials).ConfigureAwait(false);
            if (exitCode != 0)
            {
                safeProgress?.Report(SteamCmdPolicy.ClassifyFailure(output.ToString(), exitCode).Message);
            }
        }

        return exitCode;
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(
        SteamInstallDefinition definition,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        SteamCmdPolicy.ValidateAppId(definition.AppId);
        await EnsureInstalledAsync(null, cancellationToken).ConfigureAwait(false);

        if (!forceRefresh)
        {
            var cached = TryReadBranchCache(definition.AppId);
            if (cached.Count > 0)
            {
                progress?.Report($"Using cached Steam branches: {string.Join(", ", cached)}");
                return cached;
            }
        }

        var credentials = _credentialProvider?.Load();
        var safeProgress = CreateRedactingProgress(progress, credentials, branchPassword: "");
        var arguments = BuildLoginArguments(definition, credentials);
        arguments.AddRange(["-overrideminos", "+app_info_update", "1", "+app_info_print", definition.AppId, "+quit"]);
        var output = new StringBuilder();

        safeProgress?.Report("Loading Steam branch list...");
        await RunCommandAsync(
            arguments,
            safeProgress,
            cancellationToken,
            output,
            echoOutput: false,
            credentials: definition.LoginAnonymous ? null : credentials).ConfigureAwait(false);

        var branchesIndex = output.ToString().IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branchesIndex < 0)
        {
            return ["public"];
        }

        var branchOutput = output.ToString()[branchesIndex..];
        var branches = Regex.Matches(branchOutput, "\"([^\"]+)\"\\s*\\r?\\n\\s*\\{\\s*\\r?\\n\\s*\"buildid\"", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => match.Groups[1].Value)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(branch => string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(branch => branch)
            .ToArray();

        var result = branches.Length == 0 ? ["public"] : branches;
        WriteBranchCache(definition.AppId, result);
        safeProgress?.Report($"Found Steam branches: {string.Join(", ", result)}");
        return result;
    }

    public Task<int> VerifyAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallOrUpdateAsync(
            installPath,
            definition with { ValidateByDefault = true },
            branch,
            branchPassword,
            progress,
            cancellationToken);
    }

    public async Task<string?> GetRemoteBuildIdAsync(
        SteamInstallDefinition definition,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SteamCmdPolicy.ValidateAppId(definition.AppId);
        await EnsureInstalledAsync(null, cancellationToken).ConfigureAwait(false);

        var credentials = _credentialProvider?.Load();
        var safeProgress = CreateRedactingProgress(progress, credentials, branchPassword: "");
        var arguments = BuildLoginArguments(definition, credentials);
        arguments.AddRange(["-overrideminos", "+app_info_update", "1", "+app_info_print", definition.AppId, "+quit"]);
        var output = new StringBuilder();

        safeProgress?.Report("Checking remote Steam build.");
        await RunCommandAsync(
            arguments,
            safeProgress,
            cancellationToken,
            output,
            echoOutput: false,
            credentials: definition.LoginAnonymous ? null : credentials).ConfigureAwait(false);

        var wantedBranch = string.IsNullOrWhiteSpace(branch) ? "public" : branch;
        var branchPattern = $"\"{Regex.Escape(wantedBranch)}\"\\s*\\r?\\n\\s*\\{{(?<body>.*?)\\r?\\n\\s*\\}}";
        var match = Regex.Match(output.ToString(), branchPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var buildMatch = Regex.Match(match.Groups["body"].Value, "\"buildid\"\\s+\"(?<buildid>[^\"]+)\"", RegexOptions.IgnoreCase);
        return buildMatch.Success ? buildMatch.Groups["buildid"].Value : null;
    }

    private async Task<int> RunCommandAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        StringBuilder? outputCapture = null,
        bool echoOutput = true,
        bool tailConsoleLog = false,
        SteamCredentials? credentials = null)
    {
        var stdinWriterTcs = credentials != null || _codeProvider != null
            ? new TaskCompletionSource<StreamWriter?>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var passwordGate = new SteamPasswordPromptGate();
        var passwordProgress = credentials != null && stdinWriterTcs != null
            ? new SteamPasswordDetectingProgress(
                progress,
                stdinWriterTcs.Task,
                credentials.Password,
                cancellationToken,
                passwordGate)
            : progress;
        var passwordClassifyProgress = credentials != null && stdinWriterTcs != null
            ? new SteamPasswordDetectingProgress(
                null,
                stdinWriterTcs.Task,
                credentials.Password,
                cancellationToken,
                passwordGate)
            : null;

        if (_codeProvider == null)
        {
            try
            {
                return await _processRunner(new SteamCmdRunRequest(
                    arguments, _installPath, passwordProgress, cancellationToken,
                    outputCapture, echoOutput, tailConsoleLog, stdinWriterTcs,
                    ClassifyProgress: passwordClassifyProgress)).ConfigureAwait(false);
            }
            finally
            {
                stdinWriterTcs?.TrySetResult(null);
            }
        }

        var challengeChannel = Channel.CreateBounded<SteamGuardChallengeKind>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        // Shared dedup gate prevents the same prompt being queued twice when it is
        // detected through both the console-log tail and the stdout classify path.
        var dedup = new SteamGuardChallengeDedup();
        var effectiveProgress = new SteamGuardDetectingProgress(passwordProgress, challengeChannel.Writer, dedup);
        var classifyOnlyProgress = new SteamGuardDetectingProgress(passwordClassifyProgress, challengeChannel.Writer, dedup);
        stdinWriterTcs ??= new TaskCompletionSource<StreamWriter?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationId = Guid.NewGuid().ToString("N");
        var steamAccount = _credentialProvider?.Load()?.Username ?? string.Empty;

        // operationCts lets the challenge handler cancel the SteamCMD process when the
        // user explicitly dismisses the dialog without entering a code.
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = _processRunner(new SteamCmdRunRequest(
            arguments, _installPath, effectiveProgress, operationCts.Token,
            outputCapture, echoOutput, tailConsoleLog, stdinWriterTcs,
            ClassifyProgress: classifyOnlyProgress));

        // processExitCts closes an open Steam Guard dialog when the process exits.
        // channelCancellationToken only stops reading new items on external cancel,
        // so buffered challenges written before process exit are always processed.
        using var processExitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var challengeTask = HandleSteamGuardChallengesAsync(
            challengeChannel.Reader, _codeProvider, operationId, steamAccount,
            stdinWriterTcs.Task,
            channelCancellationToken: cancellationToken,
            dialogCancellationToken: processExitCts.Token,
            dedup: dedup,
            operationCts: operationCts);

        int exitCode;
        try
        {
            exitCode = await runTask.ConfigureAwait(false);
        }
        finally
        {
            challengeChannel.Writer.TryComplete();
            stdinWriterTcs.TrySetResult(null);
            processExitCts.Cancel();
        }

        try
        {
            await challengeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Process exited while the Steam Guard dialog was open — dialog was closed
            // by the cancellation callback; the operation continues with the process exit code.
        }

        return exitCode;
    }

    private List<string> BuildLoginArguments(SteamInstallDefinition definition, SteamCredentials? credentials = null)
    {
        if (definition.LoginAnonymous)
        {
            return ["+login", "anonymous"];
        }

        credentials ??= _credentialProvider?.Load();
        if (credentials == null)
        {
            return [];
        }

        if (credentials.Username.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            credentials.Username.TrimStart().StartsWith('+'))
        {
            throw new ArgumentException("Steam username contains an unsafe SteamCMD command token.");
        }

        return ["+login", credentials.Username];
    }

    private Task<int> RunProcessAsync(SteamCmdRunRequest request)
    {
        return RunToExitAsync(
            request.ArgumentTokens,
            request.WorkingDirectory,
            request.Progress,
            request.CancellationToken,
            request.OutputCapture,
            request.EchoOutput,
            request.TailConsoleLog,
            request.StdinWriterTcs,
            request.ClassifyProgress);
    }

    private static async Task DownloadSteamCmdArchiveAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using var remote = await SharedHttpClient.GetStreamAsync(DownloadUrl, cancellationToken).ConfigureAwait(false);
        await using var local = File.Create(destinationPath);
        await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
    }

    private static void RestorePreviousInstall(string oldInstallPath, string installPath, IProgress<string>? progress)
    {
        if (!Directory.Exists(oldInstallPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(installPath))
            {
                throw new IOException($"Cannot restore previous SteamCMD install because '{installPath}' still exists.");
            }

            Directory.Move(oldInstallPath, installPath);
            progress?.Report("Restored the previous SteamCMD install after reinstall failure.");
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"SteamCMD reinstall failed and WindowsGSH could not restore the previous install. " +
                $"The backup remains at '{oldInstallPath}'.", ex);
        }
    }

    private async Task<int> RunToExitAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        StringBuilder? outputCapture = null,
        bool echoOutput = true,
        bool tailConsoleLog = false,
        TaskCompletionSource<StreamWriter?>? stdinWriterTcs = null,
        IProgress<string>? classifyProgress = null)
    {
        var consoleLogPath = Path.Combine(_installPath, "logs", "console_log.txt");
        var consoleLogStartLength = tailConsoleLog && File.Exists(consoleLogPath)
            ? new FileInfo(consoleLogPath).Length
            : 0;
        using var process = new Process
        {
            StartInfo =
            {
                FileName = _exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        if (stdinWriterTcs != null)
            stdinWriterTcs.TrySetResult(process.StandardInput);
        else
            process.StandardInput.Close(); // EOF so SteamCMD does not block on stdin reads

        using var tailCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tailTask = tailConsoleLog
            ? TailConsoleLogAsync(consoleLogPath, consoleLogStartLength, progress, tailCancellation.Token)
            : Task.CompletedTask;
        var shouldEchoOutput = echoOutput && !tailConsoleLog;
        var outputTask = PumpProcessStreamAsync(
            process.StandardOutput,
            line =>
            {
                AppendCapturedOutput(outputCapture, line);
                if (shouldEchoOutput)
                {
                    progress?.Report(line);
                }
                else
                {
                    // When tailing the console log, stdout is not shown in the UI,
                    // but we still need to classify it so Steam Guard prompts are detected.
                    classifyProgress?.Report(line);
                }
            },
            cancellationToken);
        var errorTask = PumpProcessStreamAsync(
            process.StandardError,
            line => progress?.Report(line),
            cancellationToken);

        // Tie the best-effort nudge to this process run, not just the caller token. On a normal
        // successful run the caller token usually remains live; without the linked lifetime token
        // each completed run left a five-minute delay retaining the now-disposed Process object.
        _ = SendEnterPreventFreezeAsync(process, tailCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            // Must run for every exit path (success, cancellation, or any other fault from
            // the output pumps) - otherwise tailCancellation is disposed without ever being
            // cancelled, and TailConsoleLogAsync's polling loop becomes an uncancellable
            // orphan task for the rest of the process lifetime.
            tailCancellation.Cancel();
            try
            {
                await tailTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // A tail failure must not mask the primary result or exception.
            }
        }
    }

    private static void AppendCapturedOutput(StringBuilder? outputCapture, string line)
    {
        if (outputCapture == null)
        {
            return;
        }

        outputCapture.AppendLine(line);
        if (outputCapture.Length <= MaxCapturedOutputCharacters)
        {
            return;
        }

        // Retain both the beginning (authentication/failure diagnostics commonly appear there)
        // and a large rolling tail (including the requested app's VDF branch data and final
        // result), while preventing multi-GB progress output from growing memory without bound.
        var removeCount = outputCapture.Length - CapturedOutputTrimTargetCharacters;
        outputCapture.Remove(CapturedOutputPrefixCharacters, removeCount);
        outputCapture.Insert(CapturedOutputPrefixCharacters, Environment.NewLine + "[earlier SteamCMD output omitted]" + Environment.NewLine);
    }

    private static async Task HandleSteamGuardChallengesAsync(
        ChannelReader<SteamGuardChallengeKind> reader,
        ISteamGuardCodeProvider codeProvider,
        string operationId,
        string steamAccount,
        Task<StreamWriter?> stdinWriterTask,
        CancellationToken channelCancellationToken,
        CancellationToken dialogCancellationToken,
        SteamGuardChallengeDedup dedup,
        CancellationTokenSource operationCts)
    {
        var stdinWriter = await stdinWriterTask.ConfigureAwait(false);

        // channelCancellationToken stops iteration on external cancel.
        // dialogCancellationToken also fires on process exit to close any open dialog.
        await foreach (var kind in reader.ReadAllAsync(channelCancellationToken).ConfigureAwait(false))
        {
            var challenge = new SteamGuardChallengeRequest(operationId, kind, steamAccount, DateTimeOffset.UtcNow);
            if (challenge.IsExpired)
            {
                dedup.Release();
                continue;
            }

            string? code;
            try
            {
                code = await codeProvider.RequestCodeAsync(challenge, dialogCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dialogCancellationToken.IsCancellationRequested
                                                     && !channelCancellationToken.IsCancellationRequested)
            {
                // Process exited while the dialog was open — stop handling challenges.
                dedup.Release();
                break;
            }

            // Reset so SteamCMD can re-prompt with the same kind (e.g. after a wrong code).
            dedup.Release();

            if (code == null)
            {
                // User explicitly cancelled the dialog — kill the SteamCMD process.
                operationCts.Cancel();
                break;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                // Timer expired without user interaction (empty-string sentinel from provider).
                // SteamCMD may be proceeding via mobile push auth and does not need stdin input.
                // Leave the process running; processExitCts will close any open dialog when it exits.
                break;
            }

            if (stdinWriter != null)
            {
                try
                {
                    await stdinWriter.WriteLineAsync(code.AsMemory(), channelCancellationToken).ConfigureAwait(false);
                    await stdinWriter.FlushAsync(channelCancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Stdin pipe closed — process has already exited; stop trying to write.
                    break;
                }
            }
        }
    }

    private static async Task TailConsoleLogAsync(
        string consoleLogPath,
        long startPosition,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var position = startPosition;
        var pending = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(consoleLogPath))
            {
                position = await ReadNewConsoleLogLinesAsync(consoleLogPath, position, pending, progress, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadNewConsoleLogLinesAsync(
        string consoleLogPath,
        long position,
        StringBuilder pending,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            consoleLogPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (stream.Length < position)
        {
            position = 0;
        }

        if (stream.Length == position)
        {
            return position;
        }

        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        position = stream.Position;

        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                ReportBufferedConsoleLogLine(pending, progress);
                continue;
            }

            pending.Append(ch);
        }

        return position;
    }

    private static void ReportBufferedConsoleLogLine(StringBuilder line, IProgress<string>? progress)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = StripSteamConsoleLogTimestamp(line.ToString().TrimEnd());
        line.Clear();
        if (!string.IsNullOrWhiteSpace(text))
        {
            progress?.Report(text);
        }
    }

    private static string StripSteamConsoleLogTimestamp(string text)
    {
        return Regex.Replace(text, @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s*", "");
    }

    private static async Task PumpProcessStreamAsync(StreamReader reader, Action<string> reportLine, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        Task<int>? pendingRead = null;

        while (true)
        {
            pendingRead ??= reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).AsTask();

            int read;
            if (line.Length > 0)
            {
                // Race against a short timeout so that prompts written without a trailing
                // newline (e.g. SteamCMD's Steam Guard code prompt) are still detected
                // when SteamCMD blocks waiting for stdin after writing the partial line.
                try
                {
                    read = await pendingRead.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
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
                var ch = buffer[index];
                if (ch is '\r' or '\n')
                {
                    ReportBufferedLine(line, reportLine);
                }
                else
                {
                    line.Append(ch);
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

        var text = line.ToString().TrimEnd();
        line.Clear();
        if (!string.IsNullOrWhiteSpace(text))
        {
            reportLine(text);
        }
    }

    private static async Task SendEnterPreventFreezeAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                for (var j = 0; j < 3; j++)
                {
                    if (process.HasExited)
                    {
                        return;
                    }

                    process.StandardInput.WriteLine(string.Empty);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Best-effort nudge, matching the WindowsGSM workaround.
        }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destinationDirectory)
    {
        ModuleZipImportValidator.Validate(archivePath, destinationDirectory, SteamCmdZipLimits);
        ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
    }

    internal static bool HasValveAuthenticodeSignature(string executablePath)
    {
        if (!HasValidAuthenticodeSignature(executablePath))
        {
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            return IsValveCertificate(certificate);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValveCertificate(X509Certificate2 certificate)
    {
        var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return simpleName.Equals("Valve", StringComparison.OrdinalIgnoreCase) ||
            certificate.Subject.Contains("O=Valve Corp.", StringComparison.OrdinalIgnoreCase) ||
            certificate.Subject.Contains("O=Valve Corporation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidAuthenticodeSignature(string executablePath)
    {
        var filePathPtr = IntPtr.Zero;
        var fileInfoPtr = IntPtr.Zero;
        var trustData = new WinTrustData();

        try
        {
            filePathPtr = Marshal.StringToCoTaskMemUni(executablePath);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPtr
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustNoUi,
                RevocationChecks = WinTrustRevocationWholeChain,
                UnionChoice = WinTrustUnionChoiceFile,
                FileInfo = fileInfoPtr,
                StateAction = WinTrustActionVerify,
                ProvFlags = WinTrustRevocationCheckChainExcludeRoot
            };

            return WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref trustData) == 0;
        }
        finally
        {
            if (trustData.StateData != IntPtr.Zero)
            {
                trustData.StateAction = WinTrustActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, ref trustData);
            }

            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPtr);
            }

            if (filePathPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(filePathPtr);
            }
        }
    }

    private static IProgress<string>? CreateRedactingProgress(
        IProgress<string>? progress,
        SteamCredentials? credentials,
        string branchPassword)
    {
        return progress == null
            ? null
            : new RedactingProgress(progress, credentials?.Username, credentials?.Password, branchPassword);
    }

    private string GetBranchCachePath(string appId)
    {
        return Path.Combine(_installPath, "branch-cache", $"{appId}.json");
    }

    private IReadOnlyList<string> TryReadBranchCache(string appId)
    {
        try
        {
            var path = GetBranchCachePath(appId);
            if (!File.Exists(path))
            {
                return [];
            }

            var cache = JsonSerializer.Deserialize<SteamBranchCache>(File.ReadAllText(path));
            if (cache == null || DateTimeOffset.UtcNow - cache.UpdatedUtc > BranchCacheMaxAge)
            {
                return [];
            }

            return cache.Branches.Where(branch => !string.IsNullOrWhiteSpace(branch)).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void WriteBranchCache(string appId, IReadOnlyList<string> branches)
    {
        try
        {
            var path = GetBranchCachePath(appId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new SteamBranchCache(DateTimeOffset.UtcNow, branches.ToArray()), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static bool DeleteAppManifest(string installPath, string appId, IProgress<string>? progress)
    {
        try
        {
            var manifestPath = Path.Combine(installPath, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            File.Delete(manifestPath);
            progress?.Report($"Deleted stale Steam appmanifest: {manifestPath}");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Could not delete stale appmanifest: {ex.Message}");
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class SteamGuardDetectingProgress(
        IProgress<string>? inner,
        ChannelWriter<SteamGuardChallengeKind> challengeWriter,
        SteamGuardChallengeDedup dedup) : IProgress<string>
    {
        public void Report(string value)
        {
            inner?.Report(value);
            var kind = SteamCmdPolicy.ClassifyChallengeLine(value);
            if (kind.HasValue && dedup.TryAcquire())
            {
                challengeWriter.TryWrite(kind.Value);
            }
        }
    }

    private sealed class SteamGuardChallengeDedup
    {
        // At most one challenge may be queued or handled at a time, regardless of kind.
        // Rejecting by kind would allow a simultaneously-detected Email + Unknown pair to
        // queue two dialogs. Release() clears the flag after the dialog closes so genuine
        // SteamCMD retry prompts are allowed immediately.
        private readonly object _lock = new();
        private bool _active;

        public bool TryAcquire()
        {
            lock (_lock)
            {
                if (_active) return false;
                _active = true;
                return true;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                _active = false;
            }
        }
    }

    private sealed class SteamPasswordDetectingProgress(
        IProgress<string>? inner,
        Task<StreamWriter?> stdinWriterTask,
        string password,
        CancellationToken cancellationToken,
        SteamPasswordPromptGate gate) : IProgress<string>
    {
        public void Report(string value)
        {
            inner?.Report(value);
            if (SteamCmdPolicy.IsPasswordPrompt(value) && gate.TryAcquire())
            {
                _ = WritePasswordAsync(stdinWriterTask, password, cancellationToken);
            }
        }

        private static async Task WritePasswordAsync(
            Task<StreamWriter?> stdinWriterTask,
            string password,
            CancellationToken cancellationToken)
        {
            try
            {
                var writer = await stdinWriterTask.ConfigureAwait(false);
                if (writer == null)
                {
                    return;
                }

                await writer.WriteLineAsync(password.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // SteamCMD exited or the operation was cancelled while authentication was pending.
            }
        }
    }

    private sealed class SteamPasswordPromptGate
    {
        private int _submitted;

        public bool TryAcquire() => Interlocked.Exchange(ref _submitted, 1) == 0;
    }

    private sealed class RedactingProgress(
        IProgress<string> inner,
        string? username,
        string? password,
        string? branchPassword) : IProgress<string>
    {
        public void Report(string value)
        {
            inner.Report(SteamCmdPolicy.MaskSensitiveText(value, username, password, branchPassword));
        }
    }

    private sealed record SteamBranchCache(DateTimeOffset UpdatedUtc, string[] Branches);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

internal sealed record SteamCmdRunRequest(
    IReadOnlyList<string> ArgumentTokens,
    string WorkingDirectory,
    IProgress<string>? Progress,
    CancellationToken CancellationToken,
    StringBuilder? OutputCapture,
    bool EchoOutput,
    bool TailConsoleLog,
    TaskCompletionSource<StreamWriter?>? StdinWriterTcs = null,
    IProgress<string>? ClassifyProgress = null)
{
    public string Arguments => SteamCmdPolicy.FormatArguments(ArgumentTokens);
}

internal delegate Task<int> SteamCmdProcessRunner(SteamCmdRunRequest request);

internal delegate Task SteamCmdArchiveDownloader(string destinationPath, CancellationToken cancellationToken);
