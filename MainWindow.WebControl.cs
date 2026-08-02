using System.Windows;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Api;

namespace WindowsGSH;

public partial class MainWindow
{
    internal void InitializeWebServerControl()
    {
        WebServerControl.SetDispatch((serverId, kind, webUsername) =>
        {
            var server = WebServerState.GetServers().FirstOrDefault(s => s.Id == serverId);
            if (server == null)
                return WebControlOutcome.NotFound();

            // Cancel uses TryCancelWithAudit to stamp the web username into the operation
            // description before the CTS fires, so the history row records who cancelled.
            if (kind == WebControlKind.Cancel)
            {
                var cancelDescription = $"Cancelled via web by {webUsername}";
                var cancelled = _operationManager.TryCancelWithAudit(serverId, cancelDescription);
                if (cancelled)
                    WebLog.Add($"Web user '{webUsername}' cancelled operation for '{server.Name}'.");
                return cancelled ? WebControlOutcome.Accepted() : WebControlOutcome.NothingToCancel();
            }

            // Eligibility: mirror the desktop bulk action rules (MainWindow.xaml.cs ~line 1103).
            if (kind == WebControlKind.Start && !server.CanStart)
                return WebControlOutcome.Conflict("ServerAlreadyRunning");
            if (kind == WebControlKind.Stop && !server.CanStop)
                return WebControlOutcome.Conflict("ServerAlreadyOffline");
            if (kind == WebControlKind.Restart && !server.CanStop && !server.CanStart)
                return WebControlOutcome.Conflict("ServerNotRestartable");
            if (kind == WebControlKind.Update)
            {
                var caps = ModuleDescriptor.GetEffectiveCapabilities(GetModule(server));
                if (!caps.SupportsUpdate)
                    return WebControlOutcome.Conflict("ModuleDoesNotSupportUpdates");
                if (server.CanStop)
                    return WebControlOutcome.Conflict("ServerMustBeOfflineToUpdate");
                if (!server.CanStart)
                    return WebControlOutcome.Conflict("ServerNotUpdateable");
            }

            // Atomically acquire the operation lock before returning HTTP 202.
            // This eliminates the race window where two simultaneous requests both pass
            // an IsOperationRunning check and both receive 202.
            var webDescription = $"Initiated via web by {webUsername}";
            var opKind = ToServerOperationKind(kind);
            if (!_serverOperations.TryBegin(server, opKind, out var scope, description: webDescription))
            {
                var existing = _operationManager.Get(serverId);
                return WebControlOutcome.Conflict(existing?.Kind.ToString());
            }

            WebLog.Add($"Web user '{webUsername}' requested {kind} for '{server.Name}'.");
            var localServer = server;
            var localScope = scope!;

            System.Windows.Threading.DispatcherOperation<Task> dispatchOp;
            try
            {
                dispatchOp = Dispatcher.InvokeAsync(
                    () => ExecuteWebOperationAsync(kind, localServer, localScope));
            }
            catch (Exception ex)
            {
                // InvokeAsync threw — dispatcher is shutting down. Fail the scope so the
                // server is not left permanently locked as "busy" until restart.
                _operationManager.Fail(serverId, ex);
                return WebControlOutcome.Unavailable();
            }

            // Guard against the dispatcher aborting a queued-but-not-yet-started operation
            // (e.g. fast shutdown between InvokeAsync and first execution). Fail the scope so
            // it does not remain active indefinitely.
            dispatchOp.Aborted += (_, _) =>
                _operationManager.Fail(serverId,
                    new InvalidOperationException("Operation aborted: dispatcher shut down before work could run."));
            _ = ObserveWebOperationAsync(dispatchOp.Task.Unwrap(), serverId);

            return WebControlOutcome.Accepted();
        });
    }

    private async Task ExecuteWebOperationAsync(
        WebControlKind kind,
        WindowsGSH.Core.Servers.InstalledServer server,
        ServerOperationScope scope)
    {
        switch (kind)
        {
            case WebControlKind.Start:     await _serverOperations.StartWithScopeAsync(server, scope);     break;
            case WebControlKind.Stop:      await _serverOperations.StopWithScopeAsync(server, scope);      break;
            case WebControlKind.Restart:   await _serverOperations.RestartWithScopeAsync(server, scope);   break;
            case WebControlKind.ForceStop: await _serverOperations.ForceStopWithScopeAsync(server, scope); break;
            case WebControlKind.Update:    await _serverOperations.UpdateWithScopeAsync(server, scope);    break;
        }
    }

    private async Task ObserveWebOperationAsync(Task operation, string serverId)
    {
        try
        {
            await operation;
        }
        catch (Exception ex)
        {
            _operationManager.Fail(serverId, ex);
            AppLogService.Add(
                $"Web server operation failed due to an internal error ({ex.GetType().Name}).",
                serverId);
        }
    }

    private static ServerOperationKind ToServerOperationKind(WebControlKind kind) => kind switch
    {
        WebControlKind.Start     => ServerOperationKind.Start,
        WebControlKind.Stop      => ServerOperationKind.Stop,
        WebControlKind.Restart   => ServerOperationKind.Restart,
        WebControlKind.ForceStop => ServerOperationKind.ForceStop,
        WebControlKind.Update    => ServerOperationKind.Update,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
