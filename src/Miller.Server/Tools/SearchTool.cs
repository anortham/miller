using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The rescue/mode half of the local semantic arm (ADR-0003, design §6.3): symbol-card and docs-chunk KNN for
/// one workspace root. Separate from <see cref="ISymbolFusionArm"/> because these callers do not fuse a lexical
/// symbol ranking — they add a last-resort affordance, reorder chunk hits, or ask only whether the artifact
/// could have been consulted at all.
/// </summary>
/// <remarks>
/// The root is per call rather than per instance: read tools route by <c>workspace_id</c>, so the artifact a
/// query must consult is the one belonging to the workspace that query resolved to.
/// </remarks>
internal interface ISemanticTextArm
{
    SemanticQueryResult QuerySymbols(string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow);

    SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k);
}

/// <summary>
/// The production <see cref="ISemanticTextArm"/>. Only <see cref="SemanticMode.On"/> retrieves: under
/// <c>shadow</c> vectors are built and evaluated but never served, and under <c>off</c> nothing is asked at
/// all — so neither mode may open an arm, launch a sidecar, or stat an artifact.
/// </summary>
internal sealed class SemanticTextArm(SemanticMode mode, Func<string, SemanticSearchArm> openArm)
    : ISemanticTextArm
{
    private static readonly SemanticQueryResult NotServing =
        SemanticQueryResult.Unavailable("Semantic retrieval is not serving results in this mode.");

    /// <summary>
    /// Composes the arm from the two registered services that carry the whole activation decision, or returns
    /// null when either is absent — which is how a host without the semantic graph stays lexical-only.
    /// </summary>
    public static ISemanticTextArm? For(VectorSidecar? sidecar, SemanticEmbeddingSessionBroker? broker) =>
        sidecar is null || broker is null
            ? null
            : new SemanticTextArm(
                sidecar.Mode,
                root => new SemanticSearchArm(root, sidecar, broker));

    public SemanticQueryResult QuerySymbols(
        string workspaceRoot, string query, int k, Func<VectorMatch, bool>? allow) =>
        mode is SemanticMode.On
            ? openArm(workspaceRoot).QuerySymbolsAsync(query, k, allow).GetAwaiter().GetResult()
            : NotServing;

    public SemanticQueryResult QueryChunks(string workspaceRoot, string query, int k) =>
        mode is SemanticMode.On
            ? openArm(workspaceRoot).QueryChunksAsync(query, k).GetAwaiter().GetResult()
            : NotServing;
}

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

internal enum SearchServingPolicy
{
    Lexical,
    Production,
    Treatment,
    Shadow,
}

internal enum SearchRetrievalMode
{
    Auto,
    Lexical,
    Hybrid,
    Semantic,
}

internal sealed record CanaryVectorProbe(string State, SemanticGenerationIdentity? Identity)
{
    public static CanaryVectorProbe From(VectorSidecarFacts facts) => new(facts.State, facts.Identity);
}

/// <summary>
/// The output of symbol candidate generation: the ordered candidates plus the miss-path facts rendering needs
/// when there are none. <see cref="OutsideScope"/> holds the bounded sample of hits a file/language filter
/// excluded and <see cref="EmptySuggestions"/> the near-match symbols for an empty result — both are computed
/// during generation so rendering never touches the index.
/// </summary>
internal sealed record SymbolCandidateSet(
    IReadOnlyList<SymbolCandidate> Candidates,
    IReadOnlyList<SymbolCandidate> OutsideScope,
    IReadOnlyList<IndexedSymbol> EmptySuggestions,
    bool FileMode,
    ToolSearchFilters Filters,
    SymbolVisibilityPolicy? Visibility = null,
    bool Relaxed = false,
    bool Mixed = false);

/// <summary>
/// The visibility predicate candidate generation applied, carried forward so another retrieval arm can admit
/// only symbols the lexical arm would also have shown. Without it a semantic hit could surface a test symbol
/// or an out-of-filter file that the same query answered lexically would have hidden.
/// </summary>
internal sealed record SymbolVisibilityPolicy(bool HideTests, bool HideLowSignalKinds, ToolSearchFilters Filters)
{
    public bool Allows(IndexedSymbol symbol) =>
        !(HideTests && IsTestPath.IsTest(symbol)) &&
        !(HideLowSignalKinds && SearchTool.IsLowSignalKind(symbol.Kind)) &&
        Filters.Allows(symbol.FilePath, symbol.Language);
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
    // Bounded by the compact empty budget (≤400 chars), not by taste: at 5 the `Try:` line alone measured ~378,
    // which put every suggestion-bearing miss over budget before the diagnosis text was even added. Capping the
    // SOURCE rather than the render keeps compact and JSON agreeing and drops nothing silently.
    private const int EmptySuggestionLimit = 3;
    private const string RegionsUsageHint =
        "regions must be comment, doc_comment, or string_literal. Example: regions=comment or regions=doc_comment,string_literal.";

    private static readonly string[] WorkspaceContentSearchKinds =
    [
        TextContentKind.WorkspaceDocs,
        TextContentKind.WorkspaceConfig,
    ];

