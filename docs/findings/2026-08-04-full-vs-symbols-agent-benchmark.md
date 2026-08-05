# Full versus symbols-only frozen agent benchmark — 2026-08-04

## Decision

Keep `full` as the default for every checkout and retire the `progressive` default. `symbols-only` remains an
explicit opt-in (`MILLER_INDEX_LEVELS=symbols-only` or `miller workspace levels --set symbols-only`) for
storage-lean worktrees. A symbols-only default for linked worktrees was drafted from this data and reverted in
review: forced rescans (extractor-upgrade, schema-heal) would have silently downgraded existing full worktree
artifacts — losing rename, trace refs, impact, and patterns without user action — and the approved delta-rebind
program (`docs/plans/2026-08-02-worktree-delta-rebind-program.md`) addresses the same worktree cost without
giving up the reference layer.

This is a storage and indexing decision, not a claim that every symbols-only read is faster. The frozen harness
passed correctness but failed its relevance and strict efficiency gates.

## Method

- Frozen visible calibration corpus: 15 tasks over Goldfish, Eros, Razorback, tree-sitter-razor, and
  tree-sitter-c-sharp at the checked-in snapshot commits.
- Agent runtime: Codex CLI 0.145.0, `gpt-5.6-sol`, reasoning `medium`, seed 731, 8-call / 12,000-output-token /
  120-second limits.
- Paired prepared clones used the same Miller 1.16.1 development binary and fully converged semantic vectors;
  only the extraction level differed.
- The product identity hashes the compiled `miller.dll`, not the unchanged framework-dependent apphost launcher.
- First-pair disagreements expanded to the protocol-required three repetitions per arm: 54 immutable arm runs.

## Storage

Across the five prepared repositories, `.miller` storage was 610,764 KiB at full level and 259,684 KiB at
symbols level: **351,080 KiB / 57.5% smaller**. Definitions, symbol counts, search, and relationship edges remain;
the identifier-resolution, source-region, and structural-fact layers account for most of the removed bytes.

## Correctness result

| Gate fact | Full | Symbols-only |
|---|---:|---:|
| Correct tasks | 9 | 9 |
| Wrong-action tasks | 2 | 1 |
| Median tool calls on both-correct tasks | 5.0 | 5.0 |

- Correctness: **pass**.
- Critical losses: **0**.
- Stable completion: 8 both correct, 1 full-only, 1 symbols-only, 5 neither.
- The original critical call-path loss (`dev-010`) was a consumer contract bug: symbols-level `inspect` returned
  the exact relationship caller at `src/scanner.c:357`, but labeled the whole result `expected_empty` and emitted
  relationship kind `calls` instead of canonical `call`. After repair, both arms passed the task on the first
  repetition with 5 calls and zero wrong actions.

## Non-passing gates

- Relevance: **fail**. Full MRR / recall@6 were 0.577 / 0.526; symbols-only were 0.500 / 0.449.
- Strict efficiency: **fail**. On the 8 both-correct tasks, symbols-only used 5,511.5 median output tokens versus
  5,199.5 for full, and p75 wall time was 60.84 s versus 57.04 s. The call medians were equal.
- Action verdict: **fail**, because the benchmark requires correctness and efficiency together. One non-critical
  symbols-only answer was factually complete from a single `context` call, but that task's frozen acceptable-action
  list recognizes `inspect`/workspace-recovery actions rather than `context`.
- This is a calibration run (`decision=not_decisional`), not the sealed takeover lane.

## Evidence

- Full 15-task safe aggregate:
  `/Users/murphy/bench/runs/levels-full-after-inspect/exports/safe-aggregate.json`
- Targeted repaired call-path aggregate:
  `/Users/murphy/bench/runs/levels-dev10-after-inspect/exports/safe-aggregate.json`
- Cold indexing benchmark:
  [`2026-08-04-index-levels-indexing-benchmark.md`](2026-08-04-index-levels-indexing-benchmark.md)
