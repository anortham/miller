using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miller.Core.Graph;
using Miller.Core.Search;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>context</c> tool (miller-toolbox.md §3, M5 D6): a task-anchored, token-budgeted bundle of the most
/// relevant code for a question. Seeds from the BM25 search of <c>query</c>, unioned with any resolved
/// <c>entry_symbols</c> and the symbol-name tokens parsed from a <c>failing_test</c> / <c>stack_trace</c> hint
/// (scenario hints folded into seeds — the "mode-switch without a mode enum" the toolbox intends), expands both
/// directions over the in-memory dependency graph to <c>max_hops</c> (0–2), then greedily packs the candidates
/// within <c>token_budget</c> in priority order (seed rank, then hop, then id). All in-memory — the sub-100ms
/// target julie's 439ms get_context missed (D6 perf mandate).
///
/// <para>This is the thin MCP/DI/telemetry shell; the pure, DB-free <see cref="Run"/> core (mirroring
/// <see cref="InspectTool.Run"/>) holds the correctness and is unit-tested. Token cost is computed here by the
/// Server's <see cref="TokenEstimator"/> over a conservative per-candidate render line and handed to the pure
/// <see cref="ContextPacker"/> (D8 — cost in, Core stays pure). Reads the live <see cref="IndexHolder"/> per
/// call (M3 step 10).</para>
/// </summary>
[McpServerToolType]
public sealed partial class ContextTool
{
    private readonly IWorkspaceIndexProvider _workspaceProvider;

