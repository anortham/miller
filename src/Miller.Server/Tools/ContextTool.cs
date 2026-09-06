using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Semantic;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools.Context;
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
    private readonly ContextQueryService _queryService;

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
        _queryService = new ContextQueryService(
            workspaceProvider,
            semanticArm,
            semanticSidecar,
            phaseObserver,
            lookupPhaseObserver);
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
        "context query=\"<the task in this area>\". Compact by default; " +
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
        [Description("Registered workspace selector: display ID, unique prefix, full ID, or root path. Required for MCP calls.")] [System.ComponentModel.DataAnnotations.Required] string? workspace_id = null,
        [Description("Wait for a refresh before reading. With workspace_id the default now serves the pinned index immediately and refreshes in the background; true still waits, false does zero refresh work.")]
        bool? ensure_fresh = null,
        [Description("Workspace-relative files changed by the current task; their symbols rank as pivots. Optional.")]
        string[]? edited_files = null,
        [Description("Framework request cancellation token.")]
        CancellationToken cancellationToken = default)
    {
        return _queryService.Execute(new ContextQueryRequest(
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
            cancellationToken));
    }

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

    internal const int AnchorAmbiguousMatchLimit = ContextBundleBuilder.AnchorAmbiguousMatchLimit;
    internal const int AnchorIdentifierTokenLimit = ContextBundleBuilder.AnchorIdentifierTokenLimit;
    internal const int AnchorMatchesPerToken = ContextBundleBuilder.AnchorMatchesPerToken;
    internal const int AnchorStackFrameLimit = ContextBundleBuilder.AnchorStackFrameLimit;
    internal const int ReferenceRowsPerSymbol = 12;
    internal const string ReferenceEvidenceBatchEnvironmentVariable =
        ContextBundleBuilder.ReferenceEvidenceBatchEnvironmentVariable;
    internal const int ReferenceReadOverscanFactor = ContextBundleBuilder.ReferenceReadOverscanFactor;
    internal const int ReferenceReadChunkSize = ContextBundleBuilder.ReferenceReadChunkSize;
    internal const int ContentChunksPerSymbol = 2;
    internal const int TermRescueStrengthCap = ContextBundleBuilder.TermRescueStrengthCap;
    internal const int SourceRescueStrength = ContextBundleBuilder.SourceRescueStrength;
    internal const int SemanticSeedStrength = ContextBundleBuilder.SemanticSeedStrength;
    internal const int SourceRescueHitLimit = ContextBundleBuilder.SourceRescueHitLimit;
    internal const int SourceRescueSeedLimit = ContextBundleBuilder.SourceRescueSeedLimit;

    /// <summary>
    /// Map a bounded source/doc content search onto unique containing symbols for NL context queries.
    /// Fail-soft: missing content index or search errors yield no seeds.
    /// </summary>
    internal static IReadOnlyList<ContextSourceSeed> LoadSourceRescueSeeds(
        ISymbolLookupIndex index,
        ITextContentSearchIndex? contentIndex,
        string query,
        bool excludeTests) =>
        ContextBundleBuilder.LoadSourceRescueSeeds(index, contentIndex, query, excludeTests);

    internal static int TaskQueryAffinity(IndexedSymbol symbol, IReadOnlyList<string> terms) =>
        ContextBundleBuilder.TaskQueryAffinity(symbol, terms);

    internal static IReadOnlyList<(string File, int Line)> ParseStackFrames(
        string? stackTrace,
        out bool truncated) =>
        ContextBundleBuilder.ParseStackFrames(stackTrace, out truncated);

    internal static IEnumerable<string> ExtractIdentifierTokens(string? hint) =>
        ContextBundleBuilder.ExtractIdentifierTokens(hint);

    internal static IReadOnlyList<IndexedSymbol> FindNamedAnchorCandidates(
        ISymbolLookupIndex index,
        string? hint,
        out bool truncated) =>
        ContextBundleBuilder.FindNamedAnchorCandidates(index, hint, out truncated);

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
        ContextBundleBuildResult built = ContextBundleBuilder.BuildActionable(
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
            cancellationToken,
            phaseObserver,
            retrieval);
        candidatesExamined = built.CandidatesExamined;
        if (built.Candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(built.AnchorDiagnostics, tokenBudget, json);
        }

        var packCandidates = new List<PackCandidate<Candidate>>(built.Candidates.Count);
        foreach (Candidate candidate in built.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cost = (int)TokenEstimator.Count(CompactCostLine(candidate));
            packCandidates.Add(new PackCandidate<Candidate>(
                candidate,
                cost,
                AllocationTier: candidate.IsPivot ? 0 : 2));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Candidate> selected = ContextPacker.PackAllocated(packCandidates, tokenBudget);
        phaseObserver?.Invoke("candidate_pack");
        Func<IReadOnlyList<Candidate>, string> renderer = json
            ? selected => RenderJson(selected, built.AnchorDiagnostics, query, boundOptionalFields: false)
            : selected => RenderCompact(selected, built.AnchorDiagnostics, query);
        Func<IReadOnlyList<Candidate>, string> boundedRenderer = json
            ? selected => RenderJson(selected, built.AnchorDiagnostics, query, boundOptionalFields: true)
            : selected => RenderCompact(selected, built.AnchorDiagnostics, query);
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
        ContextQueryRetrieval? retrieval = null,
        Action<ContextReferenceReadCounts>? referenceReadObserver = null)
    {
        ContextReferenceBuildResult built = ContextBundleBuilder.BuildReferenceAware(
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
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks,
            readMany,
            (items, diagnostics, renderedQuery) => TokenEstimator.Count(
                json
                    ? RenderReferenceJson(items, diagnostics, renderedQuery, boundOptionalFields: true)
                    : RenderReferenceCompact(items, diagnostics, renderedQuery)),
            cancellationToken,
            phaseObserver,
            retrieval);
        candidatesExamined = built.CandidatesExamined;
        if (built.Candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(built.AnchorDiagnostics, tokenBudget, json);
        }

        referenceReadObserver?.Invoke(built.ReadCounts);
        phaseObserver?.Invoke("reference_items");

        var packCandidates = new List<PackCandidate<ReferenceContextItem>>(built.Items.Count);
        foreach (ReferenceContextItem item in built.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            packCandidates.Add(new PackCandidate<ReferenceContextItem>(
                item,
                (int)TokenEstimator.Count(ContextBundleBuilder.ReferenceCostLine(item)),
                AllocationTier: ContextBundleBuilder.ReferenceAllocationTier(item)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReferenceContextItem> selected =
            ContextPacker.PackAllocated(packCandidates, tokenBudget);
        phaseObserver?.Invoke("candidate_pack");
        Func<IReadOnlyList<ReferenceContextItem>, string> renderer = json
            ? selected => RenderReferenceJson(
                selected,
                built.AnchorDiagnostics,
                query,
                boundOptionalFields: false)
            : selected => RenderReferenceCompact(selected, built.AnchorDiagnostics, query);
        Func<IReadOnlyList<ReferenceContextItem>, string> boundedRenderer = json
            ? selected => RenderReferenceJson(
                selected,
                built.AnchorDiagnostics,
                query,
                boundOptionalFields: true)
            : selected => RenderReferenceCompact(selected, built.AnchorDiagnostics, query);
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

    internal static string BoundFinalOutput(string output, int tokenBudget, bool json)
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

    private sealed record ContextNextAction(string Call, string Reason);

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
            ContextEvidenceDisposition emptyDisposition = ContextBundleBuilder.DispositionFor(selected);
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

        ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionFor(selected);
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
            .Where(static item => ContextBundleBuilder.CarriesImplementationKind(item.Kind))
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
            .Where(static pivot => ContextBundleBuilder.CarriesImplementation(pivot.Symbol))
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
            ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionFor(selected);
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
            ContextEvidenceDisposition emptyDisposition = ContextBundleBuilder.DispositionForReference(selected);
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
        ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionForReference(selected);
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
            ContextEvidenceDisposition disposition = ContextBundleBuilder.DispositionForReference(selected);
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

    internal static string Truncate(string value, int max) => ContextTextBounds.Truncate(value, max);
}
