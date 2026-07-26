using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miller.Core.Graph;
using Miller.Core.References;
using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;
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
    private readonly IWorkspaceIndexProvider _workspaceProvider;
    private readonly ISemanticTextArm? _semanticArm;
    private readonly VectorSidecar? _semanticSidecar;

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
        VectorSidecar? semanticSidecar)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
        _semanticArm = semanticArm;
        _semanticSidecar = semanticSidecar;
    }

    [McpServerTool(Name = "context")]
    [Description(
        "First call in an UNFAMILIAR code area: give a task plus optional entry symbols, edited files, failing " +
        "test, or stack trace. Returns ranked pivots with bounded implementation snippets, neighbour signatures, " +
        "reasons, and an evidence disposition within token_budget; a next action appears only when evidence is " +
        "insufficient. When disposition is sufficient, answer from the bundle instead of inspecting every pivot. " +
        "NOT for: a symbol you can already name (inspect it) or text lookups (search). Example: " +
        "context query=\"how does workspace refresh converge the search sidecar\". Compact by default; " +
        "format=json to chain.")]
    public string Context(
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
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null,
        [Description("Workspace-relative files changed by the current task; their symbols rank as pivots. Optional.")]
        string[]? edited_files = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (token_budget <= 0)
                return string.Empty;

            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            int effectiveTokenBudget = Math.Min(token_budget, ToolOutputBudget.ContextMcpMaxTokens);
            WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            int bundleTokenBudget = Math.Max(
                0,
                effectiveTokenBudget -
                (compactBanner is null ? 0 : (int)TokenEstimator.Count(compactBanner + '\n')));
            int selectedCount;
            int candidatesExamined;
            string output;
            ReferenceMode parsedReferenceMode = ParseReferenceMode(reference_mode);
            IReadOnlyList<ContextSemanticSeed> semanticSeeds = LoadSemanticSeeds(
                context,
                query,
                parsedReferenceMode == ReferenceMode.Usage && exclude_tests);
            switch (parsedReferenceMode)
            {
                case ReferenceMode.Off:
                    output = RunActionable(
                        context.Index,
                        context.Index.Graph,
                        context.Resolver,
                        query,
                        bundleTokenBudget,
                        max_hops,
                        entry_symbols,
                        edited_files,
                        failing_test,
                        stack_trace,
                        semanticSeeds,
                        readBody: symbol => ReadPivotBody(
                            context.IndexDbPath,
                            context.WorkspaceRoot,
                            symbol),
                        json,
                        out selectedCount, out candidatesExamined);
                    break;
                case ReferenceMode.Usage:
                    output = RunReferenceAwareActionable(
                        context.Index,
                        context.Index.Graph,
                        context.Resolver,
                        query,
                        bundleTokenBudget,
                        max_hops,
                        entry_symbols,
                        edited_files,
                        failing_test,
                        stack_trace,
                        semanticSeeds,
                        readBody: symbol => ReadPivotBody(
                            context.IndexDbPath,
                            context.WorkspaceRoot,
                            symbol),
                        reference_depth, exclude_tests, json,
                        readReferenceEvidence: symbol => ReferenceEvidenceReader.Read(
                            context.IndexDbPath,
                            symbol.SymbolId,
                            new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol)),
                        readOutgoingEvidence: symbol => ReferenceEvidenceReader.ReadOutgoing(
                            context.IndexDbPath,
                            symbol.SymbolId,
                            new ReferenceEvidenceBounds(ReferenceRowsPerSymbol, ReferenceRowsPerSymbol)),
                        readContentChunks: (symbols, excludeTests) => ContentCorpusContextReader.ReadContainingSymbolChunks(
                            ContentCorpusSidecar.ContentDbPathFor(context.IndexDbPath),
                            symbols,
                            excludeTests,
                            ContentChunksPerSymbol),
                        out selectedCount, out candidatesExamined);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reference_mode));
            }
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
            return BoundFinalOutput(output, effectiveTokenBudget, json);
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
    internal const int ContentChunksPerSymbol = 2;
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

    private IReadOnlyList<ContextSemanticSeed> LoadSemanticSeeds(
        WorkspaceReadContext context,
        string query,
        bool excludeTests)
    {
        if (_semanticSidecar is not { Mode: SemanticMode.On } ||
            _semanticArm is null ||
            string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        SymbolCandidateSet lexical = SearchTool.CollectSymbolCandidates(
            context.Index,
            query,
            SearchToolMode.Symbol,
            limit: 2,
            excludeTests: excludeTests);
        var evidence = new LexicalEvidence(
            lexical.Candidates.Count,
            lexical.Candidates.Count > 0 ? lexical.Candidates[0].Score : 0,
            lexical.Candidates.Count > 1 ? lexical.Candidates[1].Score : 0);
        if (!SemanticQueryPolicy.Route(query, evidence).IsHybrid)
            return [];

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
                !seen.Add(symbolId) ||
                context.Index.FindBySymbolId(symbolId) is not { } symbol ||
                excludeTests && (symbol.IsTest || IsTestPath.Check(symbol.FilePath)))
            {
                continue;
            }

            seeds.Add(new ContextSemanticSeed(symbol, hit.Rank, hit.Cosine));
        }
        return seeds;
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
        out int candidatesExamined)
    {
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
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out candidatesExamined);

        candidates = AttachPivotBodies(candidates, tokenBudget, readBody);

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(anchorDiagnostics, tokenBudget, json);
        }

        var packCandidates = new List<PackCandidate<Candidate>>(candidates.Count);
        foreach (var c in candidates)
        {
            int cost = (int)TokenEstimator.Count(CompactCostLine(c));
            packCandidates.Add(new PackCandidate<Candidate>(
                c,
                cost,
                AllocationTier: c.IsPivot ? 0 : 2));
        }

        IReadOnlyList<Candidate> selected = ContextPacker.PackAllocated(packCandidates, tokenBudget);
        Func<IReadOnlyList<Candidate>, string> renderer = json
            ? selected => RenderJson(selected, anchorDiagnostics, boundOptionalFields: false)
            : selected => RenderCompact(selected, anchorDiagnostics);
        Func<IReadOnlyList<Candidate>, string> boundedRenderer = json
            ? selected => RenderJson(selected, anchorDiagnostics, boundOptionalFields: true)
            : selected => RenderCompact(selected, anchorDiagnostics);
        return RenderWithinBudget(selected, tokenBudget, renderer, boundedRenderer, out selectedCount);
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
            readBody: null,
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
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody,
        int referenceDepth,
        bool excludeTests,
        bool json,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount,
        out int candidatesExamined)
    {
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
            out IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
            out candidatesExamined);

        candidates = AttachPivotBodies(candidates, tokenBudget, readBody);

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return RenderNoPivots(anchorDiagnostics, tokenBudget, json);
        }

        IReadOnlyList<ReferenceContextItem> items = BuildReferenceItems(
            candidates,
            referenceDepth,
            excludeTests,
            readReferenceEvidence,
            readOutgoingEvidence,
            readContentChunks);
        var packCandidates = new List<PackCandidate<ReferenceContextItem>>(items.Count);
        foreach (ReferenceContextItem item in items)
            packCandidates.Add(new PackCandidate<ReferenceContextItem>(
                item,
                (int)TokenEstimator.Count(ReferenceCostLine(item)),
                AllocationTier: ReferenceAllocationTier(item)));

        IReadOnlyList<ReferenceContextItem> selected =
            ContextPacker.PackAllocated(packCandidates, tokenBudget);
        Func<IReadOnlyList<ReferenceContextItem>, string> renderer = json
            ? selected => RenderReferenceJson(selected, anchorDiagnostics, boundOptionalFields: false)
            : selected => RenderReferenceCompact(selected, anchorDiagnostics);
        Func<IReadOnlyList<ReferenceContextItem>, string> boundedRenderer = json
            ? selected => RenderReferenceJson(selected, anchorDiagnostics, boundOptionalFields: true)
            : selected => RenderReferenceCompact(selected, anchorDiagnostics);
        return RenderWithinBudget(selected, tokenBudget, renderer, boundedRenderer, out selectedCount);
    }

    internal static string RunReferenceAware(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        int referenceDepth, bool excludeTests, bool json,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readReferences,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readCallees,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount, out int candidatesExamined) =>
        RunReferenceAware(
            index,
            graph,
            resolver,
            query,
            tokenBudget,
            maxHops,
            entrySymbols,
            failingTest,
            stackTrace,
            referenceDepth,
            excludeTests,
            json,
            symbol => LegacyInboundEvidence(symbol, readReferences(symbol)),
            symbol => LegacyOutgoingEvidence(symbol, readCallees(symbol)),
            readContentChunks,
            out selectedCount,
            out candidatesExamined);

    private static string RenderWithinBudget<T>(
        IReadOnlyList<T> initiallySelected,
        int tokenBudget,
        Func<IReadOnlyList<T>, string> renderer,
        Func<IReadOnlyList<T>, string> boundedRenderer,
        out int selectedCount)
    {
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

    private sealed record ContextEvidenceDisposition(string Status, string Reason);

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
        out int candidatesExamined)
    {
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
            SymbolCandidateSet retrieved = SearchTool.CollectSymbolCandidates(
                index,
                query,
                SearchToolMode.Symbol,
                SearchSeedLimit,
                excludeTests: null);
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

            foreach (string term in queryTerms)
            {
                SymbolCandidateSet termCandidates = SearchTool.CollectSymbolCandidates(
                    index,
                    term,
                    SearchToolMode.Symbol,
                    limit: 6,
                    excludeTests: null);
                int termCandidateCount = Math.Min(termCandidates.Candidates.Count, 6);
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
                            TaskQueryAffinity(symbol, queryTerms),
                            $"query_term_{term}");
                    }
                }
            }
        }

        if (entrySymbols is not null)
        {
            foreach (string entry in entrySymbols)
            {
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

        if (semanticSeeds is not null)
        {
            foreach (ContextSemanticSeed seed in semanticSeeds.Where(static seed => IsQueryPivot(seed.Symbol)))
            {
                IndexedSymbol symbol = PreferDefinitionPivot(index, seed.Symbol);
                AddSignal(
                    symbol,
                    SearchSeedLimit + seed.Rank,
                    seed.Score,
                    0,
                    $"semantic_rank_{seed.Rank}");
            }
        }

        anchorDiagnostics = diagnostics;
        IReadOnlyList<ContextPivot> pivots = ContextPivotRanker.Rank(signals, limit: 4);
        if (pivots.Count == 0)
            return Array.Empty<Candidate>();

        string[] pivotIds = pivots.Select(static pivot => pivot.SymbolId).ToArray();
        IReadOnlyList<ReachedNode> reached = graph.Reach(pivotIds, maxHops, ReachCap, Direction.Both);
        var candidates = new List<Candidate>(pivotIds.Length + reached.Count);
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(
            index,
            pivotIds.Concat(reached.Select(static node => node.Id)));

        var pivotSymbols = new List<IndexedSymbol>(pivotIds.Length);
        foreach (string pivotId in pivotIds)
        {
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
                int added = 0;
                foreach (IndexedSymbol symbol in index.FindByFilePath(pivot.FilePath)
                             .Where(static symbol => IsQueryPivot(symbol))
                             .OrderByDescending(symbol => TaskQueryAffinity(symbol, queryTerms))
                             .ThenBy(symbol => LineDistance(symbol, pivot.StartLine))
                             .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
                {
                    if (!seenNeighbourIds.Add(symbol.SymbolId))
                        continue;
                    scoredReached.Add((symbol, 1, scorer.Score(symbol), "file_neighbour"));
                    added++;
                    if (added == 2)
                        break;
                }
            }
        }
        foreach (var entry in scoredReached
                     .OrderBy(static candidate => candidate.Hop)
                     .ThenByDescending(static candidate => candidate.Score)
                     .ThenBy(static candidate => candidate.Symbol.SymbolId, StringComparer.Ordinal))
            candidates.Add(new Candidate(entry.Symbol, entry.Hop, entry.Reason));

        candidatesExamined = candidates.Count;
        return candidates;
    }

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

    private static int TaskQueryAffinity(IndexedSymbol symbol, IReadOnlyList<string> terms)
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
                score += 15;
            else if (names.Contains(term))
            {
                score += 10;
                matchedNameTerms++;
            }
            else if (signatures.Contains(term))
                score += 5;
            else if (paths.Contains(term))
                score += 12;
        }
        if (matchedNameTerms > 1)
            score += (matchedNameTerms - 1) * 10;
        return Math.Min(score, 50);
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
        Func<IndexedSymbol, ExtractReader.BodyReadResult>? readBody)
    {
        if (readBody is null)
            return candidates;

        int pivotCount = candidates.Count(static candidate => candidate.IsPivot);
        if (pivotCount == 0)
            return candidates;

        int maxBodyChars = Math.Min(2400, Math.Max(80, tokenBudget * 2 / pivotCount));
        var enriched = new Candidate[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            Candidate candidate = candidates[index];
            if (!candidate.IsPivot)
            {
                enriched[index] = candidate;
                continue;
            }

            ExtractReader.BodyReadResult body = readBody(candidate.Symbol);
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
        string indexDbPath,
        string workspaceRoot,
        IndexedSymbol symbol)
    {
        try
        {
            return ExtractReader.ReadBody(indexDbPath, workspaceRoot, symbol);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ExtractReader.BodyReadResult.Unavailable(
                ExtractReader.BodyUnavailableReason.FileHashUnavailable);
        }
    }

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
        int referenceDepth,
        bool excludeTests,
        Func<IndexedSymbol, ReferenceEvidenceSet> readReferenceEvidence,
        Func<IndexedSymbol, OutgoingReferenceEvidenceSet> readOutgoingEvidence,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks)
    {
        var items = new List<ReferenceContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usableCandidates = candidates
            .Where(candidate =>
                !excludeTests ||
                !(candidate.Symbol.IsTest || IsTestPath.Check(candidate.Symbol.FilePath)))
            .ToArray();

        foreach (Candidate candidate in usableCandidates)
        {
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
        foreach (TextContentSearchHit hit in readContentChunks(symbols, excludeTests))
        {
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

        if (referenceDepth >= 1)
        {
            foreach (Candidate candidate in usableCandidates)
            {
                IndexedSymbol symbol = candidate.Symbol;
                OutgoingReferenceEvidenceSet outgoing = readOutgoingEvidence(symbol);
                foreach (OutgoingReferenceEvidence callee in outgoing.Exact)
                {
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

                ReferenceEvidenceSet inbound = readReferenceEvidence(symbol);
                foreach (ReferenceEvidence reference in inbound.Exact)
                {
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
        RenderCompact(selected, []);

    private static string RenderCompact(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics)
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

        if (pivots.Count > 0 && disposition.Status != "sufficient")
        {
            sb.Append("## next inspect\n");
            int inspectCount = Math.Min(NextInspectCount, pivots.Count);
            for (int i = 0; i < inspectCount; i++)
                sb.Append(NextInspectLine(pivots[i].Symbol)).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string NextInspectLine(IndexedSymbol symbol) =>
        NextInspectLine(symbol.Name, symbol.FilePath);

    private static string NextInspectLine(string name, string filePath) =>
        "inspect(target=\"" + EscapeCallString(name) +
        "\", scope=\"" + EscapeCallString(filePath) +
        "\", depth=\"overview\")";

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
        RenderJson(selected, [], boundOptionalFields: false);

    private static string RenderBoundedJson(IReadOnlyList<Candidate> selected) =>
        RenderJson(selected, [], boundOptionalFields: true);

    private static string RenderJson(
        IReadOnlyList<Candidate> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
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
                    .Take(NextInspectCount)
                    .ToArray();
                if (pivots.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (Candidate pivot in pivots)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", NextInspectLine(pivot.Symbol));
                        w.WriteString("reason", "inspect a pivot implementation");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ReferenceEvidenceSet LegacyInboundEvidence(
        IndexedSymbol target,
        IReadOnlyList<SymbolRef> references)
    {
        ReferenceEvidence[] fallback = references
            .Select(reference => new ReferenceEvidence(
                null,
                reference.ContainingSymbolId,
                reference.FilePath,
                reference.StartLine,
                null,
                null,
                null,
                null,
                null,
                ReferenceEvidenceReader.NormalizeKind(reference.Kind),
                reference.Kind,
                ReferenceEvidenceSource.NameFallback,
                null,
                0.5,
                ReferenceResolutionStatus.Fallback))
            .ToArray();
        return new ReferenceEvidenceSet(
            [],
            fallback,
            new ReferenceEvidenceCoverage(
                0,
                0,
                0,
                fallback.Length,
                fallback.Length,
                1,
                false,
                false,
                fallback.Length == 0
                    ? ReferenceFallbackStatus.NoCandidates
                    : ReferenceFallbackStatus.Available));
    }

    private static OutgoingReferenceEvidenceSet LegacyOutgoingEvidence(
        IndexedSymbol containing,
        IReadOnlyList<SymbolRef> references)
    {
        OutgoingReferenceEvidence[] fallback = references
            .Select(reference => new OutgoingReferenceEvidence(
                containing.SymbolId,
                null,
                reference.Name,
                reference.FilePath,
                reference.StartLine,
                null,
                null,
                null,
                null,
                null,
                ReferenceEvidenceReader.NormalizeKind(reference.Kind),
                reference.Kind,
                ReferenceEvidenceSource.NameFallback,
                null,
                0.5,
                ReferenceResolutionStatus.Fallback))
            .ToArray();
        return new OutgoingReferenceEvidenceSet(
            [],
            fallback,
            new OutgoingReferenceEvidenceCoverage(
                0,
                0,
                0,
                fallback.Length,
                fallback.Length,
                false,
                false));
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
        RenderReferenceCompact(selected, []);

    private static string RenderReferenceCompact(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics)
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
        if (disposition.Status != "sufficient")
        {
            ReferenceContextItem[] pivots = selected
                .Where(static item => item.ItemType == "symbol" && item.Role == "pivot")
                .Take(NextInspectCount)
                .ToArray();
            if (pivots.Length > 0)
            {
                sb.Append("## next inspect\n");
                foreach (ReferenceContextItem pivot in pivots)
                    sb.Append(NextInspectLine(pivot.Name, pivot.File)).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderReferenceJson(IReadOnlyList<ReferenceContextItem> selected) =>
        RenderReferenceJson(selected, [], boundOptionalFields: false);

    private static string RenderBoundedReferenceJson(IReadOnlyList<ReferenceContextItem> selected) =>
        RenderReferenceJson(selected, [], boundOptionalFields: true);

    private static string RenderReferenceJson(
        IReadOnlyList<ReferenceContextItem> selected,
        IReadOnlyList<ContextAnchorDiagnostic> anchorDiagnostics,
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
                ReferenceContextItem[] pivots = selected
                    .Where(static item => item.ItemType == "symbol" && item.Role == "pivot")
                    .Take(NextInspectCount)
                    .ToArray();
                if (pivots.Length > 0)
                {
                    w.WritePropertyName("next_actions");
                    w.WriteStartArray();
                    foreach (ReferenceContextItem pivot in pivots)
                    {
                        w.WriteStartObject();
                        w.WriteString("call", NextInspectLine(pivot.Name, pivot.File));
                        w.WriteString("reason", "inspect a pivot implementation");
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
                IsAuthoritativeImplementationReason(candidate.Reason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        if (selected.Any(static candidate => candidate.IsPivot))
            return new ContextEvidenceDisposition("partial", "pivot_signature_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
    }

    private static ContextEvidenceDisposition DispositionForReference(
        IReadOnlyList<ReferenceContextItem> selected)
    {
        if (selected.Any(static item =>
                item.ItemType == "implementation" &&
                IsAuthoritativeImplementationReason(item.AnchorReason)))
            return new ContextEvidenceDisposition("sufficient", "pivot_implementation_present");
        if (selected.Any(static item => item.ItemType == "content_chunk" && item.Confidence == "exact"))
            return new ContextEvidenceDisposition("sufficient", "exact_containing_content_present");
        if (selected.Any(static item => item.ItemType == "symbol"))
            return new ContextEvidenceDisposition("partial", "symbol_and_relation_evidence_only");
        return new ContextEvidenceDisposition("insufficient", "no_pivot_rendered");
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
