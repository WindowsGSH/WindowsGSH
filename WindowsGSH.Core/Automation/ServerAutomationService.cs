using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Scheduling;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Automation;

public sealed class ServerAutomationService : IServerAutomationService
{
    private static readonly TimeSpan RestartAttemptWindow = TimeSpan.FromHours(1);
    private readonly object _sync = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _restartAttempts = [];
    private readonly AutoUpdatePlanner _autoUpdatePlanner = new();

    public IReadOnlyList<AutomationStartupCandidate> PlanStartup(AutomationStartupPlanRequest request)
    {
        if (request.IsClosing)
        {
            return [];
        }

        return request.Candidates
            .Where(candidate => candidate.ConfigExists)
            .Where(candidate => candidate.CanStart)
            .Where(candidate => !candidate.IsManuallyStopped)
            .Where(candidate => !candidate.IsConfigOpen)
            .Where(candidate => !candidate.IsOperationRunning)
            .Where(candidate => candidate.LastAutomationCheck == null ||
                request.Now - candidate.LastAutomationCheck.Value >= request.MinimumCheckInterval)
            .Where(candidate => candidate.Settings.AutoStart || candidate.Settings.AutoRestart)
            .ToArray();
    }

    public IReadOnlyList<AutomationBeforeStartAction> PlanBeforeStart(ServerAutomationSettings settings, bool automatic)
    {
        var actions = new List<AutomationBeforeStartAction>();
        if (settings.BackupOnStart)
        {
            actions.Add(AutomationBeforeStartAction.Backup);
        }

        if (settings.UpdateOnStart || (automatic && settings.AutoUpdate))
        {
            actions.Add(AutomationBeforeStartAction.Update);
        }

        return actions;
    }

    public CrashRestartDecision PlanCrashRestart(CrashRestartRequest request)
    {
        if (request.IsClosing)
        {
            return new CrashRestartDecision(false, TimeSpan.Zero, "App is closing.");
        }

        if (request.IsManuallyStopped)
        {
            return new CrashRestartDecision(false, TimeSpan.Zero, "Server was manually stopped.");
        }

        if (!request.Settings.RestartOnCrash && !request.Settings.AutoRestart)
        {
            return new CrashRestartDecision(false, TimeSpan.Zero, "Crash restart is disabled.");
        }

        var maxAttempts = Math.Max(0, request.Settings.MaxRestartAttempts);
        if (maxAttempts == 0)
        {
            return new CrashRestartDecision(false, TimeSpan.Zero, "Crash restart max attempts is zero.");
        }

        var attempts = GetRecentAttempts(request.ServerId, request.CrashDetectedAt);
        if (attempts.Count >= maxAttempts)
        {
            return new CrashRestartDecision(
                false,
                TimeSpan.Zero,
                $"Crash restart suppressed after {attempts.Count} attempt(s).",
                IsMaxAttemptsReached: true);
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, request.Settings.RestartDelaySeconds));
        return new CrashRestartDecision(true, delay, $"Crash restart queued in {delay.TotalSeconds:0} second(s).");
    }

    public IReadOnlyList<string> PlanAutoUpdates(AutoUpdatePlanRequest request)
    {
        return _autoUpdatePlanner
            .Plan(request)
            .Select(update => update.Server.ServerId)
            .ToArray();
    }

    public Task RecordCrashDetectedAsync(string configPath, DateTimeOffset crashDetectedAt, CancellationToken cancellationToken = default)
    {
        return UpdateAutomationTimestampAsync(configPath, "lastCrashDetectedAt", crashDetectedAt, cancellationToken);
    }

    public async Task RecordAutoRestartAsync(string configPath, DateTimeOffset restartedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordRestartAttempt(GetServerId(configPath), restartedAt);
        await UpdateAutomationTimestampAsync(configPath, "lastAutoRestartAt", restartedAt, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<DateTimeOffset> GetRecentAttempts(string serverId, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_restartAttempts.TryGetValue(serverId, out var attempts))
            {
                return [];
            }

            attempts.RemoveAll(attempt => now - attempt > RestartAttemptWindow);
            return attempts.ToArray();
        }
    }

    private void RecordRestartAttempt(string serverId, DateTimeOffset restartedAt)
    {
        lock (_sync)
        {
            if (!_restartAttempts.TryGetValue(serverId, out var attempts))
            {
                attempts = [];
                _restartAttempts[serverId] = attempts;
            }

            attempts.RemoveAll(attempt => restartedAt - attempt > RestartAttemptWindow);
            attempts.Add(restartedAt);
        }
    }

    private static async Task UpdateAutomationTimestampAsync(
        string configPath,
        string propertyName,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidOperationException("Server config is not a JSON object.");
        var automation = root["automation"] as JsonObject;
        if (automation == null)
        {
            automation = [];
            root["automation"] = automation;
        }

        automation[propertyName] = timestamp.ToUniversalTime().ToString("O");
        AtomicFile.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetServerId(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? configPath
                : configPath;
        }
        catch
        {
            return configPath;
        }
    }
}
