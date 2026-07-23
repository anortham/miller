---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-23T02:49:45.246Z
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

## Why now

The semantic integration program established the local vector foundation, but the completed nine-tool audit found deeper correctness and workflow gaps: name-based reference contamination, weak context composition, thin lexical ranking, flat impact output, unsafe rename coverage, untyped failures, and unbounded responses. Search quality alone will not complete the replacement.

## Execution order

1. Gap-close and freeze the takeover evaluator already present on main.
2. Build exact symbol-ID reference evidence.
3. Introduce typed diagnostics and deterministic output-budget/continuation contracts before migrating tool consumers.
4. Migrate trace, inspect, context usage, and edit/rename truth.
5. Improve shared search ranking and routing.
6. Redesign context around bounded, one-call actionability.
7. Make impact risk-ranked and test-aware.
8. Bound and simplify content, patterns, and workspace.
9. Raise all-language resolution coverage in julie-extractors.
10. Validate RC3 protocol/platform support early; decide BGE-small versus CodeRankEmbed after final search/context behavior exists.
11. Run the sealed paired decision, nine fresh Claude tool reviews, a separate broad Claude review, and only then decide Julie retirement.

## Constraints

- Miller owns the local agent workflow and optional local semantics; julie-extractors owns parser-backed extraction; julie-semantic-sidecar owns embedding generation; Eros owns fleet semantics.
- Keep the nine MCP tools unless evidence and explicit user approval justify a surface change. No new MCP tool without explicit approval.
- Exact and fallback evidence remain separate and provenance-bearing.
- Extractor-backed behavior must report and pass all-language coverage.
- MILLER_SEMANTIC=off remains a permanent zero-work guarantee.
- Use TDD, architecture-quality review, per-phase verification gates, and fresh Claude review for every affected tool. The final gate repeats all nine tool reviews plus a broad review.
- No push, release, or Julie retirement without explicit user approval.

## RC3 evidence

`julie-semantic-sidecar` v0.1.0-rc.3 was published on 2026-07-23 at commit `24ce625`. The release API lists three portable platform packages plus three `.sha256` sidecars; the release page's total of eight also includes GitHub's two generated source archives. BGE Small remains the default and Qwen is opt-in. The release advertises Apple arm64 Metal, Linux x64 Vulkan, and Windows x64 Vulkan packages, accelerated backend/device discovery with CPU fallback, size-aware download timeouts, and physical Linux/Windows validation. Treat these as release claims to verify in Miller's Phase 9 preflight; the release does not choose BGE Small over CodeRankEmbed for Miller.

## Success criteria

Miller meets or beats Julie on sealed correctness, nDCG/MRR/top-1, calls, tokens, wall time, and wrong-action rate; reference and rename truth are homonym-safe; failures are machine-readable; outputs are bounded and recoverable; semantic integration passes supported-platform gates; no Julie-only workflow is lost.

## References

- `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- `docs/plans/2026-07-22-miller-julie-takeover-remediation-plan.md`
- `docs/plans/2026-07-22-miller-julie-takeover-audit-plan.md`
- `docs/plans/2026-07-19-miller-semantic-integration-design.md`
- `https://github.com/anortham/julie-semantic-sidecar/releases/tag/v0.1.0-rc.3`

## Status

The isolated implementation worktree is `/Users/murphy/source/miller/.worktrees/miller-julie-takeover` on `codex/miller-julie-takeover`. Phase 0's v1 contract, MRR/top-1 metrics, and disallowed-tool void fix are committed locally at `87bf18a`; Python semantic-contract and pure C# action-scorer work is active. RC3 is released and queued for the early protocol/platform preflight after Phase 0. Do not inspect sealed labels or interfere with the active Julie session.
