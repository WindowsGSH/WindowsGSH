using Xunit;

namespace WindowsGSH.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalStateRecoveryTestCollection
{
    public const string Name = "Local state recovery";
}
