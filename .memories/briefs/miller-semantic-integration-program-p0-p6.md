---
id: miller-semantic-integration-program-p0-p6
title: Miller Julie takeover program
status: active
created: 2026-07-19T21:20:23.364Z
updated: 2026-07-25T21:05:18.774Z
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

- The current takeover branch is `codex/miller-julie-takeover` in `/Users/murphy/source/miller/.worktrees/miller-julie-takeover`.
- Search, inspect/edit, context, trace, impact, patterns, and Content deep-remediation milestones are complete. Content is checkpointed and ready to commit: universal 12 KiB MCP bounds, revision-safe/failure-isolated search, bounded FTS hydration, drift hashes, truthful continuation/diagnostics, and streaming CLI JSONL. Its iterative Claude review ended clean.
- The next tool phase is Workspace. After Workspace, repeat all nine tool-specific Claude reviews plus one broad review, fix every accepted finding, and rerun affected reviews until clean.
- No sealed data has been inspected. Julie remains read-only and has an unrelated active session.

## Next sequence

1. Commit the verified Content milestone including its Goldfish checkpoint.
2. Deep-audit and remediate Workspace against the matrix/plan and Julie workflow; complete the required Claude review loop.
3. Update the matrix, contracts, findings, and current docs so the nine-tool takeover evidence is complete.
4. Run nine fresh tool-specific Claude reviews plus one broad Claude review; fix accepted findings and rerun affected reviews until clean.
5. Run final evaluator, retrieval, plugin, mirror, fast, Scale, Release, AOT, and local package-smoke gates; reconcile every Miller worktree.
6. Request only unavoidable approval boundaries for GitHub-hosted four-platform package validation and the spend-once sealed decision. Do not push, publish, release, merge local main, or modify Julie without approval.
7. Present the clean exact commit ready for approved local merge to main.

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
- `docs/findings/2026-07-25-content-correctness-and-bounds.md`
- `.razorback/sdd/takeover-phase-10-readiness-audit.md`
