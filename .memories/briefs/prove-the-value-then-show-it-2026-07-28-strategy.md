---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T03:21:40.771Z
tags:
  - strategy
  - performance
  - family-store
  - release-blocker
  - overnight
---

## Direction

Miller replaces the retired Julie agent-tool core, but release work is paused until the product is fast enough for routine agent use. The user approved an overnight autonomous performance-recovery run across Miller and julie-extractors. Continue through diagnosis, TDD fixes, integration, and bounded dogfood; stop only at an approval boundary or genuine blocker.

## Performance mandate

- Treat slow interactive reads, producer imports, resolution, retries, and CPU/RSS spikes as release blockers.
- `PERF.md` at the Miller repository root is the canonical live incident ledger. Record every discovered bottleneck, evidence, root cause, fix commit, and acceptance gate there before moving on.
- Add phase/query telemetry and profile once before changing code; fix the largest measured bottleneck, verify with one focused replay, then move to the next.
- Do not repeat any operation over 60 seconds without new phase-level evidence. Do not run three-repeat performance gates until a focused fix is green.
- Warm Miller inspect targets <=500ms; context/impact/trace <=2s on the development machine. Constrained Windows-oriented budgets are <=2s inspect and <=5s graph tools.
- Retained private/PSS target is <=350MB per idle host; ordinary read peak <=600MB.
- One-file Julie resolution target <=5s and full Miller-corpus resolution <=60s.
- No-op and byte-identical retry work must be near-zero. Background work must be bounded enough for a corporate Windows laptop running other applications.

## Architecture authority

The producer/consumer ownership boundary remains: julie-extractors owns extraction and family-store writes; Miller owns reads and derived sidecars. The user explicitly authorized changes in either repository, including redesigning database internals where measurements show the current design cannot meet the performance budget. Preserve public correctness, crash recovery, and versioned compatibility; do not protect a slow internal design for its own sake.

## Current proven evidence

- Miller family-store context/impact/trace hydrate and retain a complete repository index and graph per process/generation.
- Family-store inspect hydrates a workspace-sized symbol projection instead of using the existing generation-checked FTS sidecar.
- More critically, store bootstrap and FreshnessService rebuild the complete 223,716-symbol repository index in every host on every revision even with no active tool call. One reader reached 101.5GB logical reads, 24.8M read syscalls, ~1GB RSS, and sustained CPU; several hosts retained ~5.4GB PSS total.
- Julie scoped resolution separately recorded ~98.1GB logical reads and 24.1M read syscalls for 199,123 rows/47 names. The global candidate-window cutoff leaves high-fanout names unprimed and repeats equivalent candidate SQL.
- Scope crossover reduced ~20-minute incidents to ~165.5s, still above budget.
- Byte-identical cross-key artifact retry is fixed on Julie main at 70cd205f. Writer lease heartbeat is fixed at 0500ab1e.

## Active worktrees

- Miller: `perf/family-store-read-performance` in `.worktrees/family-store-read-performance`.
- Julie: `perf/store-resolution-query-amplification` in `.worktrees/fix-store-resolution-query-amplification`.
- Keep .NET and Rust compiles serialized on the performance machine.

## Release status and constraints

- Stable Miller v1.18.1 and julie-extract v2.32.0 remain published. Candidate releases are paused pending PERF-001 through PERF-005 closure.
- No new MCP tools without explicit approval. Existing-tool telemetry may be expanded.
- No push, tag, publish, marketplace advertisement, deploy, or release without explicit user approval.

## References

- `PERF.md`
- `docs/plans/2026-08-11-family-store-performance-recovery-design.md`
- `docs/plans/2026-08-11-family-store-performance-recovery-plan.md`
- Julie `docs/plans/2026-08-11-store-resolution-query-amplification-design.md`
- Julie `docs/plans/2026-08-11-store-resolution-query-amplification-plan.md`
