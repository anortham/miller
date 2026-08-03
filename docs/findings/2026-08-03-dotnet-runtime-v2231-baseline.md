# dotnet/runtime cold-scan baseline — julie-extract v2.23.1 (2026-08-03)

The clean, uncontended cold-scan baseline on a large real repo. This document is the control for
the #18 resolver-decay investigation and the before-number for the progressive indexing levels
program (P3 validation compares against these figures).

## Setup

- Box: Apple Silicon (M2 Ultra class), 64 GiB RAM, idle during the run (no concurrent worktree
  fleet, no other extractors — the deliberate contrast with the contended 2026-08-01 field run).
- Binary: released `julie-extract 2.23.1` (Miller's pinned `.tools` copy).
- Repo: dotnet/runtime @ `a2f953fe266`, read-only clone. 58,366 files scanned; 41,406 indexed;
  16,960 unsupported; 8 failed (non-UTF-8 XML test data — expected, `recoverable: true`).
- Command: `julie-extract scan --root <clone> --db <fresh path> --jobs 4 --json`, wrapped in
  `/usr/bin/time -l`, with a 2s sampler (db/WAL/spool bytes, RSS, CPU time) and a 5s `sample`
  profile every ~3 min. Exit code 1 with `status: partial` is the by-design outcome for the 8
  read failures.

## Headline numbers

| Metric | Value |
| --- | --- |
| Wall clock | 4,580 s = **76.3 min** |
| CPU time | 2,570 s user + 676 s sys = 3,246 s (≈ 71% of one core averaged) |
| Peak RSS | **19.6 GiB** |
| Artifact size | 22.84 GiB (`runtime.db`) |
| Symbols / identifiers / reference sites | 2.58 M / 12.86 M / 15.5 M |
| Identifier resolutions | 12.86 M (276 k pending) |
| Warnings | 30 `slow_file_skipped`, 28 `reference_site_payload_conflict` |

The contended-field-report projection was 45 min–multiple hours; the honest uncontended number is
~76 min — still far past what anyone will wait on, which is the levels program's premise.

## Phase partition (from the sampler + per-language profile)

| Phase | Window | Share | Evidence |
| --- | --- | --- | --- |
| Discovery + extraction + spool | 0–210 s (~3.5 min) | 5% | spool peaks at 3.19 GiB at t+210; CPU duty 159% (4 workers); per-language `extract_duration_ms` sums to only ~490 s of worker time (C# 401 s, C++ 58 s) |
| Bulk load (spool → artifact) | 210–~950 s (~12 min) | 16% | db 0 → ~12.1 GiB, ~17 MB/s sustained |
| **Identifier resolution** | ~950–4,177 s (**~54 min**) | **70%** | single-threaded, ~60% CPU duty (40% blocked), db creeps +2.5 GiB |
| Finalize / index build | 4,177–4,580 s (~6.7 min) | 9% | db 14.6 → 22.84 GiB (index b-trees), `pread` + `vdbeRecordCompareString` frames |

Extraction — the part people picture as the cost — is 5% of wall. Resolution alone is 70%.

## Resolution decay curve (#18 control)

db growth per ~458 s decile inside the resolution phase decays steadily while CPU duty holds
~60–63%: +0.44, +0.43, +0.41, +0.32, +0.28, +0.26, +0.22 GiB. Throughput halves across the phase
with no corresponding CPU change — the per-batch cost is growing, matching the #18 decay claim on
a clean box (so the decay is intrinsic, not contention).

## Profiler verdict — three regimes inside the write phase

5 s `sample` snapshots, top-of-stack counts of ~4,200:

- **Early resolution (t+1227 s):** `pread` 3,182; `sqlite3BtreeTableMoveto` 298; `pcache1Fetch`
  143 — read-bound b-tree probes. The bulk connection's page cache is
  `SQLITE_BULK_CACHE_SIZE_KIB = -131072` (128 MiB) against a 12+ GiB artifact: near-100% miss
  rate on random symbol lookups.
- **Mid resolution (t+2128 → t+3930 s):** `pwrite` 2,902 → 3,894 and `memjrnlTruncate` 1,095 →
  509 → 45 — write-bound. Dirty pages overflow the same 128 MiB cache and spill mid-savepoint,
  and the in-memory statement journal (`temp_store=MEMORY` + the resolution SAVEPOINT) churns
  through `memjrnlTruncate`. RSS ~18 GiB in this window with only a 128 MiB page cache says the
  journal pre-images, not the cache, hold the memory.
- **Finalize (t+4471 s):** `pread` 3,396 + `vdbeRecordCompareString` — index construction reads.

Both prior #18 hypotheses (cache thrash; memjrnl/savepoint overhead) are confirmed, and they are
facets of one root cause: **the 128 MiB bulk-connection cache is dimensioned for the write path
but the resolution phase does random reads and re-dirtying over a multi-GiB working set through
it.** The first experiment is therefore cache sizing (single variable), not savepoint surgery.

## Experiment ladder (same repo, same box, same argv; run same day)

Two env-gated experiment knobs on the `resolver-decay` worktree branch (commits `d940e0c`,
`7ffaa3c`; defaults unchanged):

| Run | Wall | User CPU | Sys CPU | Peak RSS | vs baseline |
| --- | --- | --- | --- | --- | --- |
| Baseline (128 MiB cache, whole-pass savepoint) | 76.3 min | 2,570 s | 676 s | 19.6 GiB | — |
| Exp 1: 8 GiB bulk cache | 47.0 min | 2,487 s | 105 s | 29.2 GiB | **1.62×** |
| Exp 2: + skip whole-pass savepoint | **18.8 min** | 1,153 s | 70 s | 30.1 GiB | **4.05×** |

All three artifacts are equivalent: identical byte size (24,524,304,384), identical row counts,
and a full logical check on Exp 2 — identical `identifier_resolutions`
outcome/tier/method/confidence distributions, aggregates, and `pending_resolutions` count.

- **Exp 1 (cache):** resolution went from ~54 min at 60% CPU duty to ~22 min at ~93% — the
  syscall storm vanished (sys 676 → 105 s) and the decay curve flattened. It also unmasked the
  next bottleneck: `memjrnlTruncate` became ~82% of resolution samples.
- **Exp 2 (no savepoint):** the v2.9.0 `ResolutionWriteBuffer` batching bounded statement-ends
  for that era's scale, but 12.86 M identifiers ÷ 500-row flush chunks ≈ 30 k statement-ends,
  each truncating the whole-pass `SAVEPOINT resolution_hook` sub-journal by walking its multi-GiB
  chunk list — quadratic again. Without the savepoint, resolution collapses to minutes and the
  profile shows the actual resolver algorithm (`tier_candidates`, candidate memcmp, the #17
  metadata BTreeMap) instead of journal bookkeeping. User CPU fell by ~1,330 s — that was pure
  `memjrnlTruncate`.

The experiment savepoint skip was measurement-only (error rollback is unsound in that mode). The
shipped 2.24.0 shape keeps resolution inside the bulk transaction but savepoint-free: on a bulk
FIRST BUILD a hook error aborts the whole scan (`BulkResolutionFailed`, empty artifact
discarded, rerun rebuilds) — nothing durable exists to protect, so the savepoint bought nothing
but the quadratic. WAL delta paths keep the savepoint and the contained "rows stay unresolved"
failure semantics unchanged.

## Production validation (2.24.0 implementation, same repo/box/argv, no env knobs)

The shipped three-part package (memory-aware cache = total/8 clamped [512 MiB, 8 GiB];
savepoint-free bulk resolution; three retired indexes) ran the same scan in **23.7 min wall**
(1,239 s user + 108 s sys, 25.7 GiB peak RSS) producing a **20.41 GiB artifact** — 2.43 GiB
smaller, all 24 row-count domains and the resolution aggregates identical to baseline, retired
indexes absent, FK-supporting indexes present. The profile is clean: resolution shows the
resolver algorithm (`IdentifierLocator::locate`, candidate memcmp), zero `memjrnlTruncate`.
The gap vs Exp 2's 18.8 min is environmental (lower CPU duty at equal phases on the fourth
consecutive full scan; no new bottleneck frames) — treat the production number as **3.2–4.1×**
run-to-run.

## Implications

- The julie-extractors fixes shipped as described above; remaining wall is bulk load (~6–7 min)
  and index finalize — future levers, not blockers.
- A ~19 min cold scan on a 41 k-file repo still fails the "nobody waits on an index" bar —
  the levels program remains the product answer; these fixes make every level's background
  convergence ~4× cheaper.
- This unblocks #15 (was blocked on #18) and closes #18's investigation: root cause found,
  fix validated at scale, decay curve explained (cache-miss growth, not an algorithmic decay in
  the resolver itself).
- Raw evidence (report.json, time.txt, samples.csv, profsamples/) for all three runs captured in
  the session scratchpad; this document records everything decision-relevant.
