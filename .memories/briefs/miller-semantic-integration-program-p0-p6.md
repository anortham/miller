---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T06:01:42.773Z
tags:
  - miller
  - julie
  - takeover
  - agent-efficiency
  - semantic-search
---

# Miller Julie takeover program

## Goal

Make Miller strictly better than Julie at getting an agent the evidence it needs to act correctly, measured by relevance, calls, tokens, wall-clock time, recovery, and wrong-action rate. Retire Julie only after Miller passes the sealed paired gate.

## Ownership and constraints

- Miller owns the local agent workflow and optional local semantics; julie-extractors owns parser-backed extraction; julie-semantic-sidecar owns embedding generation; Eros owns fleet semantics.
- Keep the nine MCP tools unless evidence and explicit user approval justify a surface change. Removed MCP behavior is hard-removed, not deprecated. No new MCP tool without explicit approval.
- Exact and fallback evidence stay separate and provenance-bearing; extractor behavior must prove all-language coverage; `MILLER_SEMANTIC=off` stays zero-work.
- Use TDD, architecture-quality review, phase gates, and fresh Claude review for every affected tool. The final gate repeats all nine tool reviews plus a broad review.
- Do not push, release, merge, or retire Julie without explicit user approval. Never inspect sealed evaluator prompts, labels, task rows, mappings, answers, or scorer rows. Do not interfere with the active Julie session.

## Completed

Phases 0-7 are complete in `/Users/murphy/source/miller/.worktrees/miller-julie-takeover` on `codex/miller-julie-takeover`: evaluator freeze, exact reference evidence, typed diagnostics and budgets, exact consumer/rename migration, shared search ranking/routing, bounded one-call context, risk-ranked impact, and bounded content/patterns/workspace surfaces. Phase 7 passed 4,775 fast tests plus two expected skips, 87 scale tests, Release build, Native AOT, 48 plugin tests, mirror/whitespace gates, and fresh Claude reviews for content, patterns, and workspace with every accepted finding fixed.

Exact RC3 Apple arm64 proof is committed in julie-semantic-sidecar. The local sidecar integration branch `codex/miller-takeover-macos-x64` at `8977758` also adds a reviewed macOS x64 Metal package candidate. Combined default/Metal Rust suites, clippy, Python harnesses, shell checks, and workflow validation pass. Physical Intel-Mac support proof and an RC4 public artifact remain release-boundary work.

## Active next work

1. Finish Phase 8 in `/Users/murphy/source/julie-extractors/.worktrees/miller-takeover-resolution`: close fresh Claude findings, rerun all-language gates, commit, then request approval for release/push before pinning the released extractor in Miller.
2. Complete Phase 9: implement the bounded test-only CodeRank evaluation adapter through Miller's real vector/search/context path; keep BGE as default unless CodeRank wins visible action efficiency enough to justify its approximately 6x memory and 10x query cost. Do not spend sealed tasks on a visible tie/loss.
3. Prepare the sidecar RC4 release state and request approval before push/workflow dispatch/release. Exact Linux/Windows and physical Intel-Mac package proof remain open.
4. Execute Phase 10 in corrected order: conditional retirement docs, candidate freeze, visible evaluator, nine fresh Claude tool reviews, broad review, fix/refreeze, full local gates, approval-gated pushed package-only validation, one operator-controlled sealed run, safe aggregate plus product-role attestation, then prepare the local merge decision.

## References

- `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- `docs/plans/2026-07-22-miller-julie-takeover-remediation-plan.md`
- `docs/plans/2026-07-22-miller-julie-takeover-audit-plan.md`
- `.razorback/sdd/takeover-phase-10-readiness-audit.md`
- `https://github.com/anortham/julie-semantic-sidecar/releases/tag/v0.1.0-rc.3`
