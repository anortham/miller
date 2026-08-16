---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-16T05:27:37.383Z
tags:
  - performance
  - family-store
  - windows
  - acceptance
  - adoption-gated
---

## Goal

Restore Miller startup, indexing, relationship-query, and family-store performance to the published Linux and Windows budgets without disabling features or weakening correctness.

## Current Evidence

Linux recovery is complete: the production-volume full request improved from 148.431 s to 54.814 s wall and 44.673 s resolution at 26.831 MB peak PSS. Julie base `65bb7862` passed the Linux producer suites and official three-run gate; Miller's Linux build, fast/Scale suites, and 30-minute semantic soak passed.

Native Windows acceptance is also complete on Miller base `5d593419` and Julie base `65bb7862`, with current Windows fixes uncommitted. Miller Release build passed with zero warnings/errors; fast was 6,546 passed/24 skipped, Scale 142/6, focused TRX 80/6, Python 150/3, and proxy 17/17. Julie's latest resolution contract passed 30/30 in 109.83 s. The 1,800-second semantic soak passed 26/26 probes with zero failures/hangs. Strict replay passed all 42 measured records across 14 workloads with zero measured hard-gate failures/nonzero exits; peak PrivateUsage was 60,510,208 bytes and idle maximum was 35,983,360 bytes.

## Architecture Direction

Retain the accepted Linux recovery design and all public contracts. Retain the Windows fixes for Job Object supervision, bounded fixture readiness/cleanup, explicit semantic-model preparation, extended path identity, staged `change_root`, and Julie family-view root identity. Performance expansion and evidence reconciliation are closed.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, and all relationship features.
- Preserve Linux and Windows paths, locks, process supervision, SQLite portability, and memory gates.
- Do not mutate the protected live store; raw acceptance artifacts remain under `C:\Users\alann\.miller\perf-recovery-windows-acceptance\run-5d593419-65bb7862`.
- No pin bump, commit, push, tag, publish, or release without explicit approval.

## Next Work

Only the approval-gated adoption path remains: review/commit the uncommitted Miller and Julie Windows fixes and evidence, push the branches if approved, then make any producer pin or release change under separate explicit approval.

## References

- `docs/findings/2026-08-13-performance-recovery-verification.md`
- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `PERF.md`
- `.memories/2026-08-16/052722_fbcd.md`
