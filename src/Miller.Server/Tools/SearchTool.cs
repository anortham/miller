using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>The interpretation axis for <c>search</c> (miller-toolbox.md L74). NOT and/or.</summary>
public enum SearchToolMode
{
    /// <summary>Infer interpretation from the query shape (the default).</summary>
    Auto,

    /// <summary>Treat the query as a natural-language / text phrase.</summary>
    Text,

    /// <summary>Treat the query as a symbol name / identifier.</summary>
    Symbol,

    /// <summary>Treat the query as a file path fragment.</summary>
    File,

    /// <summary>Search docs-like file CONTENT (prose/markup/config), returning path + line + snippet hits.</summary>
    Content,

    /// <summary>Search workspace source-file body text, returning path + line + snippet hits.</summary>
    Source,

    /// <summary>Search explicitly imported external-file text.</summary>
    External,

    /// <summary>Search explicitly imported web markdown/text.</summary>
    Web,

    /// <summary>Search all content corpus text kinds. Explicit only; never the default.</summary>
    AllText,

    /// <summary>Audit TODO/FIXME/HACK/XXX markers in comments and doc comments.</summary>
    Markers,
}

/// <summary>
/// The <c>search</c> tool (M2 §4): find indexed code and return ranked results. Maps to
/// <see cref="MillerRepositoryIndex.Search"/> (lexical, SearchMode.Or) — the ordering is already
/// score-DESC / DocId-ASC with the exact-name boost, so the renderer NEVER re-sorts. The
/// <c>exclude_tests</c> tri-state and natural-language detection are applied here over a centralized
/// <see cref="IsTestPath"/>. Returns compact text by default; <c>format=json</c> for chaining.
/// </summary>
[McpServerToolType]
public sealed class SearchTool
{
    internal const int DefaultLimit = 6;
    private const string SymbolNoResultsHint =
        "No results. Try a shorter symbol query, mode=source for code text, or mode=content for docs/config.";
    private const int EmptySuggestionLimit = 5;
    private const string RegionsUsageHint =
        "regions must be comment, doc_comment, or string_literal. Example: regions=comment or regions=doc_comment,string_literal.";

    private static readonly string[] WorkspaceContentSearchKinds =
    [
        TextContentKind.WorkspaceDocs,
        TextContentKind.WorkspaceConfig,
    ];

    private readonly IWorkspaceSearchProvider _workspaceProvider;
    private readonly IWorkspaceRegionSearchProvider _regionProvider;
    private readonly IWorkspaceTextContentSearchProvider _textContentProvider;

    /// <summary>
    /// Construct over the symbol-search and content-search providers (production / freshness-aware). In
    /// production both resolve to the one <c>WorkspaceIndexProvider</c> singleton; they are split here so the
    /// content/docs projection loads lazily and only when <c>mode=content</c> asks for it.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either provider is null.</exception>
    public SearchTool(IWorkspaceSearchProvider workspaceProvider, IWorkspaceContentSearchProvider contentProvider)
        : this(
            workspaceProvider,
            contentProvider,
            workspaceProvider as IWorkspaceRegionSearchProvider ?? new UnavailableRegionSearchProvider(),
            workspaceProvider as IWorkspaceTextContentSearchProvider
                ?? contentProvider as IWorkspaceTextContentSearchProvider
                ?? new UnavailableTextContentSearchProvider())
    {
    }

    /// <summary>
    /// Construct over all search providers. Region search is split out because it has no in-memory fallback and
    /// must read a revision-fresh <c>search.db</c> sidecar.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any provider is null.</exception>
    public SearchTool(
        IWorkspaceSearchProvider workspaceProvider,
        IWorkspaceContentSearchProvider contentProvider,
        IWorkspaceRegionSearchProvider regionProvider)
        : this(
            workspaceProvider,
            contentProvider,
            regionProvider,
            workspaceProvider as IWorkspaceTextContentSearchProvider
                ?? contentProvider as IWorkspaceTextContentSearchProvider
                ?? regionProvider as IWorkspaceTextContentSearchProvider
                ?? new UnavailableTextContentSearchProvider())
    {
    }

    /// <summary>
    /// Construct over all search providers, including the explicit text-content corpus provider used by
    /// <c>mode=source</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Any provider is null.</exception>
    public SearchTool(
        IWorkspaceSearchProvider workspaceProvider,
        IWorkspaceContentSearchProvider contentProvider,
        IWorkspaceRegionSearchProvider regionProvider,
        IWorkspaceTextContentSearchProvider textContentProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(contentProvider);
        ArgumentNullException.ThrowIfNull(regionProvider);
        ArgumentNullException.ThrowIfNull(textContentProvider);
        _workspaceProvider = workspaceProvider;
        _regionProvider = regionProvider;
        _textContentProvider = textContentProvider;
    }

