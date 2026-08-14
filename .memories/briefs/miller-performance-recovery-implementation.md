---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T13:15:46.195Z
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

The frozen production-volume Linux baseline proves lock-winning leader startup and registered no-change open take about 27.5 seconds, ordinary tool processes take 6.7–7.5 seconds, a one-file resolve times out at 60 seconds, and full resolution spends 171.696 seconds inside resolution. Warm reader startup (about 0.85 seconds), identical producer retry (116–117 ms), and memory gates pass, so broad SQLite or memory pressure is not the primary fault.

## Constraints

- Preserve Store Contract v1 and existing MCP/CLI schemas and deterministic semantics.
- Every replay row must exercise the production path it names.
- Never mutate the live store or pointer; snapshots preserve SQLite durable state and use disposable supervision paths.
- Do not invoke Miller MCP or `miller serve` during recovery packets until the startup path is repaired; a read-oriented Miller call spawned a live leader and resolve. Use bounded shell/JQ/source fallback.
- Pointer adoption remains fail-closed and registry-first when usable lineage already exists.
- Keep context batching default-off until copied-store same-depth parity and measured benefit pass.
- Characterize shipped incremental behavior before editing it.
- Split lifecycle rebase/collection from query/index/statistics tuning.
- Keep Windows-specific coordinator, resolution, memory, copy/lock, and broker gates.
- No pin bump, push, tag, publish, or release without explicit approval.

## Success Criteria

Task 5B removes only the measured redundant no-change phase and meets startup/open budgets; Task 6 proves incremental resolver routing/equivalence; Tasks 7A/7B close retained-history and relationship read-plan costs independently; Task 8 closes Linux and Windows correctness, scale, memory, semantic, and timing gates.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/findings/2026-08-14-performance-recovery-baseline.md`
- `docs/plans/2026-08-14-validated-store-pointer-adoption-design.md`
- `docs/adr/ADR-0005-validated-store-pointer-adoption.md`
- `PERF.md`

## Status

Implementation active on `feature/performance-recovery`. Tasks 1–4, 1B-A/B/C, 2B, and 5A are complete. Task 5B is active. Baseline phase logs show each measured no-change leader spent 24.8–25.4 seconds in import; resolve then spent about 1.82–1.83 seconds and advanced the store sequence by 206, while warmed sidecars took only about 0.25–0.31 seconds. A bounded attribution packet is determining the exact request/journal semantics before the TDD repair. Tasks 6, 7A, 7B, and 8 remain open; Windows gates and the long Unix semantic socket-path defect remain open.
