---
id: miller-semantic-integration-program-p0-p6
title: Miller semantic integration program (P0–P6)
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-20T13:35:45.225Z
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
- **Miller stays LOCAL until the plan completes (user directive 2026-07-20):** no miller pushes — main or branch — without asking; the user does not want anyone pulling partially-working source. Exception: `worktree-semantic-p2` stays on origin because sidecar CI pins miller fixtures at `8edfa14` on that branch. Sidecar repo pushes unaffected.

## Phase state
- P0 (governance/gates): DONE — merged; model pin 512d int8; codex pre-merge review folded in.
- P1 (freeze/conformance): DONE — merged as miller PR #7; contracts frozen, goldens token-exact.
- P2a (sidecar v1 implementation): DONE 2026-07-20 — sidecar PR #1 merged (ab65a82). Includes the 19 GiB output-buffer OOM root-cause fix, codex round-2 review (5/5 findings fixed), CI cost re-scope (PRs = 1x Linux leg only; push-to-main = Linux+Windows; 10x macOS legs + long-input embeds = workflow_dispatch only — reference machine is macOS and proves those locally).
- NEXT: P2 Miller-side parallel lanes (fake sidecar; typed candidate seam; edit lane; MinHash clones) → P3 integration (RC publication needs explicit user approval) → P4 shadow → P5 canary→default-on → P6 eval-gated extensions.
- Open P4/P5 question (design doc §model policy): download footprint — Qwen3 f16 is ~1.1 GiB; evaluate Q8_0 pin (~640 MB, needs re-benchmark + new goldens) or bge-small default tier on P4 shadow evidence.

## Status
Sidecar main is green (merge matrix run pending at last check). Miller worktree `.claude/worktrees/semantic-integration` on `worktree-semantic-p2` is the active lane, clean. Miller main is 3 checkpoint commits ahead of origin — held local per the no-push directive.
