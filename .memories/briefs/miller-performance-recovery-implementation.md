---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T19:08:50.643Z
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

Tasks 1–5B are complete. No-change import fell from 24.826 s to a 308 ms median; leader startup fell from 26.967 s to 1.418 s; registered workspace open is 1.018 s. Exact frozen-source replay passed 8/8 with generation/hash/count parity and no crash, claim, or lease residue.

Task 6 proved and fixed a crossover-policy defect in Julie. One changed README path selected 776 versions and 386,163 prior identifier rows, but a one-path exemption bypassed the existing 0.7 work estimate. Removing that exemption changed production routing from scoped resolution timing out beyond 600.003 s to the full crossover path completing in 178.621 s. Synthetic bounded one-file scope remains exact and finishes in 3.792 s versus a 2.273 s full oracle. Routing correctness is recovered, but the production 5 s gate still fails; 171.167 s remains inside full resolution.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, and all relationship features.
- Keep lifecycle rebase/collection separate from query/index/statistics tuning.
- Preserve Linux and Windows paths, locks, process supervision, and memory gates.
- Miller MCP stays disabled during recovery replays because connecting it starts live producer work and contaminates evidence.
- No pin bump, push, tag, publish, or release without explicit approval.

## Next Work

Task 7A is active: continuously protect a newly built rebase base across the ready-to-view-CAS gap, put the existing pin-aware superseded-delta cleanup on the successful resolve path, prove generic maintenance reclaims obsolete bases/files, and validate retained-history ceilings on a second copied snapshot. Current Miller already contains all three old consumer rotation/rebind tests, so no branch-test duplication is needed. Task 7B then captures post-rotation eight-arm query plans and changes only the measured read owner. Task 8 closes Linux and native Windows correctness, Scale, semantic, memory, and timing gates.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/findings/2026-08-14-performance-recovery-baseline.md`
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-6-crossover-fix-report.md`
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-6-postfix-production-replay-report.md`
- `PERF.md`
