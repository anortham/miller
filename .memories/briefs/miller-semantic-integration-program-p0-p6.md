---
id: miller-semantic-integration-program-p0-p6
title: Miller semantic integration program (P0–P6)
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-21T01:59:18.510Z
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
- **Harness numbers ≠ engine numbers (2026-07-20 lesson):** the RC shipped 12x under the P0 llama-server floor (CPU-only backend selection + `n_seq_max` never set → silent per-text bisection). RC→release promotion must include a target-machine throughput floor. Memory: `validate-hardware-perf-on-real-artifact`.

## Phase state
- P0 (governance/gates): DONE. P1 (freeze/conformance): DONE (miller PR #7). P2a (sidecar v1): DONE (sidecar PR #1).
- P2 (Miller-side lanes): DONE 2026-07-20 — merged to local main 40ca89a after codex review (3/3 real findings fixed).
- P3 (query-time hybrid + Track 1 pins/packaging): DONE 2026-07-20 — merged to local main a8c499c. Sidecar v0.1.0-rc.1 published; RC promotion gate passed live.
- Initial-converge scale fix + sidecar throughput fix: DONE 2026-07-20 (post-P3 interlude, merged d9b65e5; sidecar v0.1.0-rc.2 published, pin bumped a921bae). ~3.5 min clean initial semantic index. Evidence: `docs/findings/2026-07-20-first-real-shadow-converge-benchmark.md`.
- **P4 (shadow rollout): DONE 2026-07-21 — merged to local main 43abbed** after codex review (4 findings: F1 retention-mtime, F2 Unix disk-probe, F4 marker race fixed; F3 dismissed→pre-P5 follow-up). Shipped: converge_pause_state producer (circuit-open + disk-blocked), DiskPreflight, `miller semantic prepare` CLI verb + marker, `downloading` status, generation GC + VectorLiveReaderRegistry, sidecar promotion-gate throughput floor (≥40 u/s M2 Ultra warm 64-batch) + bench script with RSS, fast-suite fsync fix (~15s), shadow dogfood across goldfish/eros/julie (fault campaign clean). Evidence: `docs/findings/2026-07-20-p4-shadow-dogfood.md`, `docs/findings/2026-07-20-q8-footprint-benchmark.md`, run report `.memories/autonomous-run-2026-07-21-semantic-p4-shadow-rollout.md`.
- NEXT: **P5 canary→default-on** (wire CanaryTelemetry arms + randomized holdout). Pre-P5 follow-ups: chunk-cursor starvation retry wake (medium — planner holds cursor with AdvanceTo=0 on hash-disagreement deferral; quiet workspace never re-wakes), incremental-path disk gate (codex F3), deferred-source logging (low), sidecar RSS peak ceiling check, compact "ready (rebuilding)" hint. Then P6 eval-gated extensions.
- **Pending user decisions:** (1) model footprint — NO Q8_0 manifest pin exists; measured f16 82.9 u/s @1.27GiB vs bge-small 743.7 u/s @196MiB (`docs/findings/2026-07-20-q8-footprint-benchmark.md`); (2) sidecar RC→v0.1.0 promotion — re-run gate WITH the new 40 u/s floor (sidecar main ahead 3 of origin, unpushed); (3) miller push timing (main ~78 ahead of origin, held).

## Status
No active worktrees; miller main 43abbed (P4 merged; pushes held; another session's untracked machine-service files present in the checkout). Sidecar main 34866ba local (ahead 3 of origin: bench --model/RSS + gate floor).
