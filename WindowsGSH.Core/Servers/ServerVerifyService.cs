using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Steam;

namespace WindowsGSH.Core.Servers;

public sealed class ServerVerifyService
{
    private readonly ISteamCmdClient _steamCmd;
    private readonly IServerOperationCoordinator _operations;

    public ServerVerifyService(
        ISteamCmdClient steamCmd,
        IServerOperationCoordinator? operations = null)
    {
        _steamCmd = steamCmd;
        _operations = operations ?? ServerOperationManager.Shared;
    }

    public async Task<ServerVerifyResult> VerifyAsync(
        ServerVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var allowsOnline = request.Module is IModuleVerifyCapability v && v.AllowsOnlineVerification;

        if (request.ServerIsRunning && !allowsOnline)
        {
            return new ServerVerifyResult(
                ModuleVerifyOutcome.Failed,
                request.Instance.Id,
                request.Instance.Name,
                $"{request.Instance.Name} must be stopped before file verification.");
        }

        if (request.Module is not IModuleVerifyCapability && request.Module.GetSteamInstall() == null)
        {
            var unsupported = $"{request.Module.Name} does not support file verification.";
            request.Progress?.Report(unsupported);
            return new ServerVerifyResult(ModuleVerifyOutcome.Unsupported, request.Instance.Id, request.Instance.Name, unsupported);
        }

        ServerOperationScope? operation = null;
        if (request.UseOperationCoordinator &&
            !_operations.TryBeginServerOperation(
                request.Instance.Id,
                request.Instance.Name,
                ServerOperationKind.Verify,
                out operation,
                out var existing,
                "Verifying server files."))
        {
            var busy = existing == null
                ? $"{request.Instance.Name} already has a conflicting operation."
                : $"{request.Instance.Name} is busy: {existing.Status}.";
            return new ServerVerifyResult(ModuleVerifyOutcome.Failed, request.Instance.Id, request.Instance.Name, busy);
        }

        try
        {
            var token = request.UseOperationCoordinator && operation != null
                ? operation.CancellationToken
                : cancellationToken;

            if (request.Module is IModuleVerifyCapability verifyCapability)
            {
                var moduleResult = await verifyCapability.VerifyAsync(request.Instance, token).ConfigureAwait(false);
                request.Progress?.Report(moduleResult.Message);
                if (moduleResult.Outcome == ModuleVerifyOutcome.Failed && operation != null)
                {
                    _operations.FailOperation(request.Instance.Id, new InvalidOperationException(moduleResult.Message));
                    operation = null;
                }

                return new ServerVerifyResult(
                    moduleResult.Outcome,
                    request.Instance.Id,
                    request.Instance.Name,
                    moduleResult.Message);
            }

            var steamInstall = request.Module.GetSteamInstall()!;
            var branch = NormalizeSteamBranch(request.SteamBranch);
            request.Progress?.Report($"Starting file verification for {request.Instance.Name} on branch {(string.IsNullOrWhiteSpace(branch) ? "public" : branch)}.");
            var exitCode = await _steamCmd.VerifyAsync(
                request.Instance.InstallPath,
                steamInstall,
                branch,
                request.SteamBranchPassword,
                request.Progress,
                token).ConfigureAwait(false);

            var message = exitCode == 0
                ? $"{request.Instance.Name} file verification completed."
                : $"{request.Instance.Name} file verification failed (Steam installer exit code {exitCode}).";

            request.Progress?.Report(message);

            if (exitCode != 0 && operation != null)
            {
                _operations.FailOperation(request.Instance.Id, new InvalidOperationException(message));
                operation = null;
            }

            return new ServerVerifyResult(
                exitCode == 0 ? ModuleVerifyOutcome.Verified : ModuleVerifyOutcome.Failed,
                request.Instance.Id,
                request.Instance.Name,
                message,
                SteamExitCode: exitCode);
        }
        catch (OperationCanceledException)
        {
            if (request.UseOperationCoordinator)
            {
                _operations.CancelOperation(request.Instance.Id);
            }

            return new ServerVerifyResult(
                ModuleVerifyOutcome.Cancelled,
                request.Instance.Id,
                request.Instance.Name,
                $"{request.Instance.Name} file verification was cancelled.");
        }
        catch (Exception ex)
        {
            if (request.UseOperationCoordinator)
            {
                _operations.FailOperation(request.Instance.Id, ex);
                operation = null;
            }

            return new ServerVerifyResult(
                ModuleVerifyOutcome.Failed,
                request.Instance.Id,
                request.Instance.Name,
                ex.Message);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private static string NormalizeSteamBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            string.Equals(branch.Trim(), "Loading...", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return branch.Trim();
    }
}

public sealed record ServerVerifyRequest(
    IGameServerModule Module,
    ServerInstance Instance,
    bool ServerIsRunning,
    string SteamBranch = "",
    string SteamBranchPassword = "",
    IProgress<string>? Progress = null,
    bool UseOperationCoordinator = true);

public sealed record ServerVerifyResult(
    ModuleVerifyOutcome Outcome,
    string ServerId,
    string ServerName,
    string Message,
    int? SteamExitCode = null)
{
    public bool Success => Outcome is ModuleVerifyOutcome.Verified or ModuleVerifyOutcome.Repaired;

    public static ServerVerifyResult Failed(string serverId, string serverName, string message) =>
        new(ModuleVerifyOutcome.Failed, serverId, serverName, message);
}
