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
    private readonly IWorkspaceSearchProvider _workspaceProvider;

    /// <summary>Construct over the workspace search provider (production / freshness-aware).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="workspaceProvider"/> is null.</exception>
    public SearchTool(IWorkspaceSearchProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
    }

    [McpServerTool(Name = "search")]
    [Description(
        "Search indexed code and return ranked results. Use this before shell rg/grep/cat or reading whole " +
        "files. Pass a symbol name, an identifier, or a natural-language phrase. Test code is hidden for " +
        "natural-language queries unless you ask for it. Returns compact text by default; pass format=json " +
        "to chain results.")]
    public string Search(
        [Description("Symbol name, identifier, or natural-language phrase.")] string query,
        [Description("Interpretation axis: auto|text|symbol|file. Default auto.")] string mode = "auto",
        [Description("Max results to return. Default 10.")] int limit = 10,
        [Description("Hide test code: leave unset to auto-hide for natural-language queries; true/false to force.")]
        bool? exclude_tests = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Registered workspace id to query. Omit for the current workspace.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var scope = TelemetryContext.Current;
        try
        {
            var parsedMode = ParseMode(mode);
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceSymbolSearchContext context = _workspaceProvider.ResolveSymbolSearch(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string output = Run(context.Index, query, parsedMode, limit, exclude_tests, json, out int count, compactBanner);

            if (scope is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(scope, context);
                scope.SetTarget(query);
                scope.ResultCount = count;
                scope.Outcome = count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
            }
            return output;
        }
        catch (Exception ex)
        {
            if (scope is not null)
            {
                scope.Outcome = TelemetryOutcome.Error;
                scope.ErrorKind = ex.GetType().Name;
            }
            // Return a clean compact error rather than throwing raw (which the SDK redacts to the client).
            return $"search failed: {ex.Message}";
        }
    }

    private static SearchToolMode ParseMode(string mode) => mode?.ToLowerInvariant() switch
    {
        "text" => SearchToolMode.Text,
        "symbol" => SearchToolMode.Symbol,
        "file" => SearchToolMode.File,
        _ => SearchToolMode.Auto, // includes "auto", null, and anything unrecognized
    };

    private const int SignatureMaxLength = 110;

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry) the tool method delegates to. Returns the rendered
    /// string and sets <paramref name="renderedCount"/> to the number of rows actually shown (the page).
    /// </summary>
    public static string Run(
        ISymbolSearchIndex index, string query, SearchToolMode mode, int limit,
        bool? excludeTests, bool json, out int renderedCount, string? compactBanner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1) limit = 1;

        // Pull enough to know whether there is an overflow beyond `limit` after the test filter, so the
        // "… N more" note is accurate. Cap the over-fetch to avoid pathological cost.
        int overFetch = Math.Min(limit * 4 + 10, 500);
        IReadOnlyList<SearchHit> hits = index.Search(query, overFetch, SearchMode.Or);

        bool hideTests = ResolveExcludeTests(excludeTests, query, mode);

        // Preserve index order; only filter (never re-sort).
        var kept = new List<IndexedSymbol>(hits.Count);
        var scores = new List<double>(hits.Count);
        foreach (var hit in hits)
        {
            var sym = index.Resolve(hit.Document.DocId);
            // Cross-language predicate (decision-4): julie's persisted is_test OR the path fallback. Using the
            // shared helper means an AST-flagged test in a non-test-named file is hidden, not just *Tests.cs.
            if (hideTests && IsTestPath.IsTest(sym))
                continue;
            kept.Add(sym);
            scores.Add(hit.Score);
        }

        int total = kept.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
            return json ? "[]" : ReadToolWorkspaceRouting.PrefixCompact("No results.", compactBanner);

        return json
            ? RenderJson(kept, scores, page)
            : RenderCompact(kept, page, total, compactBanner);
    }

    // tri-state: null → hide only for NL phrases lacking test/def intent; true → always; false → never.
    private static bool ResolveExcludeTests(bool? excludeTests, string query, SearchToolMode mode)
    {
        if (excludeTests is { } forced)
            return forced;
        // mode=text or an inferred NL phrase → auto-hide unless the phrase signals test/def intent.
        bool isPhrase = mode == SearchToolMode.Text || IsNaturalLanguagePhrase(query);
        if (!isPhrase)
            return false;
        return !HasTestOrDefIntent(query);
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

    private static string RenderCompact(IReadOnlyList<IndexedSymbol> kept, int page, int total, string? compactBanner)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(compactBanner))
            sb.Append(compactBanner).Append('\n');
        for (int i = 0; i < page; i++)
        {
            var s = kept[i];
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine);
            if (!string.IsNullOrEmpty(s.Signature))
                sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
            if (i < page - 1)
                sb.Append('\n');
        }
        int remainder = total - page;
        if (remainder > 0)
            sb.Append('\n').Append("… ").Append(remainder).Append(" more (raise limit)");
        return sb.ToString();
    }

    private static string RenderJson(IReadOnlyList<IndexedSymbol> kept, IReadOnlyList<double> scores, int page)
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
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
