# 2026-08-02 — Cross-model review of the fleet-safety branch

**Scope:** the eight commits on `fleet-safety` implementing W1, W2, W3, W7, W8, W9 of
[`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`](../plans/2026-08-01-multi-worktree-fleet-safety-plan.md)
(94 files, ~13k insertions). Too large for one pass, so it was split into four units and each unit was
reviewed independently and adversarially by **Codex** (gpt-5.1-codex-max) and **Grok** (grok-4.5) —
eight reviews, read-only, structured output.

| Unit | Commits | Focus |
|---|---|---|
| A | jobs cap, bootstrap lock, unfinished-artifact/ops-gate/ignore fixes | bootstrap lifecycle + locking, `--jobs` on every path |
| B | machine-wide scan governor | admission holding, crash recovery, refusal honesty |
| C | ignore propagation, linked-worktree watcher + root identity | seeder races, HEAD attach, path reuse |
| D | persisted scan-failure policy + scan intents | repair isolation, downgrade semantics, retry liveness |

**Result: 15 findings raised, 4 confirmed and fixed, 1 confirmed-but-narrower-than-claimed (comment
corrected), the rest recorded below.** Every finding was re-verified against the code before being
accepted; the confirmed ones were each mutation-proven.

---

## Confirmed and fixed

**1. Machine-wide admission was still held while blocking, at four of five governed sites.**
*(codex/A high 0.99, codex/B high 0.98, grok/B high 0.93 — three independent hits.)*
An earlier round bounded the on-demand path only. The debounce drain, startup delta, extractor-upgrade
rescan, and leader-requested full scan all still took admission and then blocked on an unbounded
`lock (_opsGate)`, which the governor-exempt per-file write-through can hold arbitrarily long — sitting
on the one-at-a-time machine-wide lease and refusing every sibling worktree. The fleet starvation the
governor exists to prevent, inverted. All five now share one bounded helper. Fixed in `de8c30d6`.

**2. A successful weaker scan left an already-expired throttle in place.** *(codex/D high 0.99.)*
`RecordSuccess` correctly refuses to let a delta discharge an owed force — but returned without
touching the record, and the delta was only admitted because `next_attempt_at` had already elapsed. So
the next automatic cross-workspace read was admitted too, and the one after that, each spawning a
whole-repo scan: the extractor storm the persisted backoff exists to stop, reached through the success
path rather than the failure path. Now re-spaced at the same streak, and only when the deadline has
actually passed. Fixed in `de8c30d6`.

**3. A transient read failure permanently swapped a worktree's ignore policy.** *(codex/C high 0.99,
grok/C medium 0.72.)* The seeder fell back to the generated baseline when the main checkout's
`.julieignore` could not be read. The create is exclusive, so that file is never revisited — one
transient error permanently replaced the repository's ignore rules with a generic baseline, and the
worktree then indexed everything the repo deliberately excludes, silently. An existing-but-unreadable
source now seeds nothing, leaving the failure retryable. Fixed in `cbc4a3d2`.

**4. Miller's own extract hard cap was smaller than a healthy scan.** Not raised by either reviewer —
found by the W10 measurement running alongside. See
[`2026-08-02-w10-scale-repro-wal-measurement.md`](2026-08-02-w10-scale-repro-wal-measurement.md) §6a.
Fixed in `de8c30d6`.

## Confirmed narrower than claimed

**5. "A workspace marked missing can still be scanned through other entry points."** *(codex/C high
0.99.)* The gap is real — the presence flag suspends the debounce path only, and an on-demand
`workspace refresh|full` does not consult it. The predicted consequence is not: measured against the
pinned julie-extract 2.21.0, a scan with a missing `--root` exits 1 and leaves the artifact
byte-identical (40 files before, 40 after), so nothing is deleted. What remains is a doomed extract
that wastes a machine-wide admission. Gating those paths was considered and deliberately not done —
only the debounce tick refreshes the flag, so a root that has just returned would read as missing and
a legitimate user rebuild would be refused for a stale reason. Miller's own comment claimed the
data-loss outcome and was corrected to state the measured one (`cbc4a3d2`).

