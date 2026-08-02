namespace WindowsGSH.Core.Scheduling;

public sealed class AutoUpdatePlanner
{
    public IReadOnlyList<AutoUpdatePlanItem> Plan(AutoUpdatePlanRequest request)
    {
        if (request.Interval <= TimeSpan.Zero || request.IsClosing)
        {
            return [];
        }

        return request.Servers
            .Select(server => new AutoUpdatePlanItem(server, EvaluateServer(request, server)))
            .Where(item => item.Decision.ShouldRun)
            .ToArray();
    }

    public AutoUpdateDecision Evaluate(AutoUpdatePlanRequest request, AutoUpdateServerCandidate server)
    {
        if (request.Interval <= TimeSpan.Zero)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.IntervalDisabled);
        }

        if (request.IsClosing)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.AppClosing);
        }

        return EvaluateServer(request, server);
    }

    private static AutoUpdateDecision EvaluateServer(AutoUpdatePlanRequest request, AutoUpdateServerCandidate server)
    {
        if (!server.ConfigExists)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.MissingConfig);
        }

        if (!server.HasUsableModule)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.ModuleUnavailable);
        }

        if (server.CanStop)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.ServerRunning);
        }

        if (server.HasActiveOperation)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.ActiveOperation);
        }

        if (!server.AutoUpdateEnabled)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.AutoUpdateDisabled);
        }

        if (server.LastCheckUtc is { } lastCheck &&
            request.NowUtc - lastCheck.ToUniversalTime() < request.Interval)
        {
            return AutoUpdateDecision.Skip(AutoUpdateSkipReason.CheckedRecently);
        }

        return AutoUpdateDecision.Run();
    }
}

public sealed record AutoUpdatePlanRequest(
    DateTimeOffset NowUtc,
    TimeSpan Interval,
    bool IsClosing,
    IReadOnlyList<AutoUpdateServerCandidate> Servers);

public sealed record AutoUpdateServerCandidate(
    string ServerId,
    bool ConfigExists,
    bool HasUsableModule,
    bool CanStop,
    bool HasActiveOperation,
    bool AutoUpdateEnabled,
    DateTimeOffset? LastCheckUtc,
    string RemoteBuildId,
    string IgnoredBuildId);

public sealed record AutoUpdatePlanItem(
    AutoUpdateServerCandidate Server,
    AutoUpdateDecision Decision);

public sealed record AutoUpdateDecision(
    bool ShouldRun,
    AutoUpdateSkipReason? SkipReason)
{
    public static AutoUpdateDecision Run() => new(ShouldRun: true, SkipReason: null);

    public static AutoUpdateDecision Skip(AutoUpdateSkipReason reason) => new(ShouldRun: false, reason);
}

public enum AutoUpdateSkipReason
{
    IntervalDisabled,
    AppClosing,
    MissingConfig,
    ModuleUnavailable,
    ServerRunning,
    ActiveOperation,
    AutoUpdateDisabled,
    CheckedRecently
}
