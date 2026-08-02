using WindowsGSH.Core.Diagnostics;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class AppCrashDiagnosticsTests
{
    [Fact]
    public void CrashLog_includes_environment_exception_and_recent_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var service = new AppCrashLogService(root, () => new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));

        var result = service.Write(new AppCrashLogRequest(
            "unit test",
            IsFatal: true,
            Exception: new InvalidOperationException("boom"),
            RecentAppLog: "recent line"));

        var text = File.ReadAllText(result.Path);
        Assert.Contains("WindowsGSH App Crash Report", text);
        Assert.Contains("Context: unit test", text);
        Assert.Contains("Fatal: True", text);
        Assert.Contains("OS:", text);
        Assert.Contains("Framework:", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("boom", text);
        Assert.Contains("recent line", text);
    }

    // ── P2-05: RecentAppLog redaction ─────────────────────────────────────────

    [Fact]
    public void CrashLog_redacts_secrets_in_recent_app_log()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var service = new AppCrashLogService(root, () => new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero));

        var result = service.Write(new AppCrashLogRequest(
            "unit test",
            IsFatal: false,
            RecentAppLog: "Web: api_key=supersecret123 connected"));

        var text = File.ReadAllText(result.Path);
        Assert.DoesNotContain("supersecret123", text);
        Assert.Contains("Recent App Log", text);
    }

    // ── P2-07: Sanitize regex covers --arg value style ────────────────────────

    [Theory]
    [InlineData("app.exe --token abc123", "abc123")]
    [InlineData("app.exe --password hunter2", "hunter2")]
    [InlineData("app.exe --secret xyz789 --port 27015", "xyz789")]
    [InlineData("app.exe /token abc123", "abc123")]
    [InlineData("app.exe token=abc123", "abc123")]
    [InlineData("app.exe password:hunter2", "hunter2")]
    [InlineData("app.exe --gslt MyGSLTToken99", "MyGSLTToken99")]
    // Quoted multi-word values must be redacted as a single unit (P2 fix).
    [InlineData("app.exe --password \"alpha beta\"", "alpha")]
    [InlineData("app.exe --password \"alpha beta\"", "beta")]
    [InlineData("app.exe --token \"multi word token here\"", "multi")]
    public void Sanitize_redacts_secret_argument(string input, string expectedSecret)
    {
        var result = AppCrashLogService.Sanitize(input);
        Assert.DoesNotContain(expectedSecret, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrashRecovery_suppresses_when_crash_count_reaches_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "recovery.json");
        var now = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var service = new AppCrashRecoveryService(statePath, () => now);
        var options = new AppCrashRecoveryOptions(
            RestartAppOnCrash: true,
            MaxRestartAttempts: 2,
            RestartWindow: TimeSpan.FromMinutes(10));

        var first = service.RecordCrashAndPlan(options);
        now = now.AddMinutes(1);
        var second = service.RecordCrashAndPlan(options);

        Assert.True(first.ShouldRestart);
        Assert.False(second.ShouldRestart);
        Assert.Equal(2, second.RecentCrashCount);
        Assert.Contains("suppressed", second.Message);
    }

    [Fact]
    public void CrashRecovery_resets_after_window()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "recovery.json");
        var now = new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);
        var service = new AppCrashRecoveryService(statePath, () => now);
        var options = new AppCrashRecoveryOptions(
            RestartAppOnCrash: true,
            MaxRestartAttempts: 2,
            RestartWindow: TimeSpan.FromMinutes(10));

        Assert.True(service.RecordCrashAndPlan(options).ShouldRestart);
        now = now.AddMinutes(11);
        var afterWindow = service.RecordCrashAndPlan(options);

        Assert.True(afterWindow.ShouldRestart);
        Assert.Equal(1, afterWindow.RecentCrashCount);
    }

    [Fact]
    public void CrashRecovery_state_path_may_be_filename_only()
    {
        var statePath = $"recovery-{Guid.NewGuid():N}.json";
        try
        {
            var service = new AppCrashRecoveryService(statePath);

            var decision = service.RecordCrashAndPlan(new AppCrashRecoveryOptions(
                RestartAppOnCrash: false,
                MaxRestartAttempts: 2,
                RestartWindow: TimeSpan.FromMinutes(10)));

            Assert.False(decision.ShouldRestart);
            Assert.True(File.Exists(statePath));
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Fact]
    public void CrashRecovery_respects_disabled_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var service = new AppCrashRecoveryService(Path.Combine(root, "recovery.json"));

        var decision = service.RecordCrashAndPlan(AppCrashRecoveryOptions.Default);

        Assert.False(decision.ShouldRestart);
        Assert.Contains("disabled", decision.Message);
    }
}
