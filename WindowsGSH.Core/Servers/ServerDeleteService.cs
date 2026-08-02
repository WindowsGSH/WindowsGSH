using System.IO;
using WindowsGSH.Core;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public sealed class ServerDeleteService : IServerDeleteService
{
    private readonly IServerOperationCoordinator _operations;
    private readonly string _serversRoot;

    public ServerDeleteService(IServerOperationCoordinator? operations = null, string? serversRoot = null)
    {
        _operations = operations ?? ServerOperationManager.Shared;
        _serversRoot = Path.GetFullPath(serversRoot ?? AppPaths.GetPath("servers"));
    }

    public async Task<ServerDeleteResult> DeleteAsync(ServerDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Options.RequireStopped &&
            (request.Server.CanStop || request.IsRunning?.Invoke(request.Server) == true))
        {
            return ServerDeleteResult.Failed("Stop the server before deleting it.");
        }

        ServerOperationScope? operation = null;
        if (request.UseOperationCoordinator &&
            !_operations.TryBeginServerOperation(
                request.Server.Id,
                request.Server.Name,
                ServerOperationKind.Delete,
                out operation,
                out var existing,
                "Delete server"))
        {
            var message = existing == null
                ? $"{request.Server.Name} already has a conflicting operation."
                : $"{request.Server.Name} is busy: {existing.Status}.";
            return ServerDeleteResult.Failed(message, ServerOperationStatus.Running);
        }

        try
        {
            var token = operation?.CancellationToken ?? cancellationToken;
            var steps = await Task.Run(() => DeleteCore(request, token), token).ConfigureAwait(false);
            var success = steps.All(step => step.Success || step.Skipped);
            if (!success && request.UseOperationCoordinator)
            {
                _operations.FailOperation(request.Server.Id, new InvalidOperationException("One or more delete steps failed."));
            }
            else
            {
                operation?.Dispose();
            }

            return new ServerDeleteResult(
                success,
                success ? ServerOperationStatus.Completed : ServerOperationStatus.Failed,
                success ? $"{request.Server.Name} deleted." : $"{request.Server.Name} delete completed with errors.",
                steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request.UseOperationCoordinator)
            {
                _operations.FailOperation(request.Server.Id, ex);
            }

            return ServerDeleteResult.Failed(ex.Message);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private IReadOnlyList<ServerDeleteStepResult> DeleteCore(ServerDeleteRequest request, CancellationToken cancellationToken)
    {
        var steps = new List<ServerDeleteStepResult>();
        var serverFolder = Path.GetFullPath(request.Server.ServerFolder);
        var expectedFolder = Path.GetFullPath(Path.Combine(_serversRoot, request.Server.Id));
        if (!string.Equals(serverFolder, expectedFolder, StringComparison.OrdinalIgnoreCase) ||
            !IsInside(_serversRoot, serverFolder))
        {
            return [Failed("Safety", serverFolder, "Refusing to delete a folder outside the WindowsGSH servers directory.")];
        }

        if (request.Options.RemoveFirewallRules)
        {
            steps.Add(RunStep("Firewall rules", null, () =>
            {
                if (request.RemoveFirewallRules == null)
                {
                    return "No firewall removal callback was supplied.";
                }

                var removed = request.RemoveFirewallRules(request.Server);
                return removed == 0
                    ? "No managed firewall rules found."
                    : $"Removed {removed} managed firewall rule(s).";
            }));
        }

        var installPath = Path.GetFullPath(request.Server.InstallPath);
        var fullServerFolderDelete = request.Options.DeleteServerRecord &&
            request.Options.DeleteInstalledFiles &&
            request.Options.DeleteBackups &&
            request.Options.DeleteLogs &&
            IsInside(serverFolder, installPath);

        if (fullServerFolderDelete)
        {
            steps.Add(RunStep("Server folder", serverFolder, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(serverFolder))
                {
                    Directory.Delete(serverFolder, recursive: true);
                }

                return "Deleted server folder.";
            }));
            return steps;
        }

        if (request.Options.DeleteInstalledFiles)
        {
            steps.Add(RunStep("Installed files", installPath, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsInside(serverFolder, installPath))
                {
                    throw new InvalidOperationException("Refusing to delete install files outside the server folder.");
                }

                if (Directory.Exists(installPath))
                {
                    Directory.Delete(installPath, recursive: true);
                }

                return "Deleted installed files.";
            }));
        }

        if (request.Options.DeleteBackups)
        {
            steps.Add(DeleteChildDirectory("Backups", serverFolder, "backups", cancellationToken));
        }

        if (request.Options.DeleteLogs)
        {
            steps.Add(DeleteChildDirectory("Logs", serverFolder, "logs", cancellationToken));
        }

        if (request.Options.DeleteServerRecord)
        {
            steps.Add(RunStep("Server record", request.Server.ConfigPath, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configPath = Path.GetFullPath(request.Server.ConfigPath);
                if (!IsInside(serverFolder, configPath))
                {
                    throw new InvalidOperationException("Refusing to delete a server record outside the server folder.");
                }

                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }

                var importMetadataPath = Path.Combine(serverFolder, "import.json");
                if (File.Exists(importMetadataPath))
                {
                    File.Delete(importMetadataPath);
                }

                TryDeleteEmptyDirectory(serverFolder);
                return "Deleted server record.";
            }));
        }

        return steps;
    }

    private static ServerDeleteStepResult DeleteChildDirectory(
        string name,
        string serverFolder,
        string childName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(serverFolder, childName);
        return RunStep(name, path, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return $"Deleted {childName}.";
        });
    }

    private static ServerDeleteStepResult RunStep(string name, string? path, Func<string> action)
    {
        try
        {
            var message = action();
            return new ServerDeleteStepResult(name, true, false, path, message);
        }
        catch (Exception ex)
        {
            return Failed(name, path, ex.Message);
        }
    }

    private static ServerDeleteStepResult Failed(string name, string? path, string message)
    {
        return new ServerDeleteStepResult(name, false, false, path, message);
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static bool IsInside(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
