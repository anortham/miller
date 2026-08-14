---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T03:57:03.433Z
tags:
  - performance
  - family-store
  - sqlite
  - context
  - impact
  - windows
  - multi-session
---

## Goal

Restore Miller startup, indexing, relationship-query, and family-store performance to the published Linux and Windows budgets without disabling features or weakening correctness.

## Why Now

Measured default relationship context takes about 11.93 seconds, leader startup about 28.5 seconds, exact resolution about 164–172 seconds, and the live family store retains about 1.12 GB of resolution state. A stranded claimed import also demonstrated that coordination failures can compound convergence and overlay cost.

## Constraints

- Preserve Store Contract v1 and all existing MCP/CLI schemas and deterministic outputs.
- Measure every performance change before and after on the same fixed workload.
- Characterize shipped incremental behavior before editing it.
- Keep Windows compatibility and dedicated Windows memory/broker gates.
- Keep source changes in the named Miller and Julie recovery worktrees.
- No pin bump, push, tag, publish, or release without explicit approval.

## Success Criteria

All eight tasks in the implementation plan are landed and lead-reviewed; Linux and Windows correctness, scale, memory, semantic, and timing gates pass; remaining PERF rows are closed with measured evidence.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `PERF.md`

## Status

Implementation active on `feature/performance-recovery`; Task 1 replay harness is in progress.
