using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the SINGLE production build path (D9): <see cref="RepositoryIndexLoader.Load"/> reads symbols + edges
/// from a julie extract and builds a <see cref="MillerRepositoryIndex"/> whose dependency graph is populated end
/// to end. This is the one path both the bootstrap and the freshness rebuild route through, so the graph is
/// always ready when the index is published (the holder's atomic swap, M3). Contract test over a synth DB —
/// NOT a re-test of julie extraction.
/// </summary>
public sealed class RepositoryIndexLoaderTests
{
    private const string ProcessId = "00000000000000000000000000000001";
    private const string ValidateId = "00000000000000000000000000000002";

    private static JulieDbFixture FixtureWithEdges()
    {
        var rows = new[]
        {
            new JulieDbFixture.SymbolRow(ProcessId, "Process", "method", "csharp", "src/A.cs",
                "public void Process()", 1, null) { EndLine = 3 },
            new JulieDbFixture.SymbolRow(ValidateId, "Validate", "method", "csharp", "src/A.cs",
                "public void Validate()", 5, null) { EndLine = 7 },
        };
        // Both edge sources: a relationships row AND an identifier name-ref, both Process → Validate.
        var relationships = new[]
        {
            new JulieDbFixture.RelationshipRow("r1", ProcessId, ValidateId, "calls"),
        };
        var identifiers = new[]
        {
            new JulieDbFixture.IdentifierRow("i1", "Validate", "call", "csharp", "src/A.cs", 2, ProcessId),
        };
        return JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows, identifiers: identifiers, relationships: relationships);
    }

    [Fact]
    public void Load_BuildsAnIndexWithAPopulatedGraph()
    {
        using var fx = FixtureWithEdges();

        var index = RepositoryIndexLoader.Load(fx.DbPath);

        // The index carries every symbol AND a populated graph.
        Assert.Equal(2, index.DocumentCount);
        Assert.True(index.Graph.Contains(ProcessId));
        Assert.True(index.Graph.Contains(ValidateId));

        // Process → Validate resolved from BOTH sources, deduped by the graph; Validate's sole dependent is Process.
        var dependents = index.Dependents(ValidateId);
        Assert.Single(dependents);
        Assert.Equal("Process", dependents[0].Name);

        var deps = index.Dependencies(ProcessId);
        Assert.Single(deps);
        Assert.Equal("Validate", deps[0].Name);
    }

    [Fact]
    public void Load_CarriesEndLineFromTheReader()
    {
        // The loader reads symbols via SqliteSymbolReader, so EndLine (D7) flows through to the index.
        using var fx = FixtureWithEdges();

        var index = RepositoryIndexLoader.Load(fx.DbPath);

        var process = index.FindByName("Process").Single();
        Assert.Equal(1, process.StartLine);
        Assert.Equal(3, process.EndLine);
    }

    [Fact]
    public void Load_EmptyEdgeTables_YieldsAnEdgelessGraph()
    {
        // A scan-only extract with no relationships and no resolvable identifiers still builds — every symbol is
        // a node, no edges. (CreateDefault carries neither relationships nor identifiers.)
        using var fx = JulieDbFixture.CreateDefault();

        var index = RepositoryIndexLoader.Load(fx.DbPath);

        Assert.Equal(JulieDbFixture.DefaultRows.Count, index.DocumentCount);
        var anyId = JulieDbFixture.DefaultRows[0].Id;
        Assert.True(index.Graph.Contains(anyId));
        Assert.Empty(index.Dependents(anyId));
        Assert.Empty(index.Dependencies(anyId));
    }

    [Fact]
    public void Load_DropsIdentifierFallbackWhenNameIsTooAmbiguous()
    {
        var rows = new List<JulieDbFixture.SymbolRow>
        {
            new(ProcessId, "Process", "method", "csharp", "src/A.cs", "public void Process()", 1, null),
        };
        for (int i = 0; i < 17; i++)
        {
            rows.Add(new JulieDbFixture.SymbolRow(
                $"000000000000000000000000000001{i:x2}",
                "Shared",
                "method",
                "csharp",
                $"src/Shared{i}.cs",
                "public void Shared()",
                1,
                null));
        }

        var identifiers = new[]
        {
            new JulieDbFixture.IdentifierRow("i1", "Shared", "call", "csharp", "src/A.cs", 2, ProcessId),
        };
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            identifiers: identifiers);

        var index = RepositoryIndexLoader.Load(fx.DbPath);

        Assert.True(index.Graph.Contains(ProcessId));
        Assert.Empty(index.Dependencies(ProcessId));
    }

    [Fact]
    public void Load_MissingDbFile_ThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-nope-" + Guid.NewGuid().ToString("N"), "symbols.db");

        var ex = Assert.Throws<FileNotFoundException>(() => RepositoryIndexLoader.Load(missing));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Load_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RepositoryIndexLoader.Load(null!));
    }
}
