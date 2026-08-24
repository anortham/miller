---
id: close-every-miller-performance-audit-finding
title: Close every Miller performance-audit finding
status: active
created: 2026-08-24T06:21:51.511Z
updated: 2026-08-24T06:45:45.387Z
tags:
  - performance
  - continuous-testing
  - audit-closure
---

## Goal

Close every finding recorded in the 2026-08-23 Miller performance audit: CT polling/selection/storage lifecycle, disk pressure, cursor/status hygiene, and general impact/context hot paths.

## Why Now

The first CT-hardening slice fixed the released JSON regression and the largest measured daemon allocation cost, but the user explicitly wants zero deferred or open audit findings.

## Constraints

- Continue from `perf/ct-audit-2026-08-23`; do not merge partial work to `main` first.
- Measure every performance change before and after on a fixed workload.
- Preserve CT fast/Scale separation, read-only product boundaries, language parity, and public CLI/MCP contracts unless explicitly approved.
- Retain active runs, 30 days of history, and at least 50 outcomes per test.
- Prune inactive CT build caches after 7 days; enforce 2 GiB per-workspace and 8 GiB machine-wide caps; never delete active or newest-complete generations.
- No push, merge, release, or publication without explicit user approval.
- Keep each slice independently verified and the branch shippable.

## Success Criteria

- The performance-audit ledger has zero deferred or open findings; every item is fixed with evidence or conclusively retired by measurement.
- Deterministic regression guards cover operation counts, allocation, query plans, retention bounds, and status convergence where applicable.
- Final Release build, fast suite, Scale suite, live CT measurement, and related-worktree audit pass on one clean branch state.

## References

- `docs/findings/2026-08-23-performance-audit.md`
- `docs/plans/2026-08-23-ct-performance-hardening-design.md`
- `docs/plans/2026-08-23-ct-performance-hardening-plan.md`
- `docs/plans/2026-08-24-performance-audit-closure-design.md`
- `docs/plans/2026-08-24-performance-audit-closure-plan.md`
