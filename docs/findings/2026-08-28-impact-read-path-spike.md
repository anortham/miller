# Impact read-path spike — fixed replay and SQL falsification

Date: 2026-08-28. Branch: `fix/tool-latency-health` at `8824d519`. This diagnostic consumes the
resolution breakdown instrumentation from `08e0e38a`/`f8cb9a29` and does not change graph semantics,
the producer store, or any read query.

## Result

The 5-second impact gate remains open, but a reference sidecar is not justified by this evidence.
The dominant measured work is the within-symbol read arms plus query-time resolution, not the
name-visibility join:

- Warm median breakdown time is 7,853 ms. `IdentifierWithin` is 1,893 ms for 103,599 rows,
  `PendingWithin` is 1,710 ms for 26,983 rows, and identifier/pending detail plus resolver work is
  another 2,858 ms (1,014 + 1,351 + 493).
- The named arms are 248 ms (`IdentifierNamed`) plus 250 ms (`PendingNamed`), 6.3% of the
  breakdown. They perform 2,347 name probes and return 34,695 and 2,347 rows respectively.
- The named-name consumer does not read `ResolvedIdentifier.Details` in `TryUniqueNameTarget`; the
  shared `ResolveQuery` currently hydrates details before both graph consumers use the scratch. This
  is a measured candidate for lazy consumer-specific work, but this spike does not implement it.
- The next implementation must be measured as an ordered ladder, one change at a time. First split
  `ResolveQuery` into direction/consumer-specific graph-read arms only, preserving scratch reuse,
  resolver behavior, detail loading, edge ordering, and output contracts; then re-run this exact
  replay. Only if that result still misses 5,000 ms should a separate second change defer identifier-
  detail hydration, with its own exact-reference parity tests and replay. This keeps the 3.6 s within
  arms and the 2.36 s identifier detail/resolution work attributable instead of bundling two fixes.

The sidecar is therefore rejected as the next change. It would address the wrong subphase: even a
perfect removal of the named visibility work could not explain the 7.8 s warm breakdown. A sidecar
remains a last-resort option only if both ordered read changes, measured separately, leave the
within/detail/resolver costs above the product gate.

## Fixed replay

Every run used the rebuilt Release binary and this exact command shape, sequentially, with the six
paths below, workspace `/home/murphy/source/miller/.worktrees/tool-latency-health`, `--max-depth 2`,
`--limit 200`, and `--json`:

```text
src/Miller.Server/bin/Release/net10.0/miller impact --changed-paths \
  src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs,\
  src/Miller.Server/Cli/CliDispatch.cs,\
  src/Miller.Server/Tools/WorkspaceRender.cs,\
  src/Miller.Server/Tools/WorkspaceTool.cs,\
  src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs,\
  src/Miller.Server/Workspaces/WorkspaceRemoval.cs \
  --workspace /home/murphy/source/miller/.worktrees/tool-latency-health \
  --max-depth 2 --limit 200 --json
```

The one-shot replay was one cold call followed by five warm calls. The cold call took 10.92 s; warm
calls took 10.89, 10.76, 10.80, 10.90, and 10.79 s. All six outputs were byte-identical at SHA-256
`fc9ad40c061d620c346a90866dda9ea47fcb81ce3af081caa00ec3931e2ca483`, with 53 impacted symbols and
147 likely tests. The warm nearest-rank p95/max was 10.90/10.90 s, below the instrumentation hard
gate of 11.30 s. The one-shot result includes process startup and is the contract replay; phase
breakdowns came from the matching branch binary in one resident MCP process.

The instrumented process emitted six correlation IDs, seven complete breakdowns per call, and the
same candidate-batch shape on every call: 396, 329, 457, 500, 171, 286, 286 candidates. The cold
MCP call was 13,272 ms and is discarded for warm analysis. Warm tool-call durations were 8,189,
8,262, 8,296, 8,244, and 8,271 ms; warm p95/max was 8,296/8,296 ms. Correlation IDs, in replay
order, were:

| call | correlation ID | tool ms |
|---:|---|---:|
| cold | `01a04b31-25f0-7b55-8fb4-99358140239e` | 13,272 |
| warm 1 | `01a04b31-59d1-7645-a329-1e6d7d8f27b9` | 8,189 |
| warm 2 | `01a04b31-79d0-75ac-ae10-38015a0fa898` | 8,262 |
| warm 3 | `01a04b31-9a17-71f2-ab1c-4df32b956853` | 8,296 |
| warm 4 | `01a04b31-ba81-7df8-a3d9-f92213f8b8ab` | 8,244 |
| warm 5 | `01a04b31-dab7-7702-a047-6445d4674187` | 8,271 |

