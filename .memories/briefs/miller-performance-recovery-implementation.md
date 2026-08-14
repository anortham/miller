---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-14T08:47:12.883Z
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
- Never mutate the live store. Refuse live/unknown owners. For SQLite inputs, capture source content/metadata, stream-copy the durable main/WAL pair to a private shadow, revalidate durable source facts, let SQLite rebuild transient SHM only in the shadow, then back up and validate/digest the WAL-free destination before atomic promotion.
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
- `PERF.md`

## Status

Implementation active on `feature/performance-recovery`. Tasks 1–4 and Task 5A are committed and lead-reviewed. Task 1B-A is committed and lead-verified. The first Task 1B-B snapshot attempt safely aborted because a reader changed transient source SHM; official SQLite documentation confirms SHM contains no database content and is rebuilt from WAL. The snapshot helper is being narrowed to durable main+WAL inputs before retrying the copied-store baseline.
