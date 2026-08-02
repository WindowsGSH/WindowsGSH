using WindowsGSH.Core.Operations;
using Xunit;

namespace WindowsGSH.Tests;

/// <summary>
/// Unit tests for ServerOperationManager.TryCancelWithAudit.
/// These exercise the production code path directly — no WPF, no web host.
/// </summary>
public sealed class ServerOperationManagerAuditTests
{
    [Fact]
    public void TryCancelWithAudit_returns_false_when_no_operation_running()
    {
        var mgr = new ServerOperationManager();
        Assert.False(mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator"));
    }

    [Fact]
    public void TryCancelWithAudit_returns_true_and_sets_cancelling_status()
    {
        var mgr = new ServerOperationManager();
        mgr.TryBegin("srv-1", "Server", ServerOperationKind.Start, out var scope, out _, "Initiated via web by operator");

        var result = mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");

        Assert.True(result);
        var op = mgr.Get("srv-1");
        Assert.Equal("Cancelling", op!.Status);
        scope!.Dispose();
    }

    [Fact]
    public void TryCancelWithAudit_appends_audit_description_to_existing_description()
    {
        var mgr = new ServerOperationManager();
        mgr.TryBegin("srv-1", "Server", ServerOperationKind.Start, out var scope, out _, "Initiated via web by operator");

        mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");

        var op = mgr.Get("srv-1");
        Assert.NotNull(op!.Description);
        Assert.Contains("Initiated via web by operator", op.Description);
        Assert.Contains("Cancelled via web by operator", op.Description);
        scope!.Dispose();
    }

    [Fact]
    public void TryCancelWithAudit_uses_audit_as_description_when_no_prior_description()
    {
        var mgr = new ServerOperationManager();
        mgr.TryBegin("srv-1", "Server", ServerOperationKind.Start, out var scope, out _, description: null);

        mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");

        var op = mgr.Get("srv-1");
        Assert.Equal("Cancelled via web by operator", op!.Description);
        scope!.Dispose();
    }

    [Fact]
    public void TryCancelWithAudit_history_row_has_cancelled_status_and_combined_description()
    {
        var history = new List<ServerOperationSnapshot>();
        var mgr = new ServerOperationManager(history.Add);

        mgr.TryBegin("srv-1", "Server", ServerOperationKind.Start, out var scope, out _, "Initiated via web by operator");
        mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");
        scope!.Dispose(); // → status "Cancelling" → CompleteOperation writes "Cancelled"

        Assert.Single(history);
        Assert.Equal("Cancelled", history[0].Status);
        Assert.Contains("Initiated via web by operator", history[0].Description);
        Assert.Contains("Cancelled via web by operator", history[0].Description);
    }

    [Fact]
    public void TryCancelWithAudit_fires_cancellation_token()
    {
        var mgr = new ServerOperationManager();
        mgr.TryBegin("srv-1", "Server", ServerOperationKind.Start, out var scope, out _, null);

        mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");

        Assert.True(scope!.CancellationToken.IsCancellationRequested);
        scope.Dispose();
    }

    [Fact]
    public void TryCancelWithAudit_does_not_affect_different_server()
    {
        var mgr = new ServerOperationManager();
        mgr.TryBegin("srv-1", "Server 1", ServerOperationKind.Start, out var scope1, out _, null);
        mgr.TryBegin("srv-2", "Server 2", ServerOperationKind.Start, out var scope2, out _, null);

        mgr.TryCancelWithAudit("srv-1", "Cancelled via web by operator");

        Assert.True(scope1!.CancellationToken.IsCancellationRequested);
        Assert.False(scope2!.CancellationToken.IsCancellationRequested);

        scope1.Dispose();
        scope2.Dispose();
    }
}