## Recorded, not acted on

Each was checked in code; none justified a change on this branch. Listed so they are not rediscovered
from scratch.

| # | Finding | Why not acted on |
|---|---|---|
| 6 | Path reuse completing inside one 250 ms presence poll is never detected *(codex/C 0.94, grok/C 0.90)* | Known and documented in `WorkspaceRootPresenceMonitor`'s own summary. Narrowing the window means comparing identity on every tick, which reads an ordinary branch switch as a new checkout on any filesystem with no birth time — a whole-repo rebuild for nothing. The re-created checkout's file storm still overflows the watcher and forces a reconcile. |
| 7 | Dashboard overrides create a second independent governor *(codex/B 0.99)* | Requires manually pointing `MILLER_REGISTRY_DB` outside `<home>/.miller`, which already re-points the registry itself — a visibly broken configuration. On the launcher path and the default path the dashboard and server resolve the same `…/.miller/scan`. |
| 8 | `HasCommittedRevision` swallows corruption instead of forcing a heal *(codex/A 0.96)* | Deliberate and documented: the caller's contract is that no artifact read may fail the bootstrap, and here the safe answer costs one delta scan. Corruption healing has its own intent and entry point. |
| 9 | A journal write failure disables the only retry timer *(codex/D 0.99, grok/D 0.82)* | Real, and bounded by the same disk-full/permission conditions that would already be failing the scan. Fixing it well means an in-memory fallback record with its own cross-process semantics — a design change, not a patch, and out of scope for this branch. |
| 10 | Automatic `UserFullRebuild` retries are downgraded indefinitely *(codex/D 0.99)* | This is the designed behavior: only a user-initiated `workspace full` may run the expensive rebuild, automatic paths serve the prior artifact with degraded freshness. The reviewer is right that `DescribeDowngrade`'s "it retries automatically" oversells it. Wording, not mechanism. |
| 11 | Bootstrap always bypasses the persisted backoff *(grok/D 0.90)* | Bootstrap is one of the documented direct-user carve-outs. N fresh processes each bypassing once is bounded by the governor to one scan at a time. |
| 12 | Cross-workspace MCP renders scan-admission refusal with a writer-lock diagnostic *(grok/B 0.90)* | Real honesty gap, same class as one already fixed for the current-workspace path. Needs a new refusal status plumbed through `WorkspaceRefreshStatus`; worth doing, but it is a contract change and belongs with the `cli-eros-v1` surface rather than tacked on here. |
| 13 | Governor refusal reports arbitrary existing files as readable indexes *(codex/B 1.0)* | Same area as 12. |
| 14 | "Queued" scans are promised durably but latched only in memory *(codex/B 0.99)* | Accurate: the latch is in-process and a crash loses it. The next scan's freshness check reconciles, so the guarantee is eventual, not lost. The wording could say less. |
| 15 | Exclusive create can strand an empty or truncated `.julieignore` *(codex/C 0.98)* | A crash between create and flush leaves a short file that is never rewritten. Same shape as 9 (needs write-temp-then-rename), same reason for deferring. |

---

## Notes on the method

- **Splitting by workstream was necessary and sufficient.** An 845 KB diff in one prompt would have
  been read shallowly; four units of 50–320 KB each produced findings with real line numbers and
  traced call paths.
- **Convergence was the strongest signal.** The one finding three reviewers hit independently (#1) was
  real and the most severe. Single-reviewer high-confidence findings were right about half the time.
- **Confidence scores were not calibrated.** Four findings carried ≥0.98 confidence and did not survive
  verification; #5 carried 0.99 and was half right. The scores are worth reading as "how sure the model
  sounds", not as a triage order.
- **The most common false positive** remains a guard that exists just outside the reviewed diff — the
  reviewer sees the unguarded call and not the caller that already checked.
