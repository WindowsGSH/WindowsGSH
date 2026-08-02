namespace WindowsGSH.Core.Modules;

/// <param name="CustomArguments">
/// Privileged module-authored options. WindowsGSH parses this value with Windows command-line
/// quoting rules and passes each option separately. SteamCMD <c>+commands</c> are rejected; use
/// the dedicated install-definition fields for commands managed by WindowsGSH.
/// </param>
public sealed record SteamInstallDefinition(
    string AppId,
    bool LoginAnonymous = true,
    bool ValidateByDefault = true,
    string? ModName = null,
    string? CustomArguments = null);
