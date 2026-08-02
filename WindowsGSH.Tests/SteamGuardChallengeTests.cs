using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SteamGuardChallengeTests
{
    // ── ClassifyChallengeLine ────────────────────────────────────────────────

    [Theory]
    [InlineData("Enter the current code sent to your email address:", SteamGuardChallengeKind.Email)]
    [InlineData("Please check your email for a Steam Guard code", SteamGuardChallengeKind.Email)]
    [InlineData("Enter the two factor code from your mobile authenticator app:", SteamGuardChallengeKind.MobileAuthenticator)]
    [InlineData("Two-factor code:", SteamGuardChallengeKind.MobileAuthenticator)]
    [InlineData("2-factor authentication code required", SteamGuardChallengeKind.MobileAuthenticator)]
    [InlineData("Enter Steam Guard code for account 'user':", SteamGuardChallengeKind.Unknown)]
    [InlineData("Steam Guard code required", SteamGuardChallengeKind.Unknown)]
    public void ClassifyChallengeLine_identifies_steam_guard_prompts(string line, SteamGuardChallengeKind expected)
    {
        var result = SteamCmdPolicy.ClassifyChallengeLine(line);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Logging in user 'account' to Steam Public...")]
    [InlineData("Update state (0x3) reconfiguring, progress: 0.00 (0 / 0)")]
    [InlineData("Success! App '232250' fully installed.")]
    [InlineData("Invalid or missing authentication code.")]
    [InlineData("")]
    // Push-auth informational lines must not open a code-entry dialog.
    [InlineData("Waiting for mobile authenticator approval")]
    [InlineData("Please accept the Steam Guard Mobile Authenticator notification on your phone")]
    [InlineData("two-factor authentication")]
    // Completion/status lines must not re-open a dialog after a code has been submitted or used.
    [InlineData("Steam Guard code accepted")]
    [InlineData("Mobile authenticator code accepted")]
    [InlineData("Two-factor code verified")]
    [InlineData("Steam Guard code provided.")]
    public void ClassifyChallengeLine_returns_null_for_non_challenge_lines(string line)
    {
        var result = SteamCmdPolicy.ClassifyChallengeLine(line);

        Assert.Null(result);
    }

    // ── SteamGuardChallengeRequest expiry ────────────────────────────────────

    [Fact]
    public void Challenge_is_not_expired_when_just_created()
    {
        var challenge = new SteamGuardChallengeRequest(
            "op-1", SteamGuardChallengeKind.Email, "user", DateTimeOffset.UtcNow);

        Assert.False(challenge.IsExpired);
    }

    [Fact]
    public void Challenge_is_expired_when_detected_more_than_lifetime_ago()
    {
        var challenge = new SteamGuardChallengeRequest(
            "op-1", SteamGuardChallengeKind.Email, "user",
            DateTimeOffset.UtcNow - SteamGuardChallengeRequest.Lifetime - TimeSpan.FromSeconds(1));

        Assert.True(challenge.IsExpired);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void SteamGuardWindow_classifies_user_and_programmatic_closes(
        bool closingForOperation,
        bool submitted,
        bool expectedUserCancellation)
    {
        Assert.Equal(
            expectedUserCancellation,
            SteamGuardCodeWindow.ShouldTreatCloseAsUserCancellation(closingForOperation, submitted));
    }

    // ── SteamCmdManager challenge handling ───────────────────────────────────

    [Fact]
    public async Task SteamCmdManager_submits_code_to_stdin_when_challenge_detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var receivedChallenges = new List<SteamGuardChallengeRequest>();
            var codeProvider = new FakeSteamGuardCodeProvider((challenge, _) =>
            {
                receivedChallenges.Add(challenge);
                return Task.FromResult<string?>("ABCDE");
            });

            // Provide a real StreamWriter so HandleSteamGuardChallengesAsync can write to it
            using var stdinCapture = new MemoryStream();
            var stdinWriter = new StreamWriter(stdinCapture, leaveOpen: true) { AutoFlush = true };

            var manager = new SteamCmdManager(
                root,
                credentialProvider: new FakeCredentialProvider(new SteamCredentials("testuser", "pass")),
                codeProvider: codeProvider,
                processRunner: request =>
                {
                    request.StdinWriterTcs?.TrySetResult(stdinWriter);
                    request.Progress?.Report("Enter Steam Guard code for account 'testuser':");
                    return Task.FromResult(0);
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: false),
                branch: "",
                branchPassword: "");

            Assert.Single(receivedChallenges);
            Assert.Equal(SteamGuardChallengeKind.Unknown, receivedChallenges[0].Kind);
            Assert.Equal("testuser", receivedChallenges[0].SteamAccount);
            Assert.False(receivedChallenges[0].IsExpired);

            // Verify the code was actually written to stdin
            stdinCapture.Seek(0, SeekOrigin.Begin);
            using var stdinReader = new StreamReader(stdinCapture);
            var writtenLine = await stdinReader.ReadLineAsync();
            Assert.Equal("ABCDE", writtenLine);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_does_not_call_provider_when_no_challenge_line_emitted()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var providerCalled = false;
            var codeProvider = new FakeSteamGuardCodeProvider((_, _) =>
            {
                providerCalled = true;
                return Task.FromResult<string?>(null);
            });
            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: request =>
                {
                    request.Progress?.Report("Success! App '999' fully installed.");
                    return Task.FromResult(0);
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.False(providerCalled);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_provider_returns_null_when_challenge_expires()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var receivedChallenges = new List<SteamGuardChallengeRequest>();
            var codeProvider = new FakeSteamGuardCodeProvider((challenge, _) =>
            {
                receivedChallenges.Add(challenge);
                return Task.FromResult<string?>(null);
            });
            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: request =>
                {
                    request.Progress?.Report("Enter the current code sent to your email address:");
                    return Task.FromResult(5);
                });

            var exitCode = await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.Equal(5, exitCode);
            Assert.Single(receivedChallenges);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_cancellation_stops_challenge_handling()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            using var cts = new CancellationTokenSource();
            var codeProvider = new FakeSteamGuardCodeProvider(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });
            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: request =>
                {
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    cts.Cancel();
                    return Task.FromResult(0);
                });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.InstallOrUpdateAsync(
                    Path.Combine(root, "server"),
                    new SteamInstallDefinition("999", LoginAnonymous: true),
                    branch: "",
                    branchPassword: "",
                    cancellationToken: cts.Token));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_code_does_not_appear_in_progress_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var progressLines = new List<string>();
            var codeProvider = new FakeSteamGuardCodeProvider((_, _) =>
                Task.FromResult<string?>("SECRET1"));
            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: request =>
                {
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    return Task.FromResult(0);
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "",
                progress: new InlineProgress<string>(progressLines.Add));

            Assert.DoesNotContain(progressLines, line =>
                line.Contains("SECRET1", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_does_not_open_second_dialog_when_different_kind_arrives_while_first_is_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var callCount = 0;
            var firstDialogCanClose = new TaskCompletionSource();
            var codeProvider = new FakeSteamGuardCodeProvider(async (_, _) =>
            {
                callCount++;
                await firstDialogCanClose.Task;
                return "CODE1";
            });

            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: async request =>
                {
                    request.StdinWriterTcs?.TrySetResult(null);
                    // First path classifies this as Unknown
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    await Task.Delay(50);
                    // Second detection path classifies the output differently (Email) but
                    // the challenge is still for the same login attempt — only one dialog wanted.
                    request.Progress?.Report("Please check your email for a Steam Guard code");
                    firstDialogCanClose.TrySetResult();
                    return 0;
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.Equal(1, callCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_does_not_open_second_dialog_when_same_kind_arrives_while_first_is_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var callCount = 0;
            var firstDialogCanClose = new TaskCompletionSource();
            var codeProvider = new FakeSteamGuardCodeProvider(async (_, _) =>
            {
                callCount++;
                await firstDialogCanClose.Task;
                return "CODE1";
            });

            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: async request =>
                {
                    request.StdinWriterTcs?.TrySetResult(null);
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    // Wait well beyond 500 ms so a time-window gate would have expired,
                    // while the first dialog is still open (provider is blocked above).
                    await Task.Delay(600);
                    // Same prompt from the console-log tail arriving late
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    // Allow the first dialog to close, then exit
                    firstDialogCanClose.TrySetResult();
                    return 0;
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.Equal(1, callCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_calls_provider_twice_when_same_kind_prompt_arrives_after_first_code_submitted()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var callCount = 0;
            var codeProvider = new FakeSteamGuardCodeProvider((_, _) =>
            {
                callCount++;
                return Task.FromResult<string?>("CODE" + callCount);
            });

            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: async request =>
                {
                    // Resolve stdin immediately so the challenge handler can start reading
                    // from the channel without waiting for the process to exit.
                    request.StdinWriterTcs?.TrySetResult(null);
                    // First prompt — simulates initial Steam Guard request
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    // Pause long enough for the challenge handler to process the first prompt
                    // and reset the dedup gate before the second prompt arrives.
                    await Task.Delay(200);
                    // Second prompt — simulates SteamCMD re-requesting after an invalid code
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    return 0;
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.Equal(2, callCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_GetBranchesAsync_uses_anonymous_login_when_LoginAnonymous_is_true_even_if_credentials_supplied()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        try
        {
            string? capturedArguments = null;
            var manager = new SteamCmdManager(
                root,
                credentialProvider: new FakeCredentialProvider(new SteamCredentials("myaccount", "mypassword")),
                codeProvider: null,
                processRunner: request =>
                {
                    capturedArguments ??= request.Arguments;
                    return Task.FromResult(0);
                });

            await manager.GetBranchesAsync(
                new SteamInstallDefinition("999", LoginAnonymous: true),
                forceRefresh: true);

            Assert.NotNull(capturedArguments);
            Assert.Contains("+login anonymous", capturedArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("myaccount", capturedArguments, StringComparison.Ordinal);
            Assert.DoesNotContain("mypassword", capturedArguments, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_build_checks_do_not_log_off_cached_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        try
        {
            var capturedArguments = new List<IReadOnlyList<string>>();
            var manager = new SteamCmdManager(
                root,
                credentialProvider: new FakeCredentialProvider(new SteamCredentials("myaccount", "mypassword")),
                codeProvider: null,
                processRunner: request =>
                {
                    capturedArguments.Add(request.ArgumentTokens);
                    request.StdinWriterTcs?.TrySetResult(null);
                    return Task.FromResult(0);
                });

            await manager.GetBranchesAsync(
                new SteamInstallDefinition("999", LoginAnonymous: false),
                forceRefresh: true);
            await manager.GetRemoteBuildIdAsync(
                new SteamInstallDefinition("999", LoginAnonymous: false),
                branch: "public");

            Assert.Equal(2, capturedArguments.Count);
            Assert.All(capturedArguments, arguments =>
            {
                Assert.Contains("+login", arguments);
                Assert.Contains("myaccount", arguments);
                Assert.DoesNotContain(arguments, argument => argument.Equals("+logoff", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("+quit", arguments[^1]);
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_uses_cached_login_and_submits_password_only_when_prompted()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            using var stdinCapture = new MemoryStream();
            using var stdinWriter = new StreamWriter(stdinCapture, leaveOpen: true) { AutoFlush = true };
            IReadOnlyList<string>? capturedArguments = null;

            var manager = new SteamCmdManager(
                root,
                credentialProvider: new FakeCredentialProvider(new SteamCredentials("myaccount", "mypassword")),
                codeProvider: null,
                processRunner: async request =>
                {
                    capturedArguments = request.ArgumentTokens;
                    request.StdinWriterTcs?.TrySetResult(stdinWriter);
                    request.Progress?.Report("Password:");

                    for (var attempt = 0; attempt < 50 && stdinCapture.Length == 0; attempt++)
                    {
                        await Task.Delay(10);
                    }

                    return 0;
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: false),
                branch: "",
                branchPassword: "");

            Assert.NotNull(capturedArguments);
            Assert.Contains("+login", capturedArguments);
            Assert.Contains("myaccount", capturedArguments);
            Assert.DoesNotContain("mypassword", capturedArguments);

            stdinCapture.Position = 0;
            using var reader = new StreamReader(stdinCapture);
            Assert.Equal("mypassword", await reader.ReadLineAsync());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_does_not_submit_password_when_cached_login_succeeds_without_prompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            using var stdinCapture = new MemoryStream();
            using var stdinWriter = new StreamWriter(stdinCapture, leaveOpen: true) { AutoFlush = true };

            var manager = new SteamCmdManager(
                root,
                credentialProvider: new FakeCredentialProvider(new SteamCredentials("myaccount", "mypassword")),
                codeProvider: null,
                processRunner: request =>
                {
                    request.StdinWriterTcs?.TrySetResult(stdinWriter);
                    request.Progress?.Report("Logging in user 'myaccount' to Steam Public... OK");
                    return Task.FromResult(0);
                });

            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: false),
                branch: "",
                branchPassword: "");

            Assert.Equal(0, stdinCapture.Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_cancels_process_when_user_explicitly_cancels_dialog()
    {
        // Regression: when the provider returns null (user explicitly cancelled the dialog),
        // the challenge handler must cancel the SteamCMD process.  Without the fix the
        // process blocks forever waiting for stdin and the operation never completes.
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var codeProvider = new FakeSteamGuardCodeProvider((_, _) =>
                Task.FromResult<string?>(null)); // null = user clicked Cancel

            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: async request =>
                {
                    // Resolve stdin so the challenge handler can proceed immediately.
                    request.StdinWriterTcs?.TrySetResult(null);
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    // Block until the challenge handler cancels the operation — this simulates
                    // SteamCMD waiting indefinitely for the user to type a code into stdin.
                    await Task.Delay(Timeout.Infinite, request.CancellationToken);
                    return 0; // never reached
                });

            // The operation must terminate (via OCE) rather than hang indefinitely.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.InstallOrUpdateAsync(
                    Path.Combine(root, "server"),
                    new SteamInstallDefinition("999", LoginAnonymous: true),
                    branch: "",
                    branchPassword: ""));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SteamCmdManager_lets_process_continue_when_dialog_timer_expires()
    {
        // When the dialog timer expires without user interaction the provider returns "".
        // The challenge handler must NOT cancel the process — SteamCMD may be proceeding
        // via mobile push auth and does not need stdin input.
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "steamcmd.exe"), "fake");
        Directory.CreateDirectory(Path.Combine(root, "server", "steamapps"));
        try
        {
            var codeProvider = new FakeSteamGuardCodeProvider((_, _) =>
                Task.FromResult<string?>(string.Empty)); // "" = timer expired, not user cancel

            var processCompleted = false;
            var manager = new SteamCmdManager(
                root,
                credentialProvider: null,
                codeProvider: codeProvider,
                processRunner: async request =>
                {
                    request.StdinWriterTcs?.TrySetResult(null);
                    request.Progress?.Report("Enter Steam Guard code for account 'user':");
                    // Simulate SteamCMD completing via push auth after the dialog expires.
                    await Task.Delay(50);
                    processCompleted = true;
                    return 0;
                });

            // Must not throw — the process should run to completion.
            await manager.InstallOrUpdateAsync(
                Path.Combine(root, "server"),
                new SteamInstallDefinition("999", LoginAnonymous: true),
                branch: "",
                branchPassword: "");

            Assert.True(processCompleted);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class FakeSteamGuardCodeProvider(
        Func<SteamGuardChallengeRequest, CancellationToken, Task<string?>> handler)
        : ISteamGuardCodeProvider
    {
        public Task<string?> RequestCodeAsync(SteamGuardChallengeRequest challenge, CancellationToken cancellationToken)
            => handler(challenge, cancellationToken);
    }

    private sealed class FakeCredentialProvider(SteamCredentials credentials) : ISteamCredentialProvider
    {
        public SteamCredentials? Load() => credentials;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
