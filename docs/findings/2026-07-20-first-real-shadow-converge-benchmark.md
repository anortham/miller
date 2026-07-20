# First real shadow-converge benchmark — Miller corpus, julie-semantic-sidecar v0.1.0-rc.1

**Date:** 2026-07-20
**Machine:** Apple M2 Ultra (Metal, unified memory), macOS
**Corpus:** Miller repo clone at `a8c499c` — 48,804 extracted symbols
**Mode:** `MILLER_SEMANTIC=shadow`, initial build (empty `vectors.db`), Qwen3-Embedding-0.6B **f16** (pinned encoder)
**Binary under test:** Miller local main `d9b65e5` (post scale-fix), sidecar `v0.1.0-rc.1` release binary

## Why this run exists

The P0 throughput table (`docs/findings/2026-07-19-model-benchmark.md`) measured 52.3 units/s for Qwen3 f16 —
but through an HTTP harness (llama-server), with the explicit caveat "re-measure against the real sidecar's
batching". This is that re-measure, and the first end-to-end initial converge through the production path:
extract → escalation → shadow rebuild → bounded commits.

## Run 1 (pre-fix): the empty promote

The first attempt (Miller `a8c499c`, 2026-07-20 15:08) promoted an **empty** vector generation in 88ms:

```
15:08:22.161 Vector "Symbol" convergence escalates to a shadow rebuild ("BatchTooLarge").
15:08:22.249 Promoted a shadow vector generation with 0 embedded symbol cards.
```

`BuildShadowAsync` reused the incremental planner, whose `BatchTooLarge` escalation returns an empty
`ReEmbed` list for any span over 2,000 units; the `0 == 0` completeness check then passed and the empty
corpus was stamped complete and promoted. Zero CPU/GPU activity was the user-visible symptom. Fixed in
`97f2b80` (merged `d9b65e5`) along with three latent siblings: poison-unit permanent rebuild failure,
chunk-cursor escalation into the symbol-only rebuild, and the missing same-wake post-promote chunk
continuation. See the commit message for the full defect list.

## Run 2 (Miller fix, RC sidecar): the "before" numbers

Timeline (from `shadow-bench-progress.log` and `.miller/logs`):

| Phase | Wall time |
|---|---|
| Extract: 48,804 symbols to `symbols.db` (julie-extract, cold) | **~16s** (bootstrap ready at 17.5s) |
| Content + search sidecar converge | ~16s (overlapped) |
| Escalation to shadow rebuild (`BatchTooLarge`) | at 34s |
| Symbol corpus: 9,490 cards in 5 bounded slices | **~27 min** (promote at 16:02:41, 0 flagged) |
| Steady-state symbol embedding rate | **~6 units/s** |
| Chunk cursor (868 docs/config chunks) | **timed out** — see below |

Corpus math (this repo, not the P0 corpus): of 48,804 extracted symbols, only **9,490** are card-eligible
kinds (`SymbolCardBuilder.EligibleKinds` — variables/properties/imports/modules are excluded, which is most
of the 48k). The chunk corpus adds **868** docs/config chunks.

