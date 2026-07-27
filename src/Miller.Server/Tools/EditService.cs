using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Editing;
using Miller.Core.Freshness;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;

namespace Miller.Server.Tools;

/// <summary>
/// The pure-orchestration core of the M6 <c>edit</c> tool (m6-design Components/3, impl-order step 8), kept off
/// the MCP/DI/telemetry surface so it is unit-testable against a synthesized extract DB + a real temp workspace.
/// The pipeline (decision log #1-#7):
///
/// <list type="number">
///   <item>Parse + validate the operation; resolve <c>target</c> via <see cref="SmartTargetResolver"/>
///   (text ops → file; symbol ops → a single symbol; rename → a symbol whose name drives the workspace scan).
///   Ambiguous → candidates; not-found → a clean note.</item>
///   <item>For each file the edit touches: read its CURRENT disk content (edits splice live disk bytes, not the
///   index snapshot — decision-2).</item>
///   <item>Plan the byte-span edits (<see cref="EditPlanner"/> / <see cref="RenamePlanner"/>) → per-file
///   <see cref="PlannedEdit"/> + a <see cref="UnifiedDiff"/>. A planner error → a clean message.</item>
///   <item>preview (the default, <c>apply=false</c>) → return the diff preview (+ rename site summary), write
///   no source files, and skip the source freshness gate. A stale indexed candidate may still spend the shared
///   recovery budget requesting single-file index convergence before the preview is returned.</item>
///   <item><c>apply=true</c> → run the freshness gate per touched file (refuse if stale unless
///   <c>allow_stale</c>); then <see cref="EditApplier"/> applies with TOCTOU checks and rollback, reporting any
///   rollback failure as a partial apply; then write-through converges the index
///   (<see cref="IEditWriteThrough"/>).</item>
/// </list>
///
/// The edit operates on whatever is on disk now; the index supplies only the byte spans (symbol ops) /
/// occurrence sites (rename) and the freshness baseline. All splicing is UTF-8 byte-exact and language-agnostic.
/// </summary>
public sealed class EditService
{
    private const string FailureNoMatch = "no_match";
    private const string FailureAmbiguousMatch = "ambiguous_match";
    private const string FailureStaleTarget = "stale_target";
    private const string FailureInvalidRequest = "invalid_request";
    private const string FailureTargetNotFound = "target_not_found";
    private const string FailureApplyFailed = "apply_failed";
    private const string FailurePartialApply = "partial_apply";
    private const int RenameDiffMaxBytes = 4 * 1024;
    private const int RenameSummaryMaxBytes = 1024;
    private const int MaxRenameEvidenceSitesPerTier = 8;
    private const int MaxRenameCoverageRows = 8;
    private const int MaxRenameEvidencePathBytes = 256;
    private const int MaxPartialApplyPaths = 20;
    private const int MaxDiskAnchorCandidates = 32;
    /// <summary>The bucket for a known failure path that produced no more specific classification.</summary>
    internal const string FailureUnknown = "unknown";

    /// <summary>Wait-reason enum stamped when an edit spends budget waiting for a single-file converge.</summary>
    internal const string StaleConvergeWaitReason = "edit_stale_converge";

    private readonly MillerRepositoryIndex _index;
    private readonly SmartTargetResolver _resolver;
    private readonly string _dbPath;
    private readonly string _workspaceRoot;
    private readonly string _canonicalWorkspaceRoot;
    private readonly EditApplier _applier;
    private readonly IEditWriteThrough _writeThrough;
    private readonly IndexedSourceTextReader _indexedSourceTextReader;
    private readonly IndexedEditCandidateReader _indexedEditCandidateReader;
    private readonly RecoveryOptions _recovery;

    /// <summary>
    /// Bounded-wait tuning for gate-time stale recovery: when the freshness gate finds a touched file stale and
    /// the write-through reports <see cref="StaleRecoveryAttempt.Requested"/> (the leader will converge it
    /// asynchronously), the gate re-checks every <paramref name="PollInterval"/> until fresh or until
    /// <paramref name="Timeout"/> is spent. The budget is shared across ALL files of one Execute call (the
    /// multi-file rename gate loop), so a pathological rename cannot stack per-file waits.
    /// </summary>
    public sealed record RecoveryOptions(TimeSpan Timeout, TimeSpan PollInterval)
    {
        /// <summary>2.5s budget (one leader debounce tick + a single-file extract + margin), 150ms polls.</summary>
        public static RecoveryOptions Default { get; } = new(
            Timeout: TimeSpan.FromMilliseconds(2500), PollInterval: TimeSpan.FromMilliseconds(150));
    }

