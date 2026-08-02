namespace WindowsGSH.Core.Modules;

public sealed record ModuleDescriptor(
    IGameServerModule Module,
    string Id,
    string Name,
    string Version,
    string ModulePath,
    string ModuleType,
    ModuleCapabilities EffectiveCapabilities,
    ModuleOptionalInterfaces OptionalInterfaces,
    ModuleProvenanceSnapshot Provenance,
    IReadOnlyList<ModuleValidationMessage> ValidationWarnings)
{
    public static ModuleDescriptor Create(
        IGameServerModule module,
        string modulePath,
        string moduleType,
        ModuleProvenanceSnapshot provenance,
        IReadOnlyList<ModuleValidationMessage>? validationWarnings = null)
    {
        return new ModuleDescriptor(
            module,
            module.Id,
            module.Name,
            module.Version,
            modulePath,
            moduleType,
            GetEffectiveCapabilities(module),
            ModuleOptionalInterfaces.FromModule(module),
            provenance,
            validationWarnings ?? []);
    }

    public static ModuleCapabilities GetEffectiveCapabilities(IGameServerModule module)
    {
        var capabilities = module.Capabilities;
        return capabilities with
        {
            SupportsUpdate = capabilities.SupportsUpdate || module is IModuleUpdateCapability,
            SupportsConsoleCommands = capabilities.SupportsConsoleCommands || module is IModuleConsoleCommandCapability,
            SupportsApiActions = capabilities.SupportsApiActions || HasApiActions(module),
            SupportsBackups = capabilities.SupportsBackups || module.GetBackupTargets().Count > 0,
            SupportsVerify = capabilities.SupportsVerify || module is IModuleVerifyCapability || module.GetSteamInstall() != null
        };
    }

    private static bool HasApiActions(IGameServerModule module)
    {
        return module is IModuleApiActionCapability apiActionCapability &&
            apiActionCapability.GetApiActions().Count > 0;
    }
}

public sealed record ModuleOptionalInterfaces(
    bool HasUpdate,
    bool HasConsoleCommand,
    bool HasReadiness,
    bool HasApiAction)
{
    public static ModuleOptionalInterfaces FromModule(IGameServerModule module)
    {
        return new ModuleOptionalInterfaces(
            module is IModuleUpdateCapability,
            module is IModuleConsoleCommandCapability,
            module is IModuleReadinessCapability,
            module is IModuleApiActionCapability);
    }
}
