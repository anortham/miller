using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

[McpServerToolType]
public sealed class PatternsTool
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxQueryPatternIds = 25;
    private static readonly string[] MetadataPriority =
    [
        "verb",
        "route_template",
        "name",
        "attribute_name",
        "directive",
        "key",
        "framework",
        "query_family",
        "api_style",
    ];

    private readonly IWorkspaceArtifactProvider _workspaceProvider;
    private readonly PatternFactsReader _reader;

    private sealed record PatternNextAction(
        string Tool,
        string Reason,
        IReadOnlyList<KeyValuePair<string, string>> Args);

    public PatternsTool(IWorkspaceArtifactProvider workspaceProvider, PatternFactsReader reader)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(reader);
        _workspaceProvider = workspaceProvider;
        _reader = reader;
    }

    [McpServerTool(Name = "patterns")]
    [Description(
        "List, summarize, and search code-shape facts emitted by julie-extractors. Call with no args to discover " +
        "observed pattern_id values, then search by pattern_id plus path/language/where filters, or pass a free-text " +
        "query with no pattern_id to search across every pattern_id that contains it. Examples: " +
        "`patterns operation=search pattern_id=aspnet.minimal_api.route.v1`; " +
        "`patterns operation=search query=route`. List/no-match results include next_actions. Not raw AST queries.")]
    public string Patterns(
        [Description("list|summary|search. Default list.")] string? operation = "list",
        [Description("Pattern id. Required for search unless query is given; optional for summary/list. Example: htmx.attribute.v1.")] string? pattern_id = null,
        [Description("Free-text query for search when pattern_id is omitted. Maps to every pattern_id containing the substring (case-insensitive) and searches across them. Ignored when pattern_id is supplied. Example: route.")] string? query = null,
        [Description("Language filter such as csharp, html, or razor. Optional.")] string? language = null,
        [Description("Workspace-relative glob filter, e.g. Views/**. Optional.")] string? path = null,
        [Description("Top-level metadata equality filter as key=value. Repeatable on CLI; MCP accepts key=value or semicolon-separated AND filters. Requires pattern_id or query for search.")] string? where = null,
        [Description("summary grouping: language_pattern_capture|file|directory. Default language_pattern_capture.")] string? group_by = null,
        [Description("Optional summary metadata facet key.")] string? facet = null,
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh selected workspace before reading. Defaults true when workspace_id is supplied.")] bool? ensure_fresh = null,
        [Description("Max search results. Default 50, maximum 500.")] int limit = DefaultLimit,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool refresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceArtifactContext context = _workspaceProvider.ResolveArtifact(workspace_id, refresh);

            PatternToolResult result = Run(
                _reader,
                context.IndexDbPath,
                operation,
                pattern_id,
                query,
                language,
                path,
                where,
                group_by,
                facet,
                limit,
                json);

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = NormalizeOperation(operation);
                telemetry.SetTarget(TargetForTelemetry(operation, pattern_id, query));
                telemetry.ResultCount = result.ResultCount;
                telemetry.Outcome = result.ResultCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                telemetry.SetMetadata("has_pattern_id", !string.IsNullOrWhiteSpace(pattern_id));
                telemetry.SetMetadata("has_query", !string.IsNullOrWhiteSpace(query));
                telemetry.SetMetadata("has_language", !string.IsNullOrWhiteSpace(language));
                telemetry.SetMetadata("has_path", !string.IsNullOrWhiteSpace(path));
                telemetry.SetMetadata("has_where", !string.IsNullOrWhiteSpace(where));
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
                if (result.ResultCount == 0)
                    telemetry.SetEmptyReason(result.EmptyReason ?? "no_facts");
            }

            string? banner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            return ReadToolWorkspaceRouting.PrefixCompact(result.Output, banner);
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"patterns failed: {ex.Message}";
        }
    }

    internal static PatternToolResult Run(
        PatternFactsReader reader,
        string dbPath,
        string? operation,
        string? patternId,
        string? query,
        string? language,
        string? path,
        string? where,
        string? groupBy,
        string? facet,
        int limit,
        bool json)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        string op = NormalizeOperation(operation);
        if ((op is "list" or "summary") && !string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("patterns query is only supported for search.");

        IReadOnlyList<PatternMetadataFilter> metadataFilters = ParseWhereFilters(where);
        if (metadataFilters.Count > 0 && string.IsNullOrWhiteSpace(patternId) && string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("patterns where requires pattern_id or query.");

        PatternSummaryGroupBy summaryGroupBy = ParseSummaryGroupBy(groupBy);

        return op switch
        {
            "list" => List(reader, dbPath, patternId, language, path, metadataFilters, json),
            "summary" => Summary(reader, dbPath, patternId, language, path, metadataFilters, summaryGroupBy, facet, json),
            "search" => SearchDispatch(reader, dbPath, patternId, query, language, path, metadataFilters, limit, json),
            _ => throw new InvalidOperationException("patterns operation must be list, summary, or search."),
        };
    }

    private static PatternToolResult SearchDispatch(
        PatternFactsReader reader,
        string dbPath,
        string? patternId,
        string? query,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json)
    {
        if (!string.IsNullOrWhiteSpace(patternId))
            return Search(reader, dbPath, RequiredPatternId(patternId), language, path, metadataFilters, limit, json);

        if (!string.IsNullOrWhiteSpace(query))
            return SearchByQuery(reader, dbPath, query.Trim(), language, path, metadataFilters, limit, json);

        throw new InvalidOperationException("patterns search requires pattern_id or query.");
    }

    internal static IReadOnlyList<PatternMetadataFilter> ParseWhereFilters(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return Array.Empty<PatternMetadataFilter>();

        return where
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSingleWhere)
            .ToArray();
    }

    private static PatternMetadataFilter ParseSingleWhere(string where)
    {
        int equals = where.IndexOf('=');
        if (equals <= 0)
            throw new InvalidOperationException("patterns where must be key=value.");

        string key = where[..equals].Trim();
        string value = where[(equals + 1)..].Trim();
        if (key.Length == 0)
            throw new InvalidOperationException("patterns where must include a key.");

        var filter = new PatternMetadataFilter(key, value);
        filter.Validate();
        return filter;
    }

    private static PatternSummaryGroupBy ParseSummaryGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return PatternSummaryGroupBy.LanguagePatternCapture;

        return groupBy.Trim().ToLowerInvariant() switch
        {
            "language_pattern_capture" or "default" => PatternSummaryGroupBy.LanguagePatternCapture,
            "file" => PatternSummaryGroupBy.File,
            "directory" or "dir" => PatternSummaryGroupBy.Directory,
            _ => throw new InvalidOperationException("patterns group_by must be language_pattern_capture, file, or directory."),
        };
    }

    private static PatternToolResult List(
        PatternFactsReader reader,
        string dbPath,
        string? patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        bool json)
    {
        IReadOnlyList<PatternListRow> rows;
        if (string.IsNullOrWhiteSpace(path) && metadataFilters.Count == 0 && string.IsNullOrWhiteSpace(patternId))
        {
            rows = reader.List(dbPath, language);
        }
        else
        {
            rows = reader.Matches(dbPath, patternId, language, path, metadataFilters)
                .GroupBy(static row => row.PatternId, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new PatternListRow(
                    group.Key,
                    Label: group.Key,
                    Languages: group.Select(static row => row.Language).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                    Captures: group.Select(static row => row.CaptureName).Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
                    Count: group.LongCount(),
                    Catalog: "observed"))
                .ToArray();
        }

        return new PatternToolResult(json ? RenderListJson(rows) : RenderListCompact(rows), rows.Count);
    }

    private static PatternToolResult Summary(
        PatternFactsReader reader,
        string dbPath,
        string? patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        PatternSummaryGroupBy groupBy,
        string? facet,
        bool json)
    {
        IReadOnlyList<PatternSummaryRow> rows = reader.Summary(
            dbPath,
            patternId,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters,
            groupBy,
            facet);

        return new PatternToolResult(json ? RenderSummaryJson(rows, groupBy, facet) : RenderSummaryCompact(rows, groupBy, facet), rows.Count);
    }

    private static PatternToolResult Search(
        PatternFactsReader reader,
        string dbPath,
        string patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        PatternMatchRow[] rows = reader.Search(
            dbPath,
            patternId,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters,
            boundedLimit).ToArray();

        bool patternExists = rows.Length > 0
            || reader.EnumerateMatches(dbPath, patternId, language: null, pathGlob: null, metadataFilters: null, limit: 1).Any();
        bool filteredOut = rows.Length == 0
            && patternExists
            && (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(language) || metadataFilters.Count > 0);
        IReadOnlyList<string> suggestions = rows.Length == 0 && !filteredOut
            ? SuggestPatternIds(reader, dbPath, patternId, language)
            : [];
        string emptyReason = PatternEmptyReason(
            hasPatternId: true,
            patternExists,
            filteredOut,
            metadataFilters.Count > 0,
            querySearch: false);

        return new PatternToolResult(
            json
                ? RenderSearchJson(
                    patternId,
                    rows,
                    suggestions,
                    filteredOut,
                    path,
                    language,
                    metadataFilters,
                    emptyReason)
                : RenderSearchCompact(
                    patternId,
                    rows,
                    metadataFilters.Count == 1 ? metadataFilters[0] : null,
                    suggestions,
                    filteredOut,
                    path,
                    language,
                    metadataFilters.Count == 1 ? metadataFilters[0] : null),
            rows.Length,
            emptyReason);
    }

    private static PatternToolResult SearchByQuery(
        PatternFactsReader reader,
        string dbPath,
        string query,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        string[] matchedPatternIds = reader.List(dbPath)
            .Where(row => row.PatternId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static row => row.Count)
            .ThenBy(static row => row.PatternId, StringComparer.Ordinal)
            .Take(MaxQueryPatternIds)
            .Select(static row => row.PatternId)
            .ToArray();

        if (matchedPatternIds.Length == 0)
        {
            IReadOnlyList<string> nearMatches = SuggestPatternIds(reader, dbPath, query, language);
            const string emptyReason = "query_no_match";
            if (json)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (Utf8JsonWriter writer = NewWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema_version", 1);
                    writer.WriteString("operation", "search");
                    writer.WriteString("query", query);
                    writer.WriteString("empty_reason", emptyReason);
                    WriteStringArray(writer, "matched_pattern_ids", Array.Empty<string>());
                    WriteStringArray(writer, "near_matches", nearMatches);
                    WriteActiveFiltersJson(writer, path, language, metadataFilters);
                    writer.WriteStartArray("matches");
                    writer.WriteEndArray();
                    writer.WriteString("note", $"No patterns match '{query}'. Try `patterns operation=list` to see observed pattern_id values.");
                    writer.WritePropertyName("next_actions");
                    WriteNextActions(writer, QueryNoMatchNextActions(nearMatches));
                    writer.WriteEndObject();
                }
                return new PatternToolResult(Encoding.UTF8.GetString(buffer.WrittenSpan), 0, emptyReason);
            }

            string hint = RenderQueryNoMatchCompact(query, nearMatches);
            return new PatternToolResult(hint, 0, emptyReason);
        }

        var combined = new List<PatternMatchRow>(capacity: Math.Min(boundedLimit * matchedPatternIds.Length, MaxLimit * MaxQueryPatternIds));
        foreach (PatternMatchRow row in reader.EnumerateMatches(
                     dbPath,
                     matchedPatternIds,
                     language,
                     path,
                     metadataFilters.Count == 0 ? null : metadataFilters,
                     boundedLimit))
        {
            combined.Add(row);
        }

        PatternMatchRow[] rows = combined
            .OrderBy(static row => row.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.Span.StartByte)
            .ThenBy(static row => row.FactId, StringComparer.Ordinal)
            .Take(boundedLimit)
            .ToArray();
        bool hasActiveFilters = !string.IsNullOrWhiteSpace(path)
            || !string.IsNullOrWhiteSpace(language)
            || metadataFilters.Count > 0;
        string? resultEmptyReason = rows.Length == 0
            ? hasActiveFilters ? "filtered_out" : "no_facts"
            : null;

        return new PatternToolResult(
            json
                ? RenderSearchJsonForQuery(query, matchedPatternIds, rows, path, language, metadataFilters, resultEmptyReason)
                : RenderSearchCompactForQuery(query, matchedPatternIds, rows, metadataFilters.Count == 1 ? metadataFilters[0] : null),
            rows.Length,
            resultEmptyReason);
    }

    private static string RenderListCompact(IReadOnlyList<PatternListRow> rows)
    {
        if (rows.Count == 0)
            return "No patterns.";

        var sb = new StringBuilder();
        sb.AppendLine("# patterns");
        sb.AppendLine("pattern_id\tcount\tlanguages\tcaptures");
        foreach (PatternListRow row in rows)
        {
            sb.Append(row.PatternId).Append('\t')
              .Append(row.Count).Append('\t')
              .Append(string.Join(",", row.Languages)).Append('\t')
              .Append(string.Join(",", row.Captures))
              .AppendLine();
        }
        AppendNextActions(sb, ListNextActions(rows));
        return sb.ToString().TrimEnd();
    }

    private static string RenderSummaryCompact(
        IReadOnlyList<PatternSummaryRow> rows,
        PatternSummaryGroupBy groupBy,
        string? facet)
    {
        if (rows.Count == 0)
            return "No pattern groups.";

        var sb = new StringBuilder();
        sb.AppendLine("# patterns summary");
        if (groupBy != PatternSummaryGroupBy.LanguagePatternCapture)
            sb.Append("group_by=").Append(groupBy.ToString().ToLowerInvariant()).AppendLine();
        if (!string.IsNullOrWhiteSpace(facet))
            sb.Append("facet=").Append(facet.Trim()).AppendLine();

        if (groupBy == PatternSummaryGroupBy.File)
            sb.AppendLine("language\tpattern_id\tcapture\tpath\tcount");
        else if (groupBy == PatternSummaryGroupBy.Directory)
            sb.AppendLine("language\tpattern_id\tcapture\tdirectory\tcount");
        else if (!string.IsNullOrWhiteSpace(facet))
            sb.AppendLine("language\tpattern_id\tcapture\tfacet\tcount");
        else
            sb.AppendLine("language\tpattern_id\tcapture\tcount");

        foreach (PatternSummaryRow row in rows)
        {
            sb.Append(row.Language).Append('\t')
              .Append(row.PatternId).Append('\t')
              .Append(row.CaptureName).Append('\t');
            if (groupBy == PatternSummaryGroupBy.File)
                sb.Append(row.Path).Append('\t');
            else if (groupBy == PatternSummaryGroupBy.Directory)
                sb.Append(row.Directory).Append('\t');
            else if (!string.IsNullOrWhiteSpace(facet))
                sb.Append(row.FacetValue).Append('\t');
            sb.Append(row.Count)
              .AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderSearchCompact(
        string patternId,
        IReadOnlyList<PatternMatchRow> rows,
        PatternMetadataFilter? metadataFilter,
        IReadOnlyList<string> suggestions,
        bool filteredOut,
        string? path,
        string? language,
        PatternMetadataFilter? activeMetadataFilter)
    {
        if (rows.Count == 0)
        {
            if (filteredOut)
                return RenderFilteredOutCompact(patternId, path, language, activeMetadataFilter);
            if (suggestions.Count == 0)
                return $"No matches for {patternId}.";

            var empty = new StringBuilder();
            empty.Append("No matches for ").Append(patternId).AppendLine(".");
            empty.Append("Suggestions: ").Append(string.Join(", ", suggestions));
            return empty.ToString();
        }

        var sb = new StringBuilder();
        sb.Append("# patterns search ").AppendLine(patternId);
        AppendMatchGroups(sb, rows, metadataFilter);
        return sb.ToString().TrimEnd();
    }

    private static string RenderSearchCompactForQuery(
        string query,
        IReadOnlyList<string> matchedPatternIds,
        IReadOnlyList<PatternMatchRow> rows,
        PatternMetadataFilter? metadataFilter)
    {
        var sb = new StringBuilder();
        sb.Append("# patterns search query='").Append(query).AppendLine("'");
        sb.Append("matched_pattern_ids: ").Append(string.Join(", ", matchedPatternIds)).AppendLine();
        AppendMatchGroups(sb, rows, metadataFilter);
        return sb.ToString().TrimEnd();
    }

    private static string RenderQueryNoMatchCompact(string query, IReadOnlyList<string> nearMatches)
    {
        var sb = new StringBuilder();
        sb.Append("No patterns match '").Append(query).AppendLine("'. Try `patterns operation=list` to see observed pattern_id values.");
        if (nearMatches.Count > 0)
            sb.Append("near matches: ").AppendLine(string.Join(", ", nearMatches));
        AppendNextActions(sb, QueryNoMatchNextActions(nearMatches));
        return sb.ToString().TrimEnd();
    }

    private static string RenderFilteredOutCompact(
        string patternId,
        string? path,
        string? language,
        PatternMetadataFilter? metadataFilter)
    {
        string filters = ActiveFiltersCompact(path, language, metadataFilter);
        var sb = new StringBuilder();
        sb.Append("No matches for ").Append(patternId).Append(" after filters");
        if (!string.IsNullOrWhiteSpace(filters))
            sb.Append(": ").Append(filters);
        sb.AppendLine(".");
        sb.Append("Try again with this pattern_id and loosen language, path, or where.");
        AppendNextActions(sb, PatternIdRecoveryNextActions(patternId));
        return sb.ToString().TrimEnd();
    }

    private static string ActiveFiltersCompact(string? path, string? language, PatternMetadataFilter? metadataFilter)
    {
        var filters = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(language))
            filters.Add("language=" + language.Trim());
        if (!string.IsNullOrWhiteSpace(path))
            filters.Add("path=" + path.Trim());
        if (metadataFilter is not null)
            filters.Add("where=" + metadataFilter.Key + "=" + metadataFilter.Value);
        return string.Join(", ", filters);
    }

    private static IReadOnlyList<PatternNextAction> ListNextActions(IReadOnlyList<PatternListRow> rows)
    {
        if (rows.Count == 0)
            return [];

        PatternListRow top = rows
            .OrderByDescending(static row => row.Count)
            .ThenBy(static row => row.PatternId, StringComparer.Ordinal)
            .First();
        var actions = new List<PatternNextAction>
        {
            NextAction(
                "patterns",
                "search concrete facts for an observed pattern_id",
                ("operation", "search"),
                ("pattern_id", top.PatternId)),
            NextAction(
                "patterns",
                "summarize files and languages for an observed pattern_id",
                ("operation", "summary"),
                ("pattern_id", top.PatternId)),
        };

        string? query = DomainQueryFromPatternIds(rows);
        if (query is not null)
        {
            actions.Add(NextAction(
                "patterns",
                "search across observed pattern_id values by domain term",
                ("operation", "search"),
                ("query", query)));
        }

        return actions;
    }

    private static string? DomainQueryFromPatternIds(IReadOnlyList<PatternListRow> rows)
    {
        string[] domainTerms = ["route", "html", "json", "yaml", "markdown"];
        foreach (string term in domainTerms)
        {
            if (rows.Any(row => row.PatternId.Contains(term, StringComparison.OrdinalIgnoreCase)))
                return term;
        }

        return null;
    }

    private static IReadOnlyList<PatternNextAction> QueryNoMatchNextActions(IReadOnlyList<string> nearMatches)
    {
        var actions = new List<PatternNextAction>
        {
            NextAction(
                "patterns",
                "list observed pattern_id values before searching",
                ("operation", "list")),
        };
        if (nearMatches.Count > 0)
        {
            string patternId = nearMatches[0];
            actions.Add(NextAction(
                "patterns",
                "search the closest observed pattern_id",
                ("operation", "search"),
                ("pattern_id", patternId)));
            actions.Add(NextAction(
                "patterns",
                "summarize the closest observed pattern_id",
                ("operation", "summary"),
                ("pattern_id", patternId)));
        }

        return actions;
    }

    private static IReadOnlyList<PatternNextAction> PatternIdRecoveryNextActions(string patternId) =>
    [
        NextAction(
            "patterns",
            "retry without filters to check whether the pattern_id has facts",
            ("operation", "search"),
            ("pattern_id", patternId)),
        NextAction(
            "patterns",
            "summarize where this pattern_id appears before reapplying filters",
            ("operation", "summary"),
            ("pattern_id", patternId)),
    ];

    private static PatternNextAction NextAction(string tool, string reason, params (string Key, string Value)[] args) =>
        new(tool, reason, args.Select(static arg => new KeyValuePair<string, string>(arg.Key, arg.Value)).ToArray());

    private static void AppendNextActions(StringBuilder sb, IReadOnlyList<PatternNextAction> actions)
    {
        if (actions.Count == 0)
            return;

        sb.Append("Next:");
        foreach (PatternNextAction action in actions.Take(4))
        {
            sb.Append('\n')
              .Append("  ")
              .Append(FormatPatternActionCommand(action))
              .Append(" - ")
              .Append(action.Reason)
              .Append('.');
        }
        sb.AppendLine();
    }

    private static string FormatPatternActionCommand(PatternNextAction action)
    {
        var sb = new StringBuilder(action.Tool);
        foreach (KeyValuePair<string, string> arg in action.Args)
            sb.Append(' ').Append(arg.Key).Append('=').Append(arg.Value);
        return sb.ToString();
    }

    private static void WriteNextActions(Utf8JsonWriter writer, IReadOnlyList<PatternNextAction> actions)
    {
        writer.WriteStartArray();
        foreach (PatternNextAction action in actions.Take(4))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", action.Tool);
            writer.WriteString("reason", action.Reason);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> arg in action.Args)
                writer.WriteString(arg.Key, arg.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void AppendMatchGroups(StringBuilder sb, IReadOnlyList<PatternMatchRow> rows, PatternMetadataFilter? metadataFilter)
    {
        foreach (IGrouping<string, PatternMatchRow> group in rows.GroupBy(static row => row.Path, StringComparer.Ordinal))
        {
            sb.AppendLine(group.Key);
            foreach (PatternMatchRow row in group)
            {
                sb.Append("  L")
                  .Append(row.Span.StartLine).Append(':').Append(row.Span.StartColumn)
                  .Append(' ')
                  .Append(row.CaptureName)
                  .Append(' ')
                  .Append(row.PatternId);

                string metadata = MetadataCompact(row, metadataFilter);
                if (metadata.Length > 0)
                    sb.Append(" metadata=").Append(metadata);

                sb.AppendLine();
            }
        }
    }

    private static string RenderListJson(IReadOnlyList<PatternListRow> rows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "list");
            writer.WriteStartArray("patterns");
            foreach (PatternListRow row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("pattern_id", row.PatternId);
                writer.WriteString("label", row.Label);
                writer.WriteNumber("count", row.Count);
                writer.WriteString("catalog", row.Catalog);
                if (row.Description is not null)
                    writer.WriteString("description", row.Description);
                if (row.Tags is { Count: > 0 })
                    WriteStringArray(writer, "tags", row.Tags);
                if (row.ExpectedMetadataKeys is { Count: > 0 })
                    WriteStringArray(writer, "expected_metadata_keys", row.ExpectedMetadataKeys);
                WriteStringArray(writer, "languages", row.Languages);
                WriteStringArray(writer, "captures", row.Captures);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("next_actions");
            WriteNextActions(writer, ListNextActions(rows));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSummaryJson(
        IReadOnlyList<PatternSummaryRow> rows,
        PatternSummaryGroupBy groupBy,
        string? facet)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "summary");
            if (groupBy != PatternSummaryGroupBy.LanguagePatternCapture)
                writer.WriteString("group_by", groupBy.ToString().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(facet))
                writer.WriteString("facet", facet.Trim());
            writer.WriteStartArray("groups");
            foreach (PatternSummaryRow row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("language", row.Language);
                writer.WriteString("pattern_id", row.PatternId);
                writer.WriteString("capture_name", row.CaptureName);
                if (row.Path is not null)
                    writer.WriteString("path", row.Path);
                if (row.Directory is not null)
                    writer.WriteString("directory", row.Directory);
                if (row.FacetValue is not null)
                    writer.WriteString("facet_value", row.FacetValue);
                writer.WriteNumber("count", row.Count);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSearchJson(
        string patternId,
        IReadOnlyList<PatternMatchRow> rows,
        IReadOnlyList<string> suggestions,
        bool filteredOut,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        string? emptyReason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "search");
            writer.WriteString("pattern_id", patternId);
            if (rows.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(emptyReason))
                    writer.WriteString("empty_reason", emptyReason);
                WriteActiveFiltersJson(writer, path, language, metadataFilters);
                WriteStringArray(writer, "near_matches", suggestions);
                if (filteredOut)
                {
                    writer.WriteString("note", "Pattern exists but active filters removed every row.");
                    writer.WritePropertyName("next_actions");
                    WriteNextActions(writer, PatternIdRecoveryNextActions(patternId));
                }
                else if (suggestions.Count > 0)
                {
                    writer.WriteString("note", $"No matches for {patternId}.");
                }
            }
            writer.WriteStartArray("matches");
            foreach (PatternMatchRow row in rows)
                WriteMatchJson(writer, row);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSearchJsonForQuery(
        string query,
        IReadOnlyList<string> matchedPatternIds,
        IReadOnlyList<PatternMatchRow> rows,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        string? emptyReason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("operation", "search");
            writer.WriteString("query", query);
            WriteStringArray(writer, "matched_pattern_ids", matchedPatternIds);
            if (rows.Count == 0 && !string.IsNullOrWhiteSpace(emptyReason))
            {
                writer.WriteString("empty_reason", emptyReason);
                writer.WriteString("note", emptyReason == "filtered_out"
                    ? "Patterns matched the query but active filters removed every row."
                    : $"No matches for query '{query}'.");
            }
            WriteActiveFiltersJson(writer, path, language, metadataFilters);
            writer.WriteStartArray("matches");
            foreach (PatternMatchRow row in rows)
            {
                WriteMatchJson(writer, row);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMatchJson(Utf8JsonWriter writer, PatternMatchRow row)
    {
        writer.WriteStartObject();
        writer.WriteString("fact_id", row.FactId);
        writer.WriteString("pattern_id", row.PatternId);
        writer.WriteString("language", row.Language);
        writer.WriteString("path", row.Path);
        writer.WriteString("capture_name", row.CaptureName);
        writer.WriteString("node_kind", row.NodeKind);
        if (row.ContainingSymbolId is null) writer.WriteNull("containing_symbol_id"); else writer.WriteString("containing_symbol_id", row.ContainingSymbolId);
        writer.WriteNumber("confidence", row.Confidence);

        writer.WriteStartObject("span");
        writer.WriteNumber("start_line", row.Span.StartLine);
        writer.WriteNumber("start_column", row.Span.StartColumn);
        writer.WriteNumber("end_line", row.Span.EndLine);
        writer.WriteNumber("end_column", row.Span.EndColumn);
        writer.WriteNumber("start_byte", row.Span.StartByte);
        writer.WriteNumber("end_byte", row.Span.EndByte);
        writer.WriteEndObject();

        if (row.Metadata.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName("metadata");
            row.Metadata.WriteTo(writer);
        }
        if (row.MetadataError is not null)
            writer.WriteString("metadata_error", row.MetadataError);

        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string MetadataCompact(PatternMatchRow row, PatternMetadataFilter? metadataFilter)
    {
        if (row.MetadataError is not null)
            return "error";
        if (row.Metadata.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var selected = new List<(string Name, JsonElement Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (metadataFilter is not null)
            Add(metadataFilter.Key);
        foreach (string key in MetadataPriority)
            Add(key);
        foreach (JsonProperty property in row.Metadata.EnumerateObject())
        {
            if (selected.Count >= 4)
                break;
            if (seen.Add(property.Name))
                selected.Add((property.Name, property.Value));
        }

        return string.Join(
            ",",
            selected.Select(static property => property.Name + "=" + MetadataValueCompact(property.Value)));

        void Add(string key)
        {
            if (selected.Count >= 4 || !seen.Add(key))
                return;
            if (row.Metadata.TryGetProperty(key, out JsonElement value))
                selected.Add((key, value));
        }
    }

    private static IReadOnlyList<string> SuggestPatternIds(
        PatternFactsReader reader,
        string dbPath,
        string patternId,
        string? language)
    {
        string[] queryTokens = PatternTokens(patternId);
        if (queryTokens.Length == 0)
            return [];

        return reader.List(dbPath, language)
            .Select(row => (row.PatternId, Score: PatternSuggestionScore(queryTokens, row.PatternId)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.PatternId, StringComparer.Ordinal)
            .Take(5)
            .Select(static candidate => candidate.PatternId)
            .ToArray();
    }

    private static int PatternSuggestionScore(string[] queryTokens, string candidate)
    {
        string[] candidateTokens = PatternTokens(candidate);
        if (candidateTokens.Length == 0)
            return 0;

        int exactOverlap = queryTokens.Count(token => candidateTokens.Contains(token, StringComparer.Ordinal));
        int nearOverlap = queryTokens.Count(token =>
            !candidateTokens.Contains(token, StringComparer.Ordinal)
            && candidateTokens.Any(candidateToken => TokensAreNear(token, candidateToken)));
        int overlap = exactOverlap + nearOverlap;
        int requiredOverlap = queryTokens.Length == 1 ? 1 : 2;
        if (overlap < requiredOverlap)
            return 0;

        int score = (exactOverlap * 10) + (nearOverlap * 6);
        if (string.Equals(queryTokens.LastOrDefault(), candidateTokens.LastOrDefault(), StringComparison.Ordinal))
            score += 2;
        if (candidate.Contains(queryTokens[0], StringComparison.OrdinalIgnoreCase))
            score += 1;
        return score;
    }

    private static bool TokensAreNear(string queryToken, string candidateToken)
    {
        if (queryToken.Length < 4 || candidateToken.Length < 4)
            return false;
        if (candidateToken.Contains(queryToken, StringComparison.OrdinalIgnoreCase)
            || queryToken.Contains(candidateToken, StringComparison.OrdinalIgnoreCase))
            return true;

        return EditDistanceAtMost(queryToken, candidateToken, maxDistance: 2);
    }

    private static bool EditDistanceAtMost(string left, string right, int maxDistance)
    {
        if (Math.Abs(left.Length - right.Length) > maxDistance)
            return false;

        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
                return false;

            Span<int> temp = previous;
            previous = current;
            current = temp;
        }

        return previous[right.Length] <= maxDistance;
    }

    private static string[] PatternTokens(string patternId) =>
        patternId.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => token.ToLowerInvariant())
            .ToArray();

    private static string MetadataValueCompact(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static string NormalizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return "list";

        string normalized = operation.Trim().ToLowerInvariant();
        return normalized switch
        {
            "summarize" => "summary",
            _ => normalized,
        };
    }

    private static string RequiredPatternId(string? patternId)
    {
        if (string.IsNullOrWhiteSpace(patternId))
            throw new InvalidOperationException("patterns search requires pattern_id.");

        return patternId.Trim();
    }

    private static string TargetForTelemetry(string? operation, string? patternId, string? query)
    {
        string op = NormalizeOperation(operation);
        if (!string.IsNullOrWhiteSpace(patternId))
            return op + " " + patternId.Trim();
        if (!string.IsNullOrWhiteSpace(query))
            return op + " query=" + query.Trim();
        return op;
    }

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static void WriteActiveFiltersJson(
        Utf8JsonWriter writer,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        if (string.IsNullOrWhiteSpace(path)
            && string.IsNullOrWhiteSpace(language)
            && metadataFilters.Count == 0)
            return;

        writer.WriteStartObject("active_filters");
        if (!string.IsNullOrWhiteSpace(language))
            writer.WriteString("language", language.Trim());
        if (!string.IsNullOrWhiteSpace(path))
            writer.WriteString("path", path.Trim());
        if (metadataFilters.Count > 0)
        {
            writer.WriteStartArray("where");
            foreach (PatternMetadataFilter filter in metadataFilters)
            {
                writer.WriteStartObject();
                writer.WriteString("key", filter.Key);
                writer.WriteString("value", filter.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static string PatternEmptyReason(
        bool hasPatternId,
        bool patternExists,
        bool filteredOut,
        bool hasMetadataFilter,
        bool querySearch)
    {
        if (querySearch)
            return "query_no_match";
        if (filteredOut)
            return "filtered_out";
        if (hasPatternId && !patternExists)
            return "no_such_pattern_id";
        if (hasMetadataFilter)
            return "no_metadata_match";
        return "no_facts";
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

internal readonly record struct PatternToolResult(string Output, int ResultCount, string? EmptyReason = null);
