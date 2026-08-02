using WindowsGSH.Core.Scheduling;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ExternalCommandRunnerTests
{
    [Fact]
    public void App_setting_disables_external_commands_by_default()
    {
        var settings = new AppSettings();

        Assert.False(settings.ExternalScheduledCommandsEnabled);
        Assert.Empty(settings.ExternalCommandAllowedPaths);
    }

    [Fact]
    public async Task Allowed_executable_runs_without_shell_and_captures_stdout()
    {
        var executable = SystemExecutable("where.exe");
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            new ExternalCommandRunRequest(
                executable,
                "where.exe",
                "",
                10,
                [executable]),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(result.StandardOutput, line => line.Contains("where.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Disallowed_executable_is_rejected()
    {
        var runner = new ExternalCommandRunner();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(
                new ExternalCommandRunRequest(
                    SystemExecutable("where.exe"),
                    "where.exe",
                    "",
                    10,
                    [Path.GetTempPath()]),
                CancellationToken.None));

        Assert.Contains("not allowlisted", exception.Message);
    }

    [Fact]
    public async Task Shell_hosts_are_rejected_even_when_allowlisted()
    {
        var executable = SystemExecutable("cmd.exe");
        var runner = new ExternalCommandRunner();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(
                new ExternalCommandRunRequest(executable, "/c echo unsafe", "", 10, [executable]),
                CancellationToken.None));

        Assert.Contains("Shell hosts", exception.Message);
    }

    [Fact]
    public async Task Metacharacters_are_passed_to_executable_without_shell_interpretation()
    {
        var executable = SystemExecutable("where.exe");
        var marker = Path.Combine(Path.GetTempPath(), $"WindowsGSH-injection-{Guid.NewGuid():N}.txt");
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            new ExternalCommandRunRequest(
                executable,
                $"definitely-missing & echo injected > \"{marker}\"",
                "",
                10,
                [executable]),
            CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(marker));
        Assert.NotEmpty(result.StandardError);
    }

    [Fact]
    public async Task Timeout_kills_long_running_process()
    {
        var executable = SystemExecutable("ping.exe");
        var runner = new ExternalCommandRunner();

        var result = await runner.RunAsync(
            new ExternalCommandRunRequest(
                executable,
                "127.0.0.1 -n 6",
                "",
                1,
                [executable]),
            CancellationToken.None);

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task Cancellation_kills_long_running_process()
    {
        var executable = SystemExecutable("ping.exe");
        var runner = new ExternalCommandRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                new ExternalCommandRunRequest(
                    executable,
                    "127.0.0.1 -n 10",
                    "",
                    30,
                    [executable]),
                cancellation.Token));
    }

    [Fact]
    public void Secret_like_config_values_are_redacted()
    {
        const string secret = "very-secret-token";
        var values = ExternalCommandRunner.ExtractSecretLikeValues(
            """{"settings":{"password":"hunter2","apiToken":"very-secret-token","name":"visible"}}""");

        var redacted = ExternalCommandRunner.Redact(
            $"password=hunter2 token={secret} name=visible",
            values);

        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("name=visible", redacted);
    }

    [Fact]
    public async Task Noisy_output_is_streamed_but_only_a_bounded_tail_is_retained()
    {
        var executable = SystemExecutable("findstr.exe");
        var input = Path.Combine(Path.GetTempPath(), $"WindowsGSH-output-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(input, Enumerable.Range(1, 650).Select(index => $"line-{index:D4}"));
        var streamed = new List<string>();
        try
        {
            var result = await new ExternalCommandRunner().RunAsync(
                new ExternalCommandRunRequest(
                    executable,
                    $"/R \".*\" \"{input}\"",
                    "",
                    10,
                    [executable],
                    StandardOutputSink: line => streamed.Add(line)),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(650, streamed.Count);
            Assert.Equal(ExternalCommandRunner.MaxBufferedLinesPerStream + 1, result.StandardOutput.Count);
            Assert.Contains("omitted", result.StandardOutput[0]);
            Assert.Contains("line-0650", result.StandardOutput[^1]);
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static string SystemExecutable(string fileName)
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), fileName);
    }
}
