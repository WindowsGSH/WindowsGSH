using System.Diagnostics;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ProcessTuningServiceTests
{
    [Theory]
    [InlineData("AboveNormal", ProcessPriorityClass.AboveNormal)]
    [InlineData("high", ProcessPriorityClass.High)]
    public void TryParsePriority_accepts_named_values(string value, ProcessPriorityClass expected)
    {
        var parsed = ProcessTuningService.TryParsePriority(value, out var priority, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expected, priority);
    }

    [Fact]
    public void TryParsePriority_rejects_invalid_values()
    {
        var parsed = ProcessTuningService.TryParsePriority("fast", out _, out var error);

        Assert.False(parsed);
        Assert.Contains("priority", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("0x3", 3)]
    [InlineData("0X10", 16)]
    public void TryParseAffinityMask_accepts_decimal_and_hex(string value, long expected)
    {
        var parsed = ProcessTuningService.TryParseAffinityMask(value, out var affinity, out var error);

        Assert.True(parsed, error);
        Assert.Equal(expected, affinity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nope")]
    public void TryParseAffinityMask_rejects_invalid_masks(string value)
    {
        var parsed = ProcessTuningService.TryParseAffinityMask(value, out _, out var error);

        Assert.False(parsed);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Apply_reports_denied_priority_without_throwing()
    {
        using var process = Process.GetCurrentProcess();
        var service = new ProcessTuningService(
            setPriority: (_, _) => throw new UnauthorizedAccessException("denied"),
            setAffinity: (_, _) => { });

        var result = service.Apply(process, ServerRuntimeSettings.Default with
        {
            ApplyPriorityOnStart = true,
            ProcessPriorityClass = "AboveNormal"
        });

        Assert.False(result.Success);
        var step = Assert.Single(result.Steps);
        Assert.Equal(ProcessTuningTarget.Priority, step.Target);
        Assert.False(step.Success);
        Assert.Equal("denied", step.Error);
    }

    [Fact]
    public void Apply_returns_empty_when_tuning_is_disabled()
    {
        using var process = Process.GetCurrentProcess();
        var service = new ProcessTuningService();

        var result = service.Apply(process, ServerRuntimeSettings.Default);

        Assert.True(result.Success);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void Apply_sets_enabled_priority_and_affinity()
    {
        using var process = Process.GetCurrentProcess();
        ProcessPriorityClass? prioritySet = null;
        long? affinitySet = null;
        var service = new ProcessTuningService(
            setPriority: (_, priority) => prioritySet = priority,
            setAffinity: (_, affinity) => affinitySet = affinity.ToInt64());

        var result = service.Apply(process, ServerRuntimeSettings.Default with
        {
            ApplyPriorityOnStart = true,
            ProcessPriorityClass = "BelowNormal",
            ApplyAffinityOnStart = true,
            CpuAffinityMask = "0x3"
        });

        Assert.True(result.Success);
        Assert.Equal(ProcessPriorityClass.BelowNormal, prioritySet);
        Assert.Equal(3, affinitySet);
        Assert.Equal(2, result.Steps.Count);
    }
}
