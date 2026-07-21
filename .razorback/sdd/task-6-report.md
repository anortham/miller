# Task 6 — Real-artifact adopter-cost measurement

> **STALE-CONTENT COLLISION (flagged):** this file previously held a *P5-era "Content-route canary"*
> report from worktree `worktree-semantic-p5` (HEAD `ceb8dd8`) — an unrelated Task 6 from a different
> plan. It has been overwritten with the correct Task 6 (Real-artifact adopter cost) from
> `docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md`. The stale `task-6-brief.md` was likewise
> regenerated from the encoder-comparison plan.

## Worktree state (verified)
- Repo edits: `/Users/murphy/source/miller/.claude/worktrees/fusion-v2-eval` — branch
  `worktree-fusion-v2-eval`, HEAD `c3f7e58`.
- Measurement target (untouched by edits): frozen-miller at
  `/private/tmp/claude-501/-Users-murphy-source-miller/df49671d-.../scratchpad/frozen-miller` — its own
  `.miller/`, own leadership. The live `/Users/murphy/source/miller/.miller/` and the user's live Miller
  sessions were NOT touched.

## Status: COMPLETE — both encoders measured (n=2 clean each + warm-query)

qwen3 and bge-small both fully measured. bge required commit `bf58afd` (sidecar `--model` forwarding) before
it could build — that bug was discovered by this task (see below). Findings §"Real-artifact cost (Task 6)"
filled: cost table with qwen3↔bge ratios, raw runs (incl. discarded), warm-query, gate amendment, and the
build-blocker write-up. **bge is far cheaper on every axis; the Task-5 pin move to bge-small is
well-supported on cost, conditional on `bf58afd` shipping.**

Gate history: two early qwen3 runs were discarded (overlapped real model-bench lanes). The lead then
replaced the loadavg<4 gate (miscalibration — ambient 6–10 on this 24-core box at ~77% idle) with a direct
criterion: no benchmark workloads live AND CPU idle ≥ 60%. All four clean runs pass that gate.

## qwen3 vs bge-small — adopter cost (medians, n=2 each)

| metric | qwen3-0.6b-f16 | bge-small-en-v1.5-f32 | bge advantage |
|---|---|---|---|
| E2E build (both cursors) | 312.7 s | 40.4 s | 7.7× faster |
| symbol throughput | 55 cards/s | 452 cards/s | 8.2× |
| sidecar peak RSS (holds model) | 12.34 GiB | 470 MiB | ~27× less |
| warm query (warm) | 4048 ms | 802 ms | 5.0× faster |
| model download | 1.198 GB | 133.6 MB | 9.0× smaller |
| `vectors.db` | 10.27 MiB | 8.89 MiB | 1.15× |
| host peak footprint | ~337 MiB | ~336 MiB | ~equal (model-agnostic host) |

The memory story is the **sidecar child**, not the `miller` host (`time -l` sees only the host, ~336 MiB
either way). bge's 470 MiB vs qwen3's 12.3 GiB resident footprint is the headline adopter-cost difference.

### Done
- Harness (`scratchpad/run.sh`) validated end-to-end: `MILLER_SEMANTIC=shadow` serve rooted at frozen,
  FIFO-held stdin (so the MCP stdio transport does not EOF-shutdown mid-build), cursor-completion
  detection via `vectors_meta` (`symbol_completed_revision`/`chunk_completed_revision` vs
  `*_target_revision` + `build_state=ready`), symbol-promote line + chunk-converge line parsed from
  `<frozen>/.miller/logs/`, host peak from `/usr/bin/time -l`, sidecar-child peak RSS sampled separately,
  `vectors.db` size, encoder-identity confirmation, and clean SIGTERM + vector-generation cleanup between
  runs.
- **Load guard** (lead instruction 3): each run records the gate reading; amended mid-task from loadavg<4
  to "no bench workloads + CPU idle ≥ 60%" (see Concerns).
- Non-timed facts captured: model download sizes; vectors.db size and card/chunk counts; served-model
  identity confirmation for both encoders.
- Two harness safety bugs found and fixed (see Concerns); the load-bearing bge build bug discovered and
  handed to the lead (fixed in `bf58afd`).

### Discarded (kept raw, never averaged)
- **qwen3-run1**, **qwen3-run2** — ran inside the real model-bench window (contention).
- **bge-clean-1 (pre-fix)** — build-blocked by the `--model` bug (the discovery), not a protocol failure.

