using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _contentDbPath;

    public ContentCorpusWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-contentdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, "content.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Write_BuildsWorkspaceSourceContentDb_AndSkipsInvalidRowsIntoFacts()
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
        using var fx = JulieDbFixture.Create(
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
            },
            extraFiles:
            [
                new JulieDbFixture.FileSpec("docs/guide.md")
                {
                    Language = "markdown",
                    DiskText = "KnownSourceError appears in docs and content mode should find it.",
                },
                new JulieDbFixture.FileSpec("miller.json")
                {
                    Language = "json",
                    DiskText = """{"marker":"KnownConfigError"}""",
                },
                new JulieDbFixture.FileSpec("src/Stale.cs")
                {
                    Language = "csharp",
                    DiskText = "KnownSourceError stale",
                    StaleHash = true,
                },
                new JulieDbFixture.FileSpec("src/Pending.cs")
                {
                    Language = "csharp",
                    Status = "pending",
                    DiskText = "KnownSourceError pending",
                },
                new JulieDbFixture.FileSpec("src/Missing.cs")
                {
                    Language = "csharp",
                    DiskText = null,
                },
                new JulieDbFixture.FileSpec("src/Binary.cs")
                {
                    Language = "csharp",
                    DiskBytes = [0xFF, 0xFE, 0x00],
                },
                new JulieDbFixture.FileSpec("src/Big.cs")
                {
                    Language = "csharp",
                    DiskText = "small",
                    ContentBytesOverride = 2_000_000,
                },
            ]);

        ContentCorpusFacts facts = ContentCorpusWriter.Write(
            _contentDbPath,
            fx.DbPath,
            fx.WorkspaceRoot,
            workspaceId: "workspace-1",
            revision: 12);

        Assert.Equal("current", facts.State);
        Assert.Equal(1, facts.SchemaVersion);
        Assert.Equal(12, facts.WorkspaceRevision);
        Assert.Equal(3, facts.SourceCount);
        Assert.Equal(3, facts.ChunkCount);
        Assert.True(facts.IndexedSourceBytes > 0);
        Assert.True(facts.StoredRawBytes > 0);
        Assert.Equal(0, facts.ScopeSkipped);
        Assert.Equal(1, facts.StatusSkipped);
        Assert.Equal(1, facts.TooLargeSkipped);
        Assert.Equal(1, facts.MissingSkipped);
        Assert.Equal(1, facts.HashMismatchSkipped);
        Assert.Equal(1, facts.NonUtf8Skipped);

        using var connection = OpenRead();
        Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM content_sources"));
        Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM content_chunks"));
        Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceSource}'"));
        Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceDocs}'"));
        Assert.Equal(1L, ScalarLong(connection, $"SELECT COUNT(*) FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceConfig}'"));
        Assert.Equal("src/Api.cs", ScalarString(connection, $"SELECT path FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceSource}'"));
        Assert.Equal("docs/guide.md", ScalarString(connection, $"SELECT path FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceDocs}'"));
        Assert.Equal("miller.json", ScalarString(connection, $"SELECT path FROM content_sources WHERE content_kind = '{TextContentKind.WorkspaceConfig}'"));
        Assert.Equal("src/Api.cs", ScalarString(connection, $"SELECT display_path FROM content_chunks WHERE content_kind = '{TextContentKind.WorkspaceSource}'"));
        Assert.Contains("KnownSourceError", ScalarString(connection, $"SELECT raw_text FROM content_chunks WHERE content_kind = '{TextContentKind.WorkspaceSource}'"));
        Assert.Contains("KnownSourceError", ScalarString(connection, $"SELECT raw_text FROM content_chunks WHERE content_kind = '{TextContentKind.WorkspaceDocs}'"));
        Assert.Contains("KnownConfigError", ScalarString(connection, $"SELECT raw_text FROM content_chunks WHERE content_kind = '{TextContentKind.WorkspaceConfig}'"));
        Assert.Equal("sym-api", ScalarString(connection, $"SELECT containing_symbol_id FROM content_chunks WHERE content_kind = '{TextContentKind.WorkspaceSource}'"));
    }

    [Fact]
    public void Write_ReplacesExistingContentDbAtomically()
    {
        File.WriteAllText(_contentDbPath, "not sqlite");
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            [new JulieDbFixture.SymbolRow("sym", "Api", "class", "csharp", "src/Api.cs", "public class Api", 1, null)],
            fileContent: new Dictionary<string, string> { ["src/Api.cs"] = "public class Api { }" });

        ContentCorpusWriter.Write(_contentDbPath, fx.DbPath, fx.WorkspaceRoot, "workspace-1", revision: 1);

        using var connection = OpenRead();
        Assert.Equal(1L, ScalarLong(connection, "SELECT schema_version FROM content_meta"));
        Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM content_sources"));
    }

    private SqliteConnection OpenRead()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
