---
id: miller-semantic-integration-program-p0-p6
title: Miller semantic integration program (P0–P6)
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-20T20:02:13.025Z
tags:
  - semantic-search
  - program
  - strategic-direction
---

# Miller semantic integration program

**Direction (2026-07-19):** Miller stays the product; the "abandon Miller for Julie" question is resolved. Miller gains a fully integrated local semantic layer. Julie remains in maintenance mode and adopts the same sidecar. Eros boundary revised: Miller owns optional local semantic retrieval; Eros (if it ships) owns fleet-level semantics.

**Authoritative design:** `docs/plans/2026-07-19-miller-semantic-integration-design.md` (HIGH architecture risk; four Codex section reviews + doubt pass folded in).

## Load-bearing decisions
- `julie-semantic-sidecar` (new repo): Rust shim + vendored llama.cpp (Metal/Vulkan/CPU, CUDA opt-in), speaks `julie.embedding.sidecar` v1 verbatim. Model: Qwen3-Embedding-0.6B (Apache 2.0); pinned lane 512d int8 per P0 benchmark. Sidecar `prepare` owns model download.
- Miller: resident child process (never a daemon); `vectors.db` with dual cursors + shadow generations + five-field generation identity; default corpus = symbol cards + docs/config chunks (all-source opt-in, eval-gated); lexical path byte-identical; RRF hybrid behind SemanticQueryPolicy; `MILLER_SEMANTIC off|shadow|on` with `off` a permanent no-side-effects guarantee.
- No new MCP tools. No ServerInstructions growth. Canary telemetry contract frozen in P0; randomized holdout is the causal gate.
- **Miller stays LOCAL until the plan completes (user directive 2026-07-20):** no miller pushes — main or branch — without asking. Exception: `worktree-semantic-p2` stays on origin (sidecar CI pins miller fixtures at `8edfa14`). Sidecar repo pushes unaffected.
- **ModelRevision pin = HF repo revision ("main"), never the gguf file name** — corrected f68dad8 after the live RC handshake caught it; encoder fingerprints changed pre-ship.

## Phase state
- P0 (governance/gates): DONE. P1 (freeze/conformance): DONE (miller PR #7). P2a (sidecar v1): DONE (sidecar PR #1).
- P2 (Miller-side lanes): DONE 2026-07-20 — merged to local main 40ca89a after codex review (3/3 real findings fixed).
- P3 (query-time hybrid + Track 1 pins/packaging): DONE 2026-07-20 — merged to local main a8c499c. Hybrid RRF at the executor seam, rescue rung, mode contracts, CLI --arm + determinism. Sidecar v0.1.0-rc.1 PUBLISHED (prerelease); pins/restore/build-guard/real-sidecar Scale tests; RC promotion gate PASSES live. Codex review: 5/5 verified findings fixed. Local main ~46 commits ahead of origin, held.
- NEXT: **P4 shadow** — run shadow in real workspaces and act on shadow evidence; P4 backlog: GC scheduler + live-reader registry (TagsWithLiveReaders unwired), converge_pause_state producer (top diagnosability item), disk preflight, `downloading` status producer, model-footprint decision (Q8_0 ~640MB re-benchmark vs f16 ~1.1GiB vs bge-small tier), fast-suite wall-ceiling pressure (at the 30s cliff). Then P5 canary→default-on (wire CanaryTelemetry arms + randomized holdout), P6 eval-gated extensions (all-source corpus etc.).
- Pending user decisions: RC→v0.1.0 promotion (gate passed, ready); miller push timing (plan-complete definition).

## Status
No active worktrees; miller main a8c499c clean (another session's untracked machine-service files present). Sidecar main green at 2a02f35 with v0.1.0-rc.1 released.
