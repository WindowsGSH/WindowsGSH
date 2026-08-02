namespace WindowsGSH.Core.Modules;

public static class ModuleLog
{
    public static Action<string, string?>? Sink { get; set; }

    public static void Add(string message, string? source = null)
    {
        Sink?.Invoke(message, source);
    }
}
