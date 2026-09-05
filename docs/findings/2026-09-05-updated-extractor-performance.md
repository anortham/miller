# Updated extractor performance qualification

## Scope

The user approved isolated extractor testing and fixes for confirmed performance defects. Work continues on `fix/tool-latency`, based on `c110b813`. The live store and bundled extractor remain unchanged.

## Producer comparison before Miller changes

Built J1 commit `ecd021c05d774068423911877ebf254eac6ec0cf` with `cargo build --release --locked -p julie-extract-cli -j 4`. Build completed successfully in 76 seconds.

The release executable SHA-256 is `3385019be589b215d38a42fc212563dcefd8e349ff41a8e763ac643df544eb8b`.

Archived that commit into `.miller/perf-Lxmd0E/source` within this worktree. Imported the identical root into distinct `old-store` and `new-store` directories with `--jobs 4 --json`, the same family ID `d42d76ec-c2ae-4a59-9b0c-6d22260b6981`, and view `perf-view`. Neither directory is a live registered store.

| Producer | Import wall seconds | User CPU seconds | System CPU seconds | Peak RSS KiB |
|---|---:|---:|---:|---:|
| Bundled 2.39.0 | 156.72 | 63.81 | 48.49 | 237668 |
| J1 release 2.40.0 | 141.43 | 64.60 | 47.50 | 260308 |

These are single fresh imports, not a statistically qualified speedup claim. Both committed all three levels with 2213 file versions, 677912 L1 rows, 930544 L2 rows, and 794985 L3 rows. Both manifest hashes are `eb774fa291bed393d73de22578da011517961c5ca757302b99c37e04fe291736`. Grouping symbols by language confirms identical counts across all 40 supported languages, totaling 349911 symbols. The new store records both binary and minimum writer versions as 2.40.0.

## Confirmed remaining symbol-path scan

SQLite 3.51.2, read-only connections, private temporary manifest table and indexes matching Miller's compatibility projection. The queried path is `crates/julie-extract-artifact/src/store/reader.rs`, returning 193 symbol IDs.

Current join:

```sql
FROM main.symbols s JOIN e ON e.version_id=s.version_id
WHERE s.name IS NOT NULL AND s.path=?
```

Adding the file-path equality permits both existing indexes:

```sql
FROM main.symbols s JOIN e ON e.version_id=s.version_id AND e.path=s.path
WHERE s.name IS NOT NULL AND s.path=?
```

| Store | Current warm ms | Candidate warm ms |
|---|---|---|
| 2.39.0 | 20.704, 20.120, 21.028 | 0.045, 0.045, 0.045 |
| 2.40.0 | 19.551, 19.776, 19.453 | 0.045, 0.045, 0.043 |

Each series excludes its first observation. The current query uses `SCAN s`; the candidate searches the manifest path index and `idx_gc_symbols_path(version_id,path)`. These are reduced-query probes, not yet full production-query results. The earlier retained live dataset took about 1.6 seconds for the same scan shape.

## Architecture quality

The first fix is local to the family-store symbols compatibility view. It adds no interface, cache, persistent connection, schema migration, or parser rule. It preserves projected symbol fields and makes the manifest-to-symbol path equality explicit in the join. Tests must prove bounded path lookup and retained-version correctness through the public read-session/query interface. Real-extract comparison must check that path equality holds across all supported languages. Risk is low if these checks pass.

No persistent read connection was added. That would require registered lifetime ownership under M1. The WAL fix uses Miller's existing checkpoint operation, now invoked after successful non-primary work and on later no-change refreshes while debt remains. Busy or skipped checkpoints retain the owed marker. The idle indexer uses the same success-only completion helper. The helper is justified by the two callers sharing the same debt-clearing rule; no new service or timer was added.

The family checkpoint configures a 300-second command timeout. Initial code inspection suggested this might stall the indexer, but controlled active-writer and active-reader probes returned Busy promptly. The timeout was not changed, and this investigation does not claim a reproduced five-minute checkpoint wait.

## Production-query result and upgrade qualification

Fresh 2.40.0 stores correctly refuse Miller's current 2.39.0 reader pin. That gate was not bypassed. An isolated snapshot of the 2.39.0 store was upgraded with the 2.40.0 producer instead. The upgrade completed in 1.14 seconds and reused the manifest with identical counts. Miller could read this supported upgrade path without changing its pin.

