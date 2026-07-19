# Task 3 report: Edit failure-reason completeness

**Status:** COMPLETE
**Commit SHA:** none - parallel-lead-commit (no `git add` / `git commit` run)
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`, branch
`worktree-semantic-integration` @ `87f9b1d` (dirty — my owned files plus siblings' in-flight files)

> This file replaced a stale `task-3-report.md` belonging to a DIFFERENT plan ("Styled 404, version footer,
> JSON links open in new tab", worktree `dashboard-ux-fixes`). That report itself recorded replacing an even
> older stale task-3 report, so this path collides across plans repeatedly. Wrote here per the lead's explicit
> path instruction. Flagging in case the dashboard-ux-fixes report needs preserving elsewhere.

## Implementation

### Audit result (the headline finding)

The EditService audit found **zero null-reason paths for known error kinds**. Every `Error(...)` call site
already passes an explicit bucket (or falls to the `FailureUnknown` default), and every direct `EditResult`
construction with a non-`ok` outcome already carries one:

| Path | Bucket | Site |
| --- | --- | --- |
| unknown operation / occurrence / match_mode | `invalid_request` | EditService.cs:127, :131, :135 |
| `add_doc` on documented symbol | `invalid_request` | :170 |
| no recorded span | `stale_target` | :179 |
| file missing on disk | `target_not_found` | :202 |
| `new_text` missing for replace_text | `invalid_request` | :212 |
| replace_text plan error | plan bucket ?? `unknown` | :217 |
| planner failure | `FailureReasonFor(kind)` | :239 |
| splice span mismatch | `stale_target` | :258 |
| apply failure (single-file / rename) | `apply_failed` | :299, :612 |
| rename missing new name | `invalid_request` | :536 |
| rename no occurrences | `no_match` | :565 |
| rename plan error | `FailureReasonFor(kind)` | :570 |
| `StaleBlocked` | `stale_target` | :883, :884 |
| `Candidates` | `ambiguous_match` | :981, :989 |
| `NotFound` | `target_not_found` | :997, :998 |

This was confirmed empirically, not only by reading: of the 12 new failure-class tests written first,
**11 passed immediately** and only the exception-backstop test failed. That is the real gap behind the
historical 41/52 reasonless rows — a path that never builds an `EditResult` at all.

### Changes

**`src/Miller.Server/Tools/EditTool.cs`**
- Added `FailureReasonMetadataKey` (`"edit_failure_reason"`) and `UnhandledFailureReasonPrefix`
  (`"unhandled_"`) constants so the key and prefix are named once.
- **Exception backstop** (the actual fix): the `catch` block now stamps
  `edit_failure_reason = "unhandled_" + ex.GetType().Name`. Type name only — message and full detail stay in
  the scope's dedicated `ErrorMessage`/`ErrorDetail` fields and never enter `MetadataJson`.
- **Invariant guard at the stamping site**: when a result carries no `FailureReason` but the telemetry outcome
  is `Error`, stamp `EditService.FailureUnknown`. Currently unreachable through the public API (see the audit
  table); it exists so a future `EditResult` path cannot silently reintroduce a null-reason error row.

**`src/Miller.Server/Tools/EditService.cs`**
- `FailureUnknown` widened `private` → `internal` (same assembly) so the tool's guard reuses the constant
  instead of duplicating the literal. Added a doc comment stating its semantic. No behavioral change.

**`tests/Miller.Tests/Server/EditToolTests.cs`**
- `BuildTool` parameterized with optional `applier` / `writeThrough` (defaults unchanged).
- New `ThrowingWriteThrough` whose post-apply `Converge` throws `InvalidOperationException` with a
  path-bearing message — the rig for the exception path and the proof the message does not leak.
- Documented the bucket vocabulary in a comment above `DocumentedFailureBuckets`, including the two-shape rule
  and the `unknown` vs `unhandled_<T>` distinction. No third bucket introduced.
- `StampedFailureBucket` helper: asserts the key is present, that the value is either a documented stable
  bucket or `unhandled_` + an identifier-safe type name, and that none of the supplied path/content strings
  appear anywhere in `MetadataJson` — extending the :1461 privacy assertion style to every failure class.
- 12 new tests, one per failure class.

## Verification

- **Invariant:** every non-successful edit telemetry row carries a non-null, privacy-safe
  `edit_failure_reason` bucket drawn from the documented set; no paths, user content, or exception messages
  reach `MetadataJson`.
- **Assigned scope:** `dotnet test --filter "FullyQualifiedName~EditTool"`
- **Result:** **Passed — 69/69, 0 failed** (68 passed / 1 failed before the fix — exactly the exception
  backstop test).
- **Timestamp:** 2026-07-19
- Also ran the full fast suite (`scripts/test.sh`): 3616 passed / 1 failed. The failure is
  `TelemetryLedgerTests.AddTextColumn_ToleratesAColumnAnotherProcessAlreadyAdded`, in **Task 2's** file
  (`TelemetryLedger.cs`), modified concurrently in this shared worktree — not mine; the test count grew
  3617→3618 between runs from sibling churn. An earlier run showed a different transient failure
  (`IndexerServiceScanTests.StartAsync_WhenEnabledLeader_BuildsSearchSidecarAfterStartupScan`) which **passes
  in isolation** and passed on re-run — parallel-contention flake, unrelated to edit telemetry.

### Failure classes covered by test

| Test | Bucket asserted |
| --- | --- |
| `Edit_UnknownOperation_StampsInvalidRequestBucket` | `invalid_request` |
| `Edit_UnknownOccurrence_StampsInvalidRequestBucket` | `invalid_request` |
| `Edit_TargetNotFound_StampsTargetNotFoundBucket` | `target_not_found` |
| `Edit_AmbiguousTarget_StampsAmbiguousMatchBucket` | `ambiguous_match` |
| `Edit_StaleTarget_StampsStaleTargetBucket` | `stale_target` |
| `Edit_ReplaceSymbolBody_OnNullBodySymbol_StampsFailureBucket` | `invalid_request` (body-rewrite) |
| `Edit_ApplyFailure_StampsApplyFailedBucket` | `apply_failed` |
| `Edit_RenameSymbol_InvalidNewName_StampsInvalidRequestBucket` | `invalid_request` (rename) |
| `Edit_RenameSymbol_MissingNewName_StampsInvalidRequestBucket` | `invalid_request` (rename) |
| `Edit_RenameSymbol_AmbiguousTarget_StampsAmbiguousMatchBucket` | `ambiguous_match` (rename) |
| `Edit_ReplaceText_NoMatch_StampsNoMatchBucket` | `no_match` |
| `Edit_UnhandledException_StampsExceptionTypeNameBucketWithoutMessage` | `unhandled_InvalidOperationException` |

Coverage spans replace_text, replace_symbol_body, and rename_symbol, per the brief's requirement not to stop
at replace_text.

## Files changed

- `src/Miller.Server/Tools/EditTool.cs` (+17/-3)
- `src/Miller.Server/Tools/EditService.cs` (+2/-1)
- `tests/Miller.Tests/Server/EditToolTests.cs` (+233/-1)

No files outside the assigned ownership set were touched (verified with `git status --short`; the other dirty
paths belong to sibling tasks).

## Miller calls used

- `inspect target=src/Miller.Server/Tools/EditService.cs limit=80` — full symbol list; confirmed the seven
  failure constants at :40-46, the `Error` chokepoint at :1001, and the direct-construction renderers
  (`Preview`, `Applied`, `StaleBlocked`, `Candidates`, `NotFound`) that bypass it. This scoped the audit.
- `search query="FailureReason" mode=source limit=30` — every population and assertion site across src and
  tests in one call, including the `EditTool.cs:120` stamping site and `ReplaceTextPlanResult`'s
  `?? FailureUnknown` coalesce.
- `inspect target=TelemetryContext depth=overview` — confirmed the ambient-scope mechanism the tests rely on.

Both Miller result sets matched the files as subsequently read; no staleness observed.

## API-shape evidence

- `TelemetryScope.SetError` (TelemetryScope.cs:208) writes `ErrorKind`/`ErrorMessage`/`ErrorDetail` to
  dedicated properties and puts only `error_category` into metadata — read directly to confirm the exception
  message cannot reach `MetadataJson` via the existing path, so the backstop only needed the type-name bucket.
- `TelemetryScope.MetadataJson` is a public settable string property (:127), so tests assert on it directly
  without a ledger round-trip.
- `IEditWriteThrough.TryRecoverStaleFile` has a default implementation (existing `RecordingWriteThrough`
  implements only `Converge`), so `ThrowingWriteThrough` needed only `Converge`.

## Judgment calls

1. **Disk-read exceptions left to the backstop.** `ReadDisk` (`File.ReadAllText`, EditService.cs:1086) can
   throw `IOException`/`UnauthorizedAccessException` for an unreadable file. I did not add a catch in
   EditService: no existing bucket fits semantically (`target_not_found` and `apply_failed` would both
   misrepresent it), and the brief forbids a new bucket. These now surface as
   `unhandled_UnauthorizedAccessException` etc. — the designed backstop behavior, and more diagnostic than a
   generic bucket.
2. **`Error("unrecognized target resolution.", json)` at EditService.cs:192 left on the `unknown` default.**
   It is a defensive `default:` arm over a closed resolution union — unreachable in practice. `unknown` is
   exactly the documented semantic for "a known code path that reached `Error(...)` without a specific
   bucket"; assigning a specific bucket would misrepresent it.
3. **The `else if (Outcome == Error)` guard has no test** because it is unreachable through the public API
   today. It is invariant enforcement at the stamping site, not a live path; testing it would require
   contorting the production API. Documented here rather than covered by a test that proves nothing.
4. **`internal` rather than duplicating `"unknown"`.** Widening the constant keeps one source of truth for the
   bucket string across the service and the tool.

## Concerns

- None blocking. One shared-worktree hazard for the lead: the full fast suite is currently red on
  `TelemetryLedgerTests` from Task 2's in-flight edits, so a whole-suite green is only observable after the
  sibling tasks land. My assigned scope is fully green.
- The `.razorback/sdd/task-N-report.md` paths collide across plans (see the note at the top). Worth
  namespacing per plan if these reports are meant to persist.
