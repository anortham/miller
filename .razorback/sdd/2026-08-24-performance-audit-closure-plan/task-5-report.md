# Task 5 report — CT history retention review fixes

Status: complete in the worker worktree; the lead-owned closure plan remains dirty and is not part of this packet.

## Scope and API evidence

Workspace: `8737964a800fd9f1574b2d1a36b655386ac15b41702f9971bf61bf158fb781c1` (`/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23`).

Miller evidence gathered before and after the correction:

- `mcp__miller__context(query="CT retention coordinator maintenance failure isolation and honest prune counters; inspect RunMaintenanceTail PruneContinuousTestHistory result and coordinator tests", workspace_id="8737964a", token_budget=4000, ensure_fresh=true, format="compact")` identified `RunMaintenanceTail`, `PruneContinuousTestHistory`, `CommitMaintenance`, and `PruneContinuousTestHistoryInTransaction`.
- `mcp__miller__inspect(target="RunMaintenanceTail", scope="src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs", depth="full")` proved the private maintenance-tail shape: `RunMaintenanceTail(ContinuousTestWorkspace workspace, string? activeGenerationId)`, called by both successful and failure paths.
- `mcp__miller__inspect(target="PruneContinuousTestHistoryInTransaction", scope="src/Miller.Testing/Store/ContinuousTestStore.Retention.cs", depth="full")` proved the store operation is transaction-backed and builds temporary keep tables before deletion.
- `mcp__miller__inspect(target="Transaction", scope="src/Miller.Testing/Store/ContinuousTestStore.cs", depth="overview")` proved rollback behavior is owned by `ContinuousTestStore.Transaction(Action)`.
- `mcp__miller__impact(target="RunMaintenanceTail", workspace_id="8737964a", format="compact")` showed callers at `RunSelectedInsideProjectGateAsync` and `TerminalizeFailedRun`.
- After edits, `mcp__miller__workspace(operation="refresh", workspace_id="8737964a", format="compact", ensure_fresh=true)` refreshed revision `39837`; post-edit inspect/impact confirmed the guarded maintenance path and the same two callers.

Relevant internal API shapes:

- `internal ContinuousTestHistoryPruneResult PruneContinuousTestHistory(string workspaceId, DateTimeOffset now)`.
- `ContinuousTestHistoryPruneResult` reports considered/deleted/protected run, result, and artifact counts, legacy artifact count, `PageCount`, and `FreelistCount`.
- JUnit and coverage artifact payloads carry `run_id` and `project_path`; legacy rows missing either key remain compatibility-protected.

## Review corrections

### Failure isolation

`RunMaintenanceTail` now catches exceptions only around `PruneContinuousTestHistory`, emits bounded `ct_history_prune_failed type=<exception type>` diagnostics, and returns from the maintenance tail. The store still throws and rolls back when called directly. Generation maintenance remains outside this guard.

This prevents a retention trigger/SQLite error from converting an already-successful provider run into a failed run. On the failure path, the original provider exception remains the exception observed by the caller.

### Honest counters

The keep tables may contain an orphan `ct_test_states.running_run_id` for a run row that does not exist. The result now computes `ProtectedRuns`, `ProtectedResults`, and `ProtectedArtifacts` from actual post-delete workspace row counts rather than temporary keep-ID counts. Therefore each table reports the invariant `considered = deleted + protected` even with orphan references.

### Required report

This file records the exact Miller evidence, algorithm, chronology, verification, judgment calls, commits, checkpoints, and worktree state requested by the review.

## Keep-set and closure algorithm

Within one `ContinuousTestStore.Transaction` and one workspace scope:

1. Seed runs with active rows (`ended_at IS NULL` or `status=running`), rows in the 30-day window, and runs named by current `ct_test_states.running_run_id` values.
2. Seed results with rows in the 30-day window plus the newest 50 normalized outcomes per test, ordered by observed time and result id.
3. Add the runs named by retained results.
4. Seed artifacts with rows in the 30-day window, legacy rows missing `run_id` or `project_path`, and the newest artifact for each enabled project.
5. Close transitively: retain every result belonging to a retained run, every artifact referenced by a retained run/result, and every existing run named by a protected artifact payload. Repeat until no IDs are added.
6. Delete linked coverage spans/files for pruned artifacts, then unprotected results, runs, and artifacts. Do not touch test cases, current states, watermarks, project rows, or flakiness columns. No VACUUM or foreground checkpoint is run.
7. Count actual remaining rows after deletion and report SQLite page/freelist facts.

## TDD chronology

The review regressions were authored before the implementation correction was restored.

