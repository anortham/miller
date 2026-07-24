---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T18:59:39.663Z
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

## Current state

- Takeover Phases 0-9 and the released/pinned extractor 2.17.0 plus semantic sidecar RC4 prerequisites are complete on `codex/miller-julie-takeover`.
- The first two full visible Phase 10 calibrations were non-decisional: relevance/correctness passed, but oversized MCP responses and ambiguous semantic-action guidance failed efficiency/action.
- Commit `27a0ccdb` bounded inspect/context/patterns/workspace output. The current verified second-loop diff additionally caps MCP search/file-inspect at 20 rows, bounds MCP search signatures while preserving process JSON, clarifies sufficient context and exact trace evidence, and makes the evaluator action contract outcome-based.
- Fast (4,826), Scale (91), evaluator Python (100), retrieval (95), focused contract (297), plugin (48), Release build, and local osx-arm64 Native AOT gates pass for the uncommitted second-loop diff.

## Next sequence

1. Checkpoint and commit the second-loop output/action-guidance fix.
2. Rebuild exact AOT runtime and regenerate hash-bound runtime identity.
3. Reuse the five exact source snapshots but rebuild candidate artifacts as needed, run preflight, and repeat the complete visible calibration until relevance, correctness, efficiency, and action all pass.
4. Run nine fresh tool-specific Claude reviews plus one broad Claude review; disposition findings, fix, rerun affected reviews, and refreeze until clean.
5. Run final local, evaluator, plugin, mirror, AOT, and package-smoke gates; reconcile all Miller worktrees.
6. Request only unavoidable approval boundaries for GitHub-hosted four-platform package validation and the spend-once sealed decision. Do not push, publish, release, merge local main, or modify Julie without approval.
7. Present the clean exact commit ready for local merge to main.

## Constraints

- Miller owns local agent workflows and optional local semantics; julie-extractors owns parser-backed extraction; julie-semantic-sidecar owns embedding generation; Eros owns fleet semantics.
- Keep nine MCP tools. New MCP tools require explicit approval; removed behaviors are removed, not deprecated.
- Exact/fallback evidence stays separate and provenance-bearing. Extractor-backed behavior must pass all-language coverage.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee.
- Never inspect sealed prompts, labels, task rows, mappings, answers, evidence, trajectories, or scorer rows.
- No push, release, paid sealed spend, local-main merge, or Julie retirement without the applicable approval.

## References

- `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- `docs/plans/2026-07-22-miller-julie-takeover-remediation-plan.md`
- `.razorback/sdd/takeover-phase-10-readiness-audit.md`
