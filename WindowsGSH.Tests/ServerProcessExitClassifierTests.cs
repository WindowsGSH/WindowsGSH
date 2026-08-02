using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerProcessExitClassifierTests
{
    [Fact]
    public void Classify_ignores_expected_exit()
    {
        var decision = ServerProcessExitClassifier.Classify(
            isExpectedExit: true,
            isAppClosing: false,
            exitCode: 1,
            isWithinBootGracePeriod: true);

        Assert.Equal(ServerProcessExitDecision.Ignore, decision);
    }

    [Fact]
    public void Classify_ignores_app_closing_exit()
    {
        var decision = ServerProcessExitClassifier.Classify(
            isExpectedExit: false,
            isAppClosing: true,
            exitCode: 1,
            isWithinBootGracePeriod: true);

        Assert.Equal(ServerProcessExitDecision.Ignore, decision);
    }

    [Fact]
    public void Classify_treats_clean_exit_after_boot_as_clean()
    {
        var decision = ServerProcessExitClassifier.Classify(
            isExpectedExit: false,
            isAppClosing: false,
            exitCode: 0,
            isWithinBootGracePeriod: false);

        Assert.Equal(ServerProcessExitDecision.CleanExit, decision);
    }

    [Fact]
    public void Classify_treats_clean_exit_during_boot_as_unexpected()
    {
        var decision = ServerProcessExitClassifier.Classify(
            isExpectedExit: false,
            isAppClosing: false,
            exitCode: 0,
            isWithinBootGracePeriod: true);

        Assert.Equal(ServerProcessExitDecision.UnexpectedExit, decision);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public void Classify_treats_nonzero_or_unknown_exit_as_unexpected(int? exitCode)
    {
        var decision = ServerProcessExitClassifier.Classify(
            isExpectedExit: false,
            isAppClosing: false,
            exitCode,
            isWithinBootGracePeriod: false);

        Assert.Equal(ServerProcessExitDecision.UnexpectedExit, decision);
    }
}
