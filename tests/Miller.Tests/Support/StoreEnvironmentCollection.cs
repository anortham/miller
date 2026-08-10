using Xunit;

namespace Miller.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StoreEnvironmentCollection
{
    public const string Name = nameof(StoreEnvironmentCollection);
}
