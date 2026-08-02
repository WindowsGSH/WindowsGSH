namespace WindowsGSH.Core.Operations;

public enum BulkServerAction
{
    StartSelected,
    StartAutoStart,
    StopSelected,
    StopAllRunning,
    RestartSelected,
    UpdateSelectedOffline,
    BackupSelected
}

public sealed record BulkServerActionCandidate(
    string ServerId,
    string ServerName,
    bool IsSelected,
    bool ConfigExists,
    bool CanStart,
    bool CanStop,
    bool IsBusy,
    bool AutoStartEnabled,
    bool SupportsUpdate,
    bool SupportsBackups);

public sealed record BulkServerActionPlanItem(
    string ServerId,
    string ServerName,
    bool ShouldExecute,
    string Reason);

public sealed record BulkServerActionPlan(
    BulkServerAction Action,
    IReadOnlyList<BulkServerActionPlanItem> Items)
{
    public IReadOnlyList<BulkServerActionPlanItem> Executable =>
        Items.Where(item => item.ShouldExecute).ToArray();

    public IReadOnlyList<BulkServerActionPlanItem> Skipped =>
        Items.Where(item => !item.ShouldExecute).ToArray();

    public bool RequiresConfirmation => Action is
        BulkServerAction.StopSelected or
        BulkServerAction.StopAllRunning or
        BulkServerAction.RestartSelected or
        BulkServerAction.UpdateSelectedOffline;
}

public sealed record BulkServerActionResult(
    string ServerId,
    string ServerName,
    BulkServerActionResultStatus Status,
    string Message);

public enum BulkServerActionResultStatus
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

public sealed record BulkServerActionSummary(
    IReadOnlyList<BulkServerActionResult> Results)
{
    public int Succeeded => Results.Count(result => result.Status == BulkServerActionResultStatus.Succeeded);
    public int Failed => Results.Count(result => result.Status == BulkServerActionResultStatus.Failed);
    public int Cancelled => Results.Count(result => result.Status == BulkServerActionResultStatus.Cancelled);
    public int Skipped => Results.Count(result => result.Status == BulkServerActionResultStatus.Skipped);
}

public sealed class BulkServerActionPlanner
{
    public BulkServerActionPlan Plan(
        BulkServerAction action,
        IReadOnlyList<BulkServerActionCandidate> candidates)
    {
        return new BulkServerActionPlan(
            action,
            candidates.Select(candidate => Evaluate(action, candidate)).ToArray());
    }

    private static BulkServerActionPlanItem Evaluate(
        BulkServerAction action,
        BulkServerActionCandidate candidate)
    {
        var included = action switch
        {
            BulkServerAction.StartAutoStart => candidate.AutoStartEnabled,
            BulkServerAction.StopAllRunning => candidate.CanStop,
            _ => candidate.IsSelected
        };
        if (!included)
        {
            return Skip(candidate, action is BulkServerAction.StartAutoStart
                ? "Auto Start is not enabled."
                : action is BulkServerAction.StopAllRunning
                    ? "Server is not running."
                    : "Not selected.");
        }

        if (!candidate.ConfigExists)
        {
            return Skip(candidate, "Server configuration is missing or unreadable.");
        }

        if (candidate.IsBusy)
        {
            return Skip(candidate, "Another operation is already running.");
        }

        return action switch
        {
            BulkServerAction.StartSelected or BulkServerAction.StartAutoStart =>
                candidate.CanStart
                    ? Execute(candidate)
                    : Skip(candidate, candidate.CanStop ? "Server is already running." : "Server cannot currently be started."),
            BulkServerAction.StopSelected or BulkServerAction.StopAllRunning =>
                candidate.CanStop
                    ? Execute(candidate)
                    : Skip(candidate, "Server is not running."),
            BulkServerAction.RestartSelected =>
                candidate.CanStop || candidate.CanStart
                    ? Execute(candidate)
                    : Skip(candidate, "Server cannot currently be restarted."),
            BulkServerAction.UpdateSelectedOffline =>
                !candidate.SupportsUpdate
                    ? Skip(candidate, "The module does not support updates.")
                    : candidate.CanStop
                    ? Skip(candidate, "Stop the server before updating.")
                    : candidate.CanStart
                        ? Execute(candidate)
                        : Skip(candidate, "Server cannot currently be updated."),
            BulkServerAction.BackupSelected =>
                !candidate.SupportsBackups
                    ? Skip(candidate, "The module does not support backups.")
                    : candidate.CanStop
                        ? Skip(candidate, "Stop the server before running a bulk backup.")
                        : Execute(candidate),
            _ => Skip(candidate, "Unsupported bulk action.")
        };
    }

    private static BulkServerActionPlanItem Execute(BulkServerActionCandidate candidate) =>
        new(candidate.ServerId, candidate.ServerName, true, "Ready.");

    private static BulkServerActionPlanItem Skip(BulkServerActionCandidate candidate, string reason) =>
        new(candidate.ServerId, candidate.ServerName, false, reason);
}

public sealed class BulkServerActionExecutor
{
    public async Task<BulkServerActionSummary> ExecuteAsync(
        BulkServerActionPlan plan,
        int maximumConcurrency,
        Func<BulkServerActionPlanItem, CancellationToken, Task<BulkServerActionResult>> executeAsync,
        IProgress<BulkServerActionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(executeAsync);
        maximumConcurrency = Math.Clamp(maximumConcurrency, 1, 8);

        var results = plan.Skipped
            .Select(item => new BulkServerActionResult(
                item.ServerId,
                item.ServerName,
                BulkServerActionResultStatus.Skipped,
                item.Reason))
            .ToList();
        var executable = plan.Executable;
        var completed = 0;
        using var gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var tasks = executable.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new BulkServerActionResult(
                    item.ServerId,
                    item.ServerName,
                    BulkServerActionResultStatus.Cancelled,
                    "Cancelled before the action started.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await executeAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new BulkServerActionResult(
                    item.ServerId,
                    item.ServerName,
                    BulkServerActionResultStatus.Cancelled,
                    "Action cancelled.");
            }
            catch (Exception ex)
            {
                return new BulkServerActionResult(
                    item.ServerId,
                    item.ServerName,
                    BulkServerActionResultStatus.Failed,
                    ex.Message);
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new BulkServerActionProgress(current, executable.Count, item.ServerName));
            }
        }).ToArray();

        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        return new BulkServerActionSummary(results);
    }
}

public sealed record BulkServerActionProgress(int Completed, int Total, string ServerName);

public static class BulkServerActionTargetLoader
{
    public static async Task<T?> LoadAsync<T>(
        string targetId,
        Func<CancellationToken, Task<IReadOnlyList<T>>> loadAsync,
        Func<T, string> getId,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = await loadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return targets.FirstOrDefault(target =>
            string.Equals(getId(target), targetId, StringComparison.Ordinal));
    }
}
