using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusSidecarTests : IDisposable
{
    private readonly string _dir;

    public ContentCorpusSidecarTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-content-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ContentDbPathFor_DerivesSiblingContentDb()
    {
        string symbolsDb = Path.Combine(_dir, ".miller", "symbols.db");

        string contentDb = ContentCorpusSidecar.ContentDbPathFor(symbolsDb);

        Assert.Equal(Path.Combine(_dir, ".miller", "content.db"), contentDb);
    }

    [Fact]
    public void EnsureBuilt_ArtifactMissing_BuildsFreshUsableArtifactAndReturnsTrue()
    {
        using var fx = SourceFixture();
        var sidecar = new ContentCorpusSidecar();

        bool built = sidecar.EnsureBuilt(
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "workspace-1",
            revision: 7);

        Assert.True(built);
        Assert.True(File.Exists(ContentCorpusSidecar.ContentDbPathFor(fx.DbPath)));
        FtsTextContentSearchIndex index = sidecar.OpenRequired(fx.DbPath, expectedRevision: 7);
        Assert.Equal(1, index.DocumentCount);
        var hit = Assert.Single(index.Search("KnownSourceError", TextContentKind.WorkspaceSource, limit: 10));
        Assert.Equal("src/Api.cs", hit.Path);
        Assert.Equal("Handle", hit.ContainingSymbolName);
    }

    [Fact]
    public void EnsureBuilt_ArtifactAlreadyFresh_SkipsAndReturnsFalse()
    {
        using var fx = SourceFixture();
        var sidecar = new ContentCorpusSidecar();

        Assert.True(sidecar.EnsureBuilt(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7));
        Assert.False(sidecar.EnsureBuilt(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7));
    }

    [Fact]
    public void OpenRequired_MissingArtifact_FailsVisibly()
    {
        using var fx = SourceFixture();
        var sidecar = new ContentCorpusSidecar();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sidecar.OpenRequired(fx.DbPath, expectedRevision: 7));

        Assert.Contains("content corpus", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenRequired_StaleArtifact_FailsVisibly()
    {
        using var fx = SourceFixture();
        var sidecar = new ContentCorpusSidecar();
        Assert.True(sidecar.EnsureBuilt(fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 6));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sidecar.OpenRequired(fx.DbPath, expectedRevision: 7));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expected 7", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JulieDbFixture SourceFixture()
    {
        const string sourceText = """
            public class Api
            {
                public void Handle()
                {
                    throw new InvalidOperationException("KnownSourceError");
                }
            }
            """;
        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow("sym-api", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)
                {
                    EndLine = 7,
                },
                new JulieDbFixture.SymbolRow("sym-handle", "Handle", "method", "csharp", "src/Api.cs", "public void Handle()", 3, "sym-api")
                {
                    EndLine = 6,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = sourceText,
            });
    }
}
