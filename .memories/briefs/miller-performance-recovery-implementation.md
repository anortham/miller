---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T10:20:25.713Z
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

- Preserve Store Contract v1 and all existing MCP/CLI schemas and deterministic semantics.
- A replay row must exercise the production path it names; CLI status/leader reads are diagnostic controls, not startup measurements.
- Never mutate the live store. Refuse live/unknown owners. Preserve WAL mode, durable WAL content, WAL-free database identity, and required empty runtime directories in snapshots.
- Validated pointer adoption may repair missing isolated registry lineage only after catalog/view/root/generation/base/artifact checks pass; invalid pointers fail without registry mutation.
- The replay models Julie's original read-only source root separately from Miller's staged workspace root.
- Keep context batching default-off until same-depth batch parity and copied-store lexical timing pass.
- Characterize shipped incremental behavior before editing it.
- Split store lifecycle rebase/collection from query/index/statistics tuning so each has independent evidence and rollback.
- Keep Windows compatibility and dedicated Windows coordinator/resolution, memory, copy/lock, and broker gates.
- Keep source changes in the named Miller and Julie recovery worktrees.
- No pin bump, push, tag, publish, or release without explicit approval.

## Success Criteria

Completed Tasks 1–4 remain reviewed but production-volume unproven. Task 1B supplies faithful MCP/producer workloads and the immutable baseline; Task 2B closes resolve-claim and copied-field recovery; Task 5 measures before any behavior repair; Tasks 6, 7A, and 7B close resolver, lifecycle, and read-path costs separately; Task 8 closes Linux and Windows correctness, scale, memory, semantic, and timing gates.

## References

- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `docs/plans/2026-08-14-validated-store-pointer-adoption-design.md`
- `docs/adr/ADR-0005-validated-store-pointer-adoption.md`
- `PERF.md`

## Status

Implementation active on `feature/performance-recovery`. Tasks 1–4, Task 5A, and Task 2B are complete. Task 1B-B remains active. Root-cause tracing proved the remaining replay blocker is structural: producer and Miller roots were conflated, while leader bootstrap ignored a valid copied-family pointer when the isolated registry lacked lineage. The accepted design adds fail-closed pointer adoption and split replay roots before regenerating the baseline.
