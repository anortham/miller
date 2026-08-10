using Xunit;

namespace Miller.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SemanticActivationEnvironmentCollection
{
    public const string Name = nameof(SemanticActivationEnvironmentCollection);
}
