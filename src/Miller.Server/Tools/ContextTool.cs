using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// Produces a task-anchored, token-budgeted bundle of ranked pivots, implementation snippets, graph neighbours,
/// and optional usage evidence. Query retrieval and explicit task anchors share one pivot ranker; optional
/// semantic evidence is admitted only when the semantic policy serves it.
/// </summary>
[McpServerToolType]
public sealed partial class ContextTool
{
    private const int TermRescuePromotionReadLimit = 8;
    private const int TermRescueRetrievalLimit = 6;

    /// <summary>
    /// The lexical window the semantic-seed gate judges. It is NOT the pivot ranker's window, and widening it to
    /// share one retrieval is NOT free: the gate keeps the retrieved ids as the membership set that admits a
    /// semantic hit under <c>RerankOnly</c> admission, and <c>CollectSymbolCandidates</c> returns everything the
    /// over-fetch window scored rather than truncating to the limit. A wider window therefore admits semantic
    /// seeds the narrow one drops and changes the rendered bundle. Any change here is a ranking change and needs
    /// its own approval and baseline.
    /// </summary>
    private const int SemanticSeedGateLimit = 2;
    private readonly IWorkspaceIndexProvider _workspaceProvider;
    private readonly ISemanticTextArm? _semanticArm;
    private readonly VectorSidecar? _semanticSidecar;
    private readonly Action<string>? _phaseObserver;
    private readonly Action<ContextLookupPhaseObservation>? _lookupPhaseObserver;

    /// <summary>Construct a lexical-only context tool over the freshness-aware workspace provider.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ContextTool(IWorkspaceIndexProvider workspaceProvider)
        : this(workspaceProvider, semanticArm: null, semanticSidecar: null)
    {
    }

    public ContextTool(
        IWorkspaceIndexProvider workspaceProvider,
        VectorSidecar semanticSidecar,
        SemanticEmbeddingSessionBroker embeddingBroker)
        : this(
            workspaceProvider,
            SemanticTextArm.For(semanticSidecar, embeddingBroker),
            semanticSidecar)
    {
    }

    internal ContextTool(
        IWorkspaceIndexProvider workspaceProvider,
        ISemanticTextArm? semanticArm,
        VectorSidecar? semanticSidecar,
        Action<string>? phaseObserver = null,
        Action<ContextLookupPhaseObservation>? lookupPhaseObserver = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
        _semanticArm = semanticArm;
        _semanticSidecar = semanticSidecar;
        _phaseObserver = phaseObserver;
        _lookupPhaseObserver = lookupPhaseObserver;
    }

    public string Context(
        string query,
        int token_budget = 2000,
        int max_hops = 1,
        string[]? entry_symbols = null,
        string? failing_test = null,
        string? stack_trace = null,
        string format = "compact",
        string reference_mode = "off",
        int reference_depth = 1,
        bool exclude_tests = false,
        string? workspace_id = null,
        bool? ensure_fresh = null,
        string[]? edited_files = null) =>
        ContextWithCancellation(
            query,
            token_budget,
            max_hops,
            entry_symbols,
            failing_test,
            stack_trace,
            format,
            reference_mode,
            reference_depth,
            exclude_tests,
            workspace_id,
            ensure_fresh,
            edited_files,
            CancellationToken.None);

    [McpServerTool(Name = "context")]
    [Description(
        "First call in an UNFAMILIAR code area: give a task plus optional entry symbols, edited files, failing " +
        "test, or stack trace. Returns ranked pivots with bounded implementation snippets, neighbour signatures, " +
        "reasons, and an evidence disposition within token_budget; a next action appears only when evidence is " +
        "insufficient. When disposition is sufficient, answer from the bundle instead of inspecting every pivot. " +
        "NOT for: a symbol you can already name (inspect it) or text lookups (search). Example: " +
        "context query=\"how does workspace refresh converge the search sidecar\". Compact by default; " +
        "format=json to chain.")]
    public string ContextWithCancellation(
        [Description("The task or question to anchor the bundle on.")] string query,
        [Description("Hard bound on complete output in estimated tokens. Default 2000; MCP maximum 2400.")]
        int token_budget = 2000,
        [Description("Neighbour expansion radius in hops (0–2). Default 1.")] int max_hops = 1,
        [Description("Entry symbol names, ids, or indexed file paths to rank as pivots. Optional.")] string[]? entry_symbols = null,
        [Description("A failing test name or snippet used to rank matching pivots. Optional.")]
        string? failing_test = null,
        [Description("A stack trace; file, line, and symbol evidence rank matching pivots. Optional.")]
        string? stack_trace = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Reference enrichment mode: off|usage. Default off.")]
        string reference_mode = "off",
        [Description("Reference expansion depth for reference_mode=usage, clamped 0–1. Default 1.")]
        int reference_depth = 1,
        [Description("When reference_mode=usage, filter test symbols, test-path references, and test content chunks. Default false.")]
        bool exclude_tests = false,
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Wait for a refresh before reading. With workspace_id the default now serves the pinned index immediately and refreshes in the background; true still waits, false does zero refresh work.")]
        bool? ensure_fresh = null,
        [Description("Workspace-relative files changed by the current task; their symbols rank as pivots. Optional.")]
        string[]? edited_files = null,
        [Description("Framework request cancellation token.")]
        CancellationToken cancellationToken = default)
    {
        var telemetry = TelemetryContext.Current;
        long phaseStart = Stopwatch.GetTimestamp();
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token_budget <= 0)
                return string.Empty;

