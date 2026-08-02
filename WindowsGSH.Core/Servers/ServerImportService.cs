using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSH.Core.IO;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Servers;

public sealed class ServerImportService : IServerImportService
{
    private readonly IServerOperationCoordinator _operations;

    public ServerImportService(IServerOperationCoordinator? operations = null)
    {
        _operations = operations ?? ServerOperationManager.Shared;
    }

    public async Task<ServerImportResult> ImportAsync(ServerImportRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateRequest(request);
        if (validation != null)
        {
            return validation;
        }

        ServerOperationScope? operation = null;
        if (request.UseOperationCoordinator &&
            !_operations.TryBeginServerOperation(
                request.ServerId,
                request.ServerName,
                ServerOperationKind.Import,
                out operation,
                out var existing,
                "Import existing server"))
        {
            var message = existing == null
                ? $"{request.ServerName} already has a conflicting operation."
                : $"{request.ServerName} is busy: {existing.Status}.";
            return ServerImportResult.Failed(message, ServerOperationStatus.Running);
        }

        try
        {
            var token = operation?.CancellationToken ?? cancellationToken;
            token.ThrowIfCancellationRequested();

            var settings = new Dictionary<string, object?>(request.Settings, StringComparer.OrdinalIgnoreCase);
            var probe = CreateInstance(request, settings);
            var warnings = new List<string>();
            if (request.ReadExistingConfig)
            {
                try
                {
                    var readSettings = await request.Module.ReadConfigFileSettingsAsync(probe, token).ConfigureAwait(false);
                    foreach (var pair in readSettings)
                    {
                        settings[pair.Key] = pair.Value;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Existing config could not be read: {ex.Message}");
                }
            }

            var instance = CreateInstance(request, settings);
            if (!request.Module.IsInstallValid(instance))
            {
                return ServerImportResult.Failed("The selected install path is not valid for this module.");
            }

            Directory.CreateDirectory(instance.ServerFolder);
            WriteServerConfig(request.Module, instance);
            operation?.Dispose();
            return new ServerImportResult(true, ServerOperationStatus.Completed, $"{instance.Name} imported.", instance, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request.UseOperationCoordinator)
            {
                _operations.FailOperation(request.ServerId, ex);
            }

            return ServerImportResult.Failed(ex.Message);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    private static ServerImportResult? ValidateRequest(ServerImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleId) ||
            !string.Equals(request.ModuleId, request.Module.Id, StringComparison.OrdinalIgnoreCase))
        {
            return ServerImportResult.Failed("The selected module id does not match the loaded module.");
        }

        if (string.IsNullOrWhiteSpace(request.ServerId) ||
            request.ServerId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return ServerImportResult.Failed("Server id is required and must be a valid folder name.");
        }

        if (string.IsNullOrWhiteSpace(request.ServerName))
        {
            return ServerImportResult.Failed("Server name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.InstallPath) ||
            !Directory.Exists(request.InstallPath))
        {
            return ServerImportResult.Failed("Install path does not exist.");
        }

        if (string.IsNullOrWhiteSpace(request.ServerFolder))
        {
            return ServerImportResult.Failed("Server folder is required.");
        }

        if (File.Exists(Path.Combine(request.ServerFolder, "ServerConfig.json")))
        {
            return ServerImportResult.Failed("A server config already exists at the target location.");
        }

        return null;
    }

    private static ServerInstance CreateInstance(ServerImportRequest request, IReadOnlyDictionary<string, object?> settings)
    {
        return new ServerInstance(
            request.ServerId,
            request.ServerName,
            request.Module.Id,
            Path.GetFullPath(request.ServerFolder),
            Path.GetFullPath(request.InstallPath),
            Path.Combine(Path.GetFullPath(request.ServerFolder), "ServerConfig.json"),
            settings);
    }

    private static void WriteServerConfig(IGameServerModule module, ServerInstance instance)
    {
        var steamInstall = module.GetSteamInstall();
        var config = JsonSerializer.SerializeToNode(new
        {
            id = instance.Id,
            name = instance.Name,
            moduleId = module.Id,
            runtime = "native",
            installPath = instance.InstallPath,
            steam = steamInstall == null
                ? null
                : new
                {
                    appId = steamInstall.AppId,
                    branch = string.Empty
                },
            settings = instance.Settings
        })?.AsObject() ?? [];

        new ServerSecretStore().ProtectConfigSecrets(module, instance.ServerFolder, config);
        AtomicFile.WriteAllText(instance.ConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