    /// <summary>Construct over the live index holder (production / freshness-aware). Unlike inspect, context's
    /// <see cref="Run"/> core is DB-free (search + graph over the in-memory index), so it takes no
    /// WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ContextTool(IWorkspaceIndexProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
    }

    [McpServerTool(Name = "context")]
    [Description(
        "First call in an unfamiliar area: a small, justified bundle — the most relevant entry points and why, plus " +
        "the next symbols to inspect. Give the task or question (optionally a failing test or stack trace) and get a " +
        "bounded set of seed symbols with one-line reasons, capped neighbours, and a 'next inspect' footer. If you " +
        "already know the symbol, use inspect. Returns compact text by default; pass format=json to chain.")]
    public string Context(
        [Description("The task or question to anchor the bundle on.")] string query,
        [Description("Hard bound on the returned bundle size, in estimated tokens. Default 2000.")]
        int token_budget = 2000,
        [Description("Neighbour expansion radius in hops (0–2). Default 1.")] int max_hops = 1,
        [Description("Seed symbol names/ids to fold into the bundle. Optional.")] string[]? entry_symbols = null,
        [Description("A failing test name/snippet; its symbol tokens are folded into the seeds. Optional.")]
        string? failing_test = null,
        [Description("A stack trace; its symbol tokens are folded into the seeds. Optional.")]
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
        bool? ensure_fresh = null)
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            int selectedCount;
            int candidatesExamined;
            string output;
            ReferenceMode parsedReferenceMode = ParseReferenceMode(reference_mode);
            switch (parsedReferenceMode)
            {
                case ReferenceMode.Off:
                    output = Run(context.Index, context.Resolver,
                        query, token_budget, max_hops, entry_symbols, failing_test, stack_trace, json,
                        out selectedCount, out candidatesExamined);
                    break;
                case ReferenceMode.Usage:
                    output = RunReferenceAware(context.Index, context.Index.Graph, context.Resolver,
                        query, token_budget, max_hops, entry_symbols, failing_test, stack_trace,
                        reference_depth, exclude_tests, json,
                        readReferences: symbol => ExtractReader.ReadReferences(context.IndexDbPath, symbol.Name).Take(ReferenceRowsPerSymbol).ToArray(),
                        readCallees: symbol => ExtractReader.ReadCallees(context.IndexDbPath, symbol.SymbolId).Take(ReferenceRowsPerSymbol).ToArray(),
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

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = parsedReferenceMode == ReferenceMode.Usage ? "usage" : "off";
                telemetry.SetTarget(query);
                telemetry.ResultCount = selectedCount;
                // D10 work proxy (bytes_examined ≈ nodes visited): the candidate set (seeds + reached) the packer
                // considered, before the budget truncated it.
                telemetry.BytesExamined = candidatesExamined;
                telemetry.Outcome = selectedCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
                telemetry.SetMetadata("format", json ? "json" : "compact");
                telemetry.SetMetadata("token_budget_bucket", TokenBudgetBucket(token_budget));
                telemetry.SetMetadata("max_hops_bucket", HopsBucket(max_hops));
                telemetry.SetMetadata("has_entry_symbols", entry_symbols is { Length: > 0 });
                telemetry.SetMetadata("has_failing_test", !string.IsNullOrWhiteSpace(failing_test));
                telemetry.SetMetadata("has_stack_trace", !string.IsNullOrWhiteSpace(stack_trace));
                telemetry.SetMetadata("reference_depth_bucket", HopsBucket(reference_depth));
                telemetry.SetMetadata("exclude_tests", exclude_tests);
                if (selectedCount == 0)
                    telemetry.SetEmptyReason("no_context_symbols");
            }
            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.SetError(ex);
            }
            return $"context failed: {ex.Message}";
        }
    }

    private const int SignatureMaxLength = 110;
    private const int SearchSeedLimit = 10; // BM25 seed cap for bundle construction, not the rendered search page.
    // A generous internal reach cap so the budget — not an arbitrary count — bounds the bundle. The token pack
    // is the real limiter; this only guards against a pathological fan-out feeding the packer a huge candidate set.
    private const int ReachCap = 500;
    internal const int ReferenceRowsPerSymbol = 12;
    internal const int ContentChunksPerSymbol = 2;

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

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry; no DB — search + graph are in-memory). Builds the ordered
    /// seed set (search ∪ entry_symbols ∪ failing_test/stack_trace tokens), expands both directions to
    /// <paramref name="maxHops"/> (clamped 0–2), orders candidates by (seed rank, hop, id), costs each render
    /// line with the token estimator, and packs within <paramref name="tokenBudget"/>. <paramref name="selectedCount"/>
    /// is the number of symbols in the returned bundle; an unanchorable query (no seeds) yields 0 + a note.
    /// <paramref name="candidatesExamined"/> is the total candidate set (seeds + reached neighbours) the packer
    /// considered before the budget truncated it — the D10 <c>bytes_examined ≈ nodes visited</c> work proxy; a
    /// no-seed query leaves it 0.
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
        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index, graph, resolver, query, maxHops, entrySymbols, failingTest, stackTrace, out candidatesExamined);

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return json
                ? "{\"note\":\"no seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.\",\"bundle\":[]}"
                : "No seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.";
        }

        // --- 4. Cost each candidate conservatively, then pack (D6). The compact renderer groups selected rows by
        // file path, so per-candidate costing intentionally includes the file path even though the grouped output
        // prints it once per file. This keeps packing under budget while the rendered output gets the token savings.
        var packCandidates = new List<PackCandidate<Candidate>>(candidates.Count);
        foreach (var c in candidates)
        {
            int cost = (int)TokenEstimator.Count(CompactCostLine(c));
            packCandidates.Add(new PackCandidate<Candidate>(c, cost));
        }

        IReadOnlyList<Candidate> selected = ContextPacker.Pack(packCandidates, tokenBudget);
        selectedCount = selected.Count;

        return json ? RenderJson(selected) : RenderCompact(selected);
    }

    internal static string RunReferenceAware(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int tokenBudget, int maxHops,
        IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        int referenceDepth, bool excludeTests, bool json,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readReferences,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readCallees,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks,
        out int selectedCount, out int candidatesExamined)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(readReferences);
        ArgumentNullException.ThrowIfNull(readCallees);
        ArgumentNullException.ThrowIfNull(readContentChunks);

        if (referenceDepth < 0) referenceDepth = 0;
        if (referenceDepth > 1) referenceDepth = 1;

        IReadOnlyList<Candidate> candidates = BuildCandidates(
            index, graph, resolver, query, maxHops, entrySymbols, failingTest, stackTrace, out candidatesExamined);

        if (candidates.Count == 0)
        {
            selectedCount = 0;
            return json
                ? "{\"note\":\"no seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.\",\"bundle\":[]}"
                : "No seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.";
        }

        IReadOnlyList<ReferenceContextItem> items = BuildReferenceItems(
            candidates, referenceDepth, excludeTests, readReferences, readCallees, readContentChunks);
        var packCandidates = new List<PackCandidate<ReferenceContextItem>>(items.Count);
        foreach (ReferenceContextItem item in items)
            packCandidates.Add(new PackCandidate<ReferenceContextItem>(item, (int)TokenEstimator.Count(ReferenceCostLine(item))));

        IReadOnlyList<ReferenceContextItem> selected = ContextPacker.Pack(packCandidates, tokenBudget);
        selectedCount = selected.Count;
        return json ? RenderReferenceJson(selected) : RenderReferenceCompact(selected);
    }

    /// <summary>One member of the context bundle: a symbol and its hop distance from the nearest seed (0 = seed).</summary>
    private readonly record struct Candidate(IndexedSymbol Symbol, int Hop);

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
        string? Snippet = null);

    private static IReadOnlyList<Candidate> BuildCandidates(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string query, int maxHops, IReadOnlyList<string>? entrySymbols, string? failingTest, string? stackTrace,
        out int candidatesExamined)
    {
        if (maxHops < 0) maxHops = 0;
        if (maxHops > 2) maxHops = 2; // D6: max_hops range 0–2
        candidatesExamined = 0;

        // --- 1. Build the ordered seed id list (D6). Order = search rank, then entry_symbols, then hint tokens.
        // A seed is recorded once (first occurrence wins its rank). Each seed becomes a hop-0 candidate. ---
        var seedRank = new Dictionary<string, int>(StringComparer.Ordinal);
        var seedOrder = new List<string>();

        void AddSeed(string id)
        {
            if (seedRank.TryAdd(id, seedOrder.Count))
                seedOrder.Add(id);
        }

        // 1a. BM25 search seeds (only when the query is non-blank — a blank query simply contributes no seeds).
        if (!string.IsNullOrWhiteSpace(query))
        {
            foreach (var hit in index.Search(query, SearchSeedLimit, SearchMode.Or))
                AddSeed(index.Resolve(hit.Document.DocId).SymbolId);
        }

        // 1b. Resolved entry_symbols. Each is smart-resolved; only a unique symbol resolution contributes a seed
        // (a file / ambiguous / not-found entry is skipped — entry_symbols are symbol anchors, not file anchors).
        if (entrySymbols is not null)
        {
            foreach (var entry in entrySymbols)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;
                if (resolver.Resolve(entry) is TargetResolution.Symbol sym)
                    AddSeed(sym.Value.SymbolId);
            }
        }

        // 1c. Symbol-name tokens parsed from the failing_test / stack_trace hints. An identifier-like token that
        // names an indexed symbol is folded in (homonyms add every matching id — over-include, like impact's D2).
        foreach (var token in ExtractIdentifierTokens(failingTest).Concat(ExtractIdentifierTokens(stackTrace)))
        {
            foreach (var match in index.FindByName(token))
                AddSeed(match.SymbolId);
        }

        if (seedOrder.Count == 0)
            return Array.Empty<Candidate>();

        // --- 2. Expand both directions to maxHops. Reach excludes the starts and returns min-hop per node. ---
        IReadOnlyList<ReachedNode> reached =
            graph.Reach(seedOrder, maxHops, ReachCap, Direction.Both);

        // --- 3. Build the candidate list in priority order: seeds (hop 0, in seed rank) then reached (hop, id).
        // Reach already orders the reached nodes by (hop asc, id asc), so appending preserves that. ---
        var candidates = new List<Candidate>(seedOrder.Count + reached.Count);
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(
            index,
            seedOrder.Concat(reached.Select(static node => node.Id)));
        foreach (var seedId in seedOrder)
        {
            if (symbolsById.TryGetValue(seedId, out IndexedSymbol? symbol)) // defensive — a seed id always comes from the index
                candidates.Add(new Candidate(symbol, Hop: 0));
        }
        foreach (var node in reached)
        {
            if (symbolsById.TryGetValue(node.Id, out IndexedSymbol? symbol))
                candidates.Add(new Candidate(symbol, node.Hop));
        }

        // D10 work proxy: the full candidate set the packer considered (seeds + reached), before truncation.
        candidatesExamined = candidates.Count;
        return candidates;
    }

    private static IReadOnlyList<ReferenceContextItem> BuildReferenceItems(
        IReadOnlyList<Candidate> candidates,
        int referenceDepth,
        bool excludeTests,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readReferences,
        Func<IndexedSymbol, IReadOnlyList<SymbolRef>> readCallees,
        Func<IReadOnlyList<IndexedSymbol>, bool, IReadOnlyList<TextContentSearchHit>> readContentChunks)
    {
        var items = new List<ReferenceContextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usableCandidates = candidates
            .Where(candidate => !excludeTests || !candidate.Symbol.IsTest)
            .ToArray();

        foreach (Candidate candidate in usableCandidates)
        {
            IndexedSymbol symbol = candidate.Symbol;
            AddItem(new ReferenceContextItem(
                ItemType: "symbol",
                Reason: candidate.Hop == 0 ? "definition" : "graph_neighbor",
                Confidence: "exact",
                Name: symbol.Name,
                Kind: symbol.Kind,
                File: symbol.FilePath,
                Line: symbol.StartLine,
                Hop: candidate.Hop,
                Signature: symbol.Signature,
                SymbolId: symbol.SymbolId));
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
                foreach (SymbolRef callee in readCallees(symbol))
                {
                    if (excludeTests && IsTestPath.Check(callee.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: "callee_identifier",
                        Confidence: "containing_symbol",
                        Name: callee.Name,
                        Kind: callee.Kind,
                        File: callee.FilePath,
                        Line: callee.StartLine,
                        ContainingSymbolId: callee.ContainingSymbolId));
                }

                foreach (SymbolRef reference in readReferences(symbol))
                {
                    if (excludeTests && IsTestPath.Check(reference.FilePath))
                        continue;
                    AddItem(new ReferenceContextItem(
                        ItemType: "identifier",
                        Reason: "possible_reference",
                        Confidence: "name_based",
                        Name: reference.Name,
                        Kind: reference.Kind,
                        File: reference.FilePath,
                        Line: reference.StartLine,
                        ContainingSymbolId: reference.ContainingSymbolId));
                }
            }
        }

        return items;

        void AddItem(ReferenceContextItem item)
        {
            string key = item.ItemType switch
            {
                "symbol" => "symbol:" + item.SymbolId,
                "content_chunk" => "chunk:" + item.SourceId + ":" + item.ChunkId,
                "identifier" => "identifier:" + item.Reason + ":" + item.File + ":" + item.Line + ":" + item.Name + ":" + item.Kind + ":" + item.ContainingSymbolId,
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

    // An identifier token: a letter/underscore start then letters/digits/underscores. Dots, parens, colons,
    // whitespace, and line/file noise are separators, so dotted frames split into their component names.
    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    // ---------- rendering ----------

    // Conservative per-candidate cost line: includes the file path even though compact output groups by file.
    private static string CompactCostLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine)
          .Append("  hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        return sb.ToString();
    }

    private static string GroupedCandidateLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append("  :").Append(s.StartLine).Append(' ')
          .Append(s.Name).Append(' ')
          .Append(s.Kind);
        if (c.Hop > 0) // hop=0 is the seed/definition itself — the label only earns its tokens for neighbors
            sb.Append(" hop=").Append(c.Hop);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        return sb.ToString();
    }

    private const int MaxNeighbourCandidates = 12;
    private const int NextInspectCount = 3;

    private static string RenderCompact(IReadOnlyList<Candidate> selected)
    {
        if (selected.Count == 0)
            return "Bundle empty — raise token_budget.";

        // The packer preserves caller priority order (seed rank, then hop, then id), so hop-0 seeds lead. Partition
        // by hop so the render is opinionated: seeds first as the named entry points, neighbours after, capped.
        var seeds = new List<Candidate>();
        var neighbours = new List<Candidate>();
        foreach (Candidate candidate in selected)
        {
            if (candidate.Hop == 0)
                seeds.Add(candidate);
            else
                neighbours.Add(candidate);
        }

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");

        if (seeds.Count > 0)
        {
            sb.Append("## seeds\n");
            foreach (Candidate seed in seeds)
                sb.Append(SeedLine(seed)).Append('\n');
        }

        if (neighbours.Count > 0)
        {
            sb.Append("## neighbours\n");
            int renderCap = Math.Min(MaxNeighbourCandidates, neighbours.Count);
            int omitted = neighbours.Count - renderCap;
            var groups = new List<(string FilePath, List<Candidate> Candidates)>();
            for (int i = 0; i < renderCap; i++)
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

            if (omitted > 0)
                sb.Append("... ").Append(omitted).Append(" more neighbours omitted — inspect a seed for the full graph.\n");
        }

        if (seeds.Count > 0)
        {
            sb.Append("## next inspect\n");
            int inspectCount = Math.Min(NextInspectCount, seeds.Count);
            for (int i = 0; i < inspectCount; i++)
            {
                var s = seeds[i].Symbol;
                sb.Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    // A hop-0 seed gets one line with its reason ("seed") and full provenance — it is the anchor the bundle is built
    // around, so it earns the file:line inline (neighbours are grouped by file and only carry a hop label).
    private static string SeedLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine).Append("  seed");
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        return sb.ToString();
    }

    private static string RenderJson(IReadOnlyList<Candidate> selected)
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
                w.WriteString("name", s.Name);
                w.WriteString("kind", s.Kind);
                w.WriteString("file", s.FilePath);
                w.WriteNumber("line", s.StartLine);
                w.WriteNumber("hop", c.Hop);
                if (s.Signature is null) w.WriteNull("signature");
                else w.WriteString("signature", s.Signature);
                w.WriteString("symbol_id", s.SymbolId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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
            sb.Append(' ').Append(Truncate(item.Signature!, SignatureMaxLength));
        if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append(' ').Append(Truncate(item.Snippet!, SignatureMaxLength));
        return sb.ToString();
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
            sb.Append("  ").Append(Truncate(item.Signature!, SignatureMaxLength));
        else if (!string.IsNullOrEmpty(item.Snippet))
            sb.Append("  ").Append(Truncate(item.Snippet!, SignatureMaxLength));
        return sb.ToString();
    }

    private static string RenderReferenceCompact(IReadOnlyList<ReferenceContextItem> selected)
    {
        if (selected.Count == 0)
            return "Bundle empty — raise token_budget.";

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

        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderReferenceJson(IReadOnlyList<ReferenceContextItem> selected)
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
                else w.WriteString("signature", item.Signature);
                if (item.SymbolId is not null)
                    w.WriteString("symbol_id", item.SymbolId);
                if (item.ContainingSymbolId is not null)
                    w.WriteString("containing_symbol_id", item.ContainingSymbolId);
                if (item.SourceId is not null)
                    w.WriteString("source_id", item.SourceId);
                if (item.ChunkId is not null)
                    w.WriteString("chunk_id", item.ChunkId);
                if (item.LineStart is int lineStart)
                    w.WriteNumber("line_start", lineStart);
                if (item.LineEnd is int lineEnd)
                    w.WriteNumber("line_end", lineEnd);
                if (item.Snippet is not null)
                    w.WriteString("snippet", item.Snippet);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
