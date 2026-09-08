using System.Text;
using System.Text.Json;
using Miller.Core.Diff;
using Miller.Core.Editing;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Resolution;

namespace Miller.Server.Tools;

/// <summary>
/// Transactional multi-hunk and multi-file batch edit planner and executor for <c>operation=batch</c>.
/// Evaluates all operations against the initial disk snapshots (never chained rewritten text), validates disjoint
/// byte spans across all touched files, coalesces duplicate identical edits, and applies atomically via
/// <see cref="EditApplier"/> with reverse-order rollback.
/// </summary>
public static class EditBatchPlanner
{
    private const int MaxBatchOperations = 100;

    public static EditService.EditResult PlanAndExecute(
        EditService service,
        EditRequest batchRequest,
        bool json)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(batchRequest);

        IReadOnlyList<EditRequest>? items = batchRequest.BatchItems;
        if (items is null && !string.IsNullOrWhiteSpace(batchRequest.Edits))
        {
            try
            {
                items = ParseEditsJson(batchRequest.Edits, batchRequest.Apply, batchRequest.Format);
            }
            catch (JsonException ex)
            {
                return EditService.WithDiagnostic(
                    EditService.Error($"Invalid edits JSON: {ex.Message}", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }
        }

        if (items is null || items.Count == 0)
        {
            return EditService.WithDiagnostic(
                EditService.Error("batch requires a non-empty list of edits.", json,
                    failureReason: EditService.FailureInvalidRequest),
                json);
        }

        if (items.Count > MaxBatchOperations)
        {
            return EditService.WithDiagnostic(
                EditService.Error($"batch exceeds maximum of {MaxBatchOperations} operations (requested {items.Count}).", json,
                    failureReason: EditService.FailureInvalidRequest),
                json);
        }

        // 1. Pre-validate operations and reject nested batches
        foreach (EditRequest item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Operation))
            {
                return EditService.WithDiagnostic(
                    EditService.Error("operation is required for each batch item.", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }

            if (string.Equals(item.Operation, "batch", StringComparison.OrdinalIgnoreCase))
            {
                return EditService.WithDiagnostic(
                    EditService.Error("nested batch operations are not allowed.", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }

            if (!EditService.TryParseOperation(item.Operation, out _))
            {
                return EditService.WithDiagnostic(
                    EditService.Error($"unknown operation '{item.Operation}'. Valid: {string.Join(", ", EditService.OperationNames)}.", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }

            if (!EditService.TryParseOccurrence(item.Occurrence, out _))
            {
                return EditService.WithDiagnostic(
                    EditService.Error($"unknown occurrence '{item.Occurrence}'. Valid: first, last, all.", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }

            if (string.IsNullOrWhiteSpace(item.Target))
            {
                return EditService.WithDiagnostic(
                    EditService.Error("target is required for each batch item.", json,
                        failureReason: EditService.FailureInvalidRequest),
                    json);
            }
        }

        // 2. Resolve targets and check workspace path containment
        var plannedItems = new List<ResolvedBatchItem>(items.Count);
        var touchedFiles = new HashSet<string>(StringComparer.Ordinal);
        var relPathByAbs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (EditRequest item in items)
        {
            EditService.TryParseOperation(item.Operation, out var op);
            EditService.TryParseOccurrence(item.Occurrence, out var occ);

            if (op == EditOperation.ReplaceText)
            {
                try
                {
                    _ = service.ToAbsolute(item.Target);
                }
                catch (Exception ex) when (ex is EditService.InvalidEditTargetPathException or ArgumentException)
                {
                    return EditService.WithDiagnostic(
                        EditService.Error(ex.Message, json, failureReason: EditService.FailureInvalidRequest),
                        json);
                }

                var fileRes = service.Resolver.Resolve(item.Target, item.Scope, TargetKind.File);
                if (fileRes is not TargetResolution.File file)
                {
                    return EditService.WithDiagnostic(
                        EditService.NotFound(item.Target, json),
                        json);
                }

                string absPath;
                try
                {
                    absPath = service.ToAbsolute(file.Path);
                }
                catch (Exception ex)
                {
                    return EditService.WithDiagnostic(
                        EditService.Error(ex.Message, json, failureReason: EditService.FailureInvalidRequest),
                        json);
                }

                if (!File.Exists(absPath))
                {
                    return EditService.WithDiagnostic(
                        EditService.Error($"file not on disk: {file.Path} (index references it, but it is missing).", json,
                            failureReason: EditService.FailureTargetNotFound),
                        json);
                }

                touchedFiles.Add(absPath);
                relPathByAbs[absPath] = file.Path;
                plannedItems.Add(new ResolvedBatchItem(item, op, occ, file.Path, absPath, Span: null, Symbol: null));
            }
            else
            {
                var symRes = service.ResolveSymbol(item.Target, item.Scope);
                switch (symRes)
                {
                    case TargetResolution.Symbol sym:
                    {
                        if (op == EditOperation.AddDoc &&
                            !string.IsNullOrWhiteSpace(service.ReadDetail(sym.Value.SymbolId)?.DocComment))
                        {
                            return EditService.WithDiagnostic(
                                EditService.Error(
                                    $"symbol '{sym.Value.Name}' already has a doc comment. Use replace_text to modify the " +
                                    "existing doc, or insert_before to prepend lines — add_doc only documents an undocumented symbol.",
                                    json,
                                    failureReason: EditService.FailureInvalidRequest),
                                json);
                        }

                        var span = service.ReadEditSpan(sym.Value.SymbolId);
                        if (span is null)
                        {
                            return EditService.WithDiagnostic(
                                EditService.Error(
                                    $"symbol '{sym.Value.Name}' has no recorded span in the current index — the index is " +
                                    "behind the file (its id changed since the last extract). Re-index (or wait for the " +
                                    "freshness poll) and retry.",
                                    json,
                                    failureReason: EditService.FailureStaleTarget),
                                json);
                        }

                        string absPath;
                        try
                        {
                            absPath = service.ToAbsolute(sym.Value.FilePath);
                        }
                        catch (Exception ex)
                        {
                            return EditService.WithDiagnostic(
                                EditService.Error(ex.Message, json, failureReason: EditService.FailureInvalidRequest),
                                json);
                        }

                        if (!File.Exists(absPath))
                        {
                            return EditService.WithDiagnostic(
                                EditService.Error($"file not on disk: {sym.Value.FilePath} (index references it, but it is missing).", json,
                                    failureReason: EditService.FailureTargetNotFound),
                                json);
                        }

                        touchedFiles.Add(absPath);
                        relPathByAbs[absPath] = sym.Value.FilePath;
                        plannedItems.Add(new ResolvedBatchItem(item, op, occ, sym.Value.FilePath, absPath, span, sym.Value));
                        break;
                    }
                    case TargetResolution.Candidates cands:
                        return EditService.WithDiagnostic(service.Candidates(cands.Matches, json), json);
                    case TargetResolution.NotFound:
                        return EditService.WithDiagnostic(EditService.NotFound(item.Target, json), json);
                    default:
                        return EditService.WithDiagnostic(
                            EditService.Error($"could not resolve '{item.Target}' to a single symbol.", json,
                                failureReason: EditService.FailureTargetNotFound),
                            json);
                }
            }
        }

        // 3. Snapshot initial disk text once per touched file
        var originalDiskContents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string absPath in touchedFiles)
        {
            originalDiskContents[absPath] = EditService.ReadDisk(absPath);
        }

        // 4. Freshness Gate check across all touched files
        TimeSpan recoveryBudget = service.Recovery.Timeout;
        bool anyStaleWaitPerformed = false;
        foreach (string absPath in touchedFiles)
        {
            string relPath = relPathByAbs[absPath];
            string content = originalDiskContents[absPath];
            var gate = service.CheckFreshness(relPath, absPath, content);
            bool isFresh = gate.Result == FreshnessResult.Fresh;

            bool fileOnlyHasReplaceText = plannedItems
                .Where(p => string.Equals(p.AbsPath, absPath, StringComparison.Ordinal))
                .All(p => p.Op == EditOperation.ReplaceText);

            bool allowStaleForFile = fileOnlyHasReplaceText &&
                (batchRequest.AllowStale || plannedItems
                    .Where(p => string.Equals(p.AbsPath, absPath, StringComparison.Ordinal))
                    .All(p => p.Request.AllowStale));

            if (!isFresh && !allowStaleForFile)
            {
                bool recoveryWait = false;
                bool recovered = service.TryRecoverFreshness(
                    relPath,
                    absPath,
                    content,
                    ref recoveryBudget,
                    out recoveryWait);
                anyStaleWaitPerformed |= recoveryWait;

                if (!recovered)
                {
                    return EditService.WithDiagnostic(
                        EditService.StaleBlocked(relPath, gate.IndexedContentFound, json, allowStaleSafe: fileOnlyHasReplaceText) with
                        { StaleWaitPerformed = anyStaleWaitPerformed },
                        json);
                }
            }
        }

        // 5. Evaluate every operation against initial snapshot
        var fileEdits = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        var proofs = new List<OperationProof>();

        foreach (ResolvedBatchItem resolved in plannedItems)
        {
            string absPath = resolved.AbsPath;
            string relPath = resolved.RelPath;
            string content = originalDiskContents[absPath];
            if (!fileEdits.TryGetValue(absPath, out var editsList))
            {
                editsList = new List<TextEdit>();
                fileEdits[absPath] = editsList;
            }

            if (resolved.Op == EditOperation.ReplaceText)
            {
                if (resolved.Request.NewText is null)
                {
                    return EditService.WithDiagnostic(
                        EditService.Error("new_text is required for replace_text.", json,
                            failureReason: EditService.FailureInvalidRequest),
                        json);
                }

                var replacePlan = service.PlanReplaceText(
                    relPath,
                    content,
                    resolved.Request,
                    resolved.Occurrence,
                    ref recoveryBudget);
                anyStaleWaitPerformed |= replacePlan.StaleWaitPerformed;

                if (replacePlan.ErrorMessage is not null)
                {
                    return EditService.WithDiagnostic(
                        EditService.Error(replacePlan.ErrorMessage, json,
                            failureReason: replacePlan.FailureReason ?? EditService.FailureUnclassifiedResult) with
                        { StaleWaitPerformed = anyStaleWaitPerformed },
                        json);
                }

                if (!replacePlan.Plan!.IsSuccess)
                {
                    string msg = service.EditPlanFailureMessage(
                        replacePlan.Plan.Error!,
                        resolved.Op,
                        relPath,
                        resolved.Request.OldText,
                        out string failureReason);
                    return EditService.WithDiagnostic(
                        EditService.Error(msg, json, failureReason: failureReason) with
                        { StaleWaitPerformed = anyStaleWaitPerformed },
                        json);
                }

                IReadOnlyList<TextEdit> filled = EditService.FillReplacement(
                    replacePlan.Plan!.Edits,
                    resolved.Request.NewText);

                editsList.AddRange(filled);
                proofs.Add(new OperationProof(
                    resolved.Request.Operation,
                    resolved.Request.Target,
                    relPath,
                    replacePlan.Evidence,
                    filled.Count));
            }
            else
            {
                EditPlan plan = resolved.Op switch
                {
                    EditOperation.ReplaceSymbolBody => EditPlanner.ReplaceSymbolBody(content, resolved.Span!, resolved.Request.NewText ?? string.Empty),
                    EditOperation.ReplaceSymbolSignature => EditPlanner.ReplaceSymbolSignature(content, resolved.Span!, resolved.Request.NewText ?? string.Empty),
                    EditOperation.InsertBefore => EditPlanner.InsertBefore(resolved.Span!, resolved.Request.NewText ?? string.Empty),
                    EditOperation.InsertAfter => EditPlanner.InsertAfter(resolved.Span!, resolved.Request.NewText ?? string.Empty),
                    EditOperation.AddDoc => EditPlanner.AddDoc(content, resolved.Span!, resolved.Request.NewText ?? string.Empty),
                    _ => EditPlan.Failure(new EditError(EditErrorKind.MissingArgument, "unsupported operation")),
                };

                if (!plan.IsSuccess)
                {
                    string msg = service.EditPlanFailureMessage(
                        plan.Error!,
                        resolved.Op,
                        relPath,
                        resolved.Request.OldText,
                        out string failureReason);
                    return EditService.WithDiagnostic(
                        EditService.Error(msg, json, failureReason: failureReason),
                        json);
                }

                editsList.AddRange(plan.Edits);
                proofs.Add(new OperationProof(
                    resolved.Request.Operation,
                    resolved.Request.Target,
                    relPath,
                    Evidence: null,
                    plan.Edits.Count));
            }
        }

        // 6. Sort, coalesce identical duplicates, and verify disjoint spans per file
        var plannedEdits = new List<PlannedEdit>();
        var diffs = new List<string>();

        foreach ((string absPath, List<TextEdit> edits) in fileEdits)
        {
            string relPath = relPathByAbs[absPath];
            string originalContent = originalDiskContents[absPath];

            edits.Sort(static (a, b) =>
            {
                int cmp = a.StartByte.CompareTo(b.StartByte);
                return cmp != 0 ? cmp : a.EndByte.CompareTo(b.EndByte);
            });

            var coalesced = new List<TextEdit>();
            for (int i = 0; i < edits.Count; i++)
            {
                TextEdit curr = edits[i];
                if (coalesced.Count > 0)
                {
                    TextEdit prev = coalesced[^1];
                    if (prev.StartByte == curr.StartByte && prev.EndByte == curr.EndByte)
                    {
                        if (string.Equals(prev.Replacement, curr.Replacement, StringComparison.Ordinal))
                        {
                            // Identical duplicate: coalesce into one edit
                            continue;
                        }

                        // Conflicting replacements targeting the exact same span
                        return EditService.WithDiagnostic(
                            EditService.Error(
                                $"Conflicting replacements for span [{curr.StartByte},{curr.EndByte}) in {relPath}.",
                                json,
                                failureReason: EditService.FailureAmbiguousMatch),
                            json);
                    }

                    if (curr.StartByte < prev.EndByte)
                    {
                        // Overlapping spans
                        return EditService.WithDiagnostic(
                            EditService.Error(
                                $"Overlapping edit spans in {relPath}: [{prev.StartByte},{prev.EndByte}) and [{curr.StartByte},{curr.EndByte}).",
                                json,
                                failureReason: EditService.FailureInvalidRequest),
                            json);
                    }
                }
                coalesced.Add(curr);
            }

            string newContent;
            try
            {
                newContent = TextSplicer.Apply(originalContent, coalesced);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                return EditService.WithDiagnostic(
                    EditService.Error($"edit span does not fit the current file content ({ex.Message}); re-index and retry.", json,
                        failureReason: EditService.FailureStaleTarget),
                    json);
            }

            string diff = UnifiedDiff.Render(originalContent, newContent, relPath);
            diffs.Add(diff);
            plannedEdits.Add(new PlannedEdit(absPath, originalContent, newContent, coalesced));
        }

        string combinedDiff = string.Join("\n", diffs.Where(static d => !string.IsNullOrWhiteSpace(d)));

        // 7. Preview or Commit
        if (!batchRequest.Apply)
        {
            return RenderPreview(items.Count, plannedEdits.Count, combinedDiff, proofs, json) with
            {
                StaleWaitPerformed = anyStaleWaitPerformed,
            };
        }

        // Apply via EditApplier
        var applyResult = service.Applier.Apply(plannedEdits);
        if (!applyResult.Success)
        {
            if (applyResult.PartiallyApplied)
                return service.PartialApply(applyResult, json, indexFresh: true);

            return EditService.WithDiagnostic(
                EditService.Error(applyResult.Message, json, failureReason: EditService.FailureApplyFailed),
                json);
        }

        service.WriteThrough.Converge(plannedEdits.Select(static p => p.FilePath).ToArray());

        return RenderApplied(
            items.Count,
            applyResult.FilesWritten,
            combinedDiff,
            proofs,
            json,
            staleAllowed: batchRequest.AllowStale) with
        {
            StaleWaitPerformed = anyStaleWaitPerformed,
        };
    }

    private static IReadOnlyList<EditRequest> ParseEditsJson(string editsJson, bool parentApply, string parentFormat)
    {
        using var doc = JsonDocument.Parse(editsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("edits parameter must be a JSON array of edit operations.");

        var items = new List<EditRequest>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException("Each item in edits array must be an object.");

            string op = el.TryGetProperty("operation", out var opProp) ? opProp.GetString() ?? string.Empty : string.Empty;
            string target = el.TryGetProperty("target", out var targetProp) ? targetProp.GetString() ?? string.Empty : string.Empty;
            string? oldText = el.TryGetProperty("old_text", out var otProp) ? otProp.GetString() : null;
            string? newText = el.TryGetProperty("new_text", out var ntProp) ? ntProp.GetString() : null;
            string occurrence = el.TryGetProperty("occurrence", out var occProp) ? occProp.GetString() ?? "first" : "first";
            string matchMode = el.TryGetProperty("match_mode", out var mmProp) ? mmProp.GetString() ?? "auto" : "auto";
            string? query = el.TryGetProperty("query", out var qProp) ? qProp.GetString() : null;
            string? anchor = el.TryGetProperty("anchor", out var aProp) ? aProp.GetString() : null;
            int? line = el.TryGetProperty("line", out var lProp) && lProp.TryGetInt32(out int lVal) ? lVal : null;
            string? scope = el.TryGetProperty("scope", out var sProp) ? sProp.GetString() : null;
            string renameMode = el.TryGetProperty("rename_mode", out var rmProp) ? rmProp.GetString() ?? "exact" : "exact";
            string? excludeSites = el.TryGetProperty("exclude_sites", out var esProp) ? esProp.GetString() : null;
            bool allowStale = el.TryGetProperty("allow_stale", out var asProp) && asProp.GetBoolean();

            items.Add(new EditRequest(op, target)
            {
                OldText = oldText,
                NewText = newText,
                Occurrence = occurrence,
                MatchMode = matchMode,
                Query = query,
                Anchor = anchor,
                Line = line,
                Scope = scope,
                RenameMode = renameMode,
                ExcludeSites = excludeSites,
                AllowStale = allowStale,
                Apply = parentApply,
                Format = parentFormat,
            });
        }
        return items;
    }

    private static EditService.EditResult RenderPreview(
        int operationsCount,
        int filesCount,
        string combinedDiff,
        IReadOnlyList<OperationProof> proofs,
        bool json)
    {
        if (json)
        {
            string body = EditService.JsonObject(w =>
            {
                w.WriteBoolean("applied", false);
                w.WriteString("mode", "preview");
                w.WriteNumber("operations_count", operationsCount);
                w.WriteNumber("total_operations", operationsCount);
                w.WriteNumber("files_count", filesCount);
                w.WriteNumber("total_files_affected", filesCount);
                w.WriteNumber("files_modified", 0);
                w.WriteString("diff", combinedDiff);
                w.WriteStartArray("proofs");
                foreach (var proof in proofs)
                {
                    w.WriteStartObject();
                    w.WriteString("operation", proof.Operation);
                    w.WriteString("target", proof.Target);
                    w.WriteString("file", proof.RelPath);
                    w.WriteNumber("edits_count", proof.EditsCount);
                    if (proof.Evidence is not null)
                    {
                        w.WritePropertyName("evidence");
                        w.WriteStartObject();
                        EditService.WriteEvidenceJson(w, proof.Evidence);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            return new EditService.EditResult(body, Applied: false, StaleAllowed: false, IndexFresh: null, Outcome: "ok", ResultCount: operationsCount);
        }

        var sb = new StringBuilder();
        sb.Append("Preview — pass apply=true to commit.\n");
        sb.Append("Batch: ").Append(operationsCount).Append(" operation(s) across ")
            .Append(filesCount).Append(filesCount == 1 ? " file.\n" : " files.\n");

        foreach (var proof in proofs)
        {
            sb.Append("• ").Append(proof.Operation).Append(" on ").Append(proof.Target);
            if (proof.Evidence is not null)
            {
                sb.Append(" [").Append(proof.Evidence.MatchMode).Append(" ×").Append(proof.Evidence.MatchCount);
                if (proof.Evidence.LineStart is { } ls)
                    sb.Append(" @ L").Append(ls);
                sb.Append(']');
            }
            sb.Append('\n');
        }

        sb.Append(combinedDiff);
        return new EditService.EditResult(sb.ToString().TrimEnd('\n'), Applied: false, StaleAllowed: false, IndexFresh: null, Outcome: "ok", ResultCount: operationsCount);
    }

    private static EditService.EditResult RenderApplied(
        int operationsCount,
        int filesWritten,
        string combinedDiff,
        IReadOnlyList<OperationProof> proofs,
        bool json,
        bool staleAllowed)
    {
        if (json)
        {
            string body = EditService.JsonObject(w =>
            {
                w.WriteBoolean("applied", true);
                w.WriteNumber("files_written", filesWritten);
                w.WriteNumber("files_modified", filesWritten);
                w.WriteNumber("operations_count", operationsCount);
                w.WriteNumber("total_operations", operationsCount);
                w.WriteBoolean("stale_allowed", staleAllowed);
                w.WriteBoolean("index_fresh", true);
                w.WriteString("diff", combinedDiff);
                w.WriteStartArray("proofs");
                foreach (var proof in proofs)
                {
                    w.WriteStartObject();
                    w.WriteString("operation", proof.Operation);
                    w.WriteString("target", proof.Target);
                    w.WriteString("file", proof.RelPath);
                    w.WriteNumber("edits_count", proof.EditsCount);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            return new EditService.EditResult(body, Applied: true, StaleAllowed: staleAllowed, IndexFresh: true, Outcome: "ok", ResultCount: operationsCount);
        }

        var sb = new StringBuilder();
        sb.Append("Applied — ").Append(filesWritten).Append(filesWritten == 1 ? " file written" : " files written")
            .Append(" (").Append(operationsCount).Append(" operations).\n");
        sb.Append(combinedDiff);
        return new EditService.EditResult(sb.ToString().TrimEnd('\n'), Applied: true, StaleAllowed: staleAllowed, IndexFresh: true, Outcome: "ok", ResultCount: operationsCount);
    }

    private sealed record ResolvedBatchItem(
        EditRequest Request,
        EditOperation Op,
        Occurrence Occurrence,
        string RelPath,
        string AbsPath,
        SymbolEditSpan? Span,
        IndexedSymbol? Symbol);

    private sealed record OperationProof(
        string Operation,
        string Target,
        string RelPath,
        EditService.EditMatchEvidence? Evidence,
        int EditsCount);
}
