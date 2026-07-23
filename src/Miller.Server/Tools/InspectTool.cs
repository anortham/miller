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
        "Inspect a file or symbol you can already name. A file path lists its symbols; a symbol name gives " +
        "definition, signature, docs — depth=overview adds bounded refs/callers/callees and a body preview " +
        "(the right first symbol read); depth=full adds relation lists and a bounded body page. Use before reading " +
        "any entire file. NOT for: discovering which symbol matters in an unfamiliar area (use context) or full " +
        "reference lists across the repo (use trace mode=refs). Example: inspect target=FullRebuildPromotion " +
        "depth=overview.")]
    public string Inspect(
        [Description("A file path or a symbol name/id (smart-resolved).")] string target,
        [Description("summary|overview|full. overview adds bounded refs/callers/callees/body preview; full adds complete body.")]
        string depth = "summary",
        [Description("Filter a file listing to one kind (function/class/...). Optional.")] string? kind = null,
        [Description("Disambiguate an ambiguous symbol name to a file. Optional.")] string? scope = null,
        [Description("Max symbols when listing a file. Default 50.")] int limit = 50,
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
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            InspectDepth parsedDepth = ParseDepth(depth);

            WorkspaceSymbolSearchContext context =
                _workspaceSearchProvider.ResolveSymbolSearch(workspace_id, ensureFresh);
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
                limit,
                json,
                continuation,
                out int count,
                out ToolDiagnostic? diagnostic,
                compactBanner);

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
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
            }
            if (diagnostic is not null)
                output = ToolDiagnosticRenderer.Attach(
                    "inspect",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            return output;
        }
        catch (Exception ex)
        {
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

    private const int RefLimit = 50;
    private const int OverviewRelationLimit = 3;
    private const int OverviewChildLimit = 5;
    private const int OverviewBodyPreviewMaxLines = 16;
    private const int OverviewBodyPreviewMaxChars = 700;

    // Minimum name-based reference count at which a compact symbol read (overview/full) earns a trailing
    // "run impact" nudge. Below this the symbol is not hot enough for the hint to add value.
    private const int ImpactHintMinReferences = 4;

    private enum InspectDepth
    {
        Summary,
        Overview,
        Full,
    }

    private static InspectDepth ParseDepth(string? depth) =>
        string.Equals(depth, "full", StringComparison.OrdinalIgnoreCase)
            ? InspectDepth.Full
            : string.Equals(depth, "overview", StringComparison.OrdinalIgnoreCase)
                ? InspectDepth.Overview
                : InspectDepth.Summary;

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
            continuation: null);
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
            continuation);
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
        string? continuation)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        diagnostic = null;
        if (!string.IsNullOrWhiteSpace(continuation) && depth != InspectDepth.Full)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "continuation_not_applicable",
                "Inspect continuations are valid only with depth=full.",
                [new ToolDiagnosticAction(
                    $"inspect(target=\"{EscapeDiagnosticTarget(target)}\", depth=\"full\")",
                    "restart the full-body read")]));
        }

        var resolution = resolver.Resolve(target, scope);
        if (!string.IsNullOrWhiteSpace(continuation) && resolution is not TargetResolution.Symbol)
        {
            throw new ToolDiagnosticException(ToolDiagnostic.Refusal(
                "continuation_target_mismatch",
                "Inspect continuations can resume only the exact symbol body that created them.",
                [new ToolDiagnosticAction(
                    $"inspect(target=\"{EscapeDiagnosticTarget(target)}\", depth=\"full\")",
                    "restart inspection without the continuation")]));
        }

        switch (resolution)
        {
            case TargetResolution.File file:
                string fileOutput = RenderFile(index, file.Path, kind, limit, json, out resultCount);
                if (resultCount == 0)
                {
                    diagnostic = ToolDiagnostic.ExpectedEmpty(
                        "no_file_symbols",
                        $"No indexed symbols matched '{file.Path}' and the requested filters.");
                }
                return ReadToolWorkspaceRouting.PrefixCompact(
                    fileOutput,
                    json ? null : compactBanner);

            case TargetResolution.Symbol sym:
                resultCount = 1;
                string symbolOutput = json
                    ? RenderSymbolJson(
                        index, dbPath, workspaceRoot, workspaceId, sym.Value, depth, continuation)
                    : RenderSymbolCompact(
                        index, dbPath, workspaceRoot, workspaceId, sym.Value, depth, continuation);
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
                diagnostic = ToolDiagnostic.ExpectedEmpty(
                    "not_found",
                    $"No indexed file or symbol matched '{target}'.",
                    [new ToolDiagnosticAction(
                        $"search(query=\"{EscapeDiagnosticTarget(target)}\")",
                        "find the canonical file or symbol identity")]);
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
        string? compactBanner)
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
            continuation);
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

        if (json)
        {
            int jsonPage = Math.Min(limit, all.Count);
            resultCount = jsonPage;
            var buffer = new ArrayBufferWriter<byte>();
            using var w = NewWriter(buffer);
            w.WriteStartObject();
            w.WriteString("file", path);
            w.WritePropertyName("children");
            WriteSymbolArray(w, all.Take(jsonPage));
            w.WriteEndObject();
            w.Flush();
            return Utf8(buffer);
        }

        var visible = string.IsNullOrWhiteSpace(kind)
            ? all.Where(static s => !IsLowSignalKind(s.Kind)).ToList()
            : all;
        int compactPage = Math.Min(limit, visible.Count);
        resultCount = compactPage;

        if (visible.Count == 0)
            return RenderNoVisibleFileSymbols(path, all, kind);

        var sb = new StringBuilder();
        sb.Append("# ").Append(path).Append('\n');
        AppendFileSymbolGroups(sb, visible.Take(compactPage));
        int remainder = visible.Count - compactPage;
        if (remainder > 0)
            sb.Append("… ").Append(remainder).Append(" more (raise limit)\n");
        if (string.IsNullOrWhiteSpace(kind))
            AppendHiddenLowSignalNote(sb, all);
        return sb.ToString().TrimEnd('\n');
    }

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

    private static void AppendFileSymbolGroups(StringBuilder sb, IEnumerable<IndexedSymbol> symbols)
    {
        foreach (var group in symbols
                     .GroupBy(static s => s.Kind, StringComparer.Ordinal)
                     .OrderBy(static g => KindRank(g.Key))
                     .ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            var items = group.OrderBy(static s => s.StartLine).ThenBy(static s => s.Name, StringComparer.Ordinal).ToList();
            sb.Append(group.Key).Append(" (").Append(items.Count).Append(")\n");
            foreach (IndexedSymbol symbol in items)
                sb.Append("  ").Append(FileSymbolLine(symbol)).Append('\n');
        }
    }

    private static string FileSymbolLine(IndexedSymbol s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Name).Append("  :").Append(s.StartLine);
        if (SignatureAddsInfo(s))
            sb.Append("  ").Append(Truncate(InlineSignature(s.Signature!), ToolRenderLimits.SignatureMaxLength));
        return sb.ToString();
    }

    // Some extractors emit a signature that is just the symbol name again (e.g. bare fields); repeating it
    // next to the name spends tokens on nothing.
    private static bool SignatureAddsInfo(IndexedSymbol s) =>
        !string.IsNullOrEmpty(s.Signature) && !string.Equals(s.Signature!.Trim(), s.Name, StringComparison.Ordinal);

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
        string? continuation)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var sb = new StringBuilder();
        sb.Append("# ").Append(sym.Name).Append("  (").Append(sym.Kind).Append(")\n");
        sb.Append(sym.FilePath).Append(':').Append(sym.StartLine).Append('\n');
        if (SignatureAddsInfo(sym))
            sb.Append(Truncate(sym.Signature!, ToolRenderLimits.SignatureMaxLength)).Append('\n');
        if (detail is not null && !string.IsNullOrEmpty(detail.Visibility))
            sb.Append("visibility: ").Append(detail.Visibility).Append('\n');
        if (detail is not null && !string.IsNullOrEmpty(detail.DocComment))
            sb.Append("doc: ").Append(detail.DocComment).Append('\n');

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

        int relationLimit = depth == InspectDepth.Overview ? OverviewRelationLimit : RefLimit;

        var children = index.FindChildren(sym.SymbolId);
        if (children.Count > 0)
        {
            sb.Append("\n## children\n");
            int childLimit = depth == InspectDepth.Overview ? OverviewChildLimit : int.MaxValue;
            foreach (var c in children.Take(childLimit))
                sb.Append(SymbolLine(c)).Append('\n');
            AppendOmittedLine(sb, children.Count, childLimit, "children");
        }

        var refs = ExtractReader.ReadReferences(dbPath, sym.Name);
        if (refs.Count > 0)
        {
            sb.Append("\n## references\n");
            // Group the (path/line-ordered) refs by file so a file with N reference sites costs one line
            // (`path:l1,l2,…`) instead of N. Group AFTER Take(relationLimit) so the ref limit/omitted-count
            // semantics are unchanged — the omitted line still counts underlying refs, not files.
            AppendGroupedReferences(sb, refs.Take(relationLimit));
            AppendOmittedLine(sb, refs.Count, relationLimit, "refs");
        }

        var callers = DistinctCallers(index, refs);
        if (callers.Count > 0)
        {
            sb.Append("\n## callers\n");
            foreach (var c in callers.Take(relationLimit))
                sb.Append(c).Append('\n');
            AppendOmittedLine(sb, callers.Count, relationLimit, "callers");
        }

        var callees = ExtractReader.ReadCallees(dbPath, sym.SymbolId);
        if (callees.Count > 0)
        {
            sb.Append("\n## callees\n");
            // Dedup by callee name (first location wins, `×N` when a name recurs) BEFORE the relation limit,
            // so full depth spends its 50-row budget on distinct callees rather than repeated identifiers.
            // The omitted-count line counts DISTINCT callees so it can't overstate.
            var distinctCallees = DistinctCallees(callees);
            foreach (var c in distinctCallees.Take(relationLimit))
            {
                sb.Append(c.Name);
                if (c.Count > 1)
                    sb.Append(" ×").Append(c.Count);
                sb.Append("  ").Append(c.FilePath).Append(':').Append(c.StartLine).Append('\n');
            }
            AppendOmittedLine(sb, distinctCallees.Count, relationLimit, "callees");
        }

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

        // Delivery-time nudge (compact only; this path is overview/full — summary returned early). Rendered
        // last, at most once, and only in compact output (JSON shape stays byte-identical). Recovering refs
        // this render dropped outranks the impact nudge, because that data is otherwise lost from the answer.
        // Only the RefLimit cap qualifies: the overview cap already advertises depth=full as its recovery, and
        // firing there would displace the impact nudge on every overview read of a hot symbol. The explicit
        // limit is load-bearing — trace mode=refs defaults to 20, so a bare call would hand back FEWER refs
        // than the render that prompted it.
        bool refsTruncatedAtRefLimit = relationLimit == RefLimit && refs.Count > relationLimit;
        string callName = EscapeCallString(sym.Name);
        if (bodyPage is { Truncated: true, Continuation: not null })
        {
            sb.Append('\n').Append(NextStepHint.Render(
                $"inspect target=\"{sym.SymbolId}\" depth=full continuation=\"{bodyPage.Continuation}\"",
                $"continue body at byte {bodyPage.EndOffset}"));
        }
        else if (refsTruncatedAtRefLimit)
            sb.Append('\n').Append(NextStepHint.Render(
                $"trace target=\"{callName}\" mode=refs limit={refs.Count}", "full reference list"));
        else if (!sym.IsTest && refs.Count >= ImpactHintMinReferences)
            sb.Append('\n').Append(NextStepHint.Render(
                $"impact target=\"{callName}\"", $"{refs.Count} dependents"));

        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderSymbolJson(
        ISymbolLookupIndex index,
        string dbPath,
        string workspaceRoot,
        string workspaceId,
        IndexedSymbol sym,
        InspectDepth depth,
        string? continuation)
    {
        var detail = ExtractReader.ReadDetail(dbPath, sym.SymbolId);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();

            w.WritePropertyName("symbol");
            WriteSymbolObject(w, sym, detail);

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

                int relationLimit = depth == InspectDepth.Overview ? OverviewRelationLimit : RefLimit;

                w.WritePropertyName("children");
                WriteSymbolArray(w, index.FindChildren(sym.SymbolId).Take(
                    depth == InspectDepth.Overview ? OverviewChildLimit : int.MaxValue));

                var refs = ExtractReader.ReadReferences(dbPath, sym.Name);
                w.WritePropertyName("refs");
                w.WriteStartArray();
                foreach (var r in refs.Take(relationLimit))
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
                foreach (var c in DistinctCallers(index, refs).Take(relationLimit))
                    w.WriteStringValue(c);
                w.WriteEndArray();

                w.WritePropertyName("callees");
                w.WriteStartArray();
                foreach (var c in ExtractReader.ReadCallees(dbPath, sym.SymbolId).Take(relationLimit))
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

    private static void AppendOmittedLine(StringBuilder sb, int total, int visible, string label)
    {
        if (total > visible)
            sb.Append("... ").Append(total - visible).Append(" more ").Append(label).Append(" (use depth=full)\n");
    }

    /// <summary>
    /// Render a reference list as one <c>path:l1,l2,…</c> line per file, preserving first-seen file order and
    /// the incoming line order within each file. Refs arrive path/line-ordered from <c>ReadReferences</c>, so a
    /// file's sites are already contiguous; the insertion-ordered map keeps the output stable regardless.
    /// </summary>
    private static void AppendGroupedReferences(StringBuilder sb, IEnumerable<SymbolRef> refs)
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
            lines.Add(r.StartLine);
        }
        foreach (var file in order)
            sb.Append(file).Append(':').Append(string.Join(',', linesByFile[file])).Append('\n');
    }

    private readonly record struct DistinctCallee(string Name, string FilePath, int StartLine, int Count);

    /// <summary>
    /// Collapse a callee list to distinct callee names, preserving first occurrence (which also fixes the
    /// rendered location) and counting recurrences. No name is filtered — <c>nameof</c>/<c>ArgumentException</c>
    /// still carry information; dedup removes only the repetition cost. The caller applies the relation limit
    /// AFTER this collapse so full depth shows up to the limit in DISTINCT callees.
    /// </summary>
    private static List<DistinctCallee> DistinctCallees(IReadOnlyList<SymbolRef> callees)
    {
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<DistinctCallee>();
        foreach (var c in callees)
        {
            if (indexByName.TryGetValue(c.Name, out var i))
            {
                result[i] = result[i] with { Count = result[i].Count + 1 };
            }
            else
            {
                indexByName[c.Name] = result.Count;
                result.Add(new DistinctCallee(c.Name, c.FilePath, c.StartLine, 1));
            }
        }
        return result;
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
