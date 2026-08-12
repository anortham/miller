using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
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
    private const int MaximumDepth = 5;
    private const int MaximumLimit = 1000;
    private const int MinimumRankingCandidates = 500;
    private const int MaximumRankingCandidates = 2000;
    private const int RankingCandidateMultiplier = 8;

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
        "(trace mode=refs). Example: impact target=SymbolSearchSidecar. Compact by default; format=json to chain. " +
        "Large MCP responses return an impact_output_page envelope; repeat the same call with its continuation token.")]
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
        [Description("Reverse-reachability radius (how many hops of dependents to follow), clamped to 1-5. Default 2.")]
        int max_depth = 2,
        [Description("Max impacted symbols to return, clamped to 1-1000. Default 100.")] int limit = 100,
        [Description("Output format: compact|json. Default compact.")] string format = "compact",
        [Description("Workspace selector: display_id, unique prefix, full id, registered root path, current, or primary.")] string? workspace_id = null,
        [Description("Refresh a registered workspace before reading. Defaults true when workspace_id is supplied.")]
        bool? ensure_fresh = null,
        [Description("Opaque token from an impact_output_page response. Repeat the same call arguments to read the next byte-identical fragment.")]
        string? continuation = null)
    {
        var telemetry = TelemetryContext.Current;
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string continuationWorkspaceId = string.IsNullOrWhiteSpace(workspace_id) ? "current" : workspace_id;
        try
        {
            max_depth = NormalizeDepth(max_depth);
            limit = NormalizeLimit(limit);
            bool ensureFresh = ReadToolWorkspaceRouting.ResolveEnsureFresh(workspace_id, ensure_fresh);
            using WorkspaceReadContext context = _workspaceProvider.Resolve(workspace_id, ensureFresh);
            continuationWorkspaceId =
                context.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(context.WorkspaceRoot);
            string? compactBanner = ReadToolWorkspaceRouting.CompactBanner(context, workspace_id, json);
            bool resolutionConverging =
                IndexLevelGuard.ResolutionLayerConverging(context.ReadSession.Snapshot);
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
                string refusal = ToolDiagnosticRenderer.Attach(
                    "impact",
                    ReadToolWorkspaceRouting.PrefixCompact(usage, compactBanner),
                    ToolDiagnostic.Refusal(
                        "invalid_input_selection",
                        "Impact accepts exactly one input source."),
                    json,
                    telemetry);
                return PageMcpOutput(
                    refusal,
                    json,
                    continuationWorkspaceId,
                    continuation);
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
                        string refusal = ToolDiagnosticRenderer.Attach(
                            "impact",
                            ReadToolWorkspaceRouting.PrefixCompact(usage, compactBanner),
                            ToolDiagnostic.Refusal(
                                "git_diff_unavailable",
                                "No working-tree git diff is available; provide target, changed_paths, or diff."),
                            json,
                            telemetry);
                        return PageMcpOutput(
                            refusal,
                            json,
                            continuationWorkspaceId,
                            continuation);
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
            int returnedCount;
            int nodesVisited;
            IReadOnlyList<string> unseededPaths;
            ImpactEmptyReason? emptyReason;
            string? diagnosticTraceTarget;
            bool revisionDelta = from_index_revision.HasValue;
            if (resolutionConverging)
            {
                output = string.Empty;
                impactedCount = 0;
                returnedCount = 0;
                nodesVisited = 0;
                unseededPaths = [];
                emptyReason = null;
                diagnosticTraceTarget = null;
            }
            else if (revisionDelta)
            {
                ImpactRevisionDeltaSnapshot snapshot = PrepareIndexRevisionDelta(
                    workspace_id ?? context.WorkspaceId ?? context.WorkspaceRoot,
                    context.WorkspaceRoot,
                    context.ReadSession,
                    from_index_revision!.Value,
                    from_artifact_id);
                ImpactExecution execution = RunIndexRevisionDeltaExecution(
                    snapshot,
                    context.Index,
                    context.Graph,
                    max_depth,
                    limit,
                    json,
                    indexAvailable: true);
                output = execution.Output;
                impactedCount = execution.ImpactedCount;
                returnedCount = execution.ReturnedCount;
                nodesVisited = execution.NodesVisited;
                unseededPaths = execution.UnseededPaths;
                emptyReason = execution.EmptyReason;
                diagnosticTraceTarget = execution.DiagnosticTraceTarget;
            }
            else if (emptyGitDiff)
            {
                output = Note(json, "No impact — git diff is empty.");
                impactedCount = 0;
                returnedCount = 0;
                nodesVisited = 0;
                unseededPaths = [];
                emptyReason = ImpactEmptyReason.EmptyGitDiff;
                diagnosticTraceTarget = null;
            }
            else
            {
                ImpactExecution execution = RunCore(
                    context.Index,
                    context.Graph,
                    context.Resolver,
                    target,
                    changed_paths,
                    diff,
                    max_depth,
                    limit,
                    json);
                output = execution.Output;
                impactedCount = execution.ImpactedCount;
                returnedCount = execution.ReturnedCount;
                nodesVisited = execution.NodesVisited;
                unseededPaths = execution.UnseededPaths;
                emptyReason = execution.EmptyReason;
                diagnosticTraceTarget = execution.DiagnosticTraceTarget;
            }
            output = ReadToolWorkspaceRouting.PrefixCompact(output, compactBanner);
            ToolDiagnostic? diagnostic = null;
            if (resolutionConverging)
            {
                IndexLevelGuard.MarkDegraded(telemetry, "resolution_converging");
                diagnostic = IndexLevelGuard.ResolutionConverging();
            }
            else if (IndexLevelGuard.ReferenceLayerConverging(context.IndexLevel))
            {
                // Blast-radius math over a symbols-level artifact sees relationship edges only — the impacted
                // set is incomplete by construction, so say so instead of letting a thin result read as safety.
                IndexLevelGuard.MarkDegraded(telemetry, "reference_layer_converging");
                diagnostic = IndexLevelGuard.Converging(
                    "call-site edges are missing, so the impacted-symbol set undercounts.");
            }
            else if (!revisionDelta && returnedCount == 0)
            {
                diagnostic = ImpactEmptyDiagnostic(
                    emptyReason ?? ImpactEmptyReason.NoImpactedSymbols,
                    target,
                    unseededPaths.FirstOrDefault(),
                    diagnosticTraceTarget);
            }

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
                telemetry.ResultCount = returnedCount;
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
            return PageMcpOutput(
                output,
                json,
                continuationWorkspaceId,
                continuation);
        }
        catch (Exception ex)
        {
            ToolDiagnostic diagnostic = ToolDiagnostic.FromException(ex);
            if (diagnostic.Code.StartsWith("continuation_", StringComparison.Ordinal))
                diagnostic = diagnostic with { NextActions = [] };
            if (diagnostic.Outcome == ToolDiagnosticOutcome.Error)
                telemetry?.SetError(ex);
            string diagnosticOutput = ToolDiagnosticRenderer.Render(
                "impact",
                diagnostic,
                json,
                telemetry);
            if (Encoding.UTF8.GetByteCount(diagnosticOutput) <= ToolOutputBudget.ImpactMcpMaxBytes)
                return diagnosticOutput;
            return ToolDiagnosticRenderer.Render(
                "impact",
                ToolDiagnostic.Unavailable(
                    "impact_diagnostic_output_too_large",
                    "The impact failure detail exceeded the MCP output budget."),
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

    private static ToolDiagnostic ImpactEmptyDiagnostic(
        ImpactEmptyReason reason,
        string? target,
        string? changedPath,
        string? diagnosticTraceTarget)
    {
        string code = reason switch
        {
            ImpactEmptyReason.EmptyGitDiff => "empty_git_diff",
            ImpactEmptyReason.NoSeedSymbols => "no_seed_symbols",
            ImpactEmptyReason.UnresolvedTarget => "unresolved_target",
            ImpactEmptyReason.AmbiguousTarget => "unresolved_target",
            ImpactEmptyReason.NoDependents => "no_dependents",
            _ => "no_impacted_symbols",
        };

        string? actionTarget = string.IsNullOrWhiteSpace(target) ? changedPath : target;
        IReadOnlyList<ToolDiagnosticAction> actions = code switch
        {
            "no_seed_symbols" => ChangedPathRecoveryActions(actionTarget),
            "no_dependents" when
                string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(changedPath) =>
                ChangedPathRecoveryActions(actionTarget),
            _ when string.IsNullOrWhiteSpace(actionTarget) => Array.Empty<ToolDiagnosticAction>(),
            _ => code switch
            {
                "unresolved_target" =>
                [
                    new ToolDiagnosticAction(
                        $"search(query=\"{EscapeDiagnosticTarget(target!)}\")",
                        "resolve an exact impact seed"),
                ],
                "no_dependents" =>
                [
                    new ToolDiagnosticAction(
                        $"trace(target=\"{EscapeDiagnosticTarget(diagnosticTraceTarget ?? target!)}\", mode=\"refs\")",
                        "check exact inbound reference evidence"),
                    new ToolDiagnosticAction(
                        $"inspect(target=\"{EscapeDiagnosticTarget(target!)}\", depth=\"full\")",
                        "inspect the resolved symbol and its graph relations"),
                ],
                _ => Array.Empty<ToolDiagnosticAction>(),
            },
        };
        return reason == ImpactEmptyReason.AmbiguousTarget
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

    private static IReadOnlyList<ToolDiagnosticAction> ChangedPathRecoveryActions(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ?
            [
                new ToolDiagnosticAction(
                    "workspace(operation=\"refresh\")",
                    "refresh changed files before retrying impact"),
            ]
            :
            [
                new ToolDiagnosticAction(
                    $"search(query=\"{EscapeDiagnosticTarget(path)}\", mode=\"file\")",
                    "confirm the indexed file path"),
                new ToolDiagnosticAction(
                    "workspace(operation=\"refresh\")",
                    "refresh changed files before retrying impact"),
            ];

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
        <= 250 => "101-250",
        <= 500 => "251-500",
        _ => "501-1000",
    };

    private static string DepthBucket(int depth) => depth switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        _ => "3-5",
    };

    private static int NormalizeDepth(int depth) => Math.Clamp(depth, 1, MaximumDepth);

    private static int NormalizeLimit(int limit) => Math.Clamp(limit, 1, MaximumLimit);

    private static int RankingCandidateLimit(int limit)
    {
        long scaled = Math.Max(MinimumRankingCandidates, (long)limit * RankingCandidateMultiplier);
        return Math.Max(limit, (int)Math.Min(MaximumRankingCandidates, scaled));
    }

    /// <summary>
    /// The pure execution core (no MCP/DI/telemetry; no DB — the graph is in-memory). Resolves the seed symbols
    /// per D5, runs REVERSE reachability to <paramref name="maxDepth"/> over a storage-neutral candidate window
    /// of at least <c>max(500, limit * 8)</c> rows capped at 2,000, risk-ranks that window, and applies
    /// <paramref name="limit"/>. It partitions the selected nodes into impacted symbols vs likely tests and
    /// renders compact or json with provenance. <paramref name="impactedCount"/> is the number of
    /// non-test impacted symbols; a usage error / not-found / empty closure yields 0.
    /// <paramref name="nodesVisited"/> is <c>reached_count</c>: the pre-window count of non-seed graph nodes the
    /// reverse BFS produced. It excludes labeled heuristic test candidates and is independent of the post-rank
    /// result limit; guard, not-found, and no-seed paths leave it 0.
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
        ImpactExecution execution = RunCore(
            index,
            graph,
            resolver,
            target,
            changedPaths,
            diff,
            maxDepth,
            limit,
            json);
        impactedCount = execution.ImpactedCount;
        nodesVisited = execution.NodesVisited;
        return execution.Output;
    }

    private static ImpactExecution RunCore(
        ISymbolLookupIndex index, ISymbolGraphReachability graph, SmartTargetResolver resolver,
        string? target, IReadOnlyList<string>? changedPaths, string? diff,
        int maxDepth, int limit, bool json)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(resolver);
        maxDepth = NormalizeDepth(maxDepth);
        limit = NormalizeLimit(limit);

        // --- exactly-one-input guard (D1): zero or more than one → a clear usage note, never an exception. ---
        int provided =
            (string.IsNullOrWhiteSpace(target) ? 0 : 1) +
            (changedPaths is { Count: > 0 } ? 1 : 0) +
            (string.IsNullOrEmpty(diff) ? 0 : 1);
        if (provided != 1)
            return new ImpactExecution(
                Usage(json), 0, 0, 0, [], ImpactEmptyReason.NoImpactedSymbols);

        var seedIds = new List<string>();
        var seededPaths = new List<string>();
        var unseededPaths = new List<string>();
        string? note = null;
        bool targetWasFile = false;

        if (!string.IsNullOrWhiteSpace(target))
        {
            if (!SeedFromTarget(
                    index,
                    resolver,
                    target,
                    seedIds,
                    out string? targetNote,
                    out targetWasFile))
            {
                string message = targetNote ?? "impact: unresolved target.";
                return new ImpactExecution(
                    json ? Note(json: true, message) : message,
                    0,
                    0,
                    0,
                    [],
                    message.Contains("Multiple candidates", StringComparison.Ordinal)
                        ? ImpactEmptyReason.AmbiguousTarget
                        : ImpactEmptyReason.UnresolvedTarget);
            }
            note = targetNote;
        }
        else if (changedPaths is { Count: > 0 })
        {
            foreach (var path in changedPaths)
            {
                if (SeedFromFile(index, path, seedIds) > 0)
                    seededPaths.Add(path);
                else
                    unseededPaths.Add(path);
            }
            if (unseededPaths.Count > 0)
                note = NoSeedSymbolsNote("changed path(s)", unseededPaths);
        }
        else
        {
            note = SeedFromDiff(index, diff!, seedIds, seededPaths, unseededPaths);
        }

        if (seedIds.Count == 0 && note is null)
            note = "No indexed symbols matched the impact input. Try search mode=file for the changed path.";

        if (seedIds.Count == 0)
        {
            var noSeedTraversal = new ImpactTraversal(
                [], [], null, seededPaths, unseededPaths, [],
                0, false,
                "not_run", "no_seeds");
            string output = json
                ? RenderJson(noSeedTraversal, note, maxDepth, limit)
                : RenderCompact(noSeedTraversal, note, maxDepth, limit);
            return new ImpactExecution(
                output,
                0,
                0,
                0,
                unseededPaths,
                ImpactEmptyReason.NoSeedSymbols);
        }

        RankedImpactResult rankedImpact =
            TraverseAndRankImpact(index, graph, seedIds, maxDepth, limit);
        if (rankedImpact.Impacted.Count == 0 && rankedImpact.Tests.Count == 0 && note is null)
            note = NoDependentsNote(index, seedIds);

        var traversal = new ImpactTraversal(
            rankedImpact.Impacted,
            rankedImpact.Tests,
            rankedImpact.Graph,
            seededPaths,
            unseededPaths,
            [],
            rankedImpact.ReturnedTestCandidateCount,
            rankedImpact.TestCandidatesTruncated,
            rankedImpact.Status,
            rankedImpact.Reason);
        string rendered = json
            ? RenderJson(traversal, note, maxDepth, limit)
            : RenderCompact(traversal, note, maxDepth, limit);
        return new ImpactExecution(
            rendered,
            rankedImpact.Impacted.Count,
            rankedImpact.Impacted.Count + rankedImpact.Tests.Count,
            rankedImpact.Graph.ReachedCount,
            unseededPaths,
            rankedImpact.Impacted.Count + rankedImpact.Tests.Count == 0
                ? ImpactEmptyReason.NoDependents
                : null,
            targetWasFile ? seedIds.FirstOrDefault() : target);
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
        Miller.Indexing.Reads.WorkspaceReadHandle readSession,
        long fromRevision,
        string? fromArtifactId)
    {
        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            readSession, fromRevision, fromArtifactId);
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
        return RunIndexRevisionDeltaExecution(
            snapshot,
            index,
            graph,
            maxDepth,
            limit,
            json,
            indexAvailable).Output;
    }

    internal static ImpactExecution RunIndexRevisionDeltaExecution(
        ImpactRevisionDeltaSnapshot snapshot,
        ISymbolLookupIndex? index,
        ISymbolGraphReachability? graph,
        int maxDepth,
        int limit,
        bool json,
        bool indexAvailable)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RenderIndexRevisionDeltaExecution(
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
        return RenderIndexRevisionDeltaExecution(
            workspaceId,
            complete,
            fromRevision,
            toRevision,
            changedPaths,
            index,
            graph,
            maxDepth,
            limit,
            json,
            artifactId,
            fromArtifactId,
            deltaReason,
            indexAvailable,
            deletedPaths).Output;
    }

    private static ImpactExecution RenderIndexRevisionDeltaExecution(
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
        string? artifactId,
        string? fromArtifactId,
        string deltaReason,
        bool indexAvailable,
        IReadOnlyList<string>? deletedPaths)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        maxDepth = NormalizeDepth(maxDepth);
        limit = NormalizeLimit(limit);
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

        string output = json
            ? RenderDeltaJson(workspaceId, complete, fromRevision, toRevision, artifactId, fromArtifactId,
                deltaReason, paths, maxDepth, limit, traversal)
            : RenderDeltaCompact(workspaceId, complete, fromRevision, toRevision, artifactId, fromArtifactId,
                deltaReason, paths, maxDepth, limit, traversal);
        return new ImpactExecution(
            output,
            traversal.Impacted.Count,
            traversal.Impacted.Count + traversal.Tests.Count,
            traversal.Graph?.ReachedCount ?? 0,
            traversal.UnseededPaths,
            null);

        static ImpactTraversal NotRun(string reason, IReadOnlyList<string> deleted) =>
            new([], [], null, [], [], deleted, 0, false, "not_run", reason);
    }

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

        if (seedIds.Count == 0)
            return new(
                [], [], null, seededPaths, unseededPaths, deletedPaths,
                0, false,
                "not_run", "no_seeds");

        RankedImpactResult rankedImpact =
            TraverseAndRankImpact(index, graph, seedIds, maxDepth, limit);
        return new(
            rankedImpact.Impacted,
            rankedImpact.Tests,
            rankedImpact.Graph,
            seededPaths,
            unseededPaths,
            deletedPaths,
            rankedImpact.ReturnedTestCandidateCount,
            rankedImpact.TestCandidatesTruncated,
            rankedImpact.Status,
            rankedImpact.Reason);
    }

    private static RankedImpactResult TraverseAndRankImpact(
        ISymbolLookupIndex index,
        ISymbolGraphReachability graph,
        IReadOnlyList<string> seedIds,
        int maxDepth,
        int limit)
    {
        int candidateLimit = RankingCandidateLimit(limit);
        GraphReachResult graphResult =
            graph.ReachWithEvidence(seedIds, maxDepth, candidateLimit, Direction.Reverse);
        TestCandidateExpansion expansion = AddHeuristicTestCandidates(
            index, seedIds, graphResult.Nodes, graphResult.Nodes.Count + limit);
        var symbolsById =
            SymbolLookupBatch.FindBySymbolIds(index, expansion.Nodes.Select(static node => node.Id));
        ImpactRankSignal[] selected = ImpactRanker.Rank(expansion.Nodes
            .Where(node => symbolsById.ContainsKey(node.Id))
            .Select(node =>
            {
                IndexedSymbol symbol = symbolsById[node.Id];
                return new ImpactRankSignal(node, symbol.FilePath, symbol.StartLine, symbol.Name, symbol.SymbolId);
            }))
            .Take(limit)
            .ToArray();

        var impacted = new List<Reached>();
        var tests = new List<Reached>();
        foreach (ImpactRankSignal candidate in selected)
        {
            IndexedSymbol symbol = symbolsById[candidate.SymbolId];
            (symbol.IsTest ? tests : impacted).Add(new Reached(symbol, candidate.Evidence));
        }

        int returnedTestCandidateCount = selected.Count(static candidate =>
            string.Equals(candidate.Evidence.EdgeSource, "filename_role", StringComparison.Ordinal));
        int returnedGraphCount = selected.Length - returnedTestCandidateCount;
        int resolvableGraphRows = graphResult.Nodes.Count(node => symbolsById.ContainsKey(node.Id));
        bool testCandidatesTruncated =
            expansion.Truncated || expansion.CandidateCount > returnedTestCandidateCount;
        GraphReachResult truthfulGraph = graphResult with
        {
            TruncatedByLimit =
                graphResult.TruncatedByLimit ||
                resolvableGraphRows > returnedGraphCount ||
                testCandidatesTruncated,
        };
        (string status, string reason) =
            TraversalDisposition(truthfulGraph, testCandidatesTruncated);
        return new RankedImpactResult(
            impacted,
            tests,
            truthfulGraph,
            returnedTestCandidateCount,
            testCandidatesTruncated,
            status,
            reason);
    }

    private static (string Status, string Reason) TraversalDisposition(
        GraphReachResult graphResult,
        bool testCandidatesTruncated)
    {
        bool truncatedByLimit = graphResult.TruncatedByLimit || testCandidatesTruncated;
        string status = graphResult.TruncatedByDepth || truncatedByLimit ? "truncated" : "exhausted";
        string reason = (graphResult.TruncatedByDepth, truncatedByLimit) switch
        {
            (true, true) => "depth_and_limit",
            (true, false) => "depth",
            (false, true) => "limit",
            _ => "complete",
        };
        return (status, reason);
    }

    private static TestCandidateExpansion AddHeuristicTestCandidates(
        ISymbolLookupIndex index,
        IReadOnlyList<string> seedIds,
        IReadOnlyList<ReachedNode> graphNodes,
        int limit)
    {
        var combined = graphNodes.ToList();
        int candidateCount = 0;
        var seen = new HashSet<string>(
            seedIds.Concat(graphNodes.Select(static node => node.Id)),
            StringComparer.Ordinal);
        var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IndexedSymbol> seeds =
            SymbolLookupBatch.FindBySymbolIds(index, seedIds);
        foreach (IndexedSymbol seed in seeds.Values
                     .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                     .ThenBy(static symbol => symbol.StartLine)
                     .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(seed.FilePath);
            if (string.IsNullOrWhiteSpace(stem) || !seenStems.Add(stem))
                continue;

            IReadOnlyList<string> candidatePaths = index
                .FindFilePathsByFragment(stem, int.MaxValue)
                .Where(path => IsFilenameRoleCandidate(stem, path))
                .ToArray();
            foreach (IndexedSymbol candidate in candidatePaths
                         .SelectMany(index.FindByFilePath)
                         .Where(static symbol => symbol.IsTest)
                         .OrderBy(static symbol => symbol.FilePath, StringComparer.Ordinal)
                         .ThenBy(static symbol => symbol.StartLine)
                         .ThenBy(static symbol => symbol.SymbolId, StringComparer.Ordinal))
            {
                if (!seen.Add(candidate.SymbolId))
                    continue;
                if (combined.Count >= limit)
                    return new(combined, candidateCount, true);

                combined.Add(new ReachedNode(
                    candidate.SymbolId,
                    1,
                    seed.SymbolId,
                    "test_candidate",
                    0.35,
                    "filename_role",
                    Visibility: candidate.Visibility));
                candidateCount++;
            }
        }
        return new(combined, candidateCount, false);
    }

    private static bool IsFilenameRoleCandidate(string sourceStem, string candidatePath)
    {
        string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
        if (candidateStem.StartsWith(sourceStem, StringComparison.OrdinalIgnoreCase) &&
            IsTestRole(candidateStem[sourceStem.Length..].Trim('_', '.', '-')))
            return true;

        return candidateStem.EndsWith(sourceStem, StringComparison.OrdinalIgnoreCase) &&
               IsTestRole(candidateStem[..^sourceStem.Length].Trim('_', '.', '-'));
    }

    private static bool IsTestRole(string value) =>
        value.Equals("test", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("specs", StringComparison.OrdinalIgnoreCase);

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
        writer.WriteNumber(
            "graph_returned_count",
            traversal.Impacted.Count + traversal.Tests.Count - traversal.TestCandidateCount);
        writer.WriteNumber("test_candidate_count", traversal.TestCandidateCount);
        writer.WriteBoolean("test_candidates_truncated", traversal.TestCandidatesTruncated);
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
          .Append("  returned: ").Append(traversal.Impacted.Count + traversal.Tests.Count)
          .Append("  graph_returned: ")
          .Append(traversal.Impacted.Count + traversal.Tests.Count - traversal.TestCandidateCount)
          .Append("  test_candidates: ").Append(traversal.TestCandidateCount)
          .Append("  test_candidates_truncated: ").Append(traversal.TestCandidatesTruncated)
          .Append('\n');
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
        int TestCandidateCount,
        bool TestCandidatesTruncated,
        string Status,
        string Reason);

    private sealed record TestCandidateExpansion(
        IReadOnlyList<ReachedNode> Nodes,
        int CandidateCount,
        bool Truncated);

    private sealed record RankedImpactResult(
        IReadOnlyList<Reached> Impacted,
        IReadOnlyList<Reached> Tests,
        GraphReachResult Graph,
        int ReturnedTestCandidateCount,
        bool TestCandidatesTruncated,
        string Status,
        string Reason);

    internal enum ImpactEmptyReason
    {
        EmptyGitDiff,
        NoSeedSymbols,
        UnresolvedTarget,
        AmbiguousTarget,
        NoDependents,
        NoImpactedSymbols,
    }

    internal sealed record ImpactExecution(
        string Output,
        int ImpactedCount,
        int ReturnedCount,
        int NodesVisited,
        IReadOnlyList<string> UnseededPaths,
        ImpactEmptyReason? EmptyReason,
        string? DiagnosticTraceTarget = null);

    // ---------- seed resolution ----------

    private static bool SeedFromTarget(
        ISymbolLookupIndex index, SmartTargetResolver resolver, string target,
        List<string> seedIds, out string? note, out bool targetWasFile)
    {
        note = null;
        targetWasFile = false;
        var resolution = resolver.Resolve(target);
        switch (resolution)
        {
            case TargetResolution.Symbol sym:
                seedIds.Add(sym.Value.SymbolId);
                return true;

            case TargetResolution.File file:
                targetWasFile = true;
                SeedFromFile(index, file.Path, seedIds);
                return true;

            case TargetResolution.Candidates cands:
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
    private static string? SeedFromDiff(
        ISymbolLookupIndex index,
        string diff,
        List<string> seedIds,
        List<string> seededPaths,
        List<string> unseededPaths)
    {
        var degradedFiles = new List<string>();
        foreach (var file in DiffTargets.Parse(diff))
        {
            string resolved = index.ResolveIndexedFilePath(file.Path) ?? file.Path;
            var symbols = index.FindByFilePath(resolved);
            if (symbols.Count == 0)
            {
                unseededPaths.Add(file.Path);
                continue;
            }

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
                foreach (var symbol in symbols)
                    seedIds.Add(symbol.SymbolId);
                degradedFiles.Add(resolved);
            }
            seededPaths.Add(file.Path);
        }

        if (degradedFiles.Count > 0)
            return "note: no line-precise span matched in " + string.Join(", ", degradedFiles) +
                   " — seeded the whole file(s).";
        if (unseededPaths.Count > 0)
            return NoSeedSymbolsNote("diff file(s)", unseededPaths);
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
            .Append(" graph_returned=").Append(impacted.Count + tests.Count - traversal.TestCandidateCount)
            .Append(" test_candidates=").Append(traversal.TestCandidateCount)
            .Append(" test_candidates_truncated=").Append(traversal.TestCandidatesTruncated)
            .Append(" truncated_by_depth=").Append(traversal.Graph?.TruncatedByDepth ?? false)
            .Append(" truncated_by_limit=").Append(traversal.Graph?.TruncatedByLimit ?? false)
            .Append('\n');
        AppendCompactPaths(sb, "seeded_paths", traversal.SeededPaths);
        AppendCompactPaths(sb, "unseeded_paths", traversal.UnseededPaths);

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

    private static void AppendCompactPaths(
        StringBuilder builder,
        string label,
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        const int shownLimit = 5;
        builder.Append(label).Append(" (").Append(paths.Count).Append("): ")
            .Append(string.Join(", ", paths.Take(shownLimit)));
        if (paths.Count > shownLimit)
            builder.Append(" ... +").Append(paths.Count - shownLimit).Append(" (use format=json for all)");
        builder.Append('\n');
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

    private static string PageMcpOutput(
        string output,
        bool json,
        string workspaceId,
        string? continuation)
    {
        int totalBytes = Encoding.UTF8.GetByteCount(output);
        if (totalBytes <= ToolOutputBudget.ImpactMcpMaxBytes &&
            string.IsNullOrWhiteSpace(continuation))
        {
            return output;
        }

        byte[] outputBytes = Encoding.UTF8.GetBytes(output);
        string outputHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(outputBytes));
        var identity = new ToolContinuationIdentity(
            workspaceId,
            "impact",
            outputHash,
            0,
            outputBytes.LongLength);
        int fragmentBudget = Math.Min(
            totalBytes,
            ToolOutputBudget.ImpactMcpMaxBytes / 2);

        while (fragmentBudget > 0)
        {
            ToolOutputPage page = ToolOutputBudget.PageBody(
                output,
                fragmentBudget,
                identity,
                continuation);
            string envelope = RenderMcpOutputPage(page, json, totalBytes);
            if (Encoding.UTF8.GetByteCount(envelope) <= ToolOutputBudget.ImpactMcpMaxBytes)
                return envelope;
            fragmentBudget /= 2;
        }

        throw new ToolDiagnosticException(ToolDiagnostic.Unavailable(
            "impact_output_page_unavailable",
            "The MCP output budget cannot contain an impact continuation page."));
    }

    private static string RenderMcpOutputPage(
        ToolOutputPage page,
        bool json,
        int totalBytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("kind", "impact_output_page");
            writer.WriteString("format", json ? "json" : "compact");
            writer.WriteString("output_fragment", page.Text);
            writer.WriteNumber("output_start_byte", page.StartOffset);
            writer.WriteNumber("output_end_byte", page.EndOffset);
            writer.WriteNumber("output_total_bytes", totalBytes);
            writer.WriteBoolean("output_truncated", page.Truncated);
            if (page.Continuation is null)
                writer.WriteNull("continuation");
            else
                writer.WriteString("continuation", page.Continuation);
            writer.WriteString(
                "note",
                "Concatenate output_fragment values in byte order to recover the complete impact response.");
            writer.WriteEndObject();
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
