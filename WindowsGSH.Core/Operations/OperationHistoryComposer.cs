namespace WindowsGSH.Core.Operations;

public sealed class OperationHistoryComposer
{
    public IReadOnlyList<ServerOperationSnapshot> Compose(
        IEnumerable<ServerOperationSnapshot> activeOperations,
        IEnumerable<ServerOperationSnapshot> persistedHistory,
        int maxPersistedRows = 100)
    {
        var active = activeOperations
            .Where(operation => operation.IsActive)
            .OrderBy(operation => operation.StartedAt)
            .ToArray();
        var activeKeys = active
            .Select(GetOperationKey)
            .ToHashSet(StringComparer.Ordinal);
        var persisted = persistedHistory
            .Where(operation => !operation.IsActive)
            .OrderByDescending(operation => operation.FinishedAt ?? operation.StartedAt)
            .Where(operation => !activeKeys.Contains(GetOperationKey(operation)))
            .Take(Math.Clamp(maxPersistedRows, 1, 500));

        return active
            .Concat(persisted)
            .ToArray();
    }

    private static string GetOperationKey(ServerOperationSnapshot operation)
    {
        return string.Join(
            "|",
            operation.ServerId,
            operation.Kind,
            operation.StartedAt.ToUniversalTime().ToString("O"));
    }
}
