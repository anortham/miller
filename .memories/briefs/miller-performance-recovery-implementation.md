---
id: miller-performance-recovery-implementation
title: Miller performance recovery implementation
status: completed
created: 2026-08-14T03:57:03.433Z
updated: 2026-08-16T12:03:09.797Z
tags:
  - performance
  - family-store
  - windows
  - linux
  - verified
  - pushed
  - completed
---

## Goal

Restore Miller startup, indexing, relationship-query, and family-store performance to the published Linux and Windows budgets without disabling features or weakening correctness.

## Completed State

The performance-recovery implementation, Linux verification, native Windows acceptance, PERF-008/009 dogfood, evidence reconciliation, final reviews, and branch delivery are complete.

Published feature-branch heads:
- Miller `feature/performance-recovery` at `3cd0b3094919fe7ad504682fbcd04ea95906a59e`
- Julie `feature/miller-performance-recovery-producer` at `152f51e445517a818e6ffba2248318571f4eed6d`

Both remote refs were fetched and verified byte-for-byte against the clean local task worktrees after push.

## Evidence

Miller Release build passed with zero warnings/errors; final exact-tree fast was 6,567 passed/4 skipped; Scale was 138/10 against the exact corrected producer before the CLI-only bridge change. Python combined gate was 168 total/2 skipped and proxy contracts were 17/17. Julie passed format, strict Clippy, manifest 27/27, import 4/4, and resolution 30/30.

PERF-009 improved CLI bridge trace from 6.62–6.65 s and about 408–410 MiB RSS to 1.44–1.47 s and about 180 MiB RSS with identical output. MCP bridge calls passed at 1.76–1.79 s and about 187 MiB PSS, so no bridge sidecar is needed.

PERF-008 captured Miller's actual child argv on a 24-core Linux host: the default remained `--jobs 4`, no component selected all-core mode, and process CPU stayed near one core. Cold full indexing remains expensive: about 154.97 s through Miller and 243.39 s direct, with 881,572 KB and 1,663,236 KB maximum RSS respectively. The gate closes workstation-saturation risk, not cold-index latency.

Native Windows evidence is the accepted 1,800-second semantic soak (26/26 probes) and strict replay (42 records across 14 workloads, zero measured hard-gate failures/nonzero exits). Final producer review had no findings; final Miller findings were corrected and the exact-tree fast gate passed afterward.

## Constraints Preserved

- Store Contract v1, MCP/CLI schemas, deterministic output, semantic default-on behavior, relationship features, and Linux/Windows compatibility remain intact.
- The protected live Linux family and its Miller/broker processes were not mutated.
- Further cold full-index optimization is a separate campaign.
- Producer adoption/pinning, tags, publishing, merging, and release remain separate approval-gated work.

## References

- `docs/findings/2026-08-13-performance-recovery-verification.md`
- `docs/plans/2026-08-13-miller-performance-recovery-plan.md`
- `PERF.md`
- Miller `3cd0b3094919fe7ad504682fbcd04ea95906a59e`
- Julie `152f51e445517a818e6ffba2248318571f4eed6d`
