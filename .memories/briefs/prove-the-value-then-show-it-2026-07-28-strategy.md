---
id: prove-the-value-then-show-it-2026-07-28-strategy
title: Prove the value, then show it — 2026-07-28 strategy
status: active
created: 2026-07-29T00:13:39.439Z
updated: 2026-08-12T05:03:54.934Z
tags:
  - strategy
  - performance
  - family-store
  - release-blocker
  - overnight
---

## Direction

Miller replaces the retired Julie agent-tool core, but release work is paused until routine reads and producer convergence meet the performance budgets. The user approved an overnight autonomous recovery run across Miller and julie-extractors. Continue through measurement, TDD fixes, integration, and bounded dogfood; stop only at an approval boundary or genuine blocker.

## Performance mandate

- `PERF.md` is the canonical live incident ledger. Record every bottleneck, disproven hypothesis, fix commit, and acceptance gate.
- Measure once, fix the largest proven bottleneck, run one focused replay, then move to the next. Never repeat an unchanged operation over 60 seconds.
- Warm Miller inspect target: <=500 ms; context/impact/trace: <=2 s. Constrained Windows targets: <=2 s and <=5 s.
- Idle retained private/PSS: <=350 MB per host; ordinary read peak: <=600 MB.
- One-file Julie resolution: <=5 s; full Miller-corpus resolution: <=60 s.
- No-op and byte-identical retry work must be near-zero; CPU and concurrency must remain viable on a corporate Windows laptop.

## Architecture authority

julie-extractors owns extraction and family-store writes; Miller owns reads and derived sidecars. The user authorized changes in either repository, including database redesign where measurements require it. Preserve correctness, crash recovery, platform parity, and public compatibility; do not preserve a slow internal design for its own sake.

## Current proven state

- Miller family reads are disk-backed at `dabcddd7`; lazy bridge parity is at `1fa03ac9`; bootstrap/freshness repository hydration is lazy and generation-pinned at `4f7ff626`.
- Miller bounded read telemetry is at `75e86c0a`: real provider, lookup, graph, and cache facts, with 456/456 affected tests green. Rebuilt-host latency/PSS/idle-I/O dogfood remains.
- Julie byte-identical cross-key import reuse is on main at `70cd205f`; writer heartbeat is at `0500ab1e`; scope crossover is at `f39d7263`/`fb31da08`.
- Faithful Julie baseline is about 49.8 s wall: 24.8 s resolver plus about 25 s finalization. LocateIdentifier executes 10,804 times.
- Two caller inferences were disproven and their uncommitted production slices removed: materialized relationship coverage executed zero times; resolved-pending co-location hydration also executed zero times.
- Test-only caller telemetry at `5089c3a2` proves exact attribution: Pending=10,804; ResolvedPending=Relationships=Other/Unset=0. The earliest live snapshot already had Pending=8,109 at 1.047 s.
- Current Task 3, selected at `82c77ff8`, adds a session Pending wrapper with complete/optional co-location. Store mode hydrates it in existing bounded pages; other adapters preserve fallback.
- Exact finalization remains a separate roughly 25-second blocker after the Pending locator fix.

## Active worktrees

- Miller: `/home/murphy/source/miller/.worktrees/family-store-read-performance`, branch `perf/family-store-read-performance`.
- Julie: `/home/murphy/source/julie-extractors/.worktrees/fix-store-resolution-query-amplification`, branch `perf/store-resolution-query-amplification`.
- Keep .NET and Rust compiles serialized. Do not register/open the Miller performance worktree again until rebuilt-host cancellation is measured.

## Release constraints

- Stable Miller v1.18.1 and julie-extract v2.32.0 remain published. Candidate releases are paused pending PERF-001 through PERF-005.
- No new MCP tools without explicit approval.
- No push, tag, publish, marketplace advertisement, deploy, or release without explicit user approval.

## References

- Miller `PERF.md`
- Miller `docs/plans/2026-08-11-family-store-performance-recovery-{design,plan}.md`
- Julie `docs/plans/2026-08-11-store-resolution-query-amplification-{design,plan}.md`
