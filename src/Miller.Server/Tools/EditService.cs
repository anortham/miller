using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Editing;
using Miller.Core.Freshness;
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
///   NOTHING, and skip the freshness gate (the gate guards the WRITE).</item>
///   <item><c>apply=true</c> → run the freshness gate per touched file (refuse if stale unless
///   <c>allow_stale</c>); then <see cref="EditApplier"/> writes atomically (TOCTOU + rollback); then
///   write-through converges the index (<see cref="IEditWriteThrough"/>).</item>
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
    private const string FailureUnknown = "unknown";

    private readonly MillerRepositoryIndex _index;
    private readonly SmartTargetResolver _resolver;
    private readonly string _dbPath;
    private readonly string _workspaceRoot;
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
        _workspaceRoot = workspaceRoot;
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
        string? FailureReason = null);

    /// <summary>Run the full edit pipeline for <paramref name="request"/>. Never throws for an expected condition.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public EditResult Execute(EditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool json = string.Equals(request.Format, "json", StringComparison.OrdinalIgnoreCase);

        if (!TryParseOperation(request.Operation, out var op))
            return Error($"unknown operation '{request.Operation}'. Valid: {string.Join(", ", OperationNames)}.", json,
                failureReason: FailureInvalidRequest);

        if (!TryParseOccurrence(request.Occurrence, out var occurrence))
            return Error($"unknown occurrence '{request.Occurrence}'. Valid: first, last, all.", json,
                failureReason: FailureInvalidRequest);

        if (op == EditOperation.ReplaceText && !TryParseMatchMode(request.MatchMode, out _))
            return Error($"unknown match_mode '{request.MatchMode}'. Valid: auto, exact, normalized, fuzzy.", json,
                failureReason: FailureInvalidRequest);

        return op == EditOperation.RenameSymbol
            ? ExecuteRename(request, json)
            : ExecuteSingleFile(request, op, occurrence, json);
    }

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
                return Error("unrecognized target resolution.", json);
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
        if (op == EditOperation.ReplaceText)
        {
            if (request.NewText is null)
                return Error("new_text is required for replace_text.", json,
                    failureReason: FailureInvalidRequest);

            var replace = PlanReplaceText(relativePath, content, request, occurrence);
            if (replace.ErrorMessage is not null)
                return Error(replace.ErrorMessage, json,
                    failureReason: replace.FailureReason ?? FailureUnknown);

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
        return FinishSingleFile(request, op, occurrence, relativePath, planned, json, allowRecovery, evidence);
    }

    private EditResult FinishSingleFile(
        EditRequest request, EditOperation op, Occurrence occurrence,
        string relativePath, PlannedEdit planned, bool json, bool allowRecovery, EditMatchEvidence? evidence = null)
    {
        string diff = UnifiedDiff.Render(planned.OldContent, planned.NewContent, relativePath);

        if (!IsApply(request))
            return Preview(diff, json, renameSummary: null, siteCount: 0, evidence);

        // --- apply path: freshness gate (with gate-time self-heal), then atomic write, then write-through ---
        var gate = FreshnessGate.Check(_dbPath, relativePath, planned.FilePath, planned.OldContent);
        bool fresh = gate.Result == FreshnessResult.Fresh;
        if (!fresh && !request.AllowStale)
        {
            TimeSpan recoveryBudget = _recovery.Timeout;
            fresh = allowRecovery &&
                TryRecoverFreshness(relativePath, planned.FilePath, planned.OldContent, ref recoveryBudget);
            if (!fresh)
                return StaleBlocked(relativePath, gate.IndexedContentFound, json);

            // Recovery converged the index, but a SYMBOL op's byte spans were read from the PRE-recovery index —
            // if the drift moved the symbol (e.g. lines prepended above it), those spans now point at the wrong
            // bytes and splicing them would corrupt the file silently. Re-run resolve → span read → plan once
            // against the converged index and apply THAT plan instead. replace_text derives its spans from the
            // disk content itself, so its plan is safe to apply unchanged. The retry runs with the gate already
            // known-fresh (allowRecovery=false): it never re-enters recovery, and if the symbol no longer
            // resolves it returns the existing clean not-found/no-span error rather than applying a stale plan.
            if (op != EditOperation.ReplaceText)
                return ExecuteSingleFile(request, op, occurrence, json, allowRecovery: false);
        }

        var applyResult = _applier.Apply([planned]);
        if (!applyResult.Success)
            return Error(applyResult.Message, json, fresh, FailureApplyFailed);

        _writeThrough.Converge([planned.FilePath]);
        return Applied(diff, staleAllowed: !fresh && request.AllowStale, filesWritten: applyResult.FilesWritten,
            indexFresh: fresh, json, evidence: evidence);
    }

    private ReplaceTextPlanResult PlanReplaceText(
        string relativePath,
        string content,
        EditRequest request,
        Occurrence occurrence)
    {
        TryParseMatchMode(request.MatchMode, out var matchMode);
        string oldText = request.OldText ?? string.Empty;

        if (HasIndexedSelector(request))
        {
            long expectedRevision = ExpectedWorkspaceRevision();
            IndexedEditCandidateResult candidateResult = _indexedEditCandidateReader.FindCandidates(
                _dbPath,
                relativePath,
                expectedRevision,
                oldText,
                request.Query,
                request.Anchor,
                request.Line);

            if (candidateResult.State == IndexedEditCandidateState.Current)
                return PlanReplaceTextFromIndexedCandidates(content, request, occurrence, matchMode, candidateResult);

            if (candidateResult.State == IndexedEditCandidateState.NoMatch)
            {
                return ReplaceTextPlanResult.Error(
                    "no indexed edit candidates matched the selector. Narrow the edit with a different query, " +
                    "anchor, or line hint, or retry without indexed selectors.",
                    FailureNoMatch);
            }

            var fallback = TextReplaceMatcher.Plan(content, oldText, occurrence, matchMode);
            return ReplaceTextPlanResult.Success(
                fallback.Plan,
                EvidenceFromPlan(
                    fallback,
                    matchSource: "disk_after_index_unavailable",
                    contentIndexState: "unavailable",
                    occurrence,
                    candidateReason: candidateResult.Reason));
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
        string content,
        EditRequest request,
        Occurrence occurrence,
        TextMatchMode matchMode,
        IndexedEditCandidateResult candidateResult)
    {
        var successes = new List<(TextReplaceMatchPlan Plan, IndexedEditCandidate Candidate)>();
        foreach (IndexedEditCandidate indexedCandidate in candidateResult.Candidates)
        {
            TextWindow window = FocusIndexedCandidateWindow(content, indexedCandidate, request);
            TextReplaceMatchPlan windowPlan = TextReplaceMatcher.Plan(window.Text, request.OldText ?? string.Empty, occurrence, matchMode);
            if (!windowPlan.IsSuccess)
                continue;

            successes.Add((OffsetPlan(windowPlan, window), indexedCandidate));
        }

        if (successes.Count == 0)
        {
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

    private static TextWindow FocusIndexedCandidateWindow(string content, IndexedEditCandidate candidate, EditRequest request)
    {
        if (request.Line is { } line && line >= candidate.LineStart && line <= candidate.LineEnd)
            return SliceLineWindow(content, line, line);

        if (!string.IsNullOrEmpty(request.Anchor))
        {
            TextWindow candidateWindow = SliceLineWindow(content, candidate.LineStart, candidate.LineEnd);
            int anchorOffset = candidateWindow.Text.IndexOf(request.Anchor, StringComparison.Ordinal);
            if (anchorOffset >= 0)
            {
                int anchorLine = candidateWindow.StartLine + CountNewLinesBefore(candidateWindow.Text, anchorOffset);
                const int AnchorContextLines = 1;
                return SliceLineWindow(
                    content,
                    Math.Max(candidate.LineStart, anchorLine - AnchorContextLines),
                    Math.Min(candidate.LineEnd, anchorLine + AnchorContextLines));
            }
        }

        return SliceLineWindow(content, candidate.LineStart, candidate.LineEnd);
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
            plan.MatchCount);
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
            OccurrenceName(occurrence),
            DiskVerified: plan.IsSuccess,
            candidateReason,
            candidate?.LineStart,
            candidate?.LineEnd);
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

        // Every name-based occurrence across the workspace (homonyms INCLUDED — decision-5), grouped by file.
        IReadOnlyList<IdentifierSite> sites = ExtractReader.ReadIdentifierSites(_dbPath, oldName);

        // The DEFINITION name token is NOT an identifier row; locate it inside the def symbol's signature span
        // (the name token within [start_byte, body_start_byte) — or [start_byte, end_byte) for a bodyless symbol)
        // on the def file's disk content, and add it as an IsDefinition site (handoff contract).
        var span = ExtractReader.ReadEditSpan(_dbPath, target.SymbolId);

        var files = BuildRenameFiles(oldName, sites, target, span);
        if (files.Count == 0)
            return Error($"no occurrences of '{oldName}' found to rename.", json,
                failureReason: FailureNoMatch);

        RenamePlan plan = RenamePlanner.Plan(oldName, newName, files);
        if (!plan.IsSuccess)
            return Error(plan.Error!.Message, json,
                failureReason: FailureReasonFor(plan.Error.Kind));

        string diff = RenderRenameDiff(plan);
        string summary = RenderRenameSummary(oldName, newName, plan);

        if (!IsApply(request))
            return Preview(diff, json, summary, plan.TotalSites);

        // --- apply: gate EVERY touched file (with gate-time self-heal under ONE shared budget), then atomic
        // multi-file write, then per-file write-through ---
        bool anyStale = false;
        bool anyRecovered = false;
        TimeSpan renameRecoveryBudget = _recovery.Timeout;
        foreach (var pe in plan.PlannedEdits)
        {
            string rel = ToRelative(pe.FilePath);
            var gate = FreshnessGate.Check(_dbPath, rel, pe.FilePath, pe.OldContent);
            if (gate.Result != FreshnessResult.Fresh)
            {
                if (request.AllowStale)
                    anyStale = true;
                else if (allowRecovery && TryRecoverFreshness(rel, pe.FilePath, pe.OldContent, ref renameRecoveryBudget))
                    anyRecovered = true;
                else
                    return StaleBlocked(rel, gate.IndexedContentFound, json);
            }
        }

        // Recovery converged the index, but this plan's identifier/def-token sites were read from the
        // PRE-recovery index — if the drift moved them (e.g. lines prepended above), splicing the stale sites
        // would corrupt the file silently. Re-run resolve → site read → plan once against the converged index
        // (the retry gates every file again, known-fresh, and never re-enters recovery). If the symbol no
        // longer resolves the retry returns the existing clean not-found error rather than applying stale sites.
        if (anyRecovered)
            return ExecuteRename(request, json, allowRecovery: false);

        var applyResult = _applier.Apply(plan.PlannedEdits);
        if (!applyResult.Success)
            // The gate has been evaluated for every touched file (anyStale), so report the freshness verdict on
            // failure too — matching the single-file apply-failure path (FinishSingleFile) so telemetry's
            // IndexFresh is populated whenever the gate ran.
            return Error(applyResult.Message, json, !anyStale, FailureApplyFailed);

        _writeThrough.Converge(plan.PlannedEdits.Select(p => p.FilePath).ToArray());

        string appliedSummary = summary + "\n" + diff;
        return Applied(appliedSummary, staleAllowed: anyStale, filesWritten: applyResult.FilesWritten,
            indexFresh: !anyStale, json, resultCountOverride: plan.TotalSites);
    }

    // Assemble per-file RenameFileInputs from the identifier sites + the def name-token site, reading each
    // file's CURRENT disk content (the spans index into it). The def file gets the def token appended (deduped
    // so a def that also surfaced as an identifier is not rewritten twice).
    private List<RenameFileInput> BuildRenameFiles(
        string oldName, IReadOnlyList<IdentifierSite> sites, IndexedSymbol target, SymbolEditSpan? span)
    {
        // Group identifier sites by relative file path (already ordered file_path,start_byte by the reader).
        var byFile = new Dictionary<string, List<RenameSite>>(StringComparer.Ordinal);
        var content = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var s in sites)
        {
            string abs = ToAbsolute(s.FilePath);
            if (!File.Exists(abs))
                continue; // the index references a file that is gone; skip its sites (can't splice a missing file)
            if (!content.ContainsKey(s.FilePath))
                content[s.FilePath] = ReadDisk(abs);
            if (!byFile.TryGetValue(s.FilePath, out var list))
                byFile[s.FilePath] = list = [];
            list.Add(new RenameSite(s.StartByte, s.EndByte, s.StartLine, IsDefinition: false));
        }

        // Locate + add the definition name-token site in the def file.
        AddDefinitionSite(oldName, target, span, byFile, content);

        var result = new List<RenameFileInput>(byFile.Count);
        foreach (var (path, list) in byFile)
        {
            // Dedup any (start,end) collision (e.g. the def token already present as an identifier) and order
            // by start byte so the splicer's non-overlap validation sees a clean ascending set.
            var deduped = list
                .GroupBy(r => (r.StartByte, r.EndByte))
                .Select(g => g.First())
                .OrderBy(r => r.StartByte)
                .ToArray();
            result.Add(new RenameFileInput(ToAbsolute(path), content[path], deduped));
        }
        return result;
    }

    private void AddDefinitionSite(
        string oldName, IndexedSymbol target, SymbolEditSpan? span,
        Dictionary<string, List<RenameSite>> byFile, Dictionary<string, string> content)
    {
        if (span is null)
            return;

        string defRel = target.FilePath;
        string defAbs = ToAbsolute(defRel);
        if (!File.Exists(defAbs))
            return;

        if (!content.TryGetValue(defRel, out var fileText))
            content[defRel] = fileText = ReadDisk(defAbs);

        // The signature region is [start_byte, body_start_byte) when there is a body, else the whole span.
        int signatureEnd = span.BodyStartByte ?? span.EndByte;
        int? nameByteStart = FindNameTokenByteOffset(fileText, span.StartByte, signatureEnd, oldName);
        if (nameByteStart is not { } start)
            return; // could not locate the name token in the signature span — skip the def site (refs still rename)

        var defSite = new RenameSite(start, start + Encoding.UTF8.GetByteCount(oldName), span.StartLine, IsDefinition: true);
        if (!byFile.TryGetValue(defRel, out var list))
            byFile[defRel] = list = [];
        list.Add(defSite);
    }

    // Find the byte offset of the FIRST whole-word occurrence of <paramref name="name"/> within the UTF-8 byte
    // window [windowStartByte, windowEndByte) of <paramref name="content"/>. Whole-word = not flanked by an
    // identifier char, so "Total" does not match inside "GrandTotalizer". Returns null if not found.
    private static int? FindNameTokenByteOffset(string content, int windowStartByte, int windowEndByte, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (windowStartByte < 0 || windowEndByte > bytes.Length || windowEndByte <= windowStartByte)
            return null;

        byte[] needle = Encoding.UTF8.GetBytes(name);
        if (needle.Length == 0 || needle.Length > windowEndByte - windowStartByte)
            return null;

        for (int i = windowStartByte; i + needle.Length <= windowEndByte; i++)
        {
            if (!MatchesAt(bytes, i, needle))
                continue;
            // Whole-word boundary check on the ASCII identifier-char class (the name token itself is ASCII or
            // not — but the boundary chars that matter for splitting an identifier are ASCII letters/digits/_).
            bool leftOk = i == windowStartByte || !IsIdentifierByte(bytes[i - 1]);
            int after = i + needle.Length;
            bool rightOk = after >= bytes.Length || !IsIdentifierByte(bytes[after]);
            if (leftOk && rightOk)
                return i;
        }
        return null;
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

    private EditResult Preview(string diff, bool json, string? renameSummary, int siteCount, EditMatchEvidence? evidence = null)
    {
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

    private static EditResult Applied(
        string diffOrSummary, bool staleAllowed, int filesWritten, bool indexFresh, bool json,
        int? resultCountOverride = null, EditMatchEvidence? evidence = null)
    {
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
        return new EditResult(sb.ToString().TrimEnd('\n'), true, staleAllowed, indexFresh, "ok", count);
    }

    private static void AppendEvidence(StringBuilder sb, EditMatchEvidence? evidence)
    {
        if (evidence is null)
            return;

        sb.Append("Match proof:\n");
        sb.Append("- match_mode: ").Append(evidence.MatchMode).Append('\n');
        sb.Append("- match_source: ").Append(evidence.MatchSource).Append('\n');
        if (evidence.LineStart is { } lineStart && evidence.LineEnd is { } lineEnd)
            sb.Append("- line_range: ").Append(lineStart).Append('-').Append(lineEnd).Append('\n');
        sb.Append("- match_count: ").Append(evidence.MatchCount).Append('\n');
        sb.Append("- occurrence: ").Append(evidence.Occurrence).Append('\n');
        sb.Append("- disk_verified: ").Append(evidence.DiskVerified ? "true" : "false").Append('\n');
        sb.Append("- content_index_state: ").Append(evidence.ContentIndexState).Append('\n');
        if (!string.IsNullOrWhiteSpace(evidence.CandidateReason))
            sb.Append("- content_index_note: ").Append(evidence.CandidateReason).Append('\n');
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
        w.WriteString("occurrence", evidence.Occurrence);
        w.WriteBoolean("disk_verified", evidence.DiskVerified);
        w.WriteString("content_index_state", evidence.ContentIndexState);
        if (!string.IsNullOrWhiteSpace(evidence.CandidateReason))
            w.WriteString("content_index_note", evidence.CandidateReason);
    }

    private static EditResult StaleBlocked(string relativePath, bool indexedContentFound, bool json)
    {
        string reason = indexedContentFound
            ? $"index stale for {relativePath} — run a workspace refresh, or pass allow_stale to edit anyway."
            : $"no indexed snapshot for {relativePath} — run a workspace refresh first, or pass allow_stale.";
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
    private bool TryRecoverFreshness(string relativePath, string absPath, string diskText, ref TimeSpan budget)
    {
        StaleRecoveryAttempt attempt = _writeThrough.TryRecoverStaleFile(absPath);
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

    private string RenderRenameSummary(string oldName, string newName, RenamePlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("rename '").Append(oldName).Append("' → '").Append(newName).Append("': ")
          .Append(plan.TotalSites).Append(plan.TotalSites == 1 ? " site across " : " sites across ")
          .Append(plan.Summary.Count).Append(plan.Summary.Count == 1 ? " file" : " files").Append('\n');
        sb.Append("name-based match (target_symbol_id is unresolved at extract) — homonyms ARE included; ")
          .Append("review every site before apply.\n");
        foreach (var f in plan.Summary)
            sb.Append("  ").Append(ToRelative(f.FilePath)).Append("  (")
              .Append(f.SiteCount).Append(f.SiteCount == 1 ? " site)" : " sites)").Append('\n');
        return sb.ToString().TrimEnd('\n');
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

    private string ToAbsolute(string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(_workspaceRoot, relativeOrAbsolute);

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
        string Occurrence,
        bool DiskVerified,
        string? CandidateReason,
        int? CandidateLineStart,
        int? CandidateLineEnd);

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
