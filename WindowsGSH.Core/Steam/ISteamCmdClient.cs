using WindowsGSH.Core.Modules;

namespace WindowsGSH.Core.Steam;

public interface ISteamCmdClient
{
    Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<int> InstallOrUpdateAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<int> VerifyAsync(
        string installPath,
        SteamInstallDefinition definition,
        string branch,
        string branchPassword,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string?> GetRemoteBuildIdAsync(
        SteamInstallDefinition definition,
        string branch,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

