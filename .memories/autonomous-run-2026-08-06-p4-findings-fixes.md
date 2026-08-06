# Autonomous run report — P4 findings fixes

**Status:** Complete on the branch; merge to local main PENDING (this session is worktree-isolated
and the harness blocks main-checkout git operations, including from subagents). To integrate:
`cd /Users/murphy/source/miller && git merge --ff-only p4-findings-fixes`, then
`git worktree remove .claude/worktrees/p4-findings-fixes && git branch -d p4-findings-fixes`.
Push remains HELD by user.
**Plan:** docs/plans/2026-08-06-p4-findings-fixes-plan.md
**Branch:** p4-findings-fixes (base bc808b26, merged fast-forward, 7 commits)
**Reviewer:** codex (pre-merge, single pass)

## What shipped

The four P4 scale-validation product findings, fixed and re-validated:

1. **Machine-wide scan admission released before sidecar convergence** (T1, 512ccdb0). Leaders and
   cross-workspace refreshes now free the governor the moment the extract returns, instead of
   holding it through the ~200 s FTS5 sidecar build that serialized fleets and starved the 8th
   worktree.
2. **Bootstrap self-retry after an admission-wait timeout** (T2, 98028d72). A bootstrap that timed
   out waiting for admission re-queues itself on a jittered ~60 s delay instead of failing
   terminally until a human restarts the server. Ineligible rebinds now log one Information line
   naming the reason (was: silent full scan).
3. **Heartbeat-window wait before the rebind fallback** (T3, aa2b66ae). A worktree opened within
   30 s of the source checkout's scan no longer pays a silent full extraction; it waits out the
   window remainder (≤60 s budget) and rebinds.
4. **Exit-3 refusals carry exit_code 3 into the W8 scan-failure journal** (T4, 94162908).
   `IncompatibleExtractException` gains an optional exit code; read-path gates stay null.

Plus: the killed-holder governor Scale test reparked inside the new lease scope (328c2401).

## External review (codex)

2 findings, both verified with Miller, both fixed; none dismissed, none flagged.

- **[high] Stale admission retry can scan a replaced or sensitive root** — real-improvement
  (widened a pre-existing bind-time-validation gap). Fixed 76393b44: the retry re-canonicalizes
  and re-runs the sensitive-root guard at fire time, drops with one Information line on a missing,
  swapped, or now-sensitive root. Tests: vanished-root + real-symlink-swap.
- **[medium] Shutdown-cancelled heartbeat wait fell through to an uncancellable full scan** —
  real-bug. Fixed 8407a7bf: the cancelled wait throws OperationCanceledException and joins the
  existing abandoned-bootstrap path.

Codex does not report per-request token costs.

## Tests / gates

- Branch gate at 76393b44: Release build 0W/0E; fast suite 6148/0 (28 s, tripwire OK);
  Scale 129/0 (5 environment skips).
- Heartbeat smoke (mini fixture): worktree opened seconds after the source scan waited ~27 s,
  then rebound with a delta reconcile. Pre-fix: silent full scan.
- 74k 8-worktree fleet: **8/8 rebound, 8/8 fully converged in 1,210 s, zero admission-timeout
  lines** (pre-fix: 7/8 on a ~235 s ladder, the 8th starved at +10:00). A first attempt was
  invalidated by scratch-disk exhaustion (Time Machine snapshots pinned ~100 GB; thinned) — the
  sidecar failed visibly and the artifact kept serving, the fail-visible design holding.
- Evidence ledger: `.razorback/sdd/progress.md` (worktree-scoped scratch); durable copy in
  `docs/findings/2026-08-06-rebind-p4-scale-validation.md` §9.

## Blockers hit

None. One worker (fix-f1) died on an API connection drop mid-task and was restarted cleanly.

## Judgment calls

- The failing killed-holder Scale test pinned the removed hold-through-convergence lease; the test
  was reparked (4,000-file worktree makes the extract the observable admission window) rather than
  the product changed.
- The fleet re-run kept the 328c2401 binary: the two review-fix commits touch only the
  cancelled-wait and stale-retry arms, which the fleet harness does not exercise.
- Follow-up worth filing (out of scope, from fix-f1): `MarkBootstrapFailed` recreates
  `<root>/.miller` for a root that may already be gone; the registry row + presence monitor own
  that story.

## Next steps

- The push of local main (now 23 commits ahead of origin) remains HELD per your explicit
  instruction — say the word and it goes.
- Optional follow-up: the `MarkBootstrapFailed` `.miller`-recreation nit above.
