using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;
using WindowsGSH.Core.Steam;

namespace WindowsGSH.Core.Servers;

public sealed class ServerInstallService : IServerInstallService
{
    private readonly ISteamCmdClient _steamCmd;
    private readonly IServerOperationCoordinator _operations;
    private readonly Func<DateTimeOffset> _utcNow;

    public ServerInstallService(
        ISteamCmdClient steamCmd,
        IServerOperationCoordinator? operations = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _steamCmd = steamCmd;
        _operations = operations ?? ServerOperationManager.Shared;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<ServerInstallValidationResult> ValidateInstallAsync(
        IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (module.GetSteamInstall() == null && module is not IModuleUpdateCapability)
        {
            return Task.FromResult(ServerInstallValidationResult.Failed($"{module.Name} does not define an install or update path."));
        }

        if (string.IsNullOrWhiteSpace(instance.Name))
        {
            return Task.FromResult(ServerInstallValidationResult.Failed("Server Name is required."));
        }

        if (string.IsNullOrWhiteSpace(instance.InstallPath))
        {
            return Task.FromResult(ServerInstallValidationResult.Failed("Install path is required."));
        }

        return Task.FromResult(ServerInstallValidationResult.Passed);
    }

    public async Task<ServerInstallResult> InstallAsync(
        ServerInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateInstallAsync(request.Module, request.Instance, cancellationToken).ConfigureAwait(false);
        if (!validation.Success)
        {
            return ServerInstallResult.Failed(ServerOperationKind.Install, request.Instance, validation.Message ?? "Install validation failed.");
        }

        return await RunWithOptionalOperationAsync(
            request.Instance,
            ServerOperationKind.Install,
            "Installing server.",
            request.UseOperationCoordinator,
            cancellationToken,
            async token =>
            {
                Directory.CreateDirectory(request.Instance.InstallPath);
                var steamInstall = request.Module.GetSteamInstall();
                if (steamInstall != null)
                {
                    var branch = NormalizeSteamBranch(request.SteamBranch);
                    var installPlan = await request.Module.CreateInstallPlanAsync(request.Instance, token).ConfigureAwait(false);
                    request.Progress?.Report($"Install plan: {installPlan.Tool} in {installPlan.WorkingDirectory}.");
                    var exitCode = await _steamCmd.InstallOrUpdateAsync(
                        request.Instance.InstallPath,
                        steamInstall,
                        branch,
                        request.SteamBranchPassword,
                        request.Progress,
                        token).ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        return new ServerInstallResult(
                            false,
                            ServerOperationKind.Install,
                            request.Instance.Id,
                            request.Instance.Name,
                            $"Steam installer exited with code {exitCode}.",
                            SteamExitCode: exitCode,
                            Status: ServerOperationStatus.Failed,
                            LastError: $"Steam installer exited with code {exitCode}.");
                    }

                    if (request.WriteServerConfig)
                    {
                        WriteServerConfig(request.Module, request.Instance, branch, request.SteamBranchPassword, request.UpnpMappingPolicy);
                        request.Progress?.Report("Wrote " + request.Instance.ConfigPath);
                    }

                    return new ServerInstallResult(
                        true,
                        ServerOperationKind.Install,
                        request.Instance.Id,
                        request.Instance.Name,
                        "Install complete.",
                        SteamExitCode: exitCode,
                        InstallPlan: installPlan);
                }

                if (request.Module is not IModuleUpdateCapability moduleUpdater)
                {
                    return ServerInstallResult.Failed(
                        ServerOperationKind.Install,
                        request.Instance,
                        $"{request.Module.Name} does not define an install or update path.");
                }

                if (request.WriteServerConfig)
                {
                    WriteServerConfig(request.Module, request.Instance, string.Empty, string.Empty, request.UpnpMappingPolicy);
                    request.Progress?.Report("Wrote " + request.Instance.ConfigPath);
                    request.Progress?.Report("Wrote initial server config before module update.");
                }

                if (request.WriteModuleConfig)
                {
                    await request.Module.WriteConfigFileSettingsAsync(request.Instance, token).ConfigureAwait(false);
                }

                request.Progress?.Report($"Preparing {request.Module.Name} module update...");
                request.Progress?.Report("Resolving build and downloading server files through the module updater...");
                var result = await moduleUpdater.UpdateAsync(request.Instance, request.Progress, token).ConfigureAwait(false);
                ModuleUpdateStateWriter.Write(request.Instance.ConfigPath, result, _utcNow());
                ReportModuleUpdate(result, request.Progress);

                return new ServerInstallResult(
                    true,
                    ServerOperationKind.Install,
                    request.Instance.Id,
                    request.Instance.Name,
                    result.Message,
                    ModuleUpdate: result);
            }).ConfigureAwait(false);
    }

