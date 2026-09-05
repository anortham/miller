# Post-restart performance verification

Measured 2026-09-05. The serving MCP telemetry identifies Miller
`1.27.2+7bd50a76a793`, PID 2483080. The bundled extractor remains 2.39.0.
Raw requests, responses, timings and telemetry are in
[the replay artifact](2026-09-05-post-restart-performance.json).

## Live recovery

The original Julie worktree is clean at
`ecd021c05d774068423911877ebf254eac6ec0cf`.
The pre-cleanup reader-file inspect took 2,184 ms on the patched server.
Server resolution took 2,125 ms and lookup took 19 ms.

A normal `workspace refresh`, not a force rebuild or manual WAL deletion,
completed in 50,133 ms client time. It advanced revision 105091 to 105109
and manifest generation 234 to 237. It performed extraction and repaired
content/search sidecars. The response separately reported that vector convergence
requires a resident leader. No semantic work was requested by the replay.

The family is `eed7c2dd-023b-493b-b706-a135ab011fbc`, generation `gen-001`.
Its `gen-001/store.db-wal` went from 9,400,130,232 bytes before recovery to
zero bytes after recovery and remained zero after the replay. The family-root
`wal-checkpoint-owed` marker was absent afterward. Checking a `store.db-wal`
directly at the family root is incorrect; the database lives under the generation.
After timing finished, a read-only `PRAGMA quick_check` on the generation's
`store.db` returned `ok`.

## Original-request replay

Concurrency one, one first observation plus five warm samples per workload,
all `ensure_fresh=false`. No builds or tests ran during measurement.
The requests match the original baseline, but the refreshed revision differs.
These are live recovery measurements, not a same-revision causal A/B.
The previous isolated same-data tests remain the controlled evidence for the fixes.

| Request | Old warm median ms | New first ms | New warm samples ms | New median ms | New p95 ms |
| --- | ---: | ---: | --- | ---: | ---: |
| Inspect reader.rs | 1968 | 37 | 39,34,40,36,35 | 36 | 40 |
| Inspect test_tiers.rs | 1941 | 32 | 31,38,38,115,75 | 38 | 115 |
| Impact two paths | 6385 | 6545 | 2158,2161,2118,2172,2153 | 2158 | 2172 |
| File search | 1955 | 52 | 37,35,33,33,34 | 34 | 37 |

Nearest-rank p95 with five warm samples is the maximum, not a reliable estimate
of the population tail. All 24 calls succeeded. Impact retains its intentional
depth/limit truncation and returns eight results from 95 reached symbols.
Warm inspect resolution is now 6–10 ms. Warm impact graph work is 2057–2109 ms,
still the dominant remaining cost. Two xtask client outliers did not appear in
server duration, which stayed 25–26 ms for those requests. No concurrency replay
was performed; the previously observed upstream serialization remains unresolved.

## Recurrence is not fully closed

The merged change repairs the demonstrated cleanup omission. Successful coordinator
writes mark debt and attempt cleanup; no-change refreshes retry existing debt.
Resident idle maintenance retains Busy/Skipped debt and retries after 30 seconds.
Both family databases must checkpoint successfully before debt is cleared.
Prior regression tests cover an active reader preventing cleanup and recovery after
it releases its transaction.

This is not a hard WAL size bound. `StoreWorkspaceCoordinator.TryCheckpointOwedWal`
discards the status, and the resident retry emits only a debug log on failure.
There is no size/age escalation in these paths. A family with no resident owner
depends on another refresh to retry; standalone producer writes cannot be assumed
to create a Miller-owned debt marker. This audit has not proven every producer
write path has an equivalent cleanup lifecycle.

The inspected Julie J1 source sets routine/bulk auto-checkpoints to 1000/8000
pages and `journal_size_limit` to 256 MiB. These are not hard growth caps.
SQLite documents that readers can prevent checkpoint completion, and the journal
size limit applies when the WAL can be reset. See
[SQLite WAL checkpointing](https://www.sqlite.org/wal.html#checkpointing) and
[journal_size_limit](https://www.sqlite.org/pragma.html#pragma_journal_size_limit).
Do not infer that the currently bundled 2.39.0 binary matches newer source merely
from a package version.

Before declaring WAL handling finished, follow up with bounded checkpoint-result
reporting, size/age visibility through existing health/logging channels, and a
sustained-write test with a held reader, restart, and eventual recovery. Include
cross-workspace refresh without a resident leader and standalone producer writes.
A strict disk bound would need writer backpressure or bounded write transactions;
never delete a live WAL or terminate a reader just to meet a size threshold.

No production code or pins changed in this verification. Previously recorded
Linux, Windows, Scale, and Python gates were not rerun on unchanged production code.
