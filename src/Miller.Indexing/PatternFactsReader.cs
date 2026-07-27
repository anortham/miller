using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Read-only projection over julie-extractors' structural_facts table.
/// </summary>
public sealed class PatternFactsReader
{
    private readonly PatternCatalogReader _catalogReader;

    public PatternFactsReader()
        : this(new PatternCatalogReader())
    {
    }

    public PatternFactsReader(PatternCatalogReader catalogReader)
    {
        ArgumentNullException.ThrowIfNull(catalogReader);
        _catalogReader = catalogReader;
    }

    public IReadOnlyList<PatternListRow> List(string dbPath, string? language = null) =>
        List(dbPath, patternId: null, language, pathGlob: null, metadataFilters: null);

    public IReadOnlyList<PatternListRow> List(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ValidateFilters(metadataFilters);

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        List<string> where = AddSearchFilters(
            command,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            out bool pathInSql);
        var grouped = new Dictionary<string, PatternListAccumulator>(StringComparer.Ordinal);
        if (pathInSql)
        {
            command.CommandText = $"""
                SELECT pattern_id, language, capture_name, COUNT(*) AS count
                FROM structural_facts
                {WhereClause(where)}
                GROUP BY pattern_id, language, capture_name
                ORDER BY pattern_id, language, capture_name;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                AddListCount(
                    grouped,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3));
            }
        }
        else
        {
            command.CommandText = $"""
                SELECT pattern_id, language, capture_name, path
                FROM structural_facts
                {WhereClause(where)};
                """;
            Func<string, bool> pathMatches = PatternPathGlobMatcher.Compile(pathGlob);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!pathMatches(reader.GetString(3)))
                    continue;
                AddListCount(grouped, reader.GetString(0), reader.GetString(1), reader.GetString(2), 1);
            }
        }

        IReadOnlyDictionary<string, PatternCatalogEntry> catalog = _catalogReader.Read(connection, transaction);
        PatternListRow[] result = grouped.Values
            .OrderBy(static row => row.PatternId, StringComparer.Ordinal)
            .Select(row => ToListRow(row, catalog))
            .ToArray();
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<PatternSummaryRow> Summary(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob = null,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters = null,
        PatternSummaryGroupBy groupBy = PatternSummaryGroupBy.LanguagePatternCapture,
        string? facetKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ValidateFilters(metadataFilters);
        return ReadSummary(dbPath, patternId, language, pathGlob, metadataFilters, groupBy, facetKey);
    }

    public IReadOnlyList<PatternMatchRow> Search(
        string dbPath,
        string patternId,
        string? language,
        PatternMetadataFilter? metadataFilter,
        int limit) =>
        Search(dbPath, patternId, language, pathGlob: null, metadataFilter, limit);

    public IReadOnlyList<PatternMatchRow> Search(
        string dbPath,
        string patternId,
        string? language,
        string? pathGlob,
        PatternMetadataFilter? metadataFilter,
        int limit) =>
        Search(dbPath, patternId, language, pathGlob, ToFilterList(metadataFilter), limit);

    public IReadOnlyList<PatternMatchRow> Search(
        string dbPath,
        string patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patternId);
        return SearchWithCount(dbPath, patternId, language, pathGlob, metadataFilters, limit).Rows;
    }

    public IReadOnlyList<PatternMatchRow> Matches(
        string dbPath,
        string? patternId,
        string? language,
        PatternMetadataFilter? metadataFilter,
        int? limit = null) =>
        Matches(dbPath, patternId, language, pathGlob: null, ToFilterList(metadataFilter), limit);

    public IReadOnlyList<PatternMatchRow> Matches(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int? limit = null) =>
        EnumerateMatches(dbPath, patternId, language, pathGlob, metadataFilters, limit).ToArray();

    public PatternMatchResult SearchWithCount(
        string dbPath,
        string patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int limit) =>
        SearchExactWithContext(
            dbPath,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            limit).Matches;

    public PatternExactSearchResult SearchExactWithContext(
        string dbPath,
        string patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int limit)
    {
        PatternExactSearchPageResult page = SearchExactPageWithContext(
            dbPath,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            offset: 0,
            limit);
        return new PatternExactSearchResult(
            new PatternMatchResult(page.Page.TotalCount, page.Page.Rows),
            page.PatternExists,
            page.SuggestionPatternIds);
    }

    public PatternExactSearchPageResult SearchExactPageWithContext(
        string dbPath,
        string patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int offset,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(patternId);
        ValidateFilters(metadataFilters);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int boundedLimit = Math.Clamp(limit, 1, 500);
        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PatternMatchPage page = ReadMatchPage(
            connection,
            transaction,
            [patternId],
            language,
            pathGlob,
            metadataFilters,
            offset,
            boundedLimit);
        bool patternExists = page.TotalCount > 0;
        IReadOnlyList<string> suggestionPatternIds = [];
        if (!patternExists)
        {
            using SqliteCommand observedCommand = connection.CreateCommand();
            observedCommand.Transaction = transaction;
            observedCommand.CommandText = """
                SELECT pattern_id,
                       MAX(CASE WHEN $language IS NULL OR language = $language THEN 1 ELSE 0 END)
                FROM structural_facts
                GROUP BY pattern_id
                ORDER BY pattern_id;
                """;
            observedCommand.Parameters.AddWithValue(
                "$language",
                string.IsNullOrWhiteSpace(language) ? DBNull.Value : language.Trim());
            var scoped = new List<string>();
            using SqliteDataReader observedReader = observedCommand.ExecuteReader();
            while (observedReader.Read())
            {
                string observedPatternId = observedReader.GetString(0);
                patternExists |= string.Equals(observedPatternId, patternId, StringComparison.Ordinal);
                if (observedReader.GetInt64(1) != 0)
                    scoped.Add(observedPatternId);
            }
            suggestionPatternIds = scoped;
        }
        transaction.Commit();
        return new PatternExactSearchPageResult(page, patternExists, suggestionPatternIds);
    }

    public PatternMatchResult SearchWithCount(
        string dbPath,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(patternIds);
        ValidateFilters(metadataFilters);

        foreach (string patternId in patternIds)
            ArgumentException.ThrowIfNullOrWhiteSpace(patternId);
        if (patternIds.Count == 0)
            return new PatternMatchResult(0, []);

        int boundedLimit = Math.Clamp(limit, 1, 500);
        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        int paramIndex = 0;
        List<string> where = AddFactFilters(command, patternIds, language);
        bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref paramIndex);
        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(command, where, metadataFilters, ref paramIndex);
        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        PatternMatchResult result = ReadMatchesWithCount(command, where, pathGlob, pathInSql, boundedLimit);
        transaction.Commit();
        return result;
    }

    public IReadOnlyDictionary<string, long> CountMatchesByPatternId(
        string dbPath,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(patternIds);
        ValidateFilters(metadataFilters);
        foreach (string patternId in patternIds)
            ArgumentException.ThrowIfNullOrWhiteSpace(patternId);

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        return CountMatchesByPatternId(
            connection,
            transaction: null,
            patternIds,
            language,
            pathGlob,
            metadataFilters);
    }

    public PatternQueryMatchResult SearchByQueryWithCount(
        string dbPath,
        string query,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int limit,
        int maxPatternIds)
    {
        PatternQueryMatchPageResult page = SearchByQueryPageWithCount(
            dbPath,
            query,
            language,
            pathGlob,
            metadataFilters,
            offset: 0,
            limit,
            maxPatternIds);
        return new PatternQueryMatchResult(
            page.ConsideredPatternIds,
            page.SuggestionPatternIds,
            page.MatchedPatternCount,
            page.ReturnedPatternIds,
            new PatternMatchResult(page.Page.TotalCount, page.Page.Rows));
    }

    public PatternQueryMatchPageResult SearchByQueryPageWithCount(
        string dbPath,
        string query,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int offset,
        int limit,
        int maxPatternIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ValidateFilters(metadataFilters);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (maxPatternIds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPatternIds));

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand observedCommand = connection.CreateCommand();
        observedCommand.Transaction = transaction;
        observedCommand.CommandText = """
            SELECT pattern_id, COUNT(*)
            FROM structural_facts
            GROUP BY pattern_id
            ORDER BY pattern_id;
            """;
        var observed = new List<PatternIdCount>();
        using (SqliteDataReader observedReader = observedCommand.ExecuteReader())
        {
            while (observedReader.Read())
                observed.Add(new PatternIdCount(observedReader.GetString(0), observedReader.GetInt64(1)));
        }
        PatternIdCount[] matched = observed
            .Where(row => row.PatternId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] suggestionPatternIds = observed.Select(static row => row.PatternId).ToArray();
        if (matched.Length == 0 && !string.IsNullOrWhiteSpace(language))
        {
            using SqliteCommand suggestionCommand = connection.CreateCommand();
            suggestionCommand.Transaction = transaction;
            suggestionCommand.CommandText = """
                SELECT pattern_id
                FROM structural_facts
                WHERE language = $language
                GROUP BY pattern_id
                ORDER BY pattern_id;
                """;
            suggestionCommand.Parameters.AddWithValue("$language", language.Trim());
            var scoped = new List<string>();
            using SqliteDataReader suggestionReader = suggestionCommand.ExecuteReader();
            while (suggestionReader.Read())
                scoped.Add(suggestionReader.GetString(0));
            suggestionPatternIds = scoped.ToArray();
        }
        bool hasActiveFilters = !string.IsNullOrWhiteSpace(pathGlob)
            || !string.IsNullOrWhiteSpace(language)
            || metadataFilters is { Count: > 0 };
        IReadOnlyDictionary<string, long> filteredCounts = hasActiveFilters && matched.Length > maxPatternIds
            ? CountMatchesByPatternId(
                connection,
                transaction,
                matched.Select(static row => row.PatternId).ToArray(),
                language,
                pathGlob,
                metadataFilters)
            : new Dictionary<string, long>(StringComparer.Ordinal);
        string[] returnedPatternIds = matched
            .OrderByDescending(row => filteredCounts.GetValueOrDefault(row.PatternId))
            .ThenByDescending(static row => row.Count)
            .ThenBy(static row => row.PatternId, StringComparer.Ordinal)
            .Take(maxPatternIds)
            .Select(static row => row.PatternId)
            .ToArray();
        PatternMatchPage page = returnedPatternIds.Length == 0
            ? EmptyMatchPage(offset)
            : ReadFairMatchPage(
                connection,
                transaction,
                returnedPatternIds,
                language,
                pathGlob,
                metadataFilters,
                offset,
                Math.Clamp(limit, 1, 500));
        transaction.Commit();

        return new PatternQueryMatchPageResult(
            observed.Select(static row => row.PatternId).ToArray(),
            suggestionPatternIds,
            matched.Length,
            returnedPatternIds,
            page);
    }

    private static IReadOnlyDictionary<string, long> CountMatchesByPatternId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (patternIds.Count == 0)
            return counts;

        foreach (string[] patternIdBatch in patternIds.Chunk(500))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            int paramIndex = 0;
            List<string> where = AddFactFilters(command, patternIdBatch, language);
            bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
                || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref paramIndex);
            bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
                || PatternMetadataSql.TryAddMetadataFilters(command, where, metadataFilters, ref paramIndex);
            if (metadataFilters is { Count: > 0 } && !metadataInSql)
                throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

            if (pathInSql)
            {
                command.CommandText = $"""
                    SELECT pattern_id, COUNT(*)
                    FROM structural_facts
                    {WhereClause(where)}
                    GROUP BY pattern_id;
                    """;
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    counts[reader.GetString(0)] = reader.GetInt64(1);
                continue;
            }

            command.CommandText = $"""
                SELECT pattern_id, path
                FROM structural_facts
                {WhereClause(where)};
                """;
            Func<string, bool> pathMatches = PatternPathGlobMatcher.Compile(pathGlob);
            using SqliteDataReader fallbackReader = command.ExecuteReader();
            while (fallbackReader.Read())
            {
                if (!pathMatches(fallbackReader.GetString(1)))
                    continue;

                string patternId = fallbackReader.GetString(0);
                counts.TryGetValue(patternId, out long count);
                counts[patternId] = count + 1;
            }
        }

        return counts;
    }

    private static PatternMatchResult SearchWithCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int boundedLimit)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        int paramIndex = 0;
        List<string> where = AddFactFilters(command, patternIds, language);
        bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref paramIndex);
        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(command, where, metadataFilters, ref paramIndex);
        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        return ReadMatchesWithCount(command, where, pathGlob, pathInSql, boundedLimit);
    }

    private static PatternMatchPage ReadMatchPage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int offset,
        int boundedLimit)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        int parameterIndex = 0;
        List<string> where = AddFactFilters(command, patternIds, language);
        bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref parameterIndex);
        if (!pathInSql)
        {
            Func<string, bool> pathMatches = PatternPathGlobMatcher.Compile(pathGlob);
            connection.CreateFunction(
                "miller_pattern_path_matches",
                (string candidate) => pathMatches(candidate),
                isDeterministic: true);
            where.Add("miller_pattern_path_matches(path)");
        }
        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(
                command,
                where,
                metadataFilters,
                ref parameterIndex);
        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        command.CommandText = $"""
            SELECT pattern_id, path, start_byte, structural_fact_id
            FROM structural_facts
            {WhereClause(where)}
            ORDER BY path, start_byte, structural_fact_id;
            """;
        (long totalCount, string populationFingerprint) = ReadPageIdentity(command);

        command.Parameters.AddWithValue("$page_limit", boundedLimit);
        command.Parameters.AddWithValue("$page_offset", offset);
        command.CommandText = $"""
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            {WhereClause(where)}
            ORDER BY path, start_byte, structural_fact_id
            LIMIT $page_limit OFFSET $page_offset;
            """;
        IReadOnlyList<PatternMatchRow> rows = ReadPayloadPage(command, boundedLimit);

        return new PatternMatchPage(
            totalCount,
            populationFingerprint,
            offset,
            rows);
    }

    private static PatternMatchPage ReadFairMatchPage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> patternIds,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int offset,
        int boundedLimit)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        int parameterIndex = 0;
        List<string> where = AddFactFilters(command, patternIds, language);
        bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref parameterIndex);
        if (!pathInSql)
        {
            Func<string, bool> pathMatches = PatternPathGlobMatcher.Compile(pathGlob);
            connection.CreateFunction(
                "miller_pattern_path_matches",
                (string candidate) => pathMatches(candidate),
                isDeterministic: true);
            where.Add("miller_pattern_path_matches(path)");
        }
        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(
                command,
                where,
                metadataFilters,
                ref parameterIndex);
        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        string rankedCte = $"""
            WITH ranked AS (
                SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                       containing_symbol_id, start_line, start_column, end_line, end_column,
                       start_byte, end_byte, confidence, metadata_json,
                       ROW_NUMBER() OVER (
                           PARTITION BY pattern_id
                           ORDER BY path, start_byte, structural_fact_id
                       ) AS family_rank
                FROM structural_facts
                {WhereClause(where)}
            )
            """;
        command.CommandText = rankedCte + """
            SELECT pattern_id, path, start_byte, structural_fact_id
            FROM ranked
            ORDER BY family_rank, pattern_id, path, start_byte, structural_fact_id;
            """;
        (long totalCount, string populationFingerprint) = ReadPageIdentity(command);

        command.Parameters.AddWithValue("$page_limit", boundedLimit);
        command.Parameters.AddWithValue("$page_offset", offset);
        command.CommandText = rankedCte + """
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM ranked
            ORDER BY family_rank, pattern_id, path, start_byte, structural_fact_id
            LIMIT $page_limit OFFSET $page_offset;
            """;
        IReadOnlyList<PatternMatchRow> rows = ReadPayloadPage(command, boundedLimit);
        return new PatternMatchPage(totalCount, populationFingerprint, offset, rows);
    }

    private static (long TotalCount, string PopulationFingerprint) ReadPageIdentity(
        SqliteCommand command)
    {
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalCount = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            AppendPatternMatchIdentity(
                fingerprint,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3));
            totalCount++;
        }

        return (
            totalCount,
            Convert.ToHexStringLower(fingerprint.GetHashAndReset()));
    }

    private static IReadOnlyList<PatternMatchRow> ReadPayloadPage(
        SqliteCommand command,
        int boundedLimit)
    {
        var rows = new List<PatternMatchRow>(boundedLimit);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(ReadMatch(reader));
        return rows;
    }

    private static PatternMatchPage EmptyMatchPage(int offset)
    {
        using IncrementalHash fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        return new PatternMatchPage(
            0,
            Convert.ToHexStringLower(fingerprint.GetHashAndReset()),
            offset,
            []);
    }

    private static void AppendPatternMatchIdentity(IncrementalHash fingerprint, PatternMatchRow row)
        => AppendPatternMatchIdentity(
            fingerprint,
            row.PatternId,
            row.Path,
            row.Span.StartByte,
            row.FactId);

    private static void AppendPatternMatchIdentity(
        IncrementalHash fingerprint,
        string patternId,
        string path,
        int startByte,
        string factId)
    {
        AppendFingerprintField(fingerprint, patternId);
        AppendFingerprintField(fingerprint, path);
        AppendFingerprintField(
            fingerprint,
            startByte.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintField(fingerprint, factId);
    }

    private static void AppendFingerprintField(IncrementalHash fingerprint, string? field)
    {
        string value = field ?? string.Empty;
        fingerprint.AppendData(Encoding.UTF8.GetBytes(
            value.Length.ToString(CultureInfo.InvariantCulture) + ":"));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(value));
    }

    public IEnumerable<PatternMatchRow> EnumerateMatches(
        string dbPath,
        string? patternId,
        string? language,
        PatternMetadataFilter? metadataFilter,
        int? limit = null) =>
        EnumerateMatches(dbPath, patternId, language, pathGlob: null, ToFilterList(metadataFilter), limit);

    public IEnumerable<PatternMatchRow> EnumerateMatches(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ValidateFilters(metadataFilters);

        int? boundedLimit = limit is null ? null : Math.Clamp(limit.Value, 1, 500);
        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        foreach (PatternMatchRow row in EnumerateMatches(
                     connection,
                     patternId,
                     language,
                     pathGlob,
                     metadataFilters,
                     boundedLimit))
        {
            yield return row;
        }
    }

    private static IEnumerable<PatternMatchRow> EnumerateMatches(
        SqliteConnection connection,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        int? boundedLimit)
    {
        using SqliteCommand command = connection.CreateCommand();
        List<string> where = AddSearchFilters(
            command,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            out bool pathInSql);

        string limitClause = boundedLimit is null || !pathInSql ? string.Empty : $" LIMIT {boundedLimit.Value}";
        command.CommandText = $"""
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            {WhereClause(where)}
            ORDER BY path, start_byte, structural_fact_id
            {limitClause};
            """;

        int emitted = 0;
        Func<string, bool>? pathMatches = pathInSql ? null : PatternPathGlobMatcher.Compile(pathGlob);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (pathMatches is not null && !pathMatches(reader.GetString(3)))
                continue;
            PatternMatchRow row = ReadMatch(reader);

            yield return row;
            emitted++;
            if (boundedLimit is not null && emitted >= boundedLimit.Value)
                break;
        }
    }

    private static PatternMatchResult ReadMatchesWithCount(
        SqliteCommand command,
        IReadOnlyList<string> where,
        string? pathGlob,
        bool pathInSql,
        int boundedLimit)
    {
        if (pathInSql)
        {
            command.CommandText = $"""
                WITH page AS (
                    SELECT structural_fact_id, path, start_byte, COUNT(*) OVER() AS total_count
                    FROM structural_facts
                    {WhereClause(where)}
                    ORDER BY path, start_byte, structural_fact_id
                    LIMIT {boundedLimit}
                )
                SELECT fact.structural_fact_id, fact.pattern_id, fact.language, fact.path,
                       fact.capture_name, fact.node_kind, fact.containing_symbol_id,
                       fact.start_line, fact.start_column, fact.end_line, fact.end_column,
                       fact.start_byte, fact.end_byte, fact.confidence, fact.metadata_json,
                       page.total_count
                FROM page
                JOIN structural_facts AS fact ON fact.structural_fact_id = page.structural_fact_id
                ORDER BY page.path, page.start_byte, page.structural_fact_id;
                """;

            long totalCount = 0;
            var rows = new List<PatternMatchRow>(boundedLimit);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                totalCount = reader.GetInt64(15);
                rows.Add(ReadMatch(reader));
            }
            return new PatternMatchResult(totalCount, rows);
        }

        command.CommandText = $"""
            SELECT structural_fact_id, path, start_byte
            FROM structural_facts
            {WhereClause(where)}
            ORDER BY path, start_byte, structural_fact_id;
            """;
        long fallbackTotalCount = 0;
        var retainedIds = new List<string>(boundedLimit);
        Func<string, bool> pathMatches = PatternPathGlobMatcher.Compile(pathGlob);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!pathMatches(reader.GetString(1)))
                    continue;

                fallbackTotalCount++;
                if (retainedIds.Count < boundedLimit)
                    retainedIds.Add(reader.GetString(0));
            }
        }

        if (retainedIds.Count == 0)
            return new PatternMatchResult(fallbackTotalCount, []);

        SqliteConnection connection = command.Connection
            ?? throw new InvalidOperationException("patterns fallback query has no SQLite connection.");
        using SqliteCommand payload = connection.CreateCommand();
        payload.Transaction = command.Transaction;
        var idParams = new string[retainedIds.Count];
        for (int i = 0; i < retainedIds.Count; i++)
        {
            idParams[i] = $"$retained_{i}";
            payload.Parameters.AddWithValue(idParams[i], retainedIds[i]);
        }
        payload.CommandText = $"""
            SELECT structural_fact_id, pattern_id, language, path, capture_name, node_kind,
                   containing_symbol_id, start_line, start_column, end_line, end_column,
                   start_byte, end_byte, confidence, metadata_json
            FROM structural_facts
            WHERE structural_fact_id IN ({string.Join(", ", idParams)});
            """;
        var rowsById = new Dictionary<string, PatternMatchRow>(StringComparer.Ordinal);
        using (SqliteDataReader reader = payload.ExecuteReader())
        {
            while (reader.Read())
            {
                PatternMatchRow row = ReadMatch(reader);
                rowsById.Add(row.FactId, row);
            }
        }

        return new PatternMatchResult(
            fallbackTotalCount,
            retainedIds.Select(id => rowsById[id]).ToArray());
    }

    private static List<string> AddSearchFilters(
        SqliteCommand command,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        out bool pathInSql)
    {
        int paramIndex = 0;
        List<string> where = AddFactFilters(command, patternId, language, ref paramIndex);
        pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref paramIndex);
        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(command, where, metadataFilters, ref paramIndex);

        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        return where;
    }

    private static IReadOnlyList<PatternSummaryRow> ReadSummary(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        PatternSummaryGroupBy groupBy,
        string? facetKey)
    {
        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        List<string> where = AddSearchFilters(
            command,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            out bool pathInSql);

        string facetExpression = "NULL";
        if (!string.IsNullOrWhiteSpace(facetKey))
        {
            if (!PatternMetadataSql.TryBuildJsonPath(facetKey, out string jsonPath))
                throw new InvalidOperationException("patterns facet key contains unsupported characters.");
            command.Parameters.AddWithValue("$facet_path", jsonPath);
            where.Add("""
                metadata_json IS NOT NULL
                AND json_valid(metadata_json)
                AND json_type(metadata_json, $facet_path) IS NOT NULL
                """);
            facetExpression = """
                CASE json_type(metadata_json, $facet_path)
                    WHEN 'true' THEN 'true'
                    WHEN 'false' THEN 'false'
                    WHEN 'null' THEN 'null'
                    ELSE CAST(json_extract(metadata_json, $facet_path) AS TEXT)
                END
                """;
        }

        bool groupByPath = groupBy != PatternSummaryGroupBy.LanguagePatternCapture;
        bool selectPath = !pathInSql || groupByPath;
        bool hasFacet = !string.IsNullOrWhiteSpace(facetKey);
        string grouping = pathInSql
            ? "GROUP BY language, pattern_id, capture_name"
              + (groupByPath ? ", path" : string.Empty)
              + (hasFacet ? ", facet_value" : string.Empty)
            : string.Empty;
        command.CommandText = $"""
            SELECT language, pattern_id, capture_name, {(selectPath ? "path" : "NULL")} AS path,
                   {facetExpression} AS facet_value,
                   {(pathInSql ? "COUNT(*)" : "1")} AS count
            FROM structural_facts
            {WhereClause(where)}
            {grouping};
            """;

        var groups = new Dictionary<SummaryGroupKey, long>();
        Func<string, bool>? pathMatches = pathInSql ? null : PatternPathGlobMatcher.Compile(pathGlob);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            if (pathMatches is not null && !pathMatches(path))
                continue;

            string? facetValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            SummaryGroupKey key = SummaryKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                path,
                facetValue,
                groupBy);
            groups.TryGetValue(key, out long count);
            groups[key] = count + reader.GetInt64(5);
        }

        return groups
            .OrderBy(static pair => pair.Key.Language, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.PatternId, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.CaptureName, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Path, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Directory, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.FacetValue, StringComparer.Ordinal)
            .Select(static pair => new PatternSummaryRow(
                pair.Key.Language,
                pair.Key.PatternId,
                pair.Key.CaptureName,
                pair.Value,
                pair.Key.Path,
                pair.Key.Directory,
                pair.Key.FacetValue))
            .ToArray();
    }

    private static SummaryGroupKey SummaryKey(
        string language,
        string patternId,
        string captureName,
        string path,
        string? facetValue,
        PatternSummaryGroupBy groupBy) =>
        groupBy switch
        {
            PatternSummaryGroupBy.File => new SummaryGroupKey(
                language,
                patternId,
                captureName,
                Path: path,
                Directory: null,
                facetValue),
            PatternSummaryGroupBy.Directory => new SummaryGroupKey(
                language,
                patternId,
                captureName,
                Path: null,
                Directory: PatternDirectory.FromPath(path),
                facetValue),
            PatternSummaryGroupBy.TopDirectory => new SummaryGroupKey(
                language,
                patternId,
                captureName,
                Path: null,
                Directory: PatternDirectory.TopFromPath(path),
                facetValue),
            _ => new SummaryGroupKey(
                language,
                patternId,
                captureName,
                Path: null,
                Directory: null,
                facetValue),
        };

    private static void AddListCount(
        IDictionary<string, PatternListAccumulator> grouped,
        string patternId,
        string language,
        string captureName,
        long count)
    {
        if (!grouped.TryGetValue(patternId, out PatternListAccumulator? accumulator))
        {
            accumulator = new PatternListAccumulator(patternId);
            grouped.Add(patternId, accumulator);
        }

        accumulator.Languages.Add(language);
        accumulator.Captures.Add(captureName);
        accumulator.Count += count;
    }

    private static PatternListRow ToListRow(
        PatternListAccumulator row,
        IReadOnlyDictionary<string, PatternCatalogEntry> catalog)
    {
        string label = row.PatternId;
        string catalogState = "observed";
        string? description = null;
        IReadOnlyList<string>? tags = null;
        IReadOnlyList<string>? expectedKeys = null;
        if (catalog.TryGetValue(row.PatternId, out PatternCatalogEntry? entry))
        {
            label = entry.Label;
            catalogState = "known";
            description = entry.Description;
            tags = ParseStringArrayJson(entry.TagsJson);
            expectedKeys = ParseStringArrayJson(entry.ExpectedMetadataKeysJson);
        }

        return new PatternListRow(
            row.PatternId,
            Label: label,
            Languages: row.Languages.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            Captures: row.Captures.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            Count: row.Count,
            Catalog: catalogState,
            Description: description,
            Tags: tags,
            ExpectedMetadataKeys: expectedKeys);
    }

    private static IReadOnlyList<string>? ParseStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            return doc.RootElement.EnumerateArray()
                .Where(static value => value.ValueKind == JsonValueKind.String)
                .Select(static value => value.GetString() ?? string.Empty)
                .Where(static value => value.Length > 0)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SqliteConnection OpenStructuralFacts(string dbPath)
    {
        SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        try
        {
            JulieSchemaGate.Verify(connection);
            if (!TableExists(connection, "structural_facts"))
                throw new InvalidOperationException("table 'structural_facts' is missing");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = command.ExecuteScalar();
        return result is not null and not DBNull;
    }

    private static List<string> AddFactFilters(SqliteCommand command, string? patternId, string? language)
    {
        int paramIndex = 0;
        return AddFactFilters(command, patternId, language, ref paramIndex);
    }

    private static List<string> AddFactFilters(
        SqliteCommand command,
        string? patternId,
        string? language,
        ref int paramIndex)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(patternId))
        {
            where.Add("pattern_id = $pattern_id");
            command.Parameters.AddWithValue("$pattern_id", patternId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            where.Add("language = $language");
            command.Parameters.AddWithValue("$language", language.Trim());
        }

        return where;
    }

    private static List<string> AddFactFilters(
        SqliteCommand command,
        IReadOnlyList<string> patternIds,
        string? language)
    {
        var where = new List<string>();
        var patternParameters = new string[patternIds.Count];
        for (int i = 0; i < patternIds.Count; i++)
        {
            string parameter = "$pattern_id_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            patternParameters[i] = parameter;
            command.Parameters.AddWithValue(parameter, patternIds[i].Trim());
        }
        where.Add("pattern_id IN (" + string.Join(", ", patternParameters) + ")");

        if (!string.IsNullOrWhiteSpace(language))
        {
            where.Add("language = $language");
            command.Parameters.AddWithValue("$language", language.Trim());
        }

        return where;
    }

    private static string WhereClause(IReadOnlyCollection<string> where) =>
        where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);

    private static PatternMatchRow ReadMatch(SqliteDataReader reader)
    {
        string? metadataJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        JsonElement metadata = default;
        string? metadataError = null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    metadata = doc.RootElement.Clone();
                else
                    metadataError = "metadata_json root is not an object";
            }
            catch (JsonException ex)
            {
                metadataError = ex.Message;
            }
        }

        return new PatternMatchRow(
            FactId: reader.GetString(0),
            PatternId: reader.GetString(1),
            Language: reader.GetString(2),
            Path: reader.GetString(3),
            CaptureName: reader.GetString(4),
            NodeKind: reader.GetString(5),
            ContainingSymbolId: reader.IsDBNull(6) ? null : reader.GetString(6),
            Span: new PatternSpan(
                StartLine: reader.GetInt32(7),
                StartColumn: reader.GetInt32(8),
                EndLine: reader.GetInt32(9),
                EndColumn: reader.GetInt32(10),
                StartByte: reader.GetInt32(11),
                EndByte: reader.GetInt32(12)),
            Confidence: reader.GetDouble(13),
            MetadataJson: metadataJson,
            Metadata: metadata,
            MetadataError: metadataError);
    }

    private static void ValidateFilters(IReadOnlyList<PatternMetadataFilter>? metadataFilters)
    {
        if (metadataFilters is null)
            return;

        foreach (PatternMetadataFilter filter in metadataFilters)
            filter.Validate();
    }

    private static IReadOnlyList<PatternMetadataFilter>? ToFilterList(PatternMetadataFilter? metadataFilter) =>
        metadataFilter is null ? null : new[] { metadataFilter };

    private sealed class PatternListAccumulator(string patternId)
    {
        public string PatternId { get; } = patternId;
        public HashSet<string> Languages { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Captures { get; } = new(StringComparer.Ordinal);
        public long Count { get; set; }
    }

    private sealed record PatternIdCount(string PatternId, long Count);

    private sealed record SummaryGroupKey(
        string Language,
        string PatternId,
        string CaptureName,
        string? Path,
        string? Directory,
        string? FacetValue);
}

public enum PatternSummaryGroupBy
{
    LanguagePatternCapture,
    File,
    Directory,
    TopDirectory,
}

public sealed record PatternListRow(
    string PatternId,
    string Label,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Captures,
    long Count,
    string Catalog,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? ExpectedMetadataKeys = null);

public sealed record PatternSummaryRow(
    string Language,
    string PatternId,
    string CaptureName,
    long Count,
    string? Path = null,
    string? Directory = null,
    string? FacetValue = null);

public sealed record PatternMatchRow(
    string FactId,
    string PatternId,
    string Language,
    string Path,
    string CaptureName,
    string NodeKind,
    string? ContainingSymbolId,
    PatternSpan Span,
    double Confidence,
    string? MetadataJson,
    JsonElement Metadata,
    string? MetadataError);

public sealed record PatternMatchResult(
    long TotalCount,
    IReadOnlyList<PatternMatchRow> Rows);

public sealed record PatternMatchPage(
    long TotalCount,
    string PopulationFingerprint,
    int Offset,
    IReadOnlyList<PatternMatchRow> Rows);

public sealed record PatternExactSearchResult(
    PatternMatchResult Matches,
    bool PatternExists,
    IReadOnlyList<string> SuggestionPatternIds);

public sealed record PatternExactSearchPageResult(
    PatternMatchPage Page,
    bool PatternExists,
    IReadOnlyList<string> SuggestionPatternIds);

public sealed record PatternQueryMatchResult(
    IReadOnlyList<string> ConsideredPatternIds,
    IReadOnlyList<string> SuggestionPatternIds,
    int MatchedPatternCount,
    IReadOnlyList<string> ReturnedPatternIds,
    PatternMatchResult Matches);

public sealed record PatternQueryMatchPageResult(
    IReadOnlyList<string> ConsideredPatternIds,
    IReadOnlyList<string> SuggestionPatternIds,
    int MatchedPatternCount,
    IReadOnlyList<string> ReturnedPatternIds,
    PatternMatchPage Page);

public sealed record PatternSpan(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartByte,
    int EndByte);

public sealed record PatternMetadataFilter(string Key, string Value)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
        if (!PatternMetadataSql.TryBuildJsonPath(Key, out _))
            throw new InvalidOperationException("patterns where key contains unsupported characters.");
    }
}

internal static class PatternDirectory
{
    public static string FromPath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
            return string.Empty;

        string[] segments = normalized[..lastSlash].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return string.Empty;

        return string.Join('/', segments);
    }

    public static string TopFromPath(string path)
    {
        string parent = FromPath(path);
        int firstSlash = parent.IndexOf('/');
        return firstSlash < 0 ? parent : parent[..firstSlash];
    }
}
