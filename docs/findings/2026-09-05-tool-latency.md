# Tool latency investigation, 2026-09-05

## Result and deployment boundary

The current session has two measured database costs and an upstream request queue. This is a diagnosis, not a deployed fix.

Miller was rebuilt, but its bundled extractor was not updated to today's source work:

| Component | Verified state |
|---|---|
| Running Miller | 1.27.2+8c8054cd29db, serving replay PID 2424928 |
| Miller source | 8c8054cd29db6b85dae1f2829eb1f956ff853ad1 |
| Bundled extractor, source and output copies | julie-extract 2.39.0, both SHA-256 de5d6d93e353f395950b60fd22f5ee8b2656f5b4d91dea90a730a29857aaf0dc |
| Bundled extractor file timestamp | 2026-09-01 18:05:02 -0500 |
| scripts/julie-pins.json | 2.39.0 |
| Live family store metadata | binary_version=2.39.0 |
| Julie main | a87121c61b9c98ca3301da614de1a7fe23eb88e1, clean |
| Qualified J1 branch | feature/reader-retention-contract at ecd021c05d774068423911877ebf254eac6ec0cf, clean, not merged into Julie main |
| J1 local debug executable | julie-extract 2.40.0, not the executable bundled by Miller |

Julie main also has runtime changes after the v2.39.0 tag, including writer batching and one-time resolution retirement. Its package version alone cannot distinguish those changes from the release. The source diff, file timestamps, and bundle hashes must accompany version strings. No newer-producer A/B was run in this investigation.

Do not attribute current measurements to the new producer or claim that the new producer fixes them. Do not silently swap the live binary or upgrade the live catalog while measuring. J1 installs a permanent writer floor; qualification must use an isolated store and the intended Miller integration.

## Fixed workload

The raw responses, exact arguments, outer start/end timestamps, and durations are in [the baseline JSON](2026-09-05-tool-latency-baseline.json).

- Workspace root: /home/murphy/source/julie-extractors/.worktrees/reader-retention-contract
- Workspace ID: 82e547e2231631399bf69bf1b5694288db7864c4a4d737035b59790451f127b7
- Source commit: ecd021c05d774068423911877ebf254eac6ec0cf
- Served revision: 105091
- Family: eed7c2dd-023b-493b-b706-a135ab011fbc
- View: ae2aa4c1-f354-4d29-8953-20deedee029d
- Store generation: gen-001; manifest generation: 234
- Manifest hash: 988b407388de35502e81e5aaf2f60700232465db4f266fe2fad0e61e75fc4969
- All calls explicitly set ensure_fresh=false. Search uses file mode and lexical retrieval.
- First observed call is reported separately, not represented as a controlled cold-cache run.
- Five warm repetitions per workload. Nearest-rank p95 is the maximum with this small sample.
- No builds or competing code exploration during the timed replay. vmstat samples showed 96–97% idle CPU, no swap I/O, and no I/O wait.

### Sequential baseline

UTC window 09:42:38.449–09:44:02.584, 24 successful calls.

| Workload | First observed ms | Warm median ms | Warm min–max ms |
|---|---:|---:|---:|
| inspect reader.rs, summary | 2028 | 1968 | 1945–1999 |
| inspect xtask/src/test_tiers.rs, summary | 1917 | 1941 | 1923–1997 |
| impact reader.rs and store_maintenance_contract.rs, limit 8 | 17009 | 6385 | 6199–6430 |
| search store/reader.rs, file mode, limit 6 | 1958 | 1955 | 1950–1993 |

Impact is deliberately bounded and reports traversal truncation. That is not a tool failure.

### Meaning of server phases

read_resolve_ms measures workspace-provider setup, not query-time reference resolution. It includes registry checks, read-session open, sidecar selection, and context construction. Measured symbol lookup begins afterward.

- Inspect setup averaged about 1.9 seconds; actual lookup averaged 18–19 ms.
- Warm impact setup averaged 1931 ms, graph traversal 4354 ms, and lookup 47 ms.
- First observed impact graph traversal was 14934 ms.
- Search uses the same setup route but does not emit these read-phase fields.

