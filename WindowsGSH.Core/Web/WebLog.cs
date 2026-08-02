namespace WindowsGSH.Core.Web;

public static class WebLog
{
    public static Action<string, string?>? Sink { get; set; }

    public static void Add(string message) => Sink?.Invoke(message, "Web");
}
