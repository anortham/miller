---
id: miller-performance-recovery-investigation
title: Miller performance recovery investigation
status: completed
created: 2026-08-14T02:25:55.234Z
updated: 2026-08-14T02:46:06.294Z
tags:
  - performance
  - startup
  - indexing
  - relationships
  - windows
  - linux
---

## Outcome

The investigation is complete at `main` commit `321e546282df03fd077297a659fb5c3eb45d44d5`.

- Warm reader startup is healthy (~0.33s); leader delta/convergence and cold producer work create the startup drag.
- The current producer coordinator is wedged by a dead owner with an expired claimed import and an unbound view.
- Exact resolution is the indexing bottleneck: 100 recorded resolves total 25,519s, averaging 255.2s; recent full/crossover phases take ~164–172s.
- Relationship-aware context performs enrichment before token packing: a quiet fixed workload is ~1.38s at depth 0 versus ~11.93s at depth 1 with byte-identical output.
- Impact expands family-store graph work before its result limit (~2.2s on the fixed workload).
- The 2.44GB store contains ~1.12GB of live resolution overlay tables/indexes; base overlay queries scan large tables and planner statistics are absent.
- Raw tree-sitter extraction, jobs, OOM, semantic startup, and generic SQLite connection setup are ruled out as primary causes.

## Recommended sequence

1. Repair the stranded producer row/lease and diagnose exit 135/unbound state.
2. Skip resolve/import for reused manifests; finish incremental resolution with crossover fallback.
3. Rebase/compact/GC exact overlays and benchmark base query plans before changing indexes.
4. Pre-pack context candidates, then batch exact reference enrichment only for survivors.
5. Cap impact traversal before output limits.
6. Add phase/count telemetry and Linux/Windows acceptance workloads.
