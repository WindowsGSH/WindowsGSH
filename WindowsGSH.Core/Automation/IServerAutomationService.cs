using WindowsGSH.Core.Servers;
using WindowsGSH.Core.Scheduling;

namespace WindowsGSH.Core.Automation;

public interface IServerAutomationService
{
    IReadOnlyList<AutomationStartupCandidate> PlanStartup(AutomationStartupPlanRequest request);

    IReadOnlyList<AutomationBeforeStartAction> PlanBeforeStart(ServerAutomationSettings settings, bool automatic);

    IReadOnlyList<string> PlanAutoUpdates(AutoUpdatePlanRequest request);

    CrashRestartDecision PlanCrashRestart(CrashRestartRequest request);

    Task RecordCrashDetectedAsync(string configPath, DateTimeOffset crashDetectedAt, CancellationToken cancellationToken = default);

    Task RecordAutoRestartAsync(string configPath, DateTimeOffset restartedAt, CancellationToken cancellationToken = default);
}

public sealed record AutomationStartupPlanRequest(
    DateTimeOffset Now,
    bool IsClosing,
    TimeSpan MinimumCheckInterval,
    IReadOnlyList<AutomationStartupCandidate> Candidates);

public sealed record AutomationStartupCandidate(
    string ServerId,
    string ServerName,
    bool ConfigExists,
    bool CanStart,
    bool IsManuallyStopped,
    bool IsConfigOpen,
    bool IsOperationRunning,
    DateTimeOffset? LastAutomationCheck,
    ServerAutomationSettings Settings);

public enum AutomationBeforeStartAction
{
    Backup,
    Update
}

public sealed record CrashRestartRequest(
    string ServerId,
    string ServerName,
    string ConfigPath,
    ServerAutomationSettings Settings,
    DateTimeOffset CrashDetectedAt,
    bool IsManuallyStopped,
    bool IsClosing);

public sealed record CrashRestartDecision(
    bool ShouldRestart,
    TimeSpan Delay,
    string Message,
    bool IsMaxAttemptsReached = false);
