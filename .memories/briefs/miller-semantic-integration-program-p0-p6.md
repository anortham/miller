---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T07:17:48.197Z
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

- Takeover Phases 0-7 are complete on `codex/miller-julie-takeover` through commit `5ffa4782`.
- Phase 8 all-language reference resolution is complete and clean in julie-extractors at `8d5b7a8f`: 36 languages, 689 coverage cells, zero silent cells/debts, final Claude review clean. It targets `julie-extract 2.17.0`; release and Miller pin integration remain approval-gated.
- RC4 sidecar/platform prep is complete and clean at `15f6500b`: deterministic arm64 package SHA `4c4834f...`, macOS x64 Metal lane, final Claude review clean. Release and Miller pin integration remain approval-gated.
- The Miller CodeRank evaluation adapter and frozen visible model comparison are the remaining Phase 9 work. BGE-small stays the production default unless CodeRank wins the defined gate.
- Phase 10 prerequisite commits are isolated on `codex/miller-phase10-readiness-docs`: package smoke now asserts all nine tools (`2963335`); hash-bound product attestation tooling/contract is clean (`25a5646`); conditional Miller 1.14 migration/release docs are clean (`525ceae`). Julie retirement text is not touched until Julie's active session is reconciled.

## Next sequence

1. Finish and commit the CodeRank adapter/evidence lane.
2. Integrate Miller Phase 9 and Phase 10 commits; release/pin extractor 2.17.0 and sidecar RC4 only with explicit approval.
3. Freeze the exact candidate, then run full visible calibration.
4. Run nine fresh tool-specific Claude reviews plus one broad review; fix and refreeze until clean.
5. Run all local gates, then the approval-gated four-platform package-only workflow against the exact frozen ref.
6. Run the approval-gated sealed decision once; accept only the safe aggregate and schema-valid hash-bound Miller attestation.
7. If every gate passes, reconcile worktrees and present the clean exact commit ready for local merge to main. Do not push, publish, release, or modify Julie without explicit approval.

## Constraints

- Miller owns local agent workflows and optional local semantics; julie-extractors owns parser-backed extraction; julie-semantic-sidecar owns embedding generation; Eros owns fleet semantics.
- Keep nine MCP tools. New MCP tools require explicit approval; removed behaviors are removed, not deprecated.
- Exact/fallback evidence stays separate and provenance-bearing. Extractor-backed behavior must pass all-language coverage.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee.
- Never inspect sealed prompts, labels, task rows, mappings, answers, evidence, trajectories, or scorer rows.
- No push, release, package dispatch, paid sealed spend, local-main merge, or Julie retirement without the applicable approval.

## References

- `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- `docs/plans/2026-07-22-miller-julie-takeover-remediation-plan.md`
- `.razorback/sdd/takeover-phase-10-readiness-audit.md`
