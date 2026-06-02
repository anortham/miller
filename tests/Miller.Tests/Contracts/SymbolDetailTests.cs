using Miller.Core.Contracts;
using Xunit;

namespace Miller.Tests.Contracts;

/// <summary>
/// Pins <see cref="SymbolDetail.IsTest"/> — the typed test signal Miller reads from julie-extractors v1's
/// indexed <c>symbols.is_test</c> column (schema v1). It replaces the old <c>test_role</c> string record:
/// Miller only ever used the role as a presence predicate (exclude a test HttpClient url literal from the
/// route bridge), so the typed boolean is a lossless, parse-free replacement.
/// </summary>
public sealed class SymbolDetailTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsTest_FlowsVerbatimFromConstructor(bool isTest)
    {
        var detail = new SymbolDetail(
            Id: "s1",
            Name: "Foo",
            Kind: "method",
            FilePath: "src/Foo.cs",
            Signature: "void Foo()",
            Namespace: null,
            IsTest: isTest,
            ParentClassName: null);

        Assert.Equal(isTest, detail.IsTest);
    }

    [Fact]
    public void IsTest_DefaultsAreNotAssumed_ProductionSymbolIsFalse()
    {
        var prod = new SymbolDetail("s2", "Service", "class", "src/Service.cs", "class Service", "App", IsTest: false, ParentClassName: null);
        Assert.False(prod.IsTest);
    }
}