### Remaining
- None. Both lanes measured; findings + report filled. Commit of owned files pending commit-mode
  clarification with lead (dispatch said `parallel-lead-commit`, later note said `serial-worker-commit`;
  the findings doc is co-owned with Task 5).

## qwen3 (default) — COMPLETE, clean, gate-valid (n=2)
Both runs: no bench workloads, CPU idle 84%/82%. Default identity `MILLER_SEMANTIC_MODEL` unset →
**qwen3-0.6b-f16** (512-dim, `vec0-int8-512-cosine-v1`, fp `sha256:237a776b…`). Corpus 49,276 symbols →
**10,063 embeddable cards** + **792 doc chunks**.

| metric | median | range | source |
|---|---|---|---|
| E2E build (both cursors) | 312.7 s | [306.2, 319.1] | harness |
| symbol converge | 190.5 s | [184.1, 196.9] | cursor + promote |
| symbol throughput | 55 cards/s | [53, 57] | promote line |
| chunk phase | ~122 s | — | chunk converge line |
| `vectors.db` size | 10.27 MiB | 10,768,384 B (deterministic) | stat |
| host peak footprint | ~336 MiB | [336, 337] | `time -l` |
| **sidecar child peak RSS** | **12.34 GiB** | [12.33, 12.34] | sampled `ps` |
| warm query embed | 4118 ms cold / 4048 ms warm | — | 3 CLI searches |
| model download | 1.198 GB | — | cache file size |

Memory note: the `miller` .NET host is cheap (~337 MiB, `time -l`); the real cost is the sidecar child
holding the 1.2 GB model + embedding working set (~12.34 GiB), which `time -l` never sees. macOS `time -l`
"maximum resident set size" (~12.34 GiB for the host) over-counts mapped pages and is not used.

## bge-small — measured post-fix; the build bug was this task's load-bearing finding
The first bge attempt (bge-clean-1 pre-fix) could not build: the sidecar was launched with no `--model` so
it served qwen3 (512-dim) into Miller's 384-dim bge lane → `VectorStoreException: embedding has 512 dims but
lane 'vec0-int8-384-cosine-v1' declares 384`, retrying forever. Root cause + evidence in findings
§"bge-small build blocker". Broader implication: **`MILLER_SEMANTIC_MODEL` never worked on the live
serve/CLI path for any non-default encoder** (the handshake validated the sidecar against itself, not the
request), silent because the shipping default lane only exercised qwen3.

Fixed in commit **`bf58afd`** (lead; serve/CLI forward `serve --model <Active.ModelId>` + handshake refuses
a known-but-not-selected encoder; fast suite 4406/0). On the rebuilt binary, bge builds correctly (384-dim
lane, fp `sha256:3e8b7e8a…`) and was measured with 2 clean runs + warm-query. **The Task-5 pin move to
bge-small is inseparable from `bf58afd` — shipping the bge pin requires that fix in the release.**

## Concerns / decisions
1. **Contamination + gate recalibration.** Two early qwen3 runs overlapped real model-bench lanes →
   discarded. The loadavg<4 gate was a miscalibration (ambient 6–10 on 24-core @ ~77% idle); replaced by
   "no bench workloads + CPU idle ≥ 60%". Clean qwen3 runs pass it. Contention effect was modest — clean
   (53–57 cards/s, 12.34 GiB) tracked the contaminated runs (50–56 cards/s, 12.2 GiB); qwen3 is simply
   heavy.
2. **bge build blocker (diagnosed here; fix owned by lead).** See above — the load-bearing finding of this
   task.
3. **Harness safety bug (fixed).** The initial cleanup/kill patterns matched *any* `net10.0/miller serve`,
   which would have killed the user's **live main-checkout Miller sessions** (`…/source/miller/src/…`).
   Scoped every process match to `fusion-v2-eval`. An earlier probe's unscoped `pkill` is the likely cause
   of a transient Miller MCP disconnect seen early in the session; the user's session is alive now.
4. **Phantom-DB bug (fixed).** A rw `sqlite3` open of a not-yet-created `vectors.db` *creates* an empty
   file, which broke the leader's own store init (VectorStore.ReadMeta threw). `-readonly` avoids creation
   but fails with CANTOPEN(14) on this WAL-mode db when no `-shm` exists. Fix: `meta()` opens only when the
   file already exists, then rw (WAL-safe).
5. **Cursor semantics.** `build_state` flips to `ready` when the **symbol** generation is queryable while
   the **chunk** cursor is still 0/1; completion therefore requires *both* cursors at target, not
   `build_state=ready` alone. The harness gates on both.
