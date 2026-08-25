using Xunit;

namespace Miller.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ContinuousTestDaemonAdoptionCollection
{
    public const string Name = nameof(ContinuousTestDaemonAdoptionCollection);
}
