---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-24T22:16:06.425Z
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
- Commit `90178c8f` fixes two genuine fifth-calibration Miller defects: context now selects callable definitions over same-span export aliases, and full compact inspect preserves long value declarations.
- The working tree completes the exact fifth-run label audit and one additional product fix. Task-faithful replay now passes dev003/004/005/007(all reps)/008/009(reps 1-2)/011; dev009 rep 3 remains correctly rejected for unrelated downstream tracing, and dev012 remains a genuine old-runtime budget failure.
- Bounded JSON file inspection caused dev012 by returning imports first and hiding the focused test functions. It now ranks definitions before low-signal rows and reports total, returned, omitted, and truncated child counts.
- Evaluator Python 104 and fast Miller 4,832 pass on the current working tree. No sealed data has been inspected.

## Next sequence

1. Checkpoint and commit the verified visible-label audit plus relevance-ordered bounded file inspection.
2. Rebuild the exact AOT runtime and hash-bound runtime identity and run a complete sixth visible calibration; disposition exact trajectories and repeat until relevance, correctness, efficiency, and action all pass.
3. Run nine fresh tool-specific Claude reviews plus one broad Claude review; fix accepted findings and rerun affected reviews until clean.
4. Run final evaluator, retrieval, plugin, mirror, fast, Scale, Release, AOT, and local package-smoke gates; reconcile every Miller worktree.
5. Request only unavoidable approval boundaries for GitHub-hosted four-platform package validation and the spend-once sealed decision. Do not push, publish, release, merge local main, or modify Julie without approval.
6. Present the clean exact commit ready for local merge to main.

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
