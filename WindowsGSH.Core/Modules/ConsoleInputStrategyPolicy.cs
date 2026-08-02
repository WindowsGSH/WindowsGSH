namespace WindowsGSH.Core.Modules;

public static class ConsoleInputStrategyPolicy
{
    public static bool UsesRedirectedStreams(ModuleRuntimeDefinition runtime)
    {
        return runtime.EffectiveConsoleStrategy == ConsoleInputStrategy.Redirected;
    }

    public static bool SupportsConsoleCommandInput(IGameServerModule? module)
    {
        if (module == null)
        {
            return false;
        }

        return module.Runtime.EffectiveConsoleStrategy switch
        {
            ConsoleInputStrategy.Redirected => true,
            ConsoleInputStrategy.WindowMessage =>
                module.Capabilities.SupportsConsoleCommands &&
                module is IModuleConsoleCommandCapability,
            _ => false
        };
    }

    public static bool PrefersRcon(IGameServerModule? module)
    {
        return module?.Runtime.EffectiveConsoleStrategy == ConsoleInputStrategy.RconPreferred;
    }

    public static string GetCommandUnavailableMessage(IGameServerModule module)
    {
        return module.Runtime.EffectiveConsoleStrategy switch
        {
            ConsoleInputStrategy.RconPreferred => $"{module.Name} uses RCON for commands.",
            ConsoleInputStrategy.LogTailOnly => $"{module.Name} exposes log output only; console input is unavailable.",
            ConsoleInputStrategy.None => $"{module.Name} does not expose console input.",
            ConsoleInputStrategy.WindowMessage => $"{module.Name} requires a module-provided window-message console implementation.",
            _ => $"{module.Name} does not support server console commands."
        };
    }
}
