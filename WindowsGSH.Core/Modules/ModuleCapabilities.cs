namespace WindowsGSH.Core.Modules;

public sealed record ModuleCapabilities(
    bool SupportsInstall,
    bool SupportsUpdate,
    bool SupportsQuery,
    bool SupportsRcon,
    bool SupportsConsoleCommands,
    bool SupportsApiActions,
    bool SupportsBackups,
    bool SupportsDirectConnection,
    bool RequiresJava = false,
    int? MinimumJavaMajor = null,
    bool SupportsVerify = false);
