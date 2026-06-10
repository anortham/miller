using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class ContentCorpusContextReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miller-content-context-" + Guid.NewGuid().ToString("N"));
    private readonly string _contentDbPath;

    public ContentCorpusContextReaderTests()
    {
        Directory.CreateDirectory(_dir);
        _contentDbPath = Path.Combine(_dir, "content.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = ContentCorpusSchema.SchemaDdl;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void ReadContainingSymbolChunks_BySymbolId_ReturnsWorkspaceSourceChunks()
    {
        InsertChunk(
            chunkId: "chunk-service",
            path: "src/OrderService.cs",
            rawText: "public void PlaceOrder() { _repo.Save(); }",
            containingSymbolId: "service-id",
            containingSymbolName: "OrderService");
        var symbol = new IndexedSymbol(
            DocId: 0,
            SymbolId: "service-id",
            Name: "OrderService",
            Signature: "class OrderService",
            Kind: "class",
            Language: "csharp",
            FilePath: "src/OrderService.cs",
            StartLine: 1,
            EndLine: 40,
            ParentId: null,
            IsTest: false);

        var hits = ContentCorpusContextReader.ReadContainingSymbolChunks(
            _contentDbPath,
            new[] { symbol },
            excludeTests: false,
            limitPerSymbol: 4);

        var hit = Assert.Single(hits);
        Assert.Equal("chunk-service", hit.ChunkId);
        Assert.Equal("service-id", hit.ContainingSymbolId);
        Assert.Equal("OrderService", hit.ContainingSymbolName);
        Assert.Equal("src/OrderService.cs", hit.Path);
        Assert.Contains("PlaceOrder", hit.Snippet);
    }

    [Fact]
    public void ReadContainingSymbolChunks_ExcludeTests_FiltersTestChunks()
    {
        InsertChunk(
            chunkId: "chunk-test",
            path: "tests/OrderServiceTests.cs",
            rawText: "OrderService test chunk",
            containingSymbolId: "service-id",
            containingSymbolName: "OrderService",
            isTest: true);
        var symbol = new IndexedSymbol(0, "service-id", "OrderService", "class OrderService", "class", "csharp",
            "src/OrderService.cs", 1, 40, null, false);

        var hits = ContentCorpusContextReader.ReadContainingSymbolChunks(
            _contentDbPath,
            new[] { symbol },
            excludeTests: true,
            limitPerSymbol: 4);

        Assert.Empty(hits);
    }

    [Fact]
    public void ReadContainingSymbolChunks_MissingContentDb_ReturnsEmpty()
    {
        var symbol = new IndexedSymbol(0, "service-id", "OrderService", "class OrderService", "class", "csharp",
            "src/OrderService.cs", 1, 40, null, false);

        var hits = ContentCorpusContextReader.ReadContainingSymbolChunks(
            Path.Combine(_dir, "missing-content.db"),
            new[] { symbol },
            excludeTests: false,
            limitPerSymbol: 4);

        Assert.Empty(hits);
    }

    public void Dispose()
    {
        // The SUT opens its content.db connection internally (pooled), so the test cannot set Pooling=false on it.
        // Release pooled handles before deleting the temp dir, or Windows fails the delete with a sharing violation
        // (POSIX unlink tolerates open handles, which is why this only bit on Windows). Matches the sibling
        // ContentCorpus*Tests teardown convention.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void InsertChunk(
        string chunkId,
        string path,
        string rawText,
        string containingSymbolId,
        string containingSymbolName,
        bool isTest = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _contentDbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO content_chunks(
                chunk_id, source_id, content_kind, path, url, display_path, language,
                line_start, line_end, byte_start, byte_end, raw_text, doc_len, is_test,
                source_bytes, containing_symbol_id, containing_symbol_name)
            VALUES(
                $chunk_id, $source_id, $content_kind, $path, NULL, $path, 'csharp',
                10, 12, 100, 180, $raw_text, 8, $is_test,
                $source_bytes, $containing_symbol_id, $containing_symbol_name);
            """;
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        command.Parameters.AddWithValue("$source_id", "source-" + path);
        command.Parameters.AddWithValue("$content_kind", TextContentKind.WorkspaceSource);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$raw_text", rawText);
        command.Parameters.AddWithValue("$is_test", isTest ? 1 : 0);
        command.Parameters.AddWithValue("$source_bytes", rawText.Length);
        command.Parameters.AddWithValue("$containing_symbol_id", containingSymbolId);
        command.Parameters.AddWithValue("$containing_symbol_name", containingSymbolName);
        command.ExecuteNonQuery();
    }
}
