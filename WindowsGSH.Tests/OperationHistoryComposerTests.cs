using WindowsGSH.Core.Operations;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class OperationHistoryComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 16, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compose_lists_active_operations_before_persisted_history()
    {
        var active = Operation("active", ServerOperationKind.Update, Now.AddMinutes(-2), isActive: true);
        var persisted = Operation("old", ServerOperationKind.Start, Now.AddMinutes(-10), finishedAt: Now.AddMinutes(-9));

        var result = new OperationHistoryComposer().Compose([active], [persisted]);

        Assert.Equal(["active", "old"], result.Select(operation => operation.ServerId));
    }

    [Fact]
    public void Compose_orders_active_oldest_first()
    {
        var newer = Operation("newer", ServerOperationKind.Backup, Now.AddMinutes(-1), isActive: true);
        var older = Operation("older", ServerOperationKind.Update, Now.AddMinutes(-5), isActive: true);

        var result = new OperationHistoryComposer().Compose([newer, older], []);

        Assert.Equal(["older", "newer"], result.Select(operation => operation.ServerId));
    }

    [Fact]
    public void Compose_orders_persisted_newest_first()
    {
        var older = Operation("older", ServerOperationKind.Start, Now.AddMinutes(-30), finishedAt: Now.AddMinutes(-20));
        var newer = Operation("newer", ServerOperationKind.Stop, Now.AddMinutes(-10), finishedAt: Now.AddMinutes(-5));

        var result = new OperationHistoryComposer().Compose([], [older, newer]);

        Assert.Equal(["newer", "older"], result.Select(operation => operation.ServerId));
    }

    [Fact]
    public void Compose_drops_persisted_duplicate_of_active_operation()
    {
        var active = Operation("server-1", ServerOperationKind.Update, Now.AddMinutes(-2), isActive: true);
        var duplicatePersisted = active with { IsActive = false, Status = "Completed", FinishedAt = Now };

        var result = new OperationHistoryComposer().Compose([active], [duplicatePersisted]);

        var operation = Assert.Single(result);
        Assert.True(operation.IsActive);
    }

    [Fact]
    public void Compose_caps_persisted_rows_without_capping_active_operations()
    {
        var active = Operation("active", ServerOperationKind.Update, Now, isActive: true);
        var persisted = Enumerable.Range(0, 3)
            .Select(index => Operation($"old-{index}", ServerOperationKind.Start, Now.AddMinutes(-index - 10), finishedAt: Now.AddMinutes(-index)))
            .ToArray();

        var result = new OperationHistoryComposer().Compose([active], persisted, maxPersistedRows: 2);

        Assert.Equal(3, result.Count);
        Assert.Equal("active", result[0].ServerId);
        Assert.Equal(["old-0", "old-1"], result.Skip(1).Select(operation => operation.ServerId));
    }

    private static ServerOperationSnapshot Operation(
        string serverId,
        ServerOperationKind kind,
        DateTimeOffset startedAt,
        bool isActive = false,
        DateTimeOffset? finishedAt = null)
    {
        return new ServerOperationSnapshot(
            serverId,
            ServerName: serverId,
            kind,
            isActive ? "Running" : "Completed",
            startedAt,
            LastError: null,
            isActive,
            finishedAt);
    }
}
