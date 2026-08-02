using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Servers;

public static class ServerForceStopper
{
    public static async Task KillAsync(
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken,
        ServerForceStopOptions? options = null)
    {
        options ??= new ServerForceStopOptions();
        var processes = ServerProcessLocator.FindProcesses(
            module,
            instance.InstallPath,
            message => Log(message, instance, options));
        Log(
            $"Force stop scan for {GetServerName(instance, options)} " +
            $"(serverId={GetServerId(instance, options)}, moduleId={module.Id}, installPath={instance.InstallPath}, gracefulStopAttempted={options.GracefulStopAttempted}). " +
            $"MatchedProcesses={processes.Count}.",
            instance,
            options);

        if (processes.Count == 0)
        {
            return;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    Log($"Force stop skipped already-exited process: {ServerProcessLogFormatter.Describe(process)}; {ServerProcessLogFormatter.ExitResult(process)}.", instance, options);
                    continue;
                }

                try
                {
                    Log($"Force stopping process tree: {ServerProcessLogFormatter.Describe(process)}; killEntireProcessTree=True.", instance, options);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                    Log($"Force stop result: {ServerProcessLogFormatter.Describe(process)}; {ServerProcessLogFormatter.ExitResult(process)}.", instance, options);
                }
                catch (Exception ex)
                {
                    Log(
                        $"Force stop failed: {ServerProcessLogFormatter.Describe(process)}; " +
                        $"killEntireProcessTree=True; exception={ex}.",
                        instance,
                        options);
                    throw;
                }
            }
        }
    }

    private static void Log(string message, ServerInstance instance, ServerForceStopOptions options)
    {
        options.Log?.Invoke(message, GetServerId(instance, options));
    }

    private static string GetServerId(ServerInstance instance, ServerForceStopOptions options)
    {
        return string.IsNullOrWhiteSpace(options.ServerId) ? instance.Id : options.ServerId;
    }

    private static string GetServerName(ServerInstance instance, ServerForceStopOptions options)
    {
        return string.IsNullOrWhiteSpace(options.ServerName) ? instance.Name : options.ServerName;
    }
}

public sealed record ServerForceStopOptions(
    string? ServerId = null,
    string? ServerName = null,
    bool GracefulStopAttempted = false,
    Action<string, string?>? Log = null);
