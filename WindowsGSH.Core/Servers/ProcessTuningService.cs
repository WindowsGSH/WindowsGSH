using System.Diagnostics;
using System.Globalization;

namespace WindowsGSH.Core.Servers;

public sealed class ProcessTuningService : IProcessTuningService
{
    private readonly Action<Process, ProcessPriorityClass> _setPriority;
    private readonly Action<Process, IntPtr> _setAffinity;

    public ProcessTuningService(
        Action<Process, ProcessPriorityClass>? setPriority = null,
        Action<Process, IntPtr>? setAffinity = null)
    {
        _setPriority = setPriority ?? ((process, priority) => process.PriorityClass = priority);
        _setAffinity = setAffinity ?? ((process, affinity) => process.ProcessorAffinity = affinity);
    }

    public ProcessTuningResult Apply(Process process, ServerRuntimeSettings settings)
    {
        var steps = new List<ProcessTuningStepResult>();
        ProcessPriorityClass? appliedPriority = null;
        long? appliedAffinity = null;

        if (settings.ApplyPriorityOnStart)
        {
            if (!TryParsePriority(settings.ProcessPriorityClass, out var priority, out var error))
            {
                steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Priority, false, error));
            }
            else
            {
                try
                {
                    _setPriority(process, priority);
                    appliedPriority = priority;
                    steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Priority, true, $"Applied process priority {priority}."));
                }
                catch (Exception ex)
                {
                    steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Priority, false, $"Could not apply process priority {priority}.", ex.Message));
                }
            }
        }

        if (settings.ApplyAffinityOnStart)
        {
            if (!TryParseAffinityMask(settings.CpuAffinityMask, out var affinity, out var parseError))
            {
                steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Affinity, false, parseError));
            }
            else
            {
                try
                {
                    _setAffinity(process, new IntPtr(affinity));
                    appliedAffinity = affinity;
                    steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Affinity, true, $"Applied CPU affinity mask 0x{affinity:X}."));
                }
                catch (Exception ex)
                {
                    steps.Add(new ProcessTuningStepResult(ProcessTuningTarget.Affinity, false, $"Could not apply CPU affinity mask 0x{affinity:X}.", ex.Message));
                }
            }
        }

        return steps.Count == 0
            ? ProcessTuningResult.Empty
            : new ProcessTuningResult(steps.All(step => step.Success), appliedPriority, appliedAffinity, steps);
    }

    public static bool TryParsePriority(string value, out ProcessPriorityClass priority, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            priority = default;
            error = "Process priority is required when priority tuning is enabled.";
            return false;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out priority) &&
            Enum.IsDefined(priority))
        {
            error = string.Empty;
            return true;
        }

        error = "Process priority must be Idle, BelowNormal, Normal, AboveNormal, High, or RealTime.";
        return false;
    }

    public static bool TryParseAffinityMask(string value, out long affinity, out string error)
    {
        affinity = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "CPU affinity mask is required when affinity tuning is enabled.";
            return false;
        }

        var text = value.Trim();
        var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var numberText = isHex ? text[2..] : text;
        var styles = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!long.TryParse(numberText, styles, CultureInfo.InvariantCulture, out affinity))
        {
            error = "CPU affinity mask must be a positive decimal number or hex value such as 0x3.";
            return false;
        }

        if (affinity <= 0)
        {
            error = "CPU affinity mask must enable at least one CPU.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