- `2026-08-24T03:41:26-05:00`: temporarily restored the reviewed defects and ran:

  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~ContinuousTestCoordinatorRetentionTests.Retention_failure|FullyQualifiedName~ContinuousTestStoreRetentionTests.Prune_reports_actual_remaining_counts' --no-restore`

  Red result: 3 failed. The successful-run test surfaced `SQLite Error 19: retention failure`; the original-provider-failure test surfaced the SQLite exception instead of `provider original failure`; the counter test reported expected `1`, actual `2` for the invariant.

- `2026-08-24T03:41:45-05:00`: reapplied both fixes.
- `2026-08-24T03:41:55-05:00`: the same focused regression command passed 3/3.
- `2026-08-24T03:42:06-05:00`: the complete assigned focused scope passed 69/69:

  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter 'FullyQualifiedName~ContinuousTestStoreTests|FullyQualifiedName~ContinuousTestStoreRetentionTests|FullyQualifiedName~ContinuousTestCoordinatorRetentionTests|FullyQualifiedName~ContinuousTestDaemonRunnerTests|FullyQualifiedName~JunitTestArtifactImporterTests|FullyQualifiedName~CoverageArtifactImporterTests' --no-restore`

- `2026-08-24T03:42:42-05:00`–`03:42:43-05:00`: `dotnet build src/Miller.Testing/Miller.Testing.csproj --no-restore` passed with 0 warnings and 0 errors; `git diff --check` passed.

## Self-review and mutation checks

- The failure-isolation tests install a real SQLite `BEFORE DELETE` trigger for `artifact:old`; they assert successful provider status, original provider exception text, unchanged artifact, and bounded lifecycle diagnostics.
- The counter test disables foreign keys only while inserting a deterministic orphan `running_run_id`, then proves all three considered/deleted/protected invariants and zero false protection counts.
- The follow-up cleanup reuses the three actual protected counts for `deleted = considered - protected`; post-edit Miller inspect reports `CountRows ×6` in the transaction instead of the prior `CountRows ×9`. No statement-count observer was added because the existing invariant regression was the established cheap seam.
- The red run above is a mutation check for both reviewed defects; the green run proves their corrected behavior.
- Focused existing store, coordinator runner, JUnit importer, and coverage importer tests remained green alongside the new tests.
- No schema version, public CLI/MCP output, cache/generation deletion, physical compaction, or unrelated plan file was changed.

## Judgment calls

- Payload linkage uses existing JSON (`run_id`, `project_path`) instead of adding `run_artifacts` columns or expanding public model columns. Coordinator-created JUnit and coverage imports populate both keys; direct imports without project context remain compatibility-protected.
- Any legacy artifact missing either linkage key is preserved and counted as `LegacyUnlinkedArtifacts`; this avoids deleting pre-linkage history and keeps the finite compatibility set visible.
- Active means an unset end time, explicit `running` status, or a current state’s running-run reference. The state reference is retained in the keep seed even if its run row is orphaned, while reported protected counts reflect actual rows.
- Logical row deletion is enforced; `PageCount` and `FreelistCount` are evidence only. Physical compaction stays report-only as approved.

## Files, commits, and checkpoints

Changed Task 5 files:

- `src/Miller.Testing/Daemon/ContinuousTestCoordinator.cs`
- `src/Miller.Testing/Store/ContinuousTestStore.Retention.cs`
- `tests/Miller.Tests/Testing/Daemon/ContinuousTestCoordinatorRetentionTests.cs`
- `tests/Miller.Tests/Testing/Store/ContinuousTestStoreRetentionTests.cs`
- This report.

Earlier Task 5 commits retained in this branch: `83df8652` (retention implementation), `723875ac` (verification checkpoints), `845e52ea` (internal API boundary), and `86738ac9` (review correction: failure isolation, honest counters, and this report). The current cleanup removes three redundant count queries while preserving the same invariant.

Goldfish checkpoints: `checkpoint_156fe21f`, `checkpoint_41ecc92a`, `checkpoint_2b5ebf38`, and `checkpoint_a87e5ff4`.

Follow-up cleanup verification at `2026-08-24T03:45:22-05:00`–`03:45:38-05:00`: retention/coordinator tests passed 11/11; `Miller.Testing` build passed 0 warnings/errors; `git diff --check` passed. Miller refresh reached revision `39843`; post-edit inspect/impact confirmed the reduced `CountRows` call graph. The cleanup preserves the same 69-test full focused result recorded above.

## Worktree state at report authoring

- Path: `/home/murphy/source/miller/.worktrees/perf-ct-audit-2026-08-23`
- Branch: `perf/ct-audit-2026-08-23`
- HEAD at the final review-correction state: `86738ac9509ae841da3e650b3854da042bb13ca5`
- `git status --short --branch`: the closure plan was the pre-existing lead-owned modification; Task 5 source/tests and this report comprise the worker packet.
- No push, merge, release, or main-worktree mutation was performed.
