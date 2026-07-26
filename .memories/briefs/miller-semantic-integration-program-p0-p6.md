---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-26T02:24:24.188Z
tags:
  - miller
  - julie
  - takeover
  - phase-10
  - agent-efficiency
  - semantic-search
---

# Miller Julie takeover program

## Goal

Make Miller strictly better than Julie at getting an agent the evidence it needs to act correctly, measured by relevance, calls, tokens, wall-clock time, recovery, and wrong-action rate. Retire Julie only after Miller passes the sealed paired gate.

## Current state

- The active implementation is `codex/miller-julie-takeover` in `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`.
- Phases 0-9 are complete. Phase 10 is the final deep-remediation, review, documentation, and verification phase.
- Phase 10 Edit and Content remediation is implemented and under final verification. The remaining tool passes are Search, Inspect, Context, Trace, Impact, Patterns, and Workspace.
- Each of all nine tools requires a Claude review during its Phase 10 pass. After the tool passes, one broad Claude review must validate the combined takeover branch; every accepted finding is fixed and affected reviews rerun until clean.
- No sealed evaluator data has been inspected. Julie remains read-only and has an unrelated active session.

## Success criteria

- All nine Miller tools meet the takeover matrix for correctness, bounded output, truthful evidence and recovery, JSON/compact parity, and agent-efficient workflows.
- Current contracts, findings, matrix, skills, and public guidance describe the shipped behavior exactly.
- Fast, Scale, Release, Native AOT, evaluator, retrieval, plugin, mirror, and local package-smoke gates pass from a reconciled clean branch.
- Four-platform hosted validation and the sealed paired decision run only after explicit approval.

## Constraints

- Miller owns local agent workflows and optional local semantics; julie-extractors owns parser-backed extraction; julie-semantic-sidecar owns embedding generation; Eros owns fleet semantics.
- Keep nine MCP tools. New MCP tools require explicit approval; removed behaviors are removed, not deprecated.
- Exact and fallback evidence stays separate and provenance-bearing. Extractor-backed behavior must pass all-language coverage.
- `MILLER_SEMANTIC=off` remains a permanent zero-work guarantee.
- Never inspect sealed prompts, labels, task rows, mappings, answers, evidence, trajectories, or scorer rows.
- No push, release, paid sealed spend, local-main merge, or Julie retirement without the applicable approval.

## References

- `docs/findings/2026-07-22-miller-julie-takeover-matrix.md`
- `docs/plans/2026-07-22-miller-julie-takeover-remediation-plan.md`
- `.razorback/sdd/takeover-phase-10-readiness-audit.md`
