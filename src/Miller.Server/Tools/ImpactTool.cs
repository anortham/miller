using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Miller.Core.Diff;
using Miller.Core.Graph;
using Miller.Indexing;
using Miller.Server.Git;
using Miller.Server.Hosting;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Workspaces;
using ModelContextProtocol.Server;

namespace Miller.Server.Tools;

public sealed record ImpactRevisionDeltaSnapshot(
    string WorkspaceId,
    bool Complete,
    long FromRevision,
    long ToRevision,
    string? ArtifactId,
    string? FromArtifactId,
    string Reason,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> DeletedPaths);

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
    private const int CompactLikelyTestsLimit = 20;
    private const int CompactImpactedLimit = 40;
    private const int CompactOutputMaxChars = 6000;

    private readonly IWorkspaceIndexProvider _workspaceProvider;
    private readonly IGitDiffReader _gitDiffReader;

    /// <summary>Construct over the live index holder (production / freshness-aware). Unlike inspect, impact's
    /// <see cref="Run"/> core is DB-free (it traverses the in-memory graph), so it takes no WorkspaceContext.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ImpactTool(IWorkspaceIndexProvider workspaceProvider)
        : this(workspaceProvider, new ProcessGitDiffReader())
    {
    }

    public ImpactTool(IWorkspaceIndexProvider workspaceProvider, IGitDiffReader gitDiffReader)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);
        ArgumentNullException.ThrowIfNull(gitDiffReader);
        _workspaceProvider = workspaceProvider;
        _gitDiffReader = gitDiffReader;
    }

    [McpServerTool(Name = "impact")]
    [Description(
        "Blast-radius analysis: what a change affects and which tests to run. With NO args it reads the " +
        "working-tree git diff and maps changed ranges to impacted symbols + likely tests — run it after edits, " +
        "before committing. Or pass exactly one of: target, changed_paths, diff, git/base/staged, or " +
        "from_index_revision with from_artifact_id. Use BEFORE a refactor and AFTER edits; prefer it over grepping " +
        "for usages when the question is \"what breaks and what do I test\". NOT for: plain reference lists " +
        "(trace mode=refs). Example: impact target=SymbolSearchSidecar. Compact by default; format=json to chain.")]
    public string Impact(
        [Description("A symbol name/id or a file path (smart-resolved). One of target/changed_paths/diff/git.")]
        string? target = null,
        [Description("A set of changed file paths. One of target/changed_paths/diff/git.")]
        string[]? changed_paths = null,
        [Description("A unified diff; changed line ranges map to the symbols they touch. One of target/changed_paths/diff/git.")]
        string? diff = null,
        [Description("Read git diff from the selected workspace and map changed ranges to impacted symbols.")]
        bool git = false,
        [Description("Base ref for git diff, for example origin/main or HEAD~1. Implies git=true.")]
        string? @base = null,
        [Description("Use the staged/index diff (`git diff --cached`). Implies git=true.")]
        bool staged = false,
        [Description("Base extractor revision for a watched-file delta. Exclusive with target/changed_paths/diff/git.")]
        long? from_index_revision = null,
        [Description("Artifact generation id paired with from_index_revision; mismatches return an unavailable delta.")]
        string? from_artifact_id = null,
        [Description("Reverse-reachability radius (how many hops of dependents to follow). Default 2.")]
        int max_depth = 2,
        [Description("Max impacted symbols to return. Default 100.")] int limit = 100,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        try
        {
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            bool explicitGit = git || staged || !string.IsNullOrWhiteSpace(@base);
            int provided =
                (string.IsNullOrWhiteSpace(target) ? 0 : 1) +
                (changed_paths is { Length: > 0 } ? 1 : 0) +
                (string.IsNullOrWhiteSpace(diff) ? 0 : 1) +
                (explicitGit ? 1 : 0) +
                (from_index_revision.HasValue ? 1 : 0);

            if (provided > 1 || from_index_revision < 0 ||
                (!from_index_revision.HasValue && !string.IsNullOrWhiteSpace(from_artifact_id)))
            {
                string usage = Usage(json);
                return ToolDiagnosticRenderer.Attach(
                    "impact",
                    ReadToolWorkspaceRouting.PrefixCompact(usage, compactBanner),
                    ToolDiagnostic.Refusal(
                        "invalid_input_selection",
                        "Impact accepts exactly one input source."),
                    json,
                    telemetry);
            }

            bool noArgDefault = provided == 0;
            bool useGitDiff = explicitGit || noArgDefault;

            bool emptyGitDiff = false;
            if (useGitDiff)
            {
                GitDiffResult gitResult = _gitDiffReader.Read(new GitDiffRequest(context.WorkspaceRoot, @base, staged));
                if (!gitResult.Success)
                {
                    if (noArgDefault)
                    {
                        // No-arg in a non-git (or broken-git) workspace: fall back to the usage note rather than
                        // erroring -- `impact` with no args must never fail just because there is no git diff to read.
                        string usage = Usage(json);
                        return ToolDiagnosticRenderer.Attach(
                            "impact",
                            ReadToolWorkspaceRouting.PrefixCompact(usage, compactBanner),
                            ToolDiagnostic.Refusal(
                                "git_diff_unavailable",
                                "No working-tree git diff is available; provide target, changed_paths, or diff."),
                            json,
                            telemetry);
                    }
                    throw new ToolDiagnosticException(ToolDiagnostic.Unavailable(
                        "git_diff_failed",
                        $"git diff failed in {context.WorkspaceRoot}: {gitResult.Error ?? "unknown error"}"));
                }

                if (string.IsNullOrWhiteSpace(gitResult.Diff))
                {
                    emptyGitDiff = true;
                }
                else
                {
                    diff = gitResult.Diff;
                }
            }

            string output;
            int impactedCount;
            int nodesVisited;
            bool revisionDelta = from_index_revision.HasValue;
            if (revisionDelta)
            {
                ImpactRevisionDeltaSnapshot snapshot = PrepareIndexRevisionDelta(
                    workspace_id ?? context.WorkspaceId ?? context.WorkspaceRoot,
                    context.WorkspaceRoot,
                    context.IndexDbPath,
                    from_index_revision!.Value,
                    from_artifact_id);
                output = RunIndexRevisionDelta(
                    snapshot,
                    context.Index,
                    context.Index.Graph,
                    max_depth,
                    limit,
                    json,
                    indexAvailable: true);
                impactedCount = 0;
                nodesVisited = 0;
            }
            else if (emptyGitDiff)
            {
                output = Note(json, "No impact — git diff is empty.");
                impactedCount = 0;
                nodesVisited = 0;
            }
            else
            {
                output = Run(context.Index, context.Resolver,
                    target, changed_paths, diff, max_depth, limit, json,
                    out impactedCount, out nodesVisited);
            }
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);
            ToolDiagnostic? diagnostic =
                !revisionDelta && impactedCount == 0 ? ImpactEmptyDiagnostic(output, target) : null;

            if (telemetry is not null)
            {
                ReadToolWorkspaceRouting.ApplyTelemetry(telemetry, context);
                telemetry.Op = revisionDelta
                    ? "index_revision_delta"
                    : ImpactInputKind(target, changed_paths, diff, useGitDiff);
                // The target axis is whichever input was supplied (target wins, else the first changed path,
                // else a diff marker) — privacy-hashed by SetTarget.
                telemetry.SetTarget(revisionDelta
                    ? from_index_revision.GetValueOrDefault().ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    : TargetForTelemetry(target, changed_paths, diff, useGitDiff, @base, staged));
                telemetry.ResultCount = impactedCount;
                // D10 work proxy (bytes_examined ≈ nodes visited): the reverse-reachability set the BFS produced.
                telemetry.BytesExamined = nodesVisited;
                telemetry.Outcome = diagnostic is null ? TelemetryOutcome.Ok : TelemetryOutcome.Empty;
                telemetry.SetMetadata("format", json ? "json" : "compact");
                telemetry.SetMetadata("limit_bucket", LimitBucket(limit));
                telemetry.SetMetadata("max_depth_bucket", DepthBucket(max_depth));
            }
            if (diagnostic is not null)
            {
                output = ToolDiagnosticRenderer.Attach(
                    "impact",
                    output,
                    diagnostic,
                    json,
                    telemetry);
            }
            return output;
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            return ToolDiagnosticRenderer.Render(
                "impact",
                diagnostic,
                json,
                telemetry);
        }
    }

    private static string? TargetForTelemetry(
        string? target,
        string[]? changedPaths,
        string? diff,
        bool gitDiff,
        string? baseRef,
        bool staged)
    {
        if (!string.IsNullOrWhiteSpace(target))
            return target;
        if (changedPaths is { Length: > 0 })
            return string.Join(',', changedPaths);
        if (gitDiff)
            return !string.IsNullOrWhiteSpace(baseRef) ? baseRef : staged ? "staged" : "working_tree";
        return string.IsNullOrEmpty(diff) ? null : "diff";
    }

    private static string ImpactInputKind(string? target, string[]? changedPaths, string? diff, bool gitDiff)
    {
        if (!string.IsNullOrWhiteSpace(target))
            return "target";
        if (changedPaths is { Length: > 0 })
            return "changed_paths";
        if (gitDiff)
            return "git_diff";
        return string.IsNullOrWhiteSpace(diff) ? "missing_input" : "diff";
    }

    private static ToolDiagnostic ImpactEmptyDiagnostic(string output, string? target)
    {
        string code;
        if (output.Contains("git diff is empty", StringComparison.Ordinal))
            code = "empty_git_diff";
        else if (output.Contains("No indexed symbols matched", StringComparison.Ordinal))
            code = "no_seed_symbols";
        else if (output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("Multiple candidates", StringComparison.Ordinal))
            code = "unresolved_target";
        else if (output.Contains("Resolved seed symbols:", StringComparison.Ordinal))
            code = "no_dependents";
        else
            code = "no_impacted_symbols";

        IReadOnlyList<ToolDiagnosticAction> actions = string.IsNullOrWhiteSpace(target)
            ? Array.Empty<ToolDiagnosticAction>()
            : [new ToolDiagnosticAction(
                $"search(query=\"{EscapeDiagnosticTarget(target)}\")",
                "resolve an exact impact seed")];
        return output.Contains("Multiple candidates", StringComparison.Ordinal)
            ? ToolDiagnostic.Ambiguity(code, "Impact target is ambiguous.", actions)
            : ToolDiagnostic.ExpectedEmpty(code, code switch
            {
                "empty_git_diff" => "The selected git diff contains no changes.",
                "no_seed_symbols" => "No indexed symbols intersected the supplied changes.",
                "unresolved_target" => "Impact could not resolve an exact target.",
                "no_dependents" => "The resolved symbols have no indexed downstream dependents.",
                _ => "Impact produced no affected symbols.",
            }, actions);
    }

    private static string EscapeDiagnosticTarget(string target)
    {
        return ToolDiagnosticText.EscapeCallArgument(target);
    }

    private static string LimitBucket(int limit) => limit switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 10 => "6-10",
        <= 25 => "11-25",
        <= 50 => "26-50",
        <= 100 => "51-100",
        _ => "101+",
    };

    private static string DepthBucket(int depth) => depth switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        <= 5 => "3-5",
        _ => "6+",
    };

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry; no DB — the graph is in-memory). Resolves the seed symbols
    /// per D5, runs a bounded REVERSE reachability to <paramref name="maxDepth"/> capped at <paramref name="limit"/>,
    /// partitions the reached nodes into impacted symbols vs likely tests, and renders compact or json with
    /// provenance (file group, <c>:line name kind hop=N</c>). <paramref name="impactedCount"/> is the number of
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
        return Run(index, index.Graph, resolver, target, changedPaths, diff, maxDepth, limit, json,
            out impactedCount, out nodesVisited);
    }

    public static string Run(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string? target, IReadOnlyList<string>? changedPaths, string? diff,
        int maxDepth, int limit, bool json,
        out int impactedCount, out int nodesVisited)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
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
                impactedCount = 0;
                string message = targetNote ?? "impact: unresolved target.";
                return json ? Note(json: true, message) : message;
            }
            note = targetNote;
        }
        else if (changedPaths is { Count: > 0 })
        {
            var unmatched = new List<string>();
            foreach (var path in changedPaths)
            {
                if (SeedFromFile(index, path, seedIds) == 0)
                    unmatched.Add(path);
            }
            if (unmatched.Count > 0)
                note = NoSeedSymbolsNote("changed path(s)", unmatched);
        }
        else // diff
        {
            note = SeedFromDiff(index, diff!, seedIds);
        }

        if (seedIds.Count == 0 && note is null)
            note = "No indexed symbols matched the impact input. Try search mode=file for the changed path.";

        // --- bounded REVERSE reachability over the in-memory graph (D3/D5). Starts are excluded by Reach. ---
        GraphReachResult graphResult =
            graph.ReachWithEvidence(seedIds, maxDepth, limit, Direction.Reverse);
        nodesVisited = graphResult.ReachedCount;
        IReadOnlyList<ReachedNode> reachedNodes = AddHeuristicTestCandidates(
            index, seedIds, graphResult.Nodes, limit);
        if (seedIds.Count > 0 && reachedNodes.Count == 0 && note is null)
            note = NoDependentsNote(index, seedIds);

        // --- partition the reached nodes into impacted symbols vs likely tests (D5). Hydrate ids → symbols;
        // an id absent from the index is skipped (defensive — the graph bounds edges to indexed nodes). ---
        var impacted = new List<Reached>();
        var tests = new List<Reached>();
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, reachedNodes.Select(static node => node.Id));
        var ranked = ImpactRanker.Rank(reachedNodes
            .Where(node => symbolsById.ContainsKey(node.Id))
            .Select(node =>
            {
                IndexedSymbol symbol = symbolsById[node.Id];
                return new ImpactRankSignal(node, symbol.FilePath, symbol.StartLine, symbol.Name, symbol.SymbolId);
            }));
        foreach (ImpactRankSignal candidate in ranked)
        {
            IndexedSymbol symbol = symbolsById[candidate.SymbolId];
            (symbol.IsTest ? tests : impacted).Add(new Reached(symbol, candidate.Evidence));
        }

        impactedCount = impacted.Count;
        (string status, string reason) = TraversalDisposition(graphResult);
        var traversal = new ImpactTraversal(
            impacted,
            tests,
            graphResult,
            [],
            [],
            [],
            status,
            reason);
        return json
            ? RenderJson(traversal, note, maxDepth, limit)
            : RenderCompact(traversal, note, maxDepth, limit);
    }

    // ---------- index-revision delta (CT revision-delta contract R0–R2) ----------

    /// <summary>
    /// R2 truthful exclusion: drop any journal path that Miller's existing watch/ignore policy would never watch
    /// (tooling/build dirs: <c>.git</c>, <c>.miller</c>, <c>.julie</c>, <c>target</c>, <c>node_modules</c>,
    /// <c>bin</c>, <c>obj</c>, … and workspace <c>.gitignore</c>/<c>.julieignore</c> matches). The journal already
    /// omits these because Miller never feeds them to the extractor; re-applying <see cref="WatchPathFilter"/> here
    /// makes the exclusion a property of the delta itself — a stale journal row for a now-ignored path can never
    /// leak into <c>changed_paths</c>. Paths are workspace-relative; order is preserved.
    /// </summary>
    public static IReadOnlyList<string> FilterWatchedDeltaPaths(string workspaceRoot, IReadOnlyList<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return paths;

        var kept = new List<string>(paths.Count);
        foreach (string relative in paths)
        {
            if (string.IsNullOrWhiteSpace(relative))
                continue;
            string absolute = Path.Combine(workspaceRoot, relative);
            if (WatchPathFilter.ShouldProcess(workspaceRoot, absolute))
                kept.Add(relative);
        }

        return kept;
    }

    public static ImpactRevisionDeltaSnapshot PrepareIndexRevisionDelta(
        string workspaceId,
        string workspaceRoot,
        string extractDbPath,
        long fromRevision,
        string? fromArtifactId)
    {
        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            extractDbPath, fromRevision, fromArtifactId);
        bool complete = delta.Status == RevisionDeltaStatus.Complete;
        IReadOnlyList<string> changedPaths = complete
            ? FilterWatchedDeltaPaths(workspaceRoot, delta.ChangedPaths)
            : [];
        var keptPaths = changedPaths.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> deletedPaths = complete
            ? (delta.DeletedPaths ?? []).Where(keptPaths.Contains).ToArray()
            : [];
        return new ImpactRevisionDeltaSnapshot(
            workspaceId,
            complete,
            fromRevision,
            delta.ToRevision,
            delta.ArtifactId,
            fromArtifactId,
            delta.Reason,
            changedPaths,
            deletedPaths);
    }

    public static string RunIndexRevisionDelta(
        ImpactRevisionDeltaSnapshot snapshot,
        ISymbolLookupIndex? index,
        ISymbolGraphReachability? graph,
        int maxDepth,
        int limit,
        bool json,
        bool indexAvailable)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RenderIndexRevisionDelta(
            snapshot.WorkspaceId,
            snapshot.Complete,
            snapshot.FromRevision,
            snapshot.ToRevision,
            snapshot.ChangedPaths,
            index,
            graph,
            maxDepth,
            limit,
            json,
            snapshot.ArtifactId,
            snapshot.FromArtifactId,
            snapshot.Reason,
            indexAvailable,
            snapshot.DeletedPaths);
    }

    /// <summary>
    /// Render the typed index-revision delta envelope (R0): always <c>workspace_id</c>, <c>delta_status</c>
    /// (<c>complete</c>|<c>unavailable</c>), <c>from_revision</c>, <c>to_revision</c>, <c>changed_paths</c>, plus
    /// the existing impact shape (<c>impacted</c>/<c>tests</c>) computed over the changed paths. When
    /// <paramref name="complete"/> is false the delta is unavailable: <c>changed_paths</c>/<c>impacted</c>/
    /// <c>tests</c> are empty and only the revisions are reported. <paramref name="index"/> and
    /// <paramref name="graph"/> may be null (no index loaded / nothing changed) — then <c>impacted</c>/<c>tests</c>
    /// are empty but <c>changed_paths</c> is still reported truthfully.
    /// </summary>
    public static string RenderIndexRevisionDelta(
        string workspaceId,
        bool complete,
        long fromRevision,
        long toRevision,
        IReadOnlyList<string> changedPaths,
        ISymbolLookupIndex? index,
        ISymbolGraphReachability? graph,
        int maxDepth,
        int limit,
        bool json,
        string? artifactId = null,
        string? fromArtifactId = null,
        string deltaReason = "complete",
        bool indexAvailable = true,
        IReadOnlyList<string>? deletedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (maxDepth < 1) maxDepth = 1;
        if (limit < 1) limit = 1;
        IReadOnlyList<string> paths = complete ? changedPaths : Array.Empty<string>();
        IReadOnlyList<string> deleted = complete
            ? deletedPaths ?? Array.Empty<string>()
            : Array.Empty<string>();

        ImpactTraversal traversal;
        if (!complete)
            traversal = NotRun("delta_unavailable", []);
        else if (paths.Count == 0)
            traversal = NotRun("no_changes", []);
        else if (!indexAvailable || index is null || graph is null)
            traversal = NotRun("index_unavailable", deleted);
        else
            traversal = ReachFromChangedPaths(index, graph, paths, deleted, maxDepth, limit);

        return json
            ? RenderDeltaJson(workspaceId, complete, fromRevision, toRevision, artifactId, fromArtifactId,
                deltaReason, paths, maxDepth, limit, traversal)
            : RenderDeltaCompact(workspaceId, complete, fromRevision, toRevision, artifactId, fromArtifactId,
                deltaReason, paths, maxDepth, limit, traversal);

        static ImpactTraversal NotRun(string reason, IReadOnlyList<string> deleted) =>
            new([], [], null, [], [], deleted, "not_run", reason);
    }

    // Seed every changed path's symbols, then partition the bounded reverse-reachability set into impacted symbols
    // vs likely tests — the SAME core Run uses for a changed-paths impact query (D3/D5), just without the notes.
    private static ImpactTraversal ReachFromChangedPaths(
        ISymbolLookupIndex index, ISymbolGraphReachability graph,
        IReadOnlyList<string> changedPaths,
        IReadOnlyList<string> deletedPaths,
        int maxDepth,
        int limit)
    {
        var seedIds = new List<string>();
        var seededPaths = new List<string>();
        var unseededPaths = new List<string>();
        var deleted = deletedPaths.ToHashSet(StringComparer.Ordinal);
        foreach (string path in changedPaths)
        {
            if (deleted.Contains(path))
                continue;
            if (SeedFromFile(index, path, seedIds) > 0)
                seededPaths.Add(path);
            else
                unseededPaths.Add(path);
        }

        var impacted = new List<Reached>();
        var tests = new List<Reached>();
        if (seedIds.Count == 0)
            return new(
                impacted, tests, null, seededPaths, unseededPaths, deletedPaths,
                "not_run", "no_seeds");

        GraphReachResult graphResult = graph.ReachWithEvidence(seedIds, maxDepth, limit, Direction.Reverse);
        IReadOnlyList<ReachedNode> reachedNodes = AddHeuristicTestCandidates(
            index, seedIds, graphResult.Nodes, limit);
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, reachedNodes.Select(static node => node.Id));
        var ranked = ImpactRanker.Rank(reachedNodes
            .Where(node => symbolsById.ContainsKey(node.Id))
            .Select(node =>
            {
                IndexedSymbol symbol = symbolsById[node.Id];
                return new ImpactRankSignal(node, symbol.FilePath, symbol.StartLine, symbol.Name, symbol.SymbolId);
            }));
        foreach (ImpactRankSignal candidate in ranked)
        {
            IndexedSymbol symbol = symbolsById[candidate.SymbolId];
            (symbol.IsTest ? tests : impacted).Add(new Reached(symbol, candidate.Evidence));
        }

        (string status, string reason) = TraversalDisposition(graphResult);
        return new(
            impacted, tests, graphResult, seededPaths, unseededPaths, deletedPaths,
            status, reason);
    }

    private static (string Status, string Reason) TraversalDisposition(GraphReachResult graphResult)
    {
        string status = graphResult.Exhausted ? "exhausted" : "truncated";
        string reason = (graphResult.TruncatedByDepth, graphResult.TruncatedByLimit) switch
        {
            (true, true) => "depth_and_limit",
            (true, false) => "depth",
            (false, true) => "limit",
            _ => "complete",
        };
        return (status, reason);
    }

    private static IReadOnlyList<ReachedNode> AddHeuristicTestCandidates(
        ISymbolLookupIndex index,
        IReadOnlyList<string> seedIds,
        IReadOnlyList<ReachedNode> graphNodes,
        int limit)
    {
        if (graphNodes.Count >= limit)
            return graphNodes;

        var combined = graphNodes.ToList();
        var seen = new HashSet<string>(
            seedIds.Concat(graphNodes.Select(static node => node.Id)),
            StringComparer.Ordinal);
        IReadOnlyDictionary<string, IndexedSymbol> seeds =
            SymbolLookupBatch.FindBySymbolIds(index, seedIds);
        foreach (IndexedSymbol seed in seeds.Values
                     .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                     .ThenBy(static symbol => symbol.StartLine)
                     .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(seed.FilePath);
            if (string.IsNullOrWhiteSpace(stem))
                continue;

            foreach (IndexedSymbol candidate in index.FindByFilePathFragment(stem, 64)
                         .Where(static symbol => symbol.IsTest)
                         .Where(symbol => IsFilenameRoleCandidate(stem, symbol.FilePath))
                         .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                         .ThenBy(static symbol => symbol.StartLine)
                         .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
            {
                if (!seen.Add(candidate.SymbolId))
                    continue;

                combined.Add(new ReachedNode(
                    candidate.SymbolId,
                    1,
                    seed.SymbolId,
                    "test_candidate",
                    0.35,
                    "filename_role",
                    Visibility: candidate.Visibility));
                if (combined.Count >= limit)
                    return combined;
            }
        }
        return combined;
    }

    private static bool IsFilenameRoleCandidate(string sourceStem, string candidatePath)
    {
        string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
        return candidateStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase) &&
               candidateStem.AsSpan(sourceStem.Length).StartsWith("test", StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderDeltaJson(
        string workspaceId, bool complete, long fromRevision, long toRevision,
        string? artifactId, string? fromArtifactId, string deltaReason,
        IReadOnlyList<string> changedPaths, int maxDepth, int limit, ImpactTraversal traversal)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("workspace_id", workspaceId ?? string.Empty);
            w.WriteString("delta_status", complete ? "complete" : "unavailable");
            if (artifactId is null) w.WriteNull("artifact_id");
            else w.WriteString("artifact_id", artifactId);
            if (fromArtifactId is null) w.WriteNull("from_artifact_id");
            else w.WriteString("from_artifact_id", fromArtifactId);
            w.WriteString("delta_reason", deltaReason);
            w.WriteNumber("from_revision", fromRevision);
            w.WriteNumber("to_revision", toRevision);
            w.WritePropertyName("changed_paths");
            w.WriteStartArray();
            foreach (string path in changedPaths)
                w.WriteStringValue(path);
            w.WriteEndArray();
            w.WritePropertyName("impacted");
            WriteReachedArray(w, traversal.Impacted);
            w.WritePropertyName("tests");
            WriteReachedArray(w, traversal.Tests);
            WriteTestEvidenceScope(w);
            w.WritePropertyName("traversal");
            WriteTraversalJson(w, traversal, maxDepth, limit);
            w.WriteEndObject();
        }

        return Utf8(buffer);
    }

    private static void WriteTraversalJson(
        Utf8JsonWriter writer,
        ImpactTraversal traversal,
        int maxDepth,
        int limit)
    {
        writer.WriteStartObject();
        writer.WriteString("status", traversal.Status);
        writer.WriteString("reason", traversal.Reason);
        writer.WriteNumber("max_depth", maxDepth);
        writer.WriteNumber("limit", limit);
        writer.WriteNumber("reached_count", traversal.Graph?.ReachedCount ?? 0);
        writer.WriteNumber("returned_count", traversal.Impacted.Count + traversal.Tests.Count);
        writer.WriteBoolean("truncated_by_depth", traversal.Graph?.TruncatedByDepth ?? false);
        writer.WriteBoolean("truncated_by_limit", traversal.Graph?.TruncatedByLimit ?? false);
        writer.WritePropertyName("seeded_paths");
        writer.WriteStartArray();
        foreach (string path in traversal.SeededPaths)
            writer.WriteStringValue(path);
        writer.WriteEndArray();
        writer.WritePropertyName("unseeded_paths");
        writer.WriteStartArray();
        foreach (string path in traversal.UnseededPaths)
            writer.WriteStringValue(path);
        writer.WriteEndArray();
        writer.WritePropertyName("deleted_paths");
        writer.WriteStartArray();
        foreach (string path in traversal.DeletedPaths)
            writer.WriteStringValue(path);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string RenderDeltaCompact(
        string workspaceId, bool complete, long fromRevision, long toRevision,
        string? artifactId, string? fromArtifactId, string deltaReason,
        IReadOnlyList<string> changedPaths, int maxDepth, int limit, ImpactTraversal traversal)
    {
        var sb = new StringBuilder();
        sb.Append("index-revision delta  ").Append(workspaceId ?? string.Empty).Append('\n');
        sb.Append("status: ").Append(complete ? "complete" : "unavailable")
          .Append("  from_revision: ").Append(fromRevision)
          .Append("  to_revision: ").Append(toRevision).Append('\n');
        sb.Append("artifact_id: ").Append(artifactId ?? "unknown")
          .Append("  from_artifact_id: ").Append(fromArtifactId ?? "missing")
          .Append("  reason: ").Append(deltaReason).Append('\n');
        sb.Append("traversal: ").Append(traversal.Status).Append('/').Append(traversal.Reason)
          .Append("  max_depth: ").Append(maxDepth).Append("  limit: ").Append(limit)
          .Append("  reached: ").Append(traversal.Graph?.ReachedCount ?? 0)
          .Append("  returned: ").Append(traversal.Impacted.Count + traversal.Tests.Count).Append('\n');
        if (!complete)
        {
            sb.Append("delta unavailable — falling back conservatively (no truthful changed_paths).");
            return BoundCompact(sb);
        }

        sb.Append("changed_paths (").Append(changedPaths.Count).Append("):");
        if (changedPaths.Count == 0)
            sb.Append(" none");
        sb.Append('\n');
        foreach (string path in changedPaths)
            sb.Append("  ").Append(path).Append('\n');
        sb.Append("seeded_paths (").Append(traversal.SeededPaths.Count).Append("):");
        if (traversal.SeededPaths.Count == 0)
            sb.Append(" none");
        sb.Append('\n');
        foreach (string path in traversal.SeededPaths)
            sb.Append("  ").Append(path).Append('\n');
        sb.Append("unseeded_paths (").Append(traversal.UnseededPaths.Count).Append("):");
        if (traversal.UnseededPaths.Count == 0)
            sb.Append(" none");
        sb.Append('\n');
        foreach (string path in traversal.UnseededPaths)
            sb.Append("  ").Append(path).Append('\n');
        sb.Append("deleted_paths (").Append(traversal.DeletedPaths.Count).Append("):");
        if (traversal.DeletedPaths.Count == 0)
            sb.Append(" none");
        sb.Append('\n');
        foreach (string path in traversal.DeletedPaths)
            sb.Append("  ").Append(path).Append('\n');
        sb.Append("impacted (").Append(traversal.Impacted.Count).Append("):\n");
        AppendReachedGroups(sb, traversal.Impacted);
        sb.Append("likely tests (").Append(traversal.Tests.Count).Append("):\n");
        AppendReachedGroups(sb, traversal.Tests);

        return BoundCompact(sb);
    }

    /// <summary>A reached symbol carrying its blast-radius hop distance (for provenance ordering + display).</summary>
    private readonly record struct Reached(IndexedSymbol Symbol, ReachedNode Evidence)
    {
        public int Hop => Evidence.Hop;
    }

    private sealed record ImpactTraversal(
        IReadOnlyList<Reached> Impacted,
        IReadOnlyList<Reached> Tests,
        GraphReachResult? Graph,
        IReadOnlyList<string> SeededPaths,
        IReadOnlyList<string> UnseededPaths,
        IReadOnlyList<string> DeletedPaths,
        string Status,
        string Reason);

    // ---------- seed resolution ----------

    // Resolve a target into seed ids. Returns false (with a rendered message) on a hard failure (not-found /
    // ambiguous); true on success (seedIds populated, possibly empty if a file has no symbols). A file target
    // never fails hard — an unknown file simply seeds nothing and falls through to the "nothing depends" note.
    private static bool SeedFromTarget(
        ISymbolLookupIndex index, SmartTargetResolver resolver, string target,
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
                note = nf.RenderMessage();
                return false;

            default:
                note = "impact: unrecognized target resolution.";
                return false;
        }
    }

    // Seed every indexed symbol of a file (D5: a file/changed-path seeds all its symbols).
    private static int SeedFromFile(ISymbolLookupIndex index, string path, List<string> seedIds)
    {
        // Canonicalize a bare basename to its indexed path when unambiguous (e.g. Service.cs → src/Service.cs).
        string resolved = index.ResolveIndexedFilePath(path) ?? path;
        int before = seedIds.Count;
        foreach (var symbol in index.FindByFilePath(resolved))
        {
            if (IsActionableSeedKind(symbol.Kind))
                seedIds.Add(symbol.SymbolId);
        }
        return seedIds.Count - before;
    }

    private static bool IsActionableSeedKind(string kind) => kind.ToLowerInvariant() switch
    {
        "import" or "export" or "field" or "enum_member" or "property" or "variable" or "constant" => false,
        _ => true,
    };

    // Seed from a unified diff (D5): per changed file, the symbols whose [start_line, end_line] intersect a
    // changed new-side range; when nothing intersects (or no spans recorded), degrade to ALL symbols in the file
    // (a safe over-approximation, noted). Returns a degradation note when any file degraded, else null.
    private static string? SeedFromDiff(ISymbolLookupIndex index, string diff, List<string> seedIds)
    {
        var degradedFiles = new List<string>();
        var unmatchedFiles = new List<string>();
        foreach (var file in DiffTargets.Parse(diff))
        {
            string resolved = index.ResolveIndexedFilePath(file.Path) ?? file.Path;
            var symbols = index.FindByFilePath(resolved);
            if (symbols.Count == 0)
            {
                unmatchedFiles.Add(file.Path);
                continue; // a changed file with no indexed symbols contributes no seeds
            }

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

        if (seedIds.Count == 0 && unmatchedFiles.Count > 0)
            return NoSeedSymbolsNote("diff file(s)", unmatchedFiles);
        if (degradedFiles.Count > 0)
            return "note: no line-precise span matched in " + string.Join(", ", degradedFiles) +
                   " — seeded the whole file(s).";
        if (unmatchedFiles.Count > 0)
            return "note: no indexed symbols matched diff file(s): " + string.Join(", ", unmatchedFiles) + ".";
        return null;
    }

    private static string NoSeedSymbolsNote(string label, IReadOnlyList<string> values) =>
        "No indexed symbols matched " + label + ": " + string.Join(", ", values) +
        ". Try search mode=file for the path, or refresh the workspace if the file was just added.";

    private static string NoDependentsNote(ISymbolLookupIndex index, IReadOnlyList<string> seedIds)
    {
        var symbolsById = SymbolLookupBatch.FindBySymbolIds(index, seedIds);
        var seeds = seedIds
            .Select(id => symbolsById.TryGetValue(id, out IndexedSymbol? symbol) ? symbol : null)
            .Where(static symbol => symbol is not null)
            .Cast<IndexedSymbol>()
            .Take(5)
            .ToArray();
        if (seeds.Length == 0)
            return "Resolved seed symbols, but no graph dependents were found.";

        var sb = new StringBuilder();
        sb.Append("Resolved seed symbols:");
        foreach (IndexedSymbol seed in seeds)
        {
            sb.Append(' ')
              .Append(seed.Name).Append(' ')
              .Append(seed.Kind).Append(' ')
              .Append(seed.FilePath).Append(':').Append(seed.StartLine)
              .Append(';');
        }
        sb.Length--;
        sb.Append(". Try trace ")
          .Append(seeds[0].Name)
          .Append(" to inspect graph edges, or search mode=source for text references not represented in the graph.");
        return sb.ToString();
    }

    // Two inclusive line ranges [aStart,aEnd] and [bStart,bEnd] overlap when each starts at or before the other ends.
    private static bool Intersects(int aStart, int aEnd, int bStart, int bEnd) =>
        aStart <= bEnd && bStart <= aEnd;

    // ---------- rendering ----------

    private static string RenderCompact(
        ImpactTraversal traversal,
        string? note,
        int maxDepth,
        int limit)
    {
        IReadOnlyList<Reached> impacted = traversal.Impacted;
        IReadOnlyList<Reached> tests = traversal.Tests;
        var sb = new StringBuilder();
        if (note is not null)
            sb.Append(note).Append('\n');

        sb.Append("# traversal\n")
            .Append("status=").Append(traversal.Status)
            .Append(" reason=").Append(traversal.Reason)
            .Append(" max_depth=").Append(maxDepth)
            .Append(" limit=").Append(limit)
            .Append(" reached=").Append(traversal.Graph?.ReachedCount ?? 0)
            .Append(" returned=").Append(impacted.Count + tests.Count)
            .Append(" truncated_by_depth=").Append(traversal.Graph?.TruncatedByDepth ?? false)
            .Append(" truncated_by_limit=").Append(traversal.Graph?.TruncatedByLimit ?? false)
            .Append('\n');

        if (impacted.Count == 0 && tests.Count == 0)
        {
            sb.Append("No impact — nothing depends on the change.");
        }
        else
        {
            sb.Append("\n# impacted (").Append(impacted.Count).Append(")\n");
            var visibleImpacted = impacted.Where(r => !IsLowSignalKind(r.Symbol.Kind)).ToList();
            int hiddenLowSignal = impacted.Count - visibleImpacted.Count;
            if (impacted.Count == 0)
                sb.Append("(none)\n");
            else if (visibleImpacted.Count == 0)
                sb.Append("(only low_signal rows hidden; use format=json for full list.)\n");
            else
            {
                int shownImpacted = Math.Min(visibleImpacted.Count, CompactImpactedLimit);
                AppendReachedGroups(sb, visibleImpacted.Take(shownImpacted).ToList());

                int hiddenImpacted = visibleImpacted.Count - shownImpacted;
                if (hiddenImpacted > 0)
                {
                    sb.Append("... ").Append(hiddenImpacted)
                        .Append(" more impacted; use format=json for full list.\n");
                }
            }

            if (hiddenLowSignal > 0)
            {
                sb.Append("low_signal hidden: ").Append(hiddenLowSignal)
                    .Append(" imports/modules (use format=json for full list.)\n");
            }

            if (tests.Count > 0)
            {
                sb.Append("\n# likely tests (").Append(tests.Count).Append(")\n");
                int shown = Math.Min(tests.Count, CompactLikelyTestsLimit);
                AppendReachedGroups(sb, tests.Take(shown).ToList());

                int hidden = tests.Count - shown;
                if (hidden > 0)
                {
                    sb.Append("... ").Append(hidden)
                        .Append(" more likely tests; use format=json for full list.\n");
                }
            }
        }

        return BoundCompact(sb);
    }

    private static void AppendReachedGroups(StringBuilder sb, IReadOnlyList<Reached> items)
    {
        var groups = new List<(string FilePath, List<Reached> Items)>();
        foreach (Reached item in items)
        {
            int groupIndex = groups.FindIndex(group => group.FilePath == item.Symbol.FilePath);
            if (groupIndex >= 0)
                groups[groupIndex].Items.Add(item);
            else
                groups.Add((item.Symbol.FilePath, new List<Reached> { item }));
        }

        foreach (var group in groups)
        {
            sb.Append(group.FilePath).Append(':').Append('\n');
            foreach (Reached item in group.Items)
                sb.Append(ReachedLine(item)).Append('\n');
        }
    }

    private static string ReachedLine(Reached r)
    {
        var s = r.Symbol;
        return $"  :{s.StartLine} {s.Name} {s.Kind} hop={r.Hop} via={r.Evidence.ReachedVia ?? "seed"} " +
               $"edge={r.Evidence.EdgeKind ?? "unknown"} source={r.Evidence.EdgeSource ?? "unknown"}";
    }

    private static bool IsLowSignalKind(string kind) =>
        string.Equals(kind, "import", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, "module", StringComparison.OrdinalIgnoreCase);

    private static string RenderJson(
        ImpactTraversal traversal,
        string? note,
        int maxDepth,
        int limit)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = NewWriter(buffer))
        {
            w.WriteStartObject();
            if (note is null) w.WriteNull("note"); else w.WriteString("note", note);
            w.WritePropertyName("impacted");
            WriteReachedArray(w, traversal.Impacted);
            w.WritePropertyName("tests");
            WriteReachedArray(w, traversal.Tests);
            WriteTestEvidenceScope(w);
            w.WritePropertyName("traversal");
            WriteTraversalJson(w, traversal, maxDepth, limit);
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
            w.WritePropertyName("impact_evidence");
            w.WriteStartObject();
            if (r.Evidence.ReachedVia is null) w.WriteNull("reached_via_symbol_id");
            else w.WriteString("reached_via_symbol_id", r.Evidence.ReachedVia);
            if (r.Evidence.EdgeKind is null) w.WriteNull("edge_kind");
            else w.WriteString("edge_kind", r.Evidence.EdgeKind);
            if (r.Evidence.EdgeConfidence is double confidence) w.WriteNumber("edge_confidence", confidence);
            else w.WriteNull("edge_confidence");
            if (r.Evidence.EdgeSource is null) w.WriteNull("edge_source");
            else w.WriteString("edge_source", r.Evidence.EdgeSource);
            w.WriteString("tier", ImpactRanker.IsExactSource(r.Evidence.EdgeSource) ? "exact" : "heuristic");
            w.WriteNumber("centrality", r.Evidence.Centrality);
            if (r.Evidence.Visibility is null) w.WriteNull("visibility");
            else w.WriteString("visibility", r.Evidence.Visibility);
            w.WriteEndObject();
            w.WritePropertyName("test_evidence");
            WriteTestEvidence(w, r.Symbol.TestEvidence);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteTestEvidence(Utf8JsonWriter w, TestRoleEvidence evidence)
    {
        w.WriteStartObject();
        w.WriteBoolean("is_test", evidence.IsTest);
        w.WriteBoolean("test_case", evidence.IsCase);
        w.WriteBoolean("test_container", evidence.IsContainer);
        w.WriteBoolean("test_lifecycle", evidence.IsLifecycle);
        w.WriteString("status", evidence.Status);
        if (evidence.Reason is null) w.WriteNull("reason"); else w.WriteString("reason", evidence.Reason);
        w.WriteEndObject();
    }

    private static void WriteTestEvidenceScope(Utf8JsonWriter w)
    {
        w.WritePropertyName("test_evidence_scope");
        w.WriteStartObject();
        w.WriteString("status", "candidate_only");
        w.WriteString("absence", "unknown");
        w.WriteEndObject();
    }

    private static string RenderCandidatesNote(IReadOnlyList<IndexedSymbol> matches)
    {
        var sb = new StringBuilder();
        sb.Append(CandidateOutput.Header(
            matches,
            supportsScope: false,
            fallback: "Multiple candidates — pass a more specific target (or a file path):")).Append('\n');
        foreach (var s in CandidateOutput.Visible(matches))
            sb.Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
        CandidateOutput.AppendRemainderNote(sb, matches.Count);
        return BoundCompact(sb);
    }

    private static string BoundCompact(StringBuilder builder)
    {
        string output = builder.ToString().TrimEnd('\n');
        if (output.Length <= CompactOutputMaxChars)
            return output;

        const string suffix =
            "\n... compact output truncated at 6000 chars; use format=json for complete machine-readable evidence.";
        int prefixLimit = CompactOutputMaxChars - suffix.Length;
        int lineEnd = output.LastIndexOf('\n', Math.Max(0, prefixLimit - 1));
        if (lineEnd < 0)
            lineEnd = prefixLimit;
        return output[..lineEnd].TrimEnd('\n') + suffix;
    }

    // The exactly-one-input guard's message. This is guidance, NOT a failure: the wrapper records it as the Empty
    // outcome (impactedCount 0), so the JSON shape uses the same "note" key the not-found path uses — an "error"
    // key is reserved for the Error outcome (matching EditService's convention). The compact text is unchanged.
    private static string Usage(bool json) => json
        ? Note(json, "impact requires exactly one of target, changed_paths, diff, or git.")
        : "Usage: pass exactly one of target (a symbol or file), changed_paths (a set of files), or diff " +
          "(a unified diff), or git/base/staged.";

    private static string Note(bool json, string message) => json
        ? ServerJson.Note(message)
        : message;

    private static Utf8JsonWriter NewWriter(ArrayBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);
}
