using System.Runtime.CompilerServices;

namespace WindowsGSH.Core.Modules;

/// <summary>
/// Retains the validated manifest port declarations associated with a loaded module instance.
/// Consumers that only need declarative port metadata can use this snapshot without invoking
/// arbitrary compiled module code.
/// </summary>
public static class ModulePortSnapshotStore
{
    private static readonly ConditionalWeakTable<IGameServerModule, PortSnapshot> Snapshots = new();
    private static readonly object Sync = new();

    internal static void Register(
        IGameServerModule module,
        IReadOnlyList<ServerPortDefinition> ports)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(ports);

        var snapshot = new PortSnapshot(Array.AsReadOnly(ports.ToArray()));
        lock (Sync)
        {
            Snapshots.Remove(module);
            Snapshots.Add(module, snapshot);
        }
    }

    public static bool TryGet(
        IGameServerModule module,
        out IReadOnlyList<ServerPortDefinition> ports)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (Snapshots.TryGetValue(module, out var snapshot))
        {
            ports = snapshot.Ports;
            return true;
        }

        ports = [];
        return false;
    }

    private sealed record PortSnapshot(IReadOnlyList<ServerPortDefinition> Ports);
}
