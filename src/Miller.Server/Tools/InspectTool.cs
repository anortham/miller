using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>inspect</c> tool (M2 §5): view a file or a symbol you can already name (absorbs julie get_symbols +
/// deep_dive, ~44% of calls). A file path lists the file's symbols; a symbol name shows its definition,
/// signature and docs; <c>depth=full</c> adds children, exact references, one-hop callers/callees, and
/// the body re-sliced from the on-disk file under the workspace root, gated by the content_hash freshness
/// invariant (a drifted file degrades to a "body unavailable" note, never stale bytes). The resolved cross-ref
/// graph + bridge are M4, not this. The target is smart-resolved; an ambiguous name returns candidates
/// (never pick-first), an unknown one a note (never an error).
/// </summary>
[McpServerToolType]
public sealed class InspectTool
{
    private readonly IWorkspaceSymbolReadProvider _workspaceSymbolReadProvider;

    /// <summary>Construct over the live index holder (production / freshness-aware).</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public InspectTool(IWorkspaceSymbolReadProvider workspaceSymbolReadProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceSymbolReadProvider);
        _workspaceSymbolReadProvider = workspaceSymbolReadProvider;
    }

    [McpServerTool(Name = "inspect")]
    [Description(
        "Inspect a file or symbol you can already name. A file path lists its symbols; a symbol name gives " +
        "definition, signature, docs — depth=overview adds bounded refs/callers/callees and a body preview " +
        "(the right first symbol read); overview/full also expose test locations and typed inheritance or " +
        "implementation relations when extractor evidence exists; depth=full adds relation lists and a bounded body page. For constants, " +
        "fields, properties, and variables, a full result with value_declaration_complete=true is authoritative " +
        "and needs no search confirmation. Use before reading " +
        "any entire file. NOT for: discovering which symbol matters in an unfamiliar area (use context) or full " +
        "reference lists across the repo (use trace mode=refs). Example: inspect target=FullRebuildPromotion " +
        "depth=overview.")]
    public string Inspect(
        [Description("A file path or a symbol name/id (smart-resolved).")] string target,
        [Description("summary|overview|full. overview adds bounded refs/callers/callees/body preview; full adds complete body.")]
        string depth = "summary",
        [Description("Filter a file listing to one kind (function/class/...). Optional.")] string? kind = null,
        [Description("Disambiguate an ambiguous symbol name to a file. Optional.")] string? scope = null,
        [Description("Max symbols when listing a file. Default and maximum 10.")] int limit = ToolOutputBudget.McpRowLimit,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null,
        [Description("Opaque token from a truncated depth=full body. Bound to workspace, symbol, extractor hash, and source span.")]
        string? continuation = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!string.Equals(format, "compact", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "invalid_format",
                    "inspect format must be compact or json."));
            }
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            InspectDepth parsedDepth = ParseDepth(depth);
            int effectiveLimit = Math.Min(limit, ToolOutputBudget.McpRowLimit);

            WorkspaceSymbolReadContext context =
                _workspaceSymbolReadProvider.ResolveSymbolRead(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string output = RunLookupWithDiagnostics(
                context.Index,
                context.IndexDbPath,
                context.WorkspaceRoot,
                context.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(context.WorkspaceRoot),
                target,
                depth,
                kind,
                scope,
                effectiveLimit,
                json,
                continuation,
                out int count,
                out ToolDiagnostic? diagnostic,
                compactBanner,
                boundAgentOutput: true);

            if (telemetry is not null)
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);

            if (telemetry is not null)
            {
                telemetry.Op = DepthName(parsedDepth);
                telemetry.SetTarget(target);
                telemetry.ResultCount = count;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("depth", DepthName(parsedDepth));
                telemetry.SetMetadata("format", json ? "json" : "compact");
                telemetry.SetMetadata("has_kind", !string.IsNullOrWhiteSpace(kind));
                telemetry.SetMetadata("has_scope", !string.IsNullOrWhiteSpace(scope));
                telemetry.SetMetadata("limit_bucket", LimitBucket(effectiveLimit));
            }
            if (diagnostic is not null)
                output = ToolDiagnosticRenderer.Attach(
                    "inspect",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            return RequireInspectMcpOutput(output);
        }
        catch (Exception ex)
        {
            if (telemetry is not null &&
                ex is ToolDiagnosticException { Diagnostic.Code: "output_metadata_too_large" })
            {
                telemetry.ResultCount = 0;
            }
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render(
                "inspect",
                diagnostic,
                json,
                telemetry);
        }
    }

    private static string RequireInspectMcpOutput(string output)
    {
        if (Encoding.UTF8.GetByteCount(output) <= ToolOutputBudget.InspectMcpMaxBytes)
            return output;

        throw new ToolDiagnosticException(
            ToolDiagnostic.Refusal(
                "output_metadata_too_large",
                "Inspect output metadata exceeds the 12 KiB MCP budget; use CLI output for exhaustive metadata or narrow the target or depth."));
    }

    private const int RefLimit = 50;
    private const int FullCalleeLimit = 10;
    private const int OverviewRelationLimit = 3;
    private const int OverviewChildLimit = 5;
    private const int OverviewBodyPreviewMaxLines = 16;
    private const int OverviewBodyPreviewMaxChars = 700;
    private const int FullValueDeclarationMaxLength = 4096;
    private static readonly ReferenceKind[] TypedRelationshipKinds =
        [ReferenceKind.Implementation, ReferenceKind.Inheritance];

    // Minimum name-based reference count at which a compact symbol read (overview/full) earns a trailing
    // "run impact" nudge. Below this the symbol is not hot enough for the hint to add value.
    private const int ImpactHintMinReferences = 4;

    private enum InspectDepth
    {
        Summary,
        Overview,
        Full,
    }

    private static InspectDepth ParseDepth(string? depth)
    {
        if (string.IsNullOrWhiteSpace(depth) ||
            string.Equals(depth, "summary", StringComparison.OrdinalIgnoreCase))
        {
            return InspectDepth.Summary;
        }

        if (string.Equals(depth, "overview", StringComparison.OrdinalIgnoreCase))
            return InspectDepth.Overview;
        if (string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase))
            return InspectDepth.Full;

        throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
            "invalid_depth",
            "inspect depth must be summary, overview, or full."));
    }

    private static string DepthName(InspectDepth depth) => depth switch
    {
        InspectDepth.Full => "full",
        InspectDepth.Overview => "overview",
        _ => "summary",
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
        InspectDepth parsedDepth = ParseDepth(depth);

        return RunCore(index, resolver, dbPath, workspaceRoot, target, parsedDepth, kind, scope, limit,
            json, out resultCount, out _, compactBanner, WorkspaceId.FromCanonicalRoot(workspaceRoot),
            continuation: null, boundAgentOutput: false);
    }

    public static string RunLookup(
        ISymbolLookupIndex index, string dbPath, string workspaceRoot,
        string target, string depth, string? kind, string? scope, int limit, bool json,
        out int resultCount,
        string? compactBanner = null,
        string? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (limit < 1) limit = 1;
        InspectDepth parsedDepth = ParseDepth(depth);

        var resolver = new SmartTargetResolver(index);
        return RunCore(index, resolver, dbPath, workspaceRoot, target, parsedDepth, kind, scope, limit,
            json, out resultCount, out _, compactBanner, WorkspaceId.FromCanonicalRoot(workspaceRoot),
            continuation, boundAgentOutput: false);
    }

    public static string RunSummary(
        ISymbolLookupIndex index, string dbPath, string workspaceRoot,
        string target, string? kind, string? scope, int limit, bool json,
        out int resultCount,
        string? compactBanner = null)
    {
        return RunLookup(index, dbPath, workspaceRoot, target, depth: "summary", kind, scope, limit,
            json, out resultCount, compactBanner);
    }

    private static string RunCore(
        ISymbolLookupIndex index, SmartTargetResolver resolver,
        string dbPath, string workspaceRoot, string target, InspectDepth depth,
        string? kind, string? scope, int limit, bool json,
        out int resultCount,
        out ToolDiagnostic? diagnostic,
        string? compactBanner,
        string workspaceId,
        string? continuation,
        bool boundAgentOutput)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        diagnostic = null;
        var resolution = resolver.Resolve(target, scope);

        switch (resolution)
        {
            case TargetResolution.File file:
                string fileOutput = RenderFile(
                    index,
                    file.Path,
                    kind,
                    limit,
                    json,
                    workspaceId,
                    continuation,
                    boundAgentOutput,
                    out resultCount,
                    out int matchedCount);
                if (matchedCount == 0)
                {
                    diagnostic = ToolDiagnostic.ExpectedEmpty(
                        "no_file_symbols",
                        $"No indexed symbols matched '{file.Path}' and the requested filters.");
                }
                return ReadToolWorkspaceRouting.PrefixCompact(
                    fileOutput,
                    json ? null : compactBanner);

            case TargetResolution.Symbol sym:
                if (!string.IsNullOrWhiteSpace(continuation) && depth != InspectDepth.Full)
                {
                    throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                        "continuation_not_applicable",
                        "Symbol-body continuations require depth=full.",
                        [new ToolDiagnosticAction(
                            $"inspect(target=\"{EscapeDiagnosticTarget(target)}\", depth=\"full\")",
                            "restart the full-body read")]));
                }
                resultCount = 1;
                string symbolOutput = json
                    ? RenderSymbolJson(
                        index, dbPath, workspaceRoot, workspaceId, sym.Value, depth, continuation,
                        boundAgentOutput)
                    : RenderSymbolCompact(
                        index, dbPath, workspaceRoot, workspaceId, sym.Value, depth, continuation,
                        boundAgentOutput);
                return ReadToolWorkspaceRouting.PrefixCompact(symbolOutput, json ? null : compactBanner);

            case TargetResolution.Candidates cands:
                resultCount = cands.Matches.Count;
                diagnostic = ToolDiagnostic.Ambiguity(
                    "ambiguous_target",
                    $"'{target}' matched {cands.Matches.Count} symbols.",
                    CandidateOutput.RerunExamples(target, cands.Matches, supportsScope: true)
                        .Select(example => new ToolDiagnosticAction(example, "select one exact symbol"))
                        .ToArray());
                string candidatesOutput = json
                    ? RenderCandidatesJson(target, cands.Matches)
                    : RenderCandidatesCompact(target, cands.Matches);
                return ReadToolWorkspaceRouting.PrefixCompact(candidatesOutput, json ? null : compactBanner);

            case TargetResolution.NotFound nf:
                resultCount = 0;
                bool hasSuggestions = nf.Suggestions is { Count: > 0 };
                diagnostic = ToolDiagnostic.ExpectedEmpty(
                    "not_found",
                    hasSuggestions
                        ? $"No indexed file or symbol matched '{target}'."
                        : $"No exact indexed file or symbol matched '{target}'; this is a definitive empty result for the current index.",
                    hasSuggestions
                        ? [new ToolDiagnosticAction(
                            $"search(query=\"{EscapeDiagnosticTarget(target)}\")",
                            "inspect related or renamed symbols")]
                        : null);
                string notFoundOutput = json
                    ? RenderNotFoundJson(nf)
                    : nf.RenderMessage();
                return ReadToolWorkspaceRouting.PrefixCompact(notFoundOutput, json ? null : compactBanner);

            default:
                throw new ToolDiagnosticException(ToolDiagnostic.InternalFailure(
                    "unrecognized_resolution",
                    "Inspect received an unrecognized target resolution."));
        }
    }

    private static string RunLookupWithDiagnostics(
        ISymbolLookupIndex index,
        string dbPath,
        string workspaceRoot,
        string workspaceId,
        string target,
        string depth,
        string? kind,
        string? scope,
        int limit,
        bool json,
        string? continuation,
        out int resultCount,
        out ToolDiagnostic? diagnostic,
        string? compactBanner,
        bool boundAgentOutput)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (limit < 1)
            limit = 1;

        var resolver = new SmartTargetResolver(index);
        return RunCore(
            index,
            resolver,
            dbPath,
            workspaceRoot,
            target,
            ParseDepth(depth),
            kind,
            scope,
            limit,
            json,
            out resultCount,
            out diagnostic,
            compactBanner,
            workspaceId,
            continuation,
            boundAgentOutput);
    }

    // ---------- file listing ----------

    private static string RenderFile(
        ISymbolLookupIndex index,
        string path,
        string? kind,
        int limit,
        bool json,
        string workspaceId,
        string? continuation,
        bool boundAgentOutput,
        out int resultCount,
        out int matchedCount)
    {
        IReadOnlyList<IndexedSymbol> fileSymbols = index.FindByFilePath(path);
        IEnumerable<IndexedSymbol> symbols = fileSymbols;
        if (!string.IsNullOrWhiteSpace(kind))
            symbols = symbols.Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
        var all = symbols.ToList();
        matchedCount = all.Count;

        if (all.Count == 0)
        {
            resultCount = 0;
            return json
                ? $"{{\"file\":{JsonString(path)},\"children\":[]}}"
                : $"No indexed symbols in {path}";
        }

        List<IndexedSymbol> ordered = all
            .OrderBy(static symbol => IsLowSignalKind(symbol.Kind) ? 1 : 0)
            .ThenBy(static symbol => KindRank(symbol.Kind))
            .ThenBy(static symbol => symbol.StartLine)
            .ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal)
            .ToList();
        List<IndexedSymbol> population = json || !string.IsNullOrWhiteSpace(kind)
            ? ordered
            : ordered.Where(static symbol => !IsLowSignalKind(symbol.Kind)).ToList();
        if (population.Count == 0)
        {
            resultCount = 0;
            return RenderNoVisibleFileSymbols(path, all, kind);
        }
        var identity = new ToolPopulationContinuationIdentity(
            "inspect_file",
            workspaceId,
            PopulationFingerprint(population),
            RequestFingerprint(path, kind, json ? "json" : "compact", limit.ToString()));
        int offset = string.IsNullOrWhiteSpace(continuation)
            ? 0
            : ToolOutputBudget.DecodePopulationCursor(continuation, identity).Offset;
        if (offset < 0 || offset >= population.Count)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "continuation_offset_invalid",
                "Inspect file continuation offset is outside the current result population."));
        }
        List<IndexedSymbol> page = population.Skip(offset).Take(limit).ToList();
        int nextOffset = checked(offset + page.Count);
        string? nextContinuation = nextOffset < population.Count
            ? ToolOutputBudget.EncodePopulationCursor(
                identity,
                new ToolPopulationContinuationCursor(nextOffset))
            : null;
        resultCount = page.Count;

        if (json)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var w = NewWriter(buffer);
            w.WriteStartObject();
            w.WriteString("file", path);
            w.WritePropertyName("children");
            WriteFileSymbolArray(w, page, fileSymbols);
            w.WriteNumber("children_total_count", population.Count);
            w.WriteNumber("children_returned_count", page.Count);
            w.WriteNumber("children_omitted_count", population.Count - nextOffset);
            w.WriteBoolean("children_truncated", nextContinuation is not null);
            w.WriteNumber("page_offset", offset);
            if (nextContinuation is null)
                w.WriteNull("continuation");
            else
                w.WriteString("continuation", nextContinuation);
            w.WriteEndObject();
            w.Flush();
            return Utf8(buffer);
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(path).Append('\n');
        AppendFileSymbolGroups(sb, page, fileSymbols);
        int remainder = population.Count - nextOffset;
        if (remainder > 0)
        {
            sb.Append("… ").Append(remainder).Append(" more\n");
            if (nextContinuation is not null)
            {
                sb.Append(NextStepHint.Render(
                    $"inspect target=\"{EscapeDiagnosticTarget(path)}\" limit={limit}" +
                    (string.IsNullOrWhiteSpace(kind) ? "" : $" kind=\"{EscapeDiagnosticTarget(kind)}\"") +
                    $" continuation=\"{nextContinuation}\"",
                    "continue this file listing")).Append('\n');
            }
        }
        if (string.IsNullOrWhiteSpace(kind))
            AppendHiddenLowSignalNote(sb, all);
        return sb.ToString().TrimEnd('\n');
    }

    private static string PopulationFingerprint(IEnumerable<IndexedSymbol> symbols)
    {
        var builder = new StringBuilder();
        foreach (IndexedSymbol symbol in symbols)
        {
            AppendFingerprintField(builder, symbol.SymbolId);
            AppendFingerprintField(builder, symbol.Kind);
            AppendFingerprintField(builder, symbol.Name);
            AppendFingerprintField(builder, symbol.StartLine.ToString());
            AppendFingerprintField(builder, symbol.Signature ?? string.Empty);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string RequestFingerprint(params string?[] fields)
    {
        var builder = new StringBuilder();
        foreach (string? field in fields)
            AppendFingerprintField(builder, field ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendFingerprintField(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);

    private static string RenderNoVisibleFileSymbols(string path, IReadOnlyList<IndexedSymbol> all, string? kind)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(path).Append('\n');
        if (!string.IsNullOrWhiteSpace(kind))
        {
            sb.Append("No indexed symbols in ").Append(path);
            return sb.ToString();
        }

        sb.Append("No high-signal indexed symbols.");
        AppendHiddenLowSignalNote(sb.Append('\n'), all);
        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendFileSymbolGroups(
        StringBuilder sb,
        IEnumerable<IndexedSymbol> symbols,
        IReadOnlyList<IndexedSymbol> hierarchy)
    {
        IReadOnlyDictionary<string, IndexedSymbol> byId = BuildSymbolMap(hierarchy);
        foreach (var group in symbols
                     .GroupBy(static s => s.Kind, StringComparer.Ordinal)
                     .OrderBy(static g => KindRank(g.Key))
                     .ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            var items = group.OrderBy(static s => s.StartLine).ThenBy(static s => s.Name, StringComparer.Ordinal).ToList();
            sb.Append(group.Key).Append(" (").Append(items.Count).Append(")\n");
            foreach (IndexedSymbol symbol in items)
                sb.Append("  ")
                    .Append(FileSymbolLine(symbol, byId))
                    .Append('\n');
        }
    }

    private static string FileSymbolLine(
        IndexedSymbol s,
        IReadOnlyDictionary<string, IndexedSymbol> byId)
    {
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  :").Append(s.StartLine);
        if (s.EndLine > 0)
            sb.Append('-').Append(s.EndLine);
        string? parentPath = FileParentPath(s, byId);
        if (parentPath is not null)
            sb.Append("  [parent=").Append(parentPath).Append(']');
        if (SignatureAddsInfo(s))
            sb.Append("  ").Append(Truncate(InlineSignature(s.Signature!), ToolRenderLimits.SignatureMaxLength));
        return sb.ToString();
    }

    private static string? FileParentPath(
        IndexedSymbol symbol,
        IReadOnlyDictionary<string, IndexedSymbol> byId)
    {
        string? parentId = symbol.ParentId;
        var names = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (parentId is not null && visited.Add(parentId) && byId.TryGetValue(parentId, out IndexedSymbol? parent))
        {
            names.Add(parent.Name);
            parentId = parent.ParentId;
        }

        if (names.Count == 0)
            return null;

        names.Reverse();
        return string.Join('.', names);
    }

    private static int FileNestingDepth(
        IndexedSymbol symbol,
        IReadOnlyDictionary<string, IndexedSymbol> byId)
    {
        int depth = 0;
        string? parentId = symbol.ParentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (parentId is not null && visited.Add(parentId) && byId.TryGetValue(parentId, out IndexedSymbol? parent))
        {
            depth++;
            parentId = parent.ParentId;
        }

        return depth;
    }

    // Some extractors emit a signature that is just the symbol name again (e.g. bare fields); repeating it
    // next to the name spends tokens on nothing.
    private static bool SignatureAddsInfo(IndexedSymbol s) =>
        !string.IsNullOrEmpty(s.Signature) && !string.Equals(s.Signature!.Trim(), s.Name, StringComparison.Ordinal);

    private static int SignatureLimit(IndexedSymbol symbol, InspectDepth depth) =>
        depth == InspectDepth.Full &&
        IsValueDeclaration(symbol)
            ? FullValueDeclarationMaxLength
            : ToolRenderLimits.SignatureMaxLength;

    private static bool IsValueDeclaration(IndexedSymbol symbol) =>
        symbol.Kind is "constant" or "variable" or "field" or "property";

    private static bool IsCompleteValueDeclaration(IndexedSymbol symbol) =>
        symbol.Signature is not null &&
        symbol.Signature.Length <= FullValueDeclarationMaxLength;

    private static int KindRank(string kind) => kind switch
    {
        "class" or "interface" or "struct" or "record" or "enum" or "type" => 0,
        "constructor" or "method" or "function" => 1,
        "property" or "field" or "constant" or "variable" => 2,
        "enum_member" => 3,
        "namespace" => 4,
        "module" or "import" => 5,
        _ => 6,
    };

    private static bool IsLowSignalKind(string kind) =>
        string.Equals(kind, "import", StringComparison.Ordinal) ||
        string.Equals(kind, "module", StringComparison.Ordinal);

    private static void AppendHiddenLowSignalNote(StringBuilder sb, IReadOnlyList<IndexedSymbol> all)
    {
        var hidden = all
            .Where(static s => IsLowSignalKind(s.Kind))
            .GroupBy(static s => s.Kind, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal)
            .Select(static g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? string.Empty : "s")}")
            .ToList();
        if (hidden.Count == 0)
            return;

        sb.Append("low_signal hidden: ")
          .Append(string.Join(", ", hidden))
          .Append(" (pass kind=import/module)\n");
    }

    // ---------- symbol ----------

    private static string RenderSymbolCompact(
        ISymbolLookupIndex index,
        string dbPath,
        string workspaceRoot,
        string workspaceId,
        IndexedSymbol sym,
        InspectDepth depth,
        string? continuation,
        bool boundAgentOutput)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var sb = new StringBuilder();
        sb.Append("# ").Append(sym.Name).Append("  (").Append(sym.Kind).Append(")\n");
        sb.Append(sym.FilePath).Append(':').Append(sym.StartLine).Append('\n');
        if (SignatureAddsInfo(sym))
            sb.Append(Truncate(sym.Signature!, SignatureLimit(sym, depth))).Append('\n');
        if (depth == InspectDepth.Full && IsValueDeclaration(sym))
        {
            sb.Append("value_declaration_complete: ")
                .Append(IsCompleteValueDeclaration(sym) ? "true" : "false")
                .Append('\n');
        }
        if (detail is not null && !string.IsNullOrEmpty(detail.Visibility))
            sb.Append("visibility: ").Append(detail.Visibility).Append('\n');
        if (detail is not null && !string.IsNullOrEmpty(detail.DocComment))
        {
            bool docTruncated =
                boundAgentOutput &&
                Encoding.UTF8.GetByteCount(detail.DocComment) > ToolOutputBudget.InspectMcpDocMaxBytes;
            string docComment = docTruncated
                ? ToolOutputBudget.TruncateUtf8(
                    detail.DocComment,
                    ToolOutputBudget.InspectMcpDocMaxBytes,
                    "…")
                : detail.DocComment;
            sb.Append("doc: ").Append(docComment).Append('\n');
            if (docTruncated)
                sb.Append("doc_truncated: true\n");
        }

        if (depth == InspectDepth.Summary)
            return sb.ToString().TrimEnd('\n');

        var complexity = ExtractReader.ReadSymbolComplexity(dbPath, sym.SymbolId);
        if (complexity is not null)
        {
            sb.Append("complexity: decisions=").Append(complexity.DecisionCount)
                .Append("  loops=").Append(complexity.LoopCount)
                .Append("  nesting=").Append(complexity.MaxNestingDepth);
            if (complexity.ParameterCount is { } parameters)
                sb.Append("  params=").Append(parameters);
            sb.Append("  lines=").Append(complexity.CoveredLines).Append('\n');
        }

        int relationLimit = depth == InspectDepth.Overview
            ? OverviewRelationLimit
            : boundAgentOutput ? ToolOutputBudget.McpRowLimit : RefLimit;
        int calleeLimit = depth == InspectDepth.Overview ? OverviewRelationLimit : FullCalleeLimit;
        int typedRelationLimit = depth == InspectDepth.Overview
            ? OverviewRelationLimit
            : boundAgentOutput ? ToolOutputBudget.McpRowLimit : FullCalleeLimit;
        ReferenceEvidenceBundle evidence = ReferenceEvidenceReader.ReadForSymbol(
            dbPath,
            sym.SymbolId,
            new ReferenceEvidenceQuery(
                new ReferenceEvidenceBounds(relationLimit + 1, relationLimit + 1)),
            new ReferenceEvidenceQuery(
                new ReferenceEvidenceBounds(RefLimit + 1, RefLimit + 1),
                ReferenceKind.Call),
            new ReferenceEvidenceBounds(typedRelationLimit, typedRelationLimit),
            TypedRelationshipKinds);

        var children = index.FindChildren(sym.SymbolId);
        if (children.Count > 0)
        {
            sb.Append("\n## children\n");
            int childLimit = depth == InspectDepth.Overview
                ? OverviewChildLimit
                : boundAgentOutput ? ToolOutputBudget.McpRowLimit : int.MaxValue;
            foreach (var c in children.Take(childLimit))
                sb.Append(SymbolLine(c)).Append('\n');
            AppendOmittedLine(sb, children.Count, childLimit, "children");
        }

        ReferenceEvidenceSet referenceEvidence = evidence.Inbound;
        IReadOnlyList<ReferenceEvidence> refs = referenceEvidence.Exact;
        if (refs.Count > 0)
        {
            sb.Append("\n## references\n");
            AppendGroupedReferences(sb, refs.Take(relationLimit));
            AppendOmittedLine(
                sb,
                referenceEvidence.Coverage.ExactAvailable,
                relationLimit,
                "refs",
                depth == InspectDepth.Overview ? "use depth=full" : "use trace mode=refs");
        }

        if (referenceEvidence.Fallback.Count > 0)
        {
            sb.Append("\n## reference fallback (unresolved)\n");
            AppendGroupedReferences(sb, referenceEvidence.Fallback.Take(relationLimit));
            AppendOmittedLine(
                sb,
                referenceEvidence.Coverage.FallbackAvailable,
                relationLimit,
                "fallback refs",
                depth == InspectDepth.Overview ? "use depth=full" : "use trace mode=refs");
        }
        else if (referenceEvidence.Coverage.FallbackStatus ==
                 ReferenceFallbackStatus.SuppressedAmbiguousName &&
                 referenceEvidence.Coverage.FallbackAvailable > 0)
        {
            sb.Append("\nreference fallback suppressed because the target name is ambiguous (")
                .Append(referenceEvidence.Coverage.FallbackAvailable)
                .Append(" unresolved same-name candidate(s)).\n");
        }

        var callers = ResolveContainingSymbols(
            index,
            referenceEvidence.ExactCallerSymbolIds,
            refs,
            relationLimit);
        if (callers.Count > 0)
        {
            sb.Append("\n## callers\n");
            foreach (var c in callers)
                sb.Append(c.Name).Append("  ").Append(c.FilePath).Append(':').Append(c.StartLine).Append('\n');
            AppendOmittedLine(
                sb,
                referenceEvidence.ExactCallerSymbolIds.Count,
                relationLimit,
                "callers");
        }

        var referencedBy = ResolveContainingSymbols(
            index,
            referenceEvidence.ExactReferencedBySymbolIds,
            refs,
            relationLimit);
        if (referencedBy.Count > 0)
        {
            sb.Append("\n## referenced_by\n");
            foreach (var reference in referencedBy)
                sb.Append(reference.Name).Append("  ").Append(reference.FilePath).Append(':')
                    .Append(reference.StartLine).Append('\n');
            AppendOmittedLine(
                sb,
                referenceEvidence.ExactReferencedBySymbolIds.Count,
                relationLimit,
                "referenced_by");
        }

        IReadOnlyList<IndexedSymbol> testLocations =
            ResolveTestLocations(index, referenceEvidence);
        if (testLocations.Count > 0)
        {
            sb.Append("\n## test locations\n");
            foreach (IndexedSymbol testLocation in testLocations.Take(relationLimit))
                sb.Append(testLocation.Name)
                    .Append("  ")
                    .Append(testLocation.FilePath)
                    .Append(':')
                    .Append(testLocation.StartLine)
                    .Append('\n');
            AppendOmittedLine(sb, testLocations.Count, relationLimit, "test locations");
        }

        OutgoingReferenceEvidenceSet outgoing = evidence.Outgoing;
        var callees = DistinctCallees(index, outgoing.Exact);
        if (callees.Count > 0)
        {
            sb.Append("\n## callees\n");
            foreach (var c in callees.Take(calleeLimit))
            {
                sb.Append(c.Name);
                if (c.Count > 1)
                    sb.Append(" ×").Append(c.Count);
                sb.Append("  ").Append(c.FilePath).Append(':').Append(c.StartLine)
                    .Append("  [exact id=").Append(c.TargetSymbolId)
                    .Append(" source=").Append(EvidenceSourceLabel(c.Source))
                    .Append(" confidence=")
                    .Append(c.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("]\n");
            }
            AppendOmittedLine(sb, callees.Count, calleeLimit, "callees");
        }

        var calleeFallback = DistinctFallbackCallees(outgoing.Fallback);
        if (calleeFallback.Count > 0)
        {
            sb.Append("\n## callee fallback (unresolved)\n");
            foreach (var c in calleeFallback.Take(calleeLimit))
            {
                sb.Append(c.Name);
                if (c.Count > 1)
                    sb.Append(" ×").Append(c.Count);
                sb.Append("  ").Append(c.FilePath).Append(':').Append(c.StartLine)
                    .Append("  [fallback source=name_fallback confidence=")
                    .Append(c.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("]\n");
            }
            AppendOmittedLine(sb, calleeFallback.Count, calleeLimit, "callees (fallback)");
        }

        IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> typedOutgoing =
            evidence.OutgoingKinds;
        IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> typedInbound =
            evidence.InboundKinds;
        AppendTypedOutgoingRelationships(
            sb,
            "implements",
            index,
            typedOutgoing[ReferenceKind.Implementation],
            typedRelationLimit);
        AppendTypedOutgoingRelationships(
            sb,
            "extends",
            index,
            typedOutgoing[ReferenceKind.Inheritance],
            typedRelationLimit);
        AppendTypedInboundRelationships(
            sb,
            "implementations",
            index,
            typedInbound[ReferenceKind.Implementation],
            typedRelationLimit);
        AppendTypedInboundRelationships(
            sb,
            "subtypes",
            index,
            typedInbound[ReferenceKind.Inheritance],
            typedRelationLimit);

        sb.Append(depth == InspectDepth.Overview ? "\n## body preview\n" : "\n## body\n");
        var body = detail is null
            ? ExtractReader.BodyReadResult.Unavailable(ExtractReader.BodyUnavailableReason.NoSpanRecorded)
            : ExtractReader.ReadBody(dbPath, workspaceRoot, sym.FilePath,
                detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);
        ToolOutputPage? bodyPage = null;
        if (depth == InspectDepth.Overview)
        {
            var preview = BodyPreview(body);
            sb.Append(preview.Text ?? RenderBodyUnavailableNote(body.UnavailableReason));
            if (preview.Truncated)
                sb.Append("\n... body preview truncated (use depth=full)");
        }
        else
        {
            bodyPage = PageFullBody(body, detail, workspaceId, sym, continuation);
            sb.Append(bodyPage.Text);
        }

        bool refsTruncated =
            depth == InspectDepth.Full &&
            referenceEvidence.Coverage.ExactAvailable > relationLimit;
        string callName = EscapeCallString(sym.Name);
        if (bodyPage is { Truncated: true, Continuation: not null })
        {
            sb.Append('\n').Append(NextStepHint.Render(
                $"inspect target=\"{sym.SymbolId}\" depth=full continuation=\"{bodyPage.Continuation}\"",
                $"continue body at byte {bodyPage.EndOffset}"));
        }
        else if (refsTruncated)
            sb.Append('\n').Append(NextStepHint.Render(
                $"trace target=\"{callName}\" mode=refs limit={referenceEvidence.Coverage.ExactAvailable}",
                "full reference list"));
        else if (!sym.IsTest && referenceEvidence.Coverage.ExactAvailable >= ImpactHintMinReferences)
            sb.Append('\n').Append(NextStepHint.Render(
                $"impact target=\"{callName}\"",
                $"{referenceEvidence.Coverage.ExactAvailable} dependents"));

        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendTypedOutgoingRelationships(
        StringBuilder builder,
        string heading,
        ISymbolLookupIndex index,
        OutgoingReferenceEvidenceSet evidence,
        int limit)
    {
        if (evidence.Exact.Count > 0)
        {
            builder.Append("\n## ").Append(heading).Append('\n');
            foreach (OutgoingReferenceEvidence relationship in evidence.Exact)
            {
                IndexedSymbol? target = relationship.TargetSymbolId is null
                    ? null
                    : index.FindBySymbolId(relationship.TargetSymbolId);
                builder.Append(target?.Name ?? relationship.TargetName)
                    .Append("  ");
                AppendCompactLocation(
                    builder,
                    target?.FilePath ?? relationship.FilePath,
                    target is null ? relationship.StartLine : target.StartLine);
                builder.Append('\n');
            }
            AppendOmittedLine(builder, evidence.Coverage.ExactAvailable, limit, heading);
        }

        if (evidence.Fallback.Count > 0)
        {
            builder.Append("\n## ").Append(heading).Append(" fallback (unresolved)\n");
            foreach (OutgoingReferenceEvidence relationship in evidence.Fallback)
            {
                builder.Append(relationship.TargetName).Append("  ");
                AppendCompactLocation(builder, relationship.FilePath, relationship.StartLine);
                builder.Append('\n');
            }
            AppendOmittedLine(builder, evidence.Coverage.FallbackAvailable, limit, heading + " fallback");
        }
    }

    private static void AppendTypedInboundRelationships(
        StringBuilder builder,
        string heading,
        ISymbolLookupIndex index,
        ReferenceEvidenceSet evidence,
        int limit)
    {
        if (evidence.Exact.Count > 0)
        {
            builder.Append("\n## ").Append(heading).Append('\n');
            foreach (ReferenceEvidence relationship in evidence.Exact)
                AppendTypedInboundRelationship(builder, index, relationship);
            AppendOmittedLine(builder, evidence.Coverage.ExactAvailable, limit, heading);
        }

        if (evidence.Fallback.Count > 0)
        {
            builder.Append("\n## ").Append(heading).Append(" fallback (unresolved)\n");
            foreach (ReferenceEvidence relationship in evidence.Fallback)
                AppendTypedInboundRelationship(builder, index, relationship);
            AppendOmittedLine(builder, evidence.Coverage.FallbackAvailable, limit, heading + " fallback");
        }
    }

    private static void AppendTypedInboundRelationship(
        StringBuilder builder,
        ISymbolLookupIndex index,
        ReferenceEvidence relationship)
    {
        IndexedSymbol? source = relationship.ContainingSymbolId is null
            ? null
            : index.FindBySymbolId(relationship.ContainingSymbolId);
        builder.Append(source?.Name ?? "[unknown source]");
        if (source is null && relationship.ContainingSymbolId is not null)
            builder.Append(" [id=").Append(relationship.ContainingSymbolId).Append(']');
        builder.Append("  ");
        AppendCompactLocation(
            builder,
            source?.FilePath ?? relationship.FilePath,
            source is null ? relationship.StartLine : source.StartLine);
        builder.Append('\n');
    }

    private static void AppendCompactLocation(
        StringBuilder builder,
        string filePath,
        int? line)
    {
        builder.Append(filePath);
        if (line is > 0)
            builder.Append(':').Append(line.Value);
    }

    private static string RenderSymbolJson(
        ISymbolLookupIndex index,
        string dbPath,
        string workspaceRoot,
        string workspaceId,
        IndexedSymbol sym,
        InspectDepth depth,
        string? continuation,
        bool boundAgentOutput)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("symbol");
            bool isFullValueDeclaration = depth == InspectDepth.Full && IsValueDeclaration(sym);
            WriteSymbolObject(
                w,
                sym,
                detail,
                SignatureLimit(sym, depth),
                preserveSignatureWhitespace: isFullValueDeclaration,
                boundAgentOutput: boundAgentOutput);
            if (isFullValueDeclaration)
            {
                w.WriteBoolean("value_declaration_complete", IsCompleteValueDeclaration(sym));
                w.WriteString("body_role", "extractor_span_not_declaration");
            }

            if (depth != InspectDepth.Summary)
            {
                var complexity = ExtractReader.ReadSymbolComplexity(dbPath, sym.SymbolId);
                if (complexity is null)
                {
                    w.WriteNull("complexity");
                }
                else
                {
                    w.WritePropertyName("complexity");
                    w.WriteStartObject();
                    w.WriteString("algorithm_id", complexity.AlgorithmId);
                    w.WriteNumber("decision_count", complexity.DecisionCount);
                    w.WriteNumber("loop_count", complexity.LoopCount);
                    w.WriteNumber("max_nesting_depth", complexity.MaxNestingDepth);
                    if (complexity.ParameterCount is { } parameters)
                        w.WriteNumber("parameter_count", parameters);
                    else
                        w.WriteNull("parameter_count");
                    w.WriteNumber("covered_lines", complexity.CoveredLines);
                    w.WriteEndObject();
                }

                int relationLimit = depth == InspectDepth.Overview
                    ? OverviewRelationLimit
                    : boundAgentOutput ? ToolOutputBudget.McpRowLimit : RefLimit;
                int calleeLimit = depth == InspectDepth.Overview ? OverviewRelationLimit : FullCalleeLimit;
                int typedRelationLimit = depth == InspectDepth.Overview
                    ? OverviewRelationLimit
                    : boundAgentOutput ? ToolOutputBudget.McpRowLimit : FullCalleeLimit;
                ReferenceEvidenceBundle evidence = ReferenceEvidenceReader.ReadForSymbol(
                    dbPath,
                    sym.SymbolId,
                    new ReferenceEvidenceQuery(
                        new ReferenceEvidenceBounds(relationLimit, relationLimit)),
                    new ReferenceEvidenceQuery(
                        new ReferenceEvidenceBounds(calleeLimit, calleeLimit),
                        ReferenceKind.Call),
                    new ReferenceEvidenceBounds(typedRelationLimit, typedRelationLimit),
                    TypedRelationshipKinds);

                // Bounded collections report what they dropped. A short array with no accounting reads as
                // complete, which is the one thing a structured consumer cannot recover from.
                int childLimit = depth == InspectDepth.Overview
                    ? OverviewChildLimit
                    : boundAgentOutput ? ToolOutputBudget.McpRowLimit : int.MaxValue;
                IndexedSymbol[] allChildren = index.FindChildren(sym.SymbolId).ToArray();
                w.WritePropertyName("children");
                WriteSymbolArray(w, allChildren.Take(childLimit));
                w.WriteNumber("children_available", allChildren.Length);
                w.WriteBoolean("children_truncated", allChildren.Length > childLimit);

                ReferenceEvidenceSet referenceEvidence = evidence.Inbound;
                IReadOnlyList<ReferenceEvidence> refs = referenceEvidence.Exact;
                w.WritePropertyName("refs");
                w.WriteStartArray();
                foreach (ReferenceEvidence reference in refs)
                    WriteInboundReference(w, reference);
                w.WriteEndArray();

                w.WritePropertyName("reference_fallback");
                w.WriteStartArray();
                foreach (ReferenceEvidence reference in referenceEvidence.Fallback)
                    WriteInboundReference(w, reference);
                w.WriteEndArray();
                WriteInboundCoverage(w, referenceEvidence.Coverage);

                List<ContainingSymbol> callers = ResolveContainingSymbols(
                    index, referenceEvidence.ExactCallerSymbolIds, refs, relationLimit,
                    out int callersAvailable);
                w.WritePropertyName("callers");
                w.WriteStartArray();
                foreach (var c in callers)
                    w.WriteStringValue(c.Name);
                w.WriteEndArray();
                w.WriteNumber("callers_available", callersAvailable);
                w.WriteBoolean("callers_truncated", callersAvailable > callers.Count);

                List<ContainingSymbol> referencedBy = ResolveContainingSymbols(
                    index, referenceEvidence.ExactReferencedBySymbolIds, refs, relationLimit,
                    out int referencedByAvailable);
                w.WritePropertyName("referenced_by");
                w.WriteStartArray();
                foreach (var reference in referencedBy)
                    w.WriteStringValue(reference.Name);
                w.WriteEndArray();
                w.WriteNumber("referenced_by_available", referencedByAvailable);
                w.WriteBoolean("referenced_by_truncated", referencedByAvailable > referencedBy.Count);

                IReadOnlyList<IndexedSymbol> testLocations =
                    ResolveTestLocations(index, referenceEvidence);
                w.WritePropertyName("test_locations");
                WriteSymbolArray(w, testLocations.Take(relationLimit));
                int testLocationReturnedCount = Math.Min(testLocations.Count, relationLimit);
                w.WriteNumber("test_locations_total_count", testLocations.Count);
                w.WriteNumber("test_locations_returned_count", testLocationReturnedCount);
                w.WriteNumber("test_locations_omitted_count", testLocations.Count - testLocationReturnedCount);
                w.WriteBoolean("test_locations_truncated", testLocationReturnedCount < testLocations.Count);

                OutgoingReferenceEvidenceSet outgoing = evidence.Outgoing;
                w.WritePropertyName("callees");
                w.WriteStartArray();
                foreach (OutgoingReferenceEvidence callee in outgoing.Exact)
                    WriteOutgoingReference(w, index, callee);
                w.WriteEndArray();

                w.WritePropertyName("callee_fallback");
                w.WriteStartArray();
                foreach (OutgoingReferenceEvidence callee in outgoing.Fallback)
                    WriteOutgoingReference(w, index, callee);
                w.WriteEndArray();
                WriteOutgoingCoverage(w, outgoing.Coverage);

                IReadOnlyDictionary<ReferenceKind, OutgoingReferenceEvidenceSet> typedOutgoing =
                    evidence.OutgoingKinds;
                IReadOnlyDictionary<ReferenceKind, ReferenceEvidenceSet> typedInbound =
                    evidence.InboundKinds;
                WriteTypedOutgoingRelationshipSet(
                    w,
                    "implements",
                    index,
                    typedOutgoing[ReferenceKind.Implementation]);
                WriteTypedOutgoingRelationshipSet(
                    w,
                    "extends",
                    index,
                    typedOutgoing[ReferenceKind.Inheritance]);
                WriteTypedInboundRelationshipSet(
                    w,
                    "implementations",
                    index,
                    typedInbound[ReferenceKind.Implementation]);
                WriteTypedInboundRelationshipSet(
                    w,
                    "subtypes",
                    index,
                    typedInbound[ReferenceKind.Inheritance]);

                var body = detail is null
                    ? ExtractReader.BodyReadResult.Unavailable(ExtractReader.BodyUnavailableReason.NoSpanRecorded)
                    : ExtractReader.ReadBody(dbPath, workspaceRoot, sym.FilePath,
                        detail.BodyStartByte, detail.BodyEndByte, detail.BodyStartLine, detail.BodyEndLine);
                if (depth == InspectDepth.Overview)
                {
                    var preview = BodyPreview(body);
                    if (preview.Text is null)
                    {
                        w.WriteNull("body_preview");
                        w.WriteString("body_unavailable_reason", BodyUnavailableReasonJson(body.UnavailableReason));
                    }
                    else
                    {
                        w.WriteString("body_preview", preview.Text);
                    }
                    w.WriteBoolean("body_preview_truncated", preview.Truncated);
                }
                else if (body.Text is null && string.IsNullOrWhiteSpace(continuation))
                {
                    w.WriteNull("body");
                    w.WriteString("body_unavailable_reason", BodyUnavailableReasonJson(body.UnavailableReason));
                }
                else
                {
                    ToolOutputPage bodyPage =
                        PageFullBody(body, detail, workspaceId, sym, continuation);
                    w.WriteString("body", bodyPage.Text);
                    w.WriteNumber("body_start_offset", bodyPage.StartOffset);
                    w.WriteNumber("body_end_offset", bodyPage.EndOffset);
                    w.WriteBoolean("body_truncated", bodyPage.Truncated);
                    if (bodyPage.Continuation is null)
                        w.WriteNull("body_continuation");
                    else
                        w.WriteString("body_continuation", bodyPage.Continuation);
                }
            }

            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static ToolOutputPage PageFullBody(
        ExtractReader.BodyReadResult body,
        SymbolDetail? detail,
        string workspaceId,
        IndexedSymbol symbol,
        string? continuation)
    {
        if (body.Text is null)
        {
            if (!string.IsNullOrWhiteSpace(continuation))
            {
                throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                    "continuation_body_unavailable",
                    "The symbol body is no longer available for continuation."));
            }

            return new ToolOutputPage(
                RenderBodyUnavailableNote(body.UnavailableReason),
                0,
                0,
                Truncated: false,
                Continuation: null);
        }

        int bodyBytes = Encoding.UTF8.GetByteCount(body.Text);
        if (bodyBytes <= ToolOutputBudget.InspectFullBodyMaxBytes &&
            string.IsNullOrWhiteSpace(continuation))
        {
            return new ToolOutputPage(body.Text, 0, bodyBytes, Truncated: false, Continuation: null);
        }

        if (detail is null ||
            string.IsNullOrWhiteSpace(detail.BodyHash) ||
            detail.BodyStartByte is not { } startByte ||
            detail.BodyEndByte is not { } endByte ||
            startByte < 0 ||
            endByte <= startByte)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Unavailable(
                "continuation_identity_unavailable",
                "The extracted body lacks the hash or source span required for a safe continuation.",
                [new ToolDiagnosticAction(
                    "workspace(operation=\"refresh\")",
                    "refresh extraction identity before retrying")]));
        }

        return ToolOutputBudget.PageBody(
            body.Text,
            ToolOutputBudget.InspectFullBodyMaxBytes,
            new ToolContinuationIdentity(
                workspaceId,
                symbol.SymbolId,
                detail.BodyHash,
                startByte,
                endByte),
            continuation);
    }

    // Escape a symbol name for embedding inside a quoted tool-call argument, matching context's NextInspectLine
    // precedent: backslash first, then quote, so a name containing either stays a single well-formed hint line.
    private static string EscapeCallString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeDiagnosticTarget(string value) =>
        ToolDiagnosticText.EscapeCallArgument(value);

    private static void AppendOmittedLine(
        StringBuilder sb,
        int total,
        int visible,
        string label,
        string recovery = "use depth=full")
    {
        if (total > visible)
            sb.Append("... ").Append(total - visible).Append(" more ").Append(label)
                .Append(" (").Append(recovery).Append(")\n");
    }

    private static void AppendGroupedReferences(StringBuilder sb, IEnumerable<ReferenceEvidence> refs)
    {
        var order = new List<string>();
        var linesByFile = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            if (!linesByFile.TryGetValue(r.FilePath, out var lines))
            {
                lines = new List<int>();
                linesByFile[r.FilePath] = lines;
                order.Add(r.FilePath);
            }
            lines.Add(r.StartLine ?? 0);
        }
        foreach (var file in order)
            sb.Append(file).Append(':').Append(string.Join(',', linesByFile[file])).Append('\n');
    }

    private readonly record struct DistinctCallee(
        string TargetSymbolId,
        string Name,
        string FilePath,
        int StartLine,
        int Count,
        ReferenceEvidenceSource Source,
        double Confidence);

    private readonly record struct DistinctFallbackCallee(
        string Name,
        string FilePath,
        int StartLine,
        int Count,
        double Confidence);

    private readonly record struct ContainingSymbol(string SymbolId, string Name, string FilePath, int StartLine);

    private static List<DistinctCallee> DistinctCallees(
        ISymbolLookupIndex index,
        IReadOnlyList<OutgoingReferenceEvidence> callees)
    {
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<DistinctCallee>();
        foreach (var c in callees)
        {
            if (c.TargetSymbolId is not { } targetId)
                continue;
            if (indexById.TryGetValue(targetId, out var i))
            {
                result[i] = result[i] with { Count = result[i].Count + 1 };
            }
            else
            {
                IndexedSymbol? target = index.FindBySymbolId(targetId);
                indexById[targetId] = result.Count;
                result.Add(new DistinctCallee(
                    targetId,
                    target?.Name ?? c.TargetName,
                    target?.FilePath ?? c.FilePath,
                    target?.StartLine ?? c.StartLine ?? 0,
                    1,
                    c.Source,
                    c.Confidence));
            }
        }
        return result;
    }

    private static List<DistinctFallbackCallee> DistinctFallbackCallees(
        IReadOnlyList<OutgoingReferenceEvidence> callees)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<DistinctFallbackCallee>();
        foreach (var c in callees)
        {
            if (indexByName.TryGetValue(c.TargetName, out int index))
            {
                result[index] = result[index] with { Count = result[index].Count + 1 };
                continue;
            }

            indexByName[c.TargetName] = result.Count;
            result.Add(new DistinctFallbackCallee(
                c.TargetName,
                c.FilePath,
                c.StartLine ?? 0,
                1,
                c.Confidence));
        }
        return result;
    }

    private static List<ContainingSymbol> ResolveContainingSymbols(
        ISymbolLookupIndex index,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<ReferenceEvidence> displayedReferences,
        int limit) =>
        ResolveContainingSymbols(index, symbolIds, displayedReferences, limit, out _);

    /// <summary>
    /// <see cref="ResolveContainingSymbols(ISymbolLookupIndex,IReadOnlyList{string},IReadOnlyList{ReferenceEvidence},int)"/>,
    /// additionally reporting how many distinct containing symbols existed BEFORE <paramref name="limit"/> was
    /// applied, so a bounded render can say it truncated instead of returning a short array that reads as
    /// complete.
    /// </summary>
    private static List<ContainingSymbol> ResolveContainingSymbols(
        ISymbolLookupIndex index,
        IReadOnlyList<string> symbolIds,
        IReadOnlyList<ReferenceEvidence> displayedReferences,
        int limit,
        out int availableCount)
    {
        var allowedIds = symbolIds.ToHashSet(StringComparer.Ordinal);
        string[] distinctIds = displayedReferences
            .Where(reference =>
                reference.ContainingSymbolId is { } containingId &&
                allowedIds.Contains(containingId))
            .OrderBy(static reference => reference.FilePath, StringComparer.Ordinal)
            .ThenBy(static reference => reference.StartLine)
            .Select(static reference => reference.ContainingSymbolId!)
            .Concat(symbolIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        availableCount = distinctIds.Length;
        string[] selectedIds = distinctIds.Take(limit).ToArray();
        var result = new List<ContainingSymbol>(selectedIds.Length);
        foreach (string containingId in selectedIds)
        {
            IndexedSymbol? symbol = index.FindBySymbolId(containingId);
            if (symbol is not null)
            {
                result.Add(new ContainingSymbol(
                    containingId,
                    symbol.Name,
                    symbol.FilePath,
                    symbol.StartLine));
                continue;
            }

            ReferenceEvidence? reference = displayedReferences.FirstOrDefault(row =>
                string.Equals(row.ContainingSymbolId, containingId, StringComparison.Ordinal));
            result.Add(new ContainingSymbol(
                containingId,
                containingId,
                reference?.FilePath ?? string.Empty,
                reference?.StartLine ?? 0));
        }
        return result
            .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.StartLine)
            .ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<IndexedSymbol> ResolveTestLocations(
        ISymbolLookupIndex index,
        ReferenceEvidenceSet evidence)
    {
        return evidence.ExactCallerSymbolIds
            .Concat(evidence.ExactReferencedBySymbolIds)
            .Distinct(StringComparer.Ordinal)
            .Select(index.FindBySymbolId)
            .Where(static symbol => symbol is { IsTest: true })
            .Select(static symbol => symbol!)
            .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.StartLine)
            .ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ToArray();
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

    private static string ResolutionStatusLabel(ReferenceResolutionStatus status) =>
        status == ReferenceResolutionStatus.Exact ? "exact" : "fallback";

    private static void WriteInboundReference(Utf8JsonWriter writer, ReferenceEvidence reference)
    {
        writer.WriteStartObject();
        writer.WriteString("reference_site_id", reference.ReferenceSiteId);
        writer.WriteBoolean("is_exact", reference.IsExact);
        writer.WriteString("site_provenance", reference.SiteProvenance);
        writer.WriteString("target_symbol_id", reference.TargetSymbolId);
        writer.WriteString("file", reference.FilePath);
        WriteNullableNumber(writer, "line", reference.StartLine);
        WriteNullableNumber(writer, "column", reference.StartColumn);
        WriteNullableNumber(writer, "end_line", reference.EndLine);
        WriteNullableNumber(writer, "end_column", reference.EndColumn);
        WriteNullableNumber(writer, "start_byte", reference.StartByte);
        WriteNullableNumber(writer, "end_byte", reference.EndByte);
        writer.WriteString("kind", reference.SourceKind);
        writer.WriteString("resolution_status", ResolutionStatusLabel(reference.ResolutionStatus));
        writer.WriteString("source", EvidenceSourceLabel(reference.Source));
        WriteNullableNumber(writer, "resolution_tier", reference.ResolutionTier);
        writer.WriteNumber("confidence", reference.Confidence);
        if (reference.ContainingSymbolId is null)
            writer.WriteNull("containing_symbol_id");
        else
            writer.WriteString("containing_symbol_id", reference.ContainingSymbolId);
        writer.WriteEndObject();
    }

    private static void WriteOutgoingReference(
        Utf8JsonWriter writer,
        ISymbolLookupIndex index,
        OutgoingReferenceEvidence reference)
    {
        IndexedSymbol? target = reference.TargetSymbolId is null
            ? null
            : index.FindBySymbolId(reference.TargetSymbolId);
        writer.WriteStartObject();
        writer.WriteString("reference_site_id", reference.ReferenceSiteId);
        writer.WriteBoolean("is_exact", reference.IsExact);
        writer.WriteString("site_provenance", reference.SiteProvenance);
        if (reference.TargetSymbolId is null)
            writer.WriteNull("target_symbol_id");
        else
            writer.WriteString("target_symbol_id", reference.TargetSymbolId);
        writer.WriteString("name", target?.Name ?? reference.TargetName);
        if (target is null)
        {
            writer.WriteNull("definition_file");
            writer.WriteNull("definition_line");
        }
        else
        {
            writer.WriteString("definition_file", target.FilePath);
            writer.WriteNumber("definition_line", target.StartLine);
        }
        writer.WriteString("site_file", reference.FilePath);
        WriteNullableNumber(writer, "site_line", reference.StartLine);
        writer.WriteString("kind", reference.SourceKind);
        writer.WriteString("resolution_status", ResolutionStatusLabel(reference.ResolutionStatus));
        writer.WriteString("source", EvidenceSourceLabel(reference.Source));
        WriteNullableNumber(writer, "resolution_tier", reference.ResolutionTier);
        writer.WriteNumber("confidence", reference.Confidence);
        writer.WriteEndObject();
    }

    private static void WriteTypedOutgoingRelationshipSet(
        Utf8JsonWriter writer,
        string propertyName,
        ISymbolLookupIndex index,
        OutgoingReferenceEvidenceSet evidence)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName("exact");
        writer.WriteStartArray();
        foreach (OutgoingReferenceEvidence relationship in evidence.Exact)
            WriteOutgoingReference(writer, index, relationship);
        writer.WriteEndArray();
        writer.WritePropertyName("fallback");
        writer.WriteStartArray();
        foreach (OutgoingReferenceEvidence relationship in evidence.Fallback)
            WriteOutgoingReference(writer, index, relationship);
        writer.WriteEndArray();
        writer.WritePropertyName("coverage");
        WriteOutgoingCoverageObject(writer, evidence.Coverage);
        writer.WriteEndObject();
    }

    private static void WriteTypedInboundRelationshipSet(
        Utf8JsonWriter writer,
        string propertyName,
        ISymbolLookupIndex index,
        ReferenceEvidenceSet evidence)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        writer.WritePropertyName("exact");
        writer.WriteStartArray();
        foreach (ReferenceEvidence relationship in evidence.Exact)
            WriteInboundRelationship(writer, index, relationship);
        writer.WriteEndArray();
        writer.WritePropertyName("fallback");
        writer.WriteStartArray();
        foreach (ReferenceEvidence relationship in evidence.Fallback)
            WriteInboundRelationship(writer, index, relationship);
        writer.WriteEndArray();
        writer.WritePropertyName("coverage");
        WriteInboundCoverageObject(writer, evidence.Coverage);
        writer.WriteEndObject();
    }

    private static void WriteInboundRelationship(
        Utf8JsonWriter writer,
        ISymbolLookupIndex index,
        ReferenceEvidence reference)
    {
        IndexedSymbol? source = reference.ContainingSymbolId is null
            ? null
            : index.FindBySymbolId(reference.ContainingSymbolId);
        writer.WriteStartObject();
        if (reference.ContainingSymbolId is null)
            writer.WriteNull("source_symbol_id");
        else
            writer.WriteString("source_symbol_id", reference.ContainingSymbolId);
        if (source is null)
            writer.WriteNull("name");
        else
            writer.WriteString("name", source.Name);
        if (source is null)
        {
            writer.WriteNull("definition_file");
            writer.WriteNull("definition_line");
        }
        else
        {
            writer.WriteString("definition_file", source.FilePath);
            writer.WriteNumber("definition_line", source.StartLine);
        }
        writer.WriteString("site_file", reference.FilePath);
        WriteNullableNumber(writer, "site_line", reference.StartLine);
        writer.WriteString("kind", reference.SourceKind);
        writer.WriteString("resolution_status", ResolutionStatusLabel(reference.ResolutionStatus));
        writer.WriteString("source", EvidenceSourceLabel(reference.Source));
        WriteNullableNumber(writer, "resolution_tier", reference.ResolutionTier);
        writer.WriteNumber("confidence", reference.Confidence);
        writer.WriteEndObject();
    }

    private static void WriteInboundCoverage(Utf8JsonWriter writer, ReferenceEvidenceCoverage coverage)
    {
        writer.WritePropertyName("reference_coverage");
        WriteInboundCoverageObject(writer, coverage);
    }

    private static void WriteInboundCoverageObject(
        Utf8JsonWriter writer,
        ReferenceEvidenceCoverage coverage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("exact_available", coverage.ExactAvailable);
        writer.WriteNumber("exact_returned", coverage.ExactReturned);
        writer.WriteNumber("fallback_available", coverage.FallbackAvailable);
        writer.WriteNumber("fallback_returned", coverage.FallbackReturned);
        writer.WriteBoolean("exact_truncated", coverage.ExactTruncated);
        writer.WriteBoolean("fallback_truncated", coverage.FallbackTruncated);
        writer.WriteString("fallback_status", coverage.FallbackStatus switch
        {
            ReferenceFallbackStatus.NoCandidates => "no_candidates",
            ReferenceFallbackStatus.Available => "available",
            ReferenceFallbackStatus.SuppressedAmbiguousName => "suppressed_ambiguous_name",
            _ => throw new ArgumentOutOfRangeException(nameof(coverage), coverage.FallbackStatus, null),
        });
        writer.WriteEndObject();
    }

    private static void WriteOutgoingCoverage(
        Utf8JsonWriter writer,
        OutgoingReferenceEvidenceCoverage coverage)
    {
        writer.WritePropertyName("callee_coverage");
        WriteOutgoingCoverageObject(writer, coverage);
    }

    private static void WriteOutgoingCoverageObject(
        Utf8JsonWriter writer,
        OutgoingReferenceEvidenceCoverage coverage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("exact_available", coverage.ExactAvailable);
        writer.WriteNumber("exact_returned", coverage.ExactReturned);
        writer.WriteNumber("fallback_available", coverage.FallbackAvailable);
        writer.WriteNumber("fallback_returned", coverage.FallbackReturned);
        writer.WriteBoolean("exact_truncated", coverage.ExactTruncated);
        writer.WriteBoolean("fallback_truncated", coverage.FallbackTruncated);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }

    private readonly record struct BodyPreviewResult(string? Text, bool Truncated);

    private static BodyPreviewResult BodyPreview(ExtractReader.BodyReadResult body)
    {
        if (body.Text is null)
            return new BodyPreviewResult(null, Truncated: false);

        string normalized = body.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        // Drop doc-comment lines before spending the line/char budget: an overview preview of a container
        // otherwise re-prints member docs that already duplicate the `doc:` section above. Dropping alone is
        // NOT truncation — the caps below still decide Truncated against the filtered code lines.
        List<string> lines = FilterDocCommentLines(normalized.Split('\n'));
        int lineCount = Math.Min(lines.Count, OverviewBodyPreviewMaxLines);
        string preview = string.Join('\n', lines.Take(lineCount));
        bool truncated = lines.Count > lineCount;
        if (preview.Length > OverviewBodyPreviewMaxChars)
        {
            preview = preview[..OverviewBodyPreviewMaxChars].TrimEnd();
            truncated = true;
        }
        return new BodyPreviewResult(preview, truncated);
    }

    // Remove doc-comment lines (C#/Rust `///` and `//!`, and `/** … */` blocks inclusive) so the overview body
    // preview spends its budget on code rather than member docs already shown in the `doc:` section. Ordinary
    // `//` and `#` comments are code commentary — kept. Python docstrings (`"""`) are string literals, not
    // doc-comment syntax, so they are left untouched.
    private static List<string> FilterDocCommentLines(IReadOnlyList<string> lines)
    {
        var result = new List<string>(lines.Count);
        bool inBlockDoc = false;
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (inBlockDoc)
            {
                // Every line of an open `/** … */` block is dropped, up to and including the closing `*/`.
                if (trimmed.Contains("*/", StringComparison.Ordinal))
                    inBlockDoc = false;
                continue;
            }
            if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                trimmed.StartsWith("//!", StringComparison.Ordinal))
                continue;
            if (trimmed.StartsWith("/**", StringComparison.Ordinal))
            {
                // A single-line `/** … */` closes on the same line; a multi-line one opens the block.
                if (!trimmed.Contains("*/", StringComparison.Ordinal))
                    inBlockDoc = true;
                continue;
            }
            result.Add(line);
        }
        return result;
    }

    // ---------- candidates ----------

    private static string RenderCandidatesCompact(string target, IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append(CandidateOutput.Header(
            matches,
            supportsScope: true,
            fallback: "Multiple candidates — pass a more specific target:")).Append('\n');
        foreach (var s in CandidateOutput.Visible(matches))
            sb.Append(SymbolLine(s)).Append('\n');
        CandidateOutput.AppendRemainderNote(sb, matches.Count);
        CandidateOutput.AppendRerunExamples(sb, target, matches, supportsScope: true);
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderCandidatesJson(string target, IReadOnlyList<IndexedSymbol> matches)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var w = NewWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("candidates");
        WriteSymbolArray(w, matches);
        w.WriteStartArray("rerun_examples");
        foreach (string example in CandidateOutput.RerunExamples(target, matches, supportsScope: true))
            w.WriteStringValue(example);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        return Utf8(buffer);
    }

    // Additive JSON shape: `not_found` is unchanged; `closest` (near misses or scope-masked matches) and
    // `scope_missed` appear only when the resolver produced them.
    private static string RenderNotFoundJson(TargetResolution.NotFound nf)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var w = NewWriter(buffer);
        w.WriteStartObject();
        w.WriteString("not_found", nf.Target);
        if (nf.Suggestions is { Count: > 0 } suggestions)
        {
            w.WritePropertyName("closest");
            WriteSymbolArray(w, suggestions);
        }
        if (nf.ScopeMissed is { } scopeMissed)
            w.WriteString("scope_missed", scopeMissed);
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
        if (SignatureAddsInfo(s))
            sb.Append("  ").Append(Truncate(InlineSignature(s.Signature!), ToolRenderLimits.SignatureMaxLength));
        return sb.ToString();
    }

    private static void WriteSymbolArray(Utf8JsonWriter w, IEnumerable<IndexedSymbol> symbols)
    {
        w.WriteStartArray();
        foreach (var s in symbols)
            WriteSymbolObject(w, s, detail: null);
        w.WriteEndArray();
    }

    private static void WriteFileSymbolArray(
        Utf8JsonWriter w,
        IEnumerable<IndexedSymbol> symbols,
        IReadOnlyList<IndexedSymbol> hierarchy)
    {
        IReadOnlyDictionary<string, IndexedSymbol> byId = BuildSymbolMap(hierarchy);
        w.WriteStartArray();
        foreach (IndexedSymbol symbol in symbols)
            WriteSymbolObject(w, symbol, detail: null, nestingDepth: FileNestingDepth(symbol, byId));
        w.WriteEndArray();
    }

    private static IReadOnlyDictionary<string, IndexedSymbol> BuildSymbolMap(
        IReadOnlyList<IndexedSymbol> symbols)
    {
        var byId = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        foreach (IndexedSymbol symbol in symbols)
            byId.TryAdd(symbol.SymbolId, symbol);
        return byId;
    }

    private static void WriteSymbolObject(
        Utf8JsonWriter w,
        IndexedSymbol s,
        SymbolDetail? detail,
        int signatureMaxLength = ToolRenderLimits.SignatureMaxLength,
        bool preserveSignatureWhitespace = false,
        int? nestingDepth = null,
        bool boundAgentOutput = false)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteString("kind", s.Kind);
        w.WriteString("language", s.Language);
        w.WriteString("file", s.FilePath);
        w.WriteNumber("line", s.StartLine);
        if (s.EndLine > 0) w.WriteNumber("end_line", s.EndLine); else w.WriteNull("end_line");
        if (s.ParentId is null) w.WriteNull("parent_symbol_id"); else w.WriteString("parent_symbol_id", s.ParentId);
        if (nestingDepth is { } depth)
            w.WriteNumber("nesting_depth", depth);
        if (s.Signature is null) w.WriteNull("signature");
        else w.WriteString(
            "signature",
            Truncate(
                preserveSignatureWhitespace ? s.Signature : InlineSignature(s.Signature),
                signatureMaxLength));
        w.WriteString("symbol_id", s.SymbolId);
        TestRoleEvidence testEvidence = s.TestEvidence;
        w.WritePropertyName("test_evidence");
        w.WriteStartObject();
        w.WriteBoolean("is_test", testEvidence.IsTest);
        w.WriteBoolean("test_case", testEvidence.IsCase);
        w.WriteBoolean("test_container", testEvidence.IsContainer);
        w.WriteBoolean("test_lifecycle", testEvidence.IsLifecycle);
        w.WriteString("status", testEvidence.Status);
        if (testEvidence.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", testEvidence.Reason);
        w.WriteEndObject();
        if (detail is not null)
        {
            if (detail.DocComment is null)
            {
                w.WriteNull("doc");
            }
            else
            {
                bool docTruncated =
                    boundAgentOutput &&
                    Encoding.UTF8.GetByteCount(detail.DocComment) > ToolOutputBudget.InspectMcpDocMaxBytes;
                w.WriteString(
                    "doc",
                    docTruncated
                        ? ToolOutputBudget.TruncateUtf8(
                            detail.DocComment,
                            ToolOutputBudget.InspectMcpDocMaxBytes,
                            "…")
                        : detail.DocComment);
                if (docTruncated)
                    w.WriteBoolean("doc_truncated", true);
            }
            if (detail.Visibility is null) w.WriteNull("visibility"); else w.WriteString("visibility", detail.Visibility);
        }
        w.WriteEndObject();
    }

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

    private static string JsonString(string value) => ServerJson.String(value);

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
            ExtractReader.BodyUnavailableReason.InvalidEncoding => "invalid encoding",
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
            ExtractReader.BodyUnavailableReason.InvalidEncoding => "invalid_encoding",
            ExtractReader.BodyUnavailableReason.InvalidSpan => "invalid_span",
            _ => "unknown",
        };

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string InlineSignature(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
