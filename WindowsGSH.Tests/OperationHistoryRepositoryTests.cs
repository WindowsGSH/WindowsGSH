using WindowsGSH.Core.Operations;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class OperationHistoryRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        "WindowsGSH.Tests",
        "OperationHistoryRepositoryTests-" + Guid.NewGuid().ToString("N") + ".db");

    public OperationHistoryRepositoryTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        AppDatabase.Initialize(_dbPath);
    }

    [Fact]
    public void GetRecentForServer_returns_only_that_servers_operations_most_recent_first()
    {
        var targetId = "server-" + Guid.NewGuid().ToString("N");
        var otherId = "server-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        OperationHistoryRepository.Add(Snapshot(targetId, ServerOperationKind.Start, now.AddMinutes(-10)), _dbPath);
        OperationHistoryRepository.Add(Snapshot(otherId, ServerOperationKind.Start, now.AddMinutes(-9)), _dbPath);
        OperationHistoryRepository.Add(Snapshot(targetId, ServerOperationKind.Stop, now.AddMinutes(-1)), _dbPath);

        var result = OperationHistoryRepository.GetRecentForServer(targetId, databasePath: _dbPath);

        Assert.Equal(2, result.Count);
        Assert.All(result, operation => Assert.Equal(targetId, operation.ServerId));
        Assert.Equal(ServerOperationKind.Stop, result[0].Kind);
        Assert.Equal(ServerOperationKind.Start, result[1].Kind);
    }

    [Fact]
    public void GetRecentForServer_respects_maxCount()
    {
        var serverId = "server-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            OperationHistoryRepository.Add(Snapshot(serverId, ServerOperationKind.Backup, now.AddMinutes(-i)), _dbPath);
        }

        var result = OperationHistoryRepository.GetRecentForServer(serverId, maxCount: 2, databasePath: _dbPath);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetRecentForServer_returns_empty_for_a_server_with_no_recorded_operations()
    {
        var result = OperationHistoryRepository.GetRecentForServer("server-" + Guid.NewGuid().ToString("N"), databasePath: _dbPath);

        Assert.Empty(result);
    }

    [Fact]
    public void GetRecentForServer_throws_instead_of_silently_swallowing_a_read_failure()
    {
        // Regression guard for a real finding: unlike GetRecent/Add/GetServerLifecycleTimes (which
        // must never block a real server action and so swallow every failure), this method backs a
        // diagnostic read for Server Doctor, which needs "no history recorded" to stay
        // distinguishable from "the database couldn't be read" - a corrupt/inaccessible database
        // must not silently produce an empty (and therefore misleadingly clean-looking) result.
        var corruptDbPath = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", "corrupt-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptDbPath)!);
        File.WriteAllText(corruptDbPath, "not a real sqlite database");

        try
        {
            Assert.ThrowsAny<Exception>(() => OperationHistoryRepository.GetRecentForServer("any-server", databasePath: corruptDbPath));
        }
        finally
        {
            File.Delete(corruptDbPath);
        }
    }

    private static ServerOperationSnapshot Snapshot(string serverId, ServerOperationKind kind, DateTimeOffset finishedAt) =>
        new(serverId, "Test Server", kind, "Completed", finishedAt.AddSeconds(-5), null, IsActive: false, finishedAt);

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
        }
    }
}
