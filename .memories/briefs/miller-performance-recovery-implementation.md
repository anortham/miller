---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-16T01:34:00.146Z
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

The exact production-volume full request improved from 148.431 s to 54.814 s wall and 44.673 s resolution at 26.831 MB peak PSS, passing the 60 s Linux gate by 5.186 s. Final producer HEAD `65bb7862` passes xtask 120/120, default 4,318, contracts 412, strict Clippy/format, and the three-run official gate at 29.522 s full / 11.002 s scoped with zero semantic/applied/row diffs. Exact installed `julie-extract 2.33.2` SHA-256 is `46562b537878f36a13adab629413a99daf490fa4b1b58a7b708b36a6eb373c7d`.

Miller branch `feature/performance-recovery` contains the Linux recovery and harness/docs commit at `695a74ed`. The exact tested consumer source remains `686dd6e4`; only harness, plan, and committed memory changed afterward. Release build, 6,566 fast tests plus four skips, 138 Scale tests plus ten skips, and the isolated 30-minute semantic soak all pass; the soak completed 17/17 probes with zero failures/hangs/reconnects and 14,648 queries/batches.

## Architecture Direction

Retain Tasks 9/11A/11B/13/14/15/16/17, validated-base proof reuse, scoped resolution, context batching, lazy family hydration, producer-owned indexes, full resolver oracle, fixed memory bounds, and all public contracts. Performance expansion is closed. The frozen all-14 replay fixture now selects an empty current view while populated views have stale/mismatched sidecars; record this as a fixture limitation rather than starting another Linux convergence campaign.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, and all relationship features.
- Preserve Linux and Windows paths, locks, process supervision, SQLite portability, and memory gates.
- Never mutate the protected Linux store or signal protected Miller PID 23058 / broker PID 24610.
- No new optimization or Linux fixture tasks unless native Windows exposes a concrete defect.
- No pin bump, push, tag, publish, or release without explicit approval.

## Next Work

Resume on native Windows from the exact Miller and Julie task branches. Run the bounded acceptance packet, then construct the safe three-run copied-store replay from the actual Windows workspace/live-store/disposable-snapshot paths. Reconcile `PERF.md`, dated findings, docs map, and all worktrees after Windows passes; stop at adoption/push/release approval boundaries.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `.razorback/sdd/2026-08-13-miller-performance-recovery-plan/task-17-report.md`
- `.miller/handoffs/latest.md`
- `/home/murphy/.miller/perf-task14-index-mDLBxS/full-replay-task17-statement-cache-count-plan.jsonl`
- `/home/murphy/.miller/perf-recovery-task17-semantic-soak/run-20260815T225337Z/summary.json`
- `PERF.md`
