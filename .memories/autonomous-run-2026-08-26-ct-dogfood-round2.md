# Autonomous run report — CT dogfood round 2 (2026-08-26/27)

Branch `worktree-ct-dogfood-round2` (worktree `.claude/worktrees/ct-dogfood-round2`, base
`fa38f826` = released v1.24.0). Plan: `docs/plans/2026-08-26-ct-dogfood-round2.md`. Executed with
razorback subagent-driven development, parallel-lead-commit; every task lead-reviewed inline before
its commit.

## What shipped (per finding)

1. **Red survives unexecuted runs** (2993b3f4). Run start captures the displaced state in
   `pre_run_state` (ct.db schema v6) and keeps a red row's committed key; requested-but-unreported
   cases restore red with one owed `stale_since_revision` stamp at run commit; a nonzero unreported
   count logs one bounded `role:ct` `run_unreported_cases` line.
2. **Churn loop, stall half** (3ff57612). Diagnosis correction: julie no-ops byte-identical rewrites
   (`version_id` content-keyed; hash gate pinned by a new `RevisionDeltaReaderTests` regression). The
   stall was Miller's: truncated impact answered Unavailable — cursor pinned, interval growing,
   auto-runs paused after 8 misses. Now the complete delta is delivered as Changed with the truncation
   reason; the selector fails it closed to Unknown, staleness lands, the cursor advances.
3. **Churn loop, convergence half** (bfc003ee). `CtIdleDrainPolicy`: one owed-backlog drain when the
   workspace settles (staleness exists; empty queue; no executing run; poll healthy at the live
   revision; quiet ≥ debounce; auto-runs not paused; fixed 5-min per-context cooldown, also counted
   from daemon start). Selection = explicit run's stale-set selection with the automatic red rule,
   explicit test-ID list, never whole-suite eligible, no discovery.
4. **edit under churn** (550dc679). Both lazy index factories retry onto the CURRENT generation
   (bounded by `ReadableOpenAttempts`); `IndexHolder` discards a faulted lazy state instead of
   replaying the cached exception (CompareExchange so a concurrent swap wins); exhausted retries
   classify `unavailable`/`index_reloading` with a plain retry message, never `internal_failure`.
5. **failures real error line** (092b894d). Write-time two-part summary (first line + first
   error-shaped line, 400 UTF-8 bytes, rune-aware truncation); TRX capture widened from
   StackTrace/sibling Messages; RunInfo run-level errors folded into each failed case;
   `docs/contracts/tests-cli-v1.md` updated.
6. **Pause visibility** (4c6397ee). `AutoRunsPaused`/`PauseReason` trailing-optional on
   `CtDaemonStatusRecord` (old records read not-paused); `daemon.auto_runs_paused`/`pause_reason` in
   status JSON; compact `auto-runs paused: <reason>` line; one `role:ct` line on pause enter and clear.
7. **impact staged hint** (010fbc7a). Empty unstaged diff probes the staged diff once; when staged
   changes exist, the diagnostic says so with a JSON-visible `staged=true` action (CLI spells
   `--staged`); nothing-staged output byte-identical.
8. **Flat build layout** (7ad8f9df). Build root `.miller/ct-<proj12>`; deepest assembly dir exactly
   5 levels below the workspace root (separator-count pin); Windows root budget 117 → 123; peer scans
   filter to the `ct-` prefix; coordinator maintenance sweeps the legacy `.miller/ct/build` tree under
   the janitor's lease rules.

Bonus (e74b07a4): closed the TODO "intermittent single-test failure" — the SelfLog sink test blocked
only the UTC day's log family while Serilog rolls by LOCAL date; it failed exactly in the evening
local/UTC divergence window (both prior sightings were evening runs). Proven TZ=UTC green / CDT red;
the test now blocks both families.

## Verification ledger

| Scope | Command | HEAD | Result |
|---|---|---|---|
| worker-red-green (per task) | focused `dotnet test --filter` per task | per task | green, red-first TDD |
| affected-change (Batch A) | `scripts/test.sh` | e74b07a4 | 8886 passed, 0 failed |
| affected-change (Batch B) | `scripts/test.sh` | bfc003ee | 8917 passed, 0 failed |
| affected-change (Batch C) | `scripts/test.sh` | 7ad8f9df | 8930 passed, 0 failed |
| branch-gate (Scale) | `scripts/test.sh scale` | 7ad8f9df (source-final) | 198 passed, 0 failed, 17 honest skips |

Security scope: none declared (plan).

## Deferred minors (recorded in the SDD ledger)

- Idle-drain observation runs the stale-count aggregate per 250ms tick even inside cooldown — matches
  the existing per-tick Evaluate cost; make the read lazy only if profiling asks.
- A one-shot CLI from an OLD build can still write into the legacy `.miller/ct/build` tree until
  upgraded; the sweep reclaims it on the next maintenance pass.
- Round-2 observation 8 (impacted under-selection: 1 case picked vs round 1's 6) stays a watch item.

## Open user decisions

- Merge/push this branch (and the still-unmerged round-1 items if any remain).
- The main checkout's TODO.md carries the user's uncommitted round-2 notes; this branch's TODO.md
  records the campaign status. Reconcile the dirty file before merging (commit or discard it — the
  branch version preserves the substance plus statuses).
- julie-extract release + pin bump for the round-1 csproj-search half (unchanged).
