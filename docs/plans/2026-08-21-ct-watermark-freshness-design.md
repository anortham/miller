# CT watermark freshness — design

Date: 2026-08-21. Status: approved by the user on 2026-08-21 (freshness policy, worktree adoption, and debounced auto-run).
Decision 1 from `docs/plans/2026-08-20-ct-dogfood-defects-and-2344-pin.md` was decided by the user
on 2026-08-21: **watermark keep-set**.

## Goal

Make CT behave like NCrunch for agents:

- An edit marks only the tests that the change can reach as stale.
- A change triggers a run of exactly those tests, after a short debounce.
- A green result survives every edit that cannot reach its test.
- A full-suite run happens only when the served index generation really changes.

Running the whole suite after every edit is worthless as a feature. Agents already do that
too much; CT exists to stop it.

## Evidence this design rests on

Live dogfood on 2026-08-20/21 (merged main, version 1.20.1+5a0585400ab9):

- A 3-file test edit marked all 7,690 results stale. A clean 7,588-case run then finished
  with 0 failures — and every result was stale on arrival, because the index identity had
  moved during the run. Verdict stayed `partial`. A full green run bought nothing.
- Cause chain (diagnosed in the 2026-08-20 plan, confirmed live):
  1. `WorkspaceReadSnapshot.IndexIdentity` joins the whole store cursor, including
     `store_log_sequence`, which moves on **every** index write (six counts for one file save).
  2. `ContinuousTestDurableFreshness.IsCommittedFreshAt` requires identity **and** revision
     to match. Any write anywhere kills every result.
  3. The carry-forward machinery (`AdvanceContinuousTestFreshWatermark`,
     `IsWatermarkFreshAt`) exists and is tested, but has zero production callers.
  4. `ContinuousTestImpactSelector.Select` computes a ranked impacted set — and then sets
     `staleIds` to every case in scope (`ContinuousTestImpactSelector.cs:97-101`).
  5. The daemon did not auto-run when files changed; it sat idle until an explicit run.
- `TestsCore.SelectedFrom` reads the reported key from `ct.db`'s own rows. The guard is
  self-referential: without an automatic staleness signal it would read green forever.
  It also caused the status field flip seen live (rev 32424 in one read, 32161 in the next).

## Design

Ordered; each step is safe only after the one before it.

### 1. Reported key comes from the live index

`TestsCore.SelectedFrom` stops deriving the selected key from stored rows. Status asks the
live `WorkspaceReadSnapshot` for the current key and compares stored rows against it. This
lands first: removing automatic staleness while the guard is self-referential produces a
false green.

### 2. `IndexGenerationIdentity` — an identity that ignores routine writes

A new property beside `IndexIdentity`. It changes only when the served generation really
changes: generation promotion, store view or family change, extractor upgrade, schema
heal. It does **not** include `store_log_sequence`, the revision counter, the manifest
hash, or the manifest generation number — the live store proved all of those move on
routine delta imports. Family-mode clarification (Task 1 evidence): an IN-PLACE store
full import flips no identity component and keeps the revision counter monotonic; its
invalidation rides the manifest delta / impact path, with the unknown-outcome fail-closed
rule as the backstop. Every event that can restart or reuse the revision counter flips a
component.
`CtFactAdapter.cs:49` is the only place CT takes its identity, so the swap is contained.

CT result freshness key becomes `(IndexGenerationIdentity, revision)`:

- Generation changed → every result is stale. The rebuild fail-safe stays absolute.
- Same generation, revision advanced → the watermark (step 3) decides per test.

### 3. Wire the watermark

On each revision advance from `R0` to `R1` with changed files `F`:

- Run `ContinuousTestImpactSelector` over `F` to get the impacted set `I`.
- Keep-set = all currently fresh green cases not in `I` — fresh by commit **or** by an
  earlier watermark, so a second unrelated edit keeps carrying what the first kept. Call
  `AdvanceContinuousTestFreshWatermark` for the keep-set to `R1`.
- Cases in `I` go stale. A case whose reachability is unknown is treated as impacted.
  Unknown always means stale, never fresh.
- Only green results ride the watermark. A red stays red until its test reruns.
- `IsWatermarkFreshAt` joins `IsCommittedFreshAt` in verdict and staleness computation.

### 4. Stale set = impacted set; runs execute the stale set

`ContinuousTestImpactSelector.Select` stops returning `staleIds = everything`. Stale is
what the watermark did not keep. A run executes the current stale set. `verdict=green`
still requires: zero stale cases and zero red results at the current key — that
definition does not change.

### 5. Changes trigger impacted runs, with a debounce

The daemon runs the impacted set automatically when changes arrive (this is the documented
contract already; live dogfood showed it does not fire — find and fix why as part of this
work). A debounce coalesces save bursts: the timer resets on each new change and the run
starts after a quiet period. Debounce duration is an env-tunable constant
(`MILLER_CT_DEBOUNCE`, seconds; default in the low single digits — exact default picked
during implementation against the existing revision-poller cadence). Changes that arrive
during an executing run queue a follow-up selection; they never kill a healthy run.

### 6. The CT delta seam passes the family id

Step 4 from the 2026-08-20 plan, unchanged: pass the family id on the CT side without
touching `RevisionDeltaReader.cs`.

