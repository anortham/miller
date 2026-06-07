using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FtsTextContentSearchIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly string _contentDbPath;

    public FtsTextContentSearchIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-textcontent-fts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, "content.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Search_SourceKind_ReturnsLineSnippetAndMetadata()
    {
        using var fx = BuildFixture(
            ("src/Api.cs", "csharp", false, """
                public class Api
                {
                    public void Handle()
                    {
                        throw new InvalidOperationException("KnownSourceError");
                    }
                }
                """));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        TextContentSearchHit hit = Assert.Single(index.Search(
            "KnownSourceError",
            TextContentKind.WorkspaceSource,
            limit: 10,
            excludeTests: false));

        Assert.Equal(TextContentKind.WorkspaceSource, hit.ContentKind);
        Assert.Equal("src/Api.cs", hit.Path);
        Assert.Equal("csharp", hit.Language);
        Assert.Equal(5, hit.Line);
        Assert.Equal(1, hit.LineStart);
        Assert.True(hit.LineEnd >= 5);
        Assert.True(hit.ByteEnd > hit.ByteStart);
        Assert.True(hit.SourceBytes > 0);
        Assert.NotEmpty(hit.SourceId);
        Assert.NotEmpty(hit.ChunkId);
        Assert.Contains("KnownSourceError", hit.Snippet);
        Assert.Equal("sym-api", hit.ContainingSymbolId);
        Assert.Equal("Api", hit.ContainingSymbolName);
    }

    [Fact]
    public void Search_ContentKinds_ReturnsDocsAndConfigButNotSource()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [
                new JulieDbFixture.SymbolRow(
                    "sym-api",
                    "Api",
                    "class",
                    "csharp",
                    "src/Api.cs",
                    "class Api",
                    1,
                    null)
                {
                    EndLine = 1,
                },
            ],
            fileContent: new Dictionary<string, string>
            {
                ["src/Api.cs"] = "public class Api { string Marker = \"SharedMarker\"; }",
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = "SharedMarker appears in the guide.",
                },
                new JulieDbFixture.FileSpec("miller.json")
                {
                    Language = "json",
                    DiskText = """{"marker":"SharedMarker"}""",
                },
            ]);
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        var hits = index.Search(
            "SharedMarker",
            new[] { TextContentKind.WorkspaceDocs, TextContentKind.WorkspaceConfig },
            limit: 10,
            excludeTests: false);

        Assert.Equal(["docs/guide.md", "miller.json"], hits.Select(static h => h.Path!).Order().ToArray());
        Assert.All(hits, static hit =>
            Assert.True(
                string.Equals(hit.ContentKind, TextContentKind.WorkspaceDocs, StringComparison.Ordinal)
                    || string.Equals(hit.ContentKind, TextContentKind.WorkspaceConfig, StringComparison.Ordinal),
                "hit should be docs or config content"));
    }

    [Fact]
    public void Search_ExcludeTests_FiltersTestSources()
    {
        using var fx = BuildFixture(
            ("src/Prod.cs", "csharp", false, "public class Prod { string s = \"SharedMarker\"; }"),
            ("tests/ProdTests.cs", "csharp", true, "public class ProdTests { string s = \"SharedMarker\"; }"));
        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 7);
        var index = FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7);

        var all = index.Search("SharedMarker", TextContentKind.WorkspaceSource, 10, excludeTests: false);
        var filtered = index.Search("SharedMarker", TextContentKind.WorkspaceSource, 10, excludeTests: true);

        Assert.Equal(["src/Prod.cs", "tests/ProdTests.cs"], all.Select(static h => h.Path!).Order().ToArray());
        TextContentSearchHit hit = Assert.Single(filtered);
        Assert.Equal("src/Prod.cs", hit.Path);
    }

    [Fact]
    public void Open_StaleRevision_FailsClosed()
    {
        WriteMinimalContentDb(revision: 6, schemaVersion: ContentCorpusSchema.SchemaVersion);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7));

        Assert.Contains("revision", ex.Message);
        Assert.Contains("expected 7", ex.Message);
    }

    [Fact]
    public void Open_OldSchemaVersion_FailsClosed()
    {
        WriteMinimalContentDb(revision: 7, schemaVersion: 0);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsTextContentSearchIndex.Open(_contentDbPath, expectedRevision: 7));

        Assert.Contains("schema_version", ex.Message);
        Assert.Contains(ContentCorpusSchema.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    private JulieDbFixture BuildFixture(params (string Path, string Language, bool IsTest, string Text)[] files)
    {
        var rows = new List<JulieDbFixture.SymbolRow>();
        var fileContent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string path, string language, bool isTest, string text) in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            rows.Add(new JulieDbFixture.SymbolRow(
                "sym-" + name.ToLowerInvariant(),
                name,
                "class",
                language,
                path,
                "class " + name,
                1,
                null)
            {
                EndLine = text.Split('\n').Length,
                IsTest = isTest,
            });
            fileContent[path] = text;
        }

        return JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            rows,
            fileContent: fileContent);
    }

    private void WriteMinimalContentDb(long revision, int schemaVersion)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = ContentCorpusSchema.SchemaDdl + """
            INSERT INTO content_meta
                (schema_version, workspace_revision, chunker_version, source_count, chunk_count,
                 indexed_source_bytes, stored_raw_bytes, updated_at_utc)
            VALUES ($schema, $revision, 'test', 0, 0, 0, 0, '1970-01-01T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$schema", schemaVersion);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }
}
