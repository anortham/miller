using Microsoft.Data.Sqlite;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the on-disk <c>search.db</c> artifact <see cref="SearchIndexWriter"/> produces: the
/// <c>search_symbols</c> metadata table, the <c>meta</c> stats row, the word <c>symbols_fts</c> arm
/// (exact CodeTokenizer token stream incl. duplicates), and the collapsed <c>symbols_trigram</c> arm
/// (interior + boundary-crossing substring recall). These are the contract Eros and the reader depend on.
/// See docs/plans/2026-04-symbol-search-collapsed-trigram-design.md.
/// </summary>
public sealed class SearchIndexWriterTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SearchIndexWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-searchdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "search.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexedSymbol Sym(
        int docId, string id, string name, string? sig, string kind, string lang,
        string path, int startLine, int endLine, bool isTest = false)
        => new(docId, id, name, sig, kind, lang, path, startLine, endLine, ParentId: null, IsTest: isTest);

    private SqliteConnection OpenRead()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static long Long(object? o) => Convert.ToInt64(o);

    private static int TokenCount(string text)
    {
        var t = new List<string>();
        CodeTokenizer.Tokenize(text, t);
        return t.Count;
    }

    [Fact]
    public void Write_CreatesOneSearchSymbolsRowPerSymbol_WithMetadata()
    {
        var syms = new[]
        {
            Sym(0, "id0", "IAuthenticationProvider", "interface IAuthenticationProvider", "interface", "csharp", "src/Auth.cs", 10, 40),
            Sym(1, "id1", "format_external_extract", null, "function", "python", "src/fmt.py", 5, 9, isTest: true),
        };

        SearchIndexWriter.Write(_dbPath, syms, revision: 7);

        using var c = OpenRead();
        Assert.Equal(2L, Long(Scalar(c, "SELECT COUNT(*) FROM search_symbols")));
        Assert.Equal("interface", Scalar(c, "SELECT kind FROM search_symbols WHERE symbol_id=$i", ("$i", "id0")));
        Assert.Equal("csharp", Scalar(c, "SELECT language FROM search_symbols WHERE symbol_id=$i", ("$i", "id0")));
        Assert.Equal("src/fmt.py", Scalar(c, "SELECT path FROM search_symbols WHERE symbol_id=$i", ("$i", "id1")));
        Assert.Equal(40L, Long(Scalar(c, "SELECT end_line FROM search_symbols WHERE symbol_id=$i", ("$i", "id0"))));
        Assert.Equal(1L, Long(Scalar(c, "SELECT is_test FROM search_symbols WHERE symbol_id=$i", ("$i", "id1"))));
    }

    [Fact]
    public void Write_StoresSignature_AndPreservesNull()
    {
        // The artifact must be self-contained enough to render full results without julie's symbols.db:
        // search_symbols carries the raw signature (Resolve returns the full symbol). NULL signature stays NULL.
        var syms = new[]
        {
            Sym(0, "id0", "IAuthenticationProvider", "interface IAuthenticationProvider", "interface", "csharp", "a.cs", 1, 2),
            Sym(1, "id1", "format_external_extract", null, "function", "python", "b.py", 1, 2),
        };
        SearchIndexWriter.Write(_dbPath, syms, 1);

        using var c = OpenRead();
        Assert.Equal("interface IAuthenticationProvider",
            Scalar(c, "SELECT signature FROM search_symbols WHERE symbol_id=$i", ("$i", "id0")));
        Assert.Equal(DBNull.Value, Scalar(c, "SELECT signature FROM search_symbols WHERE symbol_id=$i", ("$i", "id1")));
    }

    [Fact]
    public void Write_DocLen_CountsFullTokenStreamInclDuplicates()
    {
        var syms = new[] { Sym(0, "id0", "IAuthenticationProvider", "interface IAuthenticationProvider", "interface", "csharp", "a.cs", 1, 2) };
        SearchIndexWriter.Write(_dbPath, syms, 1);

        int expected = TokenCount("IAuthenticationProvider interface IAuthenticationProvider");
        using var c = OpenRead();
        Assert.Equal((long)expected, Long(Scalar(c, "SELECT doc_len FROM search_symbols WHERE symbol_id=$i", ("$i", "id0"))));
    }

    [Fact]
    public void Write_Meta_HasDocCountRevisionAndAvgdl()
    {
        var syms = new[]
        {
            Sym(0, "id0", "Alpha", null, "class", "csharp", "a.cs", 1, 2),
            Sym(1, "id1", "BetaGamma", null, "class", "csharp", "b.cs", 1, 2),
        };
        SearchIndexWriter.Write(_dbPath, syms, revision: 99);

        double expectedAvg = (TokenCount("Alpha") + TokenCount("BetaGamma")) / 2.0;
        using var c = OpenRead();
        Assert.Equal(2L, Long(Scalar(c, "SELECT doc_count FROM meta")));
        Assert.Equal(99L, Long(Scalar(c, "SELECT revision FROM meta")));
        Assert.Equal(expectedAvg, Convert.ToDouble(Scalar(c, "SELECT avgdl FROM meta")), 5);
    }

    [Fact]
    public void Write_WordFts_MatchesComponentToken()
    {
        var syms = new[] { Sym(0, "id0", "IAuthenticationProvider", null, "interface", "csharp", "a.cs", 1, 2) };
        SearchIndexWriter.Write(_dbPath, syms, 1);

        using var c = OpenRead();
        // camelCase component tokens the word tokenizer emits are matchable
        Assert.Equal("id0", Scalar(c, "SELECT symbol_id FROM symbols_fts WHERE body MATCH $q", ("$q", "authentication")));
        Assert.Equal("id0", Scalar(c, "SELECT symbol_id FROM symbols_fts WHERE body MATCH $q", ("$q", "provider")));
    }

    [Fact]
    public void Write_WordFts_KeepsDiacriticsDistinct_ForRankingParity()
    {
        // The word arm's per-term document frequency is COUNT(*) over `body MATCH "term"`, and that DF feeds
        // BM25.Idf. The in-memory index keys postings by Ordinal token equality, so 'café' and 'cafe' are
        // DISTINCT terms. FTS5's DEFAULT unicode61 tokenizer folds diacritics ('café' → 'cafe'), which would
        // make MATCH "cafe" also hit the accented row — inflating DF and drifting the score off the in-memory
        // index. The writer must build symbols_fts with remove_diacritics 0 so the two forms stay distinct and
        // the DF (hence the ranking) matches. (Recall would stay exact regardless — the reader re-tokenizes in
        // C# and drops the false positive — but the SCORE would drift, breaking word-arm parity.)
        var syms = new[]
        {
            Sym(0, "accented", "Café", null, "class", "csharp", "a.cs", 1, 2),
            Sym(1, "plain", "Cafe", null, "class", "csharp", "b.cs", 1, 2),
        };
        SearchIndexWriter.Write(_dbPath, syms, 1);

        using var c = OpenRead();
        Assert.Equal(1L, Long(Scalar(c, "SELECT COUNT(*) FROM symbols_fts WHERE body MATCH $q", ("$q", "cafe"))));
        Assert.Equal("plain", Scalar(c, "SELECT symbol_id FROM symbols_fts WHERE body MATCH $q", ("$q", "cafe")));
    }

    [Fact]
    public void Write_Trigram_MatchesInteriorAndBoundaryCrossingSubstring()
    {
        var syms = new[]
        {
            Sym(0, "id0", "IAuthenticationProvider", null, "interface", "csharp", "a.cs", 1, 2),
            Sym(1, "id1", "format_external_extract", null, "function", "python", "b.py", 1, 2),
        };
        SearchIndexWriter.Write(_dbPath, syms, 1);

        using var c = OpenRead();
        // interior substring of a camelCase component — unreachable by word/token search
        Assert.Equal("id0", Scalar(c, "SELECT symbol_id FROM symbols_trigram WHERE name_collapsed MATCH $q", ("$q", "thenti")));
        // boundary-crossing substring in a snake_case name — the collapsed form makes it contiguous
        Assert.Equal("id1", Scalar(c, "SELECT symbol_id FROM symbols_trigram WHERE name_collapsed MATCH $q", ("$q", "matexter")));
    }

    [Fact]
    public void Write_OverwritesExistingDb_ReplacesRowsAndRevision()
    {
        SearchIndexWriter.Write(_dbPath, new[]
        {
            Sym(0, "id0", "Alpha", null, "class", "csharp", "a.cs", 1, 2),
            Sym(1, "id1", "Beta", null, "class", "csharp", "b.cs", 1, 2),
        }, revision: 1);

        SearchIndexWriter.Write(_dbPath, new[] { Sym(0, "only", "Gamma", null, "class", "csharp", "c.cs", 1, 2) }, revision: 2);

        using var c = OpenRead();
        Assert.Equal(1L, Long(Scalar(c, "SELECT COUNT(*) FROM search_symbols")));
        Assert.Equal("only", Scalar(c, "SELECT symbol_id FROM search_symbols"));
        Assert.Equal(2L, Long(Scalar(c, "SELECT revision FROM meta")));
    }

    [Fact]
    public void Write_EmptySymbols_ProducesValidEmptyIndex()
    {
        SearchIndexWriter.Write(_dbPath, Array.Empty<IndexedSymbol>(), revision: 3);

        using var c = OpenRead();
        Assert.Equal(0L, Long(Scalar(c, "SELECT COUNT(*) FROM search_symbols")));
        Assert.Equal(0L, Long(Scalar(c, "SELECT doc_count FROM meta")));
        Assert.Equal(3L, Long(Scalar(c, "SELECT revision FROM meta")));
    }
}
