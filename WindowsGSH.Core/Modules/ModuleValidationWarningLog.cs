namespace WindowsGSH.Core.Modules;

internal static class ModuleValidationWarningLog
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.OrdinalIgnoreCase);

    public static void AddOnce(string modulePath, ModuleValidationMessage warning)
    {
        var key = $"{modulePath}|{warning.Code}|{warning.Path}|{warning.Message}";
        lock (Gate)
        {
            if (!LoggedWarnings.Add(key))
            {
                return;
            }
        }

        ModuleLog.Add($"Module warning {modulePath} [{warning.Code}] {warning.Path}: {warning.Message}");
    }
}
