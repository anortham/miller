# Direction-aware impact reads design

Date: 2026-08-28

## Problem

The fixed six-path impact workload takes 8.19 to 8.30 seconds in a warm resident process. The
5-second product gate remains open.

The read-path spike measured 7.85 seconds inside query-time resolution. Reverse traversal spends a
warm median 1.89 seconds reading identifier sites within the current candidates and 1.71 seconds
reading pending sites from those candidates. Reverse consumers do not use either collection. The
named arms they do use take 498 milliseconds total.

The current `ResolveQuery` always reads both directions before `ReadResolutionEdges` and
`ReadUnresolvedNameEdges` select one direction from the shared scratch. The problem is excess work,
not a missing index. SQLite already selects the live manifest visibility index.

## Goal

Make graph resolution read only the site collections required by the requested direction while
preserving every graph edge and public output. Bring the fixed warm resident impact p95 to 5 seconds
or less. If this change does not close that gate, use its new phase breakdown to choose one separate
follow-up change.

## Non-goals

- Do not add a reference sidecar, cache, producer-store table, or materialized resolution.
- Do not change query-time resolution policy v6, resolver behavior, or identifier detail loading.
- Do not optimize by `GraphReadKind` in this change. The paired resolution and unresolved-name
  consumers reuse one scratch value and need the union of their data for the selected direction.
- Do not change graph ordering, confidence, provenance, truncation, compact output, or JSON output.
- Do not bundle lazy detail loading into this change.

## Design

### Direction becomes part of scratch construction

`ResolveGraphQuery` already accepts `Direction` and requires an exact direction match before it
reuses pending scratch. Pass that direction into `ResolveQuery` and build the scratch as follows:

| Requested direction | Identifier sites | Pending sites |
|---|---|---|
| `Forward` | sites within candidate symbols | sites whose source is a candidate symbol |
| `Reverse` | sites named after candidate symbols | sites whose target name matches a candidate symbol |
| `Both` | both collections | both collections |

Candidate lookup, identifier details, identifier resolution, pending resolution, and relationship
reads keep their current behavior for the rows selected above. The change removes unconsumed rows;
it does not change how retained rows resolve.

### Scratch reuse stays intact

The pending scratch key remains connection identity, candidate sequence, direction, and opposite
`GraphReadKind`. A scratch created for unresolved-name edges must still satisfy the paired resolution
edge read, and vice versa.

The first change must not build a smaller scratch based only on the first consumer kind. That would
make output depend on call order and could silently drop pending resolution edges.

### Telemetry proves the skipped work

Keep the existing `GraphResolutionBreakdown` fields. A skipped arm reports zero rows, zero operations,
and a nonnegative elapsed duration. `Both` continues to report the current complete split.

No new telemetry payload, MCP field, or candidate identifier is added.

## Data flow

1. The graph reader requests a candidate batch and direction.
2. `ResolveGraphQuery` reuses matching scratch or calls `ResolveQuery` with that direction.
3. `ResolveQuery` always reads candidates, then reads only the direction-required site arms.
4. The existing detail and resolver code processes the retained rows without policy changes.
5. Both graph consumers read the same scratch and emit edges in the existing order.
6. The statement observer records which arms ran and their row, operation, and time totals.

## Failure behavior

The change adds no new runtime failure mode or fallback. Cancellation, SQLite errors, and missing
facts continue through the existing paths. A partial scratch must never enter `_pendingScratch`.

If direction-specific parity fails, keep the current direction-blind implementation. If the exact
replay shows less than a 25 percent warm p95 improvement, revert the optimization and return to the
phase evidence before trying another change.

## Test design

Tests exercise the caller-facing `ReadResolutionEdges` and `ReadUnresolvedNameEdges` methods.

- A forward read reports zero identifier-named and pending-named operations.
- A reverse read reports zero identifier-within and pending-within operations.
- A `Both` read retains the existing controlled fixture counts.
- Calling the paired graph consumers in either order reuses scratch once and returns identical edges.
- Forward, reverse, and both-direction serialized graphs remain byte-identical to the current
  implementation for family-store and legacy-artifact fixtures.
- Existing resolution, homonym, pending override, QML, bounded-cache, and edge-order tests remain
  green.

Timing thresholds do not belong in unit tests. Deterministic operation counts guard the performance
behavior; the fixed replay supplies the wall-time evidence.

## Replay result

The 2026-08-29 fixed replay is recorded in
[`findings/2026-08-29-direction-aware-impact-reads.md`](../findings/2026-08-29-direction-aware-impact-reads.md).
The one-shot impact output retained the baseline hash, 53 impacted symbols, and 147 likely tests.
The resident warm samples were 4,513, 4,505, 4,510, 4,621, and 4,600 ms; p95/max was 4,621/4,621
ms. This is a 44.3% improvement from the 8,296 ms baseline, passing the 6,222 ms keep gate and the
5,000 ms product gate. Every resolution pass emitted a complete breakdown, with the direction-
opposite site arms reporting zero work.

## Measurement and acceptance

Use the exact spike workload: the same six changed paths, task worktree, depth 2, limit 200, and
sequential execution. Record one cold call and five warm resident calls. Do not change the dataset,
view, or command between before and after measurements.

Hard gates:

- Every output is byte-identical to the recorded baseline result.
- Every warm call has one complete breakdown for each resolution pass.
- Warm resident p95 improves by at least 25 percent from 8.296 seconds to 6.222 seconds or less.
- No warm sample exceeds the 8.296-second baseline maximum.
- Fast, Scale, Release, secrets, dependency, and worktree gates pass.

The product target is warm resident p95 at or below 5 seconds. If this change reaches it, stop. If it
lands between 5.001 and 6.222 seconds, keep the measured improvement and use the new breakdown to
decide whether lazy identifier-detail loading deserves a separate design. Do not assume that second
change is necessary before seeing the result.

## Architecture quality

- **Affected module:** `QueryTimeResolutionReader` graph scratch construction in `Miller.Indexing`.
- **Caller-facing interface:** unchanged. Callers already supply `Direction`.
- **Depth and locality:** direction selection stays inside the query-time reader that owns the SQL
  arms and scratch lifecycle.
- **Test surface:** existing graph read methods, serialized parity fixtures, and statement observer.
- **New seams:** none.
- **Rejected shortcuts:** sidecar, cache, parallel queries, kind-specific partial scratch, bundled
  lazy detail loading, and reduced result limits.
- **Architecture risk:** medium. The change is local, but a wrong scratch completeness rule can make
  edges depend on consumer order.

## Acceptance criteria

- [x] `ResolveQuery` reads only direction-required identifier and pending arms.
- [ ] Scratch reuse remains complete and independent of paired-consumer call order.
- [ ] Graph outputs remain byte-identical for forward, reverse, and both directions.
- [ ] Existing resolver policy, detail loading, edge ordering, and public contracts remain unchanged.
- [x] The exact replay meets the 25 percent improvement gate and records the 5-second product result.
- [ ] All repository branch gates pass on the final source tree.
