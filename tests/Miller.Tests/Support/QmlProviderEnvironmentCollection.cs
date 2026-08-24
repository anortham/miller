using Xunit;

namespace Miller.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QmlProviderEnvironmentCollection
{
    public const string Name = nameof(QmlProviderEnvironmentCollection);
}
