using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FtsRegionSearchIndexTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public FtsRegionSearchIndexTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-region-ftsread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "search.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Search_CommentKind_ReturnsCommentRegionAndNeverSearchesSymbols()
    {
        WriteSearchDb(
            new[]
            {
            Region("comment-1", "comment", "src/Comments.cs", 12,
                "// HiddenTodoMarker appears here in a comment-only region.",
                containingSymbolId: "sym-comment"),
            },
            symbols: new[] { ("sym-comment", "CommentOwner"), ("sym-code-only", "HiddenTodoMarker") });

        var index = FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7);

        var hits = index.Search("HiddenTodoMarker", new HashSet<string> { "comment" }, limit: 10, excludeTests: false);

        RegionSearchHit hit = Assert.Single(hits);
        Assert.Equal("comment-1", hit.RegionId);
        Assert.Equal("comment", hit.Kind);
        Assert.Equal("src/Comments.cs", hit.Path);
        Assert.Equal(12, hit.Line);
        Assert.Equal("sym-comment", hit.ContainingSymbolId);
        Assert.Equal("CommentOwner", hit.ContainingSymbolName);
        Assert.Contains("comment-only region", hit.Snippet);
    }

    [Fact]
    public void Search_StringLiteralKind_ReturnsStringLiteralRegion()
    {
        WriteSearchDb(
            Region("string-1", "string_literal", "src/Config.cs", 21,
                "\"Server=prod;Password=literalSecret\""),
            Region("comment-1", "comment", "src/Config.cs", 4,
                "// literalSecret is also mentioned in a comment"));

        var index = FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7);

        var hits = index.Search("literalSecret", new HashSet<string> { "string_literal" }, limit: 10, excludeTests: false);

        RegionSearchHit hit = Assert.Single(hits);
        Assert.Equal("string-1", hit.RegionId);
        Assert.Equal("string_literal", hit.Kind);
        Assert.Equal("\"Server=prod;Password=literalSecret\"", hit.RawText);
    }

    [Fact]
    public void Search_KindUnion_ReturnsEveryRequestedKindOnly()
    {
        WriteSearchDb(
            Region("comment-1", "comment", "src/One.cs", 5, "// migration hook"),
            Region("doc-1", "doc_comment", "src/Two.cs", 8, "/// migration documentation"),
            Region("string-1", "string_literal", "src/Three.cs", 10, "\"migration string\""));

        var index = FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7);

        var unionHits = index.Search("migration", new HashSet<string> { "comment", "doc_comment" }, 10, false);
        var commentOnlyHits = index.Search("migration", new HashSet<string> { "comment" }, 10, false);

        Assert.Equal(new[] { "comment", "doc_comment" }, unionHits.Select(static h => h.Kind).Order().ToArray());
        Assert.Equal(new[] { "comment" }, commentOnlyHits.Select(static h => h.Kind).ToArray());
    }

    [Fact]
    public void Search_ExcludeTests_FiltersTestPathsWithPathHeuristic()
    {
        WriteSearchDb(
            Region("prod-1", "comment", "src/Widget.cs", 3, "// fixture token in production"),
            Region("test-1", "comment", "tests/WidgetTests.cs", 4, "// fixture token in test"));

        var index = FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7);

        var all = index.Search("fixture", new HashSet<string> { "comment" }, 10, excludeTests: false);
        var filtered = index.Search("fixture", new HashSet<string> { "comment" }, 10, excludeTests: true);

        Assert.Equal(new[] { "prod-1", "test-1" }, all.Select(static h => h.RegionId).Order().ToArray());
        Assert.Equal(new[] { "prod-1" }, filtered.Select(static h => h.RegionId).ToArray());
    }

    [Fact]
    public void Search_OrdersByScoreThenPathLineAndRegionId()
    {
        WriteSearchDb(
            Region("b", "comment", "src/B.cs", 2, "// same rank term"),
            Region("a", "comment", "src/A.cs", 5, "// same rank term"),
            Region("c", "comment", "src/A.cs", 6, "// same rank term"));

        var index = FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7);

        var hits = index.Search("same rank", new HashSet<string> { "comment" }, 10, excludeTests: false);

        Assert.Equal(new[] { "a", "c", "b" }, hits.Select(static h => h.RegionId).ToArray());
    }

    [Fact]
    public void Open_MissingFile_FailsClosedWithActionableException()
    {
        string missing = Path.Combine(_dir, "missing.db");

        var ex = Assert.Throws<FileNotFoundException>(() =>
            FtsRegionSearchIndex.Open(missing, expectedRevision: 7));

        Assert.Contains("search.db", ex.Message);
    }

    [Fact]
    public void Open_StaleRevision_FailsClosed()
    {
        WriteSearchDb(Region("r", "comment", "src/A.cs", 1, "// stale"), revision: 6);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7));

        Assert.Contains("revision", ex.Message);
        Assert.Contains("expected 7", ex.Message);
    }

    [Fact]
    public void Open_OldSchemaVersion_FailsClosed()
    {
        WriteSearchDb(Region("r", "comment", "src/A.cs", 1, "// old schema"), schemaVersion: 2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7));

        Assert.Contains("schema_version", ex.Message);
        Assert.Contains(SearchIndexWriter.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public void Open_FutureSchemaVersion_FailsClosed()
    {
        WriteSearchDb(Region("r", "comment", "src/A.cs", 1, "// future schema"), schemaVersion: SearchIndexWriter.SchemaVersion + 1);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7));

        Assert.Contains("schema_version", ex.Message);
        Assert.Contains(SearchIndexWriter.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
    }

    [Fact]
    public void Open_MissingRegionTables_FailsClosed()
    {
        using var connection = OpenWriteCreate();
        Execute(connection, $"""
            CREATE TABLE meta(
                revision INTEGER,
                doc_count INTEGER,
                avgdl REAL,
                schema_version INTEGER,
                region_count INTEGER,
                region_avgdl REAL);
            INSERT INTO meta(revision, doc_count, avgdl, schema_version, region_count, region_avgdl)
            VALUES (7, 0, 0.0, {SearchIndexWriter.SchemaVersion}, 0, 0.0);
            """);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7));

        Assert.Contains("regions_fts", ex.Message);
    }

    [Fact]
    public void Open_MalformedMeta_FailsClosed()
    {
        using var connection = OpenWriteCreate();
        Execute(connection, """
            CREATE VIRTUAL TABLE regions_fts USING fts5(
                region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
            CREATE TABLE search_regions(
                region_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                containing_symbol_id TEXT,
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                start_byte INTEGER NOT NULL,
                end_byte INTEGER NOT NULL,
                raw_text TEXT NOT NULL,
                doc_len INTEGER NOT NULL);
            CREATE TABLE meta(revision INTEGER, schema_version INTEGER);
            INSERT INTO meta(revision, schema_version) VALUES (7, 3);
            """);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FtsRegionSearchIndex.Open(_dbPath, expectedRevision: 7));

        Assert.Contains("meta", ex.Message);
    }

    private static RegionRow Region(
        string id,
        string kind,
        string path,
        int line,
        string rawText,
        string? containingSymbolId = null) =>
        new(id, kind, path, "csharp", containingSymbolId, line, line, 0, rawText.Length, rawText);

    private void WriteSearchDb(
        params RegionRow[] regions) =>
        WriteSearchDb(regions, revision: 7, schemaVersion: SearchIndexWriter.SchemaVersion, symbols: null);

    private void WriteSearchDb(
        RegionRow region,
        long revision = 7,
        int schemaVersion = SearchIndexWriter.SchemaVersion) =>
        WriteSearchDb(new[] { region }, revision, schemaVersion, symbols: null);

    private void WriteSearchDb(
        RegionRow[] regions,
        long revision = 7,
        int schemaVersion = SearchIndexWriter.SchemaVersion,
        IReadOnlyList<(string Id, string Name)>? symbols = null)
    {
        using var connection = OpenWriteCreate();
        Execute(connection, """
            CREATE VIRTUAL TABLE regions_fts USING fts5(
                region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
            CREATE TABLE search_regions(
                region_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                containing_symbol_id TEXT,
                start_line INTEGER NOT NULL,
                end_line INTEGER NOT NULL,
                start_byte INTEGER NOT NULL,
                end_byte INTEGER NOT NULL,
                raw_text TEXT NOT NULL,
                doc_len INTEGER NOT NULL);
            CREATE INDEX ix_search_regions_kind ON search_regions(kind);
            CREATE TABLE meta(
                revision INTEGER,
                doc_count INTEGER,
                avgdl REAL,
                schema_version INTEGER,
                region_count INTEGER,
                region_avgdl REAL);
            """);

        if (symbols is not null)
        {
            Execute(connection, "CREATE TABLE search_symbols(symbol_id TEXT PRIMARY KEY, name TEXT);");
            using var symbolCommand = connection.CreateCommand();
            symbolCommand.CommandText = "INSERT INTO search_symbols(symbol_id, name) VALUES ($id, $name);";
            var pId = symbolCommand.Parameters.Add("$id", SqliteType.Text);
            var pName = symbolCommand.Parameters.Add("$name", SqliteType.Text);
            foreach ((string id, string name) in symbols)
            {
                pId.Value = id;
                pName.Value = name;
                symbolCommand.ExecuteNonQuery();
            }
        }

        using var regionCommand = connection.CreateCommand();
        regionCommand.CommandText = """
            INSERT INTO search_regions
                (region_id, kind, path, language, containing_symbol_id, start_line, end_line,
                 start_byte, end_byte, raw_text, doc_len)
            VALUES ($id, $kind, $path, $language, $symbol, $startLine, $endLine,
                    $startByte, $endByte, $rawText, $docLen);
            """;
        var pRegionId = regionCommand.Parameters.Add("$id", SqliteType.Text);
        var pKind = regionCommand.Parameters.Add("$kind", SqliteType.Text);
        var pPath = regionCommand.Parameters.Add("$path", SqliteType.Text);
        var pLanguage = regionCommand.Parameters.Add("$language", SqliteType.Text);
        var pSymbol = regionCommand.Parameters.Add("$symbol", SqliteType.Text);
        var pStartLine = regionCommand.Parameters.Add("$startLine", SqliteType.Integer);
        var pEndLine = regionCommand.Parameters.Add("$endLine", SqliteType.Integer);
        var pStartByte = regionCommand.Parameters.Add("$startByte", SqliteType.Integer);
        var pEndByte = regionCommand.Parameters.Add("$endByte", SqliteType.Integer);
        var pRawText = regionCommand.Parameters.Add("$rawText", SqliteType.Text);
        var pDocLen = regionCommand.Parameters.Add("$docLen", SqliteType.Integer);

        using var ftsCommand = connection.CreateCommand();
        ftsCommand.CommandText = "INSERT INTO regions_fts(region_id, body) VALUES ($id, $body);";
        var pFtsId = ftsCommand.Parameters.Add("$id", SqliteType.Text);
        var pBody = ftsCommand.Parameters.Add("$body", SqliteType.Text);

        long totalLength = 0;
        var tokens = new List<string>(32);
        foreach (RegionRow region in regions)
        {
            tokens.Clear();
            CodeTokenizer.Tokenize(region.RawText, tokens);
            int docLen = tokens.Count;
            totalLength += docLen;

            pRegionId.Value = region.Id;
            pKind.Value = region.Kind;
            pPath.Value = region.Path;
            pLanguage.Value = region.Language;
            pSymbol.Value = (object?)region.ContainingSymbolId ?? DBNull.Value;
            pStartLine.Value = region.StartLine;
            pEndLine.Value = region.EndLine;
            pStartByte.Value = region.StartByte;
            pEndByte.Value = region.EndByte;
            pRawText.Value = region.RawText;
            pDocLen.Value = docLen;
            regionCommand.ExecuteNonQuery();

            pFtsId.Value = region.Id;
            pBody.Value = string.Join(' ', tokens);
            ftsCommand.ExecuteNonQuery();
        }

        double avgdl = regions.Length == 0 ? 0.0 : (double)totalLength / regions.Length;
        using var metaCommand = connection.CreateCommand();
        metaCommand.CommandText = """
            INSERT INTO meta(revision, doc_count, avgdl, schema_version, region_count, region_avgdl)
            VALUES ($revision, 0, 0.0, $schemaVersion, $regionCount, $regionAvgdl);
            """;
        metaCommand.Parameters.AddWithValue("$revision", revision);
        metaCommand.Parameters.AddWithValue("$schemaVersion", schemaVersion);
        metaCommand.Parameters.AddWithValue("$regionCount", regions.Length);
        metaCommand.Parameters.AddWithValue("$regionAvgdl", avgdl);
        metaCommand.ExecuteNonQuery();
    }

    private SqliteConnection OpenWriteCreate()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record RegionRow(
        string Id,
        string Kind,
        string Path,
        string Language,
        string? ContainingSymbolId,
        int StartLine,
        int EndLine,
        int StartByte,
        int EndByte,
        string RawText);
}
