using System.IO;
using System.Text.Json;
using WindowsGSH.Core.IO;

namespace WindowsGSH.Core.Diagnostics;

public sealed class AppCrashRecoveryService
{
    public static readonly TimeSpan DefaultRestartWindow = TimeSpan.FromMinutes(10);
    public const int DefaultMaxRestartAttempts = 2;

    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _utcNow;

    public AppCrashRecoveryService(string? statePath = null, Func<DateTimeOffset>? utcNow = null)
    {
        _statePath = statePath ?? AppPaths.GetPath("logs", "crashes", "app-crash-recovery.json");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public AppCrashRecoveryDecision RecordCrashAndPlan(AppCrashRecoveryOptions options)
    {
        var now = _utcNow();
        var state = LoadState();
        state.Crashes = state.Crashes
            .Where(crash => now - crash <= options.RestartWindow)
            .Append(now)
            .OrderBy(crash => crash)
            .ToList();
        SaveState(state);

        if (!options.RestartAppOnCrash)
        {
            return new AppCrashRecoveryDecision(false, "App restart on crash is disabled.", state.Crashes.Count);
        }

        if (state.Crashes.Count >= options.MaxRestartAttempts)
        {
            return new AppCrashRecoveryDecision(
                false,
                $"App restart suppressed after {state.Crashes.Count} crash(es) in {options.RestartWindow.TotalMinutes:0} minute(s).",
                state.Crashes.Count);
        }

        return new AppCrashRecoveryDecision(true, "App restart allowed.", state.Crashes.Count);
    }

    public void ClearOldCrashes(TimeSpan window)
    {
        var now = _utcNow();
        var state = LoadState();
        state.Crashes = state.Crashes
            .Where(crash => now - crash <= window)
            .OrderBy(crash => crash)
            .ToList();
        SaveState(state);
    }

    private AppCrashRecoveryState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return new AppCrashRecoveryState();
            }

            return JsonSerializer.Deserialize<AppCrashRecoveryState>(File.ReadAllText(_statePath)) ?? new AppCrashRecoveryState();
        }
        catch
        {
            return new AppCrashRecoveryState();
        }
    }

    private void SaveState(AppCrashRecoveryState state)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record AppCrashRecoveryOptions(
    bool RestartAppOnCrash,
    int MaxRestartAttempts,
    TimeSpan RestartWindow)
{
    public static AppCrashRecoveryOptions Default { get; } = new(
        RestartAppOnCrash: false,
        MaxRestartAttempts: AppCrashRecoveryService.DefaultMaxRestartAttempts,
        RestartWindow: AppCrashRecoveryService.DefaultRestartWindow);
}

public sealed record AppCrashRecoveryDecision(
    bool ShouldRestart,
    string Message,
    int RecentCrashCount);

public sealed class AppCrashRecoveryState
{
    public List<DateTimeOffset> Crashes { get; set; } = [];
}