The relevant boundaries are WorkspaceIndexProvider.ResolveRegisteredSymbolRead, ResolveRegisteredSymbolSearch, ReadPhaseTelemetry, and SqliteSymbolGraphIndex.ReachWithEvidence.

## Finding 1: repeated first-read cost on the live WAL database

The store database is 13,247,893,504 bytes. Its WAL is 9,400,130,232 bytes, about 8.8 GiB. A wal-checkpoint-owed marker exists in the family root. The investigation did not delete, checkpoint, truncate, vacuum, or otherwise repair these files.

A managed stack sample during inspect caught FamilyStoreReadSession.ReadStoreMetadata inside SqliteCommand.ExecuteReader and statement preparation. The statement reads only 18 metadata rows:

```sql
SELECT key,value FROM store_meta ORDER BY key;
```

Using Python SQLite 3.51.2 and mode=ro:

| Experiment | Observed ms |
|---|---|
| First metadata read after opening each of three independent connections | 1822, 1803, 1823 |
| Second metadata read on those same connections | 0, 0, 0 at millisecond rounding |
| First reads on three new connections while one initialized read-only connection remains open | 1, 1, 0 |

The same real MCP inspect workload took 42, 53, and 47 ms while a temporary external read-only connection remained open. After that connection closed, inspect returned to 1987 ms. The source revision and WAL size remained unchanged.

This demonstrates a connection-lifetime-dependent SQLite first-read cost on this live store. Repeated WAL shared-state initialization is consistent with the observation, but no native SQLite profile was captured, so do not describe every millisecond as a proven checksum or recovery operation.

FamilyStoreReadSession.OpenReadOnly currently uses private, non-pooled connections. Blindly enabling pooling is not a qualified fix: the session installs manifest-specific temporary tables/views and must not leak them across views or generations.

A persistent open connection also needs explicit lifetime and producer-retention ownership. Implementing that outside M1 would create the very unregistered reader lifetime that J1/M1 are intended to remove. Coordinate this with [M1](../plans/2026-09-04-reader-retention-integration.md), and verify that no read transaction or Windows file handle outlives its protected generation.

## Finding 2: file filtering can scan retained symbols

LaggingSidecarSymbolLookup.EnsureLiveFiles validates sidecar rows against SqliteSymbolReader.ReadForPaths. The compatibility symbols view exposes s.path while joining visible entries only by version_id.

On the actual store, this isolated query uses SCAN s:

```sql
SELECT s.symbol_id
FROM main.symbols s
JOIN _miller_visible_entries e ON e.version_id=s.version_id
WHERE s.name IS NOT NULL AND s.path=?;
```

Filtering through the indexed visible manifest path uses two indexed searches:

```sql
SELECT s.symbol_id
FROM _miller_visible_entries e
JOIN main.symbols s ON e.version_id=s.version_id
WHERE s.name IS NOT NULL AND e.path=?;
```

The temporary table was populated from the exact view and manifest generation above, with the same path and version indexes as the production projection. These temporary objects were private to a read-only diagnostic connection.

For crates/julie-extract-artifact/src/store/reader.rs:

| Variant | Warm ms, three repetitions | Rows |
|---|---|---:|
| Symbol path filter | 1607.157, 1596.452, 1556.879 | 193 |
| Visible manifest path filter | 0.065, 0.064, 0.064 | 193 |

Every iteration compared the sorted symbol IDs for equality. SHA-256 of their JSON encoding was 124a23211fae2cde4abb1e00d90332db4697d305d3faeb44f7c26859dc1831e3 in both variants.

This is a query-level probe, not an end-to-end fixed lagging-sidecar benchmark. The production query additionally joins file evidence and parse diagnostics. Its full query and supported path invariants still require equivalence tests before changing it. Do not replace the historical 237-second number with this isolated query result.

No changed symbol-path index was found in the inspected v2.39.0-to-J1 schema delta. Nevertheless, qualify against the intended producer before choosing consumer or producer changes.

## Finding 3: the concurrent-call queue is upstream of Miller

