using Xunit;

namespace WindowsGSH.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SteamBranchPasswordStoreTestCollection
{
    public const string Name = "Steam branch password store";
}