            WorkspaceRefreshMode refresh = ReadToolWorkspaceRouting.ResolveRefreshMode(workspace_id, ensure_fresh);
            int effectiveTokenBudget = Math.Min(token_budget, ToolOutputBudget.ContextMcpMaxTokens);
            using WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, refresh);
            using IDisposable? searchTelemetry = context.ReadTelemetry?.ActivateSearchTelemetry();
            ISymbolLookupIndex contextIndex = new ContextSearchCacheLookupIndex(context.Index);
            var contextResolver = new SmartTargetResolver(contextIndex);
            CompletePhase("resolve", telemetry, ref phaseStart);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            int bundleTokenBudget = Math.Max(
                0,
                effectiveTokenBudget -
                (compactBanner is null ? 0 : (int)TokenEstimator.Count(compactBanner + '\n')));
            int selectedCount;
            int candidatesExamined;
            string output;
            ReferenceMode parsedReferenceMode = ParseReferenceMode(reference_mode);
            bool rescueExcludeTests = parsedReferenceMode == ReferenceMode.Usage
                ? exclude_tests
                : SearchTool.ResolveExcludeTests(null, query, SearchToolMode.Symbol);
            var queryRetrieval = new ContextQueryRetrieval(contextIndex);
            IReadOnlyList<ContextSemanticSeed> semanticSeeds = [];
            IReadOnlyList<ContextSourceSeed> sourceSeeds = [];
            cancellationToken.ThrowIfCancellationRequested();
                semanticSeeds = LoadSemanticSeeds(
                    context,
                    contextIndex,
                    query,
                    parsedReferenceMode == ReferenceMode.Usage && exclude_tests,
                    queryRetrieval);
                CompletePhase("semantic_seeds", telemetry, ref phaseStart);
                cancellationToken.ThrowIfCancellationRequested();
                sourceSeeds = LoadSourceRescueSeeds(
                    contextIndex,
                    TryResolveTextContentIndex(workspace_id, refresh),
                    query,
                    rescueExcludeTests);
                CompletePhase("source_rescue", telemetry, ref phaseStart, context.ReadTelemetry);
                cancellationToken.ThrowIfCancellationRequested();
                switch (parsedReferenceMode)
                {
                    case ReferenceMode.Off:
                        output = RunActionableWithCancellation(
                            contextIndex,
                            context.Graph,
                            contextResolver,
                            query,
                            bundleTokenBudget,
                            max_hops,
                            entry_symbols,
                            edited_files,
                            failing_test,
                            stack_trace,
                            semanticSeeds,
                            sourceSeeds,
                            readBody: symbol => ReadPivotBody(
                                context.ReadSession,
                                context.WorkspaceRoot,
                                symbol),
                            readOutgoingMany: symbolIds => ReadOutgoingBatch(context.ReadSession, symbolIds),
                            json,
                            out selectedCount, out candidatesExamined,
                            cancellationToken,
                            phase => CompletePhase(
                                phase,
                                telemetry,
                                ref phaseStart,
                                context.ReadTelemetry),
                            queryRetrieval);
                        break;
                    case ReferenceMode.Usage:
                        output = RunReferenceAwareActionableWithCancellation(
                            contextIndex,
                            context.Graph,
                            contextResolver,
                            query,
                            bundleTokenBudget,
                            max_hops,
                            entry_symbols,
                            edited_files,
                            failing_test,
                            stack_trace,
                            semanticSeeds,
                            sourceSeeds,
                            readBody: symbol => ReadPivotBody(
                                context.ReadSession,
                                context.WorkspaceRoot,
                                symbol),
                            reference_depth, exclude_tests, json,
                            readReferenceEvidence: symbol => ReferenceEvidenceReader.Read(
                                context.ReadSession,
                                symbol.SymbolId,
                                new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol)),
                            readOutgoingEvidence: symbol => ReferenceEvidenceReader.ReadOutgoing(
                                context.ReadSession,
                                symbol.SymbolId,
                                new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol)),
                            readContentChunks: (symbols, excludeTests) => ReadContentChunks(
                                context.ReadSession,
                                symbols,
                                excludeTests),
                            readMany: symbols => ReferenceEvidenceReader.ReadMany(
                                context.ReadSession,
                                symbols.Select(static symbol => symbol.SymbolId).ToArray(),
                                new ReferenceEvidenceQuery(
                                    new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol))),
                            out selectedCount, out candidatesExamined,
                            cancellationToken,
                            phase => CompletePhase(
                                phase,
                                telemetry,
                                ref phaseStart,
                                context.ReadTelemetry),
                            queryRetrieval);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(reference_mode));
                }
            // The usage branch used to report its WHOLE cost under "bundle" because it had no inner phases. Now
            // that it reports them, this stamp measures only what is left after its last inner phase — a
            // different quantity under the same name, which a trend across the instrumentation boundary would
            // read as a collapse. So the usage branch's remainder gets its own key; the off branch has always
            // reported the remainder here, so its key does not move.
            CompletePhase(
                parsedReferenceMode == ReferenceMode.Usage ? "bundle_remainder" : "bundle",
                telemetry,
                ref phaseStart);
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);
            ToolDiagnostic? diagnostic = null;
            if (selectedCount == 0)
            {
                diagnostic = EmptyDiagnostic(
                    query,
                    effectiveTokenBudget,
                    candidatesExamined,
                    entry_symbols,
                    ToolOutputBudget.ContextMcpMaxTokens);
            }
            else if (IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel))
            {
                // Both reference modes read reference evidence — usage enrichment for usage, outgoing evidence
                // for off — so at symbols level the bundle silently drops it and reads as "nothing uses this".
                IndexLevelGuard.MarkDegraded(telemetry, "reference_layer_converging");
                diagnostic = IndexLevelGuard.Converging(
                    "the bundle carries no usage or outgoing-reference evidence pending identifier extraction.");
            }

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = parsedReferenceMode == ReferenceMode.Usage ? "usage" : "off";
                telemetry.SetTarget(query);
                telemetry.ResultCount = selectedCount;
                telemetry.BytesExamined = candidatesExamined;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("format", json ? "json" : "compact");
                telemetry.SetMetadata("token_budget_bucket", TokenBudgetBucket(effectiveTokenBudget));
                telemetry.SetMetadata("max_hops_bucket", HopsBucket(max_hops));
                telemetry.SetMetadata("has_entry_symbols", entry_symbols is { Length: > 0 });
                telemetry.SetMetadata("has_failing_test", !string.IsNullOrWhiteSpace(failing_test));
                telemetry.SetMetadata("has_stack_trace", !string.IsNullOrWhiteSpace(stack_trace));
                telemetry.SetMetadata("has_edited_files", edited_files is { Length: > 0 });
                telemetry.SetMetadata("semantic_seed_count", semanticSeeds.Count);
                telemetry.SetMetadata("source_rescue_seed_count", sourceSeeds.Count);
                telemetry.SetMetadata("reference_depth_bucket", HopsBucket(reference_depth));
                telemetry.SetMetadata("exclude_tests", exclude_tests);
            }
            if (diagnostic is not null)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "context",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            }
            string finalOutput = BoundFinalOutput(output, effectiveTokenBudget, json);
            CompletePhase("final_render", telemetry, ref phaseStart);
            return finalOutput;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            string output = ToolDiagnosticRenderer.Render(
                "context",
                diagnostic,
                json,
                telemetry);
            return BoundFinalOutput(
                output,
                Math.Min(token_budget, ToolOutputBudget.ContextMcpMaxTokens),
                json);
        }
    }

    private void CompletePhase(
        string phase,
        TelemetryScope? telemetry,
        ref long phaseStart,
        ReadPhaseTelemetry? readTelemetry = null)
    {
        long elapsedMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds);
        phaseStart = Stopwatch.GetTimestamp();
        telemetry?.SetMetadata("context_phase", phase);
        telemetry?.SetMetadata("context_phase_elapsed_ms", elapsedMs);
        _phaseObserver?.Invoke(phase);
        ContextLookupPhase? lookupPhase = phase switch
        {
            "source_rescue" => ContextLookupPhase.SourceRescue,
            "query_retrieval" => ContextLookupPhase.QueryRetrieval,
            "term_retrieval" => ContextLookupPhase.TermRetrieval,
            "anchor_resolution" => ContextLookupPhase.AnchorResolution,
            "graph_reach" => ContextLookupPhase.GraphReach,
            "symbol_hydration" => ContextLookupPhase.SymbolHydration,
            "file_neighbours" => ContextLookupPhase.FileNeighbours,
            "candidate_ordering" => ContextLookupPhase.CandidateOrdering,
            _ => null,
        };
        if (lookupPhase is { } completedLookupPhase && readTelemetry is not null)
        {
            ContextLookupPhaseObservation observation =
                readTelemetry.CompleteLookupPhase(completedLookupPhase);
            _lookupPhaseObserver?.Invoke(observation);
            Serilog.Log.Information(
                "Context lookup phase {ContextLookupPhase} completed with delta {@ContextLookupDelta} " +
                "and total {@ContextLookupTotal}, search delta {@ContextSearchDelta}, " +
                "search total {@ContextSearchTotal}, FTS search delta {@ContextFtsSearchDelta}, " +
                "FTS search total {@ContextFtsSearchTotal}, content FTS search delta {@ContextFtsTextSearchDelta}, " +
                "content FTS search total {@ContextFtsTextSearchTotal}, content index resolve delta " +
                "{@ContextTextContentIndexResolveDelta}, and content index resolve total " +
                "{@ContextTextContentIndexResolveTotal} for cid {CorrelationId}",
                completedLookupPhase,
                observation.Delta,
                observation.Total,
                observation.SearchDelta,
                observation.SearchTotal,
                observation.FtsSearchDelta,
                observation.FtsSearchTotal,
                observation.FtsTextSearchDelta,
                observation.FtsTextSearchTotal,
                observation.TextContentIndexResolveDelta,
                observation.TextContentIndexResolveTotal,
                telemetry?.CorrelationId ?? "unmeasured");
        }
        Serilog.Log.Information(
            "Context phase {ContextPhase} completed in {ContextPhaseElapsedMs} ms for cid {CorrelationId}",
            phase,
            elapsedMs,
            telemetry?.CorrelationId ?? "unmeasured");
    }

    /// <summary>
    /// Read outgoing evidence for a whole set of symbols in one round trip. Term-rescue promotion asks for up
    /// to eight test symbols at once; one read per symbol paid the resolution load eight times over.
    /// </summary>
    /// <remarks>
    /// It reads the OUTGOING batch, not the full bundle: this path keeps only <c>Outgoing</c>, and the bundle
    /// read adds an inbound resolution pass plus a name lookup and a same-name definition count per symbol — on
    /// the default <c>reference_mode=off</c> path, which the batch exists to make cheaper. That entry point also
    /// skips an id the read session cannot resolve instead of throwing, so one symbol the search sidecar names
    /// and the served view no longer has cannot deny the whole promotion set.
    /// <para>
    /// There is no off-switch here, and <c>MILLER_CONTEXT_REFERENCE_BATCH</c> does not reach it. That switch
    /// picks between two live implementations on the usage path; here the batch replaced its only caller, so a
    /// switch would have nothing to switch to and could only restore the N+1 this fix removed.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> ReadOutgoingBatch(
        WorkspaceReadHandle readSession,
        IReadOnlyList<string> symbolIds) =>
        ReferenceEvidenceReader.ReadOutgoingMany(
            readSession,
            symbolIds,
            new ReferenceEvidenceQuery(
                new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol)));

    private static IReadOnlyList<TextContentSearchHit> ReadContentChunks(
        WorkspaceReadHandle readSession,
        IReadOnlyList<IndexedSymbol> symbols,
        bool excludeTests) => readSession.Snapshot.Mode == WorkspaceReadMode.FamilyStore
        ? ContentCorpusContextReader.ReadContainingSymbolChunks(
            readSession.FamilyStoreRoot!,
            readSession.Snapshot,
            symbols,
            excludeTests,
            ContentChunksPerSymbol)
        : ContentCorpusContextReader.ReadContainingSymbolChunks(
            ContentCorpusSidecar.ContentDbPathFor(RequiredLegacyArtifactPath(readSession)),
            RequiredLegacyArtifactPath(readSession),
            symbols,
            excludeTests,
            ContentChunksPerSymbol);

    internal static ToolDiagnostic EmptyDiagnostic(
        string query,
        int tokenBudget,
        int candidatesExamined,
        IReadOnlyList<string>? entrySymbols,
        int maxTokenBudget)
    {
        string recoveryQuery = entrySymbols?
            .FirstOrDefault(static entry => !string.IsNullOrWhiteSpace(entry)) ?? query;
        if (candidatesExamined == 0)
        {
            return ToolDiagnostic.ExpectedEmpty(
                "no_context_symbols",
                "No context symbols matched the supplied evidence.",
                [new ToolDiagnosticAction(
                    $"search(query=\"{EscapeDiagnosticQuery(recoveryQuery)}\")",
                    "find a concrete entry symbol")]);
        }

        long requestedNextBudget = Math.Max((long)tokenBudget + 256, (long)tokenBudget * 2);
        int nextBudget = (int)Math.Min(maxTokenBudget, requestedNextBudget);
        return ToolDiagnostic.ExpectedEmpty(
            "context_budget_exhausted",
            "Context candidates matched, but none fit token_budget.",
            [
                new ToolDiagnosticAction(
                    $"context(query=\"{EscapeDiagnosticQuery(query)}\", token_budget={nextBudget})",
                    "retry with more room"),
                new ToolDiagnosticAction(
                    $"search(query=\"{EscapeDiagnosticQuery(recoveryQuery)}\", mode=\"symbol\")",
                    "narrow to one exact entry symbol"),
            ]);
    }

    private const int SearchSeedLimit = 10;
    internal const int AnchorAmbiguousMatchLimit = 10;
    internal const int AnchorIdentifierTokenLimit = 24;
    internal const int AnchorMatchesPerToken = 6;
    internal const int AnchorStackFrameLimit = 24;
    private const int NoRetrievalRank = int.MaxValue;
    private const int ReachCap = 500;
    internal const int ReferenceRowsPerSymbol = 12;
    internal const string ReferenceEvidenceBatchEnvironmentVariable = "MILLER_CONTEXT_REFERENCE_BATCH";
    internal const int ContentChunksPerSymbol = 2;
    /// <summary>Term-rescue <see cref="ContextPivotSignal.AnchorStrength"/> ceiling (below full-query affinity band).</summary>
    internal const int TermRescueStrengthCap = 18;
    /// <summary>Source/doc content rescue fixed <see cref="ContextPivotSignal.AnchorStrength"/> (discovery tier).</summary>
    internal const int SourceRescueStrength = 35;
    /// <summary>Optional semantic seed fixed <see cref="ContextPivotSignal.AnchorStrength"/> (discovery tier).</summary>
    internal const int SemanticSeedStrength = 26;
    internal const int SourceRescueHitLimit = 6;
    internal const int SourceRescueSeedLimit = 3;
    private static readonly string[] SourceRescueContentKinds =
    [
        TextContentKind.WorkspaceSource,
        TextContentKind.WorkspaceDocs,
    ];
    private const int TaskQueryNameWeight = 12;
    private const int TaskQueryPathWeight = 8;
    private const int TaskQuerySignatureWeight = 5;
    private const int TaskQueryKindWeight = 15;
    private const int TaskQueryAffinityCap = 50;
    private static readonly HashSet<string> ContextQueryStopWords = new(
        [
            "assemble", "change", "context", "current", "does", "edit", "explain", "find", "give",
            "how", "implementation", "intended", "locate", "minimal", "needed", "propose", "recognized",
            "required", "selecting", "server", "target", "then", "used", "which", "with", "without",
        ],
        StringComparer.OrdinalIgnoreCase);

    private enum ReferenceMode
    {
        Off,
        Usage,
    }

    private static bool ReferenceEvidenceBatchEnabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(ReferenceEvidenceBatchEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ReferenceMode ParseReferenceMode(string? mode) =>
        mode?.ToLowerInvariant() switch
        {
            null or "" or "off" => ReferenceMode.Off,
            "usage" => ReferenceMode.Usage,
            _ => throw new ArgumentException("reference_mode must be off or usage."),
        };

    private static string TokenBudgetBucket(int tokenBudget) => tokenBudget switch
    {
        <= 0 => "0",
        <= 1000 => "1-1000",
        <= 4000 => "1001-4000",
        <= 8000 => "4001-8000",
        _ => "8001+",
    };

    private static string HopsBucket(int hops) => hops switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        _ => "3+",
    };

    /// <param name="excludeTests">
    /// Whether a test symbol may be admitted as a seed, and the test policy of the gate's own retrieval. It is
    /// NOT the pivot ranker's policy: the two differ for an ordinary phrase query, and computing this gate's
    /// evidence over the ranker's test-hidden population changes which semantic seeds are admitted.
    /// </param>
    /// <param name="retrieval">This call's shared lexical retrieval.</param>
    private IReadOnlyList<ContextSemanticSeed> LoadSemanticSeeds(
        WorkspaceReadContext context,
        ISymbolLookupIndex index,
        string query,
        bool excludeTests,
        ContextQueryRetrieval retrieval)
    {
        if (_semanticSidecar is not { Mode: SemanticMode.On } ||
            _semanticArm is null ||
            string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        // Route first. A lexical route discards everything below, so nothing below may run: the retrieval it
        // used to run ahead of this check could not reach the output.
        if (!SemanticQueryPolicy.Route(query).IsHybrid)
            return [];

        // The gate's OWN narrow window and its OWN test policy. See SemanticSeedGateLimit: the retrieved ids
        // gate semantic admission, so this limit is a ranking input, not a performance knob.
        SymbolCandidateSet lexical = retrieval.Collect(query, SemanticSeedGateLimit, excludeTests);
        var evidence = new LexicalEvidence(
            lexical.Candidates.Count,
            lexical.Candidates.Count > 0 ? lexical.Candidates[0].Score : 0,
            lexical.Candidates.Count > 1 ? lexical.Candidates[1].Score : 0);
        SemanticCandidateAdmission admission = SemanticQueryPolicy.DecideAdmission(evidence);
        var lexicalIds = new HashSet<string>(
            lexical.Candidates.Select(static candidate => candidate.SymbolId),
            StringComparer.Ordinal);

        SemanticQueryResult result = _semanticArm.QuerySymbols(
            context.WorkspaceRoot,
            query,
            SearchSeedLimit,
            allow: null);
        if (!result.Served)
            return [];

        var seeds = new List<ContextSemanticSeed>(result.Hits.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SemanticHit hit in result.Hits.Take(SearchSeedLimit))
        {
            if (hit.SymbolId is not { } symbolId ||
                !admission.AllowsExpansion && !lexicalIds.Contains(symbolId) ||
                !seen.Add(symbolId) ||
                index.FindBySymbolId(symbolId) is not { } symbol ||
                excludeTests && (symbol.IsTest || IsTestPath.Check(symbol.FilePath)))
            {
                continue;
            }

            seeds.Add(new ContextSemanticSeed(symbol, hit.Rank, hit.Cosine));
        }
        return seeds;
    }

    private ITextContentSearchIndex? TryResolveTextContentIndex(string? workspaceId, WorkspaceRefreshMode refresh)
    {
        if (_workspaceProvider is not IWorkspaceTextContentSearchProvider textProvider)
            return null;

        try
        {
            return textProvider.ResolveTextContentSearch(workspaceId, refresh).Index;
        }
        catch (Exception)
        {
            // Fail-soft: missing/unconfigured content corpus must not break context.
            return null;
        }
    }

    /// <summary>
    /// Map a bounded source/doc content search onto unique containing symbols for NL context queries.
    /// Fail-soft: missing content index or search errors yield no seeds.
    /// </summary>
    internal static IReadOnlyList<ContextSourceSeed> LoadSourceRescueSeeds(
        ISymbolLookupIndex index,
        ITextContentSearchIndex? contentIndex,
        string query,
        bool excludeTests)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (contentIndex is null ||
            string.IsNullOrWhiteSpace(query) ||
            !IsNaturalLanguagePhrase(query))
        {
            return [];
        }

        IReadOnlyList<TextContentSearchHit> hits;
        try
        {
            hits = contentIndex.Search(query, SourceRescueContentKinds, SourceRescueHitLimit, excludeTests);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or FileNotFoundException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }

        var seeds = new List<ContextSourceSeed>(SourceRescueSeedLimit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int rank = 0;
        foreach (TextContentSearchHit hit in hits)
        {
            if (hit.ContainingSymbolId is not { Length: > 0 } symbolId ||
                index.FindBySymbolId(symbolId) is not { } symbol)
            {
                continue;
            }

            symbol = PreferDefinitionPivot(index, symbol);
            if (!IsQueryPivot(symbol) ||
                excludeTests && (symbol.IsTest || IsTestPath.Check(symbol.FilePath)) ||
                !seen.Add(symbol.SymbolId))
            {
                continue;
            }

            rank++;
            seeds.Add(new ContextSourceSeed(symbol, rank));
            if (seeds.Count >= SourceRescueSeedLimit)
                break;
        }

        return seeds;
    }

    /// <summary>
    /// A natural-language phrase is multiple whitespace-delimited words (mirrors SearchTool policy).
    /// </summary>
    private static bool IsNaturalLanguagePhrase(string query)
    {
        int words = query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 2;
    }

    /// <summary>
    /// Build task-ranked pivots, expand graph and file neighbours, attach optional implementation evidence, and
    /// pack the rendered bundle within <paramref name="tokenBudget"/>. <paramref name="selectedCount"/> is the
    /// number of rendered items; <paramref name="candidatesExamined"/> is the pre-budget candidate count.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="resolver"/> is null.</exception>
    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace, bool json,
        out int selectedCount, out int candidatesExamined)
    {
        ArgumentNullException.ThrowIfNull(index);
        return Run(index, index.Graph, resolver, query, tokenBudget, maxHops,
            entrySymbols, failingTest, stackTrace, json, out selectedCount, out candidatesExamined);
    }

    public static string Run(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace, bool json,
        out int selectedCount, out int candidatesExamined)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        return RunActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles: null,
            failingTest,
            stackTrace,
            semanticSeeds: null,
            sourceSeeds: null,
            readBody: null,
            json,
            out selectedCount,
            out candidatesExamined);
    }

    internal static string RunActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        bool json,
        out int selectedCount,
        out int candidatesExamined) =>
        RunActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds: null,
            readBody,
            json,
            out selectedCount,
            out candidatesExamined);

    internal static string RunActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        bool json,
        out int selectedCount,
        out int candidatesExamined) =>
        RunActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readBody,
            readOutgoingMany: null,
            json,
            out selectedCount,
            out candidatesExamined);

    internal static string RunActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? readOutgoingMany,
        bool json,
        out int selectedCount,
        out int candidatesExamined) =>
        RunActionableWithCancellation(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readBody,
            readOutgoingMany,
            json,
            out selectedCount,
            out candidatesExamined,
            CancellationToken.None);

    internal static string RunActionableWithCancellation(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? readOutgoingMany,
        bool json,
        out int selectedCount,
        out int candidatesExamined,
        CancellationToken cancellationToken,
        Action<string>? phaseObserver = null,
        ContextQueryRetrieval? retrieval = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readOutgoingMany,
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out candidatesExamined,
            cancellationToken,
            phaseObserver,
            retrieval);
        phaseObserver?.Invoke("candidate_build");
        candidates = AttachPivotBodies(candidates, tokenBudget, readBody, cancellationToken);
        phaseObserver?.Invoke("pivot_bodies");

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(anchorDiagnostics, tokenBudget, json);
        }

        var packCandidates = new List<PackCandidate<Candidate>>(candidates.Count);
        foreach (var c in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cost = (int)TokenEstimator.Count(CompactCostLine(c));
            packCandidates.Add(new PackCandidate<Candidate>(
                c,
                cost,
                AllocationTier: c.IsPivot ? 0 : 2));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Candidate> selected = ContextPacker.PackAllocated(packCandidates, tokenBudget);
        phaseObserver?.Invoke("candidate_pack");
        Func<IReadOnlyList<Candidate>, string> renderer = json
            ? selected => RenderJson(selected, anchorDiagnostics, query, boundOptionalFields: false)
            : selected => RenderCompact(selected, anchorDiagnostics, query);
        Func<IReadOnlyList<Candidate>, string> boundedRenderer = json
            ? selected => RenderJson(selected, anchorDiagnostics, query, boundOptionalFields: true)
            : selected => RenderCompact(selected, anchorDiagnostics, query);
        string output = RenderWithinBudget(
            selected,
            tokenBudget,
            renderer,
            boundedRenderer,
            out selectedCount,
            cancellationToken);
        phaseObserver?.Invoke("bounded_render");
        return output;
    }


    internal static string RunReferenceAware(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        int referenceDepth, bool excludeTests, bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount, out int candidatesExamined)
        => RunReferenceAwareActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles: null,
            failingTest,
            stackTrace,
            semanticSeeds: null,
            sourceSeeds: null,
            readBody: null,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            out selectedCount,
            out candidatesExamined);

    internal static string RunReferenceAware(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        int referenceDepth, bool excludeTests, bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>> readMany,
        out int selectedCount, out int candidatesExamined)
        => RunReferenceAwareActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles: null,
            failingTest,
            stackTrace,
            semanticSeeds: null,
            sourceSeeds: null,
            readBody: null,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany,
            out selectedCount,
            out candidatesExamined);

    internal static string RunReferenceAwareActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount,
        out int candidatesExamined) =>
        RunReferenceAwareActionable(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds: null,
            readBody,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            out selectedCount,
            out candidatesExamined);

    internal static string RunReferenceAwareActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount,
        out int candidatesExamined) =>
        RunReferenceAwareActionableWithCancellation(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readBody,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            out selectedCount,
            out candidatesExamined,
            CancellationToken.None);

    internal static string RunReferenceAwareActionable(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>> readMany,
        out int selectedCount,
        out int candidatesExamined) =>
        RunReferenceAwareActionableWithCancellation(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readBody,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany,
            out selectedCount,
            out candidatesExamined,
            CancellationToken.None);

    internal static string RunReferenceAwareActionableWithCancellation(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount,
        out int candidatesExamined,
        CancellationToken cancellationToken) =>
        RunReferenceAwareActionableWithCancellation(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readBody,
            referenceDepth,
            excludeTests,
            json,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany: null,
            out selectedCount,
            out candidatesExamined,
            cancellationToken);

    internal static string RunReferenceAwareActionableWithCancellation(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int tokenBudget,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        out int selectedCount,
        out int candidatesExamined,
        CancellationToken cancellationToken,
        Action<string>? phaseObserver = null,
        ContextQueryRetrieval? retrieval = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(readReferenceEvidence);
        ArgumentNullException.ThrowIfNull(readOutgoingEvidence);
        ArgumentNullException.ThrowIfNull(readContentChunks);

        if (referenceDepth < 0) referenceDepth = 0;
        if (referenceDepth > 1) referenceDepth = 1;

        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readOutgoingMany: symbolIds => ReadOutgoingForSymbols(
                index,
                symbolIds,
                readOutgoingEvidence,
                readMany,
                cancellationToken),
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out candidatesExamined,
            cancellationToken,
            phaseObserver,
            retrieval);
        phaseObserver?.Invoke("candidate_build");

        candidates = AttachPivotBodies(candidates, tokenBudget, readBody, cancellationToken);
        phaseObserver?.Invoke("pivot_bodies");

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(anchorDiagnostics, tokenBudget, json);
        }

        IReadOnlyList<ReferenceContextItem> items = BuildReferenceItems(
            candidates,
            tokenBudget,
            referenceDepth,
            excludeTests,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany,
            json,
            anchorDiagnostics,
            query,
            cancellationToken);
        phaseObserver?.Invoke("reference_items");
        var packCandidates = new List<PackCandidate<ReferenceContextItem>>(items.Count);
        foreach (ReferenceContextItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packCandidates.Add(new PackCandidate<ReferenceContextItem>(
                item,
                (int)TokenEstimator.Count(ReferenceCostLine(item)),
                AllocationTier: ReferenceAllocationTier(item)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReferenceContextItem> selected =
            ContextPacker.PackAllocated(packCandidates, tokenBudget);
        phaseObserver?.Invoke("candidate_pack");
        Func<IReadOnlyList<ReferenceContextItem>, string> renderer = json
            ? selected => RenderReferenceJson(selected, anchorDiagnostics, query, boundOptionalFields: false)
            : selected => RenderReferenceCompact(selected, anchorDiagnostics, query);
        Func<IReadOnlyList<ReferenceContextItem>, string> boundedRenderer = json
            ? selected => RenderReferenceJson(selected, anchorDiagnostics, query, boundOptionalFields: true)
            : selected => RenderReferenceCompact(selected, anchorDiagnostics, query);
        string output = RenderWithinBudget(
            selected,
            tokenBudget,
            renderer,
            boundedRenderer,
            out selectedCount,
            cancellationToken);
        phaseObserver?.Invoke("bounded_render");
        return output;
    }

    /// <summary>
    /// Outgoing evidence for a set of symbols on the reference-aware path: one batched read where the caller
    /// supplied one AND the batch opt-in is on, otherwise the per-symbol reader it already carries. The gate is
    /// the same one <see cref="BuildReferenceItems"/> reads, so one switch still decides whether this path
    /// batches. Ids the index cannot resolve are absent from the result, which reads the same as the empty
    /// evidence set the caller used to synthesize.
    /// </summary>
    private static IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> ReadOutgoingForSymbols(
        ISymbolLookupIndex index,
        IReadOnlyList<string> symbolIds,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        CancellationToken cancellationToken)
    {
        var symbols = new List<IndexedSymbol>(symbolIds.Count);
        foreach (string symbolId in symbolIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.FindBySymbolId(symbolId) is { } symbol)
                symbols.Add(symbol);
        }

        var outgoing = new Dictionary<string, OutgoingReferenceEvidenceSet>(
            symbols.Count,
            StringComparer.Ordinal);
        if (symbols.Count == 0)
            return outgoing;

        if (readMany is not null && ReferenceEvidenceBatchEnabled)
        {
            foreach ((string symbolId, ReferenceEvidenceBundle bundle) in readMany(symbols))
                outgoing[symbolId] = bundle.Outgoing;
            return outgoing;
        }

        foreach (IndexedSymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outgoing[symbol.SymbolId] = readOutgoingEvidence(symbol);
        }
        return outgoing;
    }

    private static string RenderWithinBudget<T>(
        IReadOnlyList<T> initiallySelected,
        int tokenBudget,
        Func<IReadOnlyList<T>, string> renderer,
        Func<IReadOnlyList<T>, string> boundedRenderer,
        out int selectedCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<T> empty = Array.Empty<T>();
        string emptyOutput = renderer(empty);
        if (tokenBudget <= 0)
        {
            selectedCount = 0;
            return string.Empty;
        }
        int renderBudget = tokenBudget >= 512
            ? Math.Max(1, tokenBudget * 3 / 4)
            : tokenBudget;
        if (TokenEstimator.Count(emptyOutput) > renderBudget)
        {
            selectedCount = 0;
            return emptyOutput.StartsWith('{') && TokenEstimator.Count("{}") <= renderBudget
                ? "{}"
                : string.Empty;
        }

        string fullOutput = renderer(initiallySelected);
        if (TokenEstimator.Count(fullOutput) <= renderBudget)
        {
            selectedCount = initiallySelected.Count;
            return fullOutput;
        }

        T[] retained = initiallySelected.ToArray();
        int lowestCandidateCount = 1;
        int highestCandidateCount = retained.Length;
        int bestCount = 0;
        string bestOutput = emptyOutput;
        while (lowestCandidateCount <= highestCandidateCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int candidateCount = lowestCandidateCount + ((highestCandidateCount - lowestCandidateCount) / 2);
            var prefix = new ArraySegment<T>(retained, 0, candidateCount);
            string output = boundedRenderer(prefix);
            if (TokenEstimator.Count(output) <= renderBudget)
            {
                bestCount = candidateCount;
                bestOutput = output;
                lowestCandidateCount = candidateCount + 1;
            }
            else
            {
                highestCandidateCount = candidateCount - 1;
            }
        }

        selectedCount = bestCount;
        return bestOutput;
    }

    private static string BoundFinalOutput(string output, int tokenBudget, bool json)
    {
        if (tokenBudget <= 0)
            return string.Empty;
        if (TokenEstimator.Count(output) <= tokenBudget)
            return output;
        if (json)
            return TokenEstimator.Count("{}") <= tokenBudget ? "{}" : string.Empty;

        int lineEnd = output.LastIndexOf('\n');
        while (lineEnd >= 0)
        {
            string prefix = output[..lineEnd];
            if (TokenEstimator.Count(prefix) <= tokenBudget)
                return prefix;
            lineEnd = output.LastIndexOf('\n', lineEnd - 1);
        }
        return TokenEstimator.Count("…") <= tokenBudget ? "…" : string.Empty;
    }

    private static string RenderNoPivots(
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        int tokenBudget,
        bool json)
    {
        string output;
        if (!json)
        {
            var builder = new StringBuilder("No pivots — nothing to anchor on.");
            if (anchorDiagnostics.Count > 0)
                builder.Append('\n');
            AppendAnchorDiagnosticsCompact(builder, anchorDiagnostics);
            output = builder.ToString().TrimEnd('\n');
        }
        else
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(
                buffer,
                new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("note", "no pivots — nothing to anchor on.");
                writer.WritePropertyName("bundle");
                writer.WriteStartArray();
                writer.WriteEndArray();
                WriteAnchorDiagnosticsJson(writer, anchorDiagnostics);
                WriteDispositionJson(
                    writer,
                    new ContextEvidenceDisposition("insufficient", "no_pivot_resolved"));
                writer.WriteEndObject();
            }
            output = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        return BoundFinalOutput(output, tokenBudget, json);
    }

    /// <summary>One selected symbol, its graph distance, and the evidence that made it a pivot.</summary>
    private readonly record struct Candidate(
        IndexedSymbol Symbol,
        int Hop,
        string Reason = "graph_neighbor",
        bool IsPivot = false,
        int? AnchorLine = null,
        string? Body = null,
        bool BodyTruncated = false,
        string? BodyUnavailableReason = null);

    internal sealed record ContextAnchorDiagnostic(string Kind, string Value, string Reason);

    internal sealed record ContextSemanticSeed(IndexedSymbol Symbol, int Rank, double Score);

    internal sealed record ContextSourceSeed(IndexedSymbol Symbol, int Rank);

    private sealed record ContextEvidenceDisposition(string Status, string Reason);

    private sealed record ContextNextAction(string Call, string Reason);

    private sealed record ReferenceContextItem(
        string ItemType,
        string Reason,
        string Confidence,
        string Name,
        string Kind,
        string File,
        int Line,
        int? Hop = null,
        string? Signature = null,
        string? SymbolId = null,
        string? ContainingSymbolId = null,
        string? SourceId = null,
        string? ChunkId = null,
        int? LineStart = null,
        int? LineEnd = null,
        string? Snippet = null,
        string? TargetSymbolId = null,
        string? ResolutionStatus = null,
        string? Provenance = null,
        double? EvidenceConfidence = null,
        string? AnchorReason = null,
        string? Role = null);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int maxHops, IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles: null,
            failingTest,
            stackTrace,
            semanticSeeds: null,
            sourceSeeds: null,
            readOutgoingMany: null,
            out _,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds: null,
            readOutgoingMany: null,
            out anchorDiagnostics,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined) =>
        BuildCandidates(
            index,
            graph,
            resolver,
            query,
            maxHops,
            entrySymbols,
            editedFiles,
            failingTest,
            stackTrace,
            semanticSeeds,
            sourceSeeds,
            readOutgoingMany: null,
            out anchorDiagnostics,
            out candidatesExamined);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        SmartTargetResolver resolver,
        string query,
        int maxHops,
        IReadOnlyList<string>? entrySymbols,
        IReadOnlyList<string>? editedFiles,
        string? failingTest,
        string? stackTrace,
        IReadOnlyList<ContextSemanticSeed>? semanticSeeds,
        IReadOnlyList<ContextSourceSeed>? sourceSeeds,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>>? readOutgoingMany,
        out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        out int candidatesExamined,
        CancellationToken cancellationToken = default,
        Action<string>? phaseObserver = null,
        ContextQueryRetrieval? retrieval = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        retrieval = ContextQueryRetrieval.For(index, retrieval);
        if (maxHops < 0) maxHops = 0;
        if (maxHops > 2) maxHops = 2;
        candidatesExamined = 0;
        var diagnostics = new List<ContextAnchorDiagnostic>();
        var signals = new List<ContextPivotSignal>();
        var symbols = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, (int Strength, int Order, string Reason, int? Line)>(
            StringComparer.Ordinal);
        int anchorOrder = 0;
        IReadOnlyList<string> queryTerms = ContextQueryTerms(query);

        void AddSignal(
            IndexedSymbol symbol,
            int retrievalRank,
            double retrievalScore,
            int anchorStrength,
            string reason,
            int? anchorLine = null,
            int? lineDistance = null,
            bool pinned = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int order = anchorOrder++;
            symbols[symbol.SymbolId] = symbol;
            signals.Add(new ContextPivotSignal(
                symbol.SymbolId,
                retrievalRank,
                retrievalScore,
                anchorStrength,
                order,
                lineDistance,
                ContextPivotDiversityKey(symbol.Name),
                symbol.FilePath,
                symbol.IsTest || IsTestPath.Check(symbol.FilePath),
                pinned));
            if (!reasons.TryGetValue(symbol.SymbolId, out var existing) ||
                anchorStrength > existing.Strength ||
                anchorStrength == existing.Strength && order < existing.Order)
            {
                reasons[symbol.SymbolId] = (anchorStrength, order, reason, anchorLine);
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Parent-query auto policy for both arms: one-word term rescue must not reintroduce
            // tests when the original natural-language query would hide them.
            bool excludeTests = SearchTool.ResolveExcludeTests(null, query, SearchToolMode.Symbol);
            SymbolCandidateSet retrieved = retrieval.Collect(query, SearchSeedLimit, excludeTests);
            cancellationToken.ThrowIfCancellationRequested();
            int retrievedCount = Math.Min(retrieved.Candidates.Count, SearchSeedLimit);
            for (int rank = 0; rank < retrievedCount; rank++)
            {
                SymbolCandidate candidate = retrieved.Candidates[rank];
                if (index.FindBySymbolId(candidate.SymbolId) is { } symbol && IsQueryPivot(symbol))
                {
                    symbol = PreferDefinitionPivot(index, symbol);
                    AddSignal(
                        symbol,
                        rank + 1,
                        candidate.Score,
                        TaskQueryAffinity(symbol, queryTerms),
                        $"query_rank_{rank + 1}");
                }
            }
            phaseObserver?.Invoke("query_retrieval");

            foreach (string term in queryTerms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolCandidateSet termCandidates =
                    retrieval.Collect(term, TermRescueRetrievalLimit, excludeTests);
                cancellationToken.ThrowIfCancellationRequested();
                int termCandidateCount = Math.Min(termCandidates.Candidates.Count, TermRescueRetrievalLimit);
                for (int rank = 0; rank < termCandidateCount; rank++)
                {
                    SymbolCandidate candidate = termCandidates.Candidates[rank];
                    if (index.FindBySymbolId(candidate.SymbolId) is { } symbol && IsQueryPivot(symbol))
                    {
                        symbol = PreferDefinitionPivot(index, symbol);
                        AddSignal(
                            symbol,
                            rank + 1,
                            candidate.Score,
                            Math.Min(TaskQueryAffinity(symbol, queryTerms), TermRescueStrengthCap),
                            $"query_term_{term}");
                    }
                }
            }
            phaseObserver?.Invoke("term_retrieval");

            if (readOutgoingMany is not null && !HasTestOrDefIntent(query))
            {
                PromoteTermRescueTestSubjects(
                    index,
                    queryTerms,
                    excludeTests,
                    readOutgoingMany,
                    retrieval,
                    signals,
                    symbols,
                    reasons,
                    (symbol, rank, score, strength, reason) =>
                        AddSignal(symbol, rank, score, strength, reason),
                    cancellationToken);
            }
        }

        if (entrySymbols is not null)
        {
            foreach (string entry in entrySymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                switch (resolver.Resolve(entry))
                {
                    case TargetResolution.Symbol resolved:
                        AddSignal(
                            resolved.Value,
                            NoRetrievalRank,
                            0,
                            100,
                            "entry_symbol",
                            pinned: true);
                        break;
                    case TargetResolution.Candidates ambiguous:
                        IndexedSymbol[] ambiguousMatches = ambiguous.Matches
                            .Take(AnchorAmbiguousMatchLimit + 1)
                            .ToArray();
                        diagnostics.Add(new ContextAnchorDiagnostic(
                            "entry_symbol",
                            entry,
                            ambiguousMatches.Length > AnchorAmbiguousMatchLimit
                                ? "ambiguous_truncated"
                                : "ambiguous"));
                        foreach (IndexedSymbol match in ambiguousMatches.Take(AnchorAmbiguousMatchLimit))
                            AddSignal(match, NoRetrievalRank, 0, 70, "ambiguous_entry_symbol");
                        break;
                    case TargetResolution.File file:
                        foreach (IndexedSymbol match in index.FindByFilePath(file.Path)
                                     .Where(static symbol => IsFilePivotKind(symbol.Kind))
                                     .Take(SearchSeedLimit))
                        {
                            AddSignal(match, NoRetrievalRank, 0, 65, "entry_file");
                        }
                        break;
                    case TargetResolution.NotFound:
                        diagnostics.Add(new ContextAnchorDiagnostic("entry_symbol", entry, "not_found"));
                        break;
                }
            }
        }

        if (editedFiles is not null)
        {
            foreach (string editedFile in editedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(editedFile))
                    continue;
                string? resolvedPath = index.ResolveIndexedFilePath(editedFile);
                IReadOnlyList<IndexedSymbol> matches = resolvedPath is null
                    ? index.FindByFilePathFragment(editedFile, SearchSeedLimit)
                    : index.FindByFilePath(resolvedPath);
                IndexedSymbol[] actionable = matches
                    .Where(static symbol => IsFilePivotKind(symbol.Kind))
                    .Take(SearchSeedLimit)
                    .ToArray();
                if (actionable.Length == 0)
                {
                    diagnostics.Add(new ContextAnchorDiagnostic("edited_file", editedFile, "not_indexed"));
                    continue;
                }
                foreach (IndexedSymbol match in actionable)
                    AddSignal(match, NoRetrievalRank, 0, 85, "edited_file");
            }
        }

        IReadOnlyList<IndexedSymbol> failingTestMatches =
            FindNamedAnchorCandidates(index, failingTest, out bool failingTestTruncated);
        bool failingTestMatched = failingTestMatches.Count > 0;
        foreach (IndexedSymbol match in failingTestMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSignal(match, NoRetrievalRank, 0, 80, "failing_test");
        }
        if (failingTestTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "failing_test",
                failingTest!,
                failingTestMatched ? "truncated" : "no_symbol_match_truncated"));
        }
        else if (!string.IsNullOrWhiteSpace(failingTest) && !failingTestMatched)
        {
            diagnostics.Add(new ContextAnchorDiagnostic("failing_test", failingTest, "no_symbol_match"));
        }

        bool stackMatched = false;
        IReadOnlyList<(string File, int Line)> stackFrames =
            ParseStackFrames(stackTrace, out bool stackFramesTruncated);
        foreach ((string file, int line) in stackFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? resolvedPath = index.ResolveIndexedFilePath(file);
            IReadOnlyList<IndexedSymbol> matches = resolvedPath is null
                ? index.FindByFilePathFragment(file, SearchSeedLimit)
                : index.FindByFilePath(resolvedPath);
            foreach (IndexedSymbol match in matches
                         .Where(static symbol => IsQueryPivot(symbol))
                         .OrderBy(symbol => LineDistance(symbol, line))
                         .Take(2))
            {
                stackMatched = true;
                AddSignal(
                    match,
                    NoRetrievalRank,
                    0,
                    95,
                    "stack_frame",
                    anchorLine: line,
                    lineDistance: LineDistance(match, line));
            }
        }
        IReadOnlyList<IndexedSymbol> stackSymbolMatches =
            FindNamedAnchorCandidates(index, stackTrace, out bool stackSymbolsTruncated);
        foreach (IndexedSymbol match in stackSymbolMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stackMatched = true;
            AddSignal(match, NoRetrievalRank, 0, 90, "stack_symbol");
        }
        if (stackFramesTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "stack_trace",
                stackTrace!,
                stackMatched ? "frames_truncated" : "no_frame_match_truncated"));
        }
        if (stackSymbolsTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic(
                "stack_trace",
                stackTrace!,
                stackMatched ? "symbols_truncated" : "no_symbol_match_truncated"));
        }
        if (!string.IsNullOrWhiteSpace(stackTrace) &&
            !stackMatched &&
            !stackFramesTruncated &&
            !stackSymbolsTruncated)
        {
            diagnostics.Add(new ContextAnchorDiagnostic("stack_trace", stackTrace, "no_frame_match"));
        }

        if (sourceSeeds is not null)
        {
            foreach (ContextSourceSeed seed in sourceSeeds.Where(static seed => IsQueryPivot(seed.Symbol)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedSymbol symbol = PreferDefinitionPivot(index, seed.Symbol);
                AddSignal(
                    symbol,
                    SearchSeedLimit + seed.Rank,
                    retrievalScore: 0,
                    SourceRescueStrength,
                    $"source_rescue_{seed.Rank}");
            }
        }

        if (semanticSeeds is not null)
        {
            foreach (ContextSemanticSeed seed in semanticSeeds.Where(static seed => IsQueryPivot(seed.Symbol)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedSymbol symbol = PreferDefinitionPivot(index, seed.Symbol);
                AddSignal(
                    symbol,
                    SearchSeedLimit + seed.Rank,
                    seed.Score,
                    SemanticSeedStrength,
                    $"semantic_rank_{seed.Rank}");
            }
        }
        phaseObserver?.Invoke("anchor_resolution");

        anchorDiagnostics = diagnostics;
        IReadOnlyList<ContextPivot> pivots = ContextPivotRanker.Rank(signals, limit: 4);
        phaseObserver?.Invoke("pivot_ranking");
        if (pivots.Count == 0)
            return Array.Empty<Candidate>();

        string[] pivotIds = pivots.Select(static pivot => pivot.SymbolId).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReachedNode> reached = graph.Reach(pivotIds, maxHops, ReachCap, Direction.Both);
        phaseObserver?.Invoke("graph_reach");
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new List<Candidate>(pivotIds.Length + reached.Count);
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(
            index,
            pivotIds.Concat(reached.Select(static node => node.Id)));
        phaseObserver?.Invoke("symbol_hydration");

        var pivotSymbols = new List<IndexedSymbol>(pivotIds.Length);
        foreach (string pivotId in pivotIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbolsById.TryGetValue(pivotId, out IndexedSymbol? symbol))
            {
                pivotSymbols.Add(symbol);
                var reason = reasons[pivotId];
                candidates.Add(new Candidate(
                    symbol,
                    Hop: 0,
                    Reason: reason.Reason,
                    IsPivot: true,
                    AnchorLine: reason.Line));
            }
        }

        NeighbourRelevanceScorer scorer = NeighbourRelevanceScorer.Build(query, pivotSymbols);
        var seenNeighbourIds = new HashSet<string>(pivotIds, StringComparer.Ordinal);
        var scoredReached = new List<(IndexedSymbol Symbol, int Hop, int Score, string Reason)>(
            reached.Count + (pivotSymbols.Count * 2));
        foreach (ReachedNode node in reached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenNeighbourIds.Add(node.Id) &&
                symbolsById.TryGetValue(node.Id, out IndexedSymbol? symbol))
            {
                scoredReached.Add((symbol, node.Hop, scorer.Score(symbol), "graph_neighbor"));
            }
        }
        if (maxHops > 0)
        {
            foreach (IndexedSymbol pivot in pivotSymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int added = 0;
                foreach (IndexedSymbol symbol in index.FindByFilePath(pivot.FilePath)
                             .Where(static symbol => IsQueryPivot(symbol))
                             .OrderByDescending(symbol => TaskQueryAffinity(symbol, queryTerms))
                             .ThenBy(symbol => LineDistance(symbol, pivot.StartLine))
                             .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenNeighbourIds.Add(symbol.SymbolId))
                        continue;
                    scoredReached.Add((symbol, 1, scorer.Score(symbol), "file_neighbour"));
                    added++;
                    if (added == 2)
                        break;
                }
            }
        }
        phaseObserver?.Invoke("file_neighbours");
        foreach (var entry in scoredReached
                     .OrderBy(static candidate => candidate.Hop)
                     .ThenByDescending(static candidate => candidate.Score)
                     .ThenBy(static candidate => candidate.Symbol.SymbolId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.Add(new Candidate(entry.Symbol, entry.Hop, entry.Reason));
        }
        phaseObserver?.Invoke("candidate_ordering");

        candidatesExamined = candidates.Count;
        return candidates;
    }

    /// <summary>
    /// When term rescue surfaces a test on a non-test-intent query, replace it with its sole
    /// exact non-test outgoing subject (discovery-tier <c>query_term_*_subject</c>).
    /// </summary>
    private static void PromoteTermRescueTestSubjects(
        ISymbolLookupIndex index,
        IReadOnlyList<string> queryTerms,
        bool excludeTests,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet>> readOutgoingMany,
        ContextQueryRetrieval retrieval,
        List<ContextPivotSignal> signals,
        Dictionary<string, IndexedSymbol> symbols,
        Dictionary<string, (int Strength, int Order, string Reason, int? Line)> reasons,
        Action<IndexedSymbol, int, double, int, string> addSignal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new Dictionary<string, TermRescueTestHit>(StringComparer.Ordinal);

        void Consider(
            IndexedSymbol testSymbol,
            string term,
            int retrievalRank,
            double retrievalScore,
            int strength)
        {
            if (!(testSymbol.IsTest || IsTestPath.Check(testSymbol.FilePath)))
                return;
            if (!IsQueryPivot(testSymbol))
                return;

            if (hits.TryGetValue(testSymbol.SymbolId, out TermRescueTestHit existing))
            {
                if (strength > existing.Strength ||
                    strength == existing.Strength && retrievalRank < existing.RetrievalRank)
                {
                    hits[testSymbol.SymbolId] = existing with
                    {
                        Term = term,
                        RetrievalRank = retrievalRank,
                        RetrievalScore = retrievalScore,
                        Strength = strength,
                    };
                }

                return;
            }

            hits[testSymbol.SymbolId] = new TermRescueTestHit(
                testSymbol,
                term,
                retrievalRank,
                retrievalScore,
                strength);
        }

        foreach ((string symbolId, (int Strength, int Order, string Reason, int? Line) reason) in reasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reason.Reason.StartsWith("query_term_", StringComparison.Ordinal) ||
                reason.Reason.EndsWith("_subject", StringComparison.Ordinal) ||
                !symbols.TryGetValue(symbolId, out IndexedSymbol? symbol))
            {
                continue;
            }

            string term = reason.Reason["query_term_".Length..];
            ContextPivotSignal? signal = null;
            foreach (ContextPivotSignal candidate in signals)
            {
                if (candidate.SymbolId == symbolId &&
                    candidate.AnchorStrength == reason.Strength)
                {
                    signal = candidate;
                    break;
                }
            }

            Consider(
                symbol,
                term,
                signal?.RetrievalRank ?? NoRetrievalRank,
                signal?.RetrievalScore ?? 0,
                reason.Strength);
        }

        if (excludeTests)
        {
            foreach (string term in queryTerms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SymbolCandidateSet termCandidates = retrieval.Collect(
                    term,
                    TermRescueRetrievalLimit,
                    excludeTests: false);
                int termCandidateCount = Math.Min(termCandidates.Candidates.Count, TermRescueRetrievalLimit);
                for (int rank = 0; rank < termCandidateCount; rank++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SymbolCandidate candidate = termCandidates.Candidates[rank];
                    if (index.FindBySymbolId(candidate.SymbolId) is not { } symbol)
                        continue;
                    symbol = PreferDefinitionPivot(index, symbol);
                    if (!(symbol.IsTest || IsTestPath.Check(symbol.FilePath)))
                        continue;
                    Consider(
                        symbol,
                        term,
                        rank + 1,
                        candidate.Score,
                        Math.Min(TaskQueryAffinity(symbol, queryTerms), TermRescueStrengthCap));
                }
            }
        }

        TermRescueTestHit[] promotions = hits.Values
            .OrderByDescending(static hit => hit.Strength)
            .ThenBy(static hit => hit.RetrievalRank)
            .ThenBy(static hit => hit.Test.FilePath, StringComparer.Ordinal)
            .ThenBy(static hit => hit.Test.StartLine)
            .ThenBy(static hit => hit.Test.SymbolId, StringComparer.Ordinal)
            .Take(TermRescuePromotionReadLimit)
            .ToArray();
        if (promotions.Length == 0)
            return;

        // One read for the whole promotion set. Per-symbol reads paid the reference-resolution load once per
        // promoted test, and the first of them pulled it in even under reference_mode=off.
        IReadOnlyDictionary<string, OutgoingReferenceEvidenceSet> outgoingBySymbol;
        try
        {
            outgoingBySymbol = readOutgoingMany(
                Array.ConvertAll(promotions, static hit => hit.Test.SymbolId));
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException
                or Microsoft.Data.Sqlite.SqliteException)
        {
            // The batch is one read: a failure denies every promotion, as a per-symbol failure denied one.
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (TermRescueTestHit hit in promotions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!outgoingBySymbol.TryGetValue(hit.Test.SymbolId, out OutgoingReferenceEvidenceSet? outgoing))
                continue;

            if (outgoing.Coverage.ExactTruncated)
                continue;

            var subjectIds = new HashSet<string>(StringComparer.Ordinal);
            IndexedSymbol? soleSubject = null;
            foreach (OutgoingReferenceEvidence edge in outgoing.Exact)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(edge.TargetSymbolId) || !edge.IsExact)
                    continue;
                if (index.FindBySymbolId(edge.TargetSymbolId) is not { } target)
                    continue;
                if (target.IsTest || IsTestPath.Check(target.FilePath))
                    continue;
                if (!subjectIds.Add(target.SymbolId))
                    continue;
                if (subjectIds.Count > 1)
                {
                    soleSubject = null;
                    break;
                }

                soleSubject = target;
            }

            if (soleSubject is null || subjectIds.Count != 1)
                continue;

            IndexedSymbol subject = PreferContainerSubject(index, soleSubject);
            subject = PreferDefinitionPivot(index, subject);
            if (!IsQueryPivot(subject) || subject.IsTest || IsTestPath.Check(subject.FilePath))
                continue;

            signals.RemoveAll(signal => signal.SymbolId == hit.Test.SymbolId);
            symbols.Remove(hit.Test.SymbolId);
            reasons.Remove(hit.Test.SymbolId);

            addSignal(
                subject,
                hit.RetrievalRank,
                hit.RetrievalScore,
                hit.Strength,
                $"query_term_{hit.Term}_subject");
        }
    }

    private static IndexedSymbol PreferContainerSubject(
        ISymbolLookupIndex index,
        IndexedSymbol subject)
    {
        if (!IsMemberKind(subject.Kind) ||
            string.IsNullOrEmpty(subject.ParentId) ||
            index.FindBySymbolId(subject.ParentId) is not { } parent ||
            parent.IsTest ||
            IsTestPath.Check(parent.FilePath) ||
            !IsContainerKind(parent.Kind) ||
            !IsQueryPivot(parent))
        {
            return subject;
        }

        return parent;
    }

    private static bool IsMemberKind(string kind) =>
        kind is "method" or "function" or "property" or "field" or "constructor" or "constant";

    private static bool IsContainerKind(string kind) =>
        kind is "class" or "struct" or "interface" or "enum" or "record" or "type" or "module" or
            "namespace" or "trait" or "impl" or "object";

    /// <summary>Mirrors SearchTool test/def intent: whole words test/tests/spec/specs.</summary>
    private static bool HasTestOrDefIntent(string query)
    {
        foreach (string word in query.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("specs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct TermRescueTestHit(
        IndexedSymbol Test,
        string Term,
        int RetrievalRank,
        double RetrievalScore,
        int Strength);

    private static bool IsPivotKind(string kind) =>
        kind is not (
            "variable" or "key" or "heading" or "section" or "document" or "table" or "list_item");

    private static bool IsFilePivotKind(string kind) =>
        IsPivotKind(kind) &&
        kind is not (
            "import" or "module" or "export" or "field" or "property" or "constant" or "parameter" or
            "constructor");

    private static bool IsQueryPivot(IndexedSymbol symbol) =>
        IsPivotKind(symbol.Kind) &&
        symbol.Kind is not ("import" or "module" or "field" or "parameter" or "constructor") &&
        !symbol.FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        !symbol.FilePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) &&
        !symbol.FilePath.EndsWith(".rst", StringComparison.OrdinalIgnoreCase);

    private static IndexedSymbol PreferDefinitionPivot(
        ISymbolLookupIndex index,
        IndexedSymbol symbol)
    {
        if (symbol.Kind != "export")
            return symbol;

        return index.FindByName(symbol.Name)
            .Where(candidate =>
                candidate.SymbolId != symbol.SymbolId &&
                candidate.Kind != "export" &&
                IsQueryPivot(candidate) &&
                candidate.StartLine == symbol.StartLine &&
                string.Equals(candidate.FilePath, symbol.FilePath, StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.SymbolId, StringComparer.Ordinal)
            .FirstOrDefault() ?? symbol;
    }

    private static IReadOnlyList<string> ContextQueryTerms(string? query)
    {
        var terms = new List<string>(12);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in ExtractIdentifierTokens(query))
        {
            string normalized = token.ToLowerInvariant();
            if (normalized.Length < 4 || ContextQueryStopWords.Contains(normalized))
                continue;
            Add(normalized);
            if (char.IsLower(token[0]))
                Add(ContextQueryStem(normalized));
            if (terms.Count >= 12)
                break;
        }
        return terms;

        void Add(string term)
        {
            if (term.Length >= 3 && seen.Add(term))
                terms.Add(term);
        }
    }

    private static string ContextQueryStem(string term)
    {
        if (term.EndsWith("nner", StringComparison.Ordinal) && term.Length > 5)
            return term[..^3];
        if (term.EndsWith("ing", StringComparison.Ordinal) && term.Length > 6)
            return term[..^3];
        if (term.EndsWith("ed", StringComparison.Ordinal) && term.Length > 5)
            return term[..^2];
        if (term.EndsWith('s') && term.Length > 4)
            return term[..^1];
        return term;
    }

    /// <summary>
    /// Lexical affinity of a symbol to task-query terms. Name weight is at least path weight so
    /// path-token matches cannot outrank pure name matches for the same term set.
    /// </summary>
    internal static int TaskQueryAffinity(IndexedSymbol symbol, IReadOnlyList<string> terms)
    {
        var nameTokens = new List<string>();
        var signatureTokens = new List<string>();
        var pathTokens = new List<string>();
        CodeTokenizer.Tokenize(symbol.Name, nameTokens);
        CodeTokenizer.Tokenize(symbol.Signature ?? string.Empty, signatureTokens);
        CodeTokenizer.Tokenize(symbol.FilePath, pathTokens);
        var names = new HashSet<string>(nameTokens, StringComparer.OrdinalIgnoreCase);
        var signatures = new HashSet<string>(signatureTokens, StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(pathTokens, StringComparer.OrdinalIgnoreCase);
        int score = 0;
        int matchedNameTerms = 0;
        foreach (string term in terms)
        {
            if (string.Equals(symbol.Kind, term, StringComparison.OrdinalIgnoreCase))
                score += TaskQueryKindWeight;
            else if (names.Contains(term))
            {
                score += TaskQueryNameWeight;
                matchedNameTerms++;
            }
            else if (signatures.Contains(term))
                score += TaskQuerySignatureWeight;
            else if (paths.Contains(term))
                score += TaskQueryPathWeight;
        }
        if (matchedNameTerms > 1)
            score += (matchedNameTerms - 1) * 10;
        return Math.Min(score, TaskQueryAffinityCap);
    }

    private static string ContextPivotDiversityKey(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static int LineDistance(IndexedSymbol symbol, int line)
    {
        if (symbol.StartLine <= line && line <= Math.Max(symbol.StartLine, symbol.EndLine))
            return 0;
        return Math.Min(
            Math.Abs(line - symbol.StartLine),
            Math.Abs(line - Math.Max(symbol.StartLine, symbol.EndLine)));
    }

    internal static IReadOnlyList<(string File, int Line)> ParseStackFrames(
        string? stackTrace,
        out bool truncated)
    {
        string text = stackTrace ?? string.Empty;
        Match[] frames = StackFramePattern().Matches(text).Cast<Match>()
            .Concat(PythonStackFramePattern().Matches(text).Cast<Match>())
            .OrderBy(static frame => frame.Index)
            .Take(AnchorStackFrameLimit + 1)
            .ToArray();
        truncated = frames.Length > AnchorStackFrameLimit;
        return frames
            .Take(AnchorStackFrameLimit)
            .Select(static frame => (
                frame.Groups["file"].Value,
                int.Parse(
                    frame.Groups["line"].Value,
                    System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private static IReadOnlyList<Candidate> AttachPivotBodies(
        IReadOnlyList<Candidate> candidates,
        int tokenBudget,
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (readBody is null)
            return candidates;

        int pivotCount = candidates.Count(static candidate => candidate.IsPivot);
        if (pivotCount == 0)
            return candidates;

        int maxBodyChars = Math.Min(2400, Math.Max(80, tokenBudget * 2 / pivotCount));
        var enriched = new Candidate[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Candidate candidate = candidates[index];
            if (!candidate.IsPivot)
            {
                enriched[index] = candidate;
                continue;
            }

            ExtractReader.BodyReadResult body = readBody(candidate.Symbol);
            cancellationToken.ThrowIfCancellationRequested();
            if (body.Text is { } text)
            {
                bool truncated = text.Length > maxBodyChars;
                enriched[index] = candidate with
                {
                    Body = truncated ? Truncate(text, maxBodyChars) : text,
                    BodyTruncated = truncated,
                };
            }
            else
            {
                enriched[index] = candidate with
                {
                    BodyUnavailableReason = body.UnavailableReason?.ToString(),
                };
            }
        }

        return enriched;
    }

    internal static ExtractReader.BodyReadResult ReadPivotBody(
        Miller.Indexing.Reads.WorkspaceReadHandle readSession,
        string workspaceRoot,
        IndexedSymbol symbol)
    {
        try
        {
            return ExtractReader.ReadBody(readSession, workspaceRoot, symbol);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ExtractReader.BodyReadResult.Unavailable(
                ExtractReader.BodyUnavailableReason.FileHashUnavailable);
        }
    }

    private static string RequiredLegacyArtifactPath(Miller.Indexing.Reads.WorkspaceReadHandle readSession) =>
        readSession.LegacyArtifactPath
        ?? throw new InvalidOperationException("The content sidecar has not been migrated to family-store reads.");

    /// <summary>
    /// Ranks a hop&gt;0 neighbour by affinity to the anchor so relevance — not arbitrary symbol id — breaks ties
    /// within a hop: +2 per query/pivot identifier token that appears in the neighbour's name, +1 when it shares
    /// a pivot's file, +1 when it shares a pivot's directory. Same-file
    /// neighbours therefore score above same-directory-only ones (they earn both the file and the directory
    /// point). Pure and deterministic; built once per bundle over the query tokens and pivot set.
    /// </summary>
    private readonly struct NeighbourRelevanceScorer
    {
        private static readonly char[] PathSeparators = { '/', '\\' };

        private readonly string[] _tokens;
        private readonly HashSet<string> _pivotFiles;
        private readonly HashSet<string> _pivotDirectories;

        private NeighbourRelevanceScorer(
            string[] tokens,
            HashSet<string> pivotFiles,
            HashSet<string> pivotDirectories)
        {
            _tokens = tokens;
            _pivotFiles = pivotFiles;
            _pivotDirectories = pivotDirectories;
        }

        internal static NeighbourRelevanceScorer Build(string? query, IReadOnlyList<IndexedSymbol> pivots)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in ExtractIdentifierTokens(query))
                tokens.Add(token);
            var pivotFiles = new HashSet<string>(StringComparer.Ordinal);
            var pivotDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (IndexedSymbol pivot in pivots)
            {
                if (!string.IsNullOrEmpty(pivot.Name))
                    tokens.Add(pivot.Name);
                pivotFiles.Add(pivot.FilePath);
                pivotDirectories.Add(DirectoryOf(pivot.FilePath));
            }

            return new NeighbourRelevanceScorer(tokens.ToArray(), pivotFiles, pivotDirectories);
        }

        internal int Score(IndexedSymbol neighbour)
        {
            int score = 0;
            foreach (string token in _tokens)
            {
                if (neighbour.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }
            if (_pivotFiles.Contains(neighbour.FilePath))
                score += 1;
            if (_pivotDirectories.Contains(DirectoryOf(neighbour.FilePath)))
                score += 1;
            return score;
        }

        private static string DirectoryOf(string filePath)
        {
            int separator = filePath.LastIndexOfAny(PathSeparators);
            return separator < 0 ? string.Empty : filePath[..separator];
        }
    }

    private static IReadOnlyList<ReferenceContextItem> BuildReferenceItems(
        IReadOnlyList<Candidate> candidates,
        int tokenBudget,
        int referenceDepth,
        bool excludeTests,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        Func<IReadOnlyList<IndexedSymbol>, IReadOnlyDictionary<string, ReferenceEvidenceBundle>>? readMany,
        bool json,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<ReferenceContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usableCandidates = candidates
            .Where(candidate =>
                !excludeTests ||
                !(candidate.Symbol.IsTest || IsTestPath.Check(candidate.Symbol.FilePath)))
            .ToArray();

        foreach (Candidate candidate in usableCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexedSymbol symbol = candidate.Symbol;
            AddItem(new ReferenceContextItem(
                ItemType: "symbol",
                Reason: candidate.Reason,
                Confidence: "exact",
                Name: symbol.Name,
                Kind: symbol.Kind,
                File: symbol.FilePath,
                Line: symbol.StartLine,
                Hop: candidate.Hop,
                Signature: symbol.Signature,
                SymbolId: symbol.SymbolId,
                Role: candidate.IsPivot ? "pivot" : "neighbour"));
            if (candidate.Body is not null)
            {
                AddItem(new ReferenceContextItem(
                    ItemType: "implementation",
                    Reason: "pivot_body",
                    Confidence: "exact",
                    Name: symbol.Name,
                    Kind: symbol.Kind,
                    File: symbol.FilePath,
                    Line: symbol.StartLine,
                    SymbolId: symbol.SymbolId,
                    Snippet: candidate.Body,
                    AnchorReason: candidate.Reason,
                    Role: "pivot_evidence"));
            }
        }

        IReadOnlyList<IndexedSymbol> symbols = usableCandidates.Select(static candidate => candidate.Symbol).ToArray();
        IReadOnlyList<TextContentSearchHit> contentChunks = readContentChunks(symbols, excludeTests);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (TextContentSearchHit hit in contentChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludeTests && IsTestPath.Check(hit.Path ?? hit.DisplayPath))
                continue;
            AddItem(new ReferenceContextItem(
                ItemType: "content_chunk",
                Reason: "containing_chunk",
                Confidence: symbols.Any(symbol => string.Equals(symbol.SymbolId, hit.ContainingSymbolId, StringComparison.Ordinal))
                    ? "exact"
                    : "name_based",
                Name: hit.ContainingSymbolName ?? hit.DisplayPath,
                Kind: hit.ContentKind,
                File: hit.Path ?? hit.DisplayPath,
                Line: hit.Line,
                SourceId: hit.SourceId,
                ChunkId: hit.ChunkId,
                ContainingSymbolId: hit.ContainingSymbolId,
                LineStart: hit.LineStart,
                LineEnd: hit.LineEnd,
                Snippet: hit.Snippet));
        }

        ReferenceContextItem minimumIdentifier = new(
            "identifier", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0);
        ReferenceContextItem[] fixedItems = items
            .Where(static item => ReferenceAllocationTier(item) <= 1)
            .ToArray();
        ReferenceContextItem[] minimumEvidenceItems = [.. fixedItems, minimumIdentifier];
        int renderBudget = tokenBudget >= 512
            ? Math.Max(1, tokenBudget * 3 / 4)
            : tokenBudget;
        string fixedOutput = json
            ? RenderReferenceJson(fixedItems, anchorDiagnostics, query, boundOptionalFields: true)
            : RenderReferenceCompact(fixedItems, anchorDiagnostics, query);
        string minimumEvidenceOutput = json
            ? RenderReferenceJson(minimumEvidenceItems, anchorDiagnostics, query, boundOptionalFields: true)
            : RenderReferenceCompact(minimumEvidenceItems, anchorDiagnostics, query);
        bool evidenceFits = tokenBudget > 0 &&
            TokenEstimator.Count(minimumEvidenceOutput) <= renderBudget &&
            TokenEstimator.Count(fixedOutput) <= renderBudget;

        if (referenceDepth >= 1 && evidenceFits)
        {
            IReadOnlyDictionary<string, ReferenceEvidenceBundle>? evidenceById = null;
            if (readMany is not null && ReferenceEvidenceBatchEnabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                evidenceById = readMany(symbols);
                ArgumentNullException.ThrowIfNull(evidenceById);
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (Candidate candidate in usableCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedSymbol symbol = candidate.Symbol;
                OutgoingReferenceEvidenceSet outgoing;
                ReferenceEvidenceSet inbound;
                if (evidenceById is not null)
                {
                    if (!evidenceById.TryGetValue(symbol.SymbolId, out ReferenceEvidenceBundle? evidence))
                        continue;
                    outgoing = evidence.Outgoing;
                    inbound = evidence.Inbound;
                }
                else
                {
                    outgoing = readOutgoingEvidence(symbol);
                    inbound = readReferenceEvidence(symbol);
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (OutgoingReferenceEvidence callee in outgoing.Exact)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (excludeTests && IsTestPath.Check(callee.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: IsCallLike(callee.Kind) ? "callee" : "dependency",
                        Confidence: "exact",
                        Name: callee.TargetName,
                        Kind: callee.SourceKind,
                        File: callee.FilePath,
                        Line: callee.StartLine ?? 0,
                        ContainingSymbolId: callee.ContainingSymbolId,
                        TargetSymbolId: callee.TargetSymbolId,
                        ResolutionStatus: "exact",
                        Provenance: EvidenceSourceLabel(callee.Source),
                        EvidenceConfidence: callee.Confidence));
                }

                foreach (OutgoingReferenceEvidence callee in outgoing.Fallback)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (excludeTests && IsTestPath.Check(callee.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: IsCallLike(callee.Kind) ? "unresolved_callee" : "unresolved_dependency",
                        Confidence: "fallback",
                        Name: callee.TargetName,
                        Kind: callee.SourceKind,
                        File: callee.FilePath,
                        Line: callee.StartLine ?? 0,
                        ContainingSymbolId: callee.ContainingSymbolId,
                        ResolutionStatus: "fallback",
                        Provenance: EvidenceSourceLabel(callee.Source),
                        EvidenceConfidence: callee.Confidence));
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (ReferenceEvidence reference in inbound.Exact)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (excludeTests && IsTestPath.Check(reference.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: "reference",
                        Confidence: "exact",
                        Name: symbol.Name,
                        Kind: reference.SourceKind,
                        File: reference.FilePath,
                        Line: reference.StartLine ?? 0,
                        ContainingSymbolId: reference.ContainingSymbolId,
                        TargetSymbolId: reference.TargetSymbolId,
                        ResolutionStatus: "exact",
                        Provenance: EvidenceSourceLabel(reference.Source),
                        EvidenceConfidence: reference.Confidence));
                }

                foreach (ReferenceEvidence reference in inbound.Fallback)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (excludeTests && IsTestPath.Check(reference.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: "possible_reference",
                        Confidence: "fallback",
                        Name: symbol.Name,
                        Kind: reference.SourceKind,
                        File: reference.FilePath,
                        Line: reference.StartLine ?? 0,
                        ContainingSymbolId: reference.ContainingSymbolId,
                        TargetSymbolId: reference.TargetSymbolId,
                        ResolutionStatus: "fallback",
                        Provenance: EvidenceSourceLabel(reference.Source),
                        EvidenceConfidence: reference.Confidence));
                }
            }
        }

        return items;

        void AddItem(ReferenceContextItem item)
        {
            string key = item.ItemType switch
            {
                "symbol" => "symbol:" + item.SymbolId,
                "implementation" => "implementation:" + item.SymbolId,
                "content_chunk" => "chunk:" + item.SourceId + ":" + item.ChunkId,
                "identifier" => "identifier:" + item.Reason + ":" + item.File + ":" + item.Line + ":" + item.Name + ":" + item.Kind + ":" + item.ContainingSymbolId + ":" + item.TargetSymbolId,
                _ => item.ItemType + ":" + item.File + ":" + item.Line + ":" + item.Name,
            };
            if (seen.Add(key))
                items.Add(item);
        }
    }

    // ---------- identifier-token extraction (failing_test / stack_trace) ----------

    /// <summary>
    /// Pull identifier-like tokens out of a free-form hint (a failing-test name, a stack-trace frame). Splits on
    /// non-identifier characters and dot/scope separators so a frame like <c>OrderService.Process(int)</c> yields
    /// <c>OrderService</c> and <c>Process</c>. Tokens shorter than 2 chars or that are not identifier-shaped are
    /// dropped; the caller keeps only those that name an indexed symbol, so noise (keywords, file names) falls out.
    /// </summary>
    internal static IEnumerable<string> ExtractIdentifierTokens(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierPattern().Matches(hint))
        {
            string token = m.Value;
            if (token.Length >= 2 && seen.Add(token))
                yield return token;
        }
    }

    internal static IReadOnlyList<IndexedSymbol> FindNamedAnchorCandidates(
        ISymbolLookupIndex index,
        string? hint,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(index);
        var matches = new List<IndexedSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] tokens = ExtractIdentifierTokens(hint)
            .Take(AnchorIdentifierTokenLimit + 1)
            .ToArray();
        truncated = tokens.Length > AnchorIdentifierTokenLimit;
        foreach (string token in tokens.Take(AnchorIdentifierTokenLimit))
        {
            IndexedSymbol[] tokenMatches = index.FindByName(token)
                .Where(static symbol => IsQueryPivot(symbol))
                .Take(AnchorMatchesPerToken + 1)
                .ToArray();
            if (tokenMatches.Length > AnchorMatchesPerToken)
                truncated = true;
            foreach (IndexedSymbol match in tokenMatches.Take(AnchorMatchesPerToken))
            {
                if (seen.Add(match.SymbolId))
                    matches.Add(match);
            }
        }
        return matches;
    }

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"(?<file>(?:[A-Za-z]:)?[^()\s]+?\.[A-Za-z0-9]+):(?:line\s+)?(?<line>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StackFramePattern();

    [GeneratedRegex(
        @"File\s+""(?<file>[^""]+)"",\s+line\s+(?<line>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PythonStackFramePattern();

    private static string CompactCostLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine)
          .Append("  hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (!string.IsNullOrEmpty(c.Body))
            sb.Append("  ").Append(c.Body);
        return sb.ToString();
    }

    private static string GroupedCandidateLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append("  :").Append(s.StartLine).Append(' ')
          .Append(s.Name).Append(' ')
          .Append(s.Kind);
        if (c.Hop > 0)
            sb.Append(" hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        return sb.ToString();
    }

    private const int NextInspectCount = 3;

    private static string RenderCompact(IReadOnlyList<Candidate> selected) =>
        RenderCompact(selected, [], query: string.Empty);

    private static string RenderCompact(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics) =>
        RenderCompact(selected, anchorDiagnostics, query: string.Empty);

    private static string RenderCompact(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query)
    {
        if (selected.Count == 0)
        {
            var empty = new StringBuilder("No evidence fit token_budget.");
            if (anchorDiagnostics.Count == 0)
                return empty.ToString();
            empty.Append('\n');
            AppendAnchorDiagnosticsCompact(empty, anchorDiagnostics);
            ContextEvidenceDisposition emptyDisposition = DispositionFor(selected);
            empty.Append("## disposition\n")
                .Append("evidence=")
                .Append(emptyDisposition.Status)
                .Append("  reason=")
                .Append(emptyDisposition.Reason);
            return empty.ToString();
        }

        var pivots = new List<Candidate>();
        var neighbours = new List<Candidate>();
        foreach (Candidate candidate in selected)
        {
            if (candidate.Hop == 0)
                pivots.Add(candidate);
            else
                neighbours.Add(candidate);
        }

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");

        AppendAnchorDiagnosticsCompact(sb, anchorDiagnostics);

        if (pivots.Count > 0)
        {
            sb.Append("## pivots\n");
            foreach (Candidate pivot in pivots)
                sb.Append(PivotLine(pivot)).Append('\n');
        }

        Candidate[] implementations = pivots
            .Where(static candidate => candidate.Body is not null)
            .ToArray();
        if (implementations.Length > 0)
        {
            sb.Append("## implementations\n");
            foreach (Candidate implementation in implementations)
            {
                sb.Append(implementation.Symbol.Name)
                    .Append("  ")
                    .Append(implementation.Symbol.FilePath)
                    .Append(':')
                    .Append(implementation.Symbol.StartLine)
                    .Append('\n');
                foreach (string line in implementation.Body!.Split('\n'))
                    sb.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
                if (implementation.BodyTruncated)
                    sb.Append("    … body truncated to fit allocation\n");
            }
        }

        if (neighbours.Count > 0)
        {
            sb.Append("## neighbours\n");
            var groups = new List<(string FilePath, List<Candidate> Candidates)>();
            for (int i = 0; i < neighbours.Count; i++)
            {
                Candidate candidate = neighbours[i];
                int groupIndex = groups.FindIndex(group => group.FilePath == candidate.Symbol.FilePath);
                if (groupIndex >= 0)
                    groups[groupIndex].Candidates.Add(candidate);
                else
                    groups.Add((candidate.Symbol.FilePath, new List<Candidate> { candidate }));
            }

            foreach (var group in groups)
            {
                sb.Append(group.FilePath).Append(':').Append('\n');
                foreach (Candidate candidate in group.Candidates)
                    sb.Append(GroupedCandidateLine(candidate)).Append('\n');
            }

        }

        ContextEvidenceDisposition disposition = DispositionFor(selected);
        sb.Append("## disposition\n")
            .Append("evidence=")
            .Append(disposition.Status)
            .Append("  reason=")
            .Append(disposition.Reason)
            .Append('\n');

        ContextNextAction[] nextActions = BuildDiscoveryNextActions(pivots, disposition, query);
        if (nextActions.Length > 0)
        {
            sb.Append("## next inspect\n");
            foreach (ContextNextAction action in nextActions)
                sb.Append(action.Call).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string NextInspectLine(IndexedSymbol symbol) =>
        NextInspectLine(symbol.Name, symbol.FilePath);

    private static string NextInspectLine(string name, string filePath) =>
        "inspect(target=\"" + EscapeCallString(name) +
        "\", scope=\"" + EscapeCallString(filePath) +
        "\", depth=\"overview\")";

    private static string NextSourceSearchLine(string query) =>
        "search(query=\"" + EscapeDiagnosticQuery(query) + "\", mode=\"source\")";

    private static ContextNextAction[] BuildReferenceDiscoveryNextActions(
        IReadOnlyList<ReferenceContextItem> selected,
        ContextEvidenceDisposition disposition,
        string query)
    {
        ReferenceContextItem[] pivots = selected
            .Where(static item => item.ItemType == "symbol" && item.Role == "pivot")
            .ToArray();
        if (disposition.Status == "sufficient" || pivots.Length == 0)
            return [];

        ReferenceContextItem[] implementationPivots = pivots
            .Where(static item => CarriesImplementationKind(item.Kind))
            .ToArray();
        bool anyImplementation = implementationPivots.Length > 0;
        bool suggestSource =
            !string.IsNullOrWhiteSpace(query) &&
            (!anyImplementation ||
             disposition.Reason is "pivot_value_declaration_only" or "discovery_implementation_present"
                 or "symbol_and_relation_evidence_only");

        var actions = new List<ContextNextAction>(NextInspectCount + 1);
        if (!anyImplementation)
        {
            if (suggestSource)
            {
                actions.Add(new ContextNextAction(
                    NextSourceSearchLine(query),
                    "source or docs may hold conceptual language beyond value declarations"));
            }
            return actions.ToArray();
        }

        int inspectCount = Math.Min(NextInspectCount, implementationPivots.Length);
        for (int i = 0; i < inspectCount; i++)
        {
            ReferenceContextItem pivot = implementationPivots[i];
            actions.Add(new ContextNextAction(
                NextInspectLine(pivot.Name, pivot.File),
                "inspect a pivot implementation"));
        }

        if (suggestSource)
        {
            actions.Add(new ContextNextAction(
                NextSourceSearchLine(query),
                "source or docs may hold conceptual language beyond value declarations"));
        }

        return actions.ToArray();
    }

    private static ContextNextAction[] BuildDiscoveryNextActions(
        IReadOnlyList<Candidate> pivots,
        ContextEvidenceDisposition disposition,
        string query)
    {
        if (disposition.Status == "sufficient" || pivots.Count == 0)
            return [];

        IndexedSymbol[] implementationPivots = pivots
            .Where(static pivot => CarriesImplementation(pivot.Symbol))
            .Select(static pivot => pivot.Symbol)
            .ToArray();
        bool anyImplementation = implementationPivots.Length > 0;
        bool suggestSource =
            !string.IsNullOrWhiteSpace(query) &&
            (!anyImplementation ||
             disposition.Reason is "pivot_value_declaration_only" or "discovery_implementation_present");

        var actions = new List<ContextNextAction>(NextInspectCount + 1);
        if (!anyImplementation)
        {
            if (suggestSource)
            {
                actions.Add(new ContextNextAction(
                    NextSourceSearchLine(query),
                    "source or docs may hold conceptual language beyond value declarations"));
            }
            return actions.ToArray();
        }

        int inspectCount = Math.Min(NextInspectCount, implementationPivots.Length);
        for (int i = 0; i < inspectCount; i++)
        {
            actions.Add(new ContextNextAction(
                NextInspectLine(implementationPivots[i]),
                "inspect a pivot implementation"));
        }

        if (suggestSource)
        {
            actions.Add(new ContextNextAction(
                NextSourceSearchLine(query),
                "source or docs may hold conceptual language beyond value declarations"));
        }

        return actions.ToArray();
    }

    private static string EscapeCallString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeDiagnosticQuery(string value)
    {
        return ToolDiagnosticText.EscapeCallArgument(value);
    }

    private static string PivotLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine).Append("  pivot");
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (c.AnchorLine is int anchorLine)
            sb.Append("  anchor_line=").Append(anchorLine);
        return sb.ToString();
    }

    private static string RenderJson(IReadOnlyList<Candidate> selected) =>
        RenderJson(selected, [], query: string.Empty, boundOptionalFields: false);

    private static string RenderBoundedJson(IReadOnlyList<Candidate> selected) =>
        RenderJson(selected, [], query: string.Empty, boundOptionalFields: true);

    private static string RenderJson(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        bool boundOptionalFields) =>
        RenderJson(selected, anchorDiagnostics, query: string.Empty, boundOptionalFields);

    private static string RenderJson(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        bool boundOptionalFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WritePropertyName("bundle");
            w.WriteStartArray();
            foreach (var c in selected)
            {
                var s = c.Symbol;
                w.WriteStartObject();
                w.WriteString("item_type", "symbol");
                w.WriteString("name", s.Name);
                w.WriteString("kind", s.Kind);
                w.WriteString("file", s.FilePath);
                w.WriteNumber("line", s.StartLine);
                w.WriteNumber("hop", c.Hop);
                w.WriteString("role", c.IsPivot ? "pivot" : "neighbour");
                w.WriteString("reason", c.Reason);
                w.WriteString("confidence", "exact");
                if (c.AnchorLine is int anchorLine)
                    w.WriteNumber("anchor_line", anchorLine);
                if (s.Signature is null) w.WriteNull("signature");
                else w.WriteString("signature", boundOptionalFields
                    ? Truncate(s.Signature, ToolRenderLimits.SignatureMaxLength)
                    : s.Signature);
                w.WriteString("symbol_id", s.SymbolId);
                if (c.Body is not null)
                {
                    w.WriteString("body", c.Body);
                    w.WriteBoolean("body_truncated", c.BodyTruncated);
                }
                else if (c.BodyUnavailableReason is not null)
                {
                    w.WriteString("body_unavailable_reason", c.BodyUnavailableReason);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteAnchorDiagnosticsJson(w, anchorDiagnostics);
            ContextEvidenceDisposition disposition = DispositionFor(selected);
            WriteDispositionJson(w, disposition);
            if (disposition.Status != "sufficient")
            {
                Candidate[] pivots = selected
                    .Where(static candidate => candidate.IsPivot)
                    .ToArray();
                ContextNextAction[] nextActions = BuildDiscoveryNextActions(pivots, disposition, query);
                if (nextActions.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (ContextNextAction action in nextActions)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", action.Call);
                        w.WriteString("reason", action.Reason);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string EvidenceSourceLabel(ReferenceEvidenceSource source) => source switch
    {
        ReferenceEvidenceSource.IdentifierDirect => "identifier_direct",
        ReferenceEvidenceSource.IdentifierResolution => "identifier_resolution",
        ReferenceEvidenceSource.Relationship => "relationship",
        ReferenceEvidenceSource.PendingResolution => "pending_resolution",
        ReferenceEvidenceSource.NameFallback => "name_fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static bool IsCallLike(ReferenceKind kind) =>
        kind is ReferenceKind.Call or ReferenceKind.Instantiation;

    private static string ReferenceCostLine(ReferenceContextItem item)
    {
        var sb = new StringBuilder();
        sb.Append(item.ItemType).Append(' ')
          .Append(item.Reason).Append(' ')
          .Append(item.Confidence).Append(' ')
          .Append(item.Name).Append(' ')
          .Append(item.Kind).Append(' ')
          .Append(item.File).Append(':').Append(item.Line);
        if (item.Hop is not null)
            sb.Append(" hop=").Append(item.Hop.Value);
        if (!string.IsNullOrEmpty(item.Signature))
            sb.Append(' ').Append(Truncate(item.Signature!, ToolRenderLimits.SignatureMaxLength));
        if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append(' ').Append(Truncate(item.Snippet!, ToolRenderLimits.SignatureMaxLength));
        if (item.ResolutionStatus is not null)
            sb.Append(" resolution=").Append(item.ResolutionStatus);
        if (item.Provenance is not null)
            sb.Append(" source=").Append(item.Provenance);
        if (item.EvidenceConfidence is not null)
            sb.Append(" evidence_confidence=")
                .Append(item.EvidenceConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (item.AnchorReason is not null)
            sb.Append(" anchor=").Append(item.AnchorReason);
        if (item.Role is not null)
            sb.Append(" role=").Append(item.Role);
        return sb.ToString();
    }

    private static int ReferenceAllocationTier(ReferenceContextItem item) =>
        item.ItemType switch
        {
            "implementation" => 0,
            "symbol" when item.Hop == 0 => 0,
            "content_chunk" => 1,
            "identifier" => 2,
            _ => 3,
        };

    private static string ReferenceCompactLine(ReferenceContextItem item)
    {
        var sb = new StringBuilder();
        sb.Append("  :").Append(item.Line).Append(' ')
          .Append(item.Name).Append(' ')
          .Append(item.Kind)
          .Append(" reason=").Append(item.Reason)
          .Append(" confidence=").Append(item.Confidence);
        if (item.Hop is not null)
            sb.Append(" hop=").Append(item.Hop.Value);
        if (!string.IsNullOrEmpty(item.Signature))
            sb.Append("  ").Append(Truncate(item.Signature!, ToolRenderLimits.SignatureMaxLength));
        else if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append("  ").Append(Truncate(item.Snippet!, ToolRenderLimits.SignatureMaxLength));
        if (item.ResolutionStatus is not null)
            sb.Append(" resolution=").Append(item.ResolutionStatus);
        if (item.Provenance is not null)
            sb.Append(" source=").Append(item.Provenance);
        if (item.EvidenceConfidence is not null)
            sb.Append(" evidence_confidence=")
                .Append(item.EvidenceConfidence.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (item.AnchorReason is not null)
            sb.Append(" anchor=").Append(item.AnchorReason);
        if (item.Role is not null)
            sb.Append(" role=").Append(item.Role);
        return sb.ToString();
    }

    private static string RenderReferenceCompact(IReadOnlyList<ReferenceContextItem> selected) =>
        RenderReferenceCompact(selected, [], query: string.Empty);

    private static string RenderReferenceCompact(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics) =>
        RenderReferenceCompact(selected, anchorDiagnostics, query: string.Empty);

    private static string RenderReferenceCompact(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query)
    {
        if (selected.Count == 0)
        {
            var empty = new StringBuilder("No evidence fit token_budget.");
            if (anchorDiagnostics.Count == 0)
                return empty.ToString();
            empty.Append('\n');
            AppendAnchorDiagnosticsCompact(empty, anchorDiagnostics);
            ContextEvidenceDisposition emptyDisposition = DispositionForReference(selected);
            empty.Append("## disposition\n")
                .Append("evidence=")
                .Append(emptyDisposition.Status)
                .Append("  reason=")
                .Append(emptyDisposition.Reason);
            return empty.ToString();
        }

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");
        var groups = new List<(string FilePath, List<ReferenceContextItem> Items)>();
        foreach (ReferenceContextItem item in selected)
        {
            int groupIndex = groups.FindIndex(group => group.FilePath == item.File);
            if (groupIndex >= 0)
                groups[groupIndex].Items.Add(item);
            else
                groups.Add((item.File, new List<ReferenceContextItem> { item }));
        }

        foreach (var group in groups)
        {
            sb.Append(group.FilePath).Append(':').Append('\n');
            foreach (ReferenceContextItem item in group.Items)
                sb.Append(ReferenceCompactLine(item)).Append('\n');
        }

        AppendAnchorDiagnosticsCompact(sb, anchorDiagnostics);
        ContextEvidenceDisposition disposition = DispositionForReference(selected);
        sb.Append("## disposition\n")
            .Append("evidence=")
            .Append(disposition.Status)
            .Append("  reason=")
            .Append(disposition.Reason)
            .Append('\n');
        ContextNextAction[] nextActions = BuildReferenceDiscoveryNextActions(selected, disposition, query);
        if (nextActions.Length > 0)
        {
            sb.Append("## next inspect\n");
            foreach (ContextNextAction action in nextActions)
                sb.Append(action.Call).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderReferenceJson(IReadOnlyList<ReferenceContextItem> selected) =>
        RenderReferenceJson(selected, [], query: string.Empty, boundOptionalFields: false);

    private static string RenderBoundedReferenceJson(IReadOnlyList<ReferenceContextItem> selected) =>
        RenderReferenceJson(selected, [], query: string.Empty, boundOptionalFields: true);

    private static string RenderReferenceJson(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        bool boundOptionalFields) =>
        RenderReferenceJson(selected, anchorDiagnostics, query: string.Empty, boundOptionalFields);

    private static string RenderReferenceJson(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
        string query,
        bool boundOptionalFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WritePropertyName("bundle");
            w.WriteStartArray();
            foreach (ReferenceContextItem item in selected)
            {
                w.WriteStartObject();
                w.WriteString("item_type", item.ItemType);
                w.WriteString("reason", item.Reason);
                w.WriteString("confidence", item.Confidence);
                w.WriteString("name", item.Name);
                w.WriteString("kind", item.Kind);
                w.WriteString("file", item.File);
                w.WriteNumber("line", item.Line);
                if (item.Hop is int hop)
                    w.WriteNumber("hop", hop);
                if (item.Signature is null) w.WriteNull("signature");
                else w.WriteString("signature", boundOptionalFields
                    ? Truncate(item.Signature, ToolRenderLimits.SignatureMaxLength)
                    : item.Signature);
                if (item.SymbolId is not null)
                    w.WriteString("symbol_id", item.SymbolId);
                if (item.ContainingSymbolId is not null)
                    w.WriteString("containing_symbol_id", item.ContainingSymbolId);
                if (item.TargetSymbolId is not null)
                    w.WriteString("target_symbol_id", item.TargetSymbolId);
                if (item.ResolutionStatus is not null)
                    w.WriteString("resolution_status", item.ResolutionStatus);
                if (item.Provenance is not null)
                    w.WriteString("provenance", item.Provenance);
                if (item.EvidenceConfidence is not null)
                    w.WriteNumber("evidence_confidence", item.EvidenceConfidence.Value);
                if (item.AnchorReason is not null)
                    w.WriteString("anchor_reason", item.AnchorReason);
                if (item.Role is not null)
                    w.WriteString("role", item.Role);
                if (item.SourceId is not null)
                    w.WriteString("source_id", item.SourceId);
                if (item.ChunkId is not null)
                    w.WriteString("chunk_id", item.ChunkId);
                if (item.LineStart is int lineStart)
                    w.WriteNumber("line_start", lineStart);
                if (item.LineEnd is int lineEnd)
                    w.WriteNumber("line_end", lineEnd);
                if (item.Snippet is not null)
                    w.WriteString("snippet", boundOptionalFields
                        ? Truncate(item.Snippet, ToolRenderLimits.SignatureMaxLength)
                        : item.Snippet);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteAnchorDiagnosticsJson(w, anchorDiagnostics);
            ContextEvidenceDisposition disposition = DispositionForReference(selected);
            WriteDispositionJson(w, disposition);
            if (disposition.Status != "sufficient")
            {
                ContextNextAction[] nextActions =
                    BuildReferenceDiscoveryNextActions(selected, disposition, query);
                if (nextActions.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (ContextNextAction action in nextActions)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", action.Call);
                        w.WriteString("reason", action.Reason);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void AppendAnchorDiagnosticsCompact(
        StringBuilder builder,
        IReadOnlyList<ContextAnchorDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        builder.Append("## anchor diagnostics\n");
        foreach (ContextAnchorDiagnostic diagnostic in diagnostics)
        {
            string value = Truncate(
                diagnostic.Value.Replace('\r', ' ').Replace('\n', ' '),
                ToolRenderLimits.SignatureMaxLength);
            builder.Append(diagnostic.Kind)
                .Append("  ")
                .Append(value)
                .Append("  reason=")
                .Append(diagnostic.Reason)
                .Append('\n');
        }
    }

    private static void WriteAnchorDiagnosticsJson(
        Utf8JsonWriter writer,
        IReadOnlyList<ContextAnchorDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        writer.WritePropertyName("anchor_diagnostics");
        writer.WriteStartArray();
        foreach (ContextAnchorDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", diagnostic.Kind);
            writer.WriteString("value", Truncate(diagnostic.Value, ToolRenderLimits.SignatureMaxLength));
            writer.WriteString("reason", diagnostic.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static ContextEvidenceDisposition DispositionFor(IReadOnlyList<Candidate> selected)
    {
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                CarriesImplementation(candidate.Symbol) &&
                IsAuthoritativeImplementationReason(candidate.Reason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        // Discovery-tier implementation bodies (source_rescue_*, semantic_rank_*, query_term_*_subject)
        // beat an authoritative value-declaration sibling for the reason label — they still cannot
        // authorize sufficient, but must not be masked as pivot_value_declaration_only.
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                CarriesImplementation(candidate.Symbol)))
            return new ContextEvidenceDisposition("partial", "discovery_implementation_present");
        if (selected.Any(static candidate =>
                candidate.IsPivot &&
                candidate.Body is not null &&
                IsAuthoritativeImplementationReason(candidate.Reason)))
            return new ContextEvidenceDisposition("partial", "pivot_value_declaration_only");
        if (selected.Any(static candidate => candidate.IsPivot))
            return new ContextEvidenceDisposition("partial", "pivot_signature_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
    }

    /// <summary>
    /// Whether a rendered body is an implementation rather than a declared value. A constant, variable, field, or
    /// property body is the value it was assigned, so it can never be the implementation evidence
    /// <c>sufficient</c> attests to — and a top-ranked one must not tell the caller to stop looking.
    /// </summary>
    private static bool CarriesImplementation(IndexedSymbol symbol) =>
        CarriesImplementationKind(symbol.Kind);

    private static bool CarriesImplementationKind(string? kind) =>
        kind is not ("constant" or "variable" or "field" or "property");

    private static ContextEvidenceDisposition DispositionForReference(
        IReadOnlyList<ReferenceContextItem> selected)
    {
        // Authoritative implementation body only — value declarations never authorize sufficient.
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                CarriesImplementationKind(item.Kind) &&
                IsAuthoritativeImplementationReason(item.AnchorReason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        // Exact containing chunks authorize sufficient only when the matched pivot is itself
        // authoritative (entry/edited/stack/full-query). Discovery pivots (source_rescue_*,
        // semantic_rank_*, query_term_*) must not complete via a free content-chunk ride-along.
        if (selected.Any(item =>
                item.ItemType == "content_chunk" &&
                item.Confidence == "exact" &&
                HasAuthoritativePivotForSymbol(selected, item.ContainingSymbolId)))
            return new ContextEvidenceDisposition("sufficient", "exact_containing_content_present");
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                CarriesImplementationKind(item.Kind)))
            return new ContextEvidenceDisposition("partial", "discovery_implementation_present");
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                IsAuthoritativeImplementationReason(item.AnchorReason)))
            return new ContextEvidenceDisposition("partial", "pivot_value_declaration_only");
        if (selected.Any(static item => item.ItemType == "symbol"))
            return new ContextEvidenceDisposition("partial", "symbol_and_relation_evidence_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
    }

    private static bool HasAuthoritativePivotForSymbol(
        IReadOnlyList<ReferenceContextItem> selected,
        string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
            return false;
        return selected.Any(item =>
            item.ItemType == "symbol" &&
            item.Role == "pivot" &&
            string.Equals(item.SymbolId, symbolId, StringComparison.Ordinal) &&
            IsAuthoritativeImplementationReason(item.Reason));
    }

    private static bool IsAuthoritativeImplementationReason(string? reason) =>
        reason is "entry_symbol" or
            "entry_file" or
            "edited_file" or
            "failing_test" or
            "stack_frame" or
            "stack_symbol" ||
        reason?.StartsWith("query_rank_", StringComparison.Ordinal) == true;

    private static void WriteDispositionJson(
        Utf8JsonWriter writer,
        ContextEvidenceDisposition disposition)
    {
        writer.WritePropertyName("disposition");
        writer.WriteStartObject();
        writer.WriteString("status", disposition.Status);
        writer.WriteString("reason", disposition.Reason);
        writer.WriteEndObject();
    }

    internal static string Truncate(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (max < 1)
            return string.Empty;
        if (value.Length <= max)
            return value;

        int prefixLength = max - 1;
        if (prefixLength > 0 &&
            char.IsHighSurrogate(value[prefixLength - 1]) &&
            char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }
        return value[..prefixLength] + "…";
    }
}
