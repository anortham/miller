# P4 shadow dogfood — three real workspaces + induced fault (Task 9 exit evidence)

**Date:** 2026-07-20 (evening, UTC 2026-07-21 00:50–01:25) · **Machine:** M2 Ultra ·
**Build:** branch `worktree-semantic-p4` at `9143fe6` (Release, framework-dependent) ·
**Sidecar:** v0.1.0-rc.2 (Metal) · **Mode:** `MILLER_SEMANTIC=shadow` per-invocation, never global.

Method: launch the branch server as leader on each already-registered real workspace, poll
`workspace status` from a SEPARATE CLI process (so every state observed is artifact-mediated, the
way a real reader sees it), sample sidecar RSS, record wall times and artifact sizes. One
workspace got a deliberate fault campaign.

## Per-workspace results

| Workspace | Symbols | Initial converge (wall) | vectors.db | Cursors at end | Notes |
|---|---|---|---|---|---|
| goldfish (TS) | 4,806 | **40s** | 2.2 MiB | sym 1/1, chunk 1/1, 0 errors | status walked `building 0%` → `ready (updating; 94 pending)` → `ready` |
| eros (C#) | 23,563 | symbols clean post-fault; chunks **starved** (finding 1) | 7.3 MiB | sym 606/606 clean; chunk 0/606 held | fault campaign below |
| julie (Rust) | 34,210 | **244s** | 9.4 MiB | sym 22/22, chunk 22/22, 0 errors | largest; zero errors end to end |

Artifact cost ≈ 0.3–0.5 KiB/symbol at 512d int8. Sidecar RSS: **1.33 GiB steady** while
embedding; **transient peaks ~4.8 GiB** repeatedly sampled during eros's initial symbol converge
(see follow-up 3).

## Induced fault (eros): circuit-open self-reports and self-clears

- `kill -9` on the sidecar three times mid-`embed_batch` exhausted the session's restart budget:
  **`circuit-open` appeared in `workspace status` from a separate process 16s after the first
  kill**, reason `"semantic sidecar disabled after 3 consecutive failures: sidecar stdout closed
  while awaiting 'embed_batch'"` — path-free per the scrubbing rule, actionable, artifact-mediated
  (P4 Task 1 proven in production).
- Relaunched server: symbol cursor converged 606/606, pause **cleared** (`ready`, reason null) —
  the recovered⟶clear edge works across processes.

## Second-generation rebuild + GC observation (goldfish)

- `workspace full` forced a new `artifact_id`; the drain escalated `ArtifactIdChanged` → shadow
  rebuild → **"Promoted a shadow vector generation with 1048 embedded symbol cards (0 flagged)"**.
- A server killed mid-shadow-build left a partial `vectors.db.rebuild` trio; the next leader
  **reclaimed it** and completed the rebuild — crash-debris recovery works.
- `retained_generations: []` after the promote — CORRECT: retention protects *incompatible
  identity* upgrades (design §5.1); a same-identity artifact rebind retains nothing, so the GC
  pass had nothing to collect. Production observation of a retained-generation deletion therefore
  awaits a real identity change (P5/P6); that path stays proven at the unit level (T5 tests).
- During the rebuild the old generation kept serving `ready` — the designed rollback behavior.

## `miller semantic prepare` UX

Cached-model run: sha256 re-verification of the 1.1 GiB weights in 3.3s, machine-readable
`done` event, exit 0, marker created before spawn and gone after — the Task 3/4 contract works
against the real sidecar. (A cold-download run was not exercised; both pinned models were already
cached from P0.)

## Off-switch proof (accidental but real)

The first goldfish run launched the server with semantic OFF by mistake (env prefix bound to the
wrong pipeline element). Across 20 minutes including a full extractor-upgrade rescan, the server
did **zero** vector work — no sidecar spawn, no artifact, no log lines — while the shadow-mode
CLI correctly reported `unavailable` with the refresh remediation. `MILLER_SEMANTIC=off`
zero-work guarantee held under a heavy real workload.

## Findings and follow-ups

1. **Chunk-cursor starvation on partial hash disagreement (severity: medium — fix before P5
   default-on; does not block shadow).** On eros, 3 sources deferred for hash disagreement pinned
   the chunk cursor at 0/606 with "659 files pending" indefinitely: the planner correctly embeds
   the agreeing chunks but holds the cursor (`AdvanceTo=0`,
   `VectorConvergePlanner.cs` deferral branch), and on a quiet workspace **no future converge
   signal ever arrives** to retry. Status honestly shows the held error in JSON, but compact
   `ready (updating; N pending)` reads as progress that will never come. Candidate fixes:
   a retry wake when a hold reason is stamped; or advance past agreeing sources and track the
   deferred set separately; or trigger re-extract of disagreeing sources.
2. **Deferred sources are not named anywhere (severity: low).** The stored reason is path-free by
   contract (scrubbing rule), but the leader's LOG doesn't name the 3 deferred files either —
   diagnosing which sources disagree requires manual artifact spelunking. Add an INFO log line
   with the paths on the leader.
3. **Sidecar RSS peaks ~4.8 GiB during large initial converges (severity: monitor).** Steady
   state is 1.33 GiB, but eros's initial symbol converge repeatedly sampled 4.3–4.8 GiB resident.
   Likely KV/context allocation for max-token card groups. Matters for the multi-process swarm
   RAM budget (P3 gate work); worth a sidecar-side ceiling check before P5.
4. **Compact status shows plain `ready` while a shadow rebuild runs (severity: low, polish).**
   JSON carries everything; the compact line could hint `ready (rebuilding)` so operators don't
   read a long rebuild as idle.
5. Harness-only lesson re-learned: `VAR=x cmd1 | cmd2` binds the env to cmd1 (zsh/bash) — cost
   one 20-minute no-op run; documented in the zsh-gotchas memory pattern.

## Go/no-go facts for the P5 canary decision

- Initial converge wall time scales roughly linearly (~40s per 5k symbols on M2 Ultra Metal,
  chunks included) — acceptable for existing-user onboarding in shadow.
- Pause states are trustworthy across processes: induced circuit-open surfaced in 16s and cleared
  on recovery; nothing required manual artifact surgery at any point.
- Disk cost is negligible (≤10 MiB per 35k-symbol workspace + model cache shared machine-wide).
- RAM steady-state (1.33 GiB) is the real fleet cost of the f16 default tier; the bge fallback
  measured 196 MiB (see [2026-07-20-q8-footprint-benchmark.md](2026-07-20-q8-footprint-benchmark.md)) —
  the footprint decision materially changes the swarm story.
- **Recommendation:** shadow is safe to leave on for real workspaces now; fix finding 1 (and
  ideally 2) before the P5 randomized canary so chunk coverage cannot silently starve.

## Cleanup

Real registered workspaces only (no scratch): goldfish/eros/julie keep their valid shadow
artifacts (inert unless a server opts in; `MILLER_SEMANTIC` unset = off). `workspace prune
--dry-run` → would prune 0, kept 52. No dogfood servers or sidecars left running.
