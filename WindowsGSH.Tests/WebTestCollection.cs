using Xunit;

namespace WindowsGSH.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebTestCollection
{
    public const string Name = "Web tests";
}