The Miller scale fix behaved as designed end-to-end: bounded ≤2,000-unit slices committed progressively
into `vectors.db.rebuild`, nothing promoted early, the promote log carried the real counts ("9490 embedded
symbol cards (0 flagged)"), and the drain stayed on one resident sidecar session. The post-promote
same-wake chunk continuation engaged — and then exposed one more RC-throughput casualty: chunks average
~5× more tokens than cards, a 64-chunk batch exceeded the 30s sidecar request timeout at ~330 tokens/s, and
the chunk cursor recorded `no response to 'embed_batch' within 30000 ms` and held at 0/1 (a quiet
workspace gets no retry wake). At the fixed sidecar's rate a 64-chunk batch is ~4s, comfortably inside the
timeout.

## Root cause: the RC is CPU-only by design

`backend_select.rs` (module doc, lines 10–15) states it outright: *"This build compiles no accelerated
backend (`llama-cpp-2` with `default-features = false`), so the benchmark has exactly one candidate and
every selection resolves to CPU."* `select()` always returns `cpu`, and `model_params` (`engine.rs:609`)
then pins `n_gpu_layers=0` — the whole model runs on CPU. The accelerated build was deferred as a future
"Task 8" and never reconciled against the P0 design numbers, **which were measured on Metal**. The user
had stated up front that Apple Silicon GPU (MPS/Metal) support was a requirement.

Confusing forensics, for the record: the binary DOES link `Metal.framework` and initialize the Metal
device (`ggml_metal_device_init: GPU name: MTL0 (Apple M2 Ultra)` in stderr), because the vendored
llama.cpp's CMake enables Metal on Apple platforms regardless of the crate feature; with unified shared
buffers llama.cpp's scheduler runs some activation work on Metal even at `n_gpu_layers=0`, which is why
stack samples show `ggml_metal_graph_compute` while throughput stays CPU-class (~330 tokens/s ≈ 6.6
cards/s through the 0.6B f16 model).

Secondary (real but smaller) engine-shape costs, relevant once Metal is actually on:

1. **`MAX_DECODE_UBATCH_TOKENS = 256`** (`engine.rs:84`) — CI-memory-sized micro-batch; a 64-card group
   becomes dozens of small dispatches.
2. **Fresh llama context per group** (`encode_group`, `engine.rs:258`) — per-request KV/compute buffer
   allocation.

## The sidecar fix (same day): `metal-backend` branch, 6.6 → 78.9 units/s

Fixed in julie-semantic-sidecar commit `bcfe965` (branch `metal-backend`, pushed). Two independent causes:

1. **CPU-only build**: new `metal` cargo feature + `backend_select` resolving `metal` when compiled
   (MetalMachine identity keys the selection cache on GPU brand + OS build; `FORCE_BACKEND=cpu` stays the
   escape hatch; CI conformance stays CPU-forced). Alone this bought 6.6 → ~25 units/s.
2. **Silent per-text serialization (the bigger one, all backends)**: `encode_group` never set
   `n_seq_max`, llama.cpp's default of 1 rejected every multi-sequence group, and `isolate()`'s bisection
   quietly encoded one text per fresh context — ~500 context creations per 4-request probe, flat ~46ms/text
   regardless of batch size or backend. Fix: `n_seq_max = count`, `n_ctx = longest × count` (llama.cpp
   partitions the context per sequence), and grouping re-budgeted on that product
   (`group_by_cell_budget`). Contexts per probe: ~500 → 4.

Probe results on the target machine (64-text real symbol cards): **78.9 units/s steady state** (250-text:
77.4), 1.5× the P0 llama-server floor. Conformance: 9/9 ignored rows pass with the metal build, including
golden lane-vector reproduction; unit suite 53/53 (metal) and 51/51 (cpu-only). The `MAX_DECODE_UBATCH_TOKENS`
256 → 2048 raise rode along (its own doc table shows 2048 fits CI with >9GiB headroom).

## Run 3 (Miller fix + fixed sidecar): the "after" numbers

Same corpus, same machine, fixed sidecar binary swapped into `.tools` (still reporting 0.1.0-rc.1 — the
version was not bumped for the local experiment):

| Phase | Wall time |
|---|---|
| Launch → extract artifact ready (48,804 symbols) | 20s (16:06:52 → 16:07:12) |
| Escalation to shadow rebuild (`BatchTooLarge`) | 16:07:13 |
| Symbol corpus: 9,490 cards, promote (0 flagged) | 16:10:11 — **~2m50s of embedding (~56 units/s in-pipeline)** |
| Same-wake chunk continuation: 864/868 chunks (4 empty-text drops), both cursors at target, no errors | complete shortly after promote; artifact quiescent by 16:12:23 |

**End-to-end: ~3.5 minutes** (≤5.4 min by the most conservative reading) versus Run 2's 27+ minutes with a
dead chunk cursor — and Run 1's instant, empty, wrong promote. The in-pipeline 56 units/s versus the 79
units/s raw-probe rate is Miller-side card building, hash-gating, quantization, and commit overhead — no
longer sidecar-bound.

Every piece of the day's Miller fix was exercised for real in this run: `BatchTooLarge` escalation into
`RebuildWorkList`, bounded ≤2,000-card slice commits, flagged-count-aware promote logging, and the
post-promote same-wake chunk continuation on the reopened artifact.

## Consequences

- **A new sidecar release is needed** — the fixed binary must ship as `v0.1.0-rc.2` (or the user may
  choose to fold it into the `v0.1.0` promotion decision) and Miller's `scripts/semantic-pins.json` bumped.
  Publishing needs explicit user approval; the local `.tools` binaries were swapped for benchmarking only.
- **Process failure recorded** (memory `validate-hardware-perf-on-real-artifact`): the P0 number was a
  harness number; the RC gate had no throughput floor; the "re-measure against the real sidecar's
  batching" caveat was carried through three phases and an RC publish. RC→release promotion must gain a
  units/sec floor on the target machine.
- **Extract remains a non-issue**: 48.8k symbols in ~16s. The user-visible cost of semantic-on is entirely
  the embedding arm.
- Miller-side converge machinery is correct and observable: bounded slices, honest `build_state`
  progress, no empty promotes.

## Cleanup

Bench workspace `$CLAUDE_JOB_DIR/tmp/shadow-bench` (workspace_id `55847b1b…`) removed after the run,
including its `~/.miller/workspaces.db` registry entry. The main checkout's `.tools/julie-semantic-sidecar`
(and the Release output copy) hold the FIXED binary from the `metal-backend` branch — same reported
version as the RC, different bits — pending the rc.2/v0.1.0 release decision.
