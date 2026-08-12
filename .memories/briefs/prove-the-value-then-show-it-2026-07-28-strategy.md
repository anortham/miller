---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T05:25:07.948Z
tags:
  - strategy
  - performance
  - family-store
  - release-blocker
  - overnight
---

## Direction

Miller replaces the retired Julie agent-tool core, but release work is paused until routine reads and producer convergence meet performance budgets. The user approved an overnight autonomous recovery run across both repositories. Continue through measurement, TDD fixes, integration, and bounded dogfood; stop only at an approval boundary or genuine blocker.

## Mandate

- `PERF.md` is the canonical incident ledger. Record bottlenecks, disproven hypotheses, fix commits, and gates.
- Measure once, fix the largest proven bottleneck, replay once, then move on. Never repeat unchanged work over 60 seconds.
- Warm Miller inspect <=500 ms; context/impact/trace <=2 s. Windows-oriented <=2 s / <=5 s.
- Idle PSS <=350 MB; ordinary read peak <=600 MB.
- One-file Julie resolution <=5 s; full Miller-corpus resolution <=60 s.
- No-op/retry work near-zero; CPU/concurrency viable on a corporate Windows laptop.

## Proven state

- Miller disk-backed family reads: `dabcddd7`; lazy bridge: `1fa03ac9`; lazy generation-pinned holder/bootstrap/freshness: `4f7ff626`; bounded real read telemetry: `75e86c0a` with 456/456 affected tests green.
- Rebuilt-host Miller latency/PSS/idle-I/O and registration cancellation dogfood remain.
- Julie import reuse: `70cd205f`; writer heartbeat: `0500ab1e`; scope crossover: `f39d7263`/`fb31da08`.
- Julie query/caller telemetry: `bdf2076c`, `27a3e420`, `5089c3a2`.
- Faithful Julie wall remains about 50 seconds: roughly 25 seconds resolver plus 24 seconds finalization.
- Two caller guesses were disproven and removed. Exact caller telemetry then proved Pending owns all 10,804 locator calls.
- The exact Pending batching implementation removed all 10,804 locator statements and reduced candidate statements 14,980 -> 4,176, but replay regressed from 49.88 to 50.46 seconds and resolver 24.813 -> 25.418 seconds. It was removed and rejection documented at `b8abd489`.
- Next target: instrument `finish_exact` fixed phases—prior-overlay materialization, totality, row streaming, target/integrity validation, sync/publication—then optimize only the largest measured phase. After that, row-heavy PrimeWindow (313,107 rows) and IdentifierHydration (381,722 rows) remain candidates.

## Worktrees

- Miller: `/home/murphy/source/miller/.worktrees/family-store-read-performance`, `perf/family-store-read-performance`.
- Julie: `/home/murphy/source/julie-extractors/.worktrees/fix-store-resolution-query-amplification`, `perf/store-resolution-query-amplification`.
- Serialize .NET/Rust compiles. Do not register/open the Miller performance worktree through old hosts again.

## Release constraints

- Stable Miller v1.18.1 and julie-extract v2.32.0 remain published. Releases paused pending PERF-001 through PERF-005.
- No new MCP tools without approval.
- No push, tag, publish, marketplace advertisement, deploy, or release without explicit approval.

## References

- Miller `PERF.md`
- Miller family-store performance design/plan
- Julie store-resolution query-amplification design/plan