Each `ms/rows/ops` cell below is the sum of all seven complete breakdown records for that call.
Rows and operations are deterministic across all calls; the warm timing values are in warm-call
order. This reports every required subphase without exposing candidate names or symbol IDs.

| subphase | cold ms / rows / ops | warm ms (1..5) | rows / ops |
|---|---:|---:|---:|
| candidate lookup | 559 / 2,425 / 23 | 551, 562, 561, 557, 555 | 2,425 / 23 |
| identifier within | 1,947 / 103,599 / 23 | 1,880, 1,895, 1,898, 1,893, 1,893 | 103,599 / 23 |
| identifier named | 248 / 34,695 / 2,347 | 281, 239, 248, 263, 238 | 34,695 / 2,347 |
| pending within | 1,715 / 26,983 / 23 | 1,692, 1,728, 1,732, 1,710, 1,704 | 26,983 / 23 |
| pending named | 253 / 2,347 / 2,347 | 255, 241, 249, 250, 259 | 2,347 / 2,347 |
| identifier details | 1,045 / 135,097 / 1,060 | 1,014, 1,018, 1,008, 1,014, 1,028 | 135,097 / 1,060 |
| identifier resolution | 1,680 / 135,097 / 135,097 | 1,302, 1,351, 1,375, 1,301, 1,369 | 135,097 / 135,097 |
| pending resolution | 497 / 45,409 / 45,409 | 493, 502, 493, 500, 490 | 45,409 / 45,409 |
| relationships | 343 / 6,129 / 23 | 332, 339, 350, 337, 337 | 6,129 / 23 |

The phase labels are the existing `GraphStatementPhase` labels. The seven breakdowns are attached
to the existing `unresolved_name_forward` observations because scratch reuse emits the breakdown
once per completed resolution pass; the paired family-resolution observations remain unchanged.
The repeated candidate shape, complete nine-field records, fixed row/operation counts, and
byte-identical one-shot output establish graph/result parity for this diagnostic.

## SQL falsification

The live read-only store was rechecked before the experiment:

| fact | value |
|---|---|
| family | `a271f2bd-7368-4da6-b5aa-24ffad69fb1f` |
| generation directory | `gen-001` (`CURRENT` = `gen-001`) |
| view | `26382897-e460-4b0a-a6a1-3e4f67201aea` |
| root | `/home/murphy/source/miller/.worktrees/tool-latency-health` |
| manifest generation | `142` |
| resolution state | `unbound` |
| producer binary | `2.37.2` |

One SQLite connection opened `gen-001/store.db` with URI `mode=ro`. It created only the connection-
local `_miller_visible_entries` TEMP table and its two TEMP indexes, matching the existing projection:
`_miller_visible_entries_path(path)` and `_miller_visible_entries_version(version_id)`. Main-store
data was not changed. Each query fetched the full production `SitesNamed` column list, after one
warmup, in six interleaved repeats per name.

The current query uses `main.manifest_entries` visibility; the alternative joins the equivalent
TEMP projection; the upper bound omits visibility entirely. `Run` came from a fixed replay candidate
sample. `Path`, `Equal`, and `Assert` were selected as global visible high-fanout stress names.
Current and TEMP result counts are equal for every name. The no-visibility count is intentionally an
upper bound across retained views/versions.

| name | visible rows | no-visibility rows | current manifest join ms (6 repeats) | TEMP projection ms (6 repeats) | no-visibility ms (6 repeats) |
|---|---:|---:|---|---|---|
| `Run` | 1,143 | 12,713 | 5.161, 5.871, 6.078, 4.877, 5.973, 5.797 | 3.220, 4.143, 3.264, 3.006, 4.093, 3.216 | 26.080, 25.481, 24.938, 26.156, 25.148, 25.786 |
| `Path` | 7,789 | 61,948 | 26.993, 33.484, 32.811, 28.439, 33.460, 33.705 | 18.961, 25.048, 19.798, 19.481, 24.184, 20.277 | 122.993, 121.434, 122.304, 124.468, 121.845, 122.635 |
| `Equal` | 13,086 | 97,185 | 41.249, 50.667, 50.612, 43.930, 51.794, 51.936 | 31.391, 40.958, 32.321, 32.574, 39.858, 31.997 | 195.594, 197.811, 197.920, 196.697, 193.781, 199.722 |
| `Assert` | 28,145 | 211,416 | 86.367, 104.868, 106.111, 88.789, 105.712, 102.749 | 65.849, 84.248, 68.105, 66.612, 86.231, 68.943 | 399.427, 392.946, 389.339, 399.415, 395.563, 397.749 |

The medians are:

