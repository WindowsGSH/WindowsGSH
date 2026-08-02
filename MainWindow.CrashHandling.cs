using System.Windows;
using WindowsGSH.Core.Automation;
using WindowsGSH.Core.Events;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH;

public partial class MainWindow
{
    private readonly HashSet<string> _openCrashWindowServerIds = [];

    private void OnServerCrashDetected(ServerCrashDetectedEvent ev)
    {
        if (ev.Summary == null)
        {
            return;
        }

        var summary = ev.Summary;
        _ = ShowCrashSummaryAsync(ev.ServerId, summary);
    }

    private async Task ShowCrashSummaryAsync(string serverId, ServerCrashSummary summary)
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                // One crash window per server at a time — suppresses flood during crash loops.
                if (!_openCrashWindowServerIds.Add(serverId))
                {
                    return;
                }

                try
                {
                    // Ensure the main window is visible so the owned crash window is accessible.
                    // If minimised to tray, the app needs to be visible before the owned window shows.
                    if (!IsVisible)
                    {
                        Show();
                    }
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }

                    var window = new ServerCrashSummaryWindow(summary) { Owner = this };
                    window.Closed += (_, _) => _openCrashWindowServerIds.Remove(serverId);
                    window.Show();
                }
                catch
                {
                    _openCrashWindowServerIds.Remove(serverId);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            AppLogService.Add(
                $"Could not show the crash summary due to an internal error ({ex.GetType().Name}).",
                serverId);
        }
    }

    private async Task HandleUnexpectedServerExitAsync(InstalledServer server, ServerInstance instance)
    {
        var detectedAt = DateTimeOffset.UtcNow;
        try
        {
            await _automationService.RecordCrashDetectedAsync(server.ConfigPath, detectedAt);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Could not record crash time for {server.Name}: {ex.Message}", server.Id);
        }

        var decision = _automationService.PlanCrashRestart(new CrashRestartRequest(
            server.Id,
            server.Name,
            server.ConfigPath,
            instance.AppSettings.Automation,
            detectedAt,
            _manuallyStoppedServers.Contains(server.Id),
            _isClosing));
        if (!decision.ShouldRestart)
        {
            AppLogService.Add($"Crash restart skipped for {server.Name}: {decision.Message}", server.Id);
            if (decision.IsMaxAttemptsReached)
            {
                WindowsGshEventBus.Shared.Publish(new ServerLogEvent(
                    DateTimeOffset.UtcNow,
                    server.Id,
                    server.ModuleId,
                    WindowsGshEventSeverity.Error,
                    "CrashRecovery",
                    $"{server.Name} marked failed after repeated crashes: {decision.Message}"));
            }

            return;
        }

        AppLogService.Add($"{server.Name} {decision.Message}", server.Id);
        if (decision.Delay > TimeSpan.Zero)
        {
            await Task.Delay(decision.Delay);
        }

        if (_isClosing || _manuallyStoppedServers.Contains(server.Id))
        {
            return;
        }

        try
        {
            await _automationService.RecordAutoRestartAsync(server.ConfigPath, DateTimeOffset.UtcNow);
            await _serverOperations.StartAutomaticallyAsync(server);
        }
        catch (Exception ex)
        {
            AppLogService.Add($"Crash restart failed for {server.Name}: {ex.Message}", server.Id);
        }
    }

    private Task DispatchUnexpectedServerExitAsync(InstalledServer server, ServerInstance instance)
    {
        return Dispatcher.InvokeAsync(() => HandleUnexpectedServerExitAsync(server, instance)).Task.Unwrap();
    }
}
