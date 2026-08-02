using System.IO.Compression;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Steam;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class SteamCmdPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));

    public SteamCmdPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Build_arguments_include_login_branch_password_custom_and_validate()
    {
        var arguments = SteamCmdPolicy.BuildInstallArgumentList(
            @"D:\Servers\Test Server",
            new SteamInstallDefinition("123", LoginAnonymous: false, ValidateByDefault: true, ModName: "testmod", CustomArguments: "-foo bar"),
            "beta",
            "branch-secret",
            new SteamCredentials("account", "login-secret"));

        Assert.Equal(@"D:\Servers\Test Server", arguments[1]);
        Assert.Equal(["+login", "account"], arguments.Skip(2).Take(2));
        Assert.DoesNotContain("login-secret", arguments);
        Assert.Contains("testmod", arguments);
        Assert.Contains("branch-secret", arguments);
        Assert.Contains("-foo", arguments);
        Assert.Equal("+quit", arguments[^1]);
    }

    [Theory]
    [InlineData("123 +quit")]
    [InlineData("+quit")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Install_arguments_reject_invalid_app_ids(string appId)
    {
        Assert.Throws<ArgumentException>(() => SteamCmdPolicy.BuildInstallArgumentList(
            @"D:\Servers\Test", new SteamInstallDefinition(appId), "public", "", null));
    }

    [Theory]
    [InlineData("+quit")]
    [InlineData("-beta public +force_install_dir C:\\Other")]
    public void Install_arguments_reject_custom_steam_commands(string customArguments)
    {
        Assert.Throws<ArgumentException>(() => SteamCmdPolicy.BuildInstallArgumentList(
            @"D:\Servers\Test",
            new SteamInstallDefinition("123", CustomArguments: customArguments),
            "public", "", null));
    }

    [Theory]
    [InlineData("branch", "+quit", "mod")]
    [InlineData("+quit", "password", "mod")]
    [InlineData("branch", "password", "+force_install_dir")]
    public void Install_arguments_reject_dynamic_values_that_become_commands(
        string branch, string password, string modName)
    {
        Assert.Throws<ArgumentException>(() => SteamCmdPolicy.BuildInstallArgumentList(
            @"D:\Servers\Test",
            new SteamInstallDefinition("123", ModName: modName),
            branch, password, null));
    }

    [Fact]
    public void Install_arguments_keep_quotes_and_whitespace_inside_single_tokens()
    {
        var arguments = SteamCmdPolicy.BuildInstallArgumentList(
            "D:\\Servers\\A \"quoted\" server",
            new SteamInstallDefinition("123", LoginAnonymous: false, ModName: "mod \"one\""),
            "beta \"one\"", "password \"one\"", new SteamCredentials("user \"one\"", "secret"));

        Assert.Contains("D:\\Servers\\A \"quoted\" server", arguments);
        Assert.Contains("user \"one\"", arguments);
        Assert.Contains("beta \"one\"", arguments);
        Assert.Contains("password \"one\"", arguments);
        Assert.Contains("mod \"one\"", arguments);
    }

    [Fact]
    public void App_90_emits_four_update_commands()
    {
        var arguments = SteamCmdPolicy.BuildInstallArguments(
            "server",
            new SteamInstallDefinition("90"),
            "",
            "",
            credentials: null);

        Assert.Equal(4, CountOccurrences(arguments, "+app_update 90"));
    }

    [Theory]
    [InlineData("Steam Guard code required", SteamCmdFailureKind.SteamGuard)]
    [InlineData("Account Logon Denied", SteamCmdFailureKind.SteamGuard)]
    [InlineData("Invalid Password", SteamCmdFailureKind.LoginRequired)]
    [InlineData("ERROR! No subscription", SteamCmdFailureKind.AccessDenied)]
    [InlineData("Access Denied", SteamCmdFailureKind.AccessDenied)]
    [InlineData("state is 0x6 after update job", SteamCmdFailureKind.StaleManifest)]
    public void Failure_classification_returns_actionable_kind(string output, SteamCmdFailureKind expected)
    {
        var result = SteamCmdPolicy.ClassifyFailure(output, 8);

        Assert.Equal(expected, result.Kind);
        Assert.DoesNotContain(output, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_text_masks_login_and_beta_passwords()
    {
        var text = "steamcmd +login \"account\" \"login-secret\" +app_update 123 -betapassword \"branch-secret\" login-secret";

        var masked = SteamCmdPolicy.MaskSensitiveText(text, "account", "login-secret", "branch-secret");

        Assert.DoesNotContain("account", masked);
        Assert.DoesNotContain("login-secret", masked);
        Assert.DoesNotContain("branch-secret", masked);
        Assert.Contains("***", masked);
    }

    [Fact]
    public void Sensitive_text_does_not_consume_command_after_username_only_login()
    {
        const string text = "steamcmd +login \"account\" +app_update 2329680 validate +quit";

        var masked = SteamCmdPolicy.MaskSensitiveText(text, "account");

        Assert.Contains("+login \"***\" +app_update 2329680 validate +quit", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password:")]
    [InlineData("Enter password:")]
    [InlineData("Please enter the password for account:")]
    public void Password_prompt_classification_accepts_interactive_prompts(string line)
    {
        Assert.True(SteamCmdPolicy.IsPasswordPrompt(line));
    }

    [Theory]
    [InlineData("Invalid Password")]
    [InlineData("The password was rejected")]
    [InlineData("Saved password is available")]
    [InlineData("Steam Guard code:")]
    public void Password_prompt_classification_rejects_status_lines(string line)
    {
        Assert.False(SteamCmdPolicy.IsPasswordPrompt(line));
    }

    [Fact]
    public async Task VerifyAsync_forces_validate_flag_even_when_definition_has_ValidateByDefault_false()
    {
        var steamCmdPath = Path.Combine(_root, "steamcmd");
        var installPath = Path.Combine(_root, "server");
        Directory.CreateDirectory(steamCmdPath);
        Directory.CreateDirectory(Path.Combine(installPath, "steamapps"));
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.exe"), "fake");

        var capturedArguments = new List<string>();
        var manager = new SteamCmdManager(
            steamCmdPath,
            credentialProvider: null,
            request =>
            {
                capturedArguments.Add(request.Arguments);
                return Task.FromResult(0);
            },
            signatureVerifier: _ => true);

        await manager.VerifyAsync(
            installPath,
            new SteamInstallDefinition("999", LoginAnonymous: true, ValidateByDefault: false, CustomArguments: ""),
            branch: "",
            branchPassword: "");

        Assert.Single(capturedArguments);
        Assert.Contains(" validate", capturedArguments[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_manifest_failure_retries_once_with_validation_and_masks_progress()
    {
        var steamCmdPath = Path.Combine(_root, "steamcmd");
        var installPath = Path.Combine(_root, "server");
        Directory.CreateDirectory(steamCmdPath);
        Directory.CreateDirectory(Path.Combine(installPath, "steamapps"));
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.exe"), "fake");
        File.WriteAllText(
            Path.Combine(installPath, "steamapps", "appmanifest_123.acf"),
            "\"AppState\" { \"appid\" \"123\" \"buildid\" \"1\" }");

        var calls = new List<SteamCmdRunRequest>();
        var progress = new List<string>();
        var runCount = 0;
        var manager = new SteamCmdManager(
            steamCmdPath,
            new TestCredentialProvider(new SteamCredentials("account", "login-secret")),
            request =>
            {
                calls.Add(request);
                runCount++;
                if (runCount == 1)
                {
                    request.OutputCapture?.AppendLine("state is 0x6 after update job login-secret");
                    request.Progress?.Report("echo login-secret branch-secret");
                    return Task.FromResult(8);
                }

                return Task.FromResult(0);
            },
            signatureVerifier: _ => true);

        var exitCode = await manager.InstallOrUpdateAsync(
            installPath,
            new SteamInstallDefinition("123", LoginAnonymous: false, ValidateByDefault: false),
            "",
            "branch-secret",
            new InlineProgress<string>(progress.Add));

        Assert.Equal(0, exitCode);
        Assert.Equal(2, calls.Count);
        Assert.DoesNotContain(" validate", calls[0].Arguments, StringComparison.Ordinal);
        Assert.Contains(" validate", calls[1].Arguments, StringComparison.Ordinal);
        Assert.Contains(progress, line => line.Contains("Retrying SteamCMD once with validation", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, line => line.Contains("login-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, line => line.Contains("branch-secret", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(installPath, "steamapps", "appmanifest_123.acf")));
    }

    [Fact]
    public async Task Stale_manifest_retry_does_not_repeat_after_second_failure()
    {
        var steamCmdPath = Path.Combine(_root, "steamcmd");
        var installPath = Path.Combine(_root, "server");
        Directory.CreateDirectory(steamCmdPath);
        Directory.CreateDirectory(Path.Combine(installPath, "steamapps"));
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.exe"), "fake");

        var calls = 0;
        var manager = new SteamCmdManager(
            steamCmdPath,
            credentialProvider: null,
            request =>
            {
                calls++;
                request.OutputCapture?.AppendLine("missing manifest");
                return Task.FromResult(8);
            },
            signatureVerifier: _ => true);

        var exitCode = await manager.InstallOrUpdateAsync(
            installPath,
            new SteamInstallDefinition("123"),
            "",
            "");

        Assert.Equal(8, exitCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EnsureInstalledAsync_restores_previous_install_when_failure_occurs_after_old_install_is_moved()
    {
        var steamCmdPath = Path.Combine(_root, "steamcmd");
        Directory.CreateDirectory(steamCmdPath);
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.exe"), "old executable");
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.oldconfig"), "old config");

        var progress = new List<string>();
        var manager = new SteamCmdManager(
            steamCmdPath,
            credentialProvider: null,
            processRunner: _ => throw new InvalidOperationException("Process runner should not be called."),
            signatureVerifier: path => File.ReadAllText(path) == "new executable",
            archiveDownloader: (destinationPath, _) =>
            {
                CreateSteamCmdArchive(destinationPath, "new executable");
                return Task.CompletedTask;
            },
            afterExistingInstallMoved: _ => throw new IOException("Simulated failure after rename."));

        await Assert.ThrowsAsync<IOException>(() =>
            manager.EnsureInstalledAsync(new InlineProgress<string>(progress.Add)));

        Assert.Equal("old executable", File.ReadAllText(Path.Combine(steamCmdPath, "steamcmd.exe")));
        Assert.Equal("old config", File.ReadAllText(Path.Combine(steamCmdPath, "steamcmd.oldconfig")));
        Assert.Contains(progress, line => line.Contains("Restored the previous SteamCMD install", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(_root, "steamcmd.old-*"));
    }

    [Fact]
    public async Task EnsureInstalledAsync_restores_previous_install_when_bootstrap_fails_after_new_exe_is_placed()
    {
        var steamCmdPath = Path.Combine(_root, "steamcmd");
        Directory.CreateDirectory(steamCmdPath);
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.exe"), "old executable");
        File.WriteAllText(Path.Combine(steamCmdPath, "steamcmd.oldconfig"), "old config");

        var progress = new List<string>();
        var manager = new SteamCmdManager(
            steamCmdPath,
            credentialProvider: null,
            processRunner: request =>
            {
                Assert.Equal("+quit", request.Arguments);
                Assert.True(File.Exists(Path.Combine(request.WorkingDirectory, "steamcmd.exe")));
                throw new InvalidOperationException("Simulated bootstrap failure.");
            },
            signatureVerifier: path => File.ReadAllText(path) == "new executable",
            archiveDownloader: (destinationPath, _) =>
            {
                CreateSteamCmdArchive(destinationPath, "new executable");
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureInstalledAsync(new InlineProgress<string>(progress.Add)));

        Assert.Equal("old executable", File.ReadAllText(Path.Combine(steamCmdPath, "steamcmd.exe")));
        Assert.Equal("old config", File.ReadAllText(Path.Combine(steamCmdPath, "steamcmd.oldconfig")));
        Assert.Contains(progress, line => line.Contains("Restored the previous SteamCMD install", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(_root, "steamcmd.old-*"));
    }

    [Fact]
    public void Build_arguments_uses_anonymous_login_when_LoginAnonymous_is_true_even_if_credentials_supplied()
    {
        var arguments = SteamCmdPolicy.BuildInstallArguments(
            @"D:\Servers\Test",
            new SteamInstallDefinition("999", LoginAnonymous: true),
            branch: "",
            branchPassword: "",
            credentials: new SteamCredentials("myaccount", "mypassword"));

        Assert.Contains("+login anonymous", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("myaccount", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("mypassword", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_classification_returns_AccessDenied_not_SteamGuard_when_No_subscription_appears_alongside_auth_phrases()
    {
        // A successful mobile-authenticator login leaves "two-factor" phrases in SteamCMD output.
        // If the app is then not owned, we should report AccessDenied, not SteamGuard.
        const string output = "two-factor authentication\r\nLogging in user... OK\r\nERROR! No subscription";

        var result = SteamCmdPolicy.ClassifyFailure(output, 8);

        Assert.Equal(SteamCmdFailureKind.AccessDenied, result.Kind);
    }

    [Fact]
    public void ExtractArchiveSafely_rejects_path_traversal()
    {
        var archivePath = Path.Combine(_root, "steamcmd.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unsafe");
        }

        Assert.Throws<InvalidOperationException>(() =>
            SteamCmdManager.ExtractArchiveSafely(
                archivePath,
                Path.Combine(_root, "steamcmd")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void HasValveAuthenticodeSignature_rejects_unsigned_executable()
    {
        var executablePath = Path.Combine(_root, "steamcmd.exe");
        File.WriteAllText(executablePath, "not a signed executable");

        Assert.False(SteamCmdManager.HasValveAuthenticodeSignature(executablePath));
    }

    private static void CreateSteamCmdArchive(string archivePath, string executableContent)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("steamcmd.exe");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(executableContent);
    }

    private static int CountOccurrences(string value, string pattern)
    {
        return (value.Length - value.Replace(pattern, string.Empty, StringComparison.Ordinal).Length) / pattern.Length;
    }

    private sealed class TestCredentialProvider(SteamCredentials credentials) : ISteamCredentialProvider
    {
        public SteamCredentials? Load() => credentials;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
