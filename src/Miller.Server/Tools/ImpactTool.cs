using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Diff;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

/// <summary>
/// The <c>impact</c> tool (miller-toolbox.md §5, M5 D5): change-safety / blast radius. Given a symbol, a set of
/// changed files, or a unified diff, it returns the symbols and tests <b>downstream</b> of that change — the
/// REVERSE reachability (dependents) over the in-memory dependency graph (D2/D3), so it answers "what would
/// editing this break?" without julie's per-hop DB walk (the latency that left julie's blast_radius at 5s p95,
/// effectively dead). The reached set is partitioned into impacted symbols vs likely tests via julie's
/// cross-language <c>is_test</c> flag (verified-fact 5) — the "which tests to run" leg.
///
/// <para>Exactly ONE of <c>target</c> / <c>changed_paths</c> / <c>diff</c> is required (toolbox L146); zero or
/// more than one yields a clear usage note (treated as Empty, never an error). The seed legs: a symbol target
/// seeds itself; a file target (or a changed path) seeds every symbol in that file; a diff seeds the symbols
/// whose <c>[start_line, end_line]</c> intersect a changed line range, degrading to the whole file when nothing
/// intersects (a safe over-approximation, noted — no silent narrowing).</para>
///
/// <para>This is the thin MCP/DI/telemetry shell; the pure, DB-free <see cref="Run"/> core (mirroring
/// <see cref="InspectTool.Run"/>) is where the correctness lives and where the unit tests bite. It reads the live
/// <see cref="IndexHolder"/> per call (M3 step 10) so a freshness Swap is reflected on the next impact.</para>
/// </summary>
[McpServerToolType]
public sealed class ImpactTool
{
    private readonly IWorkspaceIndexProvider _workspaceProvider;