    /// <summary>
    /// Construct over the resolved workspace dependencies.
    /// </summary>
    /// <param name="index">The live in-memory index (for qualified <c>Parent.Member</c> symbol resolution).</param>
    /// <param name="resolver">Smart-string target resolution over the live index.</param>
    /// <param name="dbPath">The julie extract DB (Mode=ReadOnly) for span/site reads + the freshness baseline.</param>
    /// <param name="workspaceRoot">The absolute workspace root; relative indexed paths compose under it for disk I/O.</param>
    /// <param name="applier">The atomic apply transaction (writer-lock + TOCTOU + rollback).</param>
    /// <param name="writeThrough">Post-apply index convergence (leader reindex, else watcher backstop).</param>
    /// <param name="indexedSourceTextReader">Advisory source-corpus reader for stale-index diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public EditService(
        MillerRepositoryIndex index, SmartTargetResolver resolver, string dbPath, string workspaceRoot,
        EditApplier applier, IEditWriteThrough writeThrough, IndexedSourceTextReader? indexedSourceTextReader = null,
        IndexedEditCandidateReader? indexedEditCandidateReader = null,
        RecoveryOptions? recoveryOptions = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(writeThrough);
        _index = index;
        _resolver = resolver;
        _dbPath = dbPath;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _canonicalWorkspaceRoot = PathCanonicalizer.CanonicalizeRoot(workspaceRoot);
        _applier = applier;
        _writeThrough = writeThrough;
        _indexedSourceTextReader = indexedSourceTextReader ?? new IndexedSourceTextReader();
        _indexedEditCandidateReader = indexedEditCandidateReader ?? new IndexedEditCandidateReader();
        _recovery = recoveryOptions ?? RecoveryOptions.Default;
    }

    /// <summary>The outcome of an <c>edit</c> call: the rendered output plus the structured flags the tool/telemetry need.</summary>
    /// <param name="Output">Compact markdown (diff preview / apply summary / error) or JSON, per the request format.</param>
    /// <param name="Applied">True iff files were written to disk.</param>
    /// <param name="StaleAllowed">True iff the freshness gate was bypassed via <c>allow_stale</c> for this call.</param>
    /// <param name="IndexFresh">The freshness verdict for the touched files (null when not evaluated, e.g. preview / error).</param>
    /// <param name="Outcome">A coarse classification for telemetry: "ok" | "empty" | "error".</param>
    /// <param name="ResultCount">Files touched (apply) or sites previewed (rename preview); 0 on error/not-found.</param>
    /// <param name="FailureReason">A privacy-safe stable failure bucket, or null when the edit did not fail.</param>
    public readonly record struct EditResult(
        string Output, bool Applied, bool StaleAllowed, bool? IndexFresh, string Outcome, int ResultCount,
        string? FailureReason = null)
    {
        /// <summary>
        /// True iff the call spent budget waiting for a single-file index converge before producing this result
        /// (design §7.5). Telemetry-only: the tool shell turns it into the <c>wait_reason</c> enum.
        /// </summary>
        public bool StaleWaitPerformed { get; init; }
        public bool PartiallyApplied { get; init; }
        public IReadOnlyList<string> FilesLeftModified { get; init; } = [];
        public int FilesLeftModifiedTotalCount { get; init; }
        public int FilesLeftModifiedOmittedCount { get; init; }
        public ToolDiagnostic? Diagnostic { get; init; }
    }

    /// <summary>Run the full edit pipeline for <paramref name="request"/>. Never throws for an expected condition.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public EditResult Execute(EditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool json = string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(request.Format, "compact", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return WithDiagnostic(
                Error(
                    $"unknown format '{request.Format}'. Valid: compact, json.",
                    json,
                    failureReason: FailureInvalidRequest),
                json);
        }

        if (!TryParseOperation(request.Operation, out var op))
            return WithDiagnostic(
                Error($"unknown operation '{request.Operation}'. Valid: {string.Join(", ", OperationNames)}.", json,
                    failureReason: FailureInvalidRequest),
                json);

        if (!TryParseOccurrence(request.Occurrence, out var occurrence))
            return WithDiagnostic(
                Error($"unknown occurrence '{request.Occurrence}'. Valid: first, last, all.", json,
                    failureReason: FailureInvalidRequest),
                json);

        if (op == EditOperation.ReplaceText && !TryParseMatchMode(request.MatchMode, out _))
            return WithDiagnostic(
                Error($"unknown match_mode '{request.MatchMode}'. Valid: auto, exact, normalized, fuzzy.", json,
                    failureReason: FailureInvalidRequest),
                json);

        EditResult result;
        try
        {
            result = op == EditOperation.RenameSymbol
                ? ExecuteRename(request, json)
                : ExecuteSingleFile(request, op, occurrence, json);
        }
        catch (InvalidEditTargetPathException ex)
        {
            result = Error(ex.Message, json, failureReason: FailureInvalidRequest);
        }
        return WithDiagnostic(result, json);
    }

    private static EditResult WithDiagnostic(EditResult result, bool json)
    {
        if (string.Equals(result.Outcome, "ok", StringComparison.Ordinal))
            return result;

        ToolDiagnostic diagnostic = result.FailureReason switch
        {
            FailureNoMatch or FailureTargetNotFound =>
                ToolDiagnostic.ExpectedEmpty(result.FailureReason, DiagnosticMessage(result.Output)),
            FailureAmbiguousMatch =>
                ToolDiagnostic.Ambiguity(result.FailureReason, DiagnosticMessage(result.Output)),
            FailureInvalidRequest or FailureStaleTarget =>
                ToolDiagnostic.Refusal(result.FailureReason, DiagnosticMessage(result.Output)),
            FailureApplyFailed or FailurePartialApply =>
                ToolDiagnostic.Unavailable(result.FailureReason, DiagnosticMessage(result.Output)),
            _ => ToolDiagnostic.InternalFailure(
                result.FailureReason ?? FailureUnknown,
                DiagnosticMessage(result.Output)),
        };
        return result with
        {
            Output = ToolDiagnosticRenderer.Attach("edit", result.Output, diagnostic, json, telemetry: null),
            Diagnostic = diagnostic,
        };
    }

    private static string DiagnosticMessage(string output) =>
        SearchTool.Truncate(output, 1_024);

    // ---------- single-file operations ----------

    // allowRecovery=false marks the ONE internal post-recovery retry: the gate is already known-fresh, so the
    // retry must never re-enter TryRecoverFreshness (recursion guard) — a stale verdict there just refuses.
    private EditResult ExecuteSingleFile(
        EditRequest request, EditOperation op, Occurrence occurrence, bool json, bool allowRecovery = true)
    {
        // Resolve the target. Text ops act on a FILE; the symbol ops need a single resolved SYMBOL.
        if (op == EditOperation.ReplaceText)
        {
            var fileResolution = _resolver.Resolve(request.Target, request.Scope, TargetKind.File);
            if (fileResolution is not TargetResolution.File file)
                return NotFound(request.Target, json);
            return PlanAndFinishSingleFile(request, op, occurrence, file.Path, span: null, json, allowRecovery);
        }

        var resolution = ResolveSymbol(request.Target, request.Scope);
        switch (resolution)
        {
            case TargetResolution.Symbol sym:
            {
                // add_doc onto an already-documented symbol would stack a second doc block above the first.
                // julie persists doc_comment for every language, so consume that signal and refuse with guidance
                // rather than re-deriving per-language comment syntax to detect the existing doc.
                if (op == EditOperation.AddDoc &&
                    !string.IsNullOrWhiteSpace(ExtractReader.ReadDetail(_dbPath, sym.Value.SymbolId)?.DocComment))
                {
                    return Error(
                        $"symbol '{sym.Value.Name}' already has a doc comment. Use replace_text to modify the " +
                        "existing doc, or insert_before to prepend lines — add_doc only documents an undocumented symbol.",
                        json,
                        failureReason: FailureInvalidRequest);
                }

                var span = ExtractReader.ReadEditSpan(_dbPath, sym.Value.SymbolId);
                if (span is null)
                    return Error(
                        $"symbol '{sym.Value.Name}' has no recorded span in the current index — the index is " +
                        "behind the file (its id changed since the last extract). Re-index (or wait for the " +
                        "freshness poll) and retry.", json,
                        failureReason: FailureStaleTarget);
                return PlanAndFinishSingleFile(request, op, occurrence, sym.Value.FilePath, span, json, allowRecovery);
            }
            case TargetResolution.Candidates cands:
                return Candidates(cands.Matches, json);
            case TargetResolution.NotFound:
            case TargetResolution.File: // a file target for a symbol op is a usage error
                return NotFound(request.Target, json);
            default:
                return Error(
                    $"could not resolve '{request.Target}' to a single symbol. Locate it with inspect or search, " +
                    "then retry with scope=<file> to disambiguate.", json,
                    failureReason: FailureTargetNotFound);
        }
    }

    private EditResult PlanAndFinishSingleFile(
        EditRequest request, EditOperation op, Occurrence occurrence,
        string relativePath, SymbolEditSpan? span, bool json, bool allowRecovery)
    {
        string absPath = ToAbsolute(relativePath);
        if (!File.Exists(absPath))
            return Error($"file not on disk: {relativePath} (index references it, but it is missing).", json,
                failureReason: FailureTargetNotFound);

        string content = ReadDisk(absPath);

        EditMatchEvidence? evidence = null;
        EditPlan plan;
        bool staleWaitPerformed = false;
        TimeSpan recoveryBudget = _recovery.Timeout;
        if (op == EditOperation.ReplaceText)
        {
            if (request.NewText is null)
                return Error("new_text is required for replace_text.", json,
                    failureReason: FailureInvalidRequest);

            var replace = PlanReplaceText(
                relativePath,
                content,
                request,
                occurrence,
                ref recoveryBudget);
            staleWaitPerformed = replace.StaleWaitPerformed;
            if (replace.ErrorMessage is not null)
                return Error(replace.ErrorMessage, json,
                    failureReason: replace.FailureReason ?? FailureUnknown) with
                { StaleWaitPerformed = staleWaitPerformed };

            plan = replace.Plan!;
            evidence = replace.Evidence;
        }
        else
        {
            plan = op switch
            {
                EditOperation.ReplaceSymbolBody => EditPlanner.ReplaceSymbolBody(span!, request.NewText ?? string.Empty),
                EditOperation.ReplaceSymbolSignature => EditPlanner.ReplaceSymbolSignature(span!, request.NewText ?? string.Empty),
                EditOperation.InsertBefore => EditPlanner.InsertBefore(span!, request.NewText ?? string.Empty),
                EditOperation.InsertAfter => EditPlanner.InsertAfter(span!, request.NewText ?? string.Empty),
                EditOperation.AddDoc => EditPlanner.AddDoc(content, span!, request.NewText ?? string.Empty),
                _ => EditPlan.Failure(new EditError(EditErrorKind.MissingArgument, "unsupported operation")),
            };
        }

        if (!plan.IsSuccess)
        {
            EditError error = plan.Error!;
            string message = EditPlanFailureMessage(
                error, op, relativePath, request.OldText, out string failureReason);
            return Error(message, json, failureReason: failureReason);
        }

        // replace_text edits carry empty replacements (the planner only decides spans); fill in new_text here.
        IReadOnlyList<TextEdit> edits = op == EditOperation.ReplaceText
            ? FillReplacement(plan.Edits, request.NewText)
            : plan.Edits;

        string newContent;
        try
        {
            newContent = TextSplicer.Apply(content, edits);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            // A span out of range / overlap means the index span no longer fits the disk content (drift) — a
            // clean, actionable message rather than a crash.
            return Error($"edit span does not fit the current file content ({ex.Message}); re-index and retry.", json,
                failureReason: FailureStaleTarget);
        }

        var planned = new PlannedEdit(absPath, content, newContent, edits);
        return FinishSingleFile(
            request,
            op,
            occurrence,
            relativePath,
            planned,
            json,
            allowRecovery,
            recoveryBudget,
            evidence,
            staleWaitPerformed);
    }

    private EditResult FinishSingleFile(
        EditRequest request, EditOperation op, Occurrence occurrence,
        string relativePath, PlannedEdit planned, bool json, bool allowRecovery, TimeSpan recoveryBudget,
        EditMatchEvidence? evidence = null,
        bool staleWaitPerformed = false)
    {
        string diff = UnifiedDiff.Render(planned.OldContent, planned.NewContent, relativePath);

        if (!IsApply(request))
            return Preview(diff, json, renameSummary: null, siteCount: 0, evidence) with
            { StaleWaitPerformed = staleWaitPerformed };

        var gate = FreshnessGate.Check(_dbPath, relativePath, planned.FilePath, planned.OldContent);
        bool fresh = gate.Result == FreshnessResult.Fresh;
        if (!fresh && (op != EditOperation.ReplaceText || !request.AllowStale))
        {
            bool recoveryWait = false;
            fresh = allowRecovery &&
                TryRecoverFreshness(
                    relativePath,
                    planned.FilePath,
                    planned.OldContent,
                    ref recoveryBudget,
                    out recoveryWait);
            staleWaitPerformed |= recoveryWait;
            if (!fresh)
                return StaleBlocked(
                    relativePath,
                    gate.IndexedContentFound,
                    json,
                    allowStaleSafe: op == EditOperation.ReplaceText) with
                { StaleWaitPerformed = staleWaitPerformed };

            // Recovery converged the index, but a SYMBOL op's byte spans were read from the PRE-recovery index —
            // if the drift moved the symbol (e.g. lines prepended above it), those spans now point at the wrong
            // bytes and splicing them would corrupt the file silently. Re-run resolve → span read → plan once
            // against the converged index and apply THAT plan instead. replace_text derives its spans from the
            // disk content itself, so its plan is safe to apply unchanged. The retry runs with the gate already
            // known-fresh (allowRecovery=false): it never re-enters recovery, and if the symbol no longer
            // resolves it returns the existing clean not-found/no-span error rather than applying a stale plan.
            if (op != EditOperation.ReplaceText)
            {
                EditResult retry = ExecuteSingleFile(request, op, occurrence, json, allowRecovery: false);
                return retry with
                {
                    StaleWaitPerformed = staleWaitPerformed || retry.StaleWaitPerformed,
                };
            }
        }

        var applyResult = _applier.Apply([planned]);
        if (!applyResult.Success)
        {
            if (applyResult.PartiallyApplied)
                return PartialApply(applyResult, json, fresh);
            return Error(
                applyResult.Message,
                json,
                fresh,
                FailureApplyFailed);
        }

        _writeThrough.Converge([planned.FilePath]);
        return Applied(diff, staleAllowed: !fresh && request.AllowStale, filesWritten: applyResult.FilesWritten,
            indexFresh: fresh, json, evidence: evidence) with { StaleWaitPerformed = staleWaitPerformed };
    }

    private ReplaceTextPlanResult PlanReplaceText(
        string relativePath,
        string content,
        EditRequest request,
        Occurrence occurrence,
        ref TimeSpan recoveryBudget)
    {
        TryParseMatchMode(request.MatchMode, out var matchMode);
        string oldText = request.OldText ?? string.Empty;

        if (HasIndexedSelector(request))
        {
            if (occurrence == Occurrence.All)
            {
                return ReplaceTextPlanResult.Error(
                    "occurrence=all cannot be combined with query, anchor, or line because indexed selectors " +
                    "narrow matching to bounded content windows. Retry without indexed selectors for a " +
                    "whole-file replacement.",
                    FailureInvalidRequest);
            }

            IndexedEditCandidateResult candidateResult;
            try
            {
                long expectedRevision = ExpectedWorkspaceRevision();
                candidateResult = _indexedEditCandidateReader.FindCandidates(
                    _dbPath,
                    relativePath,
                    expectedRevision,
                    matchMode == TextMatchMode.Exact ? oldText : null,
                    request.Query,
                    request.Anchor,
                    request.Line);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or SqliteException or IOException or InvalidOperationException
                    or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                candidateResult = IndexedEditCandidateResult.Unavailable(
                    "workspace revision unavailable: " + ex.Message);
            }

            if (candidateResult.State == IndexedEditCandidateState.Current)
            {
                ReplaceTextPlanResult planned = PlanReplaceTextFromIndexedCandidates(
                    relativePath, content, request, occurrence, matchMode, candidateResult);
                if (planned.FailureReason != FailureStaleTarget)
                    return planned;

                ReplaceTextPlanResult? converged = WaitForCandidateConvergence(
                    relativePath,
                    content,
                    request,
                    occurrence,
                    matchMode,
                    ref recoveryBudget,
                    out bool waited);
                return (converged ?? planned) with { StaleWaitPerformed = waited };
            }

            if (candidateResult.State == IndexedEditCandidateState.NoMatch)
            {
                return ReplaceTextPlanResult.Error(
                    "no indexed edit candidates matched the selector. Narrow the edit with a different query, " +
                    "anchor, or line hint, or retry without indexed selectors.",
                    FailureNoMatch);
            }

            return PlanReplaceTextFromDiskSelectors(
                content,
                request,
                occurrence,
                matchMode,
                candidateResult.Reason);
        }

        var diskPlan = TextReplaceMatcher.Plan(content, oldText, occurrence, matchMode);
        return ReplaceTextPlanResult.Success(
            diskPlan.Plan,
            EvidenceFromPlan(
                diskPlan,
                matchSource: "disk",
                contentIndexState: "not_used",
                occurrence,
                candidateReason: null));
    }

    private ReplaceTextPlanResult PlanReplaceTextFromIndexedCandidates(
        string relativePath,
        string content,
        EditRequest request,
        Occurrence occurrence,
        TextMatchMode matchMode,
        IndexedEditCandidateResult candidateResult)
    {
        var successes = new List<(TextReplaceMatchPlan Plan, IndexedEditCandidate Candidate)>();
        foreach (IndexedEditCandidate indexedCandidate in candidateResult.Candidates)
        {
            IReadOnlyList<TextWindow> windows = FocusIndexedCandidateWindows(
                content,
                indexedCandidate,
                request,
                out bool anchorLimitExceeded);
            if (anchorLimitExceeded)
            {
                return ReplaceTextPlanResult.Error(
                    $"anchor matched more than {MaxDiskAnchorCandidates} disk locations in one indexed " +
                    "candidate. Retry with a line selector.",
                    FailureAmbiguousMatch);
            }
            foreach (TextWindow window in windows)
            {
                TextReplaceMatchPlan windowPlan = TextReplaceMatcher.Plan(
                    window.Text,
                    request.OldText ?? string.Empty,
                    occurrence,
                    matchMode);
                if (!windowPlan.IsSuccess)
                    continue;
                if (SelectorWindowIsAmbiguous(request, window, windowPlan))
                {
                    return ReplaceTextPlanResult.Error(
                        "selector window contains multiple plausible old_text locations. Retry with a more " +
                        "specific anchor or a larger exact old_text.",
                        FailureAmbiguousMatch);
                }

                TextReplaceMatchPlan offsetPlan = OffsetPlan(windowPlan, window);
                if (!successes.Any(success => SamePhysicalEdits(success.Plan, offsetPlan)))
                    successes.Add((offsetPlan, indexedCandidate));
            }
        }

        if (successes.Count == 0)
        {
            string absolutePath = ToAbsolute(relativePath);
            var gate = FreshnessGate.Check(_dbPath, relativePath, absolutePath, content);
            if (gate.Result == FreshnessResult.Fresh)
            {
                return ReplaceTextPlanResult.Error(
                    "old_text did not match current disk text within the indexed selector window. Retry with " +
                    "current old_text, a different query, anchor, or line, or without indexed selectors.",
                    FailureNoMatch);
            }

            return ReplaceTextPlanResult.Error(
                "indexed edit candidates were found, but old_text did not verify against the current disk text. " +
                "Run workspace refresh or retry with current old_text.",
                FailureStaleTarget);
        }

        if (successes.Count > 1)
        {
            string examples = string.Join(", ", successes.Take(3).Select(static s =>
                s.Candidate.LineStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                s.Candidate.LineEnd.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return ReplaceTextPlanResult.Error(
                "ambiguous indexed edit candidates matched current disk text at line ranges " + examples +
                ". Retry with a narrower line or anchor selector.",
                FailureAmbiguousMatch);
        }

        var (plan, candidate) = successes[0];
        return ReplaceTextPlanResult.Success(
            plan.Plan,
            EvidenceFromPlan(
                plan,
                matchSource: "indexed_content",
                contentIndexState: "current",
                occurrence,
                candidateReason: null,
                candidate));
    }

    /// <summary>
    /// Plan-time mirror of the apply path's bounded stale wait (design §7.5). An indexed edit candidate whose
    /// chunk pre-dates the current disk text fails verification and previously refused instantly, even though
    /// the leader converges the file within about a debounce tick. Ask the write-through to converge this ONE
    /// file, then re-discover and re-verify candidates until the plan succeeds or the shared
    /// <see cref="RecoveryOptions"/> budget is spent. Returns the converged plan, or null to let the caller
    /// return its original stale-target refusal unchanged. Polls the real success condition (a candidate that
    /// verifies against disk) rather than the freshness gate, because content.db can lag symbols.db by a tick.
    /// <paramref name="content"/> is the disk text already read by the caller and does not change during the
    /// wait: the index is converging toward it, not the other way round.
    /// </summary>
    /// <param name="waited">True iff budget was actually spent waiting — the telemetry wait-reason signal.</param>
    private ReplaceTextPlanResult? WaitForCandidateConvergence(
        string relativePath,
        string content,
        EditRequest request,
        Occurrence occurrence,
        TextMatchMode matchMode,
        ref TimeSpan recoveryBudget,
        out bool waited)
    {
        string absPath = ToAbsolute(relativePath);
        StaleRecoveryAttempt attempt = _writeThrough.TryRecoverStaleFile(absPath);
        waited = attempt != StaleRecoveryAttempt.None;
        if (!waited)
            return null;

        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                IndexedEditCandidateResult converged;
                try
                {
                    converged = _indexedEditCandidateReader.FindCandidates(
                        _dbPath, relativePath, ExpectedWorkspaceRevision(),
                        matchMode == TextMatchMode.Exact ? request.OldText ?? string.Empty : null,
                        request.Query, request.Anchor, request.Line);
                }
                catch (Exception ex) when (ex is SqliteException or FileNotFoundException or InvalidOperationException)
                {
                    converged = IndexedEditCandidateResult.Unavailable("transient read during converge");
                }

                if (converged.State == IndexedEditCandidateState.Current)
                {
                    ReplaceTextPlanResult retry = PlanReplaceTextFromIndexedCandidates(
                        relativePath, content, request, occurrence, matchMode, converged);
                    if (retry.FailureReason != FailureStaleTarget)
                        return retry;
                }

                if (attempt == StaleRecoveryAttempt.Converged || elapsed.Elapsed >= recoveryBudget)
                    return null;
                Thread.Sleep(_recovery.PollInterval);
            }
        }
        finally
        {
            recoveryBudget -= elapsed.Elapsed;
            if (recoveryBudget < TimeSpan.Zero)
                recoveryBudget = TimeSpan.Zero;
        }
    }

    private static IReadOnlyList<TextWindow> FocusIndexedCandidateWindows(
        string content,
        IndexedEditCandidate candidate,
        EditRequest request,
        out bool anchorLimitExceeded)
    {
        anchorLimitExceeded = false;
        int oldTextLines = 1 + (request.OldText?.Count(static ch => ch == '\n') ?? 0);
        int multilineSurroundingLines = oldTextLines - 1;

        if (request.Line is { } line && line >= candidate.LineStart && line <= candidate.LineEnd)
        {
            TextWindow lineWindow = SliceLineWindow(
                content,
                Math.Max(candidate.LineStart, line - multilineSurroundingLines),
                Math.Min(candidate.LineEnd, line + multilineSurroundingLines));
            return string.IsNullOrEmpty(request.Anchor) ||
                lineWindow.Text.Contains(request.Anchor, StringComparison.Ordinal)
                    ? [lineWindow]
                    : [];
        }

        if (!string.IsNullOrEmpty(request.Anchor))
        {
            TextWindow candidateWindow = SliceLineWindow(content, candidate.LineStart, candidate.LineEnd);
            var windows = new List<TextWindow>();
            int searchOffset = 0;
            while (searchOffset <= candidateWindow.Text.Length - request.Anchor.Length)
            {
                int anchorOffset = candidateWindow.Text.IndexOf(
                    request.Anchor,
                    searchOffset,
                    StringComparison.Ordinal);
                if (anchorOffset < 0)
                    break;
                if (windows.Count == MaxDiskAnchorCandidates)
                {
                    anchorLimitExceeded = true;
                    return windows;
                }
                int anchorLine = candidateWindow.StartLine + CountNewLinesBefore(candidateWindow.Text, anchorOffset);
                int anchorSurroundingLines = Math.Max(1, multilineSurroundingLines);
                windows.Add(SliceLineWindow(
                    content,
                    Math.Max(candidate.LineStart, anchorLine - anchorSurroundingLines),
                    Math.Min(candidate.LineEnd, anchorLine + anchorSurroundingLines)));
                searchOffset = anchorOffset + Math.Max(1, request.Anchor.Length);
            }
            return windows;
        }

        return [SliceLineWindow(content, candidate.LineStart, candidate.LineEnd)];
    }

    private static bool SamePhysicalEdits(TextReplaceMatchPlan left, TextReplaceMatchPlan right)
    {
        if (left.Edits.Count != right.Edits.Count)
            return false;

        for (int i = 0; i < left.Edits.Count; i++)
        {
            if (left.Edits[i].StartByte != right.Edits[i].StartByte ||
                left.Edits[i].EndByte != right.Edits[i].EndByte)
                return false;
        }

        return true;
    }

    private ReplaceTextPlanResult PlanReplaceTextFromDiskSelectors(
        string content,
        EditRequest request,
        Occurrence occurrence,
        TextMatchMode matchMode,
        string? unavailableReason)
    {
        if (!string.IsNullOrEmpty(request.Query))
        {
            return ReplaceTextPlanResult.Error(
                "the content index is unavailable, so query cannot be enforced safely. Retry after workspace " +
                "refresh, or retry without query.",
                FailureStaleTarget);
        }

        int oldTextLines = 1 + (request.OldText?.Count(static ch => ch == '\n') ?? 0);
        int surroundingLines = oldTextLines - 1;
        var windows = new List<TextWindow>();
        if (request.Line is { } line)
        {
            TextWindow window = SliceLineWindow(
                content,
                Math.Max(1, line - surroundingLines),
                line + surroundingLines);
            if (string.IsNullOrEmpty(request.Anchor) ||
                window.Text.Contains(request.Anchor, StringComparison.Ordinal))
            {
                windows.Add(window);
            }
        }
        else if (!string.IsNullOrEmpty(request.Anchor))
        {
            int searchOffset = 0;
            int anchorCandidates = 0;
            while (searchOffset <= content.Length - request.Anchor.Length)
            {
                int anchorOffset = content.IndexOf(request.Anchor, searchOffset, StringComparison.Ordinal);
                if (anchorOffset < 0)
                    break;
                if (anchorCandidates == MaxDiskAnchorCandidates)
                {
                    return ReplaceTextPlanResult.Error(
                        $"anchor matched more than {MaxDiskAnchorCandidates} disk locations while the content " +
                        "index was unavailable. Retry with a line selector or after workspace refresh.",
                        FailureAmbiguousMatch);
                }
                anchorCandidates++;
                int anchorLine = 1 + CountNewLinesBefore(content, anchorOffset);
                int anchorSurroundingLines = Math.Max(1, surroundingLines);
                windows.Add(SliceLineWindow(
                    content,
                    Math.Max(1, anchorLine - anchorSurroundingLines),
                    anchorLine + anchorSurroundingLines));
                searchOffset = anchorOffset + Math.Max(1, request.Anchor.Length);
            }
        }

        var successes = new List<TextReplaceMatchPlan>();
        foreach (TextWindow window in windows)
        {
            TextReplaceMatchPlan windowPlan = TextReplaceMatcher.Plan(
                window.Text,
                request.OldText ?? string.Empty,
                occurrence,
                matchMode);
            if (!windowPlan.IsSuccess)
                continue;
            if (SelectorWindowIsAmbiguous(request, window, windowPlan))
            {
                return ReplaceTextPlanResult.Error(
                    "selector window contains multiple plausible old_text locations. Retry with a more specific " +
                    "anchor or a larger exact old_text.",
                    FailureAmbiguousMatch);
            }
            TextReplaceMatchPlan offsetPlan = OffsetPlan(windowPlan, window);
            if (!successes.Any(success => SamePhysicalEdits(success, offsetPlan)))
                successes.Add(offsetPlan);
            if (successes.Count > 1)
                break;
        }

        if (successes.Count == 0)
        {
            return ReplaceTextPlanResult.Error(
                "old_text did not match current disk text within the requested line or anchor window.",
                FailureNoMatch);
        }
        if (successes.Count > 1)
        {
            return ReplaceTextPlanResult.Error(
                "multiple disk windows matched old_text while the content index was unavailable. Retry with a " +
                "line selector or after workspace refresh.",
                FailureAmbiguousMatch);
        }

        TextReplaceMatchPlan plan = successes[0];
        return ReplaceTextPlanResult.Success(
            plan.Plan,
            EvidenceFromPlan(
                plan,
                matchSource: "disk_selector_after_index_unavailable",
                contentIndexState: "unavailable",
                occurrence,
                candidateReason: unavailableReason));
    }

    private static int CountNewLinesBefore(string text, int exclusiveEnd)
    {
        int count = 0;
        int limit = Math.Min(text.Length, Math.Max(0, exclusiveEnd));
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
                count++;
        }

        return count;
    }

    private static TextReplaceMatchPlan OffsetPlan(TextReplaceMatchPlan plan, TextWindow window)
    {
        var edits = plan.Edits
            .Select(e => new TextEdit(e.StartByte + window.StartByte, e.EndByte + window.StartByte, e.Replacement))
            .ToArray();
        var matches = plan.Matches
            .Select(m => m with
            {
                StartByte = m.StartByte + window.StartByte,
                EndByte = m.EndByte + window.StartByte,
                StartLine = m.StartLine + window.StartLine - 1,
                EndLine = m.EndLine + window.StartLine - 1,
            })
            .ToArray();
        return new TextReplaceMatchPlan(
            EditPlan.Success(edits),
            plan.RequestedMode,
            plan.MatchedMode,
            matches,
            plan.MatchCount,
            plan.AmbiguousMatchCount);
    }

    private static EditMatchEvidence EvidenceFromPlan(
        TextReplaceMatchPlan plan,
        string matchSource,
        string contentIndexState,
        Occurrence occurrence,
        string? candidateReason,
        IndexedEditCandidate? candidate = null)
    {
        int? lineStart = plan.Matches.Count == 0 ? null : plan.Matches.Min(static m => m.StartLine);
        int? lineEnd = plan.Matches.Count == 0 ? null : plan.Matches.Max(static m => m.EndLine);
        return new EditMatchEvidence(
            plan.MatchedMode?.ToString().ToLowerInvariant() ?? plan.RequestedMode.ToString().ToLowerInvariant(),
            matchSource,
            contentIndexState,
            lineStart,
            lineEnd,
            plan.MatchCount,
            plan.Matches.Count,
            OccurrenceName(occurrence),
            DiskVerified: plan.IsSuccess,
            candidateReason,
            candidate?.LineStart,
            candidate?.LineEnd,
            plan.MatchedMode == TextMatchMode.Fuzzy
                ? [.. plan.Matches
                    .OrderBy(static m => m.StartLine)
                    .Select(static m => (m.StartLine, m.Distance))]
                : []);
    }

    private long ExpectedWorkspaceRevision()
    {
        using var reader = new FreshnessReader(_dbPath);
        return reader.LatestRevision();
    }

    private static bool HasIndexedSelector(EditRequest request) =>
        !string.IsNullOrEmpty(request.Query) ||
        !string.IsNullOrEmpty(request.Anchor) ||
        request.Line is not null;

    private static bool SelectorWindowIsAmbiguous(
        EditRequest request,
        TextWindow window,
        TextReplaceMatchPlan plan)
    {
        if (string.IsNullOrEmpty(request.Anchor) && request.Line is null)
            return false;
        if (plan.AmbiguousMatchCount > 1)
            return true;

        string oldText = request.OldText ?? string.Empty;
        if (oldText.Length == 0)
            return false;

        int from = 0;
        while (from <= window.Text.Length - oldText.Length)
        {
            int at = window.Text.IndexOf(oldText, from, StringComparison.Ordinal);
            if (at < 0)
                break;

            int literalStart = at;
            int literalEnd = at + oldText.Length;
            while (literalStart < literalEnd && char.IsWhiteSpace(window.Text[literalStart]))
                literalStart++;
            while (literalEnd > literalStart && char.IsWhiteSpace(window.Text[literalEnd - 1]))
                literalEnd--;
            if (literalStart == literalEnd)
            {
                literalStart = at;
                literalEnd = at + oldText.Length;
            }

            int startLine = 1 + CountNewLinesBefore(window.Text, literalStart);
            int endLine = 1 + CountNewLinesBefore(window.Text, Math.Max(literalStart, literalEnd - 1));
            if (!plan.Matches.Any(match =>
                    startLine >= match.StartLine &&
                    endLine <= match.EndLine))
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static TextWindow SliceLineWindow(string content, int lineStart, int lineEnd)
    {
        int startChar = CharOffsetOfLineStart(content, lineStart);
        int endChar = CharOffsetOfLineStart(content, lineEnd + 1);
        int startByte = Encoding.UTF8.GetByteCount(content.AsSpan(0, startChar));
        return new TextWindow(content[startChar..endChar], startByte, lineStart);
    }

    private static int CharOffsetOfLineStart(string content, int line)
    {
        if (line <= 1)
            return 0;

        int seen = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            seen++;
            if (seen == line)
                return i + 1;
        }

        return content.Length;
    }

    // ---------- workspace-wide rename ----------

    // allowRecovery=false marks the ONE internal post-recovery retry (see FinishSingleFile): the touched files
    // were just converged, so the retry must never re-enter TryRecoverFreshness — a stale verdict just refuses.
    private EditResult ExecuteRename(EditRequest request, bool json, bool allowRecovery = true)
    {
        if (string.IsNullOrWhiteSpace(request.NewText))
            return Error("new_text (the new name) is required for rename_symbol.", json,
                failureReason: FailureInvalidRequest);

        var resolution = ResolveSymbol(request.Target, request.Scope);
        IndexedSymbol target;
        switch (resolution)
        {
            case TargetResolution.Symbol sym:
                target = sym.Value;
                break;
            case TargetResolution.Candidates cands:
                return Candidates(cands.Matches, json);
            default:
                return NotFound(request.Target, json);
        }

        string oldName = target.Name;
        string newName = request.NewText!;
        if (!RenamePlanner.IsValidIdentifier(newName))
        {
            return Error(
                $"new_name \"{newName}\" is not a valid identifier (must start with a letter or underscore and " +
                "contain only letters, digits, or underscores).",
                json,
                failureReason: FailureInvalidRequest);
        }

        string renameMode = string.IsNullOrWhiteSpace(request.RenameMode)
            ? "exact"
            : request.RenameMode.Trim().ToLowerInvariant();
        if (renameMode is not ("exact" or "include_fallback"))
        {
            return Error(
                "rename_mode must be exact or include_fallback.",
                json,
                failureReason: FailureInvalidRequest);
        }

        var evidenceBounds = new ReferenceEvidenceBounds(int.MaxValue, int.MaxValue);
        ReferenceEvidenceSet evidence = ReferenceEvidenceReader.Read(
            _dbPath,
            target.SymbolId,
            evidenceBounds);
        int oldNameByteLength = Encoding.UTF8.GetByteCount(oldName);
        IReadOnlyList<IdentifierSite> exactSites = RenameIdentifierSites(evidence.Exact, oldNameByteLength);
        int unusableExactSites = CountUnreachableExactSites(evidence.Exact, oldNameByteLength);
        int missingExactFiles = exactSites
            .Select(site => site.FilePath)
            .Distinct(StringComparer.Ordinal)
            .Count(path => !File.Exists(ToAbsolute(path)));
        bool incompleteExactCoverage =
            evidence.Coverage.ExactTruncated ||
            unusableExactSites > 0 ||
            missingExactFiles > 0 ||
            evidence.Coverage.FallbackAvailable > 0;
        if (renameMode == "exact" && incompleteExactCoverage)
        {
            return Error(
                "incomplete exact reference coverage: " +
                $"{evidence.Coverage.ExactAvailable} exact site(s), " +
                $"{unusableExactSites} exact site(s) without usable byte spans, and " +
                $"{missingExactFiles} missing exact file(s), and " +
                $"{evidence.Coverage.FallbackAvailable} unresolved fallback candidate(s). " +
                "Refresh the workspace or explicitly retry with rename_mode=include_fallback after reviewing " +
                "the name-based homonym risk.",
                json,
                failureReason: FailureNoMatch);
        }

        IReadOnlyList<IdentifierSite> fallbackSites = [];
        if (renameMode == "include_fallback")
        {
            var exactKeys = exactSites
                .Select(static site => (site.FilePath, site.StartByte, site.EndByte))
                .ToHashSet();
            var resolvedHomonymKeys = _index.FindByName(oldName)
                .Where(symbol => !string.Equals(symbol.SymbolId, target.SymbolId, StringComparison.Ordinal))
                .SelectMany(symbol => RenameIdentifierSites(
                    ReferenceEvidenceReader.Read(_dbPath, symbol.SymbolId, evidenceBounds).Exact,
                    oldNameByteLength))
                .Select(static site => (site.FilePath, site.StartByte, site.EndByte))
                .ToHashSet();
            IReadOnlyList<IdentifierSite> nameBasedSites = ExtractReader.ReadIdentifierSites(_dbPath, oldName)
                .Where(site =>
                    !exactKeys.Contains((site.FilePath, site.StartByte, site.EndByte))
                    && !resolvedHomonymKeys.Contains((site.FilePath, site.StartByte, site.EndByte)))
                .ToArray();
            int unusableFallbackSites = nameBasedSites.Count(
                site => site.StartByte < 0 || site.EndByte - site.StartByte != oldNameByteLength);
            if (unusableFallbackSites > 0)
            {
                return Error(
                    $"fallback rename coverage includes {unusableFallbackSites} site(s) without a usable byte span. " +
                    "Refresh the workspace and retry.",
                    json,
                    failureReason: FailureNoMatch);
            }
            fallbackSites = nameBasedSites;
        }

        string? missingSelectedFile = exactSites
            .Concat(fallbackSites)
            .Select(site => site.FilePath)
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(path => !File.Exists(ToAbsolute(path)));
        if (missingSelectedFile is not null)
        {
            return Error(
                $"rename coverage includes missing file '{missingSelectedFile}'. Refresh the workspace and retry.",
                json,
                failureReason: FailureNoMatch);
        }

        IReadOnlyList<IdentifierSite> sites = exactSites.Concat(fallbackSites).ToArray();
        var span = ExtractReader.ReadEditSpan(_dbPath, target.SymbolId);

        var files = BuildRenameFiles(
            oldName,
            sites,
            target,
            span,
            out IdentifierSite? definitionSite,
            out IdentifierSite? invalidSite);
        if (invalidSite is not null)
        {
            string invalidPath = ToAbsolute(invalidSite.FilePath);
            var invalidGate = FreshnessGate.Check(
                _dbPath,
                invalidSite.FilePath,
                invalidPath,
                ReadDisk(invalidPath));
            if (invalidGate.Result == FreshnessResult.Fresh || !IsApply(request))
            {
                return Error(
                    $"rename coverage includes a byte span in '{invalidSite.FilePath}' that does not match " +
                    $"the old identifier '{oldName}'. Refresh the workspace and retry.",
                    json,
                    failureReason: invalidGate.Result == FreshnessResult.Fresh
                        ? FailureNoMatch
                        : FailureStaleTarget);
            }

            // Applying against a stale file whose spans already failed the token check: this plan is known to be
            // incomplete (the mismatched sites were excluded above). Converge and re-resolve rather than
            // carrying it forward — the per-file gate below cannot catch this, because another process making
            // the file fresh in the meantime leaves anyRecovered false and would apply the incomplete plan.
            TimeSpan invalidRecoveryBudget = _recovery.Timeout;
            bool invalidRecoveryWait = false;
            if (allowRecovery && TryRecoverFreshness(
                    invalidSite.FilePath,
                    invalidPath,
                    ReadDisk(invalidPath),
                    ref invalidRecoveryBudget,
                    out invalidRecoveryWait))
            {
                EditResult replanned = ExecuteRename(request, json, allowRecovery: false);
                return replanned with { StaleWaitPerformed = replanned.StaleWaitPerformed || invalidRecoveryWait };
            }

            return StaleBlocked(invalidSite.FilePath, invalidGate.IndexedContentFound, json, allowStaleSafe: false)
                with { StaleWaitPerformed = invalidRecoveryWait };
        }
        if (definitionSite is null)
        {
            return Error(
                "incomplete exact reference coverage: the selected symbol definition name token could not be " +
                "proved against current disk content. Refresh the workspace and retry.",
                json,
                failureReason: FailureNoMatch);
        }
        if (files.Count == 0)
            return Error($"no occurrences of '{oldName}' found to rename.", json,
                failureReason: FailureNoMatch);

        RenamePlan plan = RenamePlanner.Plan(oldName, newName, files);
        if (!plan.IsSuccess)
            return Error(plan.Error!.Message, json,
                failureReason: FailureReasonFor(plan.Error.Kind));

        string diff = RenderRenameDiff(plan);
        IReadOnlyList<IdentifierSite> renderedExactSites = exactSites
            .Where(site =>
                !string.Equals(site.FilePath, definitionSite.FilePath, StringComparison.Ordinal)
                || site.StartByte != definitionSite.StartByte
                || site.EndByte != definitionSite.EndByte)
            .ToArray();
        IReadOnlyList<ReferenceEvidence> renderedExactEvidence = evidence.Exact
            .Where(reference =>
                !string.Equals(reference.FilePath, definitionSite.FilePath, StringComparison.Ordinal)
                || reference.StartByte != definitionSite.StartByte
                || reference.EndByte != definitionSite.EndByte)
            .ToArray();
        var renameEvidence = new RenameEvidenceSummary(
            renameMode,
            target,
            renderedExactSites,
            fallbackSites,
            evidence.Coverage,
            renderedExactEvidence);
        string summary = RenderRenameSummary(oldName, newName, plan, renameEvidence);

        if (!IsApply(request))
            return Preview(diff, json, summary, plan.TotalSites, renameEvidence: renameEvidence);

        bool anyStale = false;
        bool anyRecovered = false;
        bool staleWaitPerformed = false;
        TimeSpan renameRecoveryBudget = _recovery.Timeout;
        foreach (var pe in plan.PlannedEdits)
        {
            string rel = ToRelative(pe.FilePath);
            var gate = FreshnessGate.Check(_dbPath, rel, pe.FilePath, pe.OldContent);
            if (gate.Result != FreshnessResult.Fresh)
            {
                bool recoveryWait = false;
                if (allowRecovery && TryRecoverFreshness(
                    rel,
                    pe.FilePath,
                    pe.OldContent,
                    ref renameRecoveryBudget,
                    out recoveryWait))
                {
                    anyRecovered = true;
                    staleWaitPerformed |= recoveryWait;
                }
                else
                {
                    staleWaitPerformed |= recoveryWait;
                    return StaleBlocked(rel, gate.IndexedContentFound, json, allowStaleSafe: false) with
                    { StaleWaitPerformed = staleWaitPerformed };
                }
            }
        }

        // Recovery converged the index, but this plan's identifier/def-token sites were read from the
        // PRE-recovery index — if the drift moved them (e.g. lines prepended above), splicing the stale sites
        // would corrupt the file silently. Re-run resolve → site read → plan once against the converged index
        // (the retry gates every file again, known-fresh, and never re-enters recovery). If the symbol no
        // longer resolves the retry returns the existing clean not-found error rather than applying stale sites.
        if (anyRecovered)
        {
            EditResult retry = ExecuteRename(request, json, allowRecovery: false);
            return retry with
            {
                StaleWaitPerformed = staleWaitPerformed || retry.StaleWaitPerformed,
            };
        }

        var applyResult = _applier.Apply(plan.PlannedEdits);
        if (!applyResult.Success)
        {
            if (applyResult.PartiallyApplied)
                return PartialApply(applyResult, json, !anyStale);
            // The gate has been evaluated for every touched file (anyStale), so report the freshness verdict on
            // failure too — matching the single-file apply-failure path (FinishSingleFile) so telemetry's
            // IndexFresh is populated whenever the gate ran.
            return Error(
                applyResult.Message,
                json,
                !anyStale,
                FailureApplyFailed);
        }

        _writeThrough.Converge(plan.PlannedEdits.Select(p => p.FilePath).ToArray());

        string appliedSummary = summary + "\n" + diff;
        string postApplyHint = NextStepHint.Render(
            $"impact target=\"{target.SymbolId}\"",
            "verify the rename, then run the selected tests");
        return Applied(appliedSummary, staleAllowed: anyStale, filesWritten: applyResult.FilesWritten,
            indexFresh: !anyStale, json, resultCountOverride: plan.TotalSites,
            renameEvidence: renameEvidence, postApplyHint: postApplyHint) with
        { StaleWaitPerformed = staleWaitPerformed };
    }

    private List<RenameFileInput> BuildRenameFiles(
        string oldName,
        IReadOnlyList<IdentifierSite> sites,
        IndexedSymbol target,
        SymbolEditSpan? span,
        out IdentifierSite? definitionSite,
        out IdentifierSite? invalidSite)
    {
        var byFile = new Dictionary<string, List<RenameSite>>(StringComparer.Ordinal);
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        var utf8Content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var absolutePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] oldNameBytes = Encoding.UTF8.GetBytes(oldName);
        invalidSite = null;
        string Absolute(string path)
        {
            if (!absolutePaths.TryGetValue(path, out string? absolute))
                absolutePaths[path] = absolute = ToAbsolute(path);
            return absolute;
        }

        foreach (var s in sites)
        {
            string abs = Absolute(s.FilePath);
            if (!File.Exists(abs))
                continue;
            if (!content.ContainsKey(s.FilePath))
            {
                content[s.FilePath] = ReadDisk(abs);
                utf8Content[s.FilePath] = Encoding.UTF8.GetBytes(content[s.FilePath]);
            }
            byte[] fileBytes = utf8Content[s.FilePath];
            if (s.StartByte < 0 ||
                s.EndByte < s.StartByte ||
                s.EndByte > fileBytes.Length ||
                !fileBytes.AsSpan(s.StartByte, s.EndByte - s.StartByte).SequenceEqual(oldNameBytes))
            {
                // A span whose disk bytes are not the old identifier must never enter the plan: the splicer
                // replaces whatever sits in range, so carrying it forward would rewrite an unrelated token of
                // the same length. Callers refuse on invalidSite; excluding it here means even a control-flow
                // path that reached the splicer could not corrupt the file.
                invalidSite ??= s;
                continue;
            }
            if (!byFile.TryGetValue(s.FilePath, out var list))
                byFile[s.FilePath] = list = [];
            list.Add(new RenameSite(s.StartByte, s.EndByte, s.StartLine, IsDefinition: false));
        }

        definitionSite = AddDefinitionSite(oldName, target, span, byFile, content, Absolute);

        var result = new List<RenameFileInput>(byFile.Count);
        foreach (var (path, list) in byFile)
        {
            var deduped = list
                .GroupBy(r => (r.StartByte, r.EndByte))
                .Select(g => g.OrderByDescending(r => r.IsDefinition).First())
                .OrderBy(r => r.StartByte)
                .ToArray();
            result.Add(new RenameFileInput(Absolute(path), content[path], deduped));
        }
        return result;
    }

    private IdentifierSite? AddDefinitionSite(
        string oldName, IndexedSymbol target, SymbolEditSpan? span,
        Dictionary<string, List<RenameSite>> byFile,
        Dictionary<string, string> content,
        Func<string, string> absolute)
    {
        if (span is null)
            return null;

        string defRel = target.FilePath;
        string defAbs = absolute(defRel);
        if (!File.Exists(defAbs))
            return null;

        if (!content.TryGetValue(defRel, out var fileText))
            content[defRel] = fileText = ReadDisk(defAbs);

        int signatureEnd = span.BodyStartByte ?? span.EndByte;
        int? nameByteStart = FindNameTokenByteOffset(fileText, span.StartByte, signatureEnd, oldName);
        if (nameByteStart is not { } start)
            return null;

        int end = start + Encoding.UTF8.GetByteCount(oldName);
        var defSite = new RenameSite(start, end, span.StartLine, IsDefinition: true);
        if (!byFile.TryGetValue(defRel, out var list))
            byFile[defRel] = list = [];
        list.Add(defSite);
        return new IdentifierSite(defRel, start, end, span.StartLine);
    }

    private static int? FindNameTokenByteOffset(string content, int windowStartByte, int windowEndByte, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (windowStartByte < 0 || windowEndByte > bytes.Length || windowEndByte <= windowStartByte)
            return null;

        byte[] needle = Encoding.UTF8.GetBytes(name);
        if (needle.Length == 0 || needle.Length > windowEndByte - windowStartByte)
            return null;

        (int Offset, int Score)? best = null;
        for (int i = windowStartByte; i + needle.Length <= windowEndByte; i++)
        {
            if (!MatchesAt(bytes, i, needle))
                continue;

            bool leftOk = i == windowStartByte || !IsIdentifierByte(bytes[i - 1]);
            int after = i + needle.Length;
            bool rightOk = after >= bytes.Length || !IsIdentifierByte(bytes[after]);
            if (!leftOk || !rightOk)
                continue;

            while (after < windowEndByte && bytes[after] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                after++;

            int score = after >= windowEndByte
                ? 2
                : bytes[after] switch
                {
                    (byte)'(' or (byte)'{' or (byte)'=' or (byte)';' or (byte)':'
                        or (byte)',' or (byte)')' or (byte)']' => 3,
                    (byte)'<' => 1,
                    _ => 0,
                };
            if (best is null || score > best.Value.Score)
                best = (i, score);
        }

        return best?.Offset;
    }

    private static bool MatchesAt(byte[] haystack, int at, byte[] needle)
    {
        for (int k = 0; k < needle.Length; k++)
            if (haystack[at + k] != needle[k])
                return false;
        return true;
    }

    private static bool IsIdentifierByte(byte b) =>
        b == (byte)'_' ||
        (b >= (byte)'0' && b <= (byte)'9') ||
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'A' && b <= (byte)'Z') ||
        b >= 0x80; // a UTF-8 continuation/lead byte is part of a (possibly non-ASCII) identifier char

    // ---------- rendering ----------

    private EditResult Preview(
        string diff,
        bool json,
        string? renameSummary,
        int siteCount,
        EditMatchEvidence? evidence = null,
        RenameEvidenceSummary? renameEvidence = null)
    {
        diff = BoundDiff(diff);
        if (renameSummary is not null)
        {
            diff = ToolOutputBudget.TruncateUtf8(
                diff,
                RenameDiffMaxBytes,
                "\n… rename diff truncated; inspect the working tree for the complete preview.");
            renameSummary = ToolOutputBudget.TruncateUtf8(
                renameSummary,
                RenameSummaryMaxBytes,
                "\n… rename summary truncated; inspect the diff for retained files.");
        }

        if (diff.Length == 0 && renameSummary is null)
        {
            if (json)
            {
                string body = JsonObject(w =>
                {
                    w.WriteBoolean("applied", false);
                    w.WriteString("diff", "");
                    w.WriteString("note", "no change");
                    WriteEvidenceJson(w, evidence);
                });
                return new EditResult(body, Applied: false, StaleAllowed: false, IndexFresh: null, Outcome: "empty", ResultCount: 0);
            }

            if (evidence is null)
            {
                return new EditResult(
                    "No change — the edit is a no-op.",
                    Applied: false, StaleAllowed: false, IndexFresh: null, Outcome: "empty", ResultCount: 0);
            }

            var noChange = new StringBuilder();
            noChange.Append("No change — the edit is a no-op.\n");
            AppendEvidence(noChange, evidence);
            return new EditResult(
                noChange.ToString().TrimEnd('\n'),
                Applied: false, StaleAllowed: false, IndexFresh: null, Outcome: "empty", ResultCount: 0);
        }

        if (json)
        {
            string body = JsonObject(w =>
            {
                w.WriteBoolean("applied", false);
                w.WriteString("mode", "preview");
                w.WriteString("diff", diff);
                if (renameSummary is not null)
                {
                    w.WriteString("rename_summary", renameSummary);
                    w.WriteNumber("sites", siteCount);
                }
                WriteRenameEvidenceJson(w, renameEvidence);
                WriteEvidenceJson(w, evidence);
            });
            return new EditResult(body, false, false, null, "ok", renameSummary is null ? 1 : siteCount);
        }

        var sb = new StringBuilder();
        sb.Append("Preview — pass apply=true to commit.\n");
        AppendEvidence(sb, evidence);
        if (renameSummary is not null)
            sb.Append(renameSummary).Append('\n');
        sb.Append(diff);
        return new EditResult(sb.ToString().TrimEnd('\n'), false, false, null, "ok",
            renameSummary is null ? 1 : siteCount);
    }

    private void WriteRenameEvidenceJson(Utf8JsonWriter writer, RenameEvidenceSummary? evidence)
    {
        if (evidence is null)
            return;

        writer.WritePropertyName("rename_evidence");
        writer.WriteStartObject();
        writer.WriteString("mode", evidence.Mode);
        writer.WriteString("target_symbol_id", evidence.Target.SymbolId);
        writer.WritePropertyName("exact_sites");
        writer.WriteStartArray();
        WriteRenameSiteJson(
            writer,
            evidence.Target.FilePath,
            evidence.Target.StartLine,
            "definition",
            "exact");
        foreach (IdentifierSite site in evidence.ExactSites.Take(MaxRenameEvidenceSitesPerTier - 1))
            WriteRenameSiteJson(writer, site.FilePath, site.StartLine, "reference", "exact");
        writer.WriteEndArray();
        int exactSiteTotal = 1 + evidence.ExactSites.Count;
        int exactSiteReturned = Math.Min(exactSiteTotal, MaxRenameEvidenceSitesPerTier);
        writer.WriteNumber("exact_sites_total_count", exactSiteTotal);
        writer.WriteNumber("exact_sites_returned_count", exactSiteReturned);
        writer.WriteNumber("exact_sites_omitted_count", exactSiteTotal - exactSiteReturned);
        writer.WritePropertyName("fallback_sites");
        writer.WriteStartArray();
        foreach (IdentifierSite site in evidence.FallbackSites.Take(MaxRenameEvidenceSitesPerTier))
            WriteRenameSiteJson(writer, site.FilePath, site.StartLine, "name_based", "fallback");
        writer.WriteEndArray();
        int fallbackSiteReturned = Math.Min(evidence.FallbackSites.Count, MaxRenameEvidenceSitesPerTier);
        writer.WriteNumber("fallback_sites_total_count", evidence.FallbackSites.Count);
        writer.WriteNumber("fallback_sites_returned_count", fallbackSiteReturned);
        writer.WriteNumber(
            "fallback_sites_omitted_count",
            evidence.FallbackSites.Count - fallbackSiteReturned);
        writer.WritePropertyName("coverage");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("language", evidence.Target.Language);
        writer.WriteString("kind", "definition");
        writer.WriteString("resolution_status", "exact");
        writer.WriteNumber("count", 1);
        writer.WriteEndObject();
        var exactCoverage = evidence.ExactEvidence
            .GroupBy(reference => (
                Language: reference.Language ?? evidence.Target.Language,
                Kind: reference.SourceKind))
            .OrderBy(group => group.Key.Language, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ToArray();
        int fallbackCoverageRows = evidence.FallbackSites.Count > 0 ? 1 : 0;
        int exactCoverageLimit = Math.Max(0, MaxRenameCoverageRows - 1 - fallbackCoverageRows);
        foreach (var group in exactCoverage.Take(exactCoverageLimit))
        {
            writer.WriteStartObject();
            writer.WriteString("language", group.Key.Language);
            writer.WriteString("kind", group.Key.Kind);
            writer.WriteString("resolution_status", "exact");
            writer.WriteNumber("count", group.Count());
            writer.WriteNumber("inferred_count", group.Count(IsInferredBinding));
            writer.WriteNumber("min_confidence", group.Min(static reference => reference.Confidence));
            writer.WriteEndObject();
        }
        if (evidence.FallbackSites.Count > 0)
        {
            writer.WriteStartObject();
            writer.WriteString("language", "unknown");
            writer.WriteString("kind", "name_based");
            writer.WriteString("resolution_status", "fallback");
            writer.WriteNumber("count", evidence.FallbackSites.Count);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        int coverageTotal = 1 + exactCoverage.Length + fallbackCoverageRows;
        writer.WriteNumber("coverage_total_count", coverageTotal);
        writer.WriteNumber("coverage_omitted_count", Math.Max(0, coverageTotal - MaxRenameCoverageRows));
        writer.WriteNumber("fallback_candidates", evidence.Coverage.FallbackAvailable);
        writer.WriteString("fallback_status", evidence.Coverage.FallbackStatus.ToString());
        writer.WriteNumber("inferred_exact_count", evidence.ExactEvidence.Count(IsInferredBinding));
        writer.WriteEndObject();
    }

    private void WriteRenameSiteJson(
        Utf8JsonWriter writer,
        string filePath,
        int line,
        string source,
        string resolutionStatus)
    {
        writer.WriteStartObject();
        writer.WriteString(
            "file",
            ToolOutputBudget.TruncateUtf8(
                ToRelative(ToAbsolute(filePath)),
                MaxRenameEvidencePathBytes,
                "…"));
        writer.WriteNumber("line", line);
        writer.WriteString("source", source);
        writer.WriteString("resolution_status", resolutionStatus);
        writer.WriteEndObject();
    }

    private EditResult Applied(
        string diffOrSummary, bool staleAllowed, int filesWritten, bool indexFresh, bool json,
        int? resultCountOverride = null,
        EditMatchEvidence? evidence = null,
        RenameEvidenceSummary? renameEvidence = null,
        string? postApplyHint = null)
    {
        int outputBudget = renameEvidence is null ? ToolOutputBudget.EditDiffMaxBytes : RenameDiffMaxBytes;
        diffOrSummary = ToolOutputBudget.TruncateUtf8(
            diffOrSummary,
            outputBudget,
            "\n… diff preview truncated; inspect the working tree for the complete applied change.");
        int count = resultCountOverride ?? filesWritten;
        if (json)
        {
            string body = JsonObject(w =>
            {
                w.WriteBoolean("applied", true);
                w.WriteNumber("files_written", filesWritten);
                w.WriteBoolean("stale_allowed", staleAllowed);
                w.WriteBoolean("index_fresh", indexFresh);
                w.WriteString("diff", diffOrSummary);
                WriteRenameEvidenceJson(w, renameEvidence);
                if (postApplyHint is not null)
                    w.WriteString("post_apply_hint", postApplyHint);
                WriteEvidenceJson(w, evidence);
            });
            return new EditResult(body, true, staleAllowed, indexFresh, "ok", count);
        }

        var sb = new StringBuilder();
        sb.Append("Applied — ").Append(filesWritten).Append(filesWritten == 1 ? " file written." : " files written.");
        if (staleAllowed)
            sb.Append(" (stale_allowed: the index was behind disk; edited anyway)");
        sb.Append('\n').Append(diffOrSummary);
        if (evidence is not null)
        {
            sb.Append('\n');
            AppendEvidence(sb, evidence);
        }
        if (postApplyHint is not null)
            sb.Append('\n').Append(postApplyHint);
        return new EditResult(sb.ToString().TrimEnd('\n'), true, staleAllowed, indexFresh, "ok", count);
    }

    private EditResult PartialApply(EditApplier.ApplyResult applyResult, bool json, bool indexFresh)
    {
        IReadOnlyList<string> absolutePaths = applyResult.FilesLeftModified ?? [];
        string? convergeFailure = null;
        try
        {
            _writeThrough.Converge(absolutePaths);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or SqliteException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            convergeFailure = ex.Message;
        }

        string[] relativePaths = absolutePaths
            .Select(ToRelative)
            .Select(static path => ToolOutputBudget.TruncateUtf8(path, MaxRenameEvidencePathBytes, "…"))
            .ToArray();
        string[] renderedPaths = relativePaths.Take(MaxPartialApplyPaths).ToArray();
        int omittedPaths = Math.Max(0, relativePaths.Length - renderedPaths.Length);
        string message = convergeFailure is null
            ? applyResult.Message
            : $"{applyResult.Message} Index convergence also failed: {convergeFailure}";
        EditResult result;
        if (json)
        {
            result = new EditResult(JsonObject(writer =>
            {
                writer.WriteBoolean("applied", false);
                writer.WriteBoolean("partially_applied", true);
                writer.WriteNumber("files_left_modified_count", relativePaths.Length);
                writer.WriteStartArray("files_left_modified");
                foreach (string path in renderedPaths)
                    writer.WriteStringValue(path);
                writer.WriteEndArray();
                writer.WriteNumber("files_left_modified_omitted_count", omittedPaths);
                writer.WriteBoolean("index_fresh", indexFresh);
                writer.WriteBoolean("index_converged", convergeFailure is null);
                writer.WriteString("failure_reason", FailurePartialApply);
                writer.WriteString("error", message);
            }), false, false, indexFresh, "error", relativePaths.Length, FailurePartialApply);
        }
        else
        {
            result = new EditResult(
                $"edit: partial apply — {relativePaths.Length} file(s) remain modified: " +
                $"{string.Join(", ", renderedPaths)}" +
                $"{(omittedPaths > 0 ? $", … {omittedPaths} more" : string.Empty)}. {message}",
                false,
                false,
                indexFresh,
                "error",
                relativePaths.Length,
                FailurePartialApply);
        }

        return result with
        {
            PartiallyApplied = true,
            FilesLeftModified = renderedPaths,
            FilesLeftModifiedTotalCount = relativePaths.Length,
            FilesLeftModifiedOmittedCount = omittedPaths,
        };
    }

    private static string BoundDiff(string diff) =>
        ToolOutputBudget.TruncateUtf8(
            diff,
            ToolOutputBudget.EditDiffMaxBytes,
            "\n… diff preview truncated; narrow the edit target for a smaller proof.");

    private static void AppendEvidence(StringBuilder sb, EditMatchEvidence? evidence)
    {
        if (evidence is null)
            return;

        sb.Append("match: ").Append(evidence.MatchMode).Append(" ×").Append(evidence.MatchCount);
        if (evidence.LineStart is { } lineStart && evidence.LineEnd is { } lineEnd)
            sb.Append(" @ L").Append(lineStart).Append('-').Append(lineEnd);
        sb.Append(evidence.DiskVerified ? " (disk verified" : " (DISK UNVERIFIED");
        sb.Append(", index ").Append(evidence.ContentIndexState).Append(")\n");

        AppendEvidenceNotes(sb, evidence);
    }

    private static void AppendEvidenceNotes(StringBuilder sb, EditMatchEvidence evidence)
    {
        var notes = new List<string>(3);
        if (evidence.Occurrence == "all" && evidence.SelectedMatchCount < evidence.MatchCount)
        {
            int skipped = evidence.MatchCount - evidence.SelectedMatchCount;
            notes.Add(
                $"occurrence=all selected {evidence.SelectedMatchCount} of {evidence.MatchCount} " +
                $"non-overlapping matches; {skipped} overlapping candidate(s) skipped");
        }
        else if (evidence.MatchCount > 1)
            notes.Add($"occurrence={evidence.Occurrence} of {evidence.MatchCount} matches");
        if (!evidence.DiskVerified)
            notes.Add("old_text did not verify against current disk text");
        if (!string.IsNullOrWhiteSpace(evidence.CandidateReason))
            notes.Add(evidence.CandidateReason);
        if (evidence.SelectedSiteDistances.Count > 0)
        {
            notes.Add("fuzzy sites " + string.Join(", ", evidence.SelectedSiteDistances
                .Select(static site =>
                    FormattableString.Invariant($"L{site.Line}~{site.Distance}"))));
        }

        if (notes.Count == 0)
            return;

        sb.Append("match note: ").Append(string.Join("; ", notes)).Append('\n');
    }

    private static void WriteEvidenceJson(Utf8JsonWriter w, EditMatchEvidence? evidence)
    {
        if (evidence is null)
            return;

        w.WriteString("match_mode", evidence.MatchMode);
        w.WriteString("match_source", evidence.MatchSource);
        if (evidence.LineStart is { } lineStart)
            w.WriteNumber("line_start", lineStart);
        if (evidence.LineEnd is { } lineEnd)
            w.WriteNumber("line_end", lineEnd);
        w.WriteNumber("match_count", evidence.MatchCount);
        w.WriteNumber("selected_match_count", evidence.SelectedMatchCount);
        w.WriteString("occurrence", evidence.Occurrence);
        w.WriteBoolean("disk_verified", evidence.DiskVerified);
        w.WriteString("content_index_state", evidence.ContentIndexState);
        if (!string.IsNullOrWhiteSpace(evidence.CandidateReason))
            w.WriteString("content_index_note", evidence.CandidateReason);
        if (evidence.SelectedSiteDistances.Count > 0)
        {
            w.WriteStartArray("fuzzy_sites");
            foreach ((int line, int distance) in evidence.SelectedSiteDistances)
            {
                w.WriteStartObject();
                w.WriteNumber("line", line);
                w.WriteNumber("distance", distance);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }

    private static EditResult StaleBlocked(
        string relativePath,
        bool indexedContentFound,
        bool json,
        bool allowStaleSafe)
    {
        string recovery = allowStaleSafe
            ? "run a workspace refresh, or pass allow_stale for replace_text only"
            : "run a workspace refresh and retry";
        string reason = indexedContentFound
            ? $"index stale for {relativePath} — {recovery}."
            : $"no indexed snapshot for {relativePath} — {recovery}.";
        if (json)
            return new EditResult(JsonObject(w =>
            {
                w.WriteBoolean("applied", false);
                w.WriteBoolean("index_fresh", false);
                w.WriteString("error", reason);
            }), false, false, false, "error", 0, FailureStaleTarget);
        return new EditResult(reason, false, false, IndexFresh: false, "error", 0, FailureStaleTarget);
    }

    /// <summary>
    /// Gate-time self-heal (the fix for "index stale for a just-edited file"): ask the write-through to
    /// converge the ONE stale file now, then re-check the gate — once, immediately, after a synchronous
    /// (leader inline) reindex, or by polling within the shared per-call <paramref name="budget"/> after an
    /// asynchronous (reader → leader request) converge. Returns true when the gate verdict turned Fresh; false
    /// when recovery is unavailable (<see cref="StaleRecoveryAttempt.None"/>), did not land, or the budget ran
    /// out — the caller then refuses exactly as it did before this seam existed. Time spent is deducted from
    /// <paramref name="budget"/> so a multi-file gate loop cannot stack per-file waits.
    /// </summary>
    private bool TryRecoverFreshness(
        string relativePath,
        string absPath,
        string diskText,
        ref TimeSpan budget,
        out bool waited)
    {
        StaleRecoveryAttempt attempt = _writeThrough.TryRecoverStaleFile(absPath);
        waited = attempt != StaleRecoveryAttempt.None;
        if (attempt == StaleRecoveryAttempt.None)
            return false;

        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                bool fresh;
                try
                {
                    fresh = FreshnessGate.Check(_dbPath, relativePath, absPath, diskText).Result
                        == FreshnessResult.Fresh;
                }
                catch (Exception ex) when (
                    ex is SqliteException or FileNotFoundException or InvalidOperationException)
                {
                    // A converge in flight can transiently break the gate read (the DB mid-swap/locked, the WAL
                    // dir probe failing). Execute promises to never throw for an expected condition, so a
                    // transient read failure reads as "not yet fresh": keep polling within the budget and let
                    // the timeout refuse cleanly if it never settles.
                    fresh = false;
                }
                if (fresh)
                    return true;
                if (attempt == StaleRecoveryAttempt.Converged || elapsed.Elapsed >= budget)
                    return false;
                Thread.Sleep(_recovery.PollInterval);
            }
        }
        finally
        {
            budget -= elapsed.Elapsed;
            if (budget < TimeSpan.Zero)
                budget = TimeSpan.Zero;
        }
    }

    private sealed record RenameEvidenceSummary(
        string Mode,
        IndexedSymbol Target,
        IReadOnlyList<IdentifierSite> ExactSites,
        IReadOnlyList<IdentifierSite> FallbackSites,
        ReferenceEvidenceCoverage Coverage,
        IReadOnlyList<ReferenceEvidence> ExactEvidence);

    /// <summary>
    /// Whether a reference's target was proved by scope rather than inferred by a heuristic tier. Tier 3 binds a
    /// receiver it can corroborate but no recorded type fact backs (julie's <c>tier3_receiver</c> at 0.65,
    /// <c>tier3_static_type</c> at 0.70); tier 4 binds on global name uniqueness alone at 0.55. Both are real
    /// references worth renaming, but a rename WRITES, so the preview must not render them as indistinguishable
    /// from a scope-proved binding. A null tier is a direct extractor target or a relationship edge — proved.
    /// </summary>
    private static bool IsInferredBinding(ReferenceEvidence reference) =>
        reference.ResolutionTier is >= 3;

    private static IReadOnlyList<IdentifierSite> RenameIdentifierSites(
        IReadOnlyList<ReferenceEvidence> evidence,
        int nameByteLength)
    {
        var sites = new List<IdentifierSite>(evidence.Count);
        foreach (ReferenceEvidence reference in evidence)
        {
            if (!HasUsableRenameSpan(reference, nameByteLength))
                continue;

            sites.Add(new IdentifierSite(
                reference.FilePath,
                (int)reference.StartByte!.Value,
                (int)reference.EndByte!.Value,
                reference.StartLine!.Value));
        }

        return sites
            .GroupBy(static site => (site.FilePath, site.StartByte, site.EndByte))
            .Select(static group => group.First())
            .OrderBy(static site => site.FilePath, StringComparer.Ordinal)
            .ThenBy(static site => site.StartByte)
            .ToArray();
    }

    /// <summary>
    /// Counts exact-arm references the rename cannot reach. A schema-5 spanless site carries no span by
    /// design and duplicates identifier evidence for the same occurrence, so it only signals a real gap when
    /// no usable identifier site covers the same file, containing symbol, and target.
    /// </summary>
    private static int CountUnreachableExactSites(
        IReadOnlyList<ReferenceEvidence> exact,
        int nameByteLength)
    {
        HashSet<(string, string?, string?)> covered = exact
            .Where(reference => HasUsableRenameSpan(reference, nameByteLength))
            .Select(reference => (reference.FilePath, reference.ContainingSymbolId, reference.TargetSymbolId))
            .ToHashSet();

        return exact.Count(reference =>
            !HasUsableRenameSpan(reference, nameByteLength) &&
            !(!reference.IsExact &&
              covered.Contains((reference.FilePath, reference.ContainingSymbolId, reference.TargetSymbolId))));
    }

    private static bool HasUsableRenameSpan(ReferenceEvidence reference, int nameByteLength) =>
        reference.Source is ReferenceEvidenceSource.IdentifierDirect or
            ReferenceEvidenceSource.IdentifierResolution &&
        reference.StartByte is { } startByte &&
        reference.EndByte is { } endByte &&
        reference.StartLine is not null &&
        startByte >= 0 &&
        endByte > startByte &&
        endByte - startByte == nameByteLength &&
        startByte <= int.MaxValue &&
        endByte <= int.MaxValue;

    private string RenderRenameSummary(
        string oldName,
        string newName,
        RenamePlan plan,
        RenameEvidenceSummary evidence)
    {
        var sb = new StringBuilder();
        sb.Append("rename '").Append(oldName).Append("' → '").Append(newName).Append("': ")
          .Append(plan.TotalSites).Append(plan.TotalSites == 1 ? " site across " : " sites across ")
          .Append(plan.Summary.Count).Append(plan.Summary.Count == 1 ? " file" : " files")
          .Append("  mode=").Append(evidence.Mode).Append('\n');
        sb.Append("exact sites:\n");
        sb.Append("  ").Append(evidence.Target.FilePath).Append(':').Append(evidence.Target.StartLine)
            .Append("  definition\n");
        AppendRenameSites(sb, evidence.ExactSites);
        if (evidence.FallbackSites.Count > 0)
        {
            sb.Append("fallback sites (name-based, may include homonyms):\n");
            AppendRenameSites(sb, evidence.FallbackSites);
        }
        sb.Append("coverage:\n");
        sb.Append("  ").Append(evidence.Target.Language).Append("/definition=1\n");
        foreach (var group in evidence.ExactEvidence
                     .GroupBy(reference => (
                         Language: reference.Language ?? evidence.Target.Language,
                         Kind: reference.SourceKind))
                     .OrderBy(group => group.Key.Language, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.Kind, StringComparer.Ordinal))
        {
            int inferred = group.Count(IsInferredBinding);
            sb.Append("  exact ").Append(group.Key.Language).Append('/').Append(group.Key.Kind)
                .Append('=').Append(group.Count());
            if (inferred > 0)
            {
                sb.Append(" (").Append(inferred)
                  .Append(" inferred, min confidence ")
                  .Append(FormattableString.Invariant(
                      $"{group.Min(static reference => reference.Confidence):0.00}"))
                  .Append(')');
            }
            sb.Append('\n');
        }
        int inferredTotal = evidence.ExactEvidence.Count(IsInferredBinding);
        if (inferredTotal > 0)
        {
            sb.Append("  note: ").Append(inferredTotal).Append(" of ").Append(evidence.ExactEvidence.Count)
              .Append(" exact site(s) are inferred bindings (receiver or global-name tiers), not scope-proved; ")
              .Append("review those lines in the diff before apply=true.\n");
        }
        if (evidence.FallbackSites.Count > 0)
            sb.Append("  fallback unknown/name_based=").Append(evidence.FallbackSites.Count).Append('\n');
        sb.Append("  fallback_status=").Append(evidence.Coverage.FallbackStatus)
            .Append(" candidates=").Append(evidence.Coverage.FallbackAvailable).Append('\n');
        sb.Append("files:\n");
        foreach (var f in plan.Summary)
            sb.Append("  ").Append(ToRelative(f.FilePath)).Append("  (")
              .Append(f.SiteCount).Append(f.SiteCount == 1 ? " site)" : " sites)").Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendRenameSites(StringBuilder sb, IReadOnlyList<IdentifierSite> sites)
    {
        foreach (var group in sites
                     .GroupBy(static site => site.FilePath, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            sb.Append("  ").Append(group.Key).Append(':')
                .Append(string.Join(',', group.Select(static site => site.StartLine)))
                .Append('\n');
        }
    }

    private string RenderRenameDiff(RenamePlan plan)
    {
        var sb = new StringBuilder();
        foreach (var pe in plan.PlannedEdits)
        {
            string d = UnifiedDiff.Render(pe.OldContent, pe.NewContent, ToRelative(pe.FilePath));
            if (d.Length > 0)
                sb.Append(d);
        }
        return sb.ToString().TrimEnd('\n');
    }

    private EditResult Candidates(IReadOnlyList<IndexedSymbol> matches, bool json)
    {
        if (json)
            return new EditResult(JsonObject(w =>
            {
                w.WritePropertyName("candidates");
                w.WriteStartArray();
                foreach (var s in matches)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteString("kind", s.Kind);
                    w.WriteString("file", s.FilePath);
                    w.WriteNumber("line", s.StartLine);
                    w.WriteString("symbol_id", s.SymbolId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }), false, false, null, "empty", matches.Count, FailureAmbiguousMatch);

        var sb = new StringBuilder();
        sb.Append("Ambiguous target — multiple candidates; pass scope=<file> to disambiguate:\n");
        foreach (var s in matches)
            sb.Append("  ").Append(s.Name).Append("  ").Append(s.Kind).Append("  ")
              .Append(s.FilePath).Append(':').Append(s.StartLine).Append('\n');
        return new EditResult(sb.ToString().TrimEnd('\n'), false, false, null, "empty", matches.Count,
            FailureAmbiguousMatch);
    }

    private static EditResult NotFound(string target, bool json)
    {
        string msg = $"'{target}' not found. Use search/inspect to locate it.";
        return json
            ? new EditResult($"{{\"applied\":false,\"not_found\":{ServerJson.String(target)}}}",
                false, false, null, "empty", 0, FailureTargetNotFound)
            : new EditResult(msg, false, false, null, "empty", 0, FailureTargetNotFound);
    }

    private static EditResult Error(
        string message, bool json, bool? indexFresh = null, string failureReason = FailureUnknown)
    {
        if (json)
            return new EditResult(JsonObject(w =>
            {
                w.WriteBoolean("applied", false);
                w.WriteString("error", message);
                if (indexFresh is { } f) w.WriteBoolean("index_fresh", f);
            }), false, false, indexFresh, "error", 0, failureReason);
        return new EditResult($"edit: {message}", false, false, indexFresh, "error", 0, failureReason);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Resolve a symbol target, accepting both a bare name and a qualified <c>Parent.Member</c> path (the
    /// documented 80% edit call, e.g. <c>OrderService.Total</c>). The bare <see cref="SmartTargetResolver"/> is
    /// tried first; if the target carries a <c>.</c> and the bare lookup was not a unique symbol, the LAST dotted
    /// segment is treated as the member name and the preceding segment as the enclosing-symbol name, and the
    /// member is filtered to those whose containment parent matches — disambiguating a homonym without modifying
    /// the shared M2 resolver. A qualified lookup yielding one symbol wins; several → candidates; none → the
    /// original (bare) resolution result.
    /// </summary>
    private TargetResolution ResolveSymbol(string target, string? scope)
    {
        var bare = _resolver.Resolve(target, scope);
        if (bare is TargetResolution.Symbol)
            return bare;

        int lastDot = target.LastIndexOf('.');
        if (lastDot <= 0 || lastDot >= target.Length - 1)
            return bare; // not a Parent.Member shape

        string parentName = target[..lastDot];
        string memberName = target[(lastDot + 1)..];

        // Candidate members named memberName whose enclosing-symbol name equals parentName (the last segment of
        // a dotted parent path, so A.B.C matches a member C inside a symbol named B).
        string expectedParent = parentName.Contains('.')
            ? parentName[(parentName.LastIndexOf('.') + 1)..]
            : parentName;

        var members = _index.FindByName(memberName)
            .Where(s => s.ParentId is { } pid
                        && _index.FindBySymbolId(pid) is { } parent
                        && string.Equals(parent.Name, expectedParent, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(scope))
            members = members.Where(s => string.Equals(s.FilePath, scope, StringComparison.Ordinal));

        var qualified = members.ToList();
        return qualified.Count switch
        {
            0 => bare,                                          // no qualified match — keep the bare verdict
            1 => new TargetResolution.Symbol(qualified[0]),
            _ => new TargetResolution.Candidates(qualified),
        };
    }

    private static bool IsApply(EditRequest request) => request.Apply;

    private static IReadOnlyList<TextEdit> FillReplacement(IReadOnlyList<TextEdit> edits, string? replacement)
    {
        string r = replacement ?? string.Empty;
        var filled = new TextEdit[edits.Count];
        for (int i = 0; i < edits.Count; i++)
            filled[i] = edits[i] with { Replacement = r };
        return filled;
    }

    private string ToAbsolute(string relativeOrAbsolute)
    {
        string lexical = Path.IsPathRooted(relativeOrAbsolute)
            ? Path.GetFullPath(relativeOrAbsolute)
            : Path.GetFullPath(relativeOrAbsolute, _workspaceRoot);
        string canonical;
        try
        {
            canonical = PathCanonicalizer.CanonicalizeFile(_canonicalWorkspaceRoot, lexical);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidEditTargetPathException(
                $"edit target '{relativeOrAbsolute}' could not be resolved safely inside the workspace root.",
                ex);
        }

        string rootWithSeparator = _canonicalWorkspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _canonicalWorkspaceRoot
            : _canonicalWorkspaceRoot + Path.DirectorySeparatorChar;
        StringComparison comparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!canonical.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidEditTargetPathException(
                $"edit target '{relativeOrAbsolute}' resolves outside the workspace root.");
        }

        string relative = Path.GetRelativePath(_workspaceRoot, lexical);
        string expectedCanonical = Path.GetFullPath(relative, _canonicalWorkspaceRoot);
        if (!string.Equals(canonical, expectedCanonical, comparison))
        {
            throw new InvalidEditTargetPathException(
                $"edit target '{relativeOrAbsolute}' resolves through a symbolic link; edit the real workspace path instead.");
        }

        return lexical;
    }

    private sealed class InvalidEditTargetPathException : Exception
    {
        public InvalidEditTargetPathException(string message)
            : base(message)
        {
        }

        public InvalidEditTargetPathException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    // Map an absolute path back to the workspace-relative path julie keyed the index/freshness snapshot under
    // (forward-slashed, matching julie's stored file_path). Falls back to the absolute path if it is outside
    // the root (which should not happen for a resolved target).
    private string ToRelative(string absolutePath)
    {
        string rel = Path.GetRelativePath(_workspaceRoot, absolutePath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ReadDisk(string absPath) => File.ReadAllText(absPath, Encoding.UTF8);

    private string EditPlanFailureMessage(
        EditError error, EditOperation op, string relativePath, string? oldText, out string failureReason)
    {
        failureReason = FailureReasonFor(error.Kind);
        if (op != EditOperation.ReplaceText ||
            error.Kind != EditErrorKind.TextNotFound ||
            string.IsNullOrEmpty(oldText))
        {
            return error.Message;
        }

        IndexedSourceTextMatch? match = _indexedSourceTextReader.FindLiteral(_dbPath, relativePath, oldText);
        if (match is null)
            return error.Message;

        failureReason = FailureStaleTarget;
        return $"old_text not found in current file: \"{oldText}\". The indexed source still contains it " +
               $"near line {match.Line}, so the file likely changed after the index snapshot. Wait for the " +
               "watcher or run workspace refresh, then retry with the current text.";
    }

    // ---- operation / occurrence parsing ----

    private static readonly string[] OperationNames =
    [
        "replace_text", "replace_symbol_body", "replace_symbol_signature",
        "rename_symbol", "insert_before", "insert_after", "add_doc",
    ];

    private static bool TryParseOperation(string? op, out EditOperation parsed)
    {
        switch (op?.ToLowerInvariant())
        {
            case "replace_text": parsed = EditOperation.ReplaceText; return true;
            case "replace_symbol_body": parsed = EditOperation.ReplaceSymbolBody; return true;
            case "replace_symbol_signature": parsed = EditOperation.ReplaceSymbolSignature; return true;
            case "rename_symbol": parsed = EditOperation.RenameSymbol; return true;
            case "insert_before": parsed = EditOperation.InsertBefore; return true;
            case "insert_after": parsed = EditOperation.InsertAfter; return true;
            case "add_doc": parsed = EditOperation.AddDoc; return true;
            default: parsed = default; return false;
        }
    }

    private static bool TryParseOccurrence(string? occ, out Occurrence parsed)
    {
        switch (occ?.ToLowerInvariant())
        {
            case null or "" or "first": parsed = Occurrence.First; return true;
            case "last": parsed = Occurrence.Last; return true;
            case "all": parsed = Occurrence.All; return true;
            default: parsed = default; return false;
        }
    }

    private static bool TryParseMatchMode(string? mode, out TextMatchMode parsed)
    {
        switch (mode?.ToLowerInvariant())
        {
            case null or "" or "auto": parsed = TextMatchMode.Auto; return true;
            case "exact": parsed = TextMatchMode.Exact; return true;
            case "normalized": parsed = TextMatchMode.Normalized; return true;
            case "fuzzy": parsed = TextMatchMode.Fuzzy; return true;
            default: parsed = default; return false;
        }
    }

    private static string OccurrenceName(Occurrence occurrence) => occurrence switch
    {
        Occurrence.First => "first",
        Occurrence.Last => "last",
        Occurrence.All => "all",
        _ => "all",
    };

    private static string FailureReasonFor(EditErrorKind kind) => kind switch
    {
        EditErrorKind.TextNotFound => FailureNoMatch,
        EditErrorKind.InvalidSpan => FailureStaleTarget,
        EditErrorKind.BodySpanUnavailable or EditErrorKind.InvalidNewName or EditErrorKind.MissingArgument =>
            FailureInvalidRequest,
        _ => FailureUnknown,
    };

    private sealed record ReplaceTextPlanResult(
        EditPlan? Plan, EditMatchEvidence? Evidence, string? ErrorMessage, string? FailureReason)
    {
        /// <summary>True iff planning spent budget waiting for a single-file converge, however it ended.</summary>
        public bool StaleWaitPerformed { get; init; }

        public static ReplaceTextPlanResult Success(EditPlan plan, EditMatchEvidence evidence) =>
            new(plan, evidence, ErrorMessage: null, FailureReason: null);

        public static ReplaceTextPlanResult Error(string message, string failureReason) =>
            new(Plan: null, Evidence: null, message, failureReason);
    }

    private sealed record EditMatchEvidence(
        string MatchMode,
        string MatchSource,
        string ContentIndexState,
        int? LineStart,
        int? LineEnd,
        int MatchCount,
        int SelectedMatchCount,
        string Occurrence,
        bool DiskVerified,
        string? CandidateReason,
        int? CandidateLineStart,
        int? CandidateLineEnd,
        /// <summary>The selected sites as <c>(line, edit distance)</c>, ascending by line. Only meaningful for
        /// fuzzy matching, where <c>occurrence=all</c> rewrites every site within the distance threshold rather
        /// than only the closest — so the spread has to be visible before an apply, not inferable after one.
        /// </summary>
        IReadOnlyList<(int Line, int Distance)> SelectedSiteDistances);

    private sealed record TextWindow(string Text, int StartByte, int StartLine);

    // ---- JSON helper ----

    private static string JsonObject(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
