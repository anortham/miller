---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-16T11:25:16.124Z
tags:
  - performance
  - family-store
  - windows
  - linux
  - verified
  - push-gated
---

## Goal

Restore Miller startup, indexing, relationship-query, and family-store performance to the published Linux and Windows budgets without disabling features or weakening correctness.

## Current State

The performance-recovery implementation, Linux verification, native Windows acceptance, evidence reconciliation, and final reviews are complete.

Remote Windows-acceptance state:
- Miller `02abf49a` plus evidence follow-up `11233ff1`
- Julie `37c81e5f`

Verified local state awaiting renewed push approval:
- Miller recovery code `21b73bcf` plus final evidence/review commit `c0e4fbd4`
- Julie Linux correction `152f51e4`

## Evidence

Miller Release build passed with zero warnings/errors; final exact-tree fast was 6,567 passed/4 skipped; Scale was 138/10 against the exact corrected producer before the CLI-only bridge change. Python combined gate was 168 total/2 skipped and proxy contracts were 17/17. Julie passed format, strict Clippy, manifest 27/27, import 4/4, and resolution 30/30.

PERF-009 improved CLI bridge trace from 6.62–6.65 s and about 408–410 MiB RSS to 1.44–1.47 s and about 180 MiB RSS with identical output. MCP bridge calls passed at 1.76–1.79 s and about 187 MiB PSS, so no bridge sidecar is needed.

Native Windows evidence remains the accepted 1,800-second semantic soak (26/26 probes) and strict replay (42 records across 14 workloads, zero measured hard-gate failures/nonzero exits). Final producer review had no findings; final Miller findings were corrected and the exact-tree fast gate passed afterward.

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, relationship features, and Linux/Windows compatibility.
- Do not mutate the protected live Linux family or its Miller/broker processes.
- No push of the new local commits, producer adoption/pin bump, tag, publish, or release without the applicable explicit approval.

## Completion Boundary

Request renewed approval to push both updated feature branches. Producer adoption/pinning and any release path remain separate approval-gated work.

## References

- `docs/findings/2026-08-13-performance-recovery-verification.md`
- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `PERF.md`
- Miller commits `21b73bcf`, `c0e4fbd4`
- Julie commit `152f51e4`
