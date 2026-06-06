using Microsoft.Data.Sqlite;
using Miller.Core.Tokenization;
using System.Text;

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
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The on-disk schema version stamped into <c>meta.schema_version</c>. Bumped 3→4 so artifacts built
    /// before <c>symbols_trigram.qual_collapsed</c> was populated are rejected and rebuilt instead of
    /// silently missing qualified-name substring recall.
    /// </summary>
    public const int SchemaVersion = 4;

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
        CREATE VIRTUAL TABLE regions_fts USING fts5(
            region_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
        CREATE TABLE search_regions(
            region_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            path TEXT NOT NULL,
            language TEXT NOT NULL,
            containing_symbol_id TEXT,
            containing_symbol_name TEXT,
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
        """;

    /// <summary>
    /// Build a fresh <c>search.db</c> at <paramref name="searchDbPath"/> from <paramref name="symbols"/>,
    /// stamping <paramref name="revision"/> into <c>meta</c>, replacing any existing file atomically.
    /// </summary>
    public static void Write(string searchDbPath, IReadOnlyList<IndexedSymbol> symbols, long revision)
        => Write(searchDbPath, symbols, revision, symbolsDbPath: null, workspaceRoot: null, RegionIndexOptions.Disabled);

    /// <summary>
    /// Build a fresh <c>search.db</c>, optionally populating the source-region tables from
    /// <paramref name="symbolsDbPath"/> by slicing verified file bytes under <paramref name="workspaceRoot"/>.
    /// </summary>
    public static void Write(
        string searchDbPath,
        IReadOnlyList<IndexedSymbol> symbols,
        long revision,
        string? symbolsDbPath,
        string? workspaceRoot,
        RegionIndexOptions regionOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchDbPath);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(regionOptions);
        if (regionOptions.Enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        }

        string fullPath = Path.GetFullPath(searchDbPath);
        string dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"Path has no directory: {searchDbPath}", nameof(searchDbPath));
        string tempPath = Path.Combine(dir, $".search-build-{Guid.NewGuid():N}.db");

        try
        {
            BuildInto(tempPath, symbols, revision, symbolsDbPath, workspaceRoot, regionOptions);
            // Release the build connection's file handle from the pool before the move (Windows can't
            // replace/rename a file with an open handle).
            SqliteConnection.ClearAllPools();
            // Windows can still transiently fail the overwrite-move if another miller briefly holds search.db open
            // read-only; retry a few times before surfacing the IOException (the finally cleans the temp, and the
            // sidecar self-heals to in-memory search until the next successful rebuild).
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(tempPath, fullPath, overwrite: true); break; }
                catch (IOException) when (attempt < 5) { System.Threading.Thread.Sleep(20 * attempt); }
            }
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

    private static void BuildInto(
        string tempPath,
        IReadOnlyList<IndexedSymbol> symbols,
        long revision,
        string? symbolsDbPath,
        string? workspaceRoot,
        RegionIndexOptions regionOptions)
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
        var symbolsById = symbols.ToDictionary(static s => s.SymbolId, StringComparer.Ordinal);
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
            tQc.Value = CollapseName.Of(QualifiedNameOf(s, symbolsById));
            triCmd.ExecuteNonQuery();
        }

        (int regionCount, double regionAvgdl) = regionOptions.Enabled
            ? InsertRegions(connection, symbols, symbolsDbPath!, workspaceRoot!, regionOptions)
            : (0, 0.0);

        double avgdl = symbols.Count == 0 ? 0.0 : (double)totalLen / symbols.Count;
        using (var metaCmd = connection.CreateCommand())
        {
            metaCmd.CommandText = """
                INSERT INTO meta(revision, doc_count, avgdl, schema_version, region_count, region_avgdl)
                VALUES ($rev, $n, $avg, $ver, $rn, $ravg);
                """;
            metaCmd.Parameters.AddWithValue("$rev", revision);
            metaCmd.Parameters.AddWithValue("$n", symbols.Count);
            metaCmd.Parameters.AddWithValue("$avg", avgdl);
            metaCmd.Parameters.AddWithValue("$ver", SchemaVersion);
            metaCmd.Parameters.AddWithValue("$rn", regionCount);
            metaCmd.Parameters.AddWithValue("$ravg", regionAvgdl);
            metaCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static string QualifiedNameOf(
        IndexedSymbol symbol,
        IReadOnlyDictionary<string, IndexedSymbol> symbolsById)
    {
        var parts = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IndexedSymbol? current = symbol;
        while (current is not null && seen.Add(current.SymbolId))
        {
            parts.Add(current.Name);
            current = current.ParentId is { Length: > 0 } parentId &&
                      symbolsById.TryGetValue(parentId, out IndexedSymbol? parent)
                ? parent
                : null;
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    private static (int RegionCount, double RegionAvgdl) InsertRegions(
        SqliteConnection connection,
        IReadOnlyList<IndexedSymbol> symbols,
        string symbolsDbPath,
        string workspaceRoot,
        RegionIndexOptions options)
    {
        IReadOnlyList<SourceRegionRow> regions = SqliteSourceRegionReader.ReadIndexedRegions(symbolsDbPath);
        if (regions.Count == 0)
            return (0, 0.0);

        var symbolNames = symbols.ToDictionary(static s => s.SymbolId, static s => s.Name, StringComparer.Ordinal);
        var fileCache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        using var regionCmd = connection.CreateCommand();
        regionCmd.CommandText = """
            INSERT INTO search_regions
                (region_id, kind, path, language, containing_symbol_id, containing_symbol_name,
                 start_line, end_line, start_byte, end_byte, raw_text, doc_len)
            VALUES ($id, $kind, $path, $lang, $sid, $sname, $sl, $el, $sb, $eb, $raw, $dl);
            """;
        var pId = regionCmd.Parameters.Add("$id", SqliteType.Text);
        var pKind = regionCmd.Parameters.Add("$kind", SqliteType.Text);
        var pPath = regionCmd.Parameters.Add("$path", SqliteType.Text);
        var pLang = regionCmd.Parameters.Add("$lang", SqliteType.Text);
        var pSid = regionCmd.Parameters.Add("$sid", SqliteType.Text);
        var pSName = regionCmd.Parameters.Add("$sname", SqliteType.Text);
        var pSl = regionCmd.Parameters.Add("$sl", SqliteType.Integer);
        var pEl = regionCmd.Parameters.Add("$el", SqliteType.Integer);
        var pSb = regionCmd.Parameters.Add("$sb", SqliteType.Integer);
        var pEb = regionCmd.Parameters.Add("$eb", SqliteType.Integer);
        var pRaw = regionCmd.Parameters.Add("$raw", SqliteType.Text);
        var pDl = regionCmd.Parameters.Add("$dl", SqliteType.Integer);

        using var ftsCmd = connection.CreateCommand();
        ftsCmd.CommandText = "INSERT INTO regions_fts(region_id, body) VALUES ($id, $body);";
        var fId = ftsCmd.Parameters.Add("$id", SqliteType.Text);
        var fBody = ftsCmd.Parameters.Add("$body", SqliteType.Text);

        var tokens = new List<string>(64);
        long totalLen = 0;
        int inserted = 0;

        foreach (SourceRegionRow region in regions)
        {
            if (!string.Equals(region.Status, "indexed", StringComparison.Ordinal))
                continue;
            int regionBytes = region.EndByte - region.StartByte;
            if (region.StartByte < 0 || region.EndByte <= region.StartByte || regionBytes > options.MaxRegionBytes)
                continue;

            byte[]? fileBytes = ReadVerifiedFileBytes(workspaceRoot, region, fileCache);
            if (fileBytes is null || region.EndByte > fileBytes.Length)
                continue;

            string rawText;
            try
            {
                rawText = StrictUtf8.GetString(fileBytes, region.StartByte, regionBytes);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            tokens.Clear();
            CodeTokenizer.Tokenize(rawText, tokens);
            int docLen = tokens.Count;
            totalLen += docLen;

            pId.Value = region.SourceRegionId;
            pKind.Value = region.Kind;
            pPath.Value = region.Path;
            pLang.Value = region.Language;
            pSid.Value = (object?)region.ContainingSymbolId ?? DBNull.Value;
            pSName.Value = region.ContainingSymbolId is not null
                && symbolNames.TryGetValue(region.ContainingSymbolId, out string? name)
                    ? name
                    : DBNull.Value;
            pSl.Value = region.StartLine;
            pEl.Value = region.EndLine;
            pSb.Value = region.StartByte;
            pEb.Value = region.EndByte;
            pRaw.Value = rawText;
            pDl.Value = docLen;
            regionCmd.ExecuteNonQuery();

            fId.Value = region.SourceRegionId;
            fBody.Value = string.Join(' ', tokens);
            ftsCmd.ExecuteNonQuery();

            inserted++;
        }

        double avgdl = inserted == 0 ? 0.0 : (double)totalLen / inserted;
        return (inserted, avgdl);
    }

    private static byte[]? ReadVerifiedFileBytes(
        string workspaceRoot,
        SourceRegionRow region,
        Dictionary<string, byte[]?> fileCache)
    {
        if (fileCache.TryGetValue(region.Path, out byte[]? cached))
            return cached;

        byte[]? result = null;
        try
        {
            string? abs = WorkspaceRelativePath.ResolveUnderRoot(workspaceRoot, region.Path);
            if (abs is not null && File.Exists(abs))
            {
                byte[] bytes = File.ReadAllBytes(abs);
                if (bytes.LongLength == region.ContentBytes &&
                    StringComparer.OrdinalIgnoreCase.Equals(
                        ContentHasher.Blake3Hex(bytes),
                        ContentHasher.NormalizeHash(region.ContentHash)))
                {
                    result = bytes;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result = null;
        }

        fileCache[region.Path] = result;
        return result;
    }
}
