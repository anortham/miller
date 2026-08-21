# Autonomous run report — CT watermark freshness

**Status:** Complete
**Plan:** `docs/plans/2026-08-21-ct-watermark-freshness.md` (design: `docs/plans/2026-08-21-ct-watermark-freshness-design.md`)
**Branch:** `feat/ct-watermark-freshness`, merged to `main` locally (fast-forward to `032ba035`). Not pushed — you asked for local-only merges this session.
**PR:** not created (local-merge mode per your instruction).
**Tasks:** 9 of 9 complete, plus 5 live-validation defect fixes (D1–D5) and 8 review fixes.

## What shipped

CT now works like NCrunch for agents:

- **The freshness key is `(generation identity, revision)`.** The identity ignores routine index writes. It flips only when the served generation really changes. A rebuild still marks every result stale.
- **The watermark carries green results.** An edit marks only the tests the change can reach as stale. Fresh green results the change cannot reach ride forward. Red results never ride. Unknown reachability always means stale.
- **Runs execute the stale set only**, as an explicit test-ID list. A truncated or degraded impact read means Unknown: everything stale, nothing executes, never a whole-suite fallback.
- **Changes trigger impacted runs automatically**, debounced (`MILLER_CT_DEBOUNCE`, default 2 s, trailing edge). Changes during a run queue a follow-up.
- **The family daemon adopts worktrees.** One daemon on the main checkout serves registered, opted-in worktrees of the same repo. Each worktree gets its own `ct.db` and index-bound context. Worktree stop detaches only that worktree.

Live validation (5 of 5 scenarios passed at `23e50e9c`, re-checked after fixes): an edit to one source file staled 1 of 2 cases, the daemon auto-ran that one case ~2 s later, and the verdict returned to green with no full run; a markdown edit kept green with stale 0; a store recreate mints a new view id, so a revision-counter replay stays stale (the false-green hole is closed); a worktree edit auto-ran in the worktree's own `ct.db`.

## External review (codex, pre-merge, subagent-driven)

**Passes:** general 7 findings / security 3. **Total after dedupe:** 9 (one dual-flagged).

**Fixed (8):** commits `c337c635`, `2ed2b3cf`, `3486861e`, `10bec231`, `c84a203e`.
1. Impact hints no longer vouch for the whole delta — per-path accounting runs on every selection (general, high).
2. Harmless-extension list narrowed to prose docs only: `.md`, `.markdown`, `.rst`, `.adoc` (dual-flagged, high).
3. No-run advances reconcile pending runs: KnownEmpty re-keys the owed run, Unknown drops queued work (general, high).
4. A store recreate always mints a new view id — the crash window between publication and its witness is closed (general, high).
5. A routed run must name a registry-registered worktree; a failed registry read refuses instead of authorizing (security, high).
6. A registry-removed worktree detaches on the next scan; a failed registry read detaches nobody (general, medium).
7. One malformed routed request gets a rejected ack instead of a persistent daemon crash loop (security, medium).
8. `MILLER_CT=off` status is now zero-work: no store, index, or budget reads (general, medium).

**Dismissed in part (1):** the security pass recommended a per-worktree execution-approval workflow. Reason: it contradicts the adoption design you approved (opt-in inherits through the git link, zero manual calls). The real gap under it — registration — is fix 5 above.

**Flagged for your review (1):** adopted-worktree status records carry no live run activity (writes happen on transitions only). Real value, but it reverses a documented Task 7 design tradeoff. Your call.

**Cost:** codex-cli does not report token counts; no figure available.
**Loud note:** this repo declares no external-model policy block. The codex dispatch ran on your explicit "approved with a final codex review".

```text
REVIEW CAMPAIGN STATUS
state: capped
evidence: external-reviewed
round: 2/2
external_invocations: 2/2
open_critical_high: 0
open_medium_low: 1
open_above_floor: 1
campaign_closed: yes
```

## Tests

Branch gate green at `032ba035`: scale 153 passed / 0 failed (12 environment skips), fast 7738 passed / 0 failed (27 environment skips). The merge was a fast-forward, so the merged tree is byte-identical to the gated tree — the evidence carries.

Two gate attempts before that failed on flakes, not on the change:
- One new review-fix regression raced the status write it asserted on. Fixed in `032ba035` (the test now polls).
- Pre-existing timing tests failed under back-to-back suite load (one ack-race test, then six clustered 5–10 s timeout tests). All green in isolation and on a quiet rerun. The CT daemon was checked during diagnosis: idle, not the load. The suite flake tail is a recorded follow-up.

Security scope: none declared in the plan.

## Judgment calls

1. Executed the review campaign's fix round with five serialized workers (one build tree — parallel builds collide on `obj/`/`bin/`). Every fix got a red-first regression and a lead inline review of the diff.
2. Fixed the racing regression test inline as the lead (two lines, product behavior proven correct) instead of dispatching a sixth worker.
3. Chose fail-closed semantics wherever the review left room: Unknown drops queued pendings; a failed registry read never authorizes and never detaches.
4. Did not rerun the gate after the fast-forward merge (unchanged tree, per CLAUDE.md).
5. Kept the worktree and branch in place after the merge (see Source control).

## Source control

- `main` is at `032ba035`, 22 commits ahead of `origin/main` counting the report commit. **Nothing pushed**, per your instruction.
- Worktree `C:\source\miller\.worktrees\ct-watermark-freshness` (branch `feat/ct-watermark-freshness`) is fully merged and clean. Kept on disk — remove with `git worktree remove .worktrees/ct-watermark-freshness` and `git branch -d feat/ct-watermark-freshness` when you are done with it.
- The stray checkpoint `.memories/2026-08-21/021442_ff1f.md` (written mid-plan on the main checkout) is committed with this report.

## Next steps

1. **Build and dogfood.** Main now holds the full CT watermark behavior. `dotnet build Miller.slnx -c Release` (stop the running MCP server first, or build Debug), then `tests start` and edit away.
2. **Your call — flagged finding:** should adopted-worktree status records carry live run activity? Reversing the transition-only tradeoff needs your decision.
3. Recorded follow-ups, none blocking: xunit THEORY rows still fail closed (Important, deferred); suite flake tail (timing tests under load); `WorkspaceRegistry.TryOpenReadOnly` maps `SqliteException` to null (the failed-vs-absent split lives in a caller probe with a benign race); an unknown-kind daemon command acks with reason `run`; serve/stop status casing (`alreadyrunning` vs `already_stopped`); split-refresh residual (a run selected at an intermediate revision does not commit at the final one; the next run heals it); Decision 2 (extraction epoch pointer owner) still open; CLI-opened fixture workspaces never appear in `workspace list`.
