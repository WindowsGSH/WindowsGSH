namespace WindowsGSH.Core.Modules;

public sealed record ModuleRuntimeDefinition(
    string StartPath,
    IReadOnlyList<string> ProcessNames,
    bool AllowsEmbeddedConsole = false,
    int PortIncrements = 1,
    string? QueryProtocol = null,
    ConsoleInputStrategy? ConsoleStrategy = null)
{
    public ConsoleInputStrategy EffectiveConsoleStrategy =>
        ConsoleStrategy ?? (AllowsEmbeddedConsole ? ConsoleInputStrategy.Redirected : ConsoleInputStrategy.None);
}

public enum ConsoleInputStrategy
{
    Redirected,
    WindowMessage,
    RconPreferred,
    LogTailOnly,
    None
}
