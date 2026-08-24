using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class JulieDbFixtureTests
{
    [Fact]
    public void AddStructuralFacts_MatchesSingleRowReaderResults()
    {
        var facts = new[]
        {
            new JulieDbFixture.StructuralFactInput(
                "marker-one",
                null,
                "src/One.cs",
                PatternId: MarkerFactReader.PatternId,
                CaptureName: "marker",
                NodeKind: "comment",
                MetadataJson: """{"marker":"TODO","owner":"team","description":"one"}"""),
            new JulieDbFixture.StructuralFactInput(
                "marker-two",
                null,
                "src/Two.cs",
                Language: "markdown",
                PatternId: MarkerFactReader.PatternId,
                CaptureName: "marker",
                NodeKind: "comment",
                MetadataJson: """{"marker":"FIXME"}""")
        };

        using var singleFixture = JulieDbFixture.CreateDefault();
        foreach (JulieDbFixture.StructuralFactInput fact in facts)
        {
            singleFixture.AddStructuralFact(
                fact.StructuralFactId,
                fact.ContainingSymbolId,
                fact.Path,
                fact.Language,
                fact.PatternId,
                fact.CaptureName,
                fact.NodeKind,
                fact.MetadataJson);
        }

        using var batchFixture = JulieDbFixture.CreateDefault();
        batchFixture.AddStructuralFacts(facts);

        Assert.Equal(
            MarkerFactReader.Read(singleFixture.DbPath, excludeTests: false, limit: 500),
            MarkerFactReader.Read(batchFixture.DbPath, excludeTests: false, limit: 500));
    }
}
