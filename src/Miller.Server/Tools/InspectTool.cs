using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>inspect</c> tool (M2 §5): view a file or a symbol you can already name (absorbs julie get_symbols +
/// deep_dive, ~44% of calls). A file path lists the file's symbols; a symbol name shows its definition,
/// signature and docs; <c>depth=full</c> adds children, name-based references, one-hop callers/callees, and
/// the body re-sliced from the on-disk file under the workspace root, gated by the content_hash freshness
/// invariant (a drifted file degrades to a "body unavailable" note, never stale bytes). The resolved cross-ref
/// graph + bridge are M4, not this. The target is smart-resolved; an ambiguous name returns candidates
/// (never pick-first), an unknown one a note (never an error).
/// </summary>
[McpServerToolType]
public sealed class InspectTool
{
    private readonly IWorkspaceIndexProvider _workspaceProvider;
    private readonly IWorkspaceSearchProvider _workspaceSearchProvider;

    /// <summary>Construct over the live index holder (production / freshness-aware).</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public InspectTool(IWorkspaceIndexProvider workspaceProvider, IWorkspaceSearchProvider workspaceSearchProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(workspaceSearchProvider);
        _workspaceProvider = workspaceProvider;
        _workspaceSearchProvider = workspaceSearchProvider;
    }

    [McpServerTool(Name = "inspect")]
    [Description(
        "Inspect a file or symbol you can already name. Give a file path to list its symbols, or a symbol " +
        "name to see its definition, signature, and docs. Add depth=full to also get references, " +
        "callers/callees, and the body. Use this before reading an entire file.")]
    public string Inspect(
        [Description("A file path or a symbol name/id (smart-resolved).")] string target,
        [Description("summary|full. summary = file's symbols or def+sig+doc; full = + refs/callers/callees/body/children.")]
        string depth = "summary",
        [Description("Filter a file listing to one kind (function/class/...). Optional.")] string? kind = null,
        [Description("Disambiguate an ambiguous symbol name to a file. Optional.")] string? scope = null,
        [Description("Max symbols when listing a file. Default 50.")] int limit = 50,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Registered workspace id to query. Omit for the current workspace.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var telemetry = TelemetryContext.Current;
        try
        {
            bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            bool full = string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase);

            string output;
            int count;
            if (full)
            {
                WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
                output = Run(context.Index, context.Resolver, context.IndexDbPath, context.WorkspaceRoot,
                    target, depth, kind, scope, limit, json, out count, compactBanner);

                if (telemetry is not null)
                    ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
            }
            else
            {
                WorkspaceSymbolSearchContext context = _workspaceSearchProvider.ResolveSymbolSearch(workspace_id, ensureFresh);
                string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
                output = RunSummary(context.Index, context.IndexDbPath, context.WorkspaceRoot,
                    target, kind, scope, limit, json, out count, compactBanner);

                if (telemetry is not null)
                    ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
            }

            if (telemetry is not null)
            {
                telemetry.SetTarget(target);
                telemetry.ResultCount = count;
                telemetry.Outcome = count == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
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
            return $"inspect failed: {ex.Message}";
        }
    }

    private const int SignatureMaxLength = 110;
    private const int RefLimit = 50;

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry). <paramref name="resultCount"/> is the count of the
    /// primary collection rendered (file symbols, candidates, or 1 for a resolved symbol; 0 for not-found).
    /// </summary>
    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string dbPath, string workspaceRoot,
        string target, string depth, string? kind, string? scope, int limit, bool json,
        out int resultCount,
        string? compactBanner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (limit < 1) limit = 1;
        bool full = string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase);

        return RunCore(index, full ? index : null, resolver, dbPath, workspaceRoot, target, full, kind, scope, limit,
            json, out resultCount, compactBanner);
    }

    public static string RunSummary(
        ISymbolLookupIndex index, string dbPath, string workspaceRoot,
        string target, string? kind, string? scope, int limit, bool json,
        out int resultCount,
        string? compactBanner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (limit < 1) limit = 1;

        var resolver = new SmartTargetResolver(index);
        return RunCore(index, fullIndex: null, resolver, dbPath, workspaceRoot, target, full: false, kind, scope, limit,
            json, out resultCount, compactBanner);
    }

    private static string RunCore(
        ISymbolLookupIndex index, MillerRepositoryIndex? fullIndex, SmartTargetResolver resolver,
        string dbPath, string workspaceRoot, string target, bool full,
        string? kind, string? scope, int limit, bool json,
        out int resultCount,
        string? compactBanner)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var resolution = resolver.Resolve(target, scope);
        switch (resolution)
        {
            case TargetResolution.File file:
                return ReadToolWorkspaceRouting.PrefixCompact(
                    RenderFile(index, file.Path, kind, limit, json, out resultCount),
                    json ? null : compactBanner);

            case TargetResolution.Symbol sym:
                resultCount = 1;
                string symbolOutput = json
                    ? RenderSymbolJson(index, fullIndex, dbPath, workspaceRoot, sym.Value, full)
                    : RenderSymbolCompact(index, fullIndex, dbPath, workspaceRoot, sym.Value, full);
                return ReadToolWorkspaceRouting.PrefixCompact(symbolOutput, json ? null : compactBanner);

            case TargetResolution.Candidates cands:
                resultCount = cands.Matches.Count;
                string candidatesOutput = json ? RenderCandidatesJson(cands.Matches) : RenderCandidatesCompact(cands.Matches);
                return ReadToolWorkspaceRouting.PrefixCompact(candidatesOutput, json ? null : compactBanner);

            case TargetResolution.NotFound nf:
                resultCount = 0;
                string notFoundOutput = json
                    ? $"{{\"not_found\":{JsonString(nf.Target)}}}"
                    : $"'{nf.Target}' not found. Try search to locate it.";
                return ReadToolWorkspaceRouting.PrefixCompact(notFoundOutput, json ? null : compactBanner);

            default:
                resultCount = 0;
                return ReadToolWorkspaceRouting.PrefixCompact(
                    "inspect: unrecognized resolution.",
                    json ? null : compactBanner);
        }
    }

    // ---------- file listing ----------

    private static string RenderFile(
        ISymbolLookupIndex index, string path, string? kind, int limit, bool json, out int resultCount)
    {
        IEnumerable<IndexedSymbol> symbols = index.FindByFilePath(path);
        if (!string.IsNullOrWhiteSpace(kind))
            symbols = symbols.Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
        var all = symbols.ToList();

        if (all.Count == 0)
        {
            resultCount = 0;
            return json
                ? $"{{\"file\":{JsonString(path)},\"children\":[]}}"
                : $"No indexed symbols in {path}";
        }

        int page = Math.Min(limit, all.Count);
        resultCount = page;

        if (json)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var w = NewWriter(buffer);
            w.WriteStartObject();
            w.WriteString("file", path);
            w.WritePropertyName("children");
            WriteSymbolArray(w, all.Take(page));
            w.WriteEndObject();
            w.Flush();
            return Utf8(buffer);
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(path).Append('\n');
        for (int i = 0; i < page; i++)
            sb.Append(SymbolLine(all[i])).Append('\n');
        int remainder = all.Count - page;
        if (remainder > 0)
            sb.Append("… ").Append(remainder).Append(" more (raise limit)\n");
        return sb.ToString().TrimEnd('\n');
    }

    // ---------- symbol ----------

    private static string RenderSymbolCompact(
        ISymbolLookupIndex index, MillerRepositoryIndex? fullIndex,
        string dbPath, string workspaceRoot, IndexedSymbol sym, bool full)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var sb = new StringBuilder();
        sb.Append("# ").Append(sym.Name).Append("  (").Append(sym.Kind).Append(")\n");
        sb.Append(sym.FilePath).Append(':').Append(sym.StartLine).Append('\n');
        if (!string.IsNullOrEmpty(sym.Signature))
            sb.Append(Truncate(sym.Signature!, SignatureMaxLength)).Append('\n');
        if (detail is not null && !string.IsNullOrEmpty(detail.Visibility))
            sb.Append("visibility: ").Append(detail.Visibility).Append('\n');
        if (detail is not null && !string.IsNullOrEmpty(detail.DocComment))
            sb.Append("doc: ").Append(detail.DocComment).Append('\n');

        if (!full)
            return sb.ToString().TrimEnd('\n');

        if (fullIndex is null)
            throw new InvalidOperationException("Full inspect requires the repository projection.");

        // children
        var children = fullIndex.FindChildren(sym.SymbolId);
        if (children.Count > 0)
        {
            sb.Append("\n## children\n");
            foreach (var c in children)
                sb.Append(SymbolLine(c)).Append('\n');
        }

        // refs (name-based)
        var refs = ExtractReader.ReadReferences(dbPath, sym.Name);
        if (refs.Count > 0)
        {
            sb.Append("\n## references\n");
            foreach (var r in refs.Take(RefLimit))
                sb.Append(r.FilePath).Append(':').Append(r.StartLine).Append('\n');
        }

        // callers = distinct containing symbols of those refs (resolved to names where possible)
        var callers = DistinctCallers(index, refs);
        if (callers.Count > 0)
        {
            sb.Append("\n## callers\n");
            foreach (var c in callers.Take(RefLimit))
                sb.Append(c).Append('\n');
        }

        // callees = one-hop calls FROM this symbol
        var callees = ExtractReader.ReadCallees(dbPath, sym.SymbolId);
        if (callees.Count > 0)
        {
            sb.Append("\n## callees\n");
            foreach (var c in callees.Take(RefLimit))
                sb.Append(c.Name).Append("  ").Append(c.FilePath).Append(':').Append(c.StartLine).Append('\n');
        }

        // body (graceful NULL degradation)
        sb.Append("\n## body\n");
        var body = detail is null
            ? ExtractReader.BodyReadResult.Unavailable(ExtractReader.BodyUnavailableReason.NoSpanRecorded)
            : ExtractReader.ReadBody(dbPath, workspaceRoot, sym.FilePath,
                detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);
        sb.Append(body.Text ?? RenderBodyUnavailableNote(body.UnavailableReason));

        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderSymbolJson(
        ISymbolLookupIndex index, MillerRepositoryIndex? fullIndex,
        string dbPath, string workspaceRoot, IndexedSymbol sym, bool full)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("symbol");
            WriteSymbolObject(w, sym, detail);

            if (full)
            {
                if (fullIndex is null)
                    throw new InvalidOperationException("Full inspect requires the repository projection.");

                w.WritePropertyName("children");
                WriteSymbolArray(w, fullIndex.FindChildren(sym.SymbolId));

                var refs = ExtractReader.ReadReferences(dbPath, sym.Name);
                w.WritePropertyName("refs");
                w.WriteStartArray();
                foreach (var r in refs.Take(RefLimit))
                {
                    w.WriteStartObject();
                    w.WriteString("file", r.FilePath);
                    w.WriteNumber("line", r.StartLine);
                    w.WriteString("kind", r.Kind);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WritePropertyName("callers");
                w.WriteStartArray();
                foreach (var c in DistinctCallers(index, refs).Take(RefLimit))
                    w.WriteStringValue(c);
                w.WriteEndArray();

                w.WritePropertyName("callees");
                w.WriteStartArray();
                foreach (var c in ExtractReader.ReadCallees(dbPath, sym.SymbolId).Take(RefLimit))
                {
                    w.WriteStartObject();
                    w.WriteString("name", c.Name);
                    w.WriteString("file", c.FilePath);
                    w.WriteNumber("line", c.StartLine);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                var body = detail is null
                    ? ExtractReader.BodyReadResult.Unavailable(ExtractReader.BodyUnavailableReason.NoSpanRecorded)
                    : ExtractReader.ReadBody(dbPath, workspaceRoot, sym.FilePath,
                        detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);
                if (body.Text is null)
                {
                    w.WriteNull("body");
                    w.WriteString("body_unavailable_reason", BodyUnavailableReasonJson(body.UnavailableReason));
                }
                else
                {
                    w.WriteString("body", body.Text);
                }
            }

            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    // distinct enclosing symbols of the refs, rendered as "Name  file:line" where resolvable, else the id.
    private static List<string> DistinctCallers(ISymbolLookupIndex index, IReadOnlyList<SymbolRef> refs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var r in refs)
        {
            if (r.ContainingSymbolId is not { } cid || !seen.Add(cid))
                continue;
            var containing = index.FindBySymbolId(cid);
            result.Add(containing is not null
                ? $"{containing.Name}  {containing.FilePath}:{containing.StartLine}"
                : cid);
        }
        return result;
    }

    // ---------- candidates ----------

    private static string RenderCandidatesCompact(IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple candidates — pass scope=<file> to disambiguate:\n");
        foreach (var s in matches)
            sb.Append(SymbolLine(s)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderCandidatesJson(IReadOnlyList<IndexedSymbol> matches)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var w = NewWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("candidates");
        WriteSymbolArray(w, matches);
        w.WriteEndObject();
        w.Flush();
        return Utf8(buffer);
    }

    // ---------- shared rendering helpers ----------

    private static string SymbolLine(IndexedSymbol s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
          .Append(s.FilePath).Append(':').Append(s.StartLine);
        if (!string.IsNullOrEmpty(s.Signature))
            sb.Append("  ").Append(Truncate(s.Signature!, SignatureMaxLength));
        return sb.ToString();
    }

    private static void WriteSymbolArray(Utf8JsonWriter w, IEnumerable<IndexedSymbol> symbols)
    {
        w.WriteStartArray();
        foreach (var s in symbols)
            WriteSymbolObject(w, s, detail: null);
        w.WriteEndArray();
    }

    private static void WriteSymbolObject(Utf8JsonWriter w, IndexedSymbol s, SymbolDetail? detail)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteString("kind", s.Kind);
        w.WriteString("file", s.FilePath);
        w.WriteNumber("line", s.StartLine);
        if (s.Signature is null) w.WriteNull("signature");
        else w.WriteString("signature", s.Signature);
        w.WriteString("symbol_id", s.SymbolId);
        if (detail is not null)
        {
            if (detail.DocComment is null) w.WriteNull("doc"); else w.WriteString("doc", detail.DocComment);
            if (detail.Visibility is null) w.WriteNull("visibility"); else w.WriteString("visibility", detail.Visibility);
        }
        w.WriteEndObject();
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string JsonString(string value) => JsonSerializer.Serialize(value);

    private static string RenderBodyUnavailableNote(ExtractReader.BodyUnavailableReason? reason) =>
        "(body unavailable — " + BodyUnavailableReasonCompact(reason) + ")";

    private static string BodyUnavailableReasonCompact(ExtractReader.BodyUnavailableReason? reason) =>
        reason switch
        {
            ExtractReader.BodyUnavailableReason.NoSpanRecorded => "no span recorded",
            ExtractReader.BodyUnavailableReason.FileHashUnavailable => "file hash unavailable",
            ExtractReader.BodyUnavailableReason.UnsafePath => "unsafe path",
            ExtractReader.BodyUnavailableReason.MissingFile => "missing file",
            ExtractReader.BodyUnavailableReason.StaleFile => "stale file",
            ExtractReader.BodyUnavailableReason.EmptyFile => "empty file",
            ExtractReader.BodyUnavailableReason.InvalidSpan => "invalid span",
            _ => "unknown reason",
        };

    private static string BodyUnavailableReasonJson(ExtractReader.BodyUnavailableReason? reason) =>
        reason switch
        {
            ExtractReader.BodyUnavailableReason.NoSpanRecorded => "no_span_recorded",
            ExtractReader.BodyUnavailableReason.FileHashUnavailable => "file_hash_unavailable",
            ExtractReader.BodyUnavailableReason.UnsafePath => "unsafe_path",
            ExtractReader.BodyUnavailableReason.MissingFile => "missing_file",
            ExtractReader.BodyUnavailableReason.StaleFile => "stale_file",
            ExtractReader.BodyUnavailableReason.EmptyFile => "empty_file",
            ExtractReader.BodyUnavailableReason.InvalidSpan => "invalid_span",
            _ => "unknown",
        };

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