    /// <summary>Construct over the live index holder (production / freshness-aware). Unlike inspect, impact's
    /// <see cref="Run"/> core is DB-free (it traverses the in-memory graph), so it takes no WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ImpactTool(IWorkspaceIndexProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        _workspaceProvider = workspaceProvider;
    }

    [McpServerTool(Name = "impact")]
    [Description(
        "Show what a change would affect — the symbols and tests downstream of editing a symbol or file. Use " +
        "before a refactor, or to find which tests to run for a change. Prefer this over grepping for usages. " +
        "Pass exactly one of target (a symbol or file), changed_paths (a set of files), or diff (a unified " +
        "diff). Returns compact text by default; pass format=json to chain results.")]
    public string Impact(
        [Description("A symbol name/id or a file path (smart-resolved). One of target/changed_paths/diff.")]
        string? target = null,
        [Description("A set of changed file paths. One of target/changed_paths/diff.")]
        string[]? changed_paths = null,
        [Description("A unified diff; changed line ranges map to the symbols they touch. One of target/changed_paths/diff.")]
        string? diff = null,
        [Description("Reverse-reachability radius (how many hops of dependents to follow). Default 2.")]
        int max_depth = 2,
        [Description("Max impacted symbols to return. Default 100.")] int limit = 100,
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
            WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            string output = Run(context.Index, context.Resolver,
                target, changed_paths, diff, max_depth, limit, json,
                out int impactedCount, out int nodesVisited);
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                // The target axis is whichever input was supplied (target wins, else the first changed path,
                // else a diff marker) — privacy-hashed by SetTarget.
                telemetry.SetTarget(TargetForTelemetry(target, changed_paths, diff));
                telemetry.ResultCount = impactedCount;
                // D10 work proxy (bytes_examined ≈ nodes visited): the reverse-reachability set the BFS produced.
                telemetry.BytesExamined = nodesVisited;
                telemetry.Outcome = impactedCount == 0 ? TelemetryOutcome.Empty : TelemetryOutcome.Ok;
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
            return $"impact failed: {ex.Message}";
        }
    }

    private static string? TargetForTelemetry(string? target, string[]? changedPaths, string? diff)
    {
        if (!string.IsNullOrWhiteSpace(target))
            return target;
        if (changedPaths is { Length: > 0 })
            return string.Join(',', changedPaths);
        return string.IsNullOrEmpty(diff) ? null : "diff";
    }

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry; no DB — the graph is in-memory). Resolves the seed symbols
    /// per D5, runs a bounded REVERSE reachability to <paramref name="maxDepth"/> capped at <paramref name="limit"/>,
    /// partitions the reached nodes into impacted symbols vs likely tests, and renders compact or json with
    /// provenance (<c>name kind file:line</c>, hop distance). <paramref name="impactedCount"/> is the number of
    /// non-test impacted symbols (the result-count KPI); a usage error / not-found / empty closure yields 0.
    /// <paramref name="nodesVisited"/> is the size of the reverse-reachability set the BFS produced (impacted +
    /// likely tests, before the partition) — the D10 <c>bytes_examined ≈ nodes visited</c> work proxy; the guard /
    /// not-found / empty-closure paths leave it 0.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="resolver"/> is null.</exception>
    public static string Run(
        MillerRepositoryIndex index, SmartTargetResolver resolver,
        string? target, IReadOnlyList<string>? changedPaths, string? diff,
        int maxDepth, int limit, bool json,
        out int impactedCount, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        if (maxDepth < 1) maxDepth = 1;
        if (limit < 1) limit = 1;
        nodesVisited = 0;

        // --- exactly-one-input guard (D1): zero or more than one → a clear usage note, never an exception. ---
        int provided =
            (string.IsNullOrWhiteSpace(target) ? 0 : 1) +
            (changedPaths is { Count: > 0 } ? 1 : 0) +
            (string.IsNullOrEmpty(diff) ? 0 : 1);
        if (provided != 1)
        {
            impactedCount = 0;
            return Usage(json);
        }

        // --- resolve the seed symbol ids (D5), collecting any user-facing note (not-found / whole-file). ---
        var seedIds = new List<string>();
        string? note = null;

        if (!string.IsNullOrWhiteSpace(target))
        {
            if (!SeedFromTarget(index, resolver, target, seedIds, out string? targetNote))
            {
                // A hard target failure (not-found / ambiguous candidates) renders its own message and stops.
                impactedCount = 0;
                return targetNote ?? Note(json, "impact: unresolved target.");
            }
            note = targetNote;
        }
        else if (changedPaths is { Count: > 0 })
        {
            foreach (var path in changedPaths)
                SeedFromFile(index, path, seedIds);
        }
        else // diff
        {
            note = SeedFromDiff(index, diff!, seedIds);
        }

        // --- bounded REVERSE reachability over the in-memory graph (D3/D5). Starts are excluded by Reach. ---
        IReadOnlyList<ReachedNode> reached =
            index.Graph.Reach(seedIds, maxDepth, limit, Direction.Reverse);
        nodesVisited = reached.Count; // D10 work proxy: the whole reached set (before the test partition)

        // --- partition the reached nodes into impacted symbols vs likely tests (D5). Hydrate ids → symbols;
        // an id absent from the index is skipped (defensive — the graph bounds edges to indexed nodes). ---
        var impacted = new List<Reached>();
        var tests = new List<Reached>();
        foreach (var node in reached)
        {
            var symbol = index.FindBySymbolId(node.Id);
            if (symbol is null)
                continue; // inconsistent build — drop rather than NRE
            (symbol.IsTest ? tests : impacted).Add(new Reached(symbol, node.Hop));
        }

        impactedCount = impacted.Count;
        return json
            ? RenderJson(impacted, tests, note)
            : RenderCompact(impacted, tests, note);
    }

    /// <summary>A reached symbol carrying its blast-radius hop distance (for provenance ordering + display).</summary>
    private readonly record struct Reached(IndexedSymbol Symbol, int Hop);

    // ---------- seed resolution ----------

    // Resolve a target into seed ids. Returns false (with a rendered message) on a hard failure (not-found /
    // ambiguous); true on success (seedIds populated, possibly empty if a file has no symbols). A file target
    // never fails hard — an unknown file simply seeds nothing and falls through to the "nothing depends" note.
    private static bool SeedFromTarget(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string target,
        List<string> seedIds, out string? note)
    {
        note = null;
        var resolution = resolver.Resolve(target);
        switch (resolution)
        {
            case TargetResolution.Symbol sym:
                seedIds.Add(sym.Value.SymbolId);
                return true;

            case TargetResolution.File file:
                SeedFromFile(index, file.Path, seedIds);
                return true;

            case TargetResolution.Candidates cands:
                // Ambiguous name — never pick-first; ask the caller to disambiguate (mirrors inspect).
                note = RenderCandidatesNote(cands.Matches);
                return false;

            case TargetResolution.NotFound nf:
                note = $"'{nf.Target}' not found. Try search to locate it.";
                return false;

            default:
                note = "impact: unrecognized target resolution.";
                return false;
        }
    }

    // Seed every indexed symbol of a file (D5: a file/changed-path seeds all its symbols).
    private static void SeedFromFile(MillerRepositoryIndex index, string path, List<string> seedIds)
    {
        // Canonicalize a bare basename to its indexed path when unambiguous (e.g. Service.cs → src/Service.cs).
        string resolved = index.ResolveIndexedFilePath(path) ?? path;
        foreach (var symbol in index.FindByFilePath(resolved))
            seedIds.Add(symbol.SymbolId);
    }

    // Seed from a unified diff (D5): per changed file, the symbols whose [start_line, end_line] intersect a
    // changed new-side range; when nothing intersects (or no spans recorded), degrade to ALL symbols in the file
    // (a safe over-approximation, noted). Returns a degradation note when any file degraded, else null.
    private static string? SeedFromDiff(MillerRepositoryIndex index, string diff, List<string> seedIds)
    {
        var degradedFiles = new List<string>();
        foreach (var file in DiffTargets.Parse(diff))
        {
            string resolved = index.ResolveIndexedFilePath(file.Path) ?? file.Path;
            var symbols = index.FindByFilePath(resolved);
            if (symbols.Count == 0)
                continue; // a changed file with no indexed symbols contributes no seeds

            // Collect the symbols whose whole span intersects ANY changed range. A symbol with no recorded span
            // (StartLine 0 / EndLine 0) can never intersect, so it falls into the whole-file degradation below.
            var intersecting = new List<string>();
            foreach (var symbol in symbols)
            {
                if (symbol.StartLine <= 0 || symbol.EndLine <= 0)
                    continue;
                foreach (var range in file.Changed)
                {
                    if (Intersects(symbol.StartLine, symbol.EndLine, range.StartLine, range.EndLine))
                    {
                        intersecting.Add(symbol.SymbolId);
                        break;
                    }
                }
            }

            if (intersecting.Count > 0)
            {
                seedIds.AddRange(intersecting);
            }
            else
            {
                // No line-precise intersection → seed the whole file (over-approximate, never silently narrow).
                foreach (var symbol in symbols)
                    seedIds.Add(symbol.SymbolId);
                degradedFiles.Add(resolved);
            }
        }

        return degradedFiles.Count == 0
            ? null
            : "note: no line-precise span matched in " + string.Join(", ", degradedFiles) +
              " — seeded the whole file(s).";
    }

    // Two inclusive line ranges [aStart,aEnd] and [bStart,bEnd] overlap when each starts at or before the other ends.
    private static bool Intersects(int aStart, int aEnd, int bStart, int bEnd) =>
        aStart <= bEnd && bStart <= aEnd;

    // ---------- rendering ----------

    private static string RenderCompact(
        IReadOnlyList<Reached> impacted, IReadOnlyList<Reached> tests, string? note)
    {
        var sb = new StringBuilder();
        if (note is not null)
            sb.Append(note).Append('\n');

        if (impacted.Count == 0 && tests.Count == 0)
        {
            sb.Append("No impact — nothing depends on the change.");
            return sb.ToString().TrimEnd('\n');
        }

        sb.Append("# impacted (").Append(impacted.Count).Append(")\n");
        if (impacted.Count == 0)
            sb.Append("(none)\n");
        foreach (var r in impacted)
            sb.Append(ProvenanceLine(r)).Append('\n');

        if (tests.Count > 0)
        {
            sb.Append("\n# likely tests (").Append(tests.Count).Append(")\n");
            foreach (var r in tests)
                sb.Append(ProvenanceLine(r)).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    // "Name  kind  file:line  (hop N)" — the impact provenance line.
    private static string ProvenanceLine(Reached r)
    {
        var s = r.Symbol;
        return $"{s.Name}  {s.Kind}  {s.FilePath}:{s.StartLine}  (hop {r.Hop})";
    }

    private static string RenderJson(
        IReadOnlyList<Reached> impacted, IReadOnlyList<Reached> tests, string? note)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            if (note is null) w.WriteNull("note"); else w.WriteString("note", note);
            w.WritePropertyName("impacted");
            WriteReachedArray(w, impacted);
            w.WritePropertyName("tests");
            WriteReachedArray(w, tests);
            w.WriteEndObject();
        }
        return Utf8(buffer);
    }

    private static void WriteReachedArray(Utf8JsonWriter w, IReadOnlyList<Reached> items)
    {
        w.WriteStartArray();
        foreach (var r in items)
        {
            w.WriteStartObject();
            w.WriteString("name", r.Symbol.Name);
            w.WriteString("kind", r.Symbol.Kind);
            w.WriteString("file", r.Symbol.FilePath);
            w.WriteNumber("line", r.Symbol.StartLine);
            w.WriteNumber("hop", r.Hop);
            w.WriteString("symbol_id", r.Symbol.SymbolId);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static string RenderCandidatesNote(IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append("Multiple candidates — pass a more specific target (or a file path):\n");
        foreach (var s in matches)
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    // The exactly-one-input guard's message. This is guidance, NOT a failure: the wrapper records it as the Empty
    // outcome (impactedCount 0), so the JSON shape uses the same "note" key the not-found path uses — an "error"
    // key is reserved for the Error outcome (matching EditService's convention). The compact text is unchanged.
    private static string Usage(bool json) => json
        ? Note(json, "impact requires exactly one of target, changed_paths, or diff.")
        : "Usage: pass exactly one of target (a symbol or file), changed_paths (a set of files), or diff " +
          "(a unified diff).";

    private static string Note(bool json, string message) => json
        ? $"{{\"note\":{JsonSerializer.Serialize(message)}}}"
        : message;

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}
