using System.Diagnostics;

namespace WindowsGSH.Core.Servers;

public interface IProcessTuningService
{
    ProcessTuningResult Apply(Process process, ServerRuntimeSettings settings);
}

public sealed record ProcessTuningResult(
    bool Success,
    ProcessPriorityClass? AppliedPriority,
    long? AppliedAffinityMask,
    IReadOnlyList<ProcessTuningStepResult> Steps)
{
    public static ProcessTuningResult Empty { get; } = new(true, null, null, []);

    public string Summary => string.Join("; ", Steps.Select(step => step.Message));
}

public sealed record ProcessTuningStepResult(
    ProcessTuningTarget Target,
    bool Success,
    string Message,
    string? Error = null);

public enum ProcessTuningTarget
{
    Priority,
    Affinity
}
