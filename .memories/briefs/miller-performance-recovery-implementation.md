---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T16:14:38.684Z
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

## Current Evidence

The frozen production-volume baseline showed leader startup about 26.97 s, import about 24.83 s, one-file resolve timing out at 60 s, and full resolution spending 171.7 s in resolution. Warm reader startup and memory were already healthy.

Task 5B is complete. Julie commits `e9f6e039` and `94c4194b` collapse fully complete no-change plans and reuse strictly validated prior terminal row counts. Direct fresh-key import median is 308 ms. The exact frozen-source Miller replay passed 8/8: leader startup median 1.418 s, workspace open median 1.018 s, import 326–333 ms, resolution skipped, exact generation/hash/counts, and no crash/core/claim/lease.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, and all relationship features.
- Characterize shipped incremental resolution before production edits.
- Keep lifecycle rebase/collection separate from query/index/statistics tuning.
- Preserve Linux and Windows paths, locks, process supervision, and memory gates.
- No pin bump, push, tag, publish, or release without explicit approval.

## Next Work

Task 6 is active next: prove incremental resolver routing, digest equivalence, and one-file/full timing on faithful copied stores. Task 7A then owns retained-history rebase/collection/rotation cost; Task 7B owns post-rotation relationship read/query-plan cost. Task 8 closes Linux and native Windows correctness, Scale, semantic, memory, and timing gates.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/findings/2026-08-14-performance-recovery-baseline.md`
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-5b-final-clean-replay-report.md`
- `PERF.md`
