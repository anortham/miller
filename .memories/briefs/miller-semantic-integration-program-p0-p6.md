---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T20:55:16.900Z
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

- Takeover Phases 0-9 and extractor 2.17.0 plus semantic sidecar RC4 prerequisites are complete on `codex/miller-julie-takeover`.
- Commit `22e55592` is the fourth visible-calibration runtime freeze. It bounds high-volume MCP output while preserving exhaustive CLI/process contracts.
- The fourth full visible calibration passed relevance and correctness but remained non-decisional because task-label overconstraints produced false wrong-action failures; candidate still beat baseline on correctness, but action and efficiency gates failed.
- Independent Claude review confirmed the task-scope/refusal defects. Global evidence/action decoupling, global answer/evidence term concatenation, duplicate-anchor penalties, and unmatched-evidence failures were rejected because they conflict with the frozen contract.
- The current verified diff fixes only accepted evaluator/label issues: semantic action alternatives, task-scoped evidence anchors and terms, explicit status/log-fixture guidance, and read-only grounding alongside safety refusal. Prior raw candidate answers rescore from 2 to 9 correct on repetition 1; a fresh complete run is still required.
- Evaluator Python 103 and fast Miller 4,829 pass on the current diff.

## Next sequence

1. Checkpoint and commit the verified evaluator/visible-label correction.
2. Rebuild the exact AOT runtime and hash-bound runtime identity for the new commit.
3. Run preflight and the complete fifth visible calibration; disposition any remaining failures from exact trajectories and repeat until relevance, correctness, efficiency, and action pass.
4. Run nine fresh tool-specific Claude reviews plus one broad Claude review; fix accepted findings and rerun affected reviews until clean.
5. Run final evaluator, retrieval, plugin, mirror, fast, Scale, Release, AOT, and local package-smoke gates; reconcile every Miller worktree.
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
