using WindowsGSH.Core.Modules;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ModulePortSnapshotStoreTests
{
    [Fact]
    public void Generic_wrapper_registers_an_immutable_port_snapshot()
    {
        var module = new GenericWrapperModule();

        var found = ModulePortSnapshotStore.TryGet(module, out var snapshot);

        Assert.True(found);
        Assert.Equal(["game", "query"], snapshot.Select(port => port.Id));
        Assert.IsAssignableFrom<IReadOnlyList<ServerPortDefinition>>(snapshot);
        Assert.False(snapshot is ServerPortDefinition[]);
    }
}
