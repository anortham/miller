---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T21:38:01.537Z
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

Tasks 1–7B are complete. No-change import fell from 24.826 s to a 308 ms median; leader startup fell from 26.967 s to 1.418 s; registered workspace open is 1.018 s. Exact frozen-source replay passed 8/8 with generation/hash/count parity and no crash, claim, or lease residue.

Task 6 fixed the one-path crossover exemption. The frozen production change improved from scoped resolution timing out beyond 600.003 s to the full crossover path completing in 178.621 s, while the bounded one-file oracle remained exact at 3.792 s.

Task 7A completed durable rebase pins, retry-safe publication/cleanup, and validation-spanning maintenance heartbeats. One copied-family GC run completed in 336.31 s, removed 170 obsolete deltas and 7 stale base files, reduced overlay rows 94.50% and logical resolution bytes 94.16%, left zero eligible rows, preserved protected roots, and passed integrity plus Miller reader/rotation gates.

Task 7B captured 40 post-rotation cells and fixed two measured single-ID reverse join-order defects. Reverse fallback fell from 2,475.0633 ms to 0.9536 ms and reverse exact from 145.5446 ms to 1.3852 ms. All reverse base scans are gone; the slowest final arm is the 100-ID reverse fallback at 943.4084 ms. Baseline/final raw rows, returned rows, and stable result digests match.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, and all relationship features.
- Preserve Linux and Windows paths, locks, process supervision, and memory gates.
- Miller MCP stays disabled during recovery replays because connecting it starts live producer work and contaminates evidence.
- No pin bump, push, tag, publish, or release without explicit approval.

## Next Work

Task 8 is active: run the coherent Linux fast/Scale/performance/semantic/memory gates, prepare one exact native Windows PowerShell evidence bundle for the user's Windows machine, reconcile both platforms, and write the final dated recovery verification. Pinning, pushing, publishing, and release remain approval-gated.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/findings/2026-08-14-performance-recovery-baseline.md`
- `docs/findings/2026-08-14-performance-recovery-task7b.md`
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-7a-production-evidence-report.md`
- `PERF.md`
