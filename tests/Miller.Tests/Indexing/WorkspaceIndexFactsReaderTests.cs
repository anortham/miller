using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceIndexFactsReaderTests
{
    [Fact]
    public void Read_ReturnsCountsFromSchemaFiveArtifact()
    {
        using var fx = JulieDbFixture.CreateDefault();

        WorkspaceIndexFacts facts = WorkspaceIndexFactsReader.Read(fx.DbPath);

        Assert.Equal(JulieDbFixture.DefaultRows.Count, facts.DocumentCount);
        Assert.True(facts.KnownExtensionsCount > 0);
    }

    [Fact]
    public void Read_KnownExtensions_DerivedFromTheV1PathColumn()
    {
        // C6 v1 lock: the reader reads symbols.path (v1), NOT the gone file_path, to derive the distinct file
        // extensions. Two rows share a .cs file and a third is a distinct .ts file → exactly two extensions.
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
            {
                new JulieDbFixture.SymbolRow("a0000000000000000000000000000001", "A", "class", "csharp",
                    "src/A.cs", "class A", 1, null),
                new JulieDbFixture.SymbolRow("a0000000000000000000000000000002", "B", "method", "csharp",
                    "src/A.cs", "void B()", 5, null),                 // same .cs file
                new JulieDbFixture.SymbolRow("a0000000000000000000000000000003", "T", "function", "typescript",
                    "web/t.ts", "function t()", 1, null),             // distinct .ts file
            });

        WorkspaceIndexFacts facts = WorkspaceIndexFactsReader.Read(fx.DbPath);

        Assert.Equal(3, facts.DocumentCount);
        Assert.Equal(2, facts.KnownExtensionsCount); // {.cs, .ts}
    }

    [Fact]
    public void Read_MissingDbThrowsFileNotFound()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "miller-facts-missing-" + Guid.NewGuid().ToString("N"), "symbols.db");

        Assert.Throws<FileNotFoundException>(() => WorkspaceIndexFactsReader.Read(missing));
    }

}
