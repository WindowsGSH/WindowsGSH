using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

[Collection(LocalStateRecoveryTestCollection.Name)]
public sealed class AppLogServiceTests
{
    [Fact]
    public async Task Add_eventually_persists_to_the_database_via_the_background_writer()
    {
        // Regression guard for a real bug: AppLogService.Add previously called
        // AppLogRepository.Add (a real SQLite write) synchronously, inline, on whatever thread
        // called Add - including the UI thread whenever Add is called from code already running
        // on it, which is the common case given how many call sites throughout this app log
        // directly from event handlers. A slow disk, or SQLite's own busy-timeout under concurrent
        // writers, could have frozen the UI mid-log-call. Add now only enqueues to an in-memory
        // channel; a single dedicated background task drains it to the real database. This proves
        // the pipeline actually works end to end - the entry still reaches the database - not just
        // that Add itself doesn't throw. Deliberately does not call AppLogService.FlushAndStopAsync
        // to force this along: that permanently shuts down the one process-wide background writer
        // every other test (and the app itself) shares, so this waits for it to catch up
        // naturally instead, the same way every other real-process-timing test in this suite does
        // (see WaitUntilAsync usages in ServerConsoleServiceTests).
        AppDatabase.Initialize();
        var marker = "app-log-db-marker-" + Guid.NewGuid().ToString("N");

        AppLogService.Add(marker, "test-source");

        var found = await WaitUntilTrueAsync(
            () =>
            {
                using var connection = AppDatabase.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM app_logs WHERE message = $message;";
                command.Parameters.AddWithValue("$message", marker);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            },
            TimeSpan.FromSeconds(10));

        Assert.True(found, "Expected the log entry to reach the database via the background writer within 10 seconds.");
    }

    private static async Task<bool> WaitUntilTrueAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public void Add_trims_oldest_messages_once_the_bounded_maximum_is_exceeded()
    {
        // Regression guard for a real bug: AppLogService.Messages and Buffer previously grew
        // forever - no caller ever trimmed either. A long-running session, especially one hitting a
        // persistent repeating background error, would accumulate unbounded memory and an
        // ever-growing ObservableCollection the UI keeps re-rendering. AppLogService is a static,
        // app-wide singleton with no reset hook, so this only asserts the bound holds and old
        // entries are actually gone after writing well past it - not an exact starting count, since
        // other tests/app activity in the same process may have already logged unrelated lines.
        // Calls AddLine directly rather than the public Add(...) entry point specifically to avoid
        // paying AppLogRepository.Add's real SQLite write cost several thousand times over - the
        // bug and the fix are both entirely within AddLine's own Buffer/Messages bookkeeping, which
        // Add just delegates to (after its own DB write, timestamp formatting, and dispatcher
        // marshaling, none of which this test is about).
        var marker = Guid.NewGuid().ToString("N");
        const int overflow = 25;
        var totalWritten = AppLogService.MaxMessages + overflow;
        for (var i = 0; i < totalWritten; i++)
        {
            AppLogService.AddLine($"trim-test-{marker}-{i}");
        }

        Assert.True(
            AppLogService.Messages.Count <= AppLogService.MaxMessages,
            $"Expected at most {AppLogService.MaxMessages} messages, found {AppLogService.Messages.Count}.");
        Assert.DoesNotContain(AppLogService.Messages, message => message.Contains($"trim-test-{marker}-0", StringComparison.Ordinal));
        Assert.Contains(AppLogService.Messages, message => message.Contains($"trim-test-{marker}-{totalWritten - 1}", StringComparison.Ordinal));

        // Buffer (Text) is trimmed in lockstep with Messages - the earliest lines from this test
        // must not still be sitting in Text even though Messages no longer holds them. Matching on
        // "-0" exactly (not "-0" as a prefix of "-01"/"-02"/etc.) is safe here: every other index is
        // formatted without leading zeros, so no other message's suffix begins with "0".
        Assert.DoesNotContain($"trim-test-{marker}-0", AppLogService.Text);
        Assert.Contains($"trim-test-{marker}-{totalWritten - 1}", AppLogService.Text);
    }
}
