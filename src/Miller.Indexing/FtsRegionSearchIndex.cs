using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

/// <summary>
/// Read-only source-region search over the Miller-owned <c>search.db</c> sidecar.
/// </summary>
public sealed class FtsRegionSearchIndex : IRegionSearchIndex
{
    private const int SnippetMaxChars = 240;

    private static readonly HashSet<string> TestSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "spec", "specs", "testdata", "fixtures",
    };

    private static readonly string[] FileNameInfixes = { ".test.", ".spec.", ".tests." };

    private static readonly string[] PascalSuffixes = { "Test", "Tests", "Spec", "Specs" };

    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, RegionDocument> _regionsById;
    private readonly int _regionCount;
    private readonly double _regionAvgdl;

    private FtsRegionSearchIndex(
        string connectionString,
        IReadOnlyList<RegionDocument> regions,
        int regionCount,
        double regionAvgdl,
        long revision)
    {
        _connectionString = connectionString;
        _regionsById = regions.ToDictionary(static r => r.RegionId, StringComparer.Ordinal);
        _regionCount = regionCount;
        _regionAvgdl = regionAvgdl;
        Revision = revision;
    }

    public int DocumentCount => _regionsById.Count;

    public long Revision { get; }

    public static FtsRegionSearchIndex Open(string searchDbPath, long expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchDbPath);

        string absPath = Path.GetFullPath(searchDbPath);
        if (!File.Exists(absPath))
            throw new FileNotFoundException($"search.db not found at '{absPath}'. Rebuild the search index.", absPath);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        RegionMeta meta = ReadMeta(connection, absPath);
        if (meta.SchemaVersion != SearchIndexWriter.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"search.db at '{absPath}' has schema_version {meta.SchemaVersion}; " +
                $"this build expects {SearchIndexWriter.SchemaVersion}. Rebuild the search index.");
        }
        if (meta.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"search.db at '{absPath}' is stale: revision {meta.Revision}, expected {expectedRevision}. " +
                "Refresh or rebuild the search index.");
        }

        EnsureRegionSchema(connection, absPath);
        IReadOnlyDictionary<string, string> symbolNames = ReadSymbolNamesIfAvailable(connection);
        IReadOnlyList<RegionDocument> regions = ReadRegions(connection, symbolNames, absPath);

        return new FtsRegionSearchIndex(
            connectionString,
            regions,
            meta.RegionCount,
            meta.RegionAvgdl,
            meta.Revision);
    }

    public IReadOnlyList<RegionSearchHit> Search(
        string query,
        IReadOnlySet<string> kinds,
        int limit = 10,
        bool excludeTests = false)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _regionsById.Count == 0 || kinds.Count == 0)
            return Array.Empty<RegionSearchHit>();

        var queryTokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, queryTokens);
        if (queryTokens.Count == 0)
            return Array.Empty<RegionSearchHit>();

        var distinctTerms = new List<string>(queryTokens.Count);
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in queryTokens)
            if (seenTerms.Add(token))
                distinctTerms.Add(token);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string term in distinctTerms)
            documentFrequency[term] = CountRegionsMatching(connection, QuoteFts(term));

        string match = string.Join(" AND ", distinctTerms.Select(QuoteFts));
        List<string> candidateIds = RegionCandidates(connection, match);

        var hits = new List<RegionSearchHit>();
        var seenRegionIds = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>(32);
        foreach (string regionId in candidateIds)
        {
            if (!seenRegionIds.Add(regionId))
                continue;
            if (!_regionsById.TryGetValue(regionId, out RegionDocument? region))
                continue;
            if (!kinds.Contains(region.Kind))
                continue;
            if (excludeTests && LooksLikeTestPath(region.Path))
                continue;

            tokens.Clear();
            CodeTokenizer.Tokenize(region.RawText, tokens);

            double score = 0.0;
            int matchedTerms = 0;
            foreach (string term in distinctTerms)
            {
                int tf = CountOccurrences(tokens, term);
                if (tf == 0)
                    continue;
                matchedTerms++;
                score += Bm25.TermScore(
                    Bm25.Idf(_regionCount, documentFrequency[term]),
                    tf,
                    region.DocLen,
                    _regionAvgdl);
            }

            if (matchedTerms != distinctTerms.Count || score <= 0.0)
                continue;

            hits.Add(new RegionSearchHit(
                region.Path,
                score,
                region.StartLine,
                region.Kind,
                MakeSnippet(region.RawText),
                region.RawText,
                region.RegionId,
                region.ContainingSymbolId,
                region.ContainingSymbolName,
                region.Language));
        }

        if (hits.Count == 0)
            return Array.Empty<RegionSearchHit>();

        hits.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byPath = string.CompareOrdinal(a.Path, b.Path);
            if (byPath != 0) return byPath;
            int byLine = a.Line.CompareTo(b.Line);
            return byLine != 0 ? byLine : string.CompareOrdinal(a.RegionId, b.RegionId);
        });

        if (hits.Count > limit)
            hits.RemoveRange(limit, hits.Count - limit);
        return hits;
    }

    private static RegionMeta ReadMeta(SqliteConnection connection, string absPath)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT revision, schema_version, region_count, region_avgdl FROM meta LIMIT 2;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw MalformedMeta(absPath, "no meta row");

            long revision = ReadInt64(reader, 0, absPath, "revision");
            int schemaVersion = checked((int)ReadInt64(reader, 1, absPath, "schema_version"));
            int regionCount = checked((int)ReadInt64(reader, 2, absPath, "region_count"));
            double regionAvgdl = ReadDouble(reader, 3, absPath, "region_avgdl");

            if (reader.Read())
                throw MalformedMeta(absPath, "multiple meta rows");
            if (regionCount < 0)
                throw MalformedMeta(absPath, "region_count is negative");
            if (regionAvgdl < 0.0)
                throw MalformedMeta(absPath, "region_avgdl is negative");

            return new RegionMeta(revision, schemaVersion, regionCount, regionAvgdl);
        }
        catch (SqliteException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
        catch (InvalidCastException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
        catch (OverflowException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
    }

    private static long ReadInt64(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedMeta(absPath, $"{column} is null");
        return reader.GetInt64(ordinal);
    }

    private static double ReadDouble(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedMeta(absPath, $"{column} is null");
        return reader.GetDouble(ordinal);
    }

    private static InvalidOperationException MalformedMeta(string absPath, string detail, Exception? inner = null) =>
        new($"search.db at '{absPath}' has malformed meta: {detail}. Rebuild the search index.", inner);

    private static void EnsureRegionSchema(SqliteConnection connection, string absPath)
    {
        EnsureTable(connection, absPath, "regions_fts");
        EnsureTable(connection, absPath, "search_regions");
        EnsureColumns(connection, absPath, "regions_fts", "region_id", "body");
        EnsureColumns(
            connection,
            absPath,
            "search_regions",
            "region_id",
            "kind",
            "path",
            "language",
            "containing_symbol_id",
            "start_line",
            "end_line",
            "start_byte",
            "end_byte",
            "raw_text",
            "doc_len");
    }

    private static void EnsureTable(SqliteConnection connection, string absPath, string tableName)
    {
        if (!TableExists(connection, tableName))
        {
            throw new InvalidOperationException(
                $"search.db at '{absPath}' is missing required table '{tableName}'. Rebuild the search index.");
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static void EnsureColumns(
        SqliteConnection connection,
        string absPath,
        string tableName,
        params string[] requiredColumns)
    {
        HashSet<string> columns = ReadColumns(connection, tableName);
        foreach (string column in requiredColumns)
        {
            if (!columns.Contains(column))
            {
                throw new InvalidOperationException(
                    $"search.db at '{absPath}' table '{tableName}' is missing required column '{column}'. " +
                    "Rebuild the search index.");
            }
        }
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static IReadOnlyDictionary<string, string> ReadSymbolNamesIfAvailable(SqliteConnection connection)
    {
        if (!TableExists(connection, "search_symbols"))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        HashSet<string> columns = ReadColumns(connection, "search_symbols");
        if (!columns.Contains("symbol_id") || !columns.Contains("name"))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id, name FROM search_symbols WHERE symbol_id IS NOT NULL AND name IS NOT NULL;";
        using var reader = cmd.ExecuteReader();

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            names[reader.GetString(0)] = reader.GetString(1);
        return names;
    }

    private static IReadOnlyList<RegionDocument> ReadRegions(
        SqliteConnection connection,
        IReadOnlyDictionary<string, string> symbolNames,
        string absPath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT region_id, kind, path, language, containing_symbol_id,
                   start_line, end_line, start_byte, end_byte, raw_text, doc_len
            FROM search_regions
            ORDER BY path, start_line, region_id;
            """;

        var regions = new List<RegionDocument>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string regionId = RequiredString(reader, 0, absPath, "region_id");
            string kind = RequiredString(reader, 1, absPath, "kind");
            string path = RequiredString(reader, 2, absPath, "path");
            string language = RequiredString(reader, 3, absPath, "language");
            string? containingSymbolId = reader.IsDBNull(4) ? null : reader.GetString(4);
            int startLine = checked((int)RequiredInt64(reader, 5, absPath, "start_line"));
            int endLine = checked((int)RequiredInt64(reader, 6, absPath, "end_line"));
            long startByte = RequiredInt64(reader, 7, absPath, "start_byte");
            long endByte = RequiredInt64(reader, 8, absPath, "end_byte");
            string rawText = RequiredString(reader, 9, absPath, "raw_text");
            int docLen = checked((int)RequiredInt64(reader, 10, absPath, "doc_len"));

            if (docLen < 0)
            {
                throw new InvalidOperationException(
                    $"search.db at '{absPath}' region '{regionId}' has negative doc_len. Rebuild the search index.");
            }

            string? containingSymbolName = containingSymbolId is not null
                && symbolNames.TryGetValue(containingSymbolId, out string? name)
                    ? name
                    : null;

            regions.Add(new RegionDocument(
                regionId,
                kind,
                path,
                language,
                containingSymbolId,
                containingSymbolName,
                startLine,
                endLine,
                startByte,
                endByte,
                rawText,
                docLen));
        }

        return regions;
    }

    private static string RequiredString(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedRegion(absPath, column);
        return reader.GetString(ordinal);
    }

    private static long RequiredInt64(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedRegion(absPath, column);
        return reader.GetInt64(ordinal);
    }

    private static InvalidOperationException MalformedRegion(string absPath, string column) =>
        new($"search.db at '{absPath}' has malformed search_regions data: '{column}' is null. Rebuild the search index.");

    private static int CountRegionsMatching(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM regions_fts WHERE body MATCH $q;";
        cmd.Parameters.AddWithValue("$q", match);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> RegionCandidates(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT region_id FROM regions_fts WHERE body MATCH $q;";
        cmd.Parameters.AddWithValue("$q", match);

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static int CountOccurrences(List<string> tokens, string term)
    {
        int count = 0;
        foreach (string token in tokens)
            if (string.Equals(token, term, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static string QuoteFts(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string MakeSnippet(string rawText)
    {
        string snippet = rawText.Trim();
        if (snippet.Length <= SnippetMaxChars)
            return snippet;
        return snippet[..SnippetMaxChars].TrimEnd() + "...";
    }

    private static bool LooksLikeTestPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        string[] segments = filePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (TestSegments.Contains(segments[i]))
                return true;
            if (segments[i].EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segments[i].EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string fileName = segments[^1];
        if (TestSegments.Contains(fileName))
            return true;

        foreach (string infix in FileNameInfixes)
            if (fileName.Contains(infix, StringComparison.OrdinalIgnoreCase))
                return true;

        return StemLooksLikeTest(StripExtension(fileName));
    }

    private static bool StemLooksLikeTest(string stem)
    {
        if (stem.Length == 0)
            return false;
        if (HasBoundaryTestToken(stem))
            return true;
        foreach (string suffix in PascalSuffixes)
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool HasBoundaryTestToken(string stem)
    {
        int start = 0;
        for (int i = 0; i <= stem.Length; i++)
        {
            if (i != stem.Length && !IsTestDelimiter(stem[i]))
                continue;

            ReadOnlySpan<char> token = stem.AsSpan(start, i - start);
            if (token.Equals("test", StringComparison.OrdinalIgnoreCase)
                || token.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || token.Equals("spec", StringComparison.OrdinalIgnoreCase)
                || token.Equals("specs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }

    private static bool IsTestDelimiter(char c) => c is '.' or '_' or '-';

    private static string StripExtension(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 ? fileName[..^ext.Length] : fileName;
    }

    private sealed record RegionMeta(long Revision, int SchemaVersion, int RegionCount, double RegionAvgdl);

    private sealed record RegionDocument(
        string RegionId,
        string Kind,
        string Path,
        string Language,
        string? ContainingSymbolId,
        string? ContainingSymbolName,
        int StartLine,
        int EndLine,
        long StartByte,
        long EndByte,
        string RawText,
        int DocLen);
}
