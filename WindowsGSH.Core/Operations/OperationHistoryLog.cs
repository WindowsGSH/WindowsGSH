namespace WindowsGSH.Core.Operations;

public static class OperationHistoryLog
{
    public static Action<ServerOperationSnapshot>? Sink { get; set; }

    public static void Add(ServerOperationSnapshot operation)
    {
        Sink?.Invoke(operation);
    }
}
