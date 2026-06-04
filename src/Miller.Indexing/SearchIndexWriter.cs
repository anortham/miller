using Microsoft.Data.Sqlite;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

/// <summary>
/// Builds the Miller-owned, on-disk search artifact <c>&lt;workspace&gt;/.miller/search.db</c> from a set of
/// <see cref="IndexedSymbol"/>s. The artifact is a STABLE schema contract (Eros and the reader depend on it),
/// with FTS5 as the internal recall engine; ranking stays in C# (the reader re-scores candidates with the
/// in-memory <c>MillerSearchIndex</c> math, using the corpus stats this writer stamps into <c>meta</c>).
///
/// Tables (see docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md):
/// <list type="bullet">
/// <item><c>symbols_fts(symbol_id UNINDEXED, body)</c> — the word arm; <c>body</c> is the EXACT
/// <see cref="CodeTokenizer"/> token stream (full words + components + duplicates), space-joined, so a
/// future C# re-rank reproduces today's recall and BM25 length/TF math.</item>
/// <item><c>symbols_trigram(symbol_id UNINDEXED, name_collapsed, qual_collapsed, tokenize='trigram')</c> —
/// the interior-substring arm over the separator-free <see cref="CollapseName"/> form.</item>
/// <item><c>search_symbols(...)</c> — self-contained metadata: candidate filtering, Eros queries, AST chunk
/// boundaries. <c>doc_len</c> is the word token count (BM25 length norm).</item>
/// <item><c>meta(revision, doc_count, avgdl, schema_version)</c> — freshness key + corpus BM25 constants.</item>
/// </list>
///
/// Build discipline: write a sibling temp DB, then atomically <see cref="File.Move(string,string,bool)"/> it
/// over the live file so a concurrent reader never sees a half-built artifact. The caller is responsible for
/// holding the workspace <c>SingleWriterLock</c> (this is invoked from the leader's index/refresh path).
/// </summary>
public static class SearchIndexWriter
{
    /// <summary>
    /// The on-disk schema version stamped into <c>meta.schema_version</c>. Bumped 1→2 with the word arm's
    /// <c>remove_diacritics 0</c> tokenizer change: the MATCH semantics live in the table definition, so a
    /// stale revision-matching artifact built by the old (diacritic-folding) writer must be rejected by
    /// <see cref="FtsSymbolSearchIndex.Open"/> on the version mismatch and rebuilt, not silently re-read.
    /// </summary>
    public const int SchemaVersion = 2;

    private const string SchemaDdl = """
        CREATE VIRTUAL TABLE symbols_fts USING fts5(
            symbol_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
        CREATE VIRTUAL TABLE symbols_trigram USING fts5(
            symbol_id UNINDEXED, name_collapsed, qual_collapsed, tokenize='trigram');
        CREATE TABLE search_symbols(
            symbol_id        TEXT PRIMARY KEY,
            name TEXT, signature TEXT, kind TEXT, language TEXT,
            path TEXT,
            start_line INTEGER, end_line INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            parent_symbol_id TEXT, is_test INTEGER,
            doc_len INTEGER);
        CREATE INDEX ix_search_symbols_kind ON search_symbols(kind);
        CREATE INDEX ix_search_symbols_lang ON search_symbols(language);
        CREATE TABLE meta(revision INTEGER, doc_count INTEGER, avgdl REAL, schema_version INTEGER);
        """;

    /// <summary>
    /// Build a fresh <c>search.db</c> at <paramref name="searchDbPath"/> from <paramref name="symbols"/>,
    /// stamping <paramref name="revision"/> into <c>meta</c>, replacing any existing file atomically.
    /// </summary>
    public static void Write(string searchDbPath, IReadOnlyList<IndexedSymbol> symbols, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchDbPath);
        ArgumentNullException.ThrowIfNull(symbols);

