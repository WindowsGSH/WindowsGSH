using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public interface IServerLifecycleService
{
    Task<ServerOperationResult> StartAsync(ServerInstance instance, CancellationToken token);

    Task<ServerOperationResult> StartAsync(ServerInstance instance, ServerLifecycleStartOptions options, CancellationToken token);

    Task<ServerOperationResult> StopAsync(ServerInstance instance, CancellationToken token);

    Task<ServerOperationResult> StopAsync(ServerInstance instance, ServerLifecycleStopOptions options, CancellationToken token);

    Task<ServerOperationResult> RestartAsync(ServerInstance instance, CancellationToken token);

    Task<ServerOperationResult> RestartAsync(ServerInstance instance, ServerLifecycleStartOptions startOptions, ServerLifecycleStopOptions stopOptions, CancellationToken token);

    Task<ServerOperationResult> ForceStopAsync(ServerInstance instance, CancellationToken token);

    Task<ServerOperationResult> ForceStopAsync(ServerInstance instance, ServerLifecycleStopOptions options, CancellationToken token);

    Task<bool> IsRunningAsync(ServerInstance instance, CancellationToken token);
}
