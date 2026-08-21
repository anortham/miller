using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

[McpServerToolType]
public sealed class PatternsTool
{
    public const int JsonSchemaVersion = 2;
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxQueryPatternIds = 25;
    public const int MaxMetadataFilters = 16;
    private const int MinCompactCollectionRowBytes = 5;
    private const int MinJsonCollectionRowBytes = 48;
    private const int MaxPatternIdEncodedBytes = 512;
    private const int MaxQueryEncodedBytes = 1_000;
    private const int MaxLanguageEncodedBytes = 128;
    private const int MaxPathEncodedBytes = 2_048;
    private const int MaxWhereEncodedBytes = 2_048;
    private const int MaxFacetEncodedBytes = 256;
    private static readonly JavaScriptEncoder PatternJsonEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
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

    private readonly record struct PatternReadTarget(string? DbPath, IWorkspaceReadSession? Session)
    {
        public static PatternReadTarget ForPath(string dbPath) => new(dbPath, null);
        public static PatternReadTarget ForSession(IWorkspaceReadSession session) => new(null, session);

        public IReadOnlyList<PatternListRow> List(
            PatternFactsReader reader,
            string? patternId,
            string? language,
            string? path,
            IReadOnlyList<PatternMetadataFilter>? filters) =>
            Session is null
                ? reader.List(DbPath!, patternId, language, path, filters)
                : reader.List(Session, patternId, language, path, filters);

        public IReadOnlyList<PatternSummaryRow> Summary(
            PatternFactsReader reader,
            string? patternId,
            string? language,
            string? path,
            IReadOnlyList<PatternMetadataFilter>? filters,
            PatternSummaryGroupBy groupBy,
            string? facet) =>
            Session is null
                ? reader.Summary(DbPath!, patternId, language, path, filters, groupBy, facet)
                : reader.Summary(Session, patternId, language, path, filters, groupBy, facet);

        public PatternExactSearchPageResult SearchExact(
            PatternFactsReader reader,
            string patternId,
            string? language,
            string? path,
            IReadOnlyList<PatternMetadataFilter>? filters,
            int offset,
            int limit) =>
            Session is null
                ? reader.SearchExactPageWithContext(DbPath!, patternId, language, path, filters, offset, limit)
                : reader.SearchExactPageWithContext(Session, patternId, language, path, filters, offset, limit);

        public PatternQueryMatchPageResult SearchQuery(
            PatternFactsReader reader,
            string query,
            string? language,
            string? path,
            IReadOnlyList<PatternMetadataFilter>? filters,
            int offset,
            int limit,
            int maxPatternIds) =>
            Session is null
                ? reader.SearchByQueryPageWithCount(
                    DbPath!, query, language, path, filters, offset, limit, maxPatternIds)
                : reader.SearchByQueryPageWithCount(
                    Session, query, language, path, filters, offset, limit, maxPatternIds);

        public string IndexLevel() => Session is null
            ? ExtractIndexLevelReader.Read(DbPath)
            : Session.Read(ExtractIndexLevelReader.Read);
    }

    private sealed record PatternNextAction(
        string Tool,
        string Reason,
        IReadOnlyList<KeyValuePair<string, string>> Args);

    private sealed record PatternQueryFanout(
        int ConsideredCount,
        int MatchedCount,
        IReadOnlyList<string> ReturnedPatternIds)
    {
        public int ReturnedCount => ReturnedPatternIds.Count;
        public int OmittedCount => MatchedCount - ReturnedCount;
        public bool Truncated => OmittedCount > 0;
    }