        string fullPath = Path.GetFullPath(searchDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {searchDbPath}", nameof(searchDbPath));
        string tempPath = Path.Combine(dir, $".search-build-{Guid.NewGuid():N}.db");

        try
        {
            BuildInto(tempPath, symbols, revision);
            // Release the build connection's file handle from the pool before the move (Windows can't
            // replace/rename a file with an open handle).
            SqliteConnection.ClearAllPools();
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(tempPath); } catch (IOException) { /* leftover temp; best effort */ }
            }
        }
    }

    private static void BuildInto(string tempPath, IReadOnlyList<IndexedSymbol> symbols, long revision)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tempPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // transient build connection; don't let the pool retain the handle past the move
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            // Bulk build into a single .db file: in-memory journal (no -wal/-shm sidecars to move) and
            // synchronous=OFF (a crash just orphans the temp, never the live artifact).
            pragma.CommandText = "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;";
            pragma.ExecuteNonQuery();
        }
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = SchemaDdl;
            ddl.ExecuteNonQuery();
        }

        using var tx = connection.BeginTransaction();

        using var symCmd = connection.CreateCommand();
        symCmd.CommandText = """
            INSERT INTO search_symbols
                (symbol_id, name, signature, kind, language, path, start_line, end_line,
                 start_byte, end_byte, parent_symbol_id, is_test, doc_len)
            VALUES ($id, $name, $sig, $kind, $lang, $path, $sl, $el, $sb, $eb, $pid, $test, $dl);
            """;
        var pId = symCmd.Parameters.Add("$id", SqliteType.Text);
        var pName = symCmd.Parameters.Add("$name", SqliteType.Text);
        var pSig = symCmd.Parameters.Add("$sig", SqliteType.Text);
        var pKind = symCmd.Parameters.Add("$kind", SqliteType.Text);
        var pLang = symCmd.Parameters.Add("$lang", SqliteType.Text);
        var pPath = symCmd.Parameters.Add("$path", SqliteType.Text);
        var pSl = symCmd.Parameters.Add("$sl", SqliteType.Integer);
        var pEl = symCmd.Parameters.Add("$el", SqliteType.Integer);
        var pSb = symCmd.Parameters.Add("$sb", SqliteType.Integer);
        var pEb = symCmd.Parameters.Add("$eb", SqliteType.Integer);
        var pPid = symCmd.Parameters.Add("$pid", SqliteType.Text);
        var pTest = symCmd.Parameters.Add("$test", SqliteType.Integer);
        var pDl = symCmd.Parameters.Add("$dl", SqliteType.Integer);

        using var ftsCmd = connection.CreateCommand();
        ftsCmd.CommandText = "INSERT INTO symbols_fts(symbol_id, body) VALUES ($id, $body);";
        var fId = ftsCmd.Parameters.Add("$id", SqliteType.Text);
        var fBody = ftsCmd.Parameters.Add("$body", SqliteType.Text);

        using var triCmd = connection.CreateCommand();
        triCmd.CommandText =
            "INSERT INTO symbols_trigram(symbol_id, name_collapsed, qual_collapsed) VALUES ($id, $nc, $qc);";
        var tId = triCmd.Parameters.Add("$id", SqliteType.Text);
        var tNc = triCmd.Parameters.Add("$nc", SqliteType.Text);
        var tQc = triCmd.Parameters.Add("$qc", SqliteType.Text);

        var tokens = new List<string>(16);
        long totalLen = 0;

        foreach (var s in symbols)
        {
            string text = string.IsNullOrEmpty(s.Signature) ? s.Name : s.Name + " " + s.Signature;
            tokens.Clear();
            CodeTokenizer.Tokenize(text, tokens);
            int docLen = tokens.Count;
            totalLen += docLen;

            pId.Value = s.SymbolId;
            pName.Value = s.Name;
            pSig.Value = (object?)s.Signature ?? DBNull.Value;
            pKind.Value = s.Kind;
            pLang.Value = s.Language;
            pPath.Value = s.FilePath;
            pSl.Value = s.StartLine;
            pEl.Value = s.EndLine;
            // Byte spans are not yet plumbed through SqliteSymbolReader/IndexedSymbol; the columns exist so
            // the artifact contract is stable, populated once byte spans land (prerequisite for Eros chunks).
            pSb.Value = DBNull.Value;
            pEb.Value = DBNull.Value;
            pPid.Value = (object?)s.ParentId ?? DBNull.Value;
            pTest.Value = s.IsTest ? 1 : 0;
            pDl.Value = docLen;
            symCmd.ExecuteNonQuery();

            fId.Value = s.SymbolId;
            fBody.Value = string.Join(' ', tokens);
            ftsCmd.ExecuteNonQuery();

            tId.Value = s.SymbolId;
            tNc.Value = CollapseName.Of(s.Name);
            // Qualified-name (parent-chain) collapse is a later phase; store empty for now (column reserved).
            tQc.Value = string.Empty;
            triCmd.ExecuteNonQuery();
        }

        double avgdl = symbols.Count == 0 ? 0.0 : (double)totalLen / symbols.Count;
        using (var metaCmd = connection.CreateCommand())
        {
            metaCmd.CommandText =
                "INSERT INTO meta(revision, doc_count, avgdl, schema_version) VALUES ($rev, $n, $avg, $ver);";
            metaCmd.Parameters.AddWithValue("$rev", revision);
            metaCmd.Parameters.AddWithValue("$n", symbols.Count);
            metaCmd.Parameters.AddWithValue("$avg", avgdl);
            metaCmd.Parameters.AddWithValue("$ver", SchemaVersion);
            metaCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
