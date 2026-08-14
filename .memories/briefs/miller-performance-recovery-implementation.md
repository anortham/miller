---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T11:16:11.026Z
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

Faithful copied-store evidence now confirms lock-winning leader startup at about 27.8 seconds against a 2-second development budget. Earlier measurements showed default relationship context around 11.93 seconds, exact resolution around 164–172 seconds, and about 1.12 GB of retained resolution state. A stranded claimed import demonstrated that coordination failures can compound convergence and overlay cost.

## Constraints

- Preserve Store Contract v1 and existing MCP/CLI schemas and deterministic semantics.
- Every replay row must exercise the production path it names.
- Never mutate the live store or pointer; snapshots preserve SQLite durable state and use disposable supervision paths.
- Pointer adoption remains fail-closed and registry-first when a usable lineage already exists.
- Model the staged Miller view, original source view, and disposable changed-source view separately.
- Keep context batching default-off until copied-store same-depth parity passes.
- Characterize shipped incremental behavior before editing it.
- Split lifecycle rebase/collection from query/index/statistics tuning.
- Keep Windows-specific coordinator, resolution, memory, copy/lock, and broker gates.
- No pin bump, push, tag, publish, or release without explicit approval.

## Success Criteria

Task 1B-B captures the immutable production-volume baseline; Task 5 measures before behavior repair; Tasks 6, 7A, and 7B close resolver, lifecycle, and read-path costs independently; Task 8 closes Linux and Windows correctness, scale, memory, semantic, and timing gates.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/plans/2026-08-14-validated-store-pointer-adoption-design.md`
- `docs/adr/ADR-0005-validated-store-pointer-adoption.md`
- `PERF.md`

## Status

Implementation active on `feature/performance-recovery`. Tasks 1–4, Task 5A, Task 2B, and Task 1B-C are complete. Task 1B-B is active. Verified replay foundations include 87 Python harness tests, 24 resolver tests, 97 coordinator/bootstrap tests, a 17ms real identical producer retry, and a real empty-registry leader adopting the copied family/view without minting. The same leader path took 27.75 seconds, so correctness is restored but the startup regression remains severe.
