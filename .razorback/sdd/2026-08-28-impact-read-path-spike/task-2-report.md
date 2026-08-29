# Task 2 report: fixed impact replay and SQL falsification

## Status

Complete as a measurement-only packet. No production or test code changed. The required three docs
were updated; the finding rejects a reference sidecar as the next change and recommends an ordered
ladder: direction/consumer-aware graph reads first, followed by separately measured lazy detail
hydration only if the first change still misses the gate.

## Replay evidence

- Workload: exactly the six changed paths in `task-2-brief.md`, workspace
  `/home/murphy/source/miller/.worktrees/tool-latency-health`, `--max-depth 2`, `--limit 200`,
  sequential, quiet, rebuilt Release binary.
- One-shot CLI: cold `10.92 s`; warm `10.89 / 10.76 / 10.80 / 10.90 / 10.79 s`; p95/max
  `10.90 / 10.90 s`, below the `11.30 s` instrumentation gate.
- All CLI outputs were byte-identical, SHA-256
  `fc9ad40c061d620c346a90866dda9ea47fcb81ce3af081caa00ec3931e2ca483`, with 53 impacted symbols
  and 147 tests.
- Matching branch MCP replay: six correlation IDs, seven complete breakdowns per call. Cold tool
  call `13,272 ms` (discarded); warm calls `8,189 / 8,262 / 8,296 / 8,244 / 8,271 ms`, p95/max
  `8,296 / 8,296 ms`. Candidate batches were `396 / 329 / 457 / 500 / 171 / 286 / 286` on
  every call.
- Warm median phase totals (ms / rows / operations): candidate lookup `557 / 2,425 / 23`;
  identifier within `1,893 / 103,599 / 23`; identifier named `248 / 34,695 / 2,347`;
  pending within `1,710 / 26,983 / 23`; pending named `250 / 2,347 / 2,347`; identifier
  details `1,014 / 135,097 / 1,060`; identifier resolution `1,351 / 135,097 / 135,097`;
  pending resolution `493 / 45,409 / 45,409`; relationships `337 / 6,129 / 23`.

## SQL evidence

- Read-only database: family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, `CURRENT=gen-001`, view
  `26382897-e460-4b0a-a6a1-3e4f67201aea`, root the task worktree, manifest generation `142`,
  resolution state `unbound`, producer `2.37.2`.
- One SQLite URI `mode=ro` connection created only TEMP `_miller_visible_entries` plus its path
  and version indexes. Each shape had one warmup and six interleaved repeats, fetching the full
  production named-site column list.
- Current manifest visibility and the equivalent TEMP projection returned equal rows for all
  names: `Run 1,143`, `Path 7,789`, `Equal 13,086`, `Assert 28,145`. No-visibility upper-bound
  rows were `12,713 / 61,948 / 97,185 / 211,416` respectively.
- Median current/TEMP/no-visibility milliseconds were respectively `Run 5.834/3.242/25.634`,
  `Path 33.136/20.038/122.470`, `Equal 50.640/32.448/197.254`, and
  `Assert 103.809/68.524/396.657`.
- `EXPLAIN QUERY PLAN` for current used `idx_read_identifiers_name_kind` then the covering
  `idx_read_manifest_entries_version(version_id,view_id,generation)`; TEMP used the analogous
  TEMP version index; no-visibility used only the identifier-name index. All used a temporary
  B-tree for ordering.

## Finding

The existing visibility index is real and the visibility join is not the dominant cause. Named
arms total only 498 ms of the 7,853 ms warm median breakdown; detail and resolver loops plus the
within-symbol arms account for the rest. Proof traversal can amplify cost by triggering repeated
full resolution passes, while relationship/supplemental SQL itself is small. The sidecar is rejected
as the next implementation. The evidence-backed ladder is: (1) implement direction/consumer-aware
graph reads only, preserving scratch reuse, resolver behavior, details, and outputs, then rerun;
(2) only if still above 5,000 ms, separately defer identifier-detail hydration with its own parity
tests and replay.

## Miller evidence

- `context` located the resolution reader, store visibility, and connection-local projection.
- `search` located `SitesNamed`, `SitesWithinSymbols`, the graph breakdown logger, and the existing
  manifest index before source inspection.
- `inspect` confirmed `ResolveQuery`, `TryUniqueNameTarget`, `BatchNeighbourEvidence`, and the
  existing `CreateCompatibilityProjection` shape.

## Verification and worktree

- `git diff --check`: passed.
- Broader tests/build/security/dependency gates remain lead-owned.
- Path: `/home/murphy/source/miller/.worktrees/tool-latency-health`
- Branch: `fix/tool-latency-health`
- HEAD before the worker commit: `8824d5194be5b3e61b8b30dc83d46f1eb6c3052c`
- Dirty state before the worker commit: only the three owned documentation files were modified or
  untracked; ignored `.miller` runtime logs were not part of the commit.

## Risks and blockers

- The product impact gate is still open at roughly 8.3 s warm on the resident branch MCP path.
- The two ordered read changes need separate implementation designs, exact-reference parity tests,
  and replays; neither is implemented here.
