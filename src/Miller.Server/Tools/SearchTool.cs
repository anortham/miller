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
    private readonly IWorkspaceContentSearchProvider _contentProvider;

    /// <summary>
    /// Construct over the symbol-search and content-search providers (production / freshness-aware). In
    /// production both resolve to the one <c>WorkspaceIndexProvider</c> singleton; they are split here so the
    /// content/docs projection loads lazily and only when <c>mode=content</c> asks for it.
    /// </summary>
    /// <exception cref="ArgumentNullException">Either provider is null.</exception>
    public SearchTool(IWorkspaceSearchProvider workspaceProvider, IWorkspaceContentSearchProvider contentProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(contentProvider);
        _workspaceProvider = workspaceProvider;
        _contentProvider = contentProvider;
    }

    [McpServerTool(Name = "search")]
    [Description(
        "Search indexed code and return ranked results. Use this before shell rg/grep/cat or reading whole " +
        "files. Pass a symbol name, an identifier, or a natural-language phrase. Test code is hidden for " +
        "natural-language queries unless you ask for it. Use mode=content (alias docs) to search docs/prose " +
        "file content instead of symbols — it returns path + line + snippet hits. Returns compact text by " +
        "default; pass format=json to chain results.")]
    public string Search(
        [Description("Symbol name, identifier, or natural-language phrase.")] string query,
        [Description("Interpretation axis: auto|text|symbol|file|content (alias docs). Default auto.")] string mode = "auto",
        [Description("Max results to return. Default 10.")] int limit = 10,
        [Description("Hide test code: leave unset to auto-hide for natural-language queries; true/false to force.")]
        bool? exclude_tests = null,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var scope = TelemetryContext.Current;
        try
        {
            var parsedMode = ParseMode(mode);
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);

            string output;
            int count;
            if (parsedMode == SearchToolMode.Content)
            {
                // Content/docs search routes to its own projection and result kind; exclude_tests is a no-op.
                WorkspaceContentSearchContext content = _contentProvider.ResolveContentSearch(workspace_id, ensureFresh);
                string? contentBanner = ReadToolWorkspaceRouting.CompactBanner(content, workspace_id, json);
                output = RunContent(content.Index, query, limit, json, out count, contentBanner);
                if (scope is not null)
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, content);
            }
            else
            {
                WorkspaceSymbolSearchContext context = _workspaceProvider.ResolveSymbolSearch(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
                output = Run(context.Index, query, parsedMode, limit, exclude_tests, json, out count, compactBanner);
                if (scope is not null)
                {
                    ReadToolWorkspaceRouting.ApplyTelemetry(scope, context);
                    scope.MetadataJson = SearchBackendMetadata(context.Index);
                }
            }

            if (scope is not null)
            {
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

    // internal (not private): the CLI search verb reuses the EXACT same mode mapping so `miller search --mode x`
    // and the MCP tool agree on interpretation — one source of truth.
    internal static SearchToolMode ParseMode(string mode) => mode?.ToLowerInvariant() switch
    {
        "text" => SearchToolMode.Text,
        "symbol" => SearchToolMode.Symbol,
        "file" => SearchToolMode.File,
        "content" => SearchToolMode.Content,
        "docs" => SearchToolMode.Content, // alias
        _ => SearchToolMode.Auto, // includes "auto", null, and anything unrecognized
    };

    /// <summary>
    /// Telemetry metadata recording which backend served a symbol search — <c>disk</c> when the on-disk
    /// <see cref="FtsSymbolSearchIndex"/> sidecar answered, <c>memory</c> when the in-memory index did. This is
    /// the observable "disk path taken" signal from the sidecar design's risk list: a silent self-heal back to
    /// the in-memory index would otherwise be invisible. Every symbol search stamps its backend into the
    /// telemetry row's <c>metadata_json</c> so it can be read back per call and aggregated ad hoc (e.g.
    /// <c>json_extract(metadata_json, '$.search_backend')</c>). No dashboard surface consumes it yet — it is
    /// recorded for diagnosis; <c>SearchToolTests.Search_RecordsServingBackend_InTelemetryMetadata</c> pins it.
    /// </summary>
    internal const string DiskBackendMetadata = "{\"search_backend\":\"disk\"}";

    /// <summary>In-memory backend marker (see <see cref="DiskBackendMetadata"/>).</summary>
    internal const string MemoryBackendMetadata = "{\"search_backend\":\"memory\"}";

    private static string SearchBackendMetadata(ISymbolLookupIndex index) =>
        index is FtsSymbolSearchIndex ? DiskBackendMetadata : MemoryBackendMetadata;

    private const int SignatureMaxLength = 110;

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry) the tool method delegates to. Returns the rendered
    /// string and sets <paramref name="renderedCount"/> to the number of rows actually shown (the page).
    /// </summary>
    public static string Run(
        ISymbolLookupIndex index, string query, SearchToolMode mode, int limit,
        bool? excludeTests, bool json, out int renderedCount, string? compactBanner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1) limit = 1;

        // Pull enough to know whether there is an overflow beyond `limit` after the test filter, so the
        // "… N more" note is accurate. Cap the over-fetch to avoid pathological cost.
        int overFetch = Math.Min(limit * 4 + 10, 500);
        bool fileMode = mode == SearchToolMode.File ||
                        (mode == SearchToolMode.Auto && IsPathLikeQuery(query, index));

        bool hideTests = ResolveExcludeTests(excludeTests, query, mode);

        // Preserve index order; only filter (never re-sort).
        var kept = new List<IndexedSymbol>();
        var scores = new List<double>();
        if (fileMode)
        {
            IReadOnlyList<IndexedSymbol> symbols = index.FindByFilePathFragment(query, overFetch);
            kept.Capacity = symbols.Count;
            scores.Capacity = symbols.Count;
            foreach (IndexedSymbol sym in symbols)
                AddIfVisible(sym, score: 1.0);
        }
        else
        {
            IReadOnlyList<SearchHit> hits = index.Search(query, overFetch, SearchMode.Or);
            kept.Capacity = hits.Count;
            scores.Capacity = hits.Count;
            foreach (var hit in hits)
                AddIfVisible(index.Resolve(hit.Document.DocId), hit.Score);
        }

        int total = kept.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
            return json ? "[]" : ReadToolWorkspaceRouting.PrefixCompact("No results.", compactBanner);

        return json
            ? RenderJson(kept, scores, page)
            : RenderCompact(kept, page, total, compactBanner);

        void AddIfVisible(IndexedSymbol sym, double score)
        {
            // Cross-language predicate (decision-4): julie's persisted is_test OR the path fallback. Using the
            // shared helper means an AST-flagged test in a non-test-named file is hidden, not just *Tests.cs.
            if (hideTests && IsTestPath.IsTest(sym))
                return;
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
        bool json, out int renderedCount, string? compactBanner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1) limit = 1;

        // Over-fetch (same cap as symbol search) so the "… N more" overflow note is accurate without paging.
        int overFetch = Math.Min(limit * 4 + 10, 500);
        IReadOnlyList<ContentSearchHit> hits = index.Search(query, overFetch);

        int total = hits.Count;
        int page = Math.Min(limit, total);
        renderedCount = page;

        if (total == 0)
            return json ? "[]" : ReadToolWorkspaceRouting.PrefixCompact("No results.", compactBanner);

        return json
            ? RenderContentJson(hits, page)
            : RenderContentCompact(hits, page, total, compactBanner);
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

    private static bool IsPathLikeQuery(string query, ISymbolLookupIndex index)
    {
        if (query.Contains('/') || query.Contains('\\'))
            return true;

        string ext = Path.GetExtension(query.Trim());
        return ext.Length > 1 && index.KnownExtensions.Contains(ext);
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

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
