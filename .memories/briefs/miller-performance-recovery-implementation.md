---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: active
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-16T11:22:23.344Z
tags:
  - performance
  - family-store
  - windows
  - linux
  - review
  - push-gated
---

## Goal

Restore Miller startup, indexing, relationship-query, and family-store performance to the published Linux and Windows budgets without disabling features or weakening correctness.

## Current State

Linux recovery and native Windows acceptance are complete. The Windows acceptance commits are already on the remote feature branches: Miller `02abf49a` (with evidence follow-up `11233ff1`) and Julie `37c81e5f`.

Exact-current Linux reconciliation added local Miller commit `21b73bcf` and local Julie commit `152f51e4`. These commits close the proxy teardown regression, Linux producer gates, and PERF-009 bridge trace budget; each branch is one commit ahead of its remote. Final evidence/docs and review correction are being committed before requesting renewed push approval.

## Evidence

Miller Release build passed with zero warnings/errors; final fast was 6,567 passed/4 skipped; Scale was 138/10 against the exact corrected producer before the CLI-only bridge change. Python combined gate was 168 total/2 skipped and proxy contracts were 17/17. Julie passed format, strict Clippy, manifest 27/27, import 4/4, and resolution 30/30.

PERF-009 improved CLI bridge trace from 6.62–6.65 s and about 408–410 MiB RSS to 1.44–1.47 s and about 180 MiB RSS with identical output. MCP bridge calls passed at 1.76–1.79 s and about 187 MiB PSS, so no bridge sidecar is needed.

Native Windows evidence remains the accepted 1,800-second semantic soak (26/26 probes) and strict replay (42 records across 14 workloads, zero measured hard-gate failures/nonzero exits).

## Constraints

- Preserve Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, relationship features, and Linux/Windows compatibility.
- Do not mutate the protected live Linux family or its Miller/broker processes.
- No push of the new local commits, producer adoption/pin bump, tag, publish, or release without the applicable explicit approval.

## Completion Boundary

Close final review and evidence locally, then request renewed approval to push both updated feature branches. Producer adoption/pinning and any release path remain separate approval-gated work.

## References

- `docs/findings/2026-08-13-performance-recovery-verification.md`
- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `PERF.md`
- Miller recovery code commit `21b73bcf`
- Julie correction commit `152f51e4`