| name | current median ms | TEMP median ms | no-visibility median ms |
|---|---:|---:|---:|
| `Run` | 5.834 | 3.242 | 25.634 |
| `Path` | 33.136 | 20.038 | 122.470 |
| `Equal` | 50.640 | 32.448 | 197.254 |
| `Assert` | 103.809 | 68.524 | 396.657 |

The TEMP projection is about 34–44% faster for this isolated named read, so Claude's visibility
intuition found a real but small cost. It does not prove a missing producer index: the live store has
the existing covering index `idx_read_manifest_entries_version(version_id,view_id,generation)`,
and the current plan uses it.

`EXPLAIN QUERY PLAN` was identical across names except for bound values:

```text
current manifest join:
SEARCH i USING INDEX idx_read_identifiers_name_kind (name=?)
SEARCH e USING COVERING INDEX idx_read_manifest_entries_version (version_id=? AND view_id=? AND generation=?)
USE TEMP B-TREE FOR ORDER BY

TEMP projection:
SEARCH i USING INDEX idx_read_identifiers_name_kind (name=?)
SEARCH e USING COVERING INDEX _miller_visible_entries_version (version_id=?)
USE TEMP B-TREE FOR ORDER BY

no visibility:
SEARCH i USING INDEX idx_read_identifiers_name_kind (name=?)
USE TEMP B-TREE FOR ORDER BY
```

The relevant live read indexes were `idx_read_manifest_entries_version`,
`idx_read_identifiers_name_kind(name,kind,version_id)`,
`idx_read_identifiers_containing(containing_symbol_id,version_id)`,
`idx_read_pending_terminal(target_terminal_name,version_id)`, and
`idx_read_pending_from(from_symbol_id,version_id)`. The complete index listing was captured during
the same read-only connection experiment.

## Cause and next change

Confirmed:

- Query-time resolution is real work, not a telemetry artifact: each warm call resolves 135,097
  identifiers and 45,409 pending rows, with 1,351 ms and 493 ms median loop time respectively.
- The within-symbol arms dominate named lookup: 1,893 + 1,710 = 3,603 ms median and 130,582 rows.
- Identifier detail hydration plus identifier resolution is 2,365 ms median for 135,097 rows.
- Named visibility is measurable, but both named arms total only 498 ms median; the TEMP comparison
  cannot close the 5-second gap.

Rejected or not proven:

- A missing `manifest_entries` index is rejected. `idx_read_manifest_entries_version` exists and is
  selected by SQLite.
- A reference sidecar is rejected as the next implementation because the tested visibility cost is
  not the dominant phase and a sidecar would add a new artifact/lifecycle contract.
- Proof traversal can amplify the cost by triggering repeated full resolution passes: the fixed call
  emitted seven complete breakdowns across the frontier batches. The relationship component was
  337 ms for 6,129 rows and supplemental observations were 0 ms for 175 rows in the warm median
  aggregate, so this does not make proof traversal irrelevant; it makes repeated resolution the
  measured amplification point. The completion observation wraps the graph pass and is not additive
  evidence.
- A pure resolver-CPU fix is not yet proven: resolver loops are substantial, but they share work with
  details and direction-blind collection. The first replay must isolate direction-aware graph reads;
  detail hydration is a separate second experiment if the gate remains open.

Recommended implementation ladder:

1. Make `ResolveQuery` direction/consumer-aware for graph reads only. Skip named arms on a
   forward-only read and within arms on a reverse-only read where the caller does not consume them.
   Preserve scratch reuse, resolver behavior, detail loading, and all existing exact evidence and
   output contracts. Re-run this exact replay and measure the new phase split.
2. If, and only if, step 1 still misses 5,000 ms, separately defer `ReadIdentifierDetailsBatch`
   until an exact-reference consumer needs `Details`. Add exact-reference parity tests and run a
   separate replay before drawing a resolver/detail conclusion.

No step changes the producer schema or creates a sidecar. Each step needs its own approved
implementation plan and evidence; do not land them as one bundled optimization.

## Evidence anchors and verification

- Miller evidence: `context` identified `QueryTimeResolutionReader`, `StoreVisibility`, and the
  read-session projection; `inspect` confirmed `IdentifierSiteReader.SitesNamed`,
  `SitesWithinSymbols`, `QueryTimeResolutionReader.ResolveQuery`, `TryUniqueNameTarget`, and
  `SqliteSymbolGraphIndex.BatchNeighbourEvidence`; `search` located the existing index and phase
  contracts before raw inspection.
- `git diff --check` passed after the documentation update.
- No production or test code was changed by this task. Broader branch gates remain lead-owned.

## Open gate

Impact still exceeds the 5,000 ms product target. This finding supplies the next implementation
direction; it does not claim the latency problem is fixed.
