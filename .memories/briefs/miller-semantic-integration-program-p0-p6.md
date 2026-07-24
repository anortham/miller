---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T22:02:24.763Z
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
- Commit `69bccd68` fixes the accepted visible evaluator and task-label defects; evaluator Python 103 and the fast Miller suite passed before the fifth visible calibration.
- The fifth full visible calibration passed relevance and correctness with no baseline-only wins, but remained non-decisional on efficiency and action: candidate stabilized 6 correct versus baseline 2, with candidate-only wins on dev002/dev006/dev010/dev015 and both-correct dev013/dev014.
- Exact fifth-run trajectories exposed two genuine Miller defects now fixed in the working tree with passing regressions: natural `context` retrieval selected a same-span `export` alias instead of the callable definition, and compact `inspect depth=full` truncated long constant initializers needed to answer embedded-fixture tasks.
- Remaining fifth-run failures include visible-label equivalences and task-specific grounding mismatches on dev003/dev004/dev008/dev009/dev011, plus workflows still requiring investigation on dev005/dev007/dev012. No sealed data has been inspected.

## Next sequence

1. Checkpoint and commit the verified context-definition and full-value-inspect fixes.
2. Correct only evidence-backed visible evaluator/label mismatches, with contract tests; investigate dev005/dev007/dev012 trajectories without weakening global evidence rules.
3. Rebuild the exact AOT runtime and hash-bound runtime identity and run complete visible calibration iterations until relevance, correctness, efficiency, and action all pass.
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
