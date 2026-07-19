---
id: miller-semantic-integration-program-p0-p6
title: Miller semantic integration program (P0–P6)
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-19T21:20:23.364Z
tags:
  - semantic-search
  - program
  - strategic-direction
---

# Miller semantic integration program

**Direction (2026-07-19):** Miller stays the product; the "abandon Miller for Julie" question is resolved. Miller gains a fully integrated local semantic layer. Julie remains in maintenance mode and adopts the same sidecar. Eros boundary revised: Miller owns optional local semantic retrieval; Eros (if it ships) owns fleet-level semantics.

**Authoritative design:** `docs/plans/2026-07-19-miller-semantic-integration-design.md` (HIGH architecture risk; four Codex section reviews + doubt pass folded in).

## Load-bearing decisions
- `julie-semantic-sidecar` (new repo): Rust shim + vendored llama.cpp (Metal/Vulkan/CPU, CUDA opt-in), speaks `julie.embedding.sidecar` v1 verbatim. Model: Qwen3-Embedding-0.6B (Apache 2.0); dims/quantization from benchmark. Sidecar `prepare` owns model download.
- Miller: resident child process (never a daemon); `vectors.db` with dual cursors + shadow generations + five-field generation identity; default corpus = symbol cards + docs/config chunks (all-source opt-in, eval-gated); lexical path byte-identical; RRF hybrid behind SemanticQueryPolicy; `MILLER_SEMANTIC off|shadow|on` with `off` a permanent no-side-effects guarantee.
- No new MCP tools. No ServerInstructions growth. Canary telemetry contract frozen in P0; randomized holdout is the causal gate.

## Phase state
- P0 (governance/gates): NOT STARTED — ADR-0003 boundary reversal, sqlite-vec-on-AOT spike (4 RIDs), eval sets, model benchmark, canary contract, telemetry version-stamping.
- P1 freeze/conformance → P2 parallel lanes (fake sidecar; typed candidate seam; edit lane; MinHash clones) → P3 integration (RC publication needs explicit user approval) → P4 shadow → P5 canary→default-on → P6 eval-gated extensions.

## Status
Design doc written, doubt-passed, awaiting user review. Then: worktree, commit design as first branch commit, razorback:writing-plans for P0.