    public PatternsTool(IWorkspaceArtifactProvider workspaceProvider, PatternFactsReader reader)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(reader);
        _workspaceProvider = workspaceProvider;
        _reader = reader;
    }

    [McpServerTool(Name = "patterns")]
    [Description(
        "Query generic code-shape facts pre-extracted by julie-extractors, including HTTP routes, HTML/htmx/Alpine, " +
        "SQL DDL/DML, async/await, and JSON/YAML/TOML/Markdown structure. Call with no args to list " +
        "observed pattern_id values; then operation=search with pattern_id (plus path/language/where filters) or " +
        "a free-text query that matches pattern ids. Use INSTEAD of raw-grepping routes, config keys, or document " +
        "structure. NOT for: raw AST queries or arbitrary text (search). Examples: patterns operation=search " +
        "pattern_id=aspnet.minimal_api.route.v1; patterns operation=search query=route.")]
    public string Patterns(
        [Description("list|summary|search. Default list.")] string? operation = "list",
        [Description("Pattern id, max 512 encoded bytes. Required for search unless query is given; optional for summary/list. Example: htmx.attribute.v1.")] string? pattern_id = null,
        [Description("Free-text search over observed pattern IDs, max 1,000 encoded bytes. Reports exact fan-out counts and searches at most 25 matched IDs. Example: route.")] string? query = null,
        [Description("Language filter such as csharp, html, or razor. Optional.")] string? language = null,
        [Description("Workspace-relative glob filter, e.g. Views/**. Optional.")] string? path = null,
        [Description("Top-level metadata equality filter as key=value. Accepts up to 16 semicolon-separated AND filters; repeat --where on CLI. Requires pattern_id or query for search.")] string? where = null,
        [Description("summary grouping: language_pattern_capture|file|directory|top_directory. Default language_pattern_capture.")] string? group_by = null,
        [Description("Optional summary metadata facet key using letters, digits, underscore, or hyphen.")] string? facet = null,
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Wait for a refresh before reading. With workspace_id the default now serves the pinned index immediately and refreshes in the background; true still waits, false does zero refresh work.")] bool? ensure_fresh = null,
        [Description("Max search results. Default 50, maximum 500.")] int limit = DefaultLimit,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Stateless continuation returned by a prior page of the same request.")] string? continuation = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_format",
                    "patterns format must be compact or json."));
            }
            WorkspaceRefreshMode refresh = ReadToolWorkspaceRouting.ResolveRefreshMode(workspace_id, ensure_fresh);
            using WorkspaceArtifactContext context = _workspaceProvider.ResolveArtifact(workspace_id, refresh);
            string? banner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            int outputBudget = ToolOutputBudget.PatternsMcpMaxBytes
                - ToolOutputBudget.PatternsMcpDiagnosticReserveBytes;
            if (banner is not null)
                outputBudget -= Encoding.UTF8.GetByteCount(banner) + 1;

            PatternToolResult result = Run(
                _reader,
                PatternReadTarget.ForSession(context.ReadSession),
                operation,
                pattern_id,
                query,
                language,
                path,
                where,
                group_by,
                facet,
                limit,
                json,
                outputBudget,
                context.WorkspaceId ?? "current",
                continuation,
                telemetry);
            ToolDiagnostic? diagnostic = result.LevelDiagnostic;
            if (diagnostic is null && result.ResultCount == 0)
            {
                diagnostic = ToolDiagnostic.ExpectedEmpty(
                    result.EmptyReason ?? "no_facts",
                    "No structural facts matched the request.",
                    [new ToolDiagnosticAction(
                        "patterns(operation=\"list\")",
                        "list pattern ids observed in this workspace")]);
            }

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = NormalizeOperation(operation);
                telemetry.SetTarget(TargetForTelemetry(operation, pattern_id, query));
                telemetry.ResultCount = result.ResultCount;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("has_pattern_id", !string.IsNullOrWhiteSpace(pattern_id));
                telemetry.SetMetadata("has_query", !string.IsNullOrWhiteSpace(query));
                telemetry.SetMetadata("has_language", !string.IsNullOrWhiteSpace(language));
                telemetry.SetMetadata("has_path", !string.IsNullOrWhiteSpace(path));
                telemetry.SetMetadata("has_where", !string.IsNullOrWhiteSpace(where));
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
            }

            string output = ReadToolWorkspaceRouting.PrefixCompact(result.Output, banner);
            if (diagnostic is not null)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "patterns",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            }
            return ToolOutputBudget.RequireWithinByteBudget(
                output,
                ToolOutputBudget.PatternsMcpMaxBytes);
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render("patterns", diagnostic, json, telemetry);
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
        bool json,
        int? outputByteBudget = null,
        string workspaceId = "current",
        string? continuation = null,
        TelemetryScope? telemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return Run(
            reader,
            PatternReadTarget.ForPath(dbPath),
            operation,
            patternId,
            query,
            language,
            path,
            where,
            groupBy,
            facet,
            limit,
            json,
            outputByteBudget,
            workspaceId,
            continuation,
            telemetry);
    }

    internal static PatternToolResult Run(
        PatternFactsReader reader,
        IWorkspaceReadSession session,
        string? operation,
        string? patternId,
        string? query,
        string? language,
        string? path,
        string? where,
        string? groupBy,
        string? facet,
        int limit,
        bool json,
        int? outputByteBudget = null,
        string workspaceId = "current",
        string? continuation = null,
        TelemetryScope? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Run(
            reader,
            PatternReadTarget.ForSession(session),
            operation,
            patternId,
            query,
            language,
            path,
            where,
            groupBy,
            facet,
            limit,
            json,
            outputByteBudget,
            workspaceId,
            continuation,
            telemetry);
    }

    private static PatternToolResult Run(
        PatternFactsReader reader,
        PatternReadTarget target,
        string? operation,
        string? patternId,
        string? query,
        string? language,
        string? path,
        string? where,
        string? groupBy,
        string? facet,
        int limit,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string? continuation,
        TelemetryScope? telemetry)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string op = NormalizeOperation(operation);
        ValidateInputLength(patternId, "pattern_id", MaxPatternIdEncodedBytes);
        ValidateInputLength(query, "query", MaxQueryEncodedBytes);
        ValidateInputLength(language, "language", MaxLanguageEncodedBytes);
        ValidateInputLength(path, "path", MaxPathEncodedBytes);
        ValidateInputLength(where, "where", MaxWhereEncodedBytes);
        ValidateInputLength(facet, "facet", MaxFacetEncodedBytes);
        ValidateFacet(facet);
        if ((op is "list" or "summary") && !string.IsNullOrWhiteSpace(query))
            throw InvalidRequest("patterns query is only supported for search.");

        IReadOnlyList<PatternMetadataFilter> metadataFilters = ParseWhereFilters(where);
        if (op == "search"
            && metadataFilters.Count > 0
            && string.IsNullOrWhiteSpace(patternId)
            && string.IsNullOrWhiteSpace(query))
            throw InvalidRequest("patterns where requires pattern_id or query.");

        PatternSummaryGroupBy summaryGroupBy = ParseSummaryGroupBy(groupBy);
        string requestFingerprint = FingerprintFields(
            op,
            patternId?.Trim(),
            query?.Trim(),
            language?.Trim(),
            path?.Trim(),
            string.Join(';', metadataFilters.Select(static filter => filter.Key + "=" + filter.Value)),
            SummaryGroupByName(summaryGroupBy),
            facet?.Trim(),
            Math.Clamp(limit, 1, MaxLimit).ToString(CultureInfo.InvariantCulture),
            json ? "json" : "compact");

        PatternToolResult result = op switch
        {
            "list" => List(
                reader, target, patternId, language, path, metadataFilters, json, outputByteBudget,
                workspaceId, requestFingerprint, continuation),
            "summary" => Summary(
                reader,
                target,
                patternId,
                language,
                path,
                metadataFilters,
                summaryGroupBy,
                facet,
                json,
                outputByteBudget,
                workspaceId,
                requestFingerprint,
                continuation),
            "search" => SearchDispatch(
                reader,
                target,
                patternId,
                query,
                language,
                path,
                metadataFilters,
                limit,
                json,
                outputByteBudget,
                workspaceId,
                requestFingerprint,
                continuation),
            _ => throw InvalidRequest("patterns operation must be list, summary, or search."),
        };

        return result with
        {
            LevelDiagnostic = FactsLevelDiagnostic(target.IndexLevel(), telemetry),
        };
    }

    /// <summary>
    /// The patterns surface's index-level decision. It lives inside <see cref="Run"/> rather than in the
    /// <c>[McpServerTool]</c> wrapper because the CLI verbs call <see cref="Run"/> directly: while the check sat
    /// in the wrapper, `miller patterns` served a symbols-level workspace a clean, empty, authoritative-looking
    /// answer about the <c>structural_facts</c> table nobody had extracted yet. Returns null — the guard is
    /// inert — for every level but symbols, so full-level output stays byte-identical.
    /// </summary>
    internal static ToolDiagnostic? FactsLevelDiagnostic(string? indexLevel, TelemetryScope? telemetry = null)
    {
        if (!IndexLevelGuard.IsSymbolsLevel(indexLevel))
            return null;

        IndexLevelGuard.MarkDegraded(telemetry, "facts_layer_converging");
        return IndexLevelGuard.Converging(
            "structural facts have not been extracted yet, so pattern results are empty.");
    }

    private static void ValidateInputLength(string? value, string parameterName, int maxEncodedBytes)
    {
        if (string.IsNullOrEmpty(value))
            return;

        int encodedBytes = JsonEncodedText.Encode(value.AsSpan(), PatternJsonEncoder).EncodedUtf8Bytes.Length;
        if (encodedBytes > maxEncodedBytes)
        {
            throw InvalidRequest(
                $"patterns {parameterName} exceeds the {maxEncodedBytes}-byte encoded input limit.");
        }
    }

    private static void ValidateFacet(string? facet)
    {
        if (string.IsNullOrWhiteSpace(facet))
            return;

        try
        {
            new PatternMetadataFilter(facet, string.Empty).Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw InvalidRequest(
                "patterns facet key must contain only letters, digits, underscore, or hyphen.");
        }
    }

    private static PatternToolResult SearchDispatch(
        PatternFactsReader reader,
        PatternReadTarget target,
        string? patternId,
        string? query,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        if (!string.IsNullOrWhiteSpace(patternId))
            return Search(
                reader,
                target,
                RequiredPatternId(patternId),
                language,
                path,
                metadataFilters,
                limit,
                json,
                outputByteBudget,
                workspaceId,
                requestFingerprint,
                continuation);

        if (!string.IsNullOrWhiteSpace(query))
            return SearchByQuery(
                reader,
                target,
                query.Trim(),
                language,
                path,
                metadataFilters,
                limit,
                json,
                outputByteBudget,
                workspaceId,
                requestFingerprint,
                continuation);

        throw InvalidRequest("patterns search requires pattern_id or query.");
    }

    internal static IReadOnlyList<PatternMetadataFilter> ParseWhereFilters(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return Array.Empty<PatternMetadataFilter>();

        PatternMetadataFilter[] filters = where
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSingleWhere)
            .ToArray();
        if (filters.Length > MaxMetadataFilters)
        {
            throw InvalidRequest(
                $"patterns where accepts at most {MaxMetadataFilters} metadata filters.");
        }

        return filters;
    }

    private static PatternMetadataFilter ParseSingleWhere(string where)
    {
        int equals = where.IndexOf('=');
        if (equals <= 0)
            throw InvalidRequest("patterns where must be key=value.");

        string key = where[..equals].Trim();
        string value = where[(equals + 1)..].Trim();
        if (key.Length == 0)
            throw InvalidRequest("patterns where must include a key.");

        var filter = new PatternMetadataFilter(key, value);
        try
        {
            filter.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw InvalidRequest(ex.Message);
        }
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
            "top_directory" => PatternSummaryGroupBy.TopDirectory,
            _ => throw InvalidRequest(
                "patterns group_by must be language_pattern_capture, file, directory, or top_directory."),
        };
    }

    private static PatternToolResult List(
        PatternFactsReader reader,
        PatternReadTarget target,
        string? patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        IReadOnlyList<PatternListRow> rows = target.List(
            reader,
            patternId,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters);

        IReadOnlyList<PatternListRow> renderRows = rows
            .OrderByDescending(static row => row.Count)
            .ThenBy(static row => row.PatternId, StringComparer.Ordinal)
            .ToArray();
        string Render(IReadOnlyList<PatternListRow> retained, int returnedThrough) =>
            json
                ? RenderListJson(
                    retained,
                    ListNextActions(retained),
                    path,
                    language,
                    metadataFilters,
                    rows.Count,
                    rows.Count - returnedThrough)
                : RenderListCompact(
                    retained,
                    ListNextActions(retained),
                    path,
                    language,
                    metadataFilters,
                    rows.Count,
                    rows.Count - returnedThrough);
        return PagePopulation(
            renderRows,
            "patterns_list",
            workspaceId,
            requestFingerprint,
            continuation,
            json,
            outputByteBudget,
            static row => string.Join(
                '|',
                row.PatternId,
                row.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(',', row.Languages),
                string.Join(',', row.Captures)),
            Render,
            emptyReason: null);
    }

    private static PatternToolResult Summary(
        PatternFactsReader reader,
        PatternReadTarget target,
        string? patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        PatternSummaryGroupBy groupBy,
        string? facet,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        IReadOnlyList<PatternSummaryRow> rows = target.Summary(
            reader,
            patternId,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters,
            groupBy,
            facet);

        IReadOnlyList<PatternSummaryRow> renderRows = rows
            .OrderByDescending(static row => row.Count)
            .ThenBy(static row => row.Language, StringComparer.Ordinal)
            .ThenBy(static row => row.PatternId, StringComparer.Ordinal)
            .ThenBy(static row => row.CaptureName, StringComparer.Ordinal)
            .ThenBy(static row => row.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.Directory, StringComparer.Ordinal)
            .ThenBy(static row => row.FacetValue, StringComparer.Ordinal)
            .ToArray();
        string Render(IReadOnlyList<PatternSummaryRow> retained, int returnedThrough) =>
            json
                ? RenderSummaryJson(
                    retained,
                    groupBy,
                    facet,
                    path,
                    language,
                    metadataFilters,
                    rows.Count,
                    rows.Count - returnedThrough)
                : RenderSummaryCompact(
                    retained,
                    groupBy,
                    facet,
                    path,
                    language,
                    metadataFilters,
                    rows.Count,
                    rows.Count - returnedThrough);
        return PagePopulation(
            renderRows,
            "patterns_summary",
            workspaceId,
            requestFingerprint,
            continuation,
            json,
            outputByteBudget,
            static row => string.Join(
                '|',
                row.Language,
                row.PatternId,
                row.CaptureName,
                row.Path,
                row.Directory,
                row.FacetValue,
                row.Count.ToString(CultureInfo.InvariantCulture)),
            Render,
            emptyReason: null);
    }

    private static int MaxMcpCollectionRenderCandidates(int maxBytes, bool json) =>
        Math.Max(
            1,
            maxBytes / (json ? MinJsonCollectionRowBytes : MinCompactCollectionRowBytes));

    private static PatternToolResult PagePopulation<T>(
        IReadOnlyList<T> population,
        string kind,
        string workspaceId,
        string requestFingerprint,
        string? continuation,
        bool json,
        int? outputByteBudget,
        Func<T, string> rowFingerprint,
        Func<IReadOnlyList<T>, int, string> renderer,
        string? emptyReason)
    {
        var identity = new ToolPopulationContinuationIdentity(
            kind,
            workspaceId,
            FingerprintPopulation(population, rowFingerprint),
            requestFingerprint);
        int offset = string.IsNullOrWhiteSpace(continuation)
            ? 0
            : ToolOutputBudget.DecodePopulationCursor(continuation, identity).Offset;
        if (offset < 0 || (population.Count > 0 && offset >= population.Count))
        {
            throw InvalidRequest("patterns continuation offset is outside the current result population.");
        }

        IReadOnlyList<T> remaining = population.Skip(offset).ToArray();
        string RenderPage(IReadOnlyList<T> retained, int _)
        {
            int returnedThrough = checked(offset + retained.Count);
            string? next = returnedThrough < population.Count
                ? ToolOutputBudget.EncodePopulationCursor(
                    identity,
                    new ToolPopulationContinuationCursor(returnedThrough))
                : null;
            return AttachContinuation(renderer(retained, returnedThrough), json, next);
        }

        BoundedPrefixRender page = outputByteBudget is { } maxBytes
            ? ToolOutputBudget.RenderPrefixWithinByteBudgetWithCount(
                remaining,
                maxBytes,
                RenderPage,
                MaxMcpCollectionRenderCandidates(maxBytes, json))
            : new BoundedPrefixRender(RenderPage(remaining, 0), remaining.Count);
        if (population.Count > 0 && page.RetainedCount == 0)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "output_metadata_too_large",
                "Patterns output metadata leaves no room for a result row; narrow the request."));
        }
        return new PatternToolResult(page.Output, page.RetainedCount, emptyReason);
    }

    private static PatternToolResult PageMatchPopulation(
        PatternMatchPage page,
        string kind,
        string workspaceId,
        string requestFingerprint,
        string? continuation,
        bool json,
        int? outputByteBudget,
        Func<IReadOnlyList<PatternMatchRow>, int, string> renderer,
        string? emptyReason)
    {
        var identity = new ToolPopulationContinuationIdentity(
            kind,
            workspaceId,
            page.PopulationFingerprint,
            requestFingerprint);
        int offset = string.IsNullOrWhiteSpace(continuation)
            ? 0
            : ToolOutputBudget.DecodePopulationCursor(continuation, identity).Offset;
        if (offset != page.Offset ||
            offset < 0 ||
            (page.TotalCount > 0 && offset >= page.TotalCount))
        {
            throw InvalidRequest("patterns continuation offset is outside the current result population.");
        }

        string RenderPage(IReadOnlyList<PatternMatchRow> retained, int _)
        {
            int returnedThrough = checked(offset + retained.Count);
            string? next = returnedThrough < page.TotalCount
                ? ToolOutputBudget.EncodePopulationCursor(
                    identity,
                    new ToolPopulationContinuationCursor(returnedThrough))
                : null;
            return AttachContinuation(renderer(retained, returnedThrough), json, next);
        }

        BoundedPrefixRender rendered = outputByteBudget is { } maxBytes
            ? ToolOutputBudget.RenderPrefixWithinByteBudgetWithCount(
                page.Rows,
                maxBytes,
                RenderPage,
                MaxMcpCollectionRenderCandidates(maxBytes, json))
            : new BoundedPrefixRender(RenderPage(page.Rows, 0), page.Rows.Count);
        if (page.TotalCount > 0 && rendered.RetainedCount == 0)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "output_metadata_too_large",
                "Patterns output metadata leaves no room for a result row; narrow the request."));
        }
        return new PatternToolResult(rendered.Output, rendered.RetainedCount, emptyReason);
    }

    private static void ValidateEmptyContinuation(
        string kind,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        if (string.IsNullOrWhiteSpace(continuation))
            return;
        var identity = new ToolPopulationContinuationIdentity(
            kind,
            workspaceId,
            FingerprintFields(),
            requestFingerprint);
        _ = ToolOutputBudget.DecodePopulationCursor(continuation, identity);
        throw InvalidRequest("patterns continuation offset is outside the current result population.");
    }

    private static string AttachContinuation(string output, bool json, string? continuation)
    {
        if (!json)
        {
            return continuation is null
                ? output
                : output + Environment.NewLine +
                  $"continuation: {continuation}{Environment.NewLine}" +
                  "Repeat the same patterns request with this continuation token.";
        }

        int end = output.LastIndexOf('}');
        if (end < 0)
            throw new InvalidDataException("Patterns JSON renderer did not produce an object.");
        string property = continuation is null
            ? ",\"continuation\":null"
            : ",\"continuation\":\"" + continuation + "\"";
        return output.Insert(end, property);
    }

    private static string FingerprintPopulation<T>(
        IEnumerable<T> rows,
        Func<T, string> rowFingerprint) =>
        FingerprintFields(rows.Select(rowFingerprint).ToArray());

    private static string FingerprintFields(params string?[] fields)
    {
        var builder = new StringBuilder();
        foreach (string? field in fields)
        {
            string value = field ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static PatternToolResult Search(
        PatternFactsReader reader,
        PatternReadTarget target,
        string patternId,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        int offset = string.IsNullOrWhiteSpace(continuation)
            ? 0
            : ToolOutputBudget.PeekPopulationCursorPosition(continuation).Offset;
        PatternExactSearchPageResult searchResult = target.SearchExact(
            reader,
            patternId,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters,
            offset,
            boundedLimit);
        PatternMatchPage page = searchResult.Page;
        long totalCount = page.TotalCount;
        IReadOnlyList<PatternMatchRow> rows = page.Rows;

        bool patternExists = searchResult.PatternExists;
        bool filteredOut = totalCount == 0
            && patternExists
            && (!string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(language) || metadataFilters.Count > 0);
        IReadOnlyList<string> suggestions = rows.Count == 0 && !filteredOut
            ? SuggestPatternIds(searchResult.SuggestionPatternIds, patternId)
            : [];
        string emptyReason = PatternEmptyReason(
            patternExists,
            filteredOut,
            querySearch: false);

        string Render(IReadOnlyList<PatternMatchRow> retained, int returnedThrough) =>
            json
                ? RenderSearchJson(
                    patternId,
                    retained,
                    suggestions,
                    filteredOut,
                    path,
                    language,
                    metadataFilters,
                    emptyReason,
                    totalCount,
                    totalCount - returnedThrough)
                : RenderSearchCompact(
                    patternId,
                    retained,
                    suggestions,
                    filteredOut,
                    path,
                    language,
                    metadataFilters,
                    totalCount,
                    totalCount - returnedThrough);
        return PageMatchPopulation(
            page,
            "patterns_search_exact",
            workspaceId,
            requestFingerprint,
            continuation,
            json,
            outputByteBudget,
            Render,
            emptyReason);
    }

    private static PatternToolResult SearchByQuery(
        PatternFactsReader reader,
        PatternReadTarget target,
        string query,
        string? language,
        string? path,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int limit,
        bool json,
        int? outputByteBudget,
        string workspaceId,
        string requestFingerprint,
        string? continuation)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxLimit);
        int offset = string.IsNullOrWhiteSpace(continuation)
            ? 0
            : ToolOutputBudget.PeekPopulationCursorPosition(continuation).Offset;
        bool hasActiveFilters = !string.IsNullOrWhiteSpace(path)
            || !string.IsNullOrWhiteSpace(language)
            || metadataFilters.Count > 0;
        PatternQueryMatchPageResult queryResult = target.SearchQuery(
            reader,
            query,
            language,
            path,
            metadataFilters.Count == 0 ? null : metadataFilters,
            offset,
            boundedLimit,
            MaxQueryPatternIds);
        IReadOnlyList<string> returnedPatternIds = queryResult.ReturnedPatternIds;
        var fanout = new PatternQueryFanout(
            queryResult.ConsideredPatternIds.Count,
            queryResult.MatchedPatternCount,
            returnedPatternIds);

        if (fanout.MatchedCount == 0)
        {
            ValidateEmptyContinuation(
                "patterns_search_query",
                workspaceId,
                requestFingerprint,
                continuation);
            IReadOnlyList<string> nearMatches = SuggestPatternIds(queryResult.SuggestionPatternIds, query);
            const string emptyReason = "query_no_match";
            if (json)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (Utf8JsonWriter writer = NewWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema_version", JsonSchemaVersion);
                    writer.WriteString("operation", "search");
                    writer.WriteString("query", query);
                    writer.WriteString("empty_reason", emptyReason);
                    WriteQueryFanoutJson(writer, fanout);
                    WriteStringArray(writer, "matched_pattern_ids", Array.Empty<string>());
                    WriteMatchCountsJson(writer, totalCount: 0, returnedCount: 0, omittedCount: 0);
                    WriteStringArray(writer, "near_matches", nearMatches);
                    WriteActiveFiltersJson(writer, path, language, metadataFilters);
                    writer.WriteStartArray("matches");
                    writer.WriteEndArray();
                    writer.WriteString("note", $"No patterns match '{query}'. Try `patterns operation=list` to see observed pattern_id values.");
                    writer.WritePropertyName("next_actions");
                    WriteNextActions(writer, QueryNoMatchNextActions(nearMatches));
                    writer.WriteEndObject();
                }
                string noMatchOutput = Encoding.UTF8.GetString(buffer.WrittenSpan);
                if (outputByteBudget is { } noMatchMaxBytes)
                {
                    noMatchOutput = ToolOutputBudget.RequireWithinByteBudget(
                        noMatchOutput,
                        noMatchMaxBytes);
                }
                return new PatternToolResult(noMatchOutput, 0, emptyReason);
            }

            string hint = RenderQueryNoMatchCompact(
                query,
                fanout,
                nearMatches,
                path,
                language,
                metadataFilters);
            if (outputByteBudget is { } maxCompactBytes)
                hint = ToolOutputBudget.RequireWithinByteBudget(hint, maxCompactBytes);
            return new PatternToolResult(hint, 0, emptyReason);
        }

        PatternMatchPage page = queryResult.Page;
        long totalCount = page.TotalCount;
        IReadOnlyList<PatternMatchRow> rows = page.Rows;
        string? resultEmptyReason = totalCount == 0
            ? hasActiveFilters ? "filtered_out" : "no_facts"
            : null;

        string Render(IReadOnlyList<PatternMatchRow> retained, int returnedThrough) =>
            json
                ? RenderSearchJsonForQuery(
                    query,
                    fanout,
                    retained,
                    path,
                    language,
                    metadataFilters,
                    resultEmptyReason,
                    totalCount,
                    totalCount - returnedThrough)
                : RenderSearchCompactForQuery(
                    query,
                    fanout,
                    retained,
                    path,
                    language,
                    metadataFilters,
                    resultEmptyReason,
                    totalCount,
                    totalCount - returnedThrough);
        return PageMatchPopulation(
            page,
            "patterns_search_query",
            workspaceId,
            requestFingerprint,
            continuation,
            json,
            outputByteBudget,
            Render,
            resultEmptyReason);
    }

    private static string RenderListCompact(
        IReadOnlyList<PatternListRow> rows,
        IReadOnlyList<PatternNextAction> nextActions,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int totalCount,
        int omittedCount)
    {
        string activeFilters = ActiveFiltersCompact(path, language, metadataFilters);
        if (totalCount == 0)
            return activeFilters.Length == 0
                ? "No patterns."
                : $"No patterns.{Environment.NewLine}active filters: {activeFilters}";

        var sb = new StringBuilder();
        sb.AppendLine("# patterns");
        if (activeFilters.Length > 0)
            sb.Append("active filters: ").AppendLine(activeFilters);
        sb.AppendLine("pattern_id\tcount\tlanguages\tcaptures");
        foreach (PatternListRow row in rows)
        {
            sb.Append(row.PatternId).Append('\t')
              .Append(row.Count).Append('\t')
              .Append(string.Join(",", row.Languages)).Append('\t')
              .Append(string.Join(",", row.Captures))
              .AppendLine();
        }
        AppendCollectionTruncationCompact(sb, "patterns", totalCount, rows.Count, omittedCount);
        AppendNextActions(sb, nextActions);
        return sb.ToString().TrimEnd();
    }

    private static string RenderSummaryCompact(
        IReadOnlyList<PatternSummaryRow> rows,
        PatternSummaryGroupBy groupBy,
        string? facet,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int totalCount,
        int omittedCount)
    {
        string activeFilters = ActiveFiltersCompact(path, language, metadataFilters);
        if (totalCount == 0)
        {
            var empty = new StringBuilder("No pattern groups.");
            if (activeFilters.Length > 0)
                empty.AppendLine().Append("active filters: ").Append(activeFilters);
            if (groupBy != PatternSummaryGroupBy.LanguagePatternCapture)
                empty.AppendLine().Append("group_by=").Append(SummaryGroupByName(groupBy));
            if (!string.IsNullOrWhiteSpace(facet))
                empty.AppendLine().Append("facet=").Append(facet.Trim());
            return empty.ToString();
        }

        var sb = new StringBuilder();
        sb.AppendLine("# patterns summary");
        if (activeFilters.Length > 0)
            sb.Append("active filters: ").AppendLine(activeFilters);
        if (groupBy != PatternSummaryGroupBy.LanguagePatternCapture)
            sb.Append("group_by=").Append(SummaryGroupByName(groupBy)).AppendLine();
        if (!string.IsNullOrWhiteSpace(facet))
            sb.Append("facet=").Append(facet.Trim()).AppendLine();

        if (groupBy == PatternSummaryGroupBy.File)
            sb.AppendLine("language\tpattern_id\tcapture\tpath\tcount");
        else if (groupBy is PatternSummaryGroupBy.Directory or PatternSummaryGroupBy.TopDirectory)
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
            else if (groupBy is PatternSummaryGroupBy.Directory or PatternSummaryGroupBy.TopDirectory)
                sb.Append(row.Directory).Append('\t');
            else if (!string.IsNullOrWhiteSpace(facet))
                sb.Append(row.FacetValue).Append('\t');
            sb.Append(row.Count)
              .AppendLine();
        }
        AppendCollectionTruncationCompact(sb, "groups", totalCount, rows.Count, omittedCount);
        return sb.ToString().TrimEnd();
    }

    private static void AppendCollectionTruncationCompact(
        StringBuilder builder,
        string label,
        int totalCount,
        int returnedCount,
        int omittedCount)
    {
        if (omittedCount <= 0)
            return;

        builder.Append(label).Append(": total=").Append(totalCount)
            .Append(" returned=").Append(returnedCount)
            .Append(" omitted=").Append(omittedCount)
            .Append(" truncated=true")
            .AppendLine();
        builder.Append(label == "patterns"
                ? "next: refine pattern_id, language, or path to narrow the result."
                : "next: refine pattern_id, language, path, where, or grouping to narrow the result.")
            .AppendLine();
    }

    private static string RenderSearchCompact(
        string patternId,
        IReadOnlyList<PatternMatchRow> rows,
        IReadOnlyList<string> suggestions,
        bool filteredOut,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        long totalCount,
        long omittedCount)
    {
        if (rows.Count == 0 && omittedCount == 0)
        {
            if (filteredOut)
                return RenderFilteredOutCompact(patternId, path, language, metadataFilters);
            if (suggestions.Count == 0)
                return $"No matches for {patternId}.";

            var empty = new StringBuilder();
            empty.Append("No matches for ").Append(patternId).AppendLine(".");
            empty.Append("Suggestions: ").AppendLine(string.Join(", ", suggestions));
            AppendNextActions(empty, QueryNoMatchNextActions(suggestions));
            return empty.ToString().TrimEnd();
        }

        var sb = new StringBuilder();
        sb.Append("# patterns search ").AppendLine(patternId);
        string activeFilters = ActiveFiltersCompact(path, language, metadataFilters);
        if (activeFilters.Length > 0)
            sb.Append("active filters: ").AppendLine(activeFilters);
        AppendMatchGroups(sb, rows, metadataFilters);
        AppendMatchTruncationCompact(sb, totalCount, rows.Count, omittedCount);
        return sb.ToString().TrimEnd();
    }

    private static string RenderSearchCompactForQuery(
        string query,
        PatternQueryFanout fanout,
        IReadOnlyList<PatternMatchRow> rows,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        string? emptyReason,
        long totalCount,
        long omittedCount)
    {
        var sb = new StringBuilder();
        sb.Append("# patterns search query='").Append(query).AppendLine("'");
        AppendQueryFanoutCompact(sb, fanout);
        sb.Append("matched_pattern_ids: ").Append(string.Join(", ", fanout.ReturnedPatternIds)).AppendLine();
        string filters = ActiveFiltersCompact(path, language, metadataFilters);
        if (rows.Count == 0 && omittedCount == 0)
        {
            if (string.Equals(emptyReason, "filtered_out", StringComparison.Ordinal))
            {
                sb.Append("No facts for matched pattern IDs after filters");
                if (filters.Length > 0)
                    sb.Append(": ").Append(filters);
                sb.AppendLine(".");
            }
            else
            {
                sb.AppendLine("No facts for matched pattern IDs.");
            }
        }
        else
        {
            if (filters.Length > 0)
                sb.Append("active filters: ").AppendLine(filters);
            AppendMatchGroups(sb, rows, metadataFilters);
        }
        AppendMatchTruncationCompact(sb, totalCount, rows.Count, omittedCount);
        return sb.ToString().TrimEnd();
    }

    private static void AppendMatchTruncationCompact(
        StringBuilder builder,
        long totalCount,
        int returnedCount,
        long omittedCount)
    {
        if (omittedCount <= 0)
            return;

        if (returnedCount > 0)
            builder.AppendLine();
        builder.Append("matches: total=").Append(totalCount)
            .Append(" returned=").Append(returnedCount)
            .Append(" omitted=").Append(omittedCount)
            .Append(" truncated=true")
            .AppendLine();
        builder.Append("next: refine path, language, or where to narrow the result.");
    }

    private static string RenderQueryNoMatchCompact(
        string query,
        PatternQueryFanout fanout,
        IReadOnlyList<string> nearMatches,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        var sb = new StringBuilder();
        sb.Append("No patterns match '").Append(query).AppendLine("'. Try `patterns operation=list` to see observed pattern_id values.");
        AppendQueryFanoutCompact(sb, fanout);
        if (nearMatches.Count > 0)
            sb.Append("near matches: ").AppendLine(string.Join(", ", nearMatches));
        string filters = ActiveFiltersCompact(path, language, metadataFilters);
        if (filters.Length > 0)
            sb.Append("active filters: ").AppendLine(filters);
        AppendNextActions(sb, QueryNoMatchNextActions(nearMatches));
        return sb.ToString().TrimEnd();
    }

    private static string RenderFilteredOutCompact(
        string patternId,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        string filters = ActiveFiltersCompact(path, language, metadataFilters);
        var sb = new StringBuilder();
        sb.Append("No matches for ").Append(patternId).Append(" after filters");
        if (!string.IsNullOrWhiteSpace(filters))
            sb.Append(": ").Append(filters);
        sb.AppendLine(".");
        sb.Append("Try again with this pattern_id and loosen language, path, or where.");
        AppendNextActions(sb, PatternIdRecoveryNextActions(patternId));
        return sb.ToString().TrimEnd();
    }

    private static string ActiveFiltersCompact(
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        var filters = new List<string>(capacity: 2 + metadataFilters.Count);
        if (!string.IsNullOrWhiteSpace(language))
            filters.Add("language=" + language.Trim());
        if (!string.IsNullOrWhiteSpace(path))
            filters.Add("path=" + path.Trim());
        foreach (PatternMetadataFilter metadataFilter in metadataFilters)
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
            {
                if (string.Equals(arg.Key, "limit", StringComparison.Ordinal)
                    && int.TryParse(
                        arg.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int limit))
                {
                    writer.WriteNumber(arg.Key, limit);
                }
                else
                {
                    writer.WriteString(arg.Key, arg.Value);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void AppendMatchGroups(
        StringBuilder sb,
        IReadOnlyList<PatternMatchRow> rows,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
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

                string metadata = MetadataCompact(row, metadataFilters);
                if (metadata.Length > 0)
                    sb.Append(" metadata=").Append(metadata);

                sb.AppendLine();
            }
        }
    }

    private static string RenderListJson(
        IReadOnlyList<PatternListRow> rows,
        IReadOnlyList<PatternNextAction> nextActions,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int totalCount,
        int omittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
            writer.WriteString("operation", "list");
            WriteActiveFiltersJson(writer, path, language, metadataFilters);
            WriteCollectionCountsJson(writer, "patterns", totalCount, rows.Count, omittedCount);
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
            WriteNextActions(writer, nextActions);
            WriteCollectionTruncationJson(writer, "patterns", omittedCount);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSummaryJson(
        IReadOnlyList<PatternSummaryRow> rows,
        PatternSummaryGroupBy groupBy,
        string? facet,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        int totalCount,
        int omittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
            writer.WriteString("operation", "summary");
            WriteActiveFiltersJson(writer, path, language, metadataFilters);
            if (groupBy != PatternSummaryGroupBy.LanguagePatternCapture)
                writer.WriteString("group_by", SummaryGroupByName(groupBy));
            if (!string.IsNullOrWhiteSpace(facet))
                writer.WriteString("facet", facet.Trim());
            WriteCollectionCountsJson(writer, "groups", totalCount, rows.Count, omittedCount);
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
            WriteCollectionTruncationJson(writer, "groups", omittedCount);
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
        string? emptyReason,
        long totalCount,
        long omittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
            writer.WriteString("operation", "search");
            writer.WriteString("pattern_id", patternId);
            WriteMatchCountsJson(writer, totalCount, rows.Count, omittedCount);
            WriteActiveFiltersJson(writer, path, language, metadataFilters);
            if (rows.Count == 0 && omittedCount == 0)
            {
                if (!string.IsNullOrWhiteSpace(emptyReason))
                    writer.WriteString("empty_reason", emptyReason);
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
                    writer.WritePropertyName("next_actions");
                    WriteNextActions(writer, QueryNoMatchNextActions(suggestions));
                }
            }
            writer.WriteStartArray("matches");
            foreach (PatternMatchRow row in rows)
                WriteMatchJson(writer, row);
            writer.WriteEndArray();
            WriteMatchTruncationJson(
                writer,
                omittedCount,
                "pattern_id",
                patternId,
                path,
                language,
                metadataFilters);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderSearchJsonForQuery(
        string query,
        PatternQueryFanout fanout,
        IReadOnlyList<PatternMatchRow> rows,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters,
        string? emptyReason,
        long totalCount,
        long omittedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", JsonSchemaVersion);
            writer.WriteString("operation", "search");
            writer.WriteString("query", query);
            WriteQueryFanoutJson(writer, fanout);
            WriteStringArray(writer, "matched_pattern_ids", fanout.ReturnedPatternIds);
            WriteMatchCountsJson(writer, totalCount, rows.Count, omittedCount);
            if (rows.Count == 0 && omittedCount == 0 && !string.IsNullOrWhiteSpace(emptyReason))
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
            WriteMatchTruncationJson(
                writer,
                omittedCount,
                "query",
                query,
                path,
                language,
                metadataFilters);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMatchCountsJson(
        Utf8JsonWriter writer,
        long totalCount,
        int returnedCount,
        long omittedCount)
    {
        writer.WriteNumber("matches_total_count", totalCount);
        writer.WriteNumber("matches_returned_count", returnedCount);
        writer.WriteNumber("matches_omitted_count", omittedCount);
        writer.WriteBoolean("matches_truncated", omittedCount > 0);
    }

    private static void WriteCollectionCountsJson(
        Utf8JsonWriter writer,
        string prefix,
        int totalCount,
        int returnedCount,
        int omittedCount)
    {
        writer.WriteNumber(prefix + "_total_count", totalCount);
        writer.WriteNumber(prefix + "_returned_count", returnedCount);
        writer.WriteNumber(prefix + "_omitted_count", omittedCount);
        writer.WriteBoolean(prefix + "_truncated", omittedCount > 0);
    }

    private static void WriteCollectionTruncationJson(Utf8JsonWriter writer, string label, int omittedCount)
    {
        if (omittedCount <= 0)
            return;

        writer.WriteString(
            "note",
            label == "patterns"
                ? "Rows were bounded by the MCP output budget. Refine pattern_id, language, or path."
                : "Rows were bounded by the MCP output budget. Refine pattern_id, language, path, where, or grouping.");
    }

    private static void WriteMatchTruncationJson(
        Utf8JsonWriter writer,
        long omittedCount,
        string targetKey,
        string targetValue,
        string? path,
        string? language,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        if (omittedCount <= 0)
            return;

        writer.WriteString("note", "Result rows were bounded by limit or the MCP output budget. Request one row or refine path, language, or where.");
        writer.WritePropertyName("next_actions");
        var args = new List<KeyValuePair<string, string>>
        {
            new("operation", "search"),
            new(targetKey, targetValue),
        };
        if (!string.IsNullOrWhiteSpace(language))
            args.Add(new("language", language.Trim()));
        if (!string.IsNullOrWhiteSpace(path))
            args.Add(new("path", path.Trim()));
        if (metadataFilters.Count > 0)
        {
            args.Add(new(
                "where",
                string.Join(
                    ';',
                    metadataFilters.Select(static filter => filter.Key + "=" + filter.Value))));
        }
        args.Add(new("limit", "1"));
        WriteNextActions(
            writer,
            [new PatternNextAction(
                "patterns",
                "request one result from the same filtered population",
                args)]);
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

    private static void AppendQueryFanoutCompact(StringBuilder sb, PatternQueryFanout fanout)
    {
        sb.Append("pattern_id_fanout: considered=").Append(fanout.ConsideredCount)
          .Append(" matched=").Append(fanout.MatchedCount)
          .Append(" returned=").Append(fanout.ReturnedCount)
          .Append(" omitted=").Append(fanout.OmittedCount)
          .Append(" truncated=").Append(fanout.Truncated ? "true" : "false")
          .AppendLine();
    }

    private static void WriteQueryFanoutJson(Utf8JsonWriter writer, PatternQueryFanout fanout)
    {
        writer.WriteNumber("pattern_ids_considered_count", fanout.ConsideredCount);
        writer.WriteNumber("pattern_ids_matched_count", fanout.MatchedCount);
        writer.WriteNumber("pattern_ids_returned_count", fanout.ReturnedCount);
        writer.WriteNumber("pattern_ids_omitted_count", fanout.OmittedCount);
        writer.WriteBoolean("pattern_id_fanout_truncated", fanout.Truncated);
    }

    private static string MetadataCompact(
        PatternMatchRow row,
        IReadOnlyList<PatternMetadataFilter> metadataFilters)
    {
        if (row.MetadataError is not null)
            return "error";
        if (row.Metadata.ValueKind != JsonValueKind.Object)
            return string.Empty;

        int selectedLimit = Math.Max(4, metadataFilters.Count);
        var selected = new List<(string Name, JsonElement Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PatternMetadataFilter metadataFilter in metadataFilters)
            Add(metadataFilter.Key);
        foreach (string key in MetadataPriority)
            Add(key);
        foreach (JsonProperty property in row.Metadata.EnumerateObject())
        {
            if (selected.Count >= selectedLimit)
                break;
            if (seen.Add(property.Name))
                selected.Add((property.Name, property.Value));
        }

        return string.Join(
            ",",
            selected.Select(static property => property.Name + "=" + MetadataValueCompact(property.Value)));

        void Add(string key)
        {
            if (selected.Count >= selectedLimit || !seen.Add(key))
                return;
            if (row.Metadata.TryGetProperty(key, out JsonElement value))
                selected.Add((key, value));
        }
    }

    private static IReadOnlyList<string> SuggestPatternIds(
        IEnumerable<string> patternIds,
        string patternId)
    {
        string[] queryTokens = PatternTokens(patternId);
        if (queryTokens.Length == 0)
            return [];

        return patternIds
            .Select(candidate => (PatternId: candidate, Score: PatternSuggestionScore(queryTokens, candidate)))
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

    private static string SummaryGroupByName(PatternSummaryGroupBy groupBy) =>
        groupBy switch
        {
            PatternSummaryGroupBy.LanguagePatternCapture => "language_pattern_capture",
            PatternSummaryGroupBy.File => "file",
            PatternSummaryGroupBy.Directory => "directory",
            PatternSummaryGroupBy.TopDirectory => "top_directory",
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, null),
        };

    private static string RequiredPatternId(string? patternId)
    {
        if (string.IsNullOrWhiteSpace(patternId))
            throw InvalidRequest("patterns search requires pattern_id.");

        return patternId.Trim();
    }

    private static ToolDiagnosticException InvalidRequest(string message) =>
        new(ToolDiagnostic.Refusal("invalid_request", message));

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
        bool patternExists,
        bool filteredOut,
        bool querySearch)
    {
        if (querySearch)
            return "query_no_match";
        if (filteredOut)
            return "filtered_out";
        if (!patternExists)
            return "no_such_pattern_id";
        return "no_facts";
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = PatternJsonEncoder });
}

/// <summary>
/// A rendered patterns result. <paramref name="LevelDiagnostic"/> is set by <see cref="PatternsTool.Run"/> when
/// the artifact serves a symbols-level index, so every entry point receives the level decision rather than
/// having to remember to make it.
/// </summary>
internal readonly record struct PatternToolResult(
    string Output,
    int ResultCount,
    string? EmptyReason = null,
    ToolDiagnostic? LevelDiagnostic = null);
