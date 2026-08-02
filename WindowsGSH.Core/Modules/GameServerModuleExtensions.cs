using System.Diagnostics;
using System.Reflection;

namespace WindowsGSH.Core.Modules;

public static class GameServerModuleExtensions
{
    public static SteamInstallDefinition? GetSteamInstall(this IGameServerModule module)
    {
        if (module is IModuleSteamInstallCapability capability)
        {
            return capability.SteamInstall;
        }

        return InvokeLegacyMember<SteamInstallDefinition?>(module, "SteamInstall", []);
    }

    public static Task<InstallPlan> CreateInstallPlanAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleSteamInstallCapability capability)
        {
            return capability.CreateInstallPlanAsync(instance, cancellationToken);
        }

        return InvokeLegacyMember<Task<InstallPlan>>(module, nameof(CreateInstallPlanAsync), [instance, cancellationToken]) ??
            Task.FromException<InstallPlan>(new NotSupportedException($"{module.Name} does not provide install automation."));
    }

    public static IReadOnlyList<ConfigFieldDefinition> GetConfigFields(this IGameServerModule module)
    {
        if (module is IModuleConfigCapability capability)
        {
            return capability.GetConfigFields();
        }

        return InvokeLegacyMember<IReadOnlyList<ConfigFieldDefinition>>(module, nameof(GetConfigFields), []) ?? [];
    }

    public static Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleConfigCapability capability)
        {
            return capability.ReadConfigFileSettingsAsync(instance, cancellationToken);
        }

        return InvokeLegacyMember<Task<IReadOnlyDictionary<string, object?>>>(
                module,
                nameof(ReadConfigFileSettingsAsync),
                [instance, cancellationToken]) ??
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    public static Task WriteConfigFileSettingsAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleConfigCapability capability)
        {
            return capability.WriteConfigFileSettingsAsync(instance, cancellationToken);
        }

        return InvokeLegacyMember<Task>(module, nameof(WriteConfigFileSettingsAsync), [instance, cancellationToken]) ??
            Task.CompletedTask;
    }

    public static Task<QueryResult> QueryAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleQueryCapability capability)
        {
            return capability.QueryAsync(instance, cancellationToken);
        }

        return InvokeLegacyMember<Task<QueryResult>>(module, nameof(QueryAsync), [instance, cancellationToken]) ??
            Task.FromResult(new QueryResult(ModuleServerStatus.Unknown, Message: $"{module.Name} does not provide query automation."));
    }

    public static Task<string> ExecuteRconCommandAsync(
        this IGameServerModule module,
        ServerInstance instance,
        string command,
        CancellationToken cancellationToken)
    {
        if (module is IModuleRconCapability capability)
        {
            return capability.ExecuteRconCommandAsync(instance, command, cancellationToken);
        }

        return InvokeLegacyMember<Task<string>>(module, nameof(ExecuteRconCommandAsync), [instance, command, cancellationToken]) ??
            Task.FromException<string>(new NotSupportedException($"{module.Name} does not provide RCON automation."));
    }

    public static IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets(this IGameServerModule module)
    {
        if (module is IModuleBackupCapability capability)
        {
            return capability.GetBackupTargets();
        }

        return InvokeLegacyMember<IReadOnlyList<ServerBackupTargetDefinition>>(module, nameof(GetBackupTargets), []) ?? [];
    }

    public static IReadOnlyList<ServerPortDefinition> GetPorts(this IGameServerModule module)
    {
        if (module is IModulePortCapability capability)
        {
            return capability.GetPorts();
        }

        return InvokeLegacyMember<IReadOnlyList<ServerPortDefinition>>(module, nameof(GetPorts), []) ?? [];
    }

    public static IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions(this IGameServerModule module)
    {
        if (module is IModuleAddonCapability capability)
        {
            return capability.GetAddonDefinitions();
        }

        return InvokeLegacyMember<IReadOnlyList<ServerAddonDefinition>>(module, nameof(GetAddonDefinitions), []) ?? [];
    }

    public static ServerAddonStatus GetAddonStatus(
        this IGameServerModule module,
        ServerInstance instance,
        string addonId)
    {
        if (module is IModuleAddonCapability capability)
        {
            return capability.GetAddonStatus(instance, addonId);
        }

        return InvokeLegacyMember<ServerAddonStatus>(module, nameof(GetAddonStatus), [instance, addonId]) ??
            new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Manual addon");
    }

    public static Task InstallAddonAsync(
        this IGameServerModule module,
        ServerInstance instance,
        string addonId,
        CancellationToken cancellationToken)
    {
        if (module is IModuleAddonCapability capability)
        {
            return capability.InstallAddonAsync(instance, addonId, cancellationToken);
        }

        return InvokeLegacyMember<Task>(module, nameof(InstallAddonAsync), [instance, addonId, cancellationToken]) ??
            Task.FromException(new NotSupportedException($"{module.Name} does not provide addon installation automation."));
    }

    public static Task RemoveAddonAsync(
        this IGameServerModule module,
        ServerInstance instance,
        string addonId,
        CancellationToken cancellationToken)
    {
        if (module is IModuleAddonCapability capability)
        {
            return capability.RemoveAddonAsync(instance, addonId, cancellationToken);
        }

        return InvokeLegacyMember<Task>(module, nameof(RemoveAddonAsync), [instance, addonId, cancellationToken]) ??
            Task.FromException(new NotSupportedException($"{module.Name} does not provide addon removal automation."));
    }

    public static Task<IReadOnlyList<Process>> StartAddonProcessesAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleAddonCapability capability)
        {
            return capability.StartAddonProcessesAsync(instance, cancellationToken);
        }

        return InvokeLegacyMember<Task<IReadOnlyList<Process>>>(
                module,
                nameof(StartAddonProcessesAsync),
                [instance, cancellationToken]) ??
            Task.FromResult<IReadOnlyList<Process>>([]);
    }

    public static Task<ModuleVerifyResult> VerifyAsync(
        this IGameServerModule module,
        ServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (module is IModuleVerifyCapability capability)
        {
            return capability.VerifyAsync(instance, cancellationToken);
        }

        return Task.FromResult(new ModuleVerifyResult(
            ModuleVerifyOutcome.Unsupported,
            $"{module.Name} does not support file verification."));
    }

    public static bool AllowsOnlineVerification(this IGameServerModule module)
    {
        if (module is IModuleVerifyCapability capability)
        {
            return capability.AllowsOnlineVerification;
        }

        return false;
    }

    private static T? InvokeLegacyMember<T>(IGameServerModule module, string name, object?[] arguments)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        if (arguments.Length == 0)
        {
            var property = module.GetType().GetProperty(name, flags);
            if (property != null && typeof(T).IsAssignableFrom(property.PropertyType))
            {
                try
                {
                    return (T?)property.GetValue(module);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
            }
        }

        var method = module.GetType().GetMethod(
            name,
            flags,
            binder: null,
            types: arguments.Select(argument => argument?.GetType() ?? typeof(object)).ToArray(),
            modifiers: null);

        if (method == null || !typeof(T).IsAssignableFrom(method.ReturnType))
        {
            return default;
        }

        try
        {
            return (T?)method.Invoke(module, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