    public async Task<ServerInstallResult> UpdateAsync(
        ServerUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return await RunWithOptionalOperationAsync(
            request.Instance,
            ServerOperationKind.Update,
            $"Updating server {request.Reason}.",
            request.UseOperationCoordinator,
            cancellationToken,
            async token =>
            {
                request.Progress?.Report($"Updating {request.Instance.Name} {request.Reason}.");
                if (request.Module is IModuleUpdateCapability moduleUpdater)
                {
                    var result = await moduleUpdater.UpdateAsync(request.Instance, request.Progress, token).ConfigureAwait(false);
                    request.Progress?.Report(result.Message);
                    ModuleUpdateStateWriter.Write(request.Instance.ConfigPath, result, _utcNow());
                    return new ServerInstallResult(
                        true,
                        ServerOperationKind.Update,
                        request.Instance.Id,
                        request.Instance.Name,
                        result.Message,
                        ModuleUpdate: result);
                }

                var steamInstall = request.Module.GetSteamInstall();
                if (!request.Module.Capabilities.SupportsUpdate || steamInstall == null)
                {
                    var unsupportedMessage = $"{request.Instance.Name} does not expose a Steam update path.";
                    request.Progress?.Report(unsupportedMessage);
                    return new ServerInstallResult(true, ServerOperationKind.Update, request.Instance.Id, request.Instance.Name, unsupportedMessage);
                }

                var branch = NormalizeSteamBranch(request.SteamBranch, allowEmptyPublic: true);
                string? remoteBuildId = null;
                try
                {
                    remoteBuildId = await _steamCmd.GetRemoteBuildIdAsync(
                        steamInstall,
                        branch,
                        request.Progress,
                        token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(remoteBuildId))
                    {
                        request.Progress?.Report($"{request.Instance.Name} remote Steam build for {FormatBranch(branch)}: {remoteBuildId}.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    request.Progress?.Report($"{request.Instance.Name} remote Steam build check failed: {ex.Message}");
                }

                if (request.SkipIgnoredBuild && IsIgnoredSteamBuild(request.IgnoredBuildId, remoteBuildId))
                {
                    var skipped = $"{request.Instance.Name} scheduled update skipped: Steam build {request.IgnoredBuildId} is ignored.";
                    request.Progress?.Report(skipped);
                    return new ServerInstallResult(true, ServerOperationKind.Update, request.Instance.Id, request.Instance.Name, skipped, RemoteBuildId: remoteBuildId);
                }

                var exitCode = await _steamCmd.InstallOrUpdateAsync(
                    request.Instance.InstallPath,
                    steamInstall,
                    branch,
                    request.SteamBranchPassword,
                    request.Progress,
                    token).ConfigureAwait(false);
                WriteSteamUpdateState(request.Instance.ConfigPath, branch, exitCode, remoteBuildId, _utcNow());
                var message = $"{request.Instance.Name} update finished with exit code {exitCode}.";
                request.Progress?.Report(message);
                return new ServerInstallResult(
                    exitCode == 0,
                    ServerOperationKind.Update,
                    request.Instance.Id,
                    request.Instance.Name,
                    message,
                    SteamExitCode: exitCode,
                    RemoteBuildId: remoteBuildId,
                    Status: exitCode == 0 ? ServerOperationStatus.Completed : ServerOperationStatus.Failed,
                    LastError: exitCode == 0 ? null : $"Steam installer exited with code {exitCode}.");
            }).ConfigureAwait(false);
    }

    public static void WriteServerConfig(IGameServerModule module, ServerInstance instance, string branch, string branchPassword, UpnpMappingPolicy upnpMappingPolicy = UpnpMappingPolicy.Manual)
    {
        var steamInstall = module.GetSteamInstall();
        var branchPasswordReference = string.Empty;
        if (steamInstall != null && !string.IsNullOrWhiteSpace(branchPassword))
        {
            new SteamBranchPasswordStore().Save(instance.Id, branchPassword);
            branchPasswordReference = SteamBranchPasswordStore.CreateReference(instance.Id);
        }

        var config = JsonSerializer.SerializeToNode(new
        {
            id = instance.Id,
            name = instance.Name,
            moduleId = module.Id,
            runtime = "native",
            installPath = "files",
            steam = steamInstall == null
                ? null
                : new
                {
                    appId = steamInstall.AppId,
                    branch = string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase) ? string.Empty : branch,
                    branchPasswordRef = branchPasswordReference
                },
            network = new
            {
                upnpMappingPolicy = upnpMappingPolicy.ToString()
            },
            settings = instance.Settings
        })?.AsObject() ?? [];

        Directory.CreateDirectory(instance.ServerFolder);
        new ServerSecretStore().ProtectConfigSecrets(module, instance.ServerFolder, config);
        AtomicFile.WriteAllText(instance.ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void WriteSteamUpdateState(
        string configPath,
        string branch,
        int exitCode,
        string? remoteBuildId,
        DateTimeOffset updatedUtc)
    {
        var root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject()
            ?? throw new InvalidOperationException("Server config is not a JSON object.");

        var steam = root["steam"] as JsonObject;
        if (steam == null)
        {
            steam = [];
            root["steam"] = steam;
        }

        steam["lastInstalledBranch"] = string.IsNullOrWhiteSpace(branch) ? "public" : branch;
        steam["lastUpdateExitCode"] = exitCode;
        steam["lastUpdateUtc"] = updatedUtc.ToString("O");
        if (!string.IsNullOrWhiteSpace(remoteBuildId))
        {
            steam["lastRemoteBuildId"] = remoteBuildId;
        }

        AtomicFile.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<ServerInstallResult> RunWithOptionalOperationAsync(
        ServerInstance instance,
        ServerOperationKind kind,
        string description,
        bool useOperationCoordinator,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<ServerInstallResult>> action)
    {
        ServerOperationScope? operation = null;
        if (useOperationCoordinator &&
            !_operations.TryBeginServerOperation(instance.Id, instance.Name, kind, out operation, out var existing, description))
        {
            var message = existing == null
                ? $"{instance.Name} already has a conflicting operation."
                : $"{instance.Name} is busy: {existing.Status}.";
            return ServerInstallResult.Failed(kind, instance, message, ServerOperationStatus.Running);
        }

        try
        {
            var token = useOperationCoordinator && operation != null
                ? operation.CancellationToken
                : cancellationToken;
            var result = await action(token).ConfigureAwait(false);
            if (!result.Success && useOperationCoordinator)
            {
                _operations.FailOperation(instance.Id, new InvalidOperationException(result.LastError ?? result.Message));
                operation = null;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (useOperationCoordinator)
            {
                _operations.CancelOperation(instance.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (useOperationCoordinator)
            {
                _operations.FailOperation(instance.Id, ex);
                operation = null;
            }

            return ServerInstallResult.Failed(kind, instance, ex.Message);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private static void ReportModuleUpdate(ModuleUpdateResult result, IProgress<string>? progress)
    {
        progress?.Report(result.Message);
        if (!string.IsNullOrWhiteSpace(result.InstalledBuildId))
        {
            progress?.Report($"Installed build: {result.InstalledBuildId}");
        }

        if (!string.IsNullOrWhiteSpace(result.DownloadedFileName))
        {
            progress?.Report($"Downloaded file: {result.DownloadedFileName}");
        }

        if (!result.Updated)
        {
            progress?.Report("Module update completed without downloading a new build.");
        }
    }

    private static bool IsIgnoredSteamBuild(string? ignoredBuildId, string? remoteBuildId)
    {
        return !string.IsNullOrWhiteSpace(ignoredBuildId) &&
            !string.IsNullOrWhiteSpace(remoteBuildId) &&
            string.Equals(remoteBuildId, ignoredBuildId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSteamBranch(string? branch, bool allowEmptyPublic = false)
    {
        if (string.IsNullOrWhiteSpace(branch) ||
            string.Equals(branch.Trim(), "Loading...", StringComparison.OrdinalIgnoreCase))
        {
            return allowEmptyPublic ? string.Empty : "public";
        }

        return branch.Trim();
    }

    private static string FormatBranch(string branch)
    {
        return string.IsNullOrWhiteSpace(branch) ? "public" : branch;
    }
}