    private static readonly HashSet<string> PathQueryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py", ".rs", ".go", ".java", ".kt", ".kts", ".swift", ".rb", ".php",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp",
        ".json", ".yaml", ".yml", ".toml", ".xml", ".sql",
        ".md", ".txt", ".sh", ".ps1", ".html", ".css", ".scss", ".razor", ".cshtml",
    };

    private readonly IWorkspaceSearchProvider _workspaceProvider;
    private readonly IWorkspaceRegionSearchProvider _regionProvider;
    private readonly IWorkspaceTextContentSearchProvider _textContentProvider;
    private readonly ISymbolFusionArm? _fusionArm;
    private readonly ISemanticTextArm? _semanticArm;
    private readonly VectorSidecar? _semanticSidecar;
    private readonly SemanticEmbeddingSessionBroker? _embeddingBroker;

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
        IWorkspaceTextContentSearchProvider textContentProvider,
        ISymbolFusionArm? fusionArm = null,
        VectorSidecar? semanticSidecar = null,
        SemanticEmbeddingSessionBroker? embeddingBroker = null)
        : this(
            workspaceProvider,
            contentProvider,
            regionProvider,
            textContentProvider,
            fusionArm,
            SemanticTextArm.For(semanticSidecar, embeddingBroker),
            semanticSidecar,
            embeddingBroker)
    {
    }

    internal SearchTool(
        IWorkspaceSearchProvider workspaceProvider,
        IWorkspaceContentSearchProvider contentProvider,
        IWorkspaceRegionSearchProvider regionProvider,
        IWorkspaceTextContentSearchProvider textContentProvider,
        ISymbolFusionArm? fusionArm,
        ISemanticTextArm? semanticArm,
        VectorSidecar? semanticSidecar = null,
        SemanticEmbeddingSessionBroker? embeddingBroker = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(contentProvider);
        ArgumentNullException.ThrowIfNull(regionProvider);
        ArgumentNullException.ThrowIfNull(textContentProvider);
        _workspaceProvider = workspaceProvider;
        _regionProvider = regionProvider;
        _textContentProvider = textContentProvider;
        _fusionArm = fusionArm;
        _semanticArm = semanticArm;
        _semanticSidecar = semanticSidecar;
        _embeddingBroker = embeddingBroker;
    }

    [McpServerTool(Name = "search")]
    [Description(
        "Search indexed code and return ranked results — use this before shell rg/grep/cat or reading whole " +
        "files. Pass a symbol name, identifier, or natural-language phrase; test code is auto-hidden for phrase " +
        "queries unless exclude_tests=false. Modes: mode=markers audits TODO/FIXME/HACK/XXX in comments; " +
        "mode=content (alias docs) searches docs/config prose; mode=source searches source-body text; " +
        "mode=external/web/all-text search imported corpus text. regions=comment,doc_comment,string_literal " +
        "restricts to those source regions. Scope with file_pattern/language/limit. NOT for: a symbol you can " +
        "already name exactly (inspect it), orienting on an unfamiliar area (use context), or finding who " +
        "references a symbol (use trace). Example: search query=\"promote rebuild\" mode=source. Compact by " +
        "default; format=json to chain.")]
    public string Search(
        [Description("Symbol name, identifier, or natural-language phrase.")] string query,
        [Description("Interpretation axis: auto|text|symbol|file|markers|content|source|external|web|all-text. Default auto.")] string mode = "auto",
        [Description("Max results to return. Default 6; MCP requests above 20 are clamped.")] int limit = DefaultLimit,
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
        string? language = null,
        [Description("Symbol retrieval policy: auto|lexical|hybrid|semantic. lexical performs zero vector work. Default auto.")]
        string retrieval = "auto")
    {
        var scope = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            SearchRetrievalMode retrievalMode = ParseRetrieval(retrieval);
            SearchRoute route = SearchRoutePlanner.Plan(mode, regions, query);
            EnsureRetrievalSupportsRoute(retrievalMode, route);
            int effectiveLimit = Math.Min(limit, ToolOutputBudget.McpRowLimit);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            if (scope is not null)
            {
                ApplyTelemetryShape(scope, route, json, effectiveLimit, regions, file_pattern, language, exclude_tests);
                scope.SetMetadata("retrieval", RetrievalName(retrievalMode));
            }

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
                        effectiveLimit,
                        json,
                        exclude_tests,
                        compactBanner,
                        FilePattern: file_pattern,
                        Language: language,
                        BoundAgentOutput: true));
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
                        effectiveLimit,
                        json,
                        exclude_tests,
                        compactBanner,
                        FilePattern: file_pattern,
                        Language: language,
                        BoundAgentOutput: true));
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

                CanaryMode canaryMode = CanaryActivation.FromEnvironment();
                SemanticMode semanticMode = retrievalMode == SearchRetrievalMode.Lexical
                    ? SemanticMode.Off
                    : _semanticSidecar?.Mode ?? SemanticMode.Off;
                ContentCanaryOutcome canary = RunContentWithCanaryProbe(
                    content.Index,
                    query,
                    effectiveLimit,
                    json,
                    contentBanner,
                    file_pattern,
                    language,
                    identifier => SuggestSymbolsBestEffort(identifier, workspace_id, ensureFresh),
                    SemanticContentRerank(
                        content.Index, query, content.WorkspaceRoot, exclude_tests is true, file_pattern, language),
                    canaryMode,
                    ModeName(route.Mode),
                    semanticDisabled: retrievalMode == SearchRetrievalMode.Lexical ||
                                      semanticMode is SemanticMode.Off,
                    content.WorkspaceId ?? string.Empty,
                    content.WorkspaceRoot,
                    CanaryUtcDate(scope),
                    () => CanaryVectorProbe.From(_semanticSidecar!.Inspect(content.WorkspaceRoot)),
                    foreignWorkspace: !content.IsCurrent,
                    treatmentArmFactory: () => BuildTreatmentContentArm(content.WorkspaceRoot),
                    excludeTests: exclude_tests is true,
                    semanticMode: semanticMode);
                output = canary.Result.Output;
                count = canary.Result.Count;
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, content);
                    scope.SourceBytes = canary.Result.SourceBytes;
                    scope.SetMetadata("search_backend", "content_disk");
                    if (canary.Facts is { } contentFacts)
                        StampContentCanary(
                            scope,
                            canaryMode,
                            contentFacts,
                            canary.ResultPathHashes,
                            canary.ResultHashTruncated,
                            canary.ServingPolicy);
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
                        effectiveLimit,
                        json,
                        exclude_tests,
                        contentBanner,
                        FilePattern: file_pattern,
                        Language: language,
                        SuggestionLookup: identifier =>
                            SuggestSymbolsBestEffort(identifier, workspace_id, ensureFresh),
                        BoundAgentOutput: true));
                output = result.Output;
                count = result.Count;
                if (!json && route.Mode == SearchToolMode.Source &&
                    SourceChunksNotIndexed(query, textContent.WorkspaceRoot))
                {
                    output += SourceChunksNotIndexedNote;
                }
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
                var request = new SearchRouteExecutionRequest(
                    query,
                    effectiveLimit,
                    json,
                    exclude_tests,
                    compactBanner,
                    HasDocLookup: symbolIds => ReadHasDocCommentBestEffort(context.IndexDbPath, symbolIds),
                    FilePattern: file_pattern,
                    Language: language,
                    FusionArm: _fusionArm,
                    WorkspaceRoot: context.WorkspaceRoot,
                    BoundAgentOutput: true);

                string canaryOp = ModeName(route.Mode);
                CanaryMode canaryMode = retrievalMode == SearchRetrievalMode.Auto
                    ? CanaryActivation.FromEnvironment()
                    : CanaryMode.Off;
                SymbolCanaryOutcome canary = retrievalMode switch
                {
                    SearchRetrievalMode.Lexical => new SymbolCanaryOutcome(
                        SearchRouteExecutor.RunSymbols(
                            context.Index,
                            route,
                            request with { FusionArm = null }),
                        Facts: null),
                    SearchRetrievalMode.Hybrid => new SymbolCanaryOutcome(
                        RunRequiredHybrid(context, route, request),
                        Facts: null,
                        ServingPolicy: SearchServingPolicy.Treatment),
                    SearchRetrievalMode.Semantic => new SymbolCanaryOutcome(
                        RunRequiredSemantic(context, route, request),
                        Facts: null,
                        ServingPolicy: SearchServingPolicy.Treatment),
                    _ => RunSymbolsWithCanaryProbe(
                        context.Index,
                        route,
                        request,
                        canaryMode,
                        canaryOp,
                        semanticDisabled: (_semanticSidecar?.Mode ?? SemanticMode.Off) is SemanticMode.Off,
                        context.WorkspaceId ?? string.Empty,
                        CanaryUtcDate(scope),
                        () => CanaryVectorProbe.From(_semanticSidecar!.Inspect(context.WorkspaceRoot)),
                        foreignWorkspace: !context.IsCurrent,
                        BuildTreatmentArmFactory(context.WorkspaceRoot),
                        BuildShadowRunner(context.WorkspaceRoot),
                        _semanticSidecar?.Mode ?? SemanticMode.Off),
                };
                output = canary.Result.Output;
                count = canary.Result.Count;

                string? canaryRescueKind = canaryOp == "auto" ? "none" : null;
                IReadOnlyList<string> rescueServedPaths = [];
                if (retrievalMode is SearchRetrievalMode.Auto or SearchRetrievalMode.Lexical &&
                    ShouldRunAutoTextRescue(route, json, query, count, context.Index))
                {
                    AutoTextRescueResult? rescue = TryRunAutoTextRescue(
                        query,
                        effectiveLimit,
                        exclude_tests,
                        output,
                        count,
                        workspace_id,
                        ensureFresh,
                        file_pattern,
                        language,
                        compactBanner,
                        context,
                        canary.ServingPolicy);
                    canaryRescueKind = MapCanaryRescueKind(rescue);
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
                        rescueServedPaths = rescue.ServedPaths;
                        if (scope is not null)
                            scope.SourceBytes += rescue.SourceBytes;
                    }
                }
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, context);
                    scope.SetMetadata("search_backend", SearchBackendName(context.Index));
                    scope.SetMetadata("relaxed", canary.Result.Relaxed);
                    scope.SetMetadata("mixed", canary.Result.Mixed);
                    if (canary.ShadowFacts is { } shadowFacts)
                    {
                        CanaryTelemetry.StampShadow(scope, canaryMode, shadowFacts);
                    }
                    else if (canary.Facts is { } canaryFacts)
                    {
                        CanaryCallFacts finalFacts = canaryRescueKind is null
                            ? canaryFacts
                            : canaryFacts with { RescueKind = canaryRescueKind };
                        // Rescue rows the agent actually follows up on are content rows served after the primary
                        // page; without their path digests attribution is arm-differential (weak lexical results
                        // rescue more often), so fold them into the served path hashes before stamping.
                        if (rescueServedPaths.Count > 0 && finalFacts.Eligibility == CanaryEligibility.Eligible)
                            finalFacts = finalFacts with { ResultCount = count, AdditionalServedPaths = rescueServedPaths };
                        StampSymbolCanary(scope, canaryMode, finalFacts, canary.ServingPolicy);
                    }
                }
            }

            ToolDiagnostic? diagnostic =
                count == 0 ? SearchEmptyDiagnostic(route, query) : null;
            if (scope is not null)
            {
                scope.SetTarget(query);
                scope.ResultCount = count;
                scope.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                if (diagnostic is not null)
                    ApplyEmptyTelemetry(scope, route, query);
            }
            if (diagnostic is not null)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "search",
                    output,
                    diagnostic,
                    json,
                    scope);
            }
            return output;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                scope?.SetError(ex);
            return ToolDiagnosticRenderer.Render(
                "search",
                diagnostic,
                json,
                scope);
        }
    }

    private static ToolDiagnostic SearchEmptyDiagnostic(SearchRoute route, string query)
    {
        string mode = ModeName(route.Mode);
        if (route.Kind == SearchRouteKind.Markers)
        {
            return ToolDiagnostic.ExpectedEmpty(
                EmptyReasonFor(route),
                "No requested source markers were found.");
        }

        string recoveryMode = route.Kind == SearchRouteKind.Symbols ? "source" : "auto";
        return ToolDiagnostic.ExpectedEmpty(
            EmptyReasonFor(route),
            $"No results matched the {mode} search route.",
            [new ToolDiagnosticAction(
                SearchCall(query, recoveryMode),
                recoveryMode == "source" ? "search source-body text" : "retry automatic routing")]);
    }

    /// <summary>
    /// The assignment <c>utc_date</c> for this call: the telemetry scope's single captured call-start instant, so
    /// the persisted row <c>ts</c> and the assignment date come from one instant and can never straddle midnight.
    /// Falls back to now only when no scope is measuring (a direct call outside the telemetry filter).
    /// </summary>
    private static string CanaryUtcDate(TelemetryScope? scope) =>
        scope?.UtcDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    internal static SearchRetrievalMode ParseRetrieval(string retrieval) =>
        retrieval?.ToLowerInvariant() switch
        {
            null or "" or "auto" => SearchRetrievalMode.Auto,
            "lexical" => SearchRetrievalMode.Lexical,
            "hybrid" => SearchRetrievalMode.Hybrid,
            "semantic" => SearchRetrievalMode.Semantic,
            _ => throw new ArgumentException(
                $"retrieval must be auto, lexical, hybrid, or semantic; got '{retrieval}'.",
                nameof(retrieval)),
        };

    private static string RetrievalName(SearchRetrievalMode retrieval) =>
        retrieval.ToString().ToLowerInvariant();

    private static void EnsureRetrievalSupportsRoute(
        SearchRetrievalMode retrieval,
        SearchRoute route)
    {
        if (retrieval is SearchRetrievalMode.Auto or SearchRetrievalMode.Lexical)
            return;

        if (route.Kind != SearchRouteKind.Symbols)
        {
            throw new ToolDiagnosticException(
                ToolDiagnostic.Unsupported(
                    "retrieval_route_unsupported",
                    $"retrieval={RetrievalName(retrieval)} is supported only for symbol searches."));
        }

        if (route.Mode == SearchToolMode.File || route.Mixed)
        {
            throw new ToolDiagnosticException(
                ToolDiagnostic.Unsupported(
                    "retrieval_route_unsupported",
                    $"retrieval={RetrievalName(retrieval)} cannot rank file results. Use retrieval=lexical."));
        }
    }

    private SearchRouteExecutionResult RunRequiredHybrid(
        WorkspaceSymbolSearchContext context,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ISymbolFusionArm arm;
        Miller.Server.Cli.ForcedHybridFusionArm? requiredArm = null;
        if (_semanticSidecar is not null && _embeddingBroker is not null)
        {
            if (_semanticSidecar.Mode is SemanticMode.Off)
                throw SemanticUnavailable("hybrid", "MILLER_SEMANTIC=off disables all vector work.");

            requiredArm = new Miller.Server.Cli.ForcedHybridFusionArm(
                () => new SemanticSearchArm(
                    context.WorkspaceRoot,
                    _semanticSidecar,
                    _embeddingBroker));
            arm = requiredArm;
        }
        else
        {
            arm = _fusionArm
                ?? throw SemanticUnavailable(
                    "hybrid",
                    "The semantic retrieval services are not configured.");
        }

        var observedArm = new RequiredFusionArm(arm);
        SearchRouteExecutionResult result = SearchRouteExecutor.RunSymbols(
            context.Index,
            route,
            request with { FusionArm = observedArm });
        if (requiredArm is { Queried: false })
            throw SemanticUnavailable("hybrid", "The hybrid arm could not accept this query.");
        if (requiredArm?.UnservedReason is { } reason)
            throw SemanticUnavailable("hybrid", reason);
        if (!observedArm.Queried)
            throw SemanticUnavailable("hybrid", "The hybrid arm could not accept this query.");
        if (!observedArm.Served)
            throw SemanticUnavailable("hybrid", "The hybrid arm did not serve results.");
        return result;
    }

    private sealed class RequiredFusionArm(ISymbolFusionArm inner) : ISymbolFusionArm
    {
        public bool Queried { get; private set; }

        public bool Served { get; private set; }

        public IReadOnlyList<FusedCandidate>? Fuse(
            ISymbolLookupIndex index,
            SymbolFusionRequest request)
        {
            Queried = true;
            IReadOnlyList<FusedCandidate>? result = inner.Fuse(index, request);
            Served = result is { Count: > 0 };
            return result;
        }
    }

    private SearchRouteExecutionResult RunRequiredSemantic(
        WorkspaceSymbolSearchContext context,
        SearchRoute route,
        SearchRouteExecutionRequest request)
    {
        ISemanticTextArm arm;
        if (_semanticSidecar is not null && _embeddingBroker is not null)
        {
            if (_semanticSidecar.Mode is SemanticMode.Off)
                throw SemanticUnavailable("semantic", "MILLER_SEMANTIC=off disables all vector work.");

            arm = new SemanticTextArm(
                SemanticMode.On,
                root => new SemanticSearchArm(root, _semanticSidecar, _embeddingBroker));
        }
        else
        {
            arm = _semanticArm
                ?? throw SemanticUnavailable(
                    "semantic",
                    "The semantic retrieval services are not configured.");
        }

        bool hideTests = ResolveExcludeTests(request.ExcludeTests, request.Query, route.Mode);
        bool hideLowSignalKinds = ResolveHideLowSignalKinds(request.Query, route.Mode);
        ToolSearchFilters filters = ToolSearchFilters.Parse(request.FilePattern, request.Language);
        var visibility = new SymbolVisibilityPolicy(hideTests, hideLowSignalKinds, filters);
        int k = Math.Clamp(request.Limit * 2, 10, SemanticSearchArm.MaxCandidates);
        SemanticQueryResult semantic = arm.QuerySymbols(
            context.WorkspaceRoot,
            request.Query,
            k,
            match => context.Index.FindBySymbolId(match.UnitId) is { } symbol &&
                     visibility.Allows(symbol));
        if (!semantic.Served)
        {
            throw SemanticUnavailable(
                "semantic",
                semantic.UnavailableReason ?? "The semantic arm did not serve this query.");
        }

        var candidates = new List<SymbolCandidate>(semantic.Hits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SemanticHit hit in semantic.Hits)
        {
            if (hit.SymbolId is not { } symbolId ||
                !seen.Add(symbolId) ||
                context.Index.FindBySymbolId(symbolId) is not { } symbol ||
                !visibility.Allows(symbol))
            {
                continue;
            }

            candidates.Add(ToCandidate(symbol, hit.Cosine));
        }

        var candidateSet = new SymbolCandidateSet(
            candidates,
            [],
            [],
            FileMode: false,
            filters,
            visibility);
        string output = RenderSymbolCandidates(
            candidateSet,
            request.Query,
            route.Mode,
            request.Limit,
            request.Json,
            out int count,
            request.CompactBanner,
            request.HasDocLookup);
        return new SearchRouteExecutionResult(output, count);
    }

    private static ToolDiagnosticException SemanticUnavailable(string retrieval, string reason) =>
        new(
            ToolDiagnostic.Unavailable(
                "semantic_unavailable",
                $"retrieval={retrieval} was requested but unavailable: {reason}",
                [new ToolDiagnosticAction(
                    "workspace(operation=\"health\")",
                    "inspect semantic artifact readiness")]));

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
    /// Telemetry value recording which backend served a symbol search — <c>disk</c> when the on-disk
    /// <see cref="FtsSymbolSearchIndex"/> sidecar answered, <c>memory</c> when the in-memory index did. This is
    /// the observable "disk path taken" signal from the sidecar design's risk list: an unexpected memory route
    /// should be easy to distinguish from the default disk route. Every symbol search stamps its backend into the
    /// telemetry row's <c>metadata_json</c> (via <c>SetMetadata("search_backend", …)</c>) so it can be read back
    /// per call and aggregated ad hoc (e.g. <c>json_extract(metadata_json, '$.search_backend')</c>). No dashboard
    /// surface consumes it yet — it is recorded for diagnosis;
    /// <c>SearchToolTests.Search_RecordsServingBackend_InTelemetryMetadata</c> pins it.
    /// </summary>
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
        scope.SetMetadata("mixed", route.Mixed);
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

    private static void ApplyEmptyTelemetry(TelemetryScope scope, SearchRoute route, string query)
    {
        string queryShape = QueryShapeFor(query);
        scope.SetMetadata("query_shape", queryShape);
        scope.SetMetadata("empty_diagnosis", EmptyDiagnosisFor(route, queryShape));
    }

    internal static string QueryShapeFor(string query)
    {
        string trimmed = query.Trim();
        if (trimmed.Length <= 2)
            return "short";
        if (LooksLikePathQueryShape(trimmed))
            return "path_like";
        if (LooksLikeDocsOrConfigQuery(trimmed))
            return "docs_like";
        if (LooksLikeSourceCodeQuery(trimmed))
            return "source_like";
        if (IsNaturalLanguagePhrase(trimmed))
            return "natural_language";
        return "identifier_like";
    }

    internal static string EmptyDiagnosisForContentSearch(string contentKind, string queryShape)
    {
        if (queryShape is "short" or "path_like")
            return "query_shape";
        if (queryShape == "docs_like" && string.Equals(contentKind, TextContentKind.WorkspaceSource, StringComparison.Ordinal))
            return "mode_mismatch";
        if (queryShape == "source_like" &&
            (string.Equals(contentKind, TextContentKind.WorkspaceDocs, StringComparison.Ordinal) ||
             string.Equals(contentKind, TextContentKind.WorkspaceConfig, StringComparison.Ordinal)))
            return "mode_mismatch";
        return "true_no_hit";
    }

    private static string EmptyDiagnosisFor(SearchRoute route, string queryShape)
    {
        if (queryShape is "short" or "path_like")
            return "query_shape";

        return route.Kind switch
        {
            SearchRouteKind.Symbols => EmptyDiagnosisForSymbols(queryShape),
            SearchRouteKind.Content => EmptyDiagnosisForContentSearch(TextContentKind.WorkspaceDocs, queryShape),
            SearchRouteKind.TextContent when route.ContentKinds is not null =>
                EmptyDiagnosisForTextContent(route.ContentKinds, queryShape),
            _ => "true_no_hit",
        };
    }

    /// <summary>
    /// The symbol/file route's diagnosis arm, split out of <see cref="EmptyDiagnosisFor"/> so the compact empty
    /// renderers key off the SAME classification the telemetry ledger records. <c>fileMode</c> lives inside
    /// <see cref="SearchRouteKind.Symbols"/>, so both renderers share this table.
    /// </summary>
    private static string EmptyDiagnosisForSymbols(string queryShape) => queryShape switch
    {
        "short" or "path_like" => "query_shape",
        "docs_like" or "source_like" or "natural_language" => "mode_mismatch",
        _ => "true_no_hit",
    };

    private static string EmptyDiagnosisForTextContent(IReadOnlyCollection<string> contentKinds, string queryShape)
    {
        if (queryShape is "short" or "path_like")
            return "query_shape";

        bool searchesSource = contentKinds.Contains(TextContentKind.WorkspaceSource);
        bool searchesDocsOrConfig =
            contentKinds.Contains(TextContentKind.WorkspaceDocs) ||
            contentKinds.Contains(TextContentKind.WorkspaceConfig);

        if (searchesSource && !searchesDocsOrConfig && queryShape == "docs_like")
            return "mode_mismatch";
        if (searchesDocsOrConfig && !searchesSource && queryShape == "source_like")
            return "mode_mismatch";
        return "true_no_hit";
    }

    private const int EmptyHintQueryLimit = 60;
    private const string MinQueryLengthNote = "3 characters up";

    /// <summary>One suggested recovery call for an empty result, plus why it is worth making.</summary>
    private sealed record SearchNextAction(string Call, string Reason);

    private static string SearchCall(string query, string? mode = null)
    {
        string call = $"search query=\"{EscapeCallString(Truncate(query, EmptyHintQueryLimit))}\"";
        return mode is null ? call : call + " mode=" + mode;
    }

    /// <summary>
    /// Render one compact diagnosis sentence followed by a primary <c>Next:</c> action, and an <c>or:</c>
    /// alternative only where the diagnosis is genuinely ambiguous between two modes.
    /// </summary>
    private static string RenderEmptyHint(string sentence, params SearchNextAction[] actions)
    {
        var sb = new StringBuilder(sentence);
        for (int i = 0; i < actions.Length; i++)
        {
            sb.Append('\n')
              .Append(i == 0 ? "Next: " : "  or: ")
              .Append(actions[i].Call)
              .Append(" — ")
              .Append(actions[i].Reason);
        }

        return sb.ToString();
    }

    // A mode_mismatch means the classifier already knows which mode fits, EXCEPT for a bare phrase: prose can sit
    // in a source comment or in docs text, and nothing in the query distinguishes them — the only reachable
    // two-action case.
    private static string ModeMismatchHint(string lead, string q, string queryShape) => queryShape switch
    {
        "docs_like" => RenderEmptyHint(
            $"{lead} '{q}' reads like docs/config prose; mode=content searches that text.",
            new SearchNextAction(SearchCall(q, "content"), "search docs/config prose")),
        "source_like" => RenderEmptyHint(
            $"{lead} '{q}' reads like source syntax; mode=source searches source bodies.",
            new SearchNextAction(SearchCall(q, "source"), "search source-body text")),
        _ => RenderEmptyHint(
            $"{lead} '{q}' is a phrase; the text modes match phrases, symbol names match identifiers.",
            new SearchNextAction(SearchCall(q, "source"), "search source-body text"),
            new SearchNextAction(SearchCall(q, "content"), "search docs/config prose")),
    };

    private static string SymbolEmptyHint(string query)
    {
        string q = Truncate(query, EmptyHintQueryLimit);
        string queryShape = QueryShapeFor(query);
        return EmptyDiagnosisForSymbols(queryShape) switch
        {
            "query_shape" when queryShape == "path_like" => RenderEmptyHint(
                "No results. Symbol search ranks names; file paths resolve through mode=file.",
                new SearchNextAction(SearchCall(q, "file"), "match the path fragment")),
            "query_shape" => RenderEmptyHint(
                $"No results. Symbol search ranks names from {MinQueryLengthNote}; '{q}' is {query.Trim().Length}.",
                new SearchNextAction(SearchCall(q + "<more>"), "extend to a longer name fragment")),
            "mode_mismatch" => ModeMismatchHint("No results.", q, queryShape),
            _ => RenderEmptyHint(
                $"No results. No indexed symbol name matches '{q}'.",
                new SearchNextAction(SearchCall(q, "source"), "find it as source-body text")),
        };
    }

    private static string FileEmptyHint(string query)
    {
        string q = Truncate(query, EmptyHintQueryLimit);
        string queryShape = QueryShapeFor(query);
        return EmptyDiagnosisForSymbols(queryShape) switch
        {
            "query_shape" when queryShape == "path_like" => FilePathShapeHint(query, q),
            "query_shape" => RenderEmptyHint(
                $"No indexed file matches '{q}'. Path search matches fragments from {MinQueryLengthNote}; '{q}' is {query.Trim().Length}.",
                new SearchNextAction(SearchCall(q + "<more>", "file"), "extend the path fragment")),
            "mode_mismatch" => ModeMismatchHint($"No indexed file matches '{q}'.", q, queryShape),
            _ => RenderEmptyHint(
                $"No indexed file matches '{q}'. Indexed paths match on fragments.",
                new SearchNextAction(SearchCall(q), "search symbol names instead")),
        };
    }

    private static string FilePathShapeHint(string query, string q)
    {
        string basename = Truncate(Path.GetFileName(query.Replace('\\', '/')), EmptyHintQueryLimit);
        return string.IsNullOrWhiteSpace(basename)
            ? RenderEmptyHint(
                $"No indexed file matches '{q}'. Indexed paths match on fragments.",
                new SearchNextAction(SearchCall(q), "search symbol names instead"))
            : RenderEmptyHint(
                $"No indexed file matches '{q}'. Indexed paths match on fragments.",
                new SearchNextAction(SearchCall(basename, "file"), "retry with the basename"));
    }

    // search·source/content/all-text empty (36-47% empty): no text hits. The right "next call" depends on whether
    // the searched kinds are workspace text (refresh re-indexes files) or imported (content list shows what's
    // loaded), so route the hint by kind instead of claiming a one-size refresh.
    private static string TextContentEmptyHint(
        IReadOnlyCollection<string> contentKinds,
        string query,
        IReadOnlyList<IndexedSymbol>? suggestions = null)
    {
        string q = Truncate(query, EmptyHintQueryLimit);
        string queryShape = QueryShapeFor(query);
        return EmptyDiagnosisForTextContent(contentKinds, queryShape) switch
        {
            "query_shape" when queryShape == "path_like" => RenderEmptyHint(
                "No text hits. Text search ranks words in file text; file paths resolve through mode=file.",
                new SearchNextAction(SearchCall(q, "file"), "match the path fragment")),
            "query_shape" => RenderEmptyHint(
                $"No text hits. Text search ranks terms from {MinQueryLengthNote}; '{q}' is {query.Trim().Length}.",
                new SearchNextAction(SearchCall(q + "<more>"), "extend the term")),
            "mode_mismatch" => ModeMismatchHint("No text hits.", q, queryShape),
            _ => TrueNoHitTextHint(contentKinds, query, q, queryShape, suggestions ?? []),
        };
    }

    /// <summary>
    /// Near-match names recovered for an identifier-like text miss, or empty when the lookup does not apply.
    /// Gated so <see cref="SymbolSuggestionEngine"/> is consulted ONLY for an empty, compact, unfiltered
    /// identifier-like miss on the source/content routes — the shape where the symbol index plausibly knows the
    /// name the agent misremembered. Callers invoke it after the JSON and filtered-miss returns, so those paths
    /// never reach the engine.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> TextEmptySuggestions(
        IReadOnlyCollection<string> contentKinds,
        string query,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup)
    {
        if (suggestionLookup is null)
            return [];
        if (QueryShapeFor(query) != "identifier_like")
            return [];
        if (ModeNameForContentKinds(contentKinds) is not ("source" or "content"))
            return [];

        return suggestionLookup(query.Trim());
    }

    // The suggestions come from the SYMBOL index, so the pasteable call has to match where the name actually
    // lives: an exact hit means the name IS indexed and only its TEXT is absent (inspect it), while a near hit
    // means the agent misremembered the name (retry the same text mode with the corrected one).
    private static string NearNameTextHint(
        IReadOnlyCollection<string> contentKinds, string query, string q, IReadOnlyList<IndexedSymbol> suggestions)
    {
        IndexedSymbol top = suggestions[0];
        bool queryIsIndexedName = string.Equals(top.Name, query.Trim(), StringComparison.OrdinalIgnoreCase);
        string sentence = queryIsIndexedName
            ? $"No text hits. '{q}' is an indexed symbol; its text has no match."
            : $"No text hits. These indexed names are close to '{q}'.";
        SearchNextAction action = queryIsIndexedName
            ? new SearchNextAction(
                $"inspect target=\"{EscapeCallString(Truncate(top.Name, EmptyHintQueryLimit))}\" depth=overview",
                "read the indexed symbol")
            : new SearchNextAction(
                SearchCall(top.Name, ModeNameForContentKinds(contentKinds)),
                "retry with the nearest indexed name");

        return AppendSuggestions(RenderEmptyHint(sentence, action), suggestions);
    }

    private static string TrueNoHitTextHint(
        IReadOnlyCollection<string> contentKinds, string query, string q, string queryShape,
        IReadOnlyList<IndexedSymbol> suggestions)
    {
        if (suggestions.Count > 0)
            return NearNameTextHint(contentKinds, query, q, suggestions);

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
            (true, false) => "`workspace refresh` re-indexes changed files",
            (false, true) => "`content list` shows imported sources",
            _ => "`workspace refresh` and `content list` show what is indexed",
        };

        if (queryShape == "natural_language")
        {
            string literal = Truncate(LongestWord(query), EmptyHintQueryLimit);
            return RenderEmptyHint(
                $"No text hits. Indexed text has no literal match for '{q}'; phrases match on literal words ({where}).",
                new SearchNextAction(
                    SearchCall(literal, ModeNameForContentKinds(contentKinds)),
                    "retry with words that appear literally in code or docs"));
        }

        return hasWorkspace
            ? RenderEmptyHint(
                $"No text hits. Indexed text has no literal match for '{q}' ({where}).",
                new SearchNextAction(SearchCall(q), "search symbol names instead"))
            : RenderEmptyHint(
                $"No text hits. Indexed text has no literal match for '{q}' ({where}).",
                new SearchNextAction("content operation=list", "see which sources are imported"));
    }

    /// <summary>
    /// The mode a set of content kinds came from, so a narrowed retry stays on the SAME route. Null when the kinds
    /// do not correspond to a mode the planner emits.
    /// </summary>
    private static string? ModeNameForContentKinds(IReadOnlyCollection<string> contentKinds)
    {
        bool source = contentKinds.Contains(TextContentKind.WorkspaceSource);
        bool docs = contentKinds.Contains(TextContentKind.WorkspaceDocs);
        bool config = contentKinds.Contains(TextContentKind.WorkspaceConfig);
        bool external = contentKinds.Contains(TextContentKind.ExternalFile);
        bool web = contentKinds.Contains(TextContentKind.Web);

        if (source && docs && config && external && web)
            return "all-text";
        if (source && !docs && !config && !external && !web)
            return "source";
        if (!source && (docs || config) && !external && !web)
            return "content";
        if (external && !web && !source && !docs && !config)
            return "external";
        if (web && !external && !source && !docs && !config)
            return "web";
        return null;
    }

    private static string LongestWord(string query)
    {
        string best = string.Empty;
        foreach (string word in query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > best.Length)
                best = word;
        }

        return best;
    }

    private const int OutsideScopeHintLimit = 3;

    /// <summary>Longest accepted query. A pasted blob beyond this is never a real symbol/text search; reject it
    /// BEFORE tokenization/CollapseName so it cannot heap-thrash the tokenizers.</summary>
    internal const int MaxQueryLength = 1000;

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
    /// Collapse identical <c>(SourceId, Line)</c> text-content hits: overlapping content-corpus chunks can both
    /// match the same physical line, so the index returns duplicate rows with identical snippets. Keeps the first
    /// occurrence per key and preserves order, so escalation counts, paging, and <c>sourceBytes</c> all see the
    /// deduped set. Returns the input list unchanged when there is nothing to collapse.
    /// </summary>
    private static List<TextContentSearchHit> DedupByLine(List<TextContentSearchHit> hits)
    {
        if (hits.Count < 2)
            return hits;

        var seen = new HashSet<(string SourceId, int Line)>(hits.Count);
        var deduped = new List<TextContentSearchHit>(hits.Count);
        foreach (TextContentSearchHit hit in hits)
        {
            if (seen.Add((hit.SourceId, hit.Line)))
                deduped.Add(hit);
        }

        return deduped.Count == hits.Count ? hits : deduped;
    }

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry) the tool method delegates to. Returns the rendered
    /// string and sets <paramref name="renderedCount"/> to the number of rows actually shown (the page).
    /// Composes the two stages: <see cref="CollectSymbolCandidates"/> then <see cref="RenderSymbolCandidates"/>.
    /// </summary>
    public static string Run(
        ISymbolLookupIndex index, string query, SearchToolMode mode, int limit,
        bool? excludeTests, bool json, out int renderedCount, string? compactBanner = null,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? hasDocLookup = null,
        string? filePattern = null,
        string? language = null)
    {
        SymbolCandidateSet candidates =
            CollectSymbolCandidates(index, query, mode, limit, excludeTests, filePattern, language);

        return RenderSymbolCandidates(
            candidates, query, mode, limit, json, out renderedCount, compactBanner, hasDocLookup);
    }

    /// <summary>
    /// Stage one of the symbol route: rank, filter, and project index rows into typed candidates. Everything
    /// that needs the index happens here — including the empty-result near-match suggestions — so stage two
    /// renders from data alone. This is the seam a retrieval arm interposes on: it may reorder or extend
    /// <see cref="SymbolCandidateSet.Candidates"/> and rendering follows without knowing the difference.
    /// </summary>
    internal static SymbolCandidateSet CollectSymbolCandidates(
        ISymbolLookupIndex index, string query, SearchToolMode mode, int limit,
        bool? excludeTests,
        string? filePattern = null,
        string? language = null,
        string? mixedFileQuery = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ThrowIfQueryTooLong(query);
        if (limit < 1) limit = 1;

        bool mixed = !string.IsNullOrWhiteSpace(mixedFileQuery);
        bool fileMode = mode == SearchToolMode.File ||
                        (mode == SearchToolMode.Auto && IsPathLikeQuery(query, index));

        bool hideTests = ResolveExcludeTests(excludeTests, query, mode);
        bool hideLowSignalKinds = fileMode || mixed || ResolveHideLowSignalKinds(query, mode);
        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        int overFetch = hideLowSignalKinds || filters.HasAny ? 500 : Math.Min(limit * 4 + 10, 500);

        var kept = new List<IndexedSymbol>();
        var scores = new List<double>();
        var outsideScope = new List<IndexedSymbol>(OutsideScopeHintLimit);

        void Fetch(SearchMode searchMode)
        {
            FetchWithEscalation(overFetch, limit, window =>
            {
                kept.Clear();
                scores.Clear();
                outsideScope.Clear();
                if (fileMode)
                {
                    IReadOnlyList<IndexedSymbol> symbols = FindByFilePathWithPrefixRecovery(index, query, window);
                    foreach (IndexedSymbol symbol in symbols)
                        AddIfVisible(symbol, score: 1.0);
                    return (symbols.Count, kept.Count);
                }

                IReadOnlyList<SearchHit> hits = index.Search(query, window, searchMode);
                foreach (SearchHit hit in hits)
                    AddIfVisible(index.Resolve(hit.Document.DocId), hit.Score);
                return (hits.Count, kept.Count);
            });
        }

        IReadOnlyList<SymbolCandidate> CurrentCandidates(bool rerank)
        {
            SymbolCandidate[] candidates = kept
                .Select((symbol, index) => ToCandidate(symbol, scores[index]))
                .ToArray();
            if (!rerank)
                return candidates;

            SymbolRerankInput input = SymbolReranker.ExpandContainers(
                query,
                candidates,
                parentId =>
                {
                    if (index.FindBySymbolId(parentId) is not { } parent ||
                        hideTests && IsTestPath.IsTest(parent) ||
                        hideLowSignalKinds && IsLowSignalKind(parent.Kind) ||
                        !filters.Allows(parent.FilePath, parent.Language))
                    {
                        return null;
                    }

                    return ToCandidate(parent, score: 0);
                });
            return SymbolReranker.Rank(
                    query,
                    input.Candidates,
                    containerEvidence: input.ContainerEvidence)
                .Select(static result => result.Candidate)
                .ToArray();
        }

        IReadOnlyList<SymbolCandidate> orderedCandidates;
        bool relaxed = false;
        if (fileMode)
        {
            Fetch(SearchMode.Or);
            orderedCandidates = CurrentCandidates(rerank: false);
        }
        else
        {
            int distinctTerms = SearchRelaxation.DistinctTermCount(query);
            SearchMode primaryMode = distinctTerms > 1 ? SearchMode.And : SearchMode.Or;
            Fetch(primaryMode);
            IReadOnlyList<SymbolCandidate> strict = CurrentCandidates(rerank: true);
            SearchRelaxationDecision decision = SearchRelaxation.Decide(
                distinctTerms,
                strict.Count,
                limit);
            relaxed = decision.Relaxed;
            if (decision.FallbackMode is { } fallbackMode)
            {
                Fetch(fallbackMode);
                IReadOnlyList<SymbolCandidate> fallback = CurrentCandidates(rerank: true);
                orderedCandidates = SearchRelaxation.Merge(
                    strict,
                    fallback,
                    strict.Count + fallback.Count);
            }
            else
            {
                orderedCandidates = strict;
            }
        }

        if (mixed)
        {
            var fileCandidates = new List<SymbolCandidate>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<IndexedSymbol> fileSymbols = FindByFilePathWithPrefixRecovery(
                index,
                mixedFileQuery!,
                Math.Max(limit * 4, 20));
            foreach (IndexedSymbol symbol in fileSymbols)
            {
                if (hideTests && IsTestPath.IsTest(symbol))
                    continue;
                if (hideLowSignalKinds && IsLowSignalKind(symbol.Kind))
                    continue;
                if (!filters.Allows(symbol.FilePath, symbol.Language))
                {
                    if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                        outsideScope.Add(symbol);
                    continue;
                }
                if (seenFiles.Add(symbol.FilePath))
                {
                    fileCandidates.Add(
                        ToCandidate(symbol, score: 1.0) with
                        {
                            Origin = SymbolCandidateOrigin.File,
                        });
                }
            }

            var mixedCandidates = new List<SymbolCandidate>(
                orderedCandidates.Count + fileCandidates.Count);
            if (orderedCandidates.Count > 0)
                mixedCandidates.Add(orderedCandidates[0]);
            if (fileCandidates.Count > 0)
                mixedCandidates.Add(fileCandidates[0]);
            mixedCandidates.AddRange(orderedCandidates.Skip(1));
            mixedCandidates.AddRange(fileCandidates.Skip(1));
            orderedCandidates = mixedCandidates;
        }

        IReadOnlyList<IndexedSymbol> emptySuggestions =
            orderedCandidates.Count == 0 && !fileMode && outsideScope.Count == 0
                ? SymbolSuggestionEngine.Suggest(index, query, EmptySuggestionLimit)
                : [];

        return new SymbolCandidateSet(
            orderedCandidates,
            [.. outsideScope.Select(static symbol => ToCandidate(symbol, score: 0))],
            emptySuggestions,
            fileMode,
            filters,
            new SymbolVisibilityPolicy(hideTests, hideLowSignalKinds, filters),
            relaxed,
            mixed);

        void AddIfVisible(IndexedSymbol symbol, double score)
        {
            if (hideTests && IsTestPath.IsTest(symbol))
                return;
            if (hideLowSignalKinds && IsLowSignalKind(symbol.Kind))
                return;
            if (!filters.Allows(symbol.FilePath, symbol.Language))
            {
                if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(symbol);
                return;
            }
            kept.Add(symbol);
            scores.Add(score);
        }
    }

    internal static SymbolCandidate ToCandidate(IndexedSymbol symbol, double score) =>
        new(
            symbol.DocId,
            symbol.SymbolId,
            symbol.Name,
            symbol.Signature,
            symbol.Kind,
            symbol.FilePath,
            symbol.StartLine,
            score,
            symbol.Language,
            ParentId: symbol.ParentId);

    /// <summary>
    /// Stage two of the symbol route: render collected candidates. Reads no index and performs no lookup —
    /// every rendered byte comes from <paramref name="candidates"/> plus the caller's presentation options —
    /// so an arm that reshapes the candidate list fully determines the output.
    /// </summary>
    internal static string RenderSymbolCandidates(
        SymbolCandidateSet candidates, string query, SearchToolMode mode, int limit, bool json,
        out int renderedCount, string? compactBanner = null,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? hasDocLookup = null,
        IReadOnlyDictionary<string, FusedCandidate>? fusion = null,
        bool boundAgentOutput = false) =>
        RenderSymbolCandidates(
            candidates, query, mode, limit, json, out renderedCount, out _, compactBanner, hasDocLookup, fusion,
            boundAgentOutput);

    /// <summary>
    /// Renders collected candidates and also exposes the served page slice — the exact rows the caller rendered,
    /// in served order — so the canary writer can hash precisely what an agent saw without re-deriving the page.
    /// </summary>
    internal static string RenderSymbolCandidates(
        SymbolCandidateSet candidates, string query, SearchToolMode mode, int limit, bool json,
        out int renderedCount, out IReadOnlyList<SymbolCandidate> servedPage, string? compactBanner = null,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>>? hasDocLookup = null,
        IReadOnlyDictionary<string, FusedCandidate>? fusion = null,
        bool boundAgentOutput = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (limit < 1) limit = 1;

        IReadOnlyList<SymbolCandidate> kept = candidates.Candidates;
        int total = kept.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;
        servedPage = page > 0 ? [.. kept.Take(page)] : [];

        if (total == 0)
        {
            if (candidates.FileMode)
            {
                if (json)
                    return "[]";
                return candidates.OutsideScope.Count > 0
                    ? RenderFilteredMissCompact(candidates.Filters, compactBanner, candidates.OutsideScope)
                    : ReadToolWorkspaceRouting.PrefixCompact(FileEmptyHint(query), compactBanner);
            }
            IReadOnlyList<IndexedSymbol> suggestions = candidates.EmptySuggestions;
            if (json)
                return suggestions.Count > 0 || candidates.Relaxed
                    ? RenderEmptyJson(suggestions, candidates.Relaxed)
                    : "[]";
            return candidates.OutsideScope.Count > 0
                ? RenderFilteredMissCompact(candidates.Filters, compactBanner, candidates.OutsideScope)
                : RenderEmptySymbolMissCompact(compactBanner, query, suggestions);
        }

        IReadOnlySet<string>? hasDocSymbolIds = null;
        if (hasDocLookup is not null && page > 0)
        {
            string[] pageIds = kept.Take(page).Select(static s => s.SymbolId).ToArray();
            hasDocSymbolIds = hasDocLookup(pageIds);
        }

        bool exactMiss =
            !candidates.FileMode &&
            !candidates.Mixed &&
            mode is (SearchToolMode.Auto or SearchToolMode.Symbol) &&
            LooksLikeExactIdentifierQuery(query) &&
            FindPromotableDefinitionIndex(kept, kept.Count, query) < 0;

        if (json)
            return RenderJson(
                kept,
                page,
                hasDocSymbolIds,
                fusion,
                candidates.Relaxed,
                candidates.Mixed,
                exactMiss,
                boundAgentOutput);
        if (candidates.FileMode)
            return RenderFileCompact(kept, page, total, compactBanner, hasDocSymbolIds);

        string compact = candidates.Mixed
            ? RenderMixedCompact(kept, page, compactBanner)
            : RenderCompact(kept, page, total, query, compactBanner, hasDocSymbolIds);
        if (exactMiss)
            compact = PrefixExactMiss(compact, compactBanner, query);
        if (candidates.Relaxed)
            compact += "\nnote: relaxed=or — strict AND results first, followed by OR fallback.";
        // Delivery-time nudge: a named symbol top hit is a natural inspect target, so route the agent there.
        // Rendered last, exactly once. Suppressed for JSON (returned above, byte-identical), file-path hits
        // (returned above), and empty results (returned earlier). Text/content/source/markers modes never reach
        // this symbol path — and text mode, which does, is gated out here so only symbol/auto intent nudges.
        if (!exactMiss && mode is (SearchToolMode.Auto or SearchToolMode.Symbol))
            compact += "\n" + NextStepHint.Render(
                $"inspect target=\"{EscapeCallString(kept[0].Name)}\" depth=overview");
        return compact;
    }

    private static string PrefixExactMiss(
        string compact,
        string? compactBanner,
        string query)
    {
        string header = $"No exact symbol named '{query.Trim()}'. Near matches:";
        if (string.IsNullOrWhiteSpace(compactBanner))
            return header + "\n" + compact;

        string banner = compactBanner + "\n";
        return compact.StartsWith(banner, StringComparison.Ordinal)
            ? banner + header + "\n" + compact[banner.Length..]
            : header + "\n" + compact;
    }

    /// <summary>
    /// The result of a symbol-route call that ran under an active or inactive canary: the rendered output and,
    /// when the canary is <c>on</c>, the assembled row facts the caller finalizes with the rescue kind and stamps.
    /// <see cref="Facts"/> is null when the canary is off — the pre-program path, no stamping, byte-identical bytes.
    /// </summary>
    internal sealed record SymbolCanaryOutcome(
        SearchRouteExecutionResult Result,
        CanaryCallFacts? Facts,
        CanaryShadowFacts? ShadowFacts = null,
        SearchServingPolicy ServingPolicy = SearchServingPolicy.Lexical);

    /// <summary>
    /// One forced-hybrid shadow pass: whether the semantic arm actually served, the fallback that stopped it
    /// otherwise, the ordered hybrid ranking by symbol id (empty when it did not serve), and the generation
    /// identity when vectors were opened. The forced fusion is why this is measured through a dedicated arm and
    /// not the production one, which correctly abstains for an identifier query's lexical-only route.
    /// </summary>
    internal sealed record ShadowExecution(
        bool Served,
        SemanticFallbackKind? Fallback,
        IReadOnlyList<string> HybridSymbolIds,
        SemanticGenerationIdentity? Identity,
        int SemanticResultCount = 0);

    /// <summary>
    /// The symbol route under the canary (<c>canary-telemetry-v1</c>): classify the query, walk the eligibility
    /// ladder, assign an arm, and serve the treatment fusion arm only when semantic mode permits serving. Control,
    /// shadow, and every ineligible call serve lexical bytes. Off is the pre-program path with no facts or added
    /// canary work.
    /// </summary>
    internal static SymbolCanaryOutcome RunSymbolsWithCanary(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        CanaryMode mode,
        string op,
        bool semanticDisabled,
        string workspaceId,
        string utcDate,
        Func<string> vectorStateProbe,
        bool foreignWorkspace,
        Func<SemanticSymbolFusionArm>? treatmentArmFactory,
        Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, ShadowExecution>? shadowRunner = null,
        SemanticMode semanticMode = SemanticMode.On) =>
        RunSymbolsWithCanaryProbe(
            index,
            route,
            request,
            mode,
            op,
            semanticDisabled,
            workspaceId,
            utcDate,
            () => new CanaryVectorProbe(vectorStateProbe(), Identity: null),
            foreignWorkspace,
            treatmentArmFactory,
            shadowRunner,
            semanticMode);

    internal static SymbolCanaryOutcome RunSymbolsWithCanaryProbe(
        ISymbolLookupIndex index,
        SearchRoute route,
        SearchRouteExecutionRequest request,
        CanaryMode mode,
        string op,
        bool semanticDisabled,
        string workspaceId,
        string utcDate,
        Func<CanaryVectorProbe> vectorStateProbe,
        bool foreignWorkspace,
        Func<SemanticSymbolFusionArm>? treatmentArmFactory,
        Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, ShadowExecution>? shadowRunner = null,
        SemanticMode semanticMode = SemanticMode.On)
    {
        ArgumentNullException.ThrowIfNull(vectorStateProbe);

        // MILLER_SEMANTIC=off is a permanent zero-side-effect guarantee (design §3, contract §Activation) that
        // outranks the canary flag: no classification, no vector probe, no shadow, no stamping, byte-identical
        // lexical bytes — exactly the canary-off path. The IneligibleSemanticDisabled rung stays in the frozen
        // vocabulary but v1 never reaches it.
        if (mode == CanaryMode.Off || semanticDisabled || string.IsNullOrEmpty(workspaceId))
        {
            SearchServingPolicy offServingPolicy = NonCanaryServingPolicy(semanticMode, semanticDisabled);
            return new SymbolCanaryOutcome(
                SearchRouteExecutor.RunSymbolsCore(
                    index,
                    route,
                    request,
                    offServingPolicy == SearchServingPolicy.Production ? request.FusionArm : null).Result,
                Facts: null,
                ServingPolicy: offServingPolicy);
        }

        CanaryContractProfile contractProfile = CanaryContractProfile.For(mode);

        SemanticQueryRoute policyRoute = SemanticQueryPolicy.Route(request.Query, LexicalEvidence.None);
        string queryClass = CanaryQueryClassifier.Classify(op, request.Query, policyRoute);
        CanaryVectorProbe vectorProbe = CanaryEligibility.RequiresVectorProbe(op, semanticDisabled, queryClass)
            ? vectorStateProbe()
            : new CanaryVectorProbe("none", Identity: null);
        string vectorState = vectorProbe.State;
        bool crossWorkspaceNoGeneration = foreignWorkspace && vectorState != "ready";
        string eligibility =
            CanaryEligibility.Resolve(op, semanticDisabled, queryClass, vectorState, crossWorkspaceNoGeneration);

        bool eligible = eligibility == CanaryEligibility.Eligible;
        bool treatment = eligible && CanaryAssignment.ResolveArm(
            CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, workspaceId, utcDate, queryClass))
            == CanaryArm.Treatment;
        SearchServingPolicy servingPolicy = CanaryServingPolicy(semanticMode, eligible, treatment);

        SemanticSymbolFusionArm? treatmentArm = servingPolicy == SearchServingPolicy.Treatment
            ? treatmentArmFactory?.Invoke()
            : null;
        SymbolExecution execution = SearchRouteExecutor.RunSymbolsCore(index, route, request, treatmentArm);

        if (ShadowSampled(contractProfile, eligibility, queryClass, workspaceId, utcDate))
        {
            CanaryShadowFacts shadowFacts = RunIdentifierShadow(
                workspaceId,
                utcDate,
                queryClass,
                eligibility,
                [.. execution.ServedPage.Select(static candidate => candidate.SymbolId)],
                shadowRunner is null ? null : () => shadowRunner(index, route, request));
            return new SymbolCanaryOutcome(
                execution.Result, Facts: null, ShadowFacts: shadowFacts, ServingPolicy: servingPolicy);
        }

        CanaryCallFacts facts = WithCohortIdentity(
            BuildCanaryFacts(
                workspaceId, utcDate, queryClass, eligibility, execution, treatmentArm?.LastDiagnostics, index),
            vectorProbe.Identity);
        return new SymbolCanaryOutcome(execution.Result, facts, ServingPolicy: servingPolicy);
    }

    private static SearchServingPolicy NonCanaryServingPolicy(SemanticMode semanticMode, bool semanticDisabled) =>
        semanticDisabled
            ? SearchServingPolicy.Lexical
            : semanticMode switch
            {
                SemanticMode.On => SearchServingPolicy.Production,
                SemanticMode.Shadow => SearchServingPolicy.Shadow,
                _ => SearchServingPolicy.Lexical,
            };

    private static SearchServingPolicy CanaryServingPolicy(
        SemanticMode semanticMode,
        bool eligible,
        bool treatment) =>
        semanticMode switch
        {
            SemanticMode.On when eligible && treatment => SearchServingPolicy.Treatment,
            SemanticMode.Shadow => SearchServingPolicy.Shadow,
            _ => SearchServingPolicy.Lexical,
        };

    private static bool AllowsSemanticServing(SearchServingPolicy policy) =>
        policy is SearchServingPolicy.Production or SearchServingPolicy.Treatment;

    /// <summary>
    /// A call is a shadow sample when it is an identifier query the canary can never serve, and its bucket under
    /// the non-inferiority experiment id falls below the active contract profile's sampling percentage. The
    /// eligibility rung already encodes "canary active and semantic not disabled": under
    /// <c>MILLER_SEMANTIC=off</c> the class rung is never reached, so <c>off</c> does zero shadow work.
    /// </summary>
    private static bool ShadowSampled(
        CanaryContractProfile contractProfile,
        string eligibility,
        string queryClass,
        string workspaceId,
        string utcDate) =>
        eligibility == CanaryEligibility.IneligibleQueryClass &&
        queryClass == CanaryQueryClass.Identifier &&
        CanaryAssignment.Bucket(CanaryAssignment.IdentifierExperimentId, workspaceId, utcDate, queryClass) <
        contractProfile.IdentifierShadowPercent;

    /// <summary>
    /// The identifier non-inferiority measurement (<c>canary-telemetry-v1</c> §Shadow Population steps 3–5), run
    /// after the lexical result is finalized. Any failure — a missing arm, an abstaining arm, or a throwing one —
    /// records a status and nothing else and can never touch the served result or the row's outcome.
    /// </summary>
    private static CanaryShadowFacts RunIdentifierShadow(
        string workspaceId,
        string utcDate,
        string queryClass,
        string eligibility,
        IReadOnlyList<string> servedLexicalIds,
        Func<ShadowExecution>? execute)
    {
        var facts = new CanaryShadowFacts
        {
            WorkspaceId = workspaceId,
            UtcDate = utcDate,
            QueryClass = queryClass,
            Eligibility = eligibility,
            Status = CanaryShadowStatus.Skipped,
        };

        if (execute is null)
            return facts;

        ShadowExecution execution;
        try
        {
            execution = execute();
        }
        catch (Exception)
        {
            return facts with { Status = CanaryShadowStatus.Error };
        }

        if (execution.Identity is { } identity)
        {
            facts = facts with
            {
                EncoderFingerprint = identity.EncoderFingerprint,
                StorageSchema = identity.StorageSchema,
                CorpusGeneration = identity.CorpusGeneration,
                FusionProfile = identity.FusionProfile,
            };
        }

        if (!execution.Served)
            return facts with { Status = MapShadowStatus(execution.Fallback) };

        (int overlap, bool top1Changed, int rank) = CompareShadow(servedLexicalIds, execution.HybridSymbolIds);
        return facts with
        {
            Status = CanaryShadowStatus.Ok,
            SemanticResultCount = execution.SemanticResultCount,
            OverlapAt10 = overlap,
            Top1Changed = top1Changed,
            LexicalTop1Rank = rank,
        };
    }

    /// <summary>
    /// The frozen shadow comparison (<c>canary-telemetry-v1</c> §Shadow Population step 4): top-10 overlap by
    /// symbol identity, whether the hybrid arm would have changed rank 1, and the 1-based rank of the served
    /// lexical top-1 within the hybrid top 50 (0 when absent from it). Neither ranking is persisted.
    /// </summary>
    private static (int OverlapAt10, bool Top1Changed, int LexicalTop1Rank) CompareShadow(
        IReadOnlyList<string> servedLexicalIds, IReadOnlyList<string> hybridIds)
    {
        string? servedTop1 = servedLexicalIds.Count > 0 ? servedLexicalIds[0] : null;
        string? hybridTop1 = hybridIds.Count > 0 ? hybridIds[0] : null;
        bool top1Changed = !string.Equals(servedTop1, hybridTop1, StringComparison.Ordinal);

        var hybridTop10 = new HashSet<string>(hybridIds.Take(10), StringComparer.Ordinal);
        int overlap = servedLexicalIds.Take(10).Distinct(StringComparer.Ordinal).Count(hybridTop10.Contains);

        int rank = 0;
        if (servedTop1 is not null)
        {
            for (int i = 0; i < hybridIds.Count && i < 50; i++)
            {
                if (string.Equals(hybridIds[i], servedTop1, StringComparison.Ordinal))
                {
                    rank = i + 1;
                    break;
                }
            }
        }

        return (overlap, top1Changed, rank);
    }

    /// <summary>
    /// Maps an abstaining arm's fallback to a shadow status (<c>canary-telemetry-v1</c> §shadow_status):
    /// an embed deadline is <c>timeout</c>; an unavailable/unusable artifact, an open circuit, or an unprepared
    /// model are prerequisites the sample cannot run against, so <c>skipped</c>; an embed or KNN execution failure
    /// is <c>error</c>. <see cref="SemanticFallbackKind.None"/> never reaches here — a served arm is <c>ok</c>.
    /// </summary>
    private static string MapShadowStatus(SemanticFallbackKind? fallback) => fallback switch
    {
        SemanticFallbackKind.EmbedTimeout => CanaryShadowStatus.Timeout,
        SemanticFallbackKind.VectorsMissing => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.VectorsStale => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.VectorsIncompatible => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.VectorsBuilding => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.ModelNotPrepared => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.CircuitOpen => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.DiskBlocked => CanaryShadowStatus.Skipped,
        SemanticFallbackKind.Disabled => CanaryShadowStatus.Skipped,
        _ => CanaryShadowStatus.Error,
    };

    private static CanaryCallFacts BuildCanaryFacts(
        string workspaceId,
        string utcDate,
        string queryClass,
        string eligibility,
        SymbolExecution execution,
        SemanticQueryDiagnostics? diagnostics,
        ISymbolLookupIndex index)
    {
        var facts = new CanaryCallFacts
        {
            WorkspaceId = workspaceId,
            UtcDate = utcDate,
            QueryClass = queryClass,
            Eligibility = eligibility,
        };

        if (eligibility != CanaryEligibility.Eligible)
            return facts;

        facts = facts with
        {
            ResultCount = execution.Result.Count,
            LexicalResultCount = execution.LexicalResultCount,
            ServedResults = BuildServedResults(execution.ServedPage, index),
            FallbackReason = diagnostics is { } d ? MapFallbackReason(d.Fallback) : CanaryFallbackReason.None,
            Backend = diagnostics is { } b ? NormalizeBackend(b.Backend) : CanaryBackend.None,
            EmbedWarmth = ResolveWarmth(diagnostics),
            EmbedLatencyMs = diagnostics?.EmbedMs,
            KnnLatencyMs = diagnostics?.KnnMs,
            EncoderFingerprint = diagnostics?.Identity?.EncoderFingerprint,
            StorageSchema = diagnostics?.Identity?.StorageSchema,
            CorpusGeneration = diagnostics?.Identity?.CorpusGeneration,
            FusionProfile = diagnostics?.FusionProfile,
        };

        if (execution.Fusion is { } fusion)
        {
            facts = facts with
            {
                SemanticResultCount = fusion.Values.Count(static row => row.SemanticRank is not null),
                FusedResultCount = fusion.Count,
                SemanticContributionCount = execution.ServedPage.Count(
                    candidate => fusion.TryGetValue(candidate.SymbolId, out FusedCandidate? row) && SemanticIsTop(row)),
            };
        }

        return facts;
    }

    private static IReadOnlyList<CanaryServedResult> BuildServedResults(
        IReadOnlyList<SymbolCandidate> servedPage, ISymbolLookupIndex index)
    {
        var results = new List<CanaryServedResult>(servedPage.Count);
        foreach (SymbolCandidate candidate in servedPage)
        {
            string? qualified = null;
            if (index.FindBySymbolId(candidate.SymbolId) is { ParentId: { } parentId } &&
                index.FindBySymbolId(parentId) is { Name.Length: > 0 } parent)
            {
                qualified = $"{parent.Name}.{candidate.Name}";
            }

            results.Add(new CanaryServedResult(candidate.Name, candidate.FilePath, qualified));
        }

        return results;
    }

    private static bool SemanticIsTop(FusedCandidate row) =>
        row.SemanticRank is { } semantic && (row.LexicalRank is not { } lexical || semantic < lexical);

    private static CanaryCallFacts WithCohortIdentity(
        CanaryCallFacts facts,
        SemanticGenerationIdentity? configuredIdentity) =>
        facts.Eligibility == CanaryEligibility.Eligible && configuredIdentity is { } identity
            ? facts with
            {
                EncoderFingerprint = identity.EncoderFingerprint,
                StorageSchema = identity.StorageSchema,
                CorpusGeneration = identity.CorpusGeneration,
                FusionProfile = identity.FusionProfile,
            }
            : facts;

    private static string ResolveWarmth(SemanticQueryDiagnostics? diagnostics) => diagnostics switch
    {
        { EmbedMs: not null } d => d.ColdEmbed ? "cold" : "warm",
        _ => CanaryEmbedWarmth.None,
    };

    private static string NormalizeBackend(string backend) =>
        CanaryBackend.All.Contains(backend) ? backend : CanaryBackend.None;

    /// <summary>The <see cref="SemanticFallbackKind"/> mirror of the thirteen frozen <c>fallback_reason</c> strings.</summary>
    private static string MapFallbackReason(SemanticFallbackKind fallback) => fallback switch
    {
        SemanticFallbackKind.None => "none",
        SemanticFallbackKind.VectorsMissing => "vectors_missing",
        SemanticFallbackKind.VectorsStale => "vectors_stale",
        SemanticFallbackKind.VectorsIncompatible => "vectors_incompatible",
        SemanticFallbackKind.VectorsBuilding => "vectors_building",
        SemanticFallbackKind.ModelNotPrepared => "model_not_prepared",
        SemanticFallbackKind.CircuitOpen => "circuit_open",
        SemanticFallbackKind.EmbedTimeout => "embed_timeout",
        SemanticFallbackKind.EmbedError => "embed_error",
        SemanticFallbackKind.KnnError => "knn_error",
        SemanticFallbackKind.DiskBlocked => "disk_blocked",
        SemanticFallbackKind.Disabled => "disabled",
        _ => "unknown",
    };

    /// <summary>
    /// Copies the existing auto-rescue outcome into the frozen <c>canary_rescue_kind</c> vocabulary: no rescue
    /// object maps to <c>none</c>; the lexical docs/config rung folds into <c>source</c> (the nearest frozen
    /// lexical text rescue, since the enum has no lexical-docs value); every other kind is already a frozen value.
    /// </summary>
    private static string MapCanaryRescueKind(AutoTextRescueResult? rescue) => rescue?.Kind switch
    {
        null or "none" => "none",
        "source" or "docs_config" => "source",
        "semantic_symbol" => "semantic_symbol",
        "semantic_docs" => "semantic_docs",
        "semantic_mixed" => "semantic_mixed",
        "unavailable" => "unavailable",
        _ => "unavailable",
    };

    /// <summary>
    /// The treatment fusion arm. The canary serving policy constructs it only for an eligible treatment request
    /// while semantic serving is on. Null when the semantic graph is absent.
    /// </summary>
    private Func<SemanticSymbolFusionArm>? BuildTreatmentArmFactory(string workspaceRoot)
    {
        if (_semanticSidecar is not { } sidecar || _embeddingBroker is not { } broker)
            return null;

        return () => new SemanticSymbolFusionArm(
            SemanticMode.On,
            root => new SemanticSearchArm(string.IsNullOrEmpty(root) ? workspaceRoot : root, sidecar, broker));
    }

    /// <summary>
    /// The identifier shadow runner: it runs a forced-hybrid pass over the same lexical candidate pool the served
    /// lexical result was rendered from, so a discarded hybrid ranking can be compared against the served one.
    /// Null when the semantic graph is absent, which makes a sampled shadow record <c>status=skipped</c>.
    /// </summary>
    private Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, ShadowExecution>? BuildShadowRunner(
        string workspaceRoot)
    {
        if (_semanticSidecar is not { } sidecar || _embeddingBroker is not { } broker)
            return null;

        return ShadowRunnerFor(
            root => new SemanticSearchArm(
                string.IsNullOrEmpty(root) ? workspaceRoot : root, sidecar, broker));
    }

    /// <summary>
    /// Wraps a semantic-arm opener into a shadow runner: it forces the hybrid pass through
    /// <see cref="ShadowSymbolArm"/>, discards the rendered output, and reports the ordered hybrid ranking plus
    /// the arm's served/fallback/identity facts. The rendered output is thrown away, so the shadow pass can never
    /// change what a call served.
    /// </summary>
    internal static Func<ISymbolLookupIndex, SearchRoute, SearchRouteExecutionRequest, ShadowExecution> ShadowRunnerFor(
        Func<string, SemanticSearchArm> openArm)
    {
        ArgumentNullException.ThrowIfNull(openArm);

        return (index, route, request) =>
        {
            var arm = new ShadowSymbolArm(openArm);
            SearchRouteExecutor.RunSymbolsCore(index, route, request, arm);
            IReadOnlyList<string> hybridIds = arm.LastFused is { } fused
                ? [.. fused.Select(static row => row.Candidate.SymbolId)]
                : [];
            return new ShadowExecution(
                arm.Served, arm.LastDiagnostics?.Fallback, hybridIds, arm.LastDiagnostics?.Identity,
                arm.LastSemanticHitCount);
        };
    }

    /// <summary>
    /// The shadow arm: it consults the semantic arm and fuses <b>regardless of the query's lexical-only route</b>,
    /// which is exactly what the production <see cref="SemanticSymbolFusionArm"/> refuses to do for an identifier
    /// query. It reuses the same recall clamp, allow predicate, and <see cref="RrfFusion"/> the served path uses,
    /// and captures the ordered fused ranking for the shadow comparison — it never serves, so a served result can
    /// never see it.
    /// </summary>
    private sealed class ShadowSymbolArm(Func<string, SemanticSearchArm> openArm) : ISymbolFusionArm
    {
        private const int MinimumRecall = 10;

        public bool Served { get; private set; }

        public SemanticQueryDiagnostics? LastDiagnostics { get; private set; }

        public IReadOnlyList<FusedCandidate>? LastFused { get; private set; }

        /// <summary>Count of hits the semantic arm returned pre-fusion — the shadow row's semantic result count.</summary>
        public int LastSemanticHitCount { get; private set; }

        public IReadOnlyList<FusedCandidate>? Fuse(ISymbolLookupIndex index, SymbolFusionRequest request)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(request);

            SemanticQueryRoute route = SemanticQueryPolicy.Route(request.Query, EvidenceFrom(request.Candidates));
            int k = Math.Clamp(request.Limit * 2, MinimumRecall, SemanticSearchArm.MaxCandidates);
            SemanticQueryResult result = openArm(request.WorkspaceRoot)
                .QuerySymbolsAsync(request.Query, k, match => Admits(index, request, match))
                .GetAwaiter()
                .GetResult();

            LastDiagnostics = result.Diagnostics;
            Served = result.Served;
            LastSemanticHitCount = result.Hits.Count;
            if (!result.Served)
                return null;

            var semantic = new List<SemanticRankedCandidate>(result.Hits.Count);
            foreach (SemanticHit hit in result.Hits)
            {
                if (hit.SymbolId is { } symbolId && index.FindBySymbolId(symbolId) is { } symbol)
                    semantic.Add(new SemanticRankedCandidate(ToCandidate(symbol, score: 0), hit.Rank));
            }

            IReadOnlyList<FusedCandidate> fused =
                RrfFusion.Fuse(request.Candidates, semantic, RrfFusion.WeightsFor(route.HybridClass));
            LastFused = fused;
            return fused.Count > 0 ? fused : null;
        }

        private static bool Admits(ISymbolLookupIndex index, SymbolFusionRequest request, VectorMatch match) =>
            index.FindBySymbolId(match.UnitId) is { } symbol && request.Allows(symbol);

        private static LexicalEvidence EvidenceFrom(IReadOnlyList<SymbolCandidate> candidates) => candidates.Count switch
        {
            0 => LexicalEvidence.None,
            1 => new LexicalEvidence(1, candidates[0].Score, 0),
            _ => new LexicalEvidence(candidates.Count, candidates[0].Score, candidates[1].Score),
        };
    }

    /// <summary>
    /// The treatment content arm. Shadow mode never constructs this serving arm; an eligible treatment request
    /// may construct it only while semantic serving is on. Null when the semantic graph is absent.
    /// </summary>
    private ISemanticTextArm? BuildTreatmentContentArm(string workspaceRoot)
    {
        if (_semanticSidecar is not { Mode: SemanticMode.On } sidecar ||
            _embeddingBroker is not { } broker)
            return null;

        return new SemanticTextArm(
            SemanticMode.On,
            root => new SemanticSearchArm(string.IsNullOrEmpty(root) ? workspaceRoot : root, sidecar, broker));
    }

    /// <summary>
    /// The result of a content-route call under an active or inactive canary: the rendered output plus, when the
    /// canary is <c>on</c>, the row facts and the served path digests the caller stamps. Content rows carry PATH
    /// hashes only — a content result is a path+line chunk with no symbol name, so the name and qualified arrays
    /// are absent per the absent-vs-zero rule, which is why the path digests ride here instead of on
    /// <see cref="CanaryCallFacts.ServedResults"/> (populating that field would emit a name array).
    /// </summary>
    internal sealed record ContentCanaryOutcome(
        SearchRouteExecutionResult Result,
        CanaryCallFacts? Facts,
        IReadOnlyList<string> ResultPathHashes,
        bool ResultHashTruncated,
        SearchServingPolicy ServingPolicy = SearchServingPolicy.Lexical);

    /// <summary>≤10 served results per canary-telemetry-v1 §Served-result hashes; the path array shares that cap.</summary>
    private const int ContentResultHashCap = 10;

    /// <summary>
    /// The content route under the canary (<c>canary-telemetry-v1</c>, <c>op=content</c>): classify the query,
    /// walk the eligibility ladder, and serve the treatment hybrid arm only when semantic mode permits serving.
    /// Control, shadow, and every ineligible call serve lexical content. Off is the pre-program path with no
    /// canary facts. Content served-result hashes are path-only.
    /// </summary>
    internal static ContentCanaryOutcome RunContentWithCanary(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool json,
        string? compactBanner,
        string? filePattern,
        string? language,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup,
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? productionRerank,
        CanaryMode mode,
        string op,
        bool semanticDisabled,
        string workspaceId,
        string workspaceRoot,
        string utcDate,
        Func<string> vectorStateProbe,
        bool foreignWorkspace,
        Func<ISemanticTextArm?>? treatmentArmFactory,
        bool excludeTests = false,
        SemanticMode semanticMode = SemanticMode.On) =>
        RunContentWithCanaryProbe(
            index,
            query,
            limit,
            json,
            compactBanner,
            filePattern,
            language,
            suggestionLookup,
            productionRerank,
            mode,
            op,
            semanticDisabled,
            workspaceId,
            workspaceRoot,
            utcDate,
            () => new CanaryVectorProbe(vectorStateProbe(), Identity: null),
            foreignWorkspace,
            treatmentArmFactory,
            excludeTests,
            semanticMode);

    internal static ContentCanaryOutcome RunContentWithCanaryProbe(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool json,
        string? compactBanner,
        string? filePattern,
        string? language,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup,
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? productionRerank,
        CanaryMode mode,
        string op,
        bool semanticDisabled,
        string workspaceId,
        string workspaceRoot,
        string utcDate,
        Func<CanaryVectorProbe> vectorStateProbe,
        bool foreignWorkspace,
        Func<ISemanticTextArm?>? treatmentArmFactory,
        bool excludeTests = false,
        SemanticMode semanticMode = SemanticMode.On)
    {
        ArgumentNullException.ThrowIfNull(vectorStateProbe);

        // MILLER_SEMANTIC=off is inert exactly like canary-off (contract §Activation): the production rerank runs
        // untouched, no probe, no facts, no stamping — the semantic-off lexical bytes verbatim.
        if (mode == CanaryMode.Off || semanticDisabled || string.IsNullOrEmpty(workspaceId))
        {
            SearchServingPolicy offServingPolicy = NonCanaryServingPolicy(semanticMode, semanticDisabled);
            string offOutput = RunContentCorpus(
                index, query, limit, json, out int offCount, out long offBytes, out _, out _, out _,
                compactBanner, filePattern, language, suggestionLookup,
                offServingPolicy == SearchServingPolicy.Production ? productionRerank : null);
            return new ContentCanaryOutcome(
                new SearchRouteExecutionResult(offOutput, offCount, offBytes),
                Facts: null,
                [],
                ResultHashTruncated: false,
                offServingPolicy);
        }

        SemanticQueryRoute policyRoute = SemanticQueryPolicy.Route(query, LexicalEvidence.None);
        string queryClass = CanaryQueryClassifier.Classify(op, query, policyRoute);
        CanaryVectorProbe vectorProbe = CanaryEligibility.RequiresVectorProbe(op, semanticDisabled, queryClass)
            ? vectorStateProbe()
            : new CanaryVectorProbe("none", Identity: null);
        string vectorState = vectorProbe.State;
        bool crossWorkspaceNoGeneration = foreignWorkspace && vectorState != "ready";
        string eligibility =
            CanaryEligibility.Resolve(op, semanticDisabled, queryClass, vectorState, crossWorkspaceNoGeneration);

        bool eligible = eligibility == CanaryEligibility.Eligible;
        bool treatment = eligible && CanaryAssignment.ResolveArm(
            CanaryAssignment.Bucket(CanaryAssignment.HybridExperimentId, workspaceId, utcDate, queryClass))
            == CanaryArm.Treatment;
        SearchServingPolicy servingPolicy = CanaryServingPolicy(semanticMode, eligible, treatment);

        SemanticQueryResult? consulted = null;
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? rerank =
            servingPolicy == SearchServingPolicy.Treatment && treatmentArmFactory?.Invoke() is { } arm
                ? BuildContentRerank(
                    arm,
                    query,
                    workspaceRoot,
                    result => consulted = result,
                    index,
                    WorkspaceContentSearchKinds,
                    excludeTests,
                    filePattern,
                    language,
                    limit)
                : null;

        string output = RunContentCorpus(
            index, query, limit, json, out int count, out long sourceBytes,
            out int lexicalResultCount, out IReadOnlyList<ContentSearchHit> servedPage,
            out IReadOnlyList<ContentSearchHit> lexicalOrder,
            compactBanner, filePattern, language, suggestionLookup, rerank);

        CanaryCallFacts facts = WithCohortIdentity(
            BuildContentCanaryFacts(
                workspaceId, utcDate, queryClass, eligibility, count, lexicalResultCount, consulted, servedPage, lexicalOrder),
            vectorProbe.Identity);

        IReadOnlyList<string> pathHashes = [];
        bool truncated = false;
        if (eligible && count > 0 && servedPage.Count > 0)
        {
            pathHashes = [.. servedPage.Take(ContentResultHashCap).Select(hit => ContentPathDigest(hit.Path))];
            truncated = servedPage.Count > ContentResultHashCap;
        }

        return new ContentCanaryOutcome(
            new SearchRouteExecutionResult(output, count, sourceBytes),
            facts,
            pathHashes,
            truncated,
            servingPolicy);
    }

    private static CanaryCallFacts BuildContentCanaryFacts(
        string workspaceId,
        string utcDate,
        string queryClass,
        string eligibility,
        int resultCount,
        int lexicalResultCount,
        SemanticQueryResult? consulted,
        IReadOnlyList<ContentSearchHit> servedPage,
        IReadOnlyList<ContentSearchHit> lexicalOrder)
    {
        var facts = new CanaryCallFacts
        {
            WorkspaceId = workspaceId,
            UtcDate = utcDate,
            QueryClass = queryClass,
            Eligibility = eligibility,
        };

        if (eligibility != CanaryEligibility.Eligible)
            return facts;

        SemanticQueryDiagnostics? diagnostics = consulted?.Diagnostics;
        facts = facts with
        {
            ResultCount = resultCount,
            LexicalResultCount = lexicalResultCount,
            FallbackReason = diagnostics is { } d ? MapFallbackReason(d.Fallback) : CanaryFallbackReason.None,
            Backend = diagnostics is { } b ? NormalizeBackend(b.Backend) : CanaryBackend.None,
            EmbedWarmth = ResolveWarmth(diagnostics),
            EmbedLatencyMs = diagnostics?.EmbedMs,
            KnnLatencyMs = diagnostics?.KnnMs,
            EncoderFingerprint = diagnostics?.Identity?.EncoderFingerprint,
            StorageSchema = diagnostics?.Identity?.StorageSchema,
            CorpusGeneration = diagnostics?.Identity?.CorpusGeneration,
        };

        if (consulted is { Served: true } served && served.Hits.Count > 0)
        {
            facts = facts with
            {
                SemanticResultCount = served.Hits.Count,
                FusedResultCount = resultCount,
                SemanticContributionCount = CountContentSemanticContributions(servedPage, lexicalOrder, served.Hits),
                FusionProfile = RrfFusion.FusionProfile,
            };
        }

        return facts;
    }

    /// <summary>
    /// Rank-aware content <c>canary_semantic_contribution_count</c> (contract field table): a served row counts
    /// only when its semantic rank is strictly better than its lexical rank (a row absent from lexical but present
    /// in semantic counts; a row absent from semantic never counts). Ranks join on the stable chunk id;
    /// the lexical rank is the row's 1-based position in the pre-rerank order.
    /// </summary>
    private static int CountContentSemanticContributions(
        IReadOnlyList<ContentSearchHit> servedPage,
        IReadOnlyList<ContentSearchHit> lexicalOrder,
        IReadOnlyList<SemanticHit> semanticHits)
    {
        var semanticRankByChunk = new Dictionary<string, int>(semanticHits.Count, StringComparer.Ordinal);
        foreach (SemanticHit hit in semanticHits)
            if (!string.IsNullOrEmpty(hit.DocId))
                semanticRankByChunk.TryAdd(hit.DocId, hit.Rank);

        var lexicalRankByChunk = new Dictionary<string, int>(lexicalOrder.Count, StringComparer.Ordinal);
        for (int i = 0; i < lexicalOrder.Count; i++)
            if (!string.IsNullOrEmpty(lexicalOrder[i].ChunkId))
                lexicalRankByChunk.TryAdd(lexicalOrder[i].ChunkId!, i + 1);

        int count = 0;
        foreach (ContentSearchHit hit in servedPage)
        {
            if (string.IsNullOrEmpty(hit.ChunkId) ||
                !semanticRankByChunk.TryGetValue(hit.ChunkId, out int semanticRank))
                continue;
            if (!lexicalRankByChunk.TryGetValue(hit.ChunkId, out int lexicalRank) || semanticRank < lexicalRank)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Stamps a content-route canary row: the frozen <see cref="CanaryTelemetry.Stamp"/> writes every field but
    /// the served-result hashes (its <see cref="CanaryCallFacts.ServedResults"/> is empty for content, so it emits
    /// no name/path/qualified array), then the path-only served hashes are written here. Content results are
    /// path+line chunks with no symbol name, so the name and qualified arrays stay absent.
    /// </summary>
    internal static void StampContentCanary(
        TelemetryScope scope,
        CanaryMode mode,
        CanaryCallFacts facts,
        IReadOnlyList<string> pathHashes,
        bool truncated,
        SearchServingPolicy servingPolicy)
    {
        if (servingPolicy == SearchServingPolicy.Shadow)
            return;

        CanaryTelemetry.Stamp(scope, mode, facts);
        if (pathHashes.Count == 0)
            return;

        scope.SetMetadata("canary_result_path_hashes", pathHashes);
        scope.SetMetadata("canary_result_hash_truncated", truncated);
    }

    internal static void StampSymbolCanary(
        TelemetryScope scope,
        CanaryMode mode,
        CanaryCallFacts facts,
        SearchServingPolicy servingPolicy)
    {
        if (servingPolicy != SearchServingPolicy.Shadow)
            CanaryTelemetry.Stamp(scope, mode, facts);
    }

    /// <summary>
    /// The SHA-256 lower-hex digest <see cref="TelemetryScope.SetTarget"/> and <see cref="CanaryTelemetry"/> apply,
    /// so a served content-path digest matches a later <c>inspect</c>/<c>content read</c> <c>target_hash</c> on the
    /// same path.
    /// </summary>
    private static string ContentPathDigest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));

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
        string? language = null,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup = null,
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? rerank = null) =>
        RunContentCorpus(
            index, query, limit, json, out renderedCount, out sourceBytes, out _, out _, out _,
            compactBanner, filePattern, language, suggestionLookup, rerank);

    /// <summary>
    /// The content core with the canary facts exposed alongside the render: <paramref name="lexicalResultCount"/>
    /// is the ranked hit count before paging, <paramref name="servedPage"/> the exact rows rendered in served
    /// order, and <paramref name="lexicalOrder"/> the pre-rerank lexical ranking so a canary row can tell which
    /// served rows the semantic arm actually ranked higher. Same pattern as the served-page overload of
    /// <see cref="RenderSymbolCandidates"/>.
    /// </summary>
    internal static string RunContentCorpus(
        ITextContentSearchIndex index,
        string query,
        int limit,
        bool json,
        out int renderedCount,
        out long sourceBytes,
        out int lexicalResultCount,
        out IReadOnlyList<ContentSearchHit> servedPage,
        out IReadOnlyList<ContentSearchHit> lexicalOrder,
        string? compactBanner = null,
        string? filePattern = null,
        string? language = null,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup = null,
        Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? rerank = null)
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
                    hit.SourceBytes,
                    hit.ChunkId);
                if (filters.Allows(contentHit.Path, contentHit.Language))
                    hits.Add(contentHit);
                else if (filters.HasAny && outsideScope.Count < OutsideScopeHintLimit)
                    outsideScope.Add(contentHit);
            }
            return (fetched.Count, hits.Count);
        });

        lexicalOrder = [.. hits];
        lexicalResultCount = lexicalOrder.Count;
        if (rerank is not null)
            hits = [.. rerank(hits)];

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;
        servedPage = page > 0 ? [.. hits.Take(page)] : [];

        if (total == 0)
        {
            sourceBytes = 0;
            if (json)
                return "[]";
            if (outsideScope.Count > 0)
                return RenderFilteredMissContentCompact(filters, compactBanner, outsideScope);

            IReadOnlyList<IndexedSymbol> suggestions =
                TextEmptySuggestions(WorkspaceContentSearchKinds, query, suggestionLookup);
            return ReadToolWorkspaceRouting.PrefixCompact(
                TextContentEmptyHint(WorkspaceContentSearchKinds, query, suggestions), compactBanner);
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
        string? language = null,
        Func<string, IReadOnlyList<IndexedSymbol>>? suggestionLookup = null,
        Action<IReadOnlyList<string>>? servedPathsSink = null)
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
            hits = DedupByLine(hits);
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
            if (outsideScope.Count > 0)
                return RenderFilteredMissTextContentCompact(filters, compactBanner, outsideScope);

            IReadOnlyList<IndexedSymbol> suggestions =
                TextEmptySuggestions(contentKinds, query, suggestionLookup);
            return ReadToolWorkspaceRouting.PrefixCompact(
                TextContentEmptyHint(contentKinds, query, suggestions), compactBanner);
        }

        sourceBytes = hits
            .Take(page)
            .GroupBy(static hit => hit.SourceId, StringComparer.Ordinal)
            .Sum(static group => group.Max(static hit => hit.SourceBytes));

        servedPathsSink?.Invoke([.. hits.Take(page).Select(static hit => hit.DisplayPath)]);
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
        string? language = null,
        Action<IReadOnlyList<string>>? servedPathsSink = null)
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
            hits = DedupByLine(hits);
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

        servedPathsSink?.Invoke([.. hits.Take(page).Select(static hit => hit.DisplayPath)]);
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

    internal static bool IsLowSignalKind(string kind) =>
        string.Equals(kind, "import", StringComparison.Ordinal) ||
        string.Equals(kind, "module", StringComparison.Ordinal);

    /// <summary>
    /// The mode-contract honesty note (design §6.3): <c>mode=source</c> stays lexical-only under the default
    /// corpus, which embeds docs and config but not source bodies. The note fires only when the arm WOULD have
    /// been consulted — a semantically shaped query over a readable artifact — so it reports a corpus boundary
    /// rather than restating that semantic retrieval is off.
    /// </summary>
    private const string SourceChunksNotIndexedNote =
        "\nnote: source_chunks_not_indexed — the default vector corpus embeds docs/config, not source bodies, " +
        "so mode=source ranks lexically only.";

    private bool SourceChunksNotIndexed(string query, string workspaceRoot) =>
        _semanticArm is { } arm &&
        SemanticQueryPolicy.Route(query, LexicalEvidence.None).IsHybrid &&
        arm.QueryChunks(workspaceRoot, query, 1).Served;

    /// <summary>
    /// The <c>mode=content</c> hybrid arm. It fuses lexical hits with semantic-only chunks when the content index
    /// can materialize stored chunk metadata. Returns null for an absent arm or a lexical-only query.
    /// </summary>
    private Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? SemanticContentRerank(
        ITextContentSearchIndex index,
        string query,
        string workspaceRoot,
        bool excludeTests,
        string? filePattern,
        string? language) =>
        _semanticArm is { } arm
            ? BuildContentRerank(
                arm,
                query,
                workspaceRoot,
                onConsult: null,
                index,
                WorkspaceContentSearchKinds,
                excludeTests,
                filePattern,
                language,
                DefaultLimit)
            : null;

    /// <summary>
    /// The content-mode fusion over one <see cref="ISemanticTextArm"/>. Semantic-only chunks join the result when
    /// <paramref name="index"/> supports <see cref="ISemanticContentLookup"/>; otherwise the lexical membership is
    /// preserved. Returns null for a lexical-only query. <paramref name="onConsult"/> observes arm diagnostics.
    /// </summary>
    internal static Func<IReadOnlyList<ContentSearchHit>, IReadOnlyList<ContentSearchHit>>? BuildContentRerank(
        ISemanticTextArm arm,
        string query,
        string workspaceRoot,
        Action<SemanticQueryResult>? onConsult = null,
        ITextContentSearchIndex? index = null,
        IReadOnlyCollection<string>? contentKinds = null,
        bool excludeTests = false,
        string? filePattern = null,
        string? language = null,
        int candidateLimit = DefaultLimit)
    {
        SemanticQueryRoute route = SemanticQueryPolicy.Route(query, LexicalEvidence.None);
        if (!route.IsHybrid)
            return null;

        FusionWeights weights = RrfFusion.WeightsFor(route.HybridClass);
        ToolSearchFilters filters = ToolSearchFilters.Parse(filePattern, language);
        return lexical =>
        {
            int semanticDepth = excludeTests || filters.HasAny
                ? SemanticSearchArm.MaxCandidates
                : Math.Clamp(
                    Math.Max(lexical.Count, candidateLimit * 4 + 10),
                    1,
                    SemanticSearchArm.MaxCandidates);
            SemanticQueryResult semantic = arm.QueryChunks(
                workspaceRoot,
                query,
                semanticDepth);
            onConsult?.Invoke(semantic);
            if (!semantic.Served || semantic.Hits.Count == 0)
                return lexical;

            IReadOnlyList<ContentSearchHit> materialized = MaterializeSemanticContentHits(
                index,
                semantic.Hits,
                contentKinds ?? WorkspaceContentSearchKinds,
                excludeTests,
                filters);
            return FuseContentHits(lexical, materialized, semantic.Hits, weights);
        };
    }

    /// <summary>
    /// Weighted reciprocal-rank fusion over chunk hits, matching the frozen <c>fusion-v1</c> constants the
    /// symbol route uses. Hits join on the stable chunk id shared by the vector map and content corpus.
    /// </summary>
    private static IReadOnlyList<ContentSearchHit> FuseContentHits(
        IReadOnlyList<ContentSearchHit> lexical,
        IReadOnlyList<ContentSearchHit> materialized,
        IReadOnlyList<SemanticHit> semantic,
        FusionWeights weights)
    {
        var semanticRanks = new Dictionary<string, int>(semantic.Count, StringComparer.Ordinal);
        foreach (SemanticHit hit in semantic)
            if (!string.IsNullOrEmpty(hit.DocId))
                semanticRanks.TryAdd(hit.DocId, hit.Rank);

        var union = new List<(ContentSearchHit Hit, int? LexicalRank)>(lexical.Count + materialized.Count);
        var seen = new HashSet<(string Path, int Line)>();
        for (int index = 0; index < lexical.Count; index++)
        {
            ContentSearchHit hit = lexical[index];
            if (seen.Add((hit.Path, hit.Line)))
                union.Add((hit, index + 1));
        }

        foreach (ContentSearchHit hit in materialized)
            if (seen.Add((hit.Path, hit.Line)))
                union.Add((hit, LexicalRank: null));

        return
        [
            .. union
                .Select(row => (row.Hit, Fused: FusedScore(row.LexicalRank, semanticRanks, row.Hit.ChunkId, weights)))
                .OrderByDescending(row => row.Fused)
                .ThenByDescending(row => row.Hit.Score)
                .ThenBy(row => row.Hit.Path, StringComparer.Ordinal)
                .ThenBy(row => row.Hit.Line)
                .Select(row => row.Hit),
        ];
    }

    private static double FusedScore(
        int? lexicalRank,
        IReadOnlyDictionary<string, int> semanticRanks,
        string? chunkId,
        FusionWeights weights) =>
        (lexicalRank is { } rank ? weights.Lexical / (RrfFusion.RankConstant + rank) : 0d) +
        (chunkId is not null && semanticRanks.TryGetValue(chunkId, out int semanticRank)
            ? weights.Semantic / (RrfFusion.RankConstant + semanticRank)
            : 0d);

    private static IReadOnlyList<ContentSearchHit> MaterializeSemanticContentHits(
        ITextContentSearchIndex? index,
        IReadOnlyList<SemanticHit> semantic,
        IReadOnlyCollection<string> contentKinds,
        bool excludeTests,
        ToolSearchFilters filters)
    {
        if (index is not ISemanticContentLookup lookup)
            return [];

        IReadOnlyList<string> chunkIds =
        [
            .. semantic
                .Select(static hit => hit.DocId)
                .Where(static chunkId => !string.IsNullOrEmpty(chunkId))
                .Select(static chunkId => chunkId!)
                .Distinct(StringComparer.Ordinal),
        ];
        if (chunkIds.Count == 0)
            return [];

        return
        [
            .. lookup.Materialize(chunkIds, contentKinds, excludeTests)
                .Where(hit => filters.Allows(hit.DisplayPath, hit.Language))
                .Select(static hit => new ContentSearchHit(
                    hit.DisplayPath,
                    hit.Score,
                    hit.Line,
                    hit.Snippet,
                    hit.Language,
                    hit.SourceBytes,
                    hit.ChunkId)),
        ];
    }

    private sealed record AutoTextRescueResult(
        string Output, int Count, long SourceBytes, string Kind, IReadOnlyList<string> ServedPaths)
    {
        public AutoTextRescueResult(string output, int count, long sourceBytes, string kind)
            : this(output, count, sourceBytes, kind, [])
        {
        }
    }

    /// <summary>The rescue block's hard row budget (design §6.3) — an affordance, not a result page.</summary>
    private const int SemanticRescueRows = 2;

    /// <summary>Recall depth per corpus. Deeper than the row budget so the visibility filter has something to
    /// reject without collapsing the rung to nothing.</summary>
    private const int SemanticRescueRecall = 8;

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
        string? compactBanner,
        WorkspaceSymbolSearchContext symbolContext,
        SearchServingPolicy servingPolicy)
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

            // The final rung (design §6.3): every lexical corpus came back empty, so the only affordance left is
            // "something semantically near your question exists". It is last precisely because a lexical hit is
            // evidence the agent can verify by reading, and a neighbour is not.
            if (!AllowsSemanticServing(servingPolicy))
                return sourceRescue;

            AutoTextRescueResult? semantic = TryRunSemanticRescue(
                query,
                excludeTests,
                primaryOutput,
                primaryCount,
                filePattern,
                language,
                compactBanner,
                symbolContext,
                textContent.Index);
            return semantic ?? sourceRescue;
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
        IReadOnlyList<string> servedPaths = [];
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
            language,
            paths => servedPaths = paths);
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
                "source",
                servedPaths);
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
        IReadOnlyList<string> servedPaths = [];
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
            language,
            suggestionLookup: null,
            servedPathsSink: paths => servedPaths = paths);
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
                "docs_config",
                servedPaths);
    }

    /// <summary>
    /// The semantic rescue rung: at most two rows, each labelled with the corpus it came from, plus the single
    /// closing affordance. Returns null whenever the arm is absent, the query is not semantically shaped, the
    /// artifact could not be consulted, or nothing was found — every one of which leaves the lexical bytes
    /// exactly as they were.
    /// </summary>
    private AutoTextRescueResult? TryRunSemanticRescue(
        string query,
        bool? excludeTests,
        string primaryOutput,
        int primaryCount,
        string? filePattern,
        string? language,
        string? compactBanner,
        WorkspaceSymbolSearchContext symbolContext,
        ITextContentSearchIndex contentIndex)
    {
        if (_semanticArm is not { } arm)
            return null;

        // Rescue only runs when the lexical arms already came back weak or empty, so that IS the evidence the
        // policy would otherwise read off a ranking — there is no stronger lexical signal left to consult.
        if (!SemanticQueryPolicy.Route(query, LexicalEvidence.None).IsHybrid)
            return null;

        var visibility = new SymbolVisibilityPolicy(
            ResolveExcludeTests(excludeTests, query, SearchToolMode.Auto),
            ResolveHideLowSignalKinds(query, SearchToolMode.Auto),
            ToolSearchFilters.Parse(filePattern, language));
        ISymbolLookupIndex index = symbolContext.Index;
        string root = symbolContext.WorkspaceRoot;

        SemanticQueryResult symbols = arm.QuerySymbols(
            root,
            query,
            SemanticRescueRecall,
            match => index.FindBySymbolId(match.UnitId) is { } symbol && visibility.Allows(symbol));
        SemanticQueryResult chunks = arm.QueryChunks(
            root,
            query,
            visibility.HideTests || visibility.Filters.HasAny
                ? SemanticSearchArm.MaxCandidates
                : SemanticRescueRecall);

        List<IndexedSymbol> symbolHits =
        [
            .. symbols.Hits
                .Select(hit => hit.SymbolId is { } id ? index.FindBySymbolId(id) : null)
                .Where(symbol => symbol is not null)
                .Select(symbol => symbol!),
        ];
        List<string> symbolRows =
            [.. symbolHits.Select(symbol => $"  semantic symbol  {symbol.Name}  {symbol.FilePath}:{symbol.StartLine}")];
        List<string> symbolPaths = [.. symbolHits.Select(symbol => symbol.FilePath)];
        IEnumerable<string> visibleChunkPaths = contentIndex is ISemanticContentLookup
            ? MaterializeSemanticContentHits(
                    contentIndex,
                    chunks.Hits,
                    WorkspaceContentSearchKinds,
                    visibility.HideTests,
                    visibility.Filters)
                .Select(static hit => hit.Path)
            : chunks.Hits
                .Where(hit => hit.DocId is not null)
                .Where(hit => !visibility.HideTests || !IsTestPath.Check(hit.FilePath))
                .Where(hit => visibility.Filters.Allows(hit.FilePath, string.Empty))
                .Select(static hit => hit.FilePath);
        List<string> chunkPaths = [.. visibleChunkPaths.Distinct(StringComparer.Ordinal)];
        List<string> chunkRows = [.. chunkPaths.Select(path => $"  semantic docs  {path}")];

        if (symbolRows.Count == 0 && chunkRows.Count == 0)
            return null;

        // One row from each corpus when both answered, otherwise two from whichever did — the ≤2 budget spent on
        // breadth first, because a second neighbour from the same corpus adds far less than the other corpus does.
        int symbolTake = chunkRows.Count == 0 ? SemanticRescueRows : Math.Min(symbolRows.Count, 1);
        int chunkTake = SemanticRescueRows - symbolTake;
        List<string> rows = [.. symbolRows.Take(symbolTake), .. chunkRows.Take(chunkTake)];
        IReadOnlyList<string> servedPaths = [.. symbolPaths.Take(symbolTake), .. chunkPaths.Take(chunkTake)];

        string kind = (symbolTake > 0 && symbolRows.Count > 0, chunkTake > 0 && chunkRows.Count > 0) switch
        {
            (true, true) => "semantic_mixed",
            (true, false) => "semantic_symbol",
            _ => "semantic_docs",
        };

        return new AutoTextRescueResult(
            RenderAutoTextRescueCompact(
                primaryOutput,
                primaryCount,
                string.Join('\n', rows),
                compactBanner,
                "Semantic matches also found:",
                SemanticRescueAffordance(kind, rows)),
            rows.Count,
            SourceBytes: 0,
            kind,
            servedPaths);
    }

    private static string SemanticRescueAffordance(string kind, IReadOnlyList<string> rows) =>
        kind == "semantic_symbol"
            ? $"Try: inspect target=\"{SymbolNameFromRescueRow(rows[0])}\""
            : "Rerun with mode=content for more docs/config snippets.";

    private static string SymbolNameFromRescueRow(string row) =>
        row.Trim()["semantic symbol  ".Length..].Split("  ", StringSplitOptions.None)[0];

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
            // Design rule: max one next-step affordance per output. The rescue's "Rerun with mode=…" line
            // is this output's single closing affordance, and it only fires when symbol results look weak —
            // a "next: inspect" nudge on a weak top hit would compete with it. So drop the trailing inspect
            // nudge that RunAutoSearch appended to primaryOutput. NextStepHint renders the hint as a single
            // line with no trailing newline, so dropping the final line iff it starts with "next: " is
            // deterministic.
            sb.Append(StripTrailingNextHint(primaryOutput.TrimEnd('\n'))).Append("\n\n");
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

    // Drop a trailing "next: …" nudge line (see RenderAutoTextRescueCompact rationale). Deterministic
    // because NextStepHint emits the hint as one line with no trailing newline.
    private static string StripTrailingNextHint(string output)
    {
        int lastNewline = output.LastIndexOf('\n');
        string lastLine = lastNewline < 0 ? output : output[(lastNewline + 1)..];
        if (!lastLine.StartsWith("next: ", StringComparison.Ordinal))
            return output;
        return lastNewline < 0 ? string.Empty : output[..lastNewline];
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

    /// <summary>
    /// Resolve a file-path query that may carry a prefix ABOVE the workspace root. Indexed paths are
    /// workspace-RELATIVE, so <c>./x</c>, <c>~/x</c>, <c>/abs/root/x</c>, and <c>&lt;repo-dir&gt;/x</c> can never be
    /// a substring of one and miss no matter how correct the agent's path is. Runs entirely above
    /// <see cref="ISymbolLookupIndex"/> so every backend gets it — the in-memory tables and the default-on FTS
    /// sidecar carry SEPARATE copies of the fragment-ranking logic, and fixing only one would leave the shipped
    /// default broken.
    /// </summary>
    private static IReadOnlyList<IndexedSymbol> FindByFilePathWithPrefixRecovery(
        ISymbolLookupIndex index, string query, int limit)
    {
        IReadOnlyList<IndexedSymbol> direct = index.FindByFilePathFragment(query, limit);
        if (direct.Count > 0)
            return direct;

        string normalized = NormalizePathQuery(query);
        if (normalized.Length == 0)
            return direct;

        if (!string.Equals(normalized, query.Trim().Replace('\\', '/'), StringComparison.Ordinal))
        {
            IReadOnlyList<IndexedSymbol> viaNormalized = index.FindByFilePathFragment(normalized, limit);
            if (viaNormalized.Count > 0)
                return viaNormalized;
        }

        foreach (string suffix in PathQuerySuffixes(normalized))
        {
            IReadOnlyList<IndexedSymbol> viaSuffix = index.FindByFilePathFragment(suffix, limit);
            if (viaSuffix.Count > 0)
                return viaSuffix;
        }

        return direct;
    }

    // Strips only the prefixes that CANNOT appear in a workspace-relative path, so this is normalization rather
    // than guessing: a leading ./, ../, ~/, or / carries no information the index could match on.
    private static string NormalizePathQuery(string query)
    {
        string normalized = query.Trim().Replace('\\', '/');
        while (normalized.Length > 0)
        {
            if (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            else if (normalized.StartsWith("../", StringComparison.Ordinal))
                normalized = normalized[3..];
            else if (normalized.StartsWith("~/", StringComparison.Ordinal))
                normalized = normalized[2..];
            else if (normalized[0] == '/')
                normalized = normalized[1..];
            else
                break;
        }

        return normalized;
    }

    // Drops leading segments one at a time to find the part of an over-qualified path the index actually holds
    // (`/Users/me/repo/src/App.cs` → `src/App.cs`). Stops before the query would decay to a bare basename: that
    // would turn "right filename, wrong directory" from an honest miss into a confident wrong answer.
    private static IEnumerable<string> PathQuerySuffixes(string normalizedQuery)
    {
        string remainder = normalizedQuery;
        while (true)
        {
            int separator = remainder.IndexOf('/', StringComparison.Ordinal);
            if (separator < 0)
                yield break;

            remainder = remainder[(separator + 1)..];
            if (SegmentCount(remainder) < 2)
                yield break;

            yield return remainder;
        }
    }

    private static int SegmentCount(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

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

    private static bool LooksLikePathQueryShape(string query)
    {
        if (query.StartsWith("./", StringComparison.Ordinal) ||
            query.StartsWith("../", StringComparison.Ordinal) ||
            query.StartsWith("~/", StringComparison.Ordinal))
            return true;

        bool hasWhitespace = query.Any(char.IsWhiteSpace);
        if (!hasWhitespace && query.Any(static ch => ch is '/' or '\\'))
            return true;

        string ext = Path.GetExtension(query);
        return !hasWhitespace && PathQueryExtensions.Contains(ext);
    }

    private static bool LooksLikeSourceCodeQuery(string query)
    {
        foreach (char ch in query)
        {
            if (ch is '(' or ')' or '{' or '}' or '[' or ']' or ';' or '=' or '<' or '>' or '!'
                or '&' or '|' or '+' or '*' or '%' or ':' or '"' or '\'' or '`')
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

    private static bool LooksLikeExactIdentifierQuery(string query)
    {
        string trimmed = query.Trim();
        if (!LooksLikeWeakIdentifierQuery(trimmed))
            return false;

        bool hasSeparator = trimmed.IndexOfAny(['_', '.']) >= 0;
        bool hasLower = false;
        bool hasUpperAfterFirst = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            hasLower |= char.IsLower(ch);
            hasUpperAfterFirst |= i > 0 && char.IsUpper(ch);
        }

        return hasSeparator || (hasLower && hasUpperAfterFirst);
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

    /// <summary>
    /// Near-match symbol names for an identifier the text corpus had no hit for. Resolving the SYMBOL index is
    /// deferred into this method on purpose: the text routes never need it, so it is paid for only when an empty
    /// identifier-like text miss actually asks. A workspace that cannot resolve suggests nothing rather than
    /// failing a search that already has a usable answer.
    /// </summary>
    private IReadOnlyList<IndexedSymbol> SuggestSymbolsBestEffort(
        string identifier,
        string? workspaceId,
        bool ensureFresh)
    {
        try
        {
            WorkspaceSymbolSearchContext context = _workspaceProvider.ResolveSymbolSearch(workspaceId, ensureFresh);
            return SymbolSuggestionEngine.Suggest(context.Index, identifier, EmptySuggestionLimit);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            return [];
        }
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
        IReadOnlyList<SymbolCandidate> outsideScope)
    {
        var sb = FilteredMissHeader(filters, compactBanner, outsideScope.Select(static symbol => symbol.FilePath));
        foreach (SymbolCandidate s in outsideScope)
        {
            sb.Append('\n')
              .Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine);
            if (IsLowSignalKind(s.Kind))
                sb.Append("  low_signal");
            else if (!string.IsNullOrEmpty(s.Signature))
                sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
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
        IReadOnlyList<SymbolCandidate> kept,
        int page,
        int total,
        string query,
        string? compactBanner,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        int definitionIndex = FindPromotableDefinitionIndex(kept, page, query);
        if (definitionIndex >= 0)
            return RenderDefinitionCompact(kept, page, total, definitionIndex, query, compactBanner, hasDocSymbolIds);

        var groups = new List<(string FilePath, List<SymbolCandidate> Symbols)>();
        for (int i = 0; i < page; i++)
        {
            SymbolCandidate symbol = kept[i];
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<SymbolCandidate> { symbol }));
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
                foreach (SymbolCandidate s in groups[g].Symbols)
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
        string query,
        IReadOnlyList<IndexedSymbol> suggestions)
    {
        return ReadToolWorkspaceRouting.PrefixCompact(
            AppendSuggestions(SymbolEmptyHint(query), suggestions), compactBanner);
    }

    /// <summary>Hard ceiling on a compact empty result, banner excluded.</summary>
    private const int EmptyCompactBudget = 400;

    private const string TryLinePrefix = "\nTry: ";
    private const string SuggestionSeparator = ", ";

    /// <summary>
    /// The one <c>Try:</c> near-match renderer, shared by the symbol-route and text-route empty paths. Entries are
    /// fitted against <see cref="EmptyCompactBudget"/> and any that do not fit are reported as <c>… N more</c>
    /// rather than dropped silently: suggestion text is variable-length (a deep path plus a long symbol name runs
    /// ~90 chars an entry), so a fixed 3-entry line would blow the budget on real workspaces even though it fits
    /// short-path fixtures. Callers pass the banner-free hint — the budget excludes the workspace banner.
    /// </summary>
    private static string AppendSuggestions(string hint, IReadOnlyList<IndexedSymbol> suggestions)
    {
        if (suggestions.Count == 0)
            return hint;

        string[] entries = suggestions
            .Select(static s => $"{s.Name} ({s.FilePath}:{s.StartLine})")
            .ToArray();

        int budget = EmptyCompactBudget - hint.Length - TryLinePrefix.Length;
        var kept = new List<string>(entries.Length);
        int used = 0;
        foreach (string entry in entries)
        {
            int cost = entry.Length + (kept.Count > 0 ? SuggestionSeparator.Length : 0);
            bool isLast = kept.Count + 1 == entries.Length;
            int reserve = isLast ? 0 : OverflowNoteLength(entries.Length - kept.Count - 1);
            if (used + cost + reserve > budget)
                break;
            kept.Add(entry);
            used += cost;
        }

        if (kept.Count == 0)
            return hint;

        string line = string.Join(SuggestionSeparator, kept);
        int omitted = entries.Length - kept.Count;
        return hint + TryLinePrefix + (omitted > 0 ? line + OverflowNote(omitted) : line);
    }

    private static string OverflowNote(int omitted) => $"{SuggestionSeparator}… {omitted} more";

    private static int OverflowNoteLength(int omitted) => OverflowNote(omitted).Length;

    // Escape a symbol name for embedding inside a quoted tool-call argument, matching context's NextInspectLine
    // precedent: backslash first, then quote, so a name containing either stays a single well-formed hint line.
    private static string EscapeCallString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void AppendSymbolAnnotations(StringBuilder sb, SymbolCandidate s, IReadOnlySet<string>? hasDocSymbolIds)
    {
        if (IsLowSignalKind(s.Kind))
            sb.Append("  low_signal");
        else if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (hasDocSymbolIds?.Contains(s.SymbolId) == true)
            sb.Append("  has_doc");
    }

    private static string RenderMixedCompact(
        IReadOnlyList<SymbolCandidate> kept,
        int page,
        string? compactBanner)
    {
        SymbolCandidate[] symbols = kept
            .Take(page)
            .Where(candidate => candidate.Origin == SymbolCandidateOrigin.Symbol)
            .ToArray();
        SymbolCandidate[] files = kept
            .Take(page)
            .Where(candidate => candidate.Origin == SymbolCandidateOrigin.File)
            .ToArray();
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            builder.Append(compactBanner).Append('\n');
        if (symbols.Length > 0)
        {
            builder.Append("Symbol matches:").Append('\n');
            foreach (SymbolCandidate symbol in symbols)
            {
                builder.Append("  ")
                    .Append(symbol.Name)
                    .Append("  ")
                    .Append(symbol.Kind)
                    .Append("  ")
                    .Append(symbol.FilePath)
                    .Append(':')
                    .Append(symbol.StartLine);
                if (!string.IsNullOrWhiteSpace(symbol.Signature))
                {
                    builder.Append('\n')
                        .Append("    ")
                        .Append(Truncate(symbol.Signature, ToolRenderLimits.SignatureMaxLength));
                }
                builder.Append('\n');
            }
        }
        if (files.Length > 0)
        {
            if (symbols.Length > 0)
                builder.Append('\n');
            builder.Append("File matches:").Append('\n');
            foreach (SymbolCandidate file in files)
                builder.Append("  ").Append(file.FilePath).Append('\n');
        }
        TrimTrailingNewlines(builder);
        return builder.ToString();
    }

    private static string RenderFileCompact(
        IReadOnlyList<SymbolCandidate> kept,
        int page,
        int total,
        string? compactBanner,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var groups = new List<(string FilePath, List<SymbolCandidate> Symbols)>();
        for (int i = 0; i < page; i++)
        {
            SymbolCandidate symbol = kept[i];
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<SymbolCandidate> { symbol }));
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
        IReadOnlyList<SymbolCandidate> symbols,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        foreach (SymbolCandidate symbol in symbols)
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
        IReadOnlyList<SymbolCandidate> kept,
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

        SymbolCandidate definition = kept[definitionIndex];
        sb.Append("Definition found: ").Append(query.Trim()).Append('\n');
        AppendPromotedDefinition(sb, definition, hasDocSymbolIds);

        var otherRows = new List<SymbolCandidate>(Math.Max(0, page - 1));
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

    private static int FindPromotableDefinitionIndex(IReadOnlyList<SymbolCandidate> kept, int page, string query)
    {
        string queryLower = query.Trim().ToLowerInvariant();
        if (queryLower.Length == 0)
            return -1;

        for (int i = 0; i < page; i++)
        {
            SymbolCandidate symbol = kept[i];
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
        SymbolCandidate symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append("  ").Append(symbol.FilePath).Append(':').Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        if (hasDocSymbolIds?.Contains(symbol.SymbolId) == true)
            sb.Append(" has_doc");
        sb.Append('\n');

        if (!string.IsNullOrEmpty(symbol.Signature))
            sb.Append("  ").Append(Truncate(symbol.Signature!, ToolRenderLimits.SignatureMaxLength)).Append('\n');
    }

    private static void AppendOtherMatchesGroupedByFile(
        StringBuilder sb,
        IReadOnlyList<SymbolCandidate> symbols,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        var groups = new List<(string FilePath, List<SymbolCandidate> Symbols)>();
        foreach (SymbolCandidate symbol in symbols)
        {
            int groupIndex = groups.FindIndex(group => group.FilePath == symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Symbols.Add(symbol);
            else
                groups.Add((symbol.FilePath, new List<SymbolCandidate> { symbol }));
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
                foreach (SymbolCandidate symbol in group.Symbols)
                    AppendGroupedOtherMatch(sb, symbol, hasDocSymbolIds);
            }

            if (i < groups.Count - 1)
                sb.Append('\n');
        }
    }

    private static void AppendSingleOtherMatch(
        StringBuilder sb,
        SymbolCandidate symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append(symbol.FilePath).Append(':').Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        AppendCompactMatchDetails(sb, symbol, "  ", hasDocSymbolIds);
    }

    private static void AppendGroupedOtherMatch(
        StringBuilder sb,
        SymbolCandidate symbol,
        IReadOnlySet<string>? hasDocSymbolIds)
    {
        sb.Append("  :").Append(symbol.StartLine)
          .Append(" (").Append(symbol.Kind).Append(')');
        AppendCompactMatchDetails(sb, symbol, "    ", hasDocSymbolIds);
    }

    private static void AppendCompactMatchDetails(
        StringBuilder sb,
        SymbolCandidate symbol,
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
            sb.Append('\n').Append(continuationIndent).Append(Truncate(symbol.Signature!, ToolRenderLimits.SignatureMaxLength));
        sb.Append('\n');
    }

    private static void TrimTrailingNewlines(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] is '\n' or '\r')
            sb.Length--;
    }

    /// <summary>
    /// Rows are the lexical shape plus, on a fused run only, the additive provenance a caller needs to explain
    /// an ordering it did not expect. <c>score</c> keeps meaning the lexical score in every mode, and a
    /// lexical-only run writes no fusion keys at all, so its bytes are unchanged.
    /// </summary>
    private static string RenderJson(
        IReadOnlyList<SymbolCandidate> kept,
        int page,
        IReadOnlySet<string>? hasDocSymbolIds,
        IReadOnlyDictionary<string, FusedCandidate>? fusion = null,
        bool relaxed = false,
        bool mixed = false,
        bool exactMiss = false,
        bool boundAgentOutput = false)
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
                else writer.WriteString(
                    "signature",
                    boundAgentOutput
                        ? Truncate(s.Signature, ToolRenderLimits.SignatureMaxLength)
                        : s.Signature);
                writer.WriteNumber("score", s.Score);
                writer.WriteString("symbol_id", s.SymbolId);
                if (mixed)
                {
                    writer.WriteString(
                        "result_type",
                        s.Origin == SymbolCandidateOrigin.File ? "file" : "symbol");
                }
                if (relaxed)
                    writer.WriteBoolean("relaxed", true);
                if (exactMiss)
                    writer.WriteBoolean("exact_match", false);
                if (fusion is not null && fusion.TryGetValue(s.SymbolId, out FusedCandidate? fused))
                {
                    writer.WriteNumber("rrf_score", fused.RrfScore);
                    if (fused.LexicalRank is { } lexicalRank)
                        writer.WriteNumber("lexical_rank", lexicalRank);
                    if (fused.SemanticRank is { } semanticRank)
                        writer.WriteNumber("semantic_rank", semanticRank);
                }
                if (hasDocSymbolIds is not null)
                    writer.WriteBoolean("has_doc", hasDocSymbolIds.Contains(s.SymbolId));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RenderEmptyJson(
        IReadOnlyList<IndexedSymbol> suggestions,
        bool relaxed = false)
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
            if (relaxed)
                writer.WriteBoolean("relaxed", true);
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