Four requests launched with Promise.all through this session completed serially. For 19 of 20 warm measurements, outer completion was only 7–10 ms after server cumulative completion. Later request-handler-called events appeared only after the prior handler completed.

Those timestamps alone cannot locate the queue. A direct stdio JSON-RPC client then sent two inspect requests without waiting for either response:

- Probe PID 2433198 used the same Miller executable and the same workspace and read arguments.
- Both handlers started at 10:00:48.618 UTC. Responses completed at 1985 and 2020 ms from client launch.
- Both warm handlers started at 10:00:50.635 UTC. Responses completed at 1917 and 1985 ms.
- All four responses succeeded.

The server can execute these calls concurrently. The earlier serialized arrival pattern is upstream of the server handler in this client/tool path. It is not evidence for a global Miller serialization lock. The exact upstream component was not instrumented.

An earlier direct probe had a Python variable-shadowing error after receiving its first response. It was discarded; PID 2433198 is the valid replay.

## Historical tails remain a separate workload

WAL-aware telemetry reads used mode=ro, never immutable=1. An initial immutable read missed WAL rows and its partial distributions were discarded.

Window: 2026-09-05T00:00:00Z through 05:25:00Z, same workspace root.

| Tool | Calls | p50 ms | p95 ms | Maximum ms | Calls at least 60s |
|---|---:|---:|---:|---:|---:|
| context | 18 | 26991 | 83576 | 83576 | 3 |
| edit | 28 | 47316 | 58470 | 64224 | 1 |
| impact | 60 | 3916 | 100661 | 197638 | 4 |
| inspect | 836 | 1238 | 24615 | 237089 | 15 |
| patterns | 4 | 862 | 2534 | 2534 | 0 |
| search | 589 | 248 | 2021 | 110734 | 3 |
| trace | 59 | 736 | 4222 | 91426 | 1 |
| workspace | 39 | 6908 | 80608 | 86542 | 3 |

The original four-tool focus contains 963 rows; the full table contains 1633. Do not label 963 as all tools.

The largest inspect, correlation ID 01a06fb0-ecd6-770d-a6ce-829661be1b31, spent 233140 ms across 24 measured lookup counts, with backend lagging_sidecar. Setup was 760 ms. These are real server durations, not merely model or tool orchestration delay. Both lagging and current sidecar paths had historical tails, so lagging-sidecar validation is not a complete explanation.

Historical requests used Miller 1.27.2+90220d7978fb. There are no runtime source or test changes between that commit and 8c8054cd; the intervening changes are documentation and memories. Restarting and rebuilding alone do not explain away the historical tails.

## Decision and verification scope

Preserve this baseline. Do not add speculative caches, change concurrency limits, or silently upgrade the live producer.

1. Qualify the intended producer and M1 reader ownership together, using an isolated store. Record executable hashes and actual store metadata, not version strings alone.
2. Repeat the exact read workloads after producer integration. Separately exercise a deliberately lagging sidecar with the full production query.
3. Address any remaining first-read cost within explicit, registered session lifetimes. Assert disposal, generation switch, eviction, and checkpoint behavior, including Windows.
4. Address the demonstrated symbol scan with a caller-level equivalence test and a deterministic query-plan or operation-count guard. Preserve failed/retained file semantics and all language coverage.
5. Treat upstream queueing as a separate client investigation. Do not change Miller's concurrency based on the Promise.all result.

Only evidence files were added. No runtime code, pins, live databases, or plans were changed. No build or suite was needed for this diagnostic-only result. Verification consists of exact-version/hash checks, 48 recorded MCP baseline calls, WAL-aware telemetry correlation, read-only SQLite probes, the direct stdio replay, JSON validation, and diff checks.

The evidence lives on fix/tool-latency in /home/murphy/source/miller/.worktrees/tool-latency. Other Miller worktrees were left alone: CT-provider and postrelease-audit trees are clean and merged; the dogfood tree retains its pre-existing untracked .tools. The unrelated preserve/pre-v127-main-dirty-20260902 branch still has one unmerged preservation commit.
