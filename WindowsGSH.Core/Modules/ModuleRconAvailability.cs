namespace WindowsGSH.Core.Modules;

public static class ModuleRconAvailability
{
    public static bool CanUseRcon(IGameServerModule? module, ServerInstance instance)
    {
        return HasRconSupport(module, instance) && IsRconEnabled(module, instance.Settings);
    }

    public static bool HasRconSupport(IGameServerModule? module, ServerInstance instance)
    {
        if (module == null)
        {
            return false;
        }

        if (module.Capabilities.SupportsRcon)
        {
            return true;
        }

        if (module.GetAddonDefinitions()
            .Any(addon => addon.CapabilitiesAdded.SupportsRcon && module.GetAddonStatus(instance, addon.Id).IsEnabled))
        {
            return true;
        }

        return HasRconConfigFields(module);
    }

    public static bool HasRconConfigFields(IGameServerModule? module)
    {
        return module?.GetConfigFields().Any(field => IsRconField(field.Key)) == true;
    }

    public static bool HasRconEnabledField(IGameServerModule? module)
    {
        return module?.GetConfigFields().Any(field => string.Equals(field.Key, "rcon.enabled", StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static bool IsRconEnabled(IGameServerModule? module, IReadOnlyDictionary<string, object?> settings)
    {
        return !HasRconEnabledField(module) || IsTruthy(GetSetting(settings, "rcon.enabled"));
    }

    public static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text => bool.TryParse(text, out var parsed) ? parsed : !string.IsNullOrWhiteSpace(text),
            int number => number != 0,
            long number => number != 0,
            double number => Math.Abs(number) > double.Epsilon,
            _ => value != null
        };
    }

    private static bool IsRconField(string key)
    {
        return key.StartsWith("rcon.", StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetSetting(IReadOnlyDictionary<string, object?> settings, string key)
    {
        return !string.IsNullOrWhiteSpace(key) && settings.TryGetValue(key, out var value) ? value : null;
    }
}