    [McpServerTool(Name = "search")]
    [Description(
        "Search indexed code and return ranked results. Use this before shell rg/grep/cat or reading whole " +
        "files. Pass a symbol name, an identifier, or a natural-language phrase. Test code is hidden for " +
        "natural-language queries unless you ask for it. Use mode=markers for TODO/FIXME/HACK/XXX audits " +
        "over comments/doc comments. Use mode=content (alias docs) to search docs/prose " +
        "file content instead of symbols, or mode=source/external/web/all-text for content corpus text — these return " +
        "path + line + snippet hits. Pass regions=comment, " +
        "doc_comment, or string_literal to search only inside those source regions. Returns compact text by " +
        "default; pass format=json to chain results.")]
    public string Search(
        [Description("Symbol name, identifier, or natural-language phrase.")] string query,
        [Description("Interpretation axis: auto|text|symbol|file|markers|content|source|external|web|all-text. Default auto.")] string mode = "auto",
        [Description("Max results to return. Default 6.")] int limit = DefaultLimit,
        [Description("Hide test code: leave unset to auto-hide for natural-language queries; true/false to force.")]
        bool? exclude_tests = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null,
        [Description("Source-region kinds to search: comma list of comment, doc_comment, string_literal. Alias: docstring.")]
        string? regions = null,
        [Description("Glob filter for workspace-relative file paths, e.g. src/ui/**. Optional.")]
        string? file_pattern = null,
        [Description("Comma-separated language filter, e.g. csharp,typescript. Optional.")]
        string? language = null)
    {
        var scope = TelemetryContext.Current;
        try
        {
            SearchRoute route = SearchRoutePlanner.Plan(mode, regions);
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            if (scope is not null)
                ApplyTelemetryShape(scope, route, json, limit, regions, file_pattern, language, exclude_tests);

            string output;
            int count;
            if (route.Kind == SearchRouteKind.Regions)
            {
                WorkspaceRegionSearchContext region = _regionProvider.ResolveRegionSearch(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(region, workspace_id, json);
                SearchRouteExecutionResult result = SearchRouteExecutor.RunRegions(
                    region.Index,
                    route,
                    new SearchRouteExecutionRequest(
                        query,
                        limit,
                        json,
                        exclude_tests,
                        compactBanner,
                        FilePattern: file_pattern,
                        Language: language));
                output = result.Output;
                count = result.Count;
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, region);
                    scope.SetMetadata("search_backend", "region_disk");
                }
            }
            else if (route.Kind == SearchRouteKind.Markers)
            {
                WorkspaceRegionSearchContext region = _regionProvider.ResolveRegionSearch(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(region, workspace_id, json);
                SearchRouteExecutionResult result = SearchRouteExecutor.RunMarkers(
                    region.Index,
                    route,
                    new SearchRouteExecutionRequest(
                        query,
                        limit,
                        json,
                        exclude_tests,
                        compactBanner,
                        FilePattern: file_pattern,
                        Language: language));
                output = result.Output;
                count = result.Count;
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, region);
                    scope.SetMetadata("search_backend", "region_disk");
                }
            }
            else if (route.Kind == SearchRouteKind.Content)
            {
                // Content/docs search routes through the corpus sidecar but keeps the legacy content result shape.
                WorkspaceTextContentSearchContext content =
                    _textContentProvider.ResolveTextContentSearch(workspace_id, ensureFresh);
                string? contentBanner = ReadToolWorkspaceRouting.CompactBanner(content, workspace_id, json);
                SearchRouteExecutionResult result = SearchRouteExecutor.RunContent(
                    content.Index,
                    route,
                    new SearchRouteExecutionRequest(
                        query,
                        limit,
                        json,
                        exclude_tests,
                        contentBanner,
                        FilePattern: file_pattern,
                        Language: language));
                output = result.Output;
                count = result.Count;
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, content);
                    scope.SourceBytes = result.SourceBytes;
                    scope.SetMetadata("search_backend", "content_disk");
                }
            }
            else if (route.Kind == SearchRouteKind.TextContent)
            {
                WorkspaceTextContentSearchContext textContent =
                    _textContentProvider.ResolveTextContentSearch(workspace_id, ensureFresh);
                string? contentBanner = ReadToolWorkspaceRouting.CompactBanner(textContent, workspace_id, json);
                SearchRouteExecutionResult result = SearchRouteExecutor.RunTextContent(
                    textContent.Index,
                    route,
                    new SearchRouteExecutionRequest(
                        query,
                        limit,
                        json,
                        exclude_tests,
                        contentBanner,
                        FilePattern: file_pattern,
                        Language: language));
                output = result.Output;
                count = result.Count;
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, textContent);
                    scope.SourceBytes = result.SourceBytes;
                    scope.SetMetadata("search_backend", "content_disk");
                }
            }
            else
            {
                WorkspaceSymbolSearchContext context = _workspaceProvider.ResolveSymbolSearch(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
                SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
                    context.Index,
                    route,
                    new SearchRouteExecutionRequest(
                        query,
                        limit,
                        json,
                        exclude_tests,
                        compactBanner,
                        HasDocLookup: symbolIds => ReadHasDocCommentBestEffort(context.IndexDbPath, symbolIds),
                        FilePattern: file_pattern,
                        Language: language));
                output = result.Output;
                count = result.Count;
                if (ShouldRunAutoTextRescue(route, json, query, count, context.Index))
                {
                    AutoTextRescueResult? rescue = TryRunAutoTextRescue(
                        query,
                        limit,
                        exclude_tests,
                        output,
                        count,
                        workspace_id,
                        ensureFresh,
                        file_pattern,
                        language,
                        compactBanner);
                    if (scope is not null)
                    {
                        scope.SetMetadata("auto_rescue_attempted", true);
                        scope.SetMetadata("auto_rescue_kind", rescue?.Kind ?? "unavailable");
                        scope.SetMetadata("auto_rescue_result_count", rescue?.Count ?? 0);
                        scope.SetMetadata("auto_source_rescue_attempted", true);
                        scope.SetMetadata("auto_source_rescue_found", rescue is { Kind: "source", Count: > 0 });
                    }
                    if (rescue is { Count: > 0 })
                    {
                        output = rescue.Output;
                        count += rescue.Count;
                        if (scope is not null)
                            scope.SourceBytes += rescue.SourceBytes;
                    }
                }
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, context);
                    scope.SetMetadata("search_backend", SearchBackendName(context.Index));
                }
            }

            if (scope is not null)
            {
                scope.SetTarget(query);
                scope.ResultCount = count;
                scope.Outcome = count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                if (count == 0)
                    scope.SetEmptyReason(EmptyReasonFor(route));
            }
            return output;
        }
        catch (Exception ex)
        {
            if (scope is not null)
            {
                scope.Outcome = TelemetryOutcome.Error;
                scope.SetError(ex);
            }
            // Return a clean compact error rather than throwing raw (which the SDK redacts to the client).
            return $"search failed: {ex.Message}";
        }
    }

    // internal (not private): the CLI search verb reuses the EXACT same mode mapping so `miller search --mode x`
    // and the MCP tool agree on interpretation — one source of truth.
    internal static SearchToolMode ParseMode(string mode) => mode?.ToLowerInvariant() switch
    {
        "text" => SearchToolMode.Text,
        "symbol" => SearchToolMode.Symbol,
        "file" => SearchToolMode.File,
        "content" => SearchToolMode.Content,
        "docs" => SearchToolMode.Content, // alias
        "source" => SearchToolMode.Source,
        "external" => SearchToolMode.External,
        "external_file" => SearchToolMode.External,
        "web" => SearchToolMode.Web,
        "all-text" => SearchToolMode.AllText,
        "all_text" => SearchToolMode.AllText,
        "markers" or "marker" => SearchToolMode.Markers,
        _ => SearchToolMode.Auto, // includes "auto", null, and anything unrecognized
    };

    internal static IReadOnlyCollection<string> ContentKindsForMode(SearchToolMode mode) =>
        mode switch
        {
            SearchToolMode.External => [TextContentKind.ExternalFile],
            SearchToolMode.Web => [TextContentKind.Web],
            SearchToolMode.AllText =>
            [
                TextContentKind.WorkspaceSource,
                TextContentKind.WorkspaceDocs,
                TextContentKind.WorkspaceConfig,
                TextContentKind.ExternalFile,
                TextContentKind.Web,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Mode is not a content corpus mode."),
        };

    internal static IReadOnlySet<string>? ParseRegionKinds(string? regions)
    {
        if (string.IsNullOrWhiteSpace(regions))
            return null;

        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawPart in regions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normalized = rawPart.ToLowerInvariant() switch
            {
                "comment" => "comment",
                "doc_comment" or "docstring" => "doc_comment",
                "string_literal" => "string_literal",
                _ => throw new InvalidOperationException(RegionsUsageHint)
            };
            parsed.Add(normalized);
        }

        if (parsed.Count == 0)
        {
            throw new InvalidOperationException(RegionsUsageHint);
        }

        return parsed;
    }

    /// <summary>
    /// Telemetry metadata recording which backend served a symbol search — <c>disk</c> when the on-disk
    /// <see cref="FtsSymbolSearchIndex"/> sidecar answered, <c>memory</c> when the in-memory index did. This is
    /// the observable "disk path taken" signal from the sidecar design's risk list: an unexpected memory route
    /// should be easy to distinguish from the default disk route. Every symbol search stamps its backend into the
    /// telemetry row's <c>metadata_json</c> so it can be read back per call and aggregated ad hoc (e.g.
    /// <c>json_extract(metadata_json, '$.search_backend')</c>). No dashboard surface consumes it yet — it is
    /// recorded for diagnosis; <c>SearchToolTests.Search_RecordsServingBackend_InTelemetryMetadata</c> pins it.
    /// </summary>
    internal const string DiskBackendMetadata = "{\"search_backend\":\"disk\"}";

    /// <summary>In-memory backend marker (see <see cref="DiskBackendMetadata"/>).</summary>
    internal const string MemoryBackendMetadata = "{\"search_backend\":\"memory\"}";

    /// <summary>Region text is always served from the disk sidecar.</summary>
    internal const string RegionBackendMetadata = "{\"search_backend\":\"region_disk\"}";

    /// <summary>Workspace source text is served from the content corpus sidecar.</summary>
    internal const string TextContentBackendMetadata = "{\"search_backend\":\"content_disk\"}";

    private static string SearchBackendMetadata(ISymbolLookupIndex index) =>
        index is FtsSymbolSearchIndex ? DiskBackendMetadata : MemoryBackendMetadata;

    private static string SearchBackendName(ISymbolLookupIndex index) =>
        index is FtsSymbolSearchIndex ? "disk" : "memory";

    private static void ApplyTelemetryShape(
        TelemetryScope scope,
        SearchRoute route,
        bool json,
        int limit,
        string? regions,
        string? filePattern,
        string? language,
        bool? excludeTests)
    {
        scope.Op = route.Kind == SearchRouteKind.Regions ? "regions" : ModeName(route.Mode);
        scope.SetMetadata("route", RouteName(route.Kind));
        scope.SetMetadata("format", json ? "json" : "compact");
        scope.SetMetadata("limit_bucket", LimitBucket(limit));
        scope.SetMetadata("has_regions", !string.IsNullOrWhiteSpace(regions));
        scope.SetMetadata("has_file_pattern", !string.IsNullOrWhiteSpace(filePattern));
        scope.SetMetadata("has_language", !string.IsNullOrWhiteSpace(language));
        scope.SetMetadata("exclude_tests", excludeTests is null ? "default" : excludeTests.Value ? "true" : "false");
    }

    private static string RouteName(SearchRouteKind kind) => kind switch
    {
        SearchRouteKind.Regions => "regions",
        SearchRouteKind.Markers => "markers",
        SearchRouteKind.Content => "content",
        SearchRouteKind.TextContent => "text_content",
        SearchRouteKind.Symbols => "symbols",
        _ => "unknown",
    };

    private static string ModeName(SearchToolMode mode) => mode switch
    {
        SearchToolMode.AllText => "all-text",
        _ => mode.ToString().ToLowerInvariant(),
    };

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        _ => "51+",
    };

    private static string EmptyReasonFor(SearchRoute route) => route.Kind switch
    {
        SearchRouteKind.Regions => "no_region_hits",
        SearchRouteKind.Markers => "no_todo_markers",
        SearchRouteKind.Content or SearchRouteKind.TextContent => "no_text_hits",
        SearchRouteKind.Symbols => "no_symbol_hits",
        _ => "no_hits",
    };

    private const int EmptyHintQueryLimit = 60;

    // search·file empty (63% empty on mac): a path fragment that indexed no file. Echo a bounded query so the
    // agent gets a copy-pasteable symbol fallback rather than a bare "No results.".
    private static string FileEmptyHint(string query)
    {
        string q = Truncate(query, EmptyHintQueryLimit);
        return $"No indexed file matches '{q}'. Try a shorter path fragment, mode=auto, or `search {q}` for symbols.";
    }

    // search·source/content/all-text empty (36-47% empty): no text hits. The right "next call" depends on whether
    // the searched kinds are workspace text (refresh re-indexes files) or imported (content list shows what's
    // loaded), so route the hint by kind instead of claiming a one-size refresh.
    private static string TextContentEmptyHint(IReadOnlyCollection<string> contentKinds, string query)
    {
        string q = Truncate(query, EmptyHintQueryLimit);
        bool hasWorkspace = false;
        bool hasImported = false;
        foreach (string kind in contentKinds)
        {
            if (kind == TextContentKind.ExternalFile || kind == TextContentKind.Web)
                hasImported = true;
            else
                hasWorkspace = true;
        }
        string where = (hasWorkspace, hasImported) switch
        {
            (true, false) => "`workspace refresh`",
            (false, true) => "`content list` to see imported sources",
            _ => "`workspace refresh` or `content list` for imported sources",
        };
        return $"No text hits. Try broader terms, {where}, or `search {q}` for symbols.";
    }

    private const int OutsideScopeHintLimit = 3;
    private const int SignatureMaxLength = 110;

    /// <summary>Longest accepted query. A pasted blob beyond this is never a real symbol/text search; reject it
    /// BEFORE tokenization/CollapseName so it cannot heap-thrash the tokenizers.</summary>
    internal const int MaxQueryLength = 1000;

    // Same throw-pattern as the ThrowIfNullOrWhiteSpace guard: the MCP boundary catches and renders it as a
    // clean `search failed:` line instead of a raw stack.
    private static void ThrowIfQueryTooLong(string query)
    {
        if (query.Length > MaxQueryLength)
        {
            throw new ArgumentException(
                $"query is too long ({query.Length} chars; max {MaxQueryLength}). Shorten the query.",
                nameof(query));
        }
    }

    // Cascading over-fetch windows. Post-search filters (test hiding, low-signal kinds, file_pattern/language)
    // run AFTER the index window is cut, so a heavily filtered query can keep fewer than `limit` rows — even
    // zero — while matches still exist past the window, with no hint that raising the window would help.
    private static readonly int[] OverFetchEscalationWindows = [500, 2000, 10000];

    /// <summary>
    /// Run an index fetch with post-filter escalation: <paramref name="fetchAndFilter"/> queries the index with
    /// the given window, resets and re-applies the caller's post-search filters in index order (filter, never
    /// re-sort), and reports (rows the index returned, rows the filters kept). While the index FILLED the window
    /// (so more rows may exist) and fewer than <paramref name="limit"/> were kept, retry with the next larger
    /// window (500 → 2000 → 10000).
    /// </summary>
    private static void FetchWithEscalation(int overFetch, int limit, Func<int, (int Fetched, int Kept)> fetchAndFilter)
    {
        int window = overFetch;
        (int fetched, int kept) = fetchAndFilter(window);
        foreach (int nextWindow in OverFetchEscalationWindows)
        {
            if (nextWindow <= window)
                continue;
            if (kept >= limit || fetched < window)
                return;
            window = nextWindow;
            (fetched, kept) = fetchAndFilter(window);
        }
    }

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry) the tool method delegates to. Returns the rendered
    /// string and sets <paramref name="renderedCount"/> to the number of rows actually shown (the page).
    /// </summary>
    public static string Run(
        ISymbolLookupIndex index, string query, SearchToolMode mode, int limit,
        bool? excludeTests, bool json, out int renderedCount, string? compactBanner = null,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? hasDocLookup = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        if (limit < 1) limit = 1;

        bool fileMode = mode == SearchToolMode.File ||
                        (mode == SearchToolMode.Auto && IsPathLikeQuery(query, index));

        bool hideTests = ResolveExcludeTests(excludeTests, query, mode);
        bool hideLowSignalKinds = fileMode || ResolveHideLowSignalKinds(query, mode);
        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        // Pull enough to know whether there is an overflow beyond `limit` after post-search filters, so the
        // "… N more" note is accurate. Natural-language phrase search can be import/module-heavy, so use the cap.
        int overFetch = hideLowSignalKinds || filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);

        // Preserve index order; only filter (never re-sort).
        var kept = new List<IndexedSymbol>();
        var scores = new List<double>();
        var outsideScope = new List<IndexedSymbol>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            kept.Clear();
            scores.Clear();
            outsideScope.Clear();
            if (fileMode)
            {
                IReadOnlyList<IndexedSymbol> symbols = index.FindByFilePathFragment(query, window);
                foreach (IndexedSymbol sym in symbols)
                    AddIfVisible(sym, score: 1.0);
                return (symbols.Count, kept.Count);
            }

            IReadOnlyList<SearchHit> hits = index.Search(query, window, SearchMode.Or);
            foreach (var hit in hits)
                AddIfVisible(index.Resolve(hit.Document.DocId), hit.Score);
            return (hits.Count, kept.Count);
        });

        int total = kept.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
        {
            if (fileMode)
            {
                if (json)
                    return "[]";
                return outsideScope.Count > 0
                    ? RenderFilteredMissCompact(filters, compactBanner, outsideScope)
                    : ReadToolWorkspaceRouting.PrefixCompact(FileEmptyHint(query), compactBanner);
            }
            IReadOnlyList<IndexedSymbol> suggestions =
                outsideScope.Count == 0
                    ? SymbolSuggestionEngine.Suggest(index, query, EmptySuggestionLimit)
                    : [];
            if (json)
                return suggestions.Count > 0 ? RenderEmptyJson(suggestions) : "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissCompact(filters, compactBanner, outsideScope)
                : RenderEmptySymbolMissCompact(compactBanner, suggestions);
        }

        IReadOnlySet<string>? hasDocSymbolIds = null;
        if (hasDocLookup is not null && page > 0)
        {
            string[] pageIds = kept.Take(page).Select(static s => s.SymbolId).ToArray();
            hasDocSymbolIds = hasDocLookup(pageIds);
        }

        return json
            ? RenderJson(kept, scores, page, hasDocSymbolIds)
            : fileMode
                ? RenderFileCompact(kept, page, total, compactBanner, hasDocSymbolIds)
            : RenderCompact(kept, page, total, query, compactBanner, hasDocSymbolIds);

        void AddIfVisible(IndexedSymbol sym, double score)
        {
            // Cross-language predicate (decision-4): julie's persisted is_test OR the path fallback. Using the
            // shared helper means an AST-flagged test in a non-test-named file is hidden, not just *Tests.cs.
            if (hideTests && IsTestPath.IsTest(sym))
                return;
            if (hideLowSignalKinds && IsLowSignalKind(sym.Kind))
                return;
            if (!filters.Allows(sym.FilePath, sym.Language))
            {
                if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(sym);
                return;
            }
            kept.Add(sym);
            scores.Add(score);
        }
    }

    /// <summary>
    /// The pure content-search execution core (no MCP/DI/telemetry): rank docs-like file content and render
    /// path + best line + snippet hits. A distinct result kind from <see cref="Run"/> — never a fake symbol —
    /// so <c>exclude_tests</c> does not apply. Sets <paramref name="renderedCount"/> to the page size.
    /// </summary>
    public static string RunContent(
        IContentSearchIndex index, string query, int limit,
        bool json, out int renderedCount, string? compactBanner = null,
        string? filePattern = null,
        string? language = null) =>
        RunContent(index, query, limit, json, out renderedCount, out _, compactBanner, filePattern, language);

    public static string RunContent(
        IContentSearchIndex index, string query, int limit,
        bool json, out int renderedCount, out long sourceBytes, string? compactBanner = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        if (limit < 1) limit = 1;

        // Over-fetch (same cap as symbol search) so the "… N more" overflow note is accurate without paging.
        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);
        var hits = new List<ContentSearchHit>();
        var outsideScope = new List<ContentSearchHit>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            hits.Clear();
            outsideScope.Clear();
            IReadOnlyList<ContentSearchHit> fetched = index.Search(query, window);
            foreach (ContentSearchHit hit in fetched)
            {
                if (filters.Allows(hit.Path, hit.Language))
                    hits.Add(hit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(hit);
            }
            return (fetched.Count, hits.Count);
        });

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
        {
            sourceBytes = 0;
            if (json)
                return "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissContentCompact(filters, compactBanner, outsideScope)
                : ReadToolWorkspaceRouting.PrefixCompact(TextContentEmptyHint(WorkspaceContentSearchKinds, query), compactBanner);
        }

        sourceBytes = hits
            .Take(page)
            .GroupBy(static hit => hit.Path, StringComparer.Ordinal)
            .Sum(static group => group.Max(static hit => hit.SourceBytes));

        return json
            ? RenderContentJson(hits, page)
            : RenderContentCompact(hits, page, total, compactBanner);
    }

    public static string RunContentCorpus(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool json,
        out int renderedCount,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null) =>
        RunContentCorpus(index, query, limit, json, out renderedCount, out _, compactBanner, filePattern, language);

    public static string RunContentCorpus(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool json,
        out int renderedCount,
        out long sourceBytes,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);
        var hits = new List<ContentSearchHit>();
        var outsideScope = new List<ContentSearchHit>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            hits.Clear();
            outsideScope.Clear();
            IReadOnlyList<TextContentSearchHit> fetched =
                index.Search(query, WorkspaceContentSearchKinds, window, excludeTests: false);
            foreach (TextContentSearchHit hit in fetched)
            {
                var contentHit = new ContentSearchHit(
                    hit.DisplayPath,
                    hit.Score,
                    hit.Line,
                    hit.Snippet,
                    hit.Language,
                    hit.SourceBytes);
                if (filters.Allows(contentHit.Path, contentHit.Language))
                    hits.Add(contentHit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(contentHit);
            }
            return (fetched.Count, hits.Count);
        });

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
        {
            sourceBytes = 0;
            if (json)
                return "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissContentCompact(filters, compactBanner, outsideScope)
                : ReadToolWorkspaceRouting.PrefixCompact(TextContentEmptyHint(WorkspaceContentSearchKinds, query), compactBanner);
        }

        sourceBytes = hits
            .Take(page)
            .GroupBy(static hit => hit.Path, StringComparer.Ordinal)
            .Sum(static group => group.Max(static hit => hit.SourceBytes));

        return json
            ? RenderContentJson(hits, page)
            : RenderContentCompact(hits, page, total, compactBanner);
    }

    /// <summary>
    /// The pure text-content corpus execution core (no MCP/DI/telemetry). This is used by explicit
    /// source/docs/external/web text modes and returns corpus chunk hits, not symbol rows.
    /// </summary>
    public static string RunTextContent(
        ITextContentSearchIndex index,
        string query,
        string contentKind,
        int limit,
        bool excludeTests,
        bool json,
        out int renderedCount,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null) =>
        RunTextContent(
            index,
            query,
            contentKind,
            limit,
            excludeTests,
            json,
            out renderedCount,
            out _,
            compactBanner,
            filePattern,
            language);

    public static string RunTextContent(
        ITextContentSearchIndex index,
        string query,
        IReadOnlyCollection<string> contentKinds,
        int limit,
        bool excludeTests,
        bool json,
        out int renderedCount,
        out long sourceBytes,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        ArgumentNullException.ThrowIfNull(contentKinds);
        if (contentKinds.Count == 0)
            throw new ArgumentException("At least one content kind is required.", nameof(contentKinds));
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);
        var hits = new List<TextContentSearchHit>();
        var outsideScope = new List<TextContentSearchHit>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            hits.Clear();
            outsideScope.Clear();
            IReadOnlyList<TextContentSearchHit> fetched = index.Search(query, contentKinds, window, excludeTests);
            foreach (TextContentSearchHit hit in fetched)
            {
                if (filters.Allows(hit.DisplayPath, hit.Language))
                    hits.Add(hit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(hit);
            }
            return (fetched.Count, hits.Count);
        });

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
        {
            sourceBytes = 0;
            if (json)
                return "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissTextContentCompact(filters, compactBanner, outsideScope)
                : ReadToolWorkspaceRouting.PrefixCompact(TextContentEmptyHint(contentKinds, query), compactBanner);
        }

        sourceBytes = hits
            .Take(page)
            .GroupBy(static hit => hit.SourceId, StringComparer.Ordinal)
            .Sum(static group => group.Max(static hit => hit.SourceBytes));

        return json
            ? RenderTextContentJson(hits, page)
            : RenderTextContentCompact(hits, page, total, compactBanner);
    }

    public static string RunTextContent(
        ITextContentSearchIndex index,
        string query,
        string contentKind,
        int limit,
        bool excludeTests,
        bool json,
        out int renderedCount,
        out long sourceBytes,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKind);
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);
        var hits = new List<TextContentSearchHit>();
        var outsideScope = new List<TextContentSearchHit>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            hits.Clear();
            outsideScope.Clear();
            IReadOnlyList<TextContentSearchHit> fetched = index.Search(query, contentKind, window, excludeTests);
            foreach (TextContentSearchHit hit in fetched)
            {
                if (filters.Allows(hit.DisplayPath, hit.Language))
                    hits.Add(hit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(hit);
            }
            return (fetched.Count, hits.Count);
        });

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
        {
            sourceBytes = 0;
            if (json)
                return "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissTextContentCompact(filters, compactBanner, outsideScope)
                : ReadToolWorkspaceRouting.PrefixCompact(TextContentEmptyHint([contentKind], query), compactBanner);
        }

        sourceBytes = hits
            .Take(page)
            .GroupBy(static hit => hit.SourceId, StringComparer.Ordinal)
            .Sum(static group => group.Max(static hit => hit.SourceBytes));

        return json
            ? RenderTextContentJson(hits, page)
            : RenderTextContentCompact(hits, page, total, compactBanner);
    }

    /// <summary>
    /// The pure source-region search execution core (no MCP/DI/telemetry). This is a distinct result kind from
    /// symbol and content search: each hit is text inside a comment, doc-comment, or string literal.
    /// </summary>
    public static string RunRegions(
        IRegionSearchIndex index,
        string query,
        IReadOnlySet<string> kinds,
        int limit,
        bool excludeTests,
        bool json,
        out int renderedCount,
        string? compactBanner = null,
        string? modeNote = null,
        string? filePattern = null,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        if (limit < 1) limit = 1;

        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);
        var hits = new List<RegionSearchHit>();
        var outsideScope = new List<RegionSearchHit>(OutsideScopeHintLimit);
        FetchWithEscalation(overFetch, limit, window =>
        {
            hits.Clear();
            outsideScope.Clear();
            IReadOnlyList<RegionSearchHit> fetched = index.Search(query, kinds, window, excludeTests);
            foreach (RegionSearchHit hit in fetched)
            {
                if (filters.Allows(hit.Path, hit.Language))
                    hits.Add(hit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(hit);
            }
            return (fetched.Count, hits.Count);
        });

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        string? prefix = CombineCompactPrefix(compactBanner, modeNote);
        if (total == 0)
        {
            if (json)
                return "[]";
            return outsideScope.Count > 0
                ? RenderFilteredMissRegionCompact(filters, prefix, outsideScope)
                : ReadToolWorkspaceRouting.PrefixCompact("No results.", prefix);
        }

        return json
            ? RenderRegionJson(hits, page)
            : RenderRegionCompact(hits, page, total, prefix);
    }

    // tri-state: null → hide only for NL phrases lacking test/def intent; true → always; false → never.
    internal static bool ResolveExcludeTests(bool? excludeTests, string query, SearchToolMode mode)
    {
        if (excludeTests is { } forced)
            return forced;
        // mode=text or an inferred NL phrase → auto-hide unless the phrase signals test/def intent.
        bool isPhrase = mode == SearchToolMode.Text || IsNaturalLanguagePhrase(query);
        if (!isPhrase)
            return false;
        return !HasTestOrDefIntent(query);
    }

    internal static bool ResolveHideLowSignalKinds(string query, SearchToolMode mode) =>
        mode == SearchToolMode.Text ||
        (mode == SearchToolMode.Auto && IsNaturalLanguagePhrase(query));

    private static bool IsLowSignalKind(string kind) =>
        string.Equals(kind, "import", StringComparison.Ordinal) ||
        string.Equals(kind, "module", StringComparison.Ordinal);

    private sealed record AutoTextRescueResult(string Output, int Count, long SourceBytes, string Kind);

    private static bool ShouldRunAutoTextRescue(
        SearchRoute route,
        bool json,
        string query,
        int primaryCount,
        ISymbolLookupIndex index)
    {
        if (json || route.Kind != SearchRouteKind.Symbols || route.Mode != SearchToolMode.Auto)
            return false;
        if (IsPathLikeQuery(query, index))
            return false;
        if (primaryCount == 0)
            return true;
        if (HasConcreteExactDefinition(index, query))
            return false;
        return LooksLikeSourceBodyQuery(query) ||
               LooksLikeDocsOrConfigQuery(query) ||
               LooksLikeWeakIdentifierQuery(query);
    }

    private AutoTextRescueResult? TryRunAutoTextRescue(
        string query,
        int limit,
        bool? excludeTests,
        string primaryOutput,
        int primaryCount,
        string? workspaceId,
        bool ensureFresh,
        string? filePattern,
        string? language,
        string? compactBanner)
    {
        try
        {
            WorkspaceTextContentSearchContext textContent =
                _textContentProvider.ResolveTextContentSearch(workspaceId, ensureFresh);
            bool preferDocsConfig = LooksLikeDocsOrConfigQuery(query);
            if (preferDocsConfig &&
                TryRunAutoDocsConfigRescue(
                    textContent.Index,
                    query,
                    limit,
                    primaryOutput,
                    primaryCount,
                    compactBanner,
                    filePattern,
                    language) is { Count: > 0 } docsRescue)
            {
                return docsRescue;
            }

            bool hideTests = ResolveExcludeTests(excludeTests, query, SearchToolMode.Source);
            AutoTextRescueResult sourceRescue = RunAutoSourceRescue(
                textContent.Index,
                query,
                limit,
                hideTests,
                primaryOutput,
                primaryCount,
                compactBanner,
                filePattern,
                language);
            if (sourceRescue.Count > 0)
                return sourceRescue;

            if (!preferDocsConfig &&
                TryRunAutoDocsConfigRescue(
                    textContent.Index,
                    query,
                    limit,
                    primaryOutput,
                    primaryCount,
                    compactBanner,
                    filePattern,
                    language) is { Count: > 0 } lateDocsRescue)
            {
                return lateDocsRescue;
            }

            return sourceRescue;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static AutoTextRescueResult RunAutoSourceRescue(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool hideTests,
        string primaryOutput,
        int primaryCount,
        string? compactBanner,
        string? filePattern,
        string? language)
    {
        string sourceOutput = RunTextContent(
            index,
            query,
            TextContentKind.WorkspaceSource,
            limit: Math.Min(Math.Max(limit, 1), 2),
            hideTests,
            json: false,
            out int sourceCount,
            out long sourceBytes,
            compactBanner: null,
            filePattern,
            language);
        return sourceCount == 0
            ? new AutoTextRescueResult(primaryOutput, 0, sourceBytes, "none")
            : new AutoTextRescueResult(
                RenderAutoTextRescueCompact(
                    primaryOutput,
                    primaryCount,
                    sourceOutput,
                    compactBanner,
                    "Source matches also found:",
                    "Rerun with mode=source for more source snippets."),
                sourceCount,
                sourceBytes,
                "source");
    }

    private static AutoTextRescueResult? TryRunAutoDocsConfigRescue(
        ITextContentSearchIndex index,
        string query,
        int limit,
        string primaryOutput,
        int primaryCount,
        string? compactBanner,
        string? filePattern,
        string? language)
    {
        string docsOutput = RunTextContent(
            index,
            query,
            WorkspaceContentSearchKinds,
            limit: Math.Min(Math.Max(limit, 1), 2),
            excludeTests: false,
            json: false,
            out int docsCount,
            out long sourceBytes,
            compactBanner: null,
            filePattern,
            language);
        return docsCount == 0
            ? null
            : new AutoTextRescueResult(
                RenderAutoTextRescueCompact(
                    primaryOutput,
                    primaryCount,
                    docsOutput,
                    compactBanner,
                    "Docs/config matches also found:",
                    "Rerun with mode=content for more docs/config snippets."),
                docsCount,
                sourceBytes,
                "docs_config");
    }

    private static string RenderAutoTextRescueCompact(
        string primaryOutput,
        int primaryCount,
        string rescueOutput,
        string? compactBanner,
        string heading,
        string rerunHint)
    {
        var sb = new StringBuilder();
        if (primaryCount > 0)
        {
            sb.Append(primaryOutput.TrimEnd('\n')).Append("\n\n");
        }
        else if (!string.IsNullOrWhiteSpace(compactBanner))
        {
            sb.Append(compactBanner).Append('\n');
        }

        sb.Append(heading).Append('\n');
        sb.Append(rescueOutput.TrimEnd('\n'));
        sb.Append('\n').Append(rerunHint);
        return sb.ToString();
    }

    private static bool HasConcreteExactDefinition(ISymbolLookupIndex index, string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
            return false;
        string queryLower = trimmed.ToLowerInvariant();
        foreach (IndexedSymbol candidate in index.FindByName(trimmed))
        {
            if (!IsLowSignalKind(candidate.Kind) && IsDefinitionNameMatch(candidate.Name, queryLower))
                return true;
        }
        return false;
    }

    private static bool LooksLikeSourceBodyQuery(string query)
    {
        if (IsNaturalLanguagePhrase(query))
            return true;
        foreach (char ch in query)
        {
            if (char.IsPunctuation(ch) && ch is not '_' and not '-' and not '.')
                return true;
        }
        return false;
    }

    private static bool LooksLikeDocsOrConfigQuery(string query)
    {
        string lower = query.ToLowerInvariant();
        return lower.Contains("config", StringComparison.Ordinal) ||
               lower.Contains("configuration", StringComparison.Ordinal) ||
               lower.Contains("doc", StringComparison.Ordinal) ||
               lower.Contains("guide", StringComparison.Ordinal) ||
               lower.Contains("install", StringComparison.Ordinal) ||
               lower.Contains("quickstart", StringComparison.Ordinal) ||
               lower.Contains("readme", StringComparison.Ordinal) ||
               lower.Contains("setup", StringComparison.Ordinal) ||
               lower.Contains("usage", StringComparison.Ordinal) ||
               lower.Contains("workspace health", StringComparison.Ordinal) ||
               lower.Contains("workspace status", StringComparison.Ordinal);
    }

    private static bool LooksLikeWeakIdentifierQuery(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length < 4)
            return false;
        bool hasLetter = false;
        foreach (char ch in trimmed)
        {
            if (char.IsLetter(ch))
            {
                hasLetter = true;
                continue;
            }
            if (!char.IsDigit(ch) && ch is not '_' and not '-' and not '.')
                return false;
        }
        return hasLetter;
    }

    // A natural-language phrase = multiple whitespace-delimited words (a single identifier-ish token is not).
    private static bool IsNaturalLanguagePhrase(string query)
    {
        int words = query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 2;
    }

    private static bool HasTestOrDefIntent(string query)
    {
        foreach (var word in query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("specs", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // mode=auto routing: a bare separator is NOT enough to mean "file path" — `src/utils#helper`,
    // `src/Run(query)`, and phrase queries containing a '/' must stay on the symbol/text arm. A query is
    // path-SHAPED only when it ends in a known indexed extension, has multiple separators, or has exactly one
    // separator with no whitespace and none of the symbol-syntax characters `< > # : ( )`.
    private static bool IsPathLikeQuery(string query, ISymbolLookupIndex index)
    {
        string trimmed = query.Trim();

        string ext = Path.GetExtension(trimmed);
        if (ext.Length > 1 && index.KnownExtensions.Contains(ext))
            return true;

        int separators = 0;
        foreach (char c in trimmed)
        {
            if (c is '/' or '\\')
                separators++;
        }

        if (separators == 0)
            return false;
        if (separators > 1)
            return true;

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || c is '<' or '>' or '#' or ':' or '(' or ')')
                return false;
        }
        return true;
    }

    private static IReadOnlySet<string> ReadHasDocCommentBestEffort(
        string dbPath,
        IReadOnlyCollection<string> symbolIds)
    {
        try
        {
            return SqliteSourceRegionReader.ReadHasDocComment(dbPath, symbolIds);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string RenderFilteredMissCompact(
        ToolSearchFilters filters,
        string? compactBanner,
        IReadOnlyList<IndexedSymbol> outsideScope)
    {
        var sb = FilteredMissHeader(filters, compactBanner, outsideScope.Select(static symbol => symbol.FilePath));
        foreach (IndexedSymbol s in outsideScope)
        {
            sb.Append('\n')
              .Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine);
            if (IsLowSignalKind(s.Kind))
                sb.Append("  low_signal");
            else if (!string.IsNullOrEmpty(s.Signature))
                sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        }
        return sb.ToString();
    }

    private static string RenderFilteredMissContentCompact(
        ToolSearchFilters filters,
        string? compactBanner,
        IReadOnlyList<ContentSearchHit> outsideScope)
    {
        var sb = FilteredMissHeader(filters, compactBanner, outsideScope.Select(static hit => hit.Path));
        foreach (ContentSearchHit h in outsideScope)
        {
            sb.Append('\n').Append(h.Path).Append(':').Append(h.Line);
            foreach (string line in h.Snippet.Split('\n'))
                sb.Append('\n').Append("    ").Append(line);
        }
        return sb.ToString();
    }

    private static string RenderFilteredMissTextContentCompact(
        ToolSearchFilters filters,
        string? compactBanner,
        IReadOnlyList<TextContentSearchHit> outsideScope)
    {
        var sb = FilteredMissHeader(filters, compactBanner, outsideScope.Select(static hit => hit.DisplayPath));
        foreach (TextContentSearchHit h in outsideScope)
        {
            sb.Append('\n').Append(h.DisplayPath).Append(':').Append(h.Line).Append("  ").Append(h.ContentKind);
            if (!string.IsNullOrWhiteSpace(h.ContainingSymbolName))
                sb.Append("  ").Append(h.ContainingSymbolName);
            foreach (string line in h.Snippet.Split('\n'))
                sb.Append('\n').Append("    ").Append(line);
        }
        return sb.ToString();
    }

    private static string RenderFilteredMissRegionCompact(
        ToolSearchFilters filters,
        string? compactPrefix,
        IReadOnlyList<RegionSearchHit> outsideScope)
    {
        var sb = FilteredMissHeader(filters, compactPrefix, outsideScope.Select(static hit => hit.Path));
        foreach (RegionSearchHit h in outsideScope)
        {
            sb.Append('\n').Append(h.Path).Append(':').Append(h.Line).Append("  ").Append(h.Kind);
            if (!string.IsNullOrWhiteSpace(h.ContainingSymbolName))
                sb.Append("  ").Append(h.ContainingSymbolName);
            foreach (string line in h.Snippet.Split('\n'))
                sb.Append('\n').Append("    ").Append(line);
        }
        return sb.ToString();
    }

    private static StringBuilder FilteredMissHeader(
        ToolSearchFilters filters,
        string? compactPrefix,
        IEnumerable<string> outsideScopePaths)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactPrefix))
            sb.Append(compactPrefix).Append('\n');
        sb.Append("No results within ").Append(filters.ScopeDescription).Append('.');
        string? nestedFilePatternHint = filters.NestedFilePatternHint(outsideScopePaths);
        if (!string.IsNullOrWhiteSpace(nestedFilePatternHint))
            sb.Append('\n').Append(nestedFilePatternHint);
        sb.Append('\n').Append("Outside scope:");
        return sb;
    }

    private static string RenderCompact(
        IReadOnlyList<IndexedSymbol> kept,
        int page,
        int total,
        string query,
        string? compactBanner,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        int definitionIndex = FindPromotableDefinitionIndex(kept, page, query);
        if (definitionIndex >= 0)
            return RenderDefinitionCompact(kept, page, total, definitionIndex, query, compactBanner, hasDocSymbolIds);

        var groups = new List<(string FilePath, List<IndexedSymbol> Symbols)>();
        for (int i = 0; i < page; i++)
        {
            IndexedSymbol symbol = kept[i];
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<IndexedSymbol> { symbol }));
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');
        if (groups.Count == page)
        {
            // Every hit is in a distinct file: a path-per-row line is strictly cheaper than a group header.
            for (int i = 0; i < page; i++)
            {
                var s = kept[i];
                sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
                  .Append(s.FilePath).Append(':').Append(s.StartLine);
                AppendSymbolAnnotations(sb, s, hasDocSymbolIds);
                if (i < page - 1)
                    sb.Append('\n');
            }
        }
        else
        {
            // A file repeats: print its path once and rank-ordered rows under it (group order = best hit's rank).
            for (int g = 0; g < groups.Count; g++)
            {
                if (g > 0)
                    sb.Append('\n');
                sb.Append(groups[g].FilePath).Append(':');
                foreach (IndexedSymbol s in groups[g].Symbols)
                {
                    sb.Append('\n').Append("  :").Append(s.StartLine)
                      .Append(' ').Append(s.Name).Append(' ').Append(s.Kind);
                    AppendSymbolAnnotations(sb, s, hasDocSymbolIds);
                }
            }
        }
        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static string RenderEmptySymbolMissCompact(
        string? compactBanner,
        IReadOnlyList<IndexedSymbol> suggestions)
    {
        string output = ReadToolWorkspaceRouting.PrefixCompact(SymbolNoResultsHint, compactBanner);
        if (suggestions.Count == 0)
            return output;

        string list = string.Join(", ", suggestions.Select(static s => $"{s.Name} ({s.FilePath}:{s.StartLine})"));
        return output + "\nTry: " + list;
    }

    private static void AppendSymbolAnnotations(StringBuilder sb, IndexedSymbol s, IReadOnlySet<string>? hasDocSymbolIds)
    {
        if (IsLowSignalKind(s.Kind))
            sb.Append("  low_signal");
        else if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        if (hasDocSymbolIds?.Contains(s.SymbolId) == true)
            sb.Append("  has_doc");
    }

    private static string RenderFileCompact(
        IReadOnlyList<IndexedSymbol> kept,
        int page,
        int total,
        string? compactBanner,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var groups = new List<(string FilePath, List<IndexedSymbol> Symbols)>();
        for (int i = 0; i < page; i++)
        {
            IndexedSymbol symbol = kept[i];
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<IndexedSymbol> { symbol }));
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');

        if (groups.Count == 1)
        {
            sb.Append("File match: ").Append(groups[0].FilePath).Append('\n');
            AppendFileModeSymbols(sb, groups[0].Symbols, hasDocSymbolIds);
        }
        else
        {
            sb.Append("File matches:").Append('\n');
            foreach (var group in groups)
            {
                sb.Append(group.FilePath).Append(':').Append('\n');
                AppendFileModeSymbols(sb, group.Symbols, hasDocSymbolIds);
            }
        }

        TrimTrailingNewlines(sb);
        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static void AppendFileModeSymbols(
        StringBuilder sb,
        IReadOnlyList<IndexedSymbol> symbols,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        foreach (IndexedSymbol symbol in symbols)
        {
            sb.Append("  :").Append(symbol.StartLine)
              .Append(' ').Append(symbol.Name)
              .Append(' ').Append(symbol.Kind);
            if (hasDocSymbolIds?.Contains(symbol.SymbolId) == true)
                sb.Append(" has_doc");
            sb.Append('\n');
        }
    }

    private static string RenderDefinitionCompact(
        IReadOnlyList<IndexedSymbol> kept,
        int page,
        int total,
        int definitionIndex,
        string query,
        string? compactBanner,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');

        IndexedSymbol definition = kept[definitionIndex];
        sb.Append("Definition found: ").Append(query.Trim()).Append('\n');
        AppendPromotedDefinition(sb, definition, hasDocSymbolIds);

        var otherRows = new List<IndexedSymbol>(Math.Max(0, page - 1));
        for (int i = 0; i < page; i++)
        {
            if (i != definitionIndex)
                otherRows.Add(kept[i]);
        }

        if (otherRows.Count > 0)
        {
            sb.Append('\n').Append("Other matches:").Append('\n').Append('\n');
            AppendOtherMatchesGroupedByFile(sb, otherRows, hasDocSymbolIds);
        }

        TrimTrailingNewlines(sb);
        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static int FindPromotableDefinitionIndex(IReadOnlyList<IndexedSymbol> kept, int page, string query)
    {
        string queryLower = query.Trim().ToLowerInvariant();
        if (queryLower.Length == 0)
            return -1;

        for (int i = 0; i < page; i++)
        {
            IndexedSymbol symbol = kept[i];
            if (!IsLowSignalKind(symbol.Kind) && IsDefinitionNameMatch(symbol.Name, queryLower))
                return i;
        }

        return -1;
    }

    private static bool IsDefinitionNameMatch(string symbolName, string queryLower)
    {
        string nameLower = symbolName.ToLowerInvariant();
        if (nameLower == queryLower)
            return true;
        if (nameLower.LastIndexOf('.') is int lastDot && lastDot >= 0 &&
            nameLower[(lastDot + 1)..] == queryLower)
            return true;
        if (queryLower.Contains('.', StringComparison.Ordinal) && nameLower.EndsWith(queryLower, StringComparison.Ordinal))
        {
            int prefixLength = nameLower.Length - queryLower.Length;
            return prefixLength == 0 || nameLower[prefixLength - 1] == '.';
        }
        return false;
    }

    private static void AppendPromotedDefinition(
        StringBuilder sb,
        IndexedSymbol symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append("  ").Append(symbol.FilePath).Append(':').Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        if (hasDocSymbolIds?.Contains(symbol.SymbolId) == true)
            sb.Append(" has_doc");
        sb.Append('\n');

        if (!string.IsNullOrEmpty(symbol.Signature))
            sb.Append("  ").Append(Truncate(symbol.Signature!, SignatureMaxLength)).Append('\n');
    }

    private static void AppendOtherMatchesGroupedByFile(
        StringBuilder sb,
        IReadOnlyList<IndexedSymbol> symbols,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var groups = new List<(string FilePath, List<IndexedSymbol> Symbols)>();
        foreach (IndexedSymbol symbol in symbols)
        {
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<IndexedSymbol> { symbol }));
        }

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group.Symbols.Count == 1)
            {
                AppendSingleOtherMatch(sb, group.Symbols[0], hasDocSymbolIds);
            }
            else
            {
                sb.Append(group.FilePath).Append(':').Append('\n');
                foreach (IndexedSymbol symbol in group.Symbols)
                    AppendGroupedOtherMatch(sb, symbol, hasDocSymbolIds);
            }

            if (i < groups.Count - 1)
                sb.Append('\n');
        }
    }

    private static void AppendSingleOtherMatch(
        StringBuilder sb,
        IndexedSymbol symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append(symbol.FilePath).Append(':').Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        AppendCompactMatchDetails(sb, symbol, "  ", hasDocSymbolIds);
    }

    private static void AppendGroupedOtherMatch(
        StringBuilder sb,
        IndexedSymbol symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append("  :").Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        AppendCompactMatchDetails(sb, symbol, "    ", hasDocSymbolIds);
    }

    private static void AppendCompactMatchDetails(
        StringBuilder sb,
        IndexedSymbol symbol,
        string continuationIndent,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        if (hasDocSymbolIds?.Contains(symbol.SymbolId) == true)
            sb.Append(" has_doc");

        if (IsLowSignalKind(symbol.Kind))
        {
            sb.Append(" low_signal").Append('\n');
            return;
        }

        if (!string.IsNullOrEmpty(symbol.Signature))
            sb.Append('\n').Append(continuationIndent).Append(Truncate(symbol.Signature!, SignatureMaxLength));
        sb.Append('\n');
    }

    private static void TrimTrailingNewlines(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is '\n' or '\r')
            sb.Length--;
    }

    private static string RenderJson(
        IReadOnlyList<IndexedSymbol> kept,
        IReadOnlyList<double> scores,
        int page,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            for (int i = 0; i < page; i++)
            {
                var s = kept[i];
                writer.WriteStartObject();
                writer.WriteString("name", s.Name);
                writer.WriteString("kind", s.Kind);
                writer.WriteString("file", s.FilePath);
                writer.WriteNumber("line", s.StartLine);
                if (s.Signature is null) writer.WriteNull("signature");
                else writer.WriteString("signature", s.Signature);
                writer.WriteNumber("score", scores[i]);
                writer.WriteString("symbol_id", s.SymbolId);
                if (hasDocSymbolIds is not null)
                    writer.WriteBoolean("has_doc", hasDocSymbolIds.Contains(s.SymbolId));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderEmptyJson(IReadOnlyList<IndexedSymbol> suggestions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("results");
            writer.WriteEndArray();
            writer.WriteStartArray("suggestions");
            foreach (IndexedSymbol suggestion in suggestions)
            {
                writer.WriteStartObject();
                writer.WriteString("name", suggestion.Name);
                writer.WriteString("kind", suggestion.Kind);
                writer.WriteString("file", suggestion.FilePath);
                writer.WriteNumber("line", suggestion.StartLine);
                if (suggestion.Signature is null) writer.WriteNull("signature");
                else writer.WriteString("signature", suggestion.Signature);
                writer.WriteString("symbol_id", suggestion.SymbolId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderRegionCompact(
        IReadOnlyList<RegionSearchHit> hits,
        int page,
        int total,
        string? compactPrefix)
    {
        var blocks = new List<string>(page);
        for (int i = 0; i < page; i++)
        {
            RegionSearchHit h = hits[i];
            var block = new StringBuilder();
            block.Append(h.Path).Append(':').Append(h.Line).Append("  ").Append(h.Kind);
            if (!string.IsNullOrWhiteSpace(h.ContainingSymbolName))
                block.Append("  ").Append(h.ContainingSymbolName);
            foreach (string line in h.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactPrefix))
            sb.Append(compactPrefix).Append('\n');
        sb.Append(string.Join("\n\n", blocks));

        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static string RenderRegionJson(IReadOnlyList<RegionSearchHit> hits, int page)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            for (int i = 0; i < page; i++)
            {
                RegionSearchHit h = hits[i];
                writer.WriteStartObject();
                writer.WriteString("file", h.Path);
                writer.WriteNumber("line", h.Line);
                writer.WriteString("kind", h.Kind);
                writer.WriteNumber("score", h.Score);
                writer.WriteString("snippet", h.Snippet);
                writer.WriteString("region_id", h.RegionId);
                if (h.ContainingSymbolId is null) writer.WriteNull("containing_symbol_id");
                else writer.WriteString("containing_symbol_id", h.ContainingSymbolId);
                if (h.ContainingSymbolName is null) writer.WriteNull("containing_symbol_name");
                else writer.WriteString("containing_symbol_name", h.ContainingSymbolName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? CombineCompactPrefix(string? compactBanner, string? modeNote)
    {
        if (string.IsNullOrWhiteSpace(compactBanner))
            return string.IsNullOrWhiteSpace(modeNote) ? null : modeNote;
        return string.IsNullOrWhiteSpace(modeNote) ? compactBanner : compactBanner + '\n' + modeNote;
    }

    // Each hit is a `path:line` header followed by its snippet window (±2 lines), each snippet line indented
    // for visual nesting; hits are separated by a blank line. A distinct shape from the symbol renderer.
    private static string RenderContentCompact(
        IReadOnlyList<ContentSearchHit> hits, int page, int total, string? compactBanner)
    {
        var blocks = new List<string>(page);
        for (int i = 0; i < page; i++)
        {
            var h = hits[i];
            var block = new StringBuilder();
            block.Append(h.Path).Append(':').Append(h.Line);
            foreach (var line in h.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');
        sb.Append(string.Join("\n\n", blocks));

        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static string RenderContentJson(IReadOnlyList<ContentSearchHit> hits, int page)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            for (int i = 0; i < page; i++)
            {
                var h = hits[i];
                writer.WriteStartObject();
                writer.WriteString("file", h.Path);
                writer.WriteNumber("line", h.Line);
                writer.WriteNumber("score", h.Score);
                writer.WriteString("snippet", h.Snippet);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderTextContentCompact(
        IReadOnlyList<TextContentSearchHit> hits, int page, int total, string? compactBanner)
    {
        var blocks = new List<string>(page);
        for (int i = 0; i < page; i++)
        {
            TextContentSearchHit h = hits[i];
            var block = new StringBuilder();
            block.Append(h.DisplayPath).Append(':').Append(h.Line).Append("  ").Append(h.ContentKind);
            if (!string.IsNullOrWhiteSpace(h.ContainingSymbolName))
                block.Append("  ").Append(h.ContainingSymbolName);
            foreach (string line in h.Snippet.Split('\n'))
                block.Append('\n').Append("    ").Append(line);
            blocks.Add(block.ToString());
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');
        sb.Append(string.Join("\n\n", blocks));

        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static string RenderTextContentJson(IReadOnlyList<TextContentSearchHit> hits, int page)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            for (int i = 0; i < page; i++)
            {
                TextContentSearchHit h = hits[i];
                writer.WriteStartObject();
                writer.WriteString("source_id", h.SourceId);
                writer.WriteString("chunk_id", h.ChunkId);
                writer.WriteString("content_kind", h.ContentKind);
                if (h.Path is null) writer.WriteNull("path");
                else writer.WriteString("path", h.Path);
                if (h.Url is null) writer.WriteNull("url");
                else writer.WriteString("url", h.Url);
                writer.WriteString("display_path", h.DisplayPath);
                writer.WriteString("language", h.Language);
                writer.WriteNumber("line", h.Line);
                writer.WriteNumber("line_start", h.LineStart);
                writer.WriteNumber("line_end", h.LineEnd);
                writer.WriteNumber("byte_start", h.ByteStart);
                writer.WriteNumber("byte_end", h.ByteEnd);
                writer.WriteNumber("score", h.Score);
                writer.WriteString("snippet", h.Snippet);
                writer.WriteNumber("source_bytes", h.SourceBytes);
                if (h.ContainingSymbolId is null) writer.WriteNull("containing_symbol_id");
                else writer.WriteString("containing_symbol_id", h.ContainingSymbolId);
                if (h.ContainingSymbolName is null) writer.WriteNull("containing_symbol_name");
                else writer.WriteString("containing_symbol_name", h.ContainingSymbolName);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private sealed class UnavailableRegionSearchProvider : IWorkspaceRegionSearchProvider
    {
        public WorkspaceRegionSearchContext ResolveRegionSearch(string? workspaceId, bool ensureFresh) =>
            throw new InvalidOperationException("region search provider is not configured.");
    }

    private sealed class UnavailableTextContentSearchProvider : IWorkspaceTextContentSearchProvider
    {
        public WorkspaceTextContentSearchContext ResolveTextContentSearch(string? workspaceId, bool ensureFresh) =>
            throw new InvalidOperationException("text content search provider is not configured.");
    }
}