## CT on worktrees (user decision, 2026-08-21: daemon adopts)

Today a linked worktree is a separate workspace with its own `.miller/`, so CT is off there
and an agent gets nothing without three manual calls. That defeats the agent workflow the
tool exists for. Design:

- **Enablement inherits through the git link.** A linked worktree resolves its main repo
  via `GitWorktreeLayout` (no `git` subprocess). If the main checkout has `.miller/ct.enabled`,
  the worktree counts as enabled. The explicit opt-in covers the repo, not one root.
- **The running daemon adopts family worktrees.** The user's explicit `tests start` covers
  the repo family: when a worktree of the same repo registers as a workspace, the running
  daemon serves it too — status, selection, and runs keyed to that worktree's own index and
  `ct.db`. No new daemon process per worktree.
- **Budget is unchanged.** The user-global one-execution budget applies across worktrees;
  N worktrees never mean N concurrent suites.
- **Safety rules unchanged.** Status reads still never create `ct.db` or start anything;
  `MILLER_CT=off` still means zero work everywhere; adoption never fires when the repo has
  no explicit enable + start.
- **Result seeding across worktrees is out of scope.** `ct.db` keys files by blake3 hash,
  so inheriting greens for identical files is possible later, but it is soundness-sensitive;
  a new worktree starts with a fresh run.

Worktree acceptance criteria:

- [x] With CT enabled and the daemon running on the main checkout, registering a worktree
      of the same repo gives `tests status` on that worktree an honest enabled/adopted
      answer without any manual enable. (Task 9 live scenario 8: `enabled=true`,
      `reason=adopted by <main root>`, status read created no files.)
- [ ] A change in the worktree triggers an impacted run against the worktree's index,
      debounced, under the shared budget. (Task 9: the family daemon observed the worktree's
      revision advance against the worktree's own index, but the selection returned Unknown —
      defect 1 in the task-9 report — so the impacted run never fired. Unproven until that
      defect is fixed.)
- [x] A worktree of a repo that never enabled CT stays fully off. (Task 9 live scenario 8:
      `enabled=false`, no `ct.db`, no `.miller/ct/` created.)
- [x] Removing the worktree detaches it without disturbing the main workspace's CT state.
      (Task 9 live scenario 8: worktree `tests stop` reported `detached`, `git worktree remove`
      left the main daemon running and the main `ct.db` untouched.)

## Safety invariants (unchanged from today)

- Status reads never create `ct.db`, never start the daemon.
- Delta-unavailable or degraded index never falls back to a full-suite run.
- `MILLER_CT=off` stays a permanent zero-work guarantee.
- Green requires complete results at the selected key. Partial coverage is `partial`.
- Revision alone is never a freshness key (rebuild restarts the counter).

## Acceptance criteria

- [ ] Editing one non-test source file marks only the impacted cases stale; the daemon
      runs only those after the debounce; verdict returns to green without a full run.
      (Task 9 live scenario 1 FAILED: the index named the impacted test correctly, but the
      selector could not map it to the stored xunit.v3 case — defect 1 in the task-9 report —
      so the outcome was Unknown: both cases stale, no run.)
- [x] Editing an unrelated markdown file (index writes, no reachable tests) leaves the
      verdict green and stale at 0. The watermark advanced instead. (Task 9 live scenario 2:
      `outcome=KnownEmpty stale=0`, watermark rows advanced, states unchanged, no new run.)
- [ ] A full rebuild (generation change) marks every case stale. (Task 9 live scenario 3:
      a store-mode `workspace full` keeps the generation identity by design; a store RECREATE
      reused family+view+gen-001 with a restarted counter and later resurrected stale greens —
      defect 4 in the task-9 report.)
- [ ] A red result never becomes green or fresh without its test rerunning. (Task 9 live
      scenario 4 proved the watermark path: the red case never rode the watermark and turned
      green only after its rerun. But defect 4's counter replay can make ANY stored row —
      red included — read fresh without a rerun, so the universal claim is unproven.)
- [x] A case with unknown reachability is stale after any change. (Task 9: observed live
      twice — `outcome=Unknown` staled everything and executed nothing; fail-closed pins in
      ForbiddenEnqueueTests and DurableFreshnessTests.)
- [x] Status reports the live index key; two consecutive reads never flip between keys.
      (Task 9: consecutive reads returned the identical `ctgen1:` key; the key stayed live
      even when every stored row carried a legacy identity.)
- [ ] Explicit `tests run` executes the stale set only. (Task 9: the run selected exactly
      the stale set, but at the daemon's START key rather than the live key — defect 2 in the
      task-9 report — so results landed at a dead revision and the verdict never converged
      without a daemon restart.)
- [ ] All existing CT safety gates (status-starts-nothing, budget, stall kill) still pass.
      (Lead-owned branch gate; task 9 observed status-starts-nothing, the kill switch, and the
      shared budget live, but the suite-level claim belongs to the branch gate.)

## Out of scope

- Decision 2 (extraction epoch pointer owner) — separate decision, still open.
- Coverage-hash freshness (`ct_coverage_maps` population) — a later precision upgrade;
  the watermark does not depend on it.
