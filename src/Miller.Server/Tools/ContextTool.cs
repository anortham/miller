using System.ComponentModel;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
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
            return ContextBundleRenderer.RenderNoPivots(built.AnchorDiagnostics, tokenBudget, json);
        }

        IReadOnlyList<Candidate> selected = ContextBundleRenderer.SelectOrdinary(
            built.Candidates,
            tokenBudget,
            cancellationToken);
        phaseObserver?.Invoke("candidate_pack");
        string output = ContextBundleRenderer.RenderOrdinary(
            selected,
            built.AnchorDiagnostics,
            query,
            tokenBudget,
            json,
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
            (items, diagnostics, renderedQuery) => ContextBundleRenderer.EstimateReferenceTokens(
                items,
                diagnostics,
                renderedQuery,
                json),
            cancellationToken,
            phaseObserver,
            retrieval);
        candidatesExamined = built.CandidatesExamined;
        if (built.Candidates.Count == 0)
        {
            selectedCount = 0;
            return ContextBundleRenderer.RenderNoPivots(built.AnchorDiagnostics, tokenBudget, json);
        }

        referenceReadObserver?.Invoke(built.ReadCounts);
        phaseObserver?.Invoke("reference_items");

        IReadOnlyList<ReferenceContextItem> selected = ContextBundleRenderer.SelectReference(
            built.Items,
            tokenBudget,
            cancellationToken);
        phaseObserver?.Invoke("candidate_pack");
        string output = ContextBundleRenderer.RenderReference(
            selected,
            built.AnchorDiagnostics,
            query,
            tokenBudget,
            json,
            out selectedCount,
            cancellationToken);
        phaseObserver?.Invoke("bounded_render");
        return output;
    }

    internal static string BoundFinalOutput(string output, int tokenBudget, bool json) =>
        ContextBundleRenderer.BoundFinalOutput(output, tokenBudget, json);
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

    private static string EscapeDiagnosticQuery(string value) =>
        ToolDiagnosticText.EscapeCallArgument(value);

    internal static string Truncate(string value, int max) => ContextTextBounds.Truncate(value, max);
}
