# Q8_0 model-footprint benchmark — evidence record (P4 Task 7)

**Date:** 2026-07-20 · **Machine:** M2 Ultra (reference) · **Sidecar:** julie-semantic-sidecar
v0.1.0-rc.2, Metal backend · **Bench:** sidecar `scripts/bench-throughput.py` (64-text batches,
4 measured rounds, warm model, post-round RSS sample)

This is the evidence the design's open footprint question asked for
(design §Model policy, "Open question for P4/P5: download footprint"). **No decision is made
here** — the footprint call (option a: Q8_0 pin; option b: bge-small default tier; or keep f16)
stays with the user, informed by this plus P4 shadow evidence.

## Finding 1 — Q8_0 is not benchmarkable yet: the manifest has no Q8_0 pin

The sidecar manifest (`src/manifest.rs`) pins exactly two models: `qwen3-0.6b-f16` (default tier)
and `bge-small-en-v1.5-f32` (fallback tier). There is **no Q8_0 entry**, so option (a) cannot be
measured through the shipping acquisition path today.

Cost of closing the gap (sidecar-repo work, out of this task's scope):

1. A new `ModelPin` — id (`qwen3-0.6b-q8_0`-style), file, source URL, sha256, size, and the same
   MRL/pooling/instruction metadata as the f16 pin.
2. Fresh conformance goldens for the new lane (golden vectors are per-weights).
3. A re-run of the P0 eval gate on the Q8_0 weights — the 512d-int8 lane decision was scored on
   f16 ([2026-07-19-model-benchmark.md](2026-07-19-model-benchmark.md)); near-zero quality loss is
   expected but is exactly the kind of claim the rc.1 lesson says to measure, not assume.

Estimated effort: one focused sidecar session (pin + goldens + gate re-run), assuming the
upstream GGUF exists with a stable sha256.

## Finding 2 — measured f16 vs bge-small on the FIXED (Metal) sidecar

Both models were already sha256-verified in the shared cache; both runs report `ready:true`,
`accelerated:true`, backend `metal`. Full raw output in the sidecar repo's
`docs/findings/2026-07-20-model-throughput-rss-bench.md`.

| | `qwen3-0.6b-f16` (default) | `bge-small-en-v1.5-f32` (fallback) | ratio |
|---|---|---|---|
| Steady throughput (units/s) | 82.9 | 743.7 | **9.0×** |
| Sidecar RSS after sustained embedding | 1.27 GiB | 196 MiB | **0.15×** |
| Weights on disk | 1.12 GiB | 127 MiB | 0.11× |
| Download ask (first `miller semantic prepare`) | ~1.1 GiB | ~127 MiB | |
| Served dims | 512 (MRL from 1024) | 384 (native) | |

The old CPU-only numbers are invalid for this comparison and were not reused (rc.1 lesson).

## What this does and does not say

- **Cost side (this doc):** bge-small is dramatically cheaper on every axis — download, disk,
  RAM, and throughput. A bge-tier workspace converges an initial Miller-sized corpus in tens of
  seconds rather than minutes and holds ~200 MB resident instead of ~1.3 GB.
- **Quality side (not this doc):** the P0 benchmark chose Qwen3 512d-int8 on worst-language nDCG
  over the eval corpus; bge-small was retained as the licensed fallback tier, not scored as an
  equal. The design directs the quality comparison to run on **P4 shadow evidence** (real
  workspace retrieval), not on this throughput bench.
- **Q8_0 (option a)** would roughly halve the download/disk ask (~640 MB class) while keeping the
  Qwen3 quality tier — but it needs the manifest pin + goldens + eval re-run above before any
  number can be attached.

## Pins, defaults, manifests: unchanged

No model pin, manifest entry, or default was changed by this task. The sidecar gained only bench
tooling (`--model` passthrough + RSS sample in `scripts/bench-throughput.py`).
