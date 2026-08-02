namespace WindowsGSH.Core.Servers;

public sealed record InstalledServer(
    string Id,
    string Name,
    string ModuleId,
    string Runtime,
    string ServerFolder,
    string InstallPath,
    string ConfigPath,
    string IpAddress,
    string Port,
    string SteamAppId,
    string SteamBranch,
    string MaxPlayers,
    string ProcessId,
    string CpuUsage,
    string MemoryUsage,
    string PlayerCount,
    string CurrentStatusText,
    string Uptime,
    bool IsOperationRunning,
    string OperationText,
    string? LastOperationError,
    bool IsInstalled,
    ServerRuntimeStatus Status,
    string StatusText,
    string StatusBrushKey,
    bool HasUpdateAvailable,
    string LocalBuildId,
    string RemoteBuildId,
    string IgnoredBuildId,
    bool CanShowInfo,
    bool CanEditConfig,
    bool CanStart,
    bool CanStop,
    string? QueryMap = null,
    string? QueryGame = null,
    string? QueryVersion = null,
    string? QueryProtocol = null,
    double? QueryDurationMilliseconds = null,
    IReadOnlyList<Modules.QueryPlayer>? QueryPlayers = null,
    string? QueryMessage = null,
    string? QueryDetailMessage = null,
    bool SupportsVerify = false,
    bool AllowsOnlineVerification = false,
    string CpuTrend = "",
    string MemoryTrend = "")
{
    public IReadOnlyList<Modules.QueryPlayer> QueryPlayerRows => QueryPlayers ?? [];
}

public enum ServerRuntimeStatus
{
    Offline,
    Warning,
    Running
}
