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

    public IReadOnlyList<PatternListRow> List(string dbPath, string? language = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using SqliteConnection connection = OpenStructuralFacts(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(language))
        {
            where.Add("language = $language");
            command.Parameters.AddWithValue("$language", language.Trim());
        }

        command.CommandText = $"""
            SELECT pattern_id, language, capture_name, COUNT(*) AS count
            FROM structural_facts
            {WhereClause(where)}
            GROUP BY pattern_id, language, capture_name
            ORDER BY pattern_id, language, capture_name;
            """;

        IReadOnlyDictionary<string, PatternCatalogEntry> catalog = _catalogReader.Read(dbPath);
        var grouped = new Dictionary<string, PatternListAccumulator>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string patternId = reader.GetString(0);
            if (!grouped.TryGetValue(patternId, out PatternListAccumulator? acc))
            {
                acc = new PatternListAccumulator(patternId);
                grouped.Add(patternId, acc);
            }

            acc.Languages.Add(reader.GetString(1));
            acc.Captures.Add(reader.GetString(2));
            acc.Count += reader.GetInt64(3);
        }

        return grouped.Values
            .OrderBy(static row => row.PatternId, StringComparer.Ordinal)
            .Select(row => ToListRow(row, catalog))
            .ToArray();
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

        if (groupBy == PatternSummaryGroupBy.LanguagePatternCapture
            && string.IsNullOrWhiteSpace(pathGlob)
            && (metadataFilters is null || metadataFilters.Count == 0)
            && string.IsNullOrWhiteSpace(facetKey))
        {
            using SqliteConnection connection = OpenStructuralFacts(dbPath);
            using SqliteCommand command = connection.CreateCommand();
            List<string> where = AddFactFilters(command, patternId, language);

            command.CommandText = $"""
                SELECT language, pattern_id, capture_name, COUNT(*) AS count
                FROM structural_facts
                {WhereClause(where)}
                GROUP BY language, pattern_id, capture_name
                ORDER BY language, pattern_id, capture_name;
                """;

            var rows = new List<PatternSummaryRow>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new PatternSummaryRow(
                    Language: reader.GetString(0),
                    PatternId: reader.GetString(1),
                    CaptureName: reader.GetString(2),
                    Count: reader.GetInt64(3)));
            }

            return rows;
        }

        return SummaryFromMatches(dbPath, patternId, language, pathGlob, metadataFilters, groupBy, facetKey);
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
        return Matches(dbPath, patternId, language, pathGlob, metadataFilters, limit);
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
        using SqliteCommand command = connection.CreateCommand();
        int paramIndex = 0;
        List<string> where = AddFactFilters(command, patternId, language, ref paramIndex);

        bool pathInSql = string.IsNullOrWhiteSpace(pathGlob)
            || PatternPathGlobSql.TryAddPathPredicate(command, where, pathGlob, ref paramIndex);

        bool metadataInSql = metadataFilters is null || metadataFilters.Count == 0
            || PatternMetadataSql.TryAddMetadataFilters(command, where, metadataFilters, ref paramIndex);

        if (metadataFilters is { Count: > 0 } && !metadataInSql)
            throw new InvalidOperationException("patterns where contains unsupported metadata keys.");

        bool filtersFullyInSql = pathInSql && metadataInSql;
        string limitClause = boundedLimit is null || !filtersFullyInSql ? string.Empty : $" LIMIT {boundedLimit.Value}";
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
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            PatternMatchRow row = ReadMatch(reader);
            if (!pathInSql && !PatternPathGlobMatcher.IsMatch(row.Path, pathGlob!))
                continue;
            if (!metadataInSql && metadataFilters is not null)
            {
                if (!MetadataMatchesAll(row, metadataFilters))
                    continue;
            }

            yield return row;
            emitted++;
            if (boundedLimit is not null && emitted >= boundedLimit.Value)
                break;
        }
    }

    private static IReadOnlyList<PatternSummaryRow> SummaryFromMatches(
        string dbPath,
        string? patternId,
        string? language,
        string? pathGlob,
        IReadOnlyList<PatternMetadataFilter>? metadataFilters,
        PatternSummaryGroupBy groupBy,
        string? facetKey)
    {
        IEnumerable<PatternMatchRow> rows = new PatternFactsReader().EnumerateMatches(
            dbPath,
            patternId,
            language,
            pathGlob,
            metadataFilters,
            limit: null);

        if (!string.IsNullOrWhiteSpace(facetKey))
        {
            return rows
                .Select(row => new
                {
                    Group = BuildSummaryGroup(row, groupBy),
                    Facet = ReadFacetValue(row, facetKey.Trim()),
                })
                .Where(static item => item.Facet is not null)
                .GroupBy(item => new
                {
                    item.Group.Language,
                    item.Group.PatternId,
                    item.Group.CaptureName,
                    item.Group.Path,
                    item.Group.Directory,
                    Facet = item.Facet!,
                })
                .OrderBy(static group => group.Key.Language, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.PatternId, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.CaptureName, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.Path, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.Directory, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.Facet, StringComparer.Ordinal)
                .Select(static group => new PatternSummaryRow(
                    Language: group.Key.Language,
                    PatternId: group.Key.PatternId,
                    CaptureName: group.Key.CaptureName,
                    Count: group.LongCount(),
                    Path: group.Key.Path,
                    Directory: group.Key.Directory,
                    FacetValue: group.Key.Facet))
                .ToArray();
        }

        return rows
            .GroupBy(row => BuildSummaryGroup(row, groupBy))
            .OrderBy(static group => group.Key.Language, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.PatternId, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.CaptureName, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Path, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Directory, StringComparer.Ordinal)
            .Select(static group => new PatternSummaryRow(
                group.Key.Language,
                group.Key.PatternId,
                group.Key.CaptureName,
                group.LongCount(),
                group.Key.Path,
                group.Key.Directory))
            .ToArray();
    }

    private static SummaryGroupKey BuildSummaryGroup(PatternMatchRow row, PatternSummaryGroupBy groupBy) =>
        groupBy switch
        {
            PatternSummaryGroupBy.File => new SummaryGroupKey(
                row.Language,
                row.PatternId,
                row.CaptureName,
                Path: row.Path,
                Directory: null),
            PatternSummaryGroupBy.Directory => new SummaryGroupKey(
                row.Language,
                row.PatternId,
                row.CaptureName,
                Path: null,
                Directory: PatternDirectory.FromPath(row.Path)),
            _ => new SummaryGroupKey(row.Language, row.PatternId, row.CaptureName, Path: null, Directory: null),
        };

    private static string? ReadFacetValue(PatternMatchRow row, string facetKey)
    {
        if (row.MetadataError is not null || row.Metadata.ValueKind != JsonValueKind.Object)
            return null;
        if (!row.Metadata.TryGetProperty(facetKey, out JsonElement value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
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

    private static bool MetadataMatchesAll(PatternMatchRow row, IReadOnlyList<PatternMetadataFilter> filters)
    {
        foreach (PatternMetadataFilter filter in filters)
        {
            if (!MetadataMatches(row, filter))
                return false;
        }

        return true;
    }

    private static bool MetadataMatches(PatternMatchRow row, PatternMetadataFilter filter)
    {
        if (row.MetadataError is not null || row.Metadata.ValueKind != JsonValueKind.Object)
            return false;
        if (!row.Metadata.TryGetProperty(filter.Key, out JsonElement value))
            return false;

        string actual = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        return string.Equals(actual, filter.Value, StringComparison.Ordinal);
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

    private sealed record SummaryGroupKey(
        string Language,
        string PatternId,
        string CaptureName,
        string? Path,
        string? Directory);
}

public enum PatternSummaryGroupBy
{
    LanguagePatternCapture,
    File,
    Directory,
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
        if (segments.Length == 1)
            return segments[0];

        return string.Join('/', segments[..Math.Min(2, segments.Length)]);
    }
}