The upgraded store reports binary version 2.40.0 and preserves its 2.39.0 reader and writer floors. This import does not exercise first reader-catalog admission or the M1 registration integration.

The first upgrade attempt exposed a snapshot-helper defect: `perf-store-snapshot.py` dropped an empty `gen-001/bases` directory required by the producer. A new test failed on the missing directory. The helper now preserves generation bases directories; all 36 snapshot tests pass. A new snapshot passed quick checks and the real producer upgrade.

The full `SqliteSymbolReader.ReadForPaths` query, including file evidence and diagnostics, was measured against that upgraded store using the original and patched Miller assemblies:

| Assembly | Warm milliseconds, five repetitions | Nearest-rank p95 ms |
|---|---|---:|
| Original | 325.8549, 329.1957, 325.2787, 341.7390, 326.8228 | 341.7390 |
| Patched | 4.9972, 5.2323, 5.4244, 5.7404, 5.6930 | 5.7404 |

The first observation was excluded. Every repetition returned 193 rows with the same full-row JSON SHA-256, `42DCA371AAED784EF55E23135405E73E7853F683CA0A063F6EBB4D52A3A2CFED`. One file per language was also read through both assemblies; all 40 result counts and full-row hashes match. Raw records are in [the replay JSON](2026-09-05-updated-extractor-replay.json). This is a production read-function benchmark, not a claim that every MCP request is now 5.7 ms.

The standalone [probe source](2026-09-05-tool-latency-probe/Program.cs) and [project](2026-09-05-tool-latency-probe/Bench.csproj) are retained for replay. Build twice with `dotnet build <project> -c Release -p:MillerBinaries=<absolute-server-output> -o <isolated-output>`, selecting the baseline and candidate server output directories. Run `dotnet <isolated-output>/Bench.dll <family-id> <isolated-store-root> <view-id> <source-root>`. Append `wal` to exercise the no-change coordinator first. On Linux set `LD_LIBRARY_PATH` to the selected server output's `runtimes/linux-x64/native` directory. The probe refuses to submit extraction work. Do not point its `wal` mode at a live store.

The first fast-suite run found a bridge fixture assigning TypeScript facts from three paths to the same unrelated C# file version. The producer's `store/rows.rs::insert_l1_rows` writes each symbol's path and language from its owning file, and both real extracts have zero manifest/symbol path mismatches. The fixture now gives those three files their own versions and manifest entries. All existing bridge expectations remain unchanged, including the scoped duplicate name and the invalid-relationship guard. Its focused test passes.

## WAL lifecycle result

Two identical isolated snapshots of the upgraded store were given a controlled 47,684,912-byte WAL by a no-op symbol update while a read transaction held the prior snapshot. The effective symbol data did not change. No live store was involved.

The original and patched `StoreWorkspaceCoordinator.Scan` then ran against unchanged source. A rejecting producer client ensured neither no-change replay could submit extraction work.

| Result | Original | Patched |
|---|---:|---:|
| WAL before refresh, bytes | 47684912 | 47684912 |
| WAL after refresh, bytes | 47684912 | 0 |
| Checkpoint owed after refresh | true | false |
| No-change refresh ms | 1247.9312 | 1408.0153 |
| Next read-session open ms | 17.7881 | 7.0460 |

These single observations demonstrate cleanup and its cost, not a statistically qualified latency percentile. The patched refresh spent about 160 ms more performing maintenance. It avoids leaving that work for subsequent reads to repeatedly pay. No persistent connection or transaction lifetime was introduced.

The previous family status aggregation incorrectly reported Ok when only one database checkpointed and the other was unreadable. A failing regression proved this. Success now requires both databases; skipped and busy outcomes preserve debt and the primary indexer retries them. Tests cover committed work, a no-change refresh with an existing stamp, an active reader, and an unreadable coordinator.

## Verification status

The 67 focused family-read tests and the 67 focused coordinator/checkpoint tests pass. Python snapshot and recovery checks pass: 189 run, two platform skips. The final Linux gate completed with zero build warnings/errors, 9679 fast tests passed and nine skipped, plus 204 scale tests passed and 24 skipped. Focused Windows verification is pending. No deployment claim is made here.
