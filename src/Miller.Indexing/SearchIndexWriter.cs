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
    private const int ParameterChunkSize = 500;

    /// <summary>
    /// The on-disk schema version stamped into <c>meta.schema_version</c>. Bumped 4→5 so artifacts that
    /// derive symbol identity from SQLite row order are rejected and rebuilt with explicit <c>doc_id</c>.
    /// </summary>
    public const int SchemaVersion = 5;

    private const string SchemaDdl = """
        CREATE VIRTUAL TABLE symbols_fts USING fts5(
            symbol_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
        CREATE VIRTUAL TABLE symbols_trigram USING fts5(
            symbol_id UNINDEXED, name_collapsed, qual_collapsed, tokenize='trigram');
        CREATE TABLE search_symbols(
            symbol_id        TEXT PRIMARY KEY,
            doc_id INTEGER NOT NULL UNIQUE,
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
            // next successful convergence repairs the derived artifact).
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

    /// <summary>
    /// Bring an existing current-schema <c>search.db</c> forward by replacing only rows for the supplied
    /// workspace-relative <paramref name="paths"/> and stamping <paramref name="revision"/> into <c>meta</c>.
    /// The caller must hold Miller's single-writer lock and must call this only after the extract DB has already
    /// advanced to <paramref name="revision"/>.
    /// </summary>
    public static void ApplyFileChanges(
        string searchDbPath,
        string symbolsDbPath,
        IReadOnlyCollection<string> paths,
        long revision,
        string? workspaceRoot,
        RegionIndexOptions regionOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolsDbPath);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(regionOptions);
        if (regionOptions.Enabled)
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var distinctPaths = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = searchDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var tx = connection.BeginTransaction();

        IReadOnlyList<OldSymbolIdentity> oldSymbols = distinctPaths.Length == 0
            ? Array.Empty<OldSymbolIdentity>()
            : ReadOldSymbolsForPaths(connection, distinctPaths);

        if (distinctPaths.Length > 0)
        {
            DeleteSymbolsForPaths(connection, distinctPaths);
            if (regionOptions.Enabled)
                DeleteRegionsForPaths(connection, distinctPaths);

            IReadOnlyList<IndexedSymbol> currentSymbols = SqliteSymbolReader.ReadForPaths(symbolsDbPath, distinctPaths);
            IReadOnlyList<IndexedSymbol> stableSymbols = AssignStableDocIds(connection, currentSymbols, oldSymbols);
            Dictionary<string, IndexedSymbol> symbolsById =
                BuildQualificationSymbolMap(connection, stableSymbols);
            InsertSymbols(connection, stableSymbols, symbolsById);

            if (regionOptions.Enabled)
            {
                var changedPathSet = distinctPaths.ToHashSet(StringComparer.Ordinal);
                InsertRegions(connection, stableSymbols, symbolsDbPath, workspaceRoot!, regionOptions, changedPathSet);
            }
        }

        RewriteMeta(connection, revision);
        tx.Commit();
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

        var symbolsById = symbols.ToDictionary(static s => s.SymbolId, StringComparer.Ordinal);
        long totalLen = InsertSymbols(connection, symbols, symbolsById);

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

    private static long InsertSymbols(
        SqliteConnection connection,
        IReadOnlyList<IndexedSymbol> symbols,
        IReadOnlyDictionary<string, IndexedSymbol> symbolsById)
    {
        using var symCmd = connection.CreateCommand();
        symCmd.CommandText = """
            INSERT INTO search_symbols
                (symbol_id, doc_id, name, signature, kind, language, path, start_line, end_line,
                 start_byte, end_byte, parent_symbol_id, is_test, doc_len)
            VALUES ($id, $doc, $name, $sig, $kind, $lang, $path, $sl, $el, $sb, $eb, $pid, $test, $dl);
            """;
        var pId = symCmd.Parameters.Add("$id", SqliteType.Text);
        var pDoc = symCmd.Parameters.Add("$doc", SqliteType.Integer);
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
            pDoc.Value = s.DocId;
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

        return totalLen;
    }

    private static IReadOnlyList<OldSymbolIdentity> ReadOldSymbolsForPaths(
        SqliteConnection connection,
        IReadOnlyList<string> paths)
    {
        var oldSymbols = new List<OldSymbolIdentity>();
        foreach ((int offset, int count) in Chunks(paths.Count))
        {
            using var cmd = connection.CreateCommand();
            string placeholders = AddPathParameters(cmd, paths, offset, count);
            cmd.CommandText = $"""
                SELECT symbol_id, doc_id
                FROM search_symbols
                WHERE path IN ({placeholders})
                ORDER BY doc_id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                oldSymbols.Add(new OldSymbolIdentity(reader.GetString(0), reader.GetInt32(1)));
        }
        return oldSymbols;
    }

    private static void DeleteSymbolsForPaths(SqliteConnection connection, IReadOnlyList<string> paths)
    {
        foreach ((int offset, int count) in Chunks(paths.Count))
        {
            DeleteByPathChunk(connection, "symbols_fts", "symbol_id", "search_symbols", paths, offset, count);
            DeleteByPathChunk(connection, "symbols_trigram", "symbol_id", "search_symbols", paths, offset, count);
            using var cmd = connection.CreateCommand();
            string placeholders = AddPathParameters(cmd, paths, offset, count);
            cmd.CommandText = $"DELETE FROM search_symbols WHERE path IN ({placeholders});";
            cmd.ExecuteNonQuery();
        }
    }

    private static void DeleteRegionsForPaths(SqliteConnection connection, IReadOnlyList<string> paths)
    {
        foreach ((int offset, int count) in Chunks(paths.Count))
        {
            DeleteByPathChunk(connection, "regions_fts", "region_id", "search_regions", paths, offset, count);
            using var cmd = connection.CreateCommand();
            string placeholders = AddPathParameters(cmd, paths, offset, count);
            cmd.CommandText = $"DELETE FROM search_regions WHERE path IN ({placeholders});";
            cmd.ExecuteNonQuery();
        }
    }

    private static void DeleteByPathChunk(
        SqliteConnection connection,
        string targetTable,
        string idColumn,
        string metadataTable,
        IReadOnlyList<string> paths,
        int offset,
        int count)
    {
        using var cmd = connection.CreateCommand();
        string placeholders = AddPathParameters(cmd, paths, offset, count);
        cmd.CommandText = $"""
            DELETE FROM {targetTable}
            WHERE {idColumn} IN (
                SELECT {idColumn}
                FROM {metadataTable}
                WHERE path IN ({placeholders})
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<IndexedSymbol> AssignStableDocIds(
        SqliteConnection connection,
        IReadOnlyList<IndexedSymbol> currentSymbols,
        IReadOnlyList<OldSymbolIdentity> oldSymbols)
    {
        if (currentSymbols.Count == 0)
            return Array.Empty<IndexedSymbol>();

        var oldBySymbolId = oldSymbols.ToDictionary(static s => s.SymbolId, static s => s.DocId, StringComparer.Ordinal);
        var currentSymbolIds = currentSymbols.Select(static s => s.SymbolId).ToHashSet(StringComparer.Ordinal);
        var reusableDocIds = new SortedSet<int>(
            oldSymbols
                .Where(s => !currentSymbolIds.Contains(s.SymbolId))
                .Select(static s => s.DocId));
        int maxResidentDocId = ReadMaxDocId(connection);
        int maxOldDocId = oldSymbols.Count == 0 ? -1 : oldSymbols.Max(static s => s.DocId);
        int nextDocId = checked(Math.Max(maxResidentDocId, maxOldDocId) + 1);
        var assigned = new List<IndexedSymbol>(currentSymbols.Count);

        foreach (IndexedSymbol symbol in currentSymbols)
        {
            int docId;
            if (oldBySymbolId.TryGetValue(symbol.SymbolId, out int oldDocId))
            {
                reusableDocIds.Remove(oldDocId);
                docId = oldDocId;
            }
            else if (reusableDocIds.Count > 0)
            {
                docId = reusableDocIds.Min;
                reusableDocIds.Remove(docId);
            }
            else
            {
                docId = checked(nextDocId++);
            }

            assigned.Add(symbol with { DocId = docId });
        }

        return assigned;
    }

    private static int ReadMaxDocId(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(doc_id) FROM search_symbols;";
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? -1 : Convert.ToInt32(result);
    }

    private static Dictionary<string, IndexedSymbol> BuildQualificationSymbolMap(
        SqliteConnection connection,
        IReadOnlyList<IndexedSymbol> changedSymbols)
    {
        var symbolsById = changedSymbols.ToDictionary(static s => s.SymbolId, StringComparer.Ordinal);
        var pendingParents = new Queue<string>();
        foreach (IndexedSymbol symbol in changedSymbols)
            if (symbol.ParentId is { Length: > 0 } parentId)
                pendingParents.Enqueue(parentId);

        var seenParentIds = new HashSet<string>(StringComparer.Ordinal);
        while (pendingParents.TryDequeue(out string? parentId))
        {
            if (!seenParentIds.Add(parentId) || symbolsById.ContainsKey(parentId))
                continue;

            IndexedSymbol? parent = ReadSearchSymbolForQualification(connection, parentId);
            if (parent is null)
                continue;

            symbolsById[parent.SymbolId] = parent;
            if (parent.ParentId is { Length: > 0 } grandParentId)
                pendingParents.Enqueue(grandParentId);
        }

        return symbolsById;
    }

    private static IndexedSymbol? ReadSearchSymbolForQualification(SqliteConnection connection, string symbolId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT doc_id, symbol_id, name, signature, kind, language, path,
                   start_line, end_line, parent_symbol_id, is_test
            FROM search_symbols
            WHERE symbol_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", symbolId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new IndexedSymbol(
            DocId: reader.GetInt32(0),
            SymbolId: reader.GetString(1),
            Name: reader.GetString(2),
            Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
            Kind: reader.GetString(4),
            Language: reader.GetString(5),
            FilePath: reader.GetString(6),
            StartLine: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            EndLine: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            ParentId: reader.IsDBNull(9) ? null : reader.GetString(9),
            IsTest: !reader.IsDBNull(10) && reader.GetInt64(10) != 0);
    }

    private static void RewriteMeta(SqliteConnection connection, long revision)
    {
        (long docCount, double avgdl) = ReadStats(connection, "search_symbols");
        (long regionCount, double regionAvgdl) = ReadStats(connection, "search_regions");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM meta;
            INSERT INTO meta(revision, doc_count, avgdl, schema_version, region_count, region_avgdl)
            VALUES ($rev, $docs, $avg, $ver, $regions, $ravg);
            """;
        cmd.Parameters.AddWithValue("$rev", revision);
        cmd.Parameters.AddWithValue("$docs", docCount);
        cmd.Parameters.AddWithValue("$avg", avgdl);
        cmd.Parameters.AddWithValue("$ver", SchemaVersion);
        cmd.Parameters.AddWithValue("$regions", regionCount);
        cmd.Parameters.AddWithValue("$ravg", regionAvgdl);
        cmd.ExecuteNonQuery();
    }

    private static (long Count, double Avgdl) ReadStats(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(doc_len), 0) FROM {table};";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (0, 0.0);

        long count = reader.GetInt64(0);
        long totalLen = reader.GetInt64(1);
        return (count, count == 0 ? 0.0 : (double)totalLen / count);
    }

    private static IEnumerable<(int Offset, int Count)> Chunks(int total)
    {
        for (int offset = 0; offset < total; offset += ParameterChunkSize)
            yield return (offset, Math.Min(ParameterChunkSize, total - offset));
    }

    private static string AddPathParameters(SqliteCommand command, IReadOnlyList<string> paths, int offset, int count)
    {
        var placeholders = new string[count];
        for (int i = 0; i < count; i++)
        {
            string name = "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, paths[offset + i]);
        }
        return string.Join(", ", placeholders);
    }

    private readonly record struct OldSymbolIdentity(string SymbolId, int DocId);

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
        RegionIndexOptions options,
        IReadOnlySet<string>? pathFilter = null)
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
            if (pathFilter is not null && !pathFilter.Contains(region.Path))
                continue;
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
