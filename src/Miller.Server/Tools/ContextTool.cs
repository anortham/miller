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
/// Server's <see cref="TokenEstimator"/> over each candidate's exact render line and handed to the pure
/// <see cref="ContextPacker"/> (D8 — cost in, Core stays pure). Reads the live <see cref="IndexHolder"/> per
/// call (M3 step 10).</para>
/// </summary>
[McpServerToolType]
public sealed partial class ContextTool
{
    private readonly IndexHolder _holder;
    private readonly SmartTargetResolver _resolver;

    /// <summary>Construct over the live index holder (production / freshness-aware). Unlike inspect, context's
    /// <see cref="Run"/> core is DB-free (search + graph over the in-memory index), so it takes no
    /// WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ContextTool(IndexHolder holder, SmartTargetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(resolver);
        _holder = holder;
        _resolver = resolver;
    }

    [McpServerTool(Name = "context")]
    [Description(
        "Assemble a token-budgeted bundle of the most relevant code for a task or question. Give a description " +
        "of what you're working on — optionally a failing test or stack trace — and get a bounded set of the " +
        "most relevant symbols and signatures with provenance. Use for orientation in an unfamiliar area; if " +
        "you already know the symbol, use inspect. Returns compact text by default; pass format=json to chain.")]
    public string Context(
        [Description("The task or question to anchor the bundle on.")] string query,
        [Description("Hard bound on the returned bundle size, in estimated tokens. Default 4000.")]
        int token_budget = 4000,
        [Description("Neighbour expansion radius in hops (0–2). Default 1.")] int max_hops = 1,
        [Description("Seed symbol names/ids to fold into the bundle. Optional.")] string[]? entry_symbols = null,
        [Description("A failing test name/snippet; its symbol tokens are folded into the seeds. Optional.")]
        string? failing_test = null,
        [Description("A stack trace; its symbol tokens are folded into the seeds. Optional.")]
        string? stack_trace = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact")
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            string output = Run(_holder.Current, _resolver,
                query, token_budget, max_hops, entry_symbols, failing_test, stack_trace, json,
                out int selectedCount, out int candidatesExamined);

            if (telemetry is not null)
            {
                telemetry.SetTarget(query);
                telemetry.ResultCount = selectedCount;
                // D10 work proxy (bytes_examined ≈ nodes visited): the candidate set (seeds + reached) the packer
                // considered, before the budget truncated it.
                telemetry.BytesExamined = candidatesExamined;
                telemetry.Outcome = selectedCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            }
            return output;
        }
        catch (Exception ex)
        {
            if (telemetry is not null)
            {
                telemetry.Outcome = TelemetryOutcome.Error;
                telemetry.ErrorKind = ex.GetType().Name;
            }
            return $"context failed: {ex.Message}";
        }
    }

    private const int SignatureMaxLength = 110;
    private const int SearchSeedLimit = 10; // BM25 seeds (the toolbox default search page)
    // A generous internal reach cap so the budget — not an arbitrary count — bounds the bundle. The token pack
    // is the real limiter; this only guards against a pathological fan-out feeding the packer a huge candidate set.
    private const int ReachCap = 500;

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
        ArgumentNullException.ThrowIfNull(resolver);
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
        {
            selectedCount = 0;
            return json
                ? "{\"note\":\"no seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.\",\"bundle\":[]}"
                : "No seeds — nothing to anchor on. Give a query, entry_symbols, or a failing test / stack trace.";
        }

        // --- 2. Expand both directions to maxHops. Reach excludes the starts and returns min-hop per node. ---
        IReadOnlyList<ReachedNode> reached =
            index.Graph.Reach(seedOrder, maxHops, ReachCap, Direction.Both);

        // --- 3. Build the candidate list in priority order: seeds (hop 0, in seed rank) then reached (hop, id).
        // Reach already orders the reached nodes by (hop asc, id asc), so appending preserves that. ---
        var candidates = new List<Candidate>(seedOrder.Count + reached.Count);
        foreach (var seedId in seedOrder)
        {
            var symbol = index.FindBySymbolId(seedId);
            if (symbol is not null) // defensive — a seed id always comes from the index
                candidates.Add(new Candidate(symbol, Hop: 0));
        }
        foreach (var node in reached)
        {
            var symbol = index.FindBySymbolId(node.Id);
            if (symbol is not null)
                candidates.Add(new Candidate(symbol, node.Hop));
        }

        // --- 4. Cost each candidate by the token estimate of its EXACT render line (D8), then pack (D6). The
        // packer is pure (cost in); it honours the priority order and uses the budget greedily (keep-scanning). ---
        var packCandidates = new List<PackCandidate<Candidate>>(candidates.Count);
        foreach (var c in candidates)
        {
            int cost = (int)TokenEstimator.Count(CompactLine(c));
            packCandidates.Add(new PackCandidate<Candidate>(c, cost));
        }

        // D10 work proxy: the full candidate set the packer considered (seeds + reached), before truncation.
        candidatesExamined = candidates.Count;

        IReadOnlyList<Candidate> selected = ContextPacker.Pack(packCandidates, tokenBudget);
        selectedCount = selected.Count;

        return json ? RenderJson(selected) : RenderCompact(selected);
    }

    /// <summary>One member of the context bundle: a symbol and its hop distance from the nearest seed (0 = seed).</summary>
    private readonly record struct Candidate(IndexedSymbol Symbol, int Hop);

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

    // The compact, token-costed line for a candidate: "Name  kind  file:line  (hop N)  <signature>". This exact
    // string is what the estimator costs AND what the compact renderer emits, so the budget reflects the output.
    private static string CompactLine(Candidate c)
    {
        var s = c.Symbol;
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine)
          .Append("  (hop ").Append(c.Hop).Append(')');
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        return sb.ToString();
    }

    private static string RenderCompact(IReadOnlyList<Candidate> selected)
    {
        if (selected.Count == 0)
            return "Bundle empty — raise token_budget.";

        var sb = new StringBuilder();
        sb.Append("# context bundle (").Append(selected.Count).Append(")\n");
        for (int i = 0; i < selected.Count; i++)
        {
            sb.Append(CompactLine(selected[i]));
            if (i < selected.Count - 1)
                sb.Append('\n');
        }
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

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
