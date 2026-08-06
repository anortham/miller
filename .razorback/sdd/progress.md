# SDD progress — P4 findings fixes

Plan: docs/plans/2026-08-06-p4-findings-fixes-plan.md
Branch: p4-findings-fixes (worktree, base bc808b26)
Reviewer choice: codex (pre-merge)

Batch order: Batch A (Tasks 1, 2, 3, 4 parallel, parallel-lead-commit).

## Task completion

Task 4: complete (parallel-lead-commit, Lead inline review clean — shared exit-3 Interpret site judgment call accepted, lead commit 94162908)
Task 2: complete (parallel-lead-commit, Lead inline review clean — source param + LogRebindFallback extraction accepted, lead commit 98028d72)
Task 3: complete (parallel-lead-commit, Lead inline review clean — slice-accumulation budget accepted; standing note: RebindBootstrapScaleTests relies on its backdated heartbeat to stay on the no-wait path, lead commit aa2b66ae)
Task 1: complete (parallel-lead-commit, Lead inline review clean — ScanAsLeaderUnderGate admission threading + CrossWorkspaceRefreshServiceTests inversion beyond declared ownership, both reconciled, lead commit 512ccdb0)

All 4 plan tasks complete. Tools restored (.tools 2.27.0 + sidecar) after worker phase.

Standing notes: fast-suite wall-clock tripwire fired under 4-worker load (73s; MetricSnapshotAggregatesTests.ReadConvergeMetrics_MarkerCountsAreExactAboveSearchLimit 44.5s + MarkerSearchTests.FindMarkers_AppliesMarkerFilterBeforeLimit 43.3s, both pre-existing from 4b3ff371) — re-judge on the quiet-machine branch gate. RebindBootstrapScaleTests relies on its backdated heartbeat to stay on the no-wait path (T3 note).

Gate follow-up: the first branch gate at 512ccdb0 failed one Scale test
(`KilledHolder_FreesScanAdmission_WithoutManualCleanup`) — it pinned the pre-T1 hold-through-convergence
lease. Reparked inside the new lease scope (lead inline, commit 328c2401): 4,000-file `SeedLargeWorktree`
makes the extract the seconds-wide admission window; `RequireLiveHolder` replaces the content-lock park.
6/6 repeat runs green. The 73s fast-suite tripwire breach was 4-worker load: quiet machine = 28s, passes.

## Pre-merge review (codex, single pass)

Diff bc808b26..328c2401. Verdict: needs-attention, 2 findings, both verified with Miller + worktree reads:
- Finding 1 (high): stale admission retry re-enters the bootstrap without re-validating the root.
  Classified real-improvement (widens a pre-existing bind-time-validation gap). FIXED — fix-f1 worker
  (restarted once after an API connection drop), lead commit 76393b44.
- Finding 2 (medium): shutdown-cancelled heartbeat wait mapped to Ineligible → uncancellable fallback
  extraction. Classified real-bug. FIXED — fix-f2 worker, lead commit 8407a7bf.
Zero dismissed, zero flagged. Codex does not report token costs.

## Expensive-specialist gates (lead-run)

- Heartbeat smoke (mini fixture): PASS — worktree opened seconds after the source scan waited ~27s
  then rebound + delta-reconciled (was: silent full scan). First attempt surfaced the new
  ineligible-rebind Information log on a stale registry row (T2 fix observed live).
- 74k 8-worktree fleet: PASS — 8/8 rebound, 8/8 fully converged in 1,210s (ladder 1,082–1,210s),
  zero admission-timeout lines in 9 logs. First attempt invalidated by scratch-disk exhaustion
  (TM snapshots pinned ~100GB; sidecar failed visibly with SQLITE_FULL, artifact kept serving) —
  environment, not product. Binary at 328c2401; the two review-fix commits touch only the
  cancelled-wait and stale-retry arms, which this harness does not exercise.

## Verification ledger
| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| branch-gate | Full fast+scale suites green with all 4 fixes + test repark | `dotnet build Miller.slnx -c Release` + `scripts/test.sh all` + `scripts/test.sh` | 328c2401 | PASS — build 0W/0E; fast 6145/0 (28s, tripwire OK); scale 129/0, 5 env skips | 2026-08-06 |
| expensive-specialist | Post-scan worktree open waits out the heartbeat window then rebinds | `smoke.sh` (scratchpad p4 harness, rebuilt binary) | 328c2401 | PASS — ~27s wait, rebind + delta reconcile, no full scan | 2026-08-06 |
| expensive-specialist | 8-worktree fleet: 8/8 rebound+converged, no admission timeouts | `fleet2.sh 8` (scratchpad p4 harness) | 328c2401 | PASS — 1,210s all-converged, 0 timeout lines | 2026-08-06 |
| branch-gate | Suites green with both codex review fixes | `dotnet build Miller.slnx -c Release` + `scripts/test.sh all` | 76393b44 | PASS — build 0W/0E; fast 6148/0 (28s, tripwire OK); scale 129/0 | 2026-08-06 |
