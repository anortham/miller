# CT sidecar foundation notes

Decisions for Tasks 2–14. Port source: Eros `71d78cd`. Namespace is `Miller.Testing`. No `Eros` identifiers remain.

## Identity and freshness

- Freshness key is `CtFreshnessKey(IndexIdentity, Revision)`.
- `IndexIdentity` is `WorkspaceReadSnapshot.IndexIdentity` (store cursor or legacy artifact id).
- `Revision` is the integer store log sequence (family store) or artifact revision (legacy).
- Revision alone is forbidden. A rebuild restarts the revision counter; a stale green must not match a new generation that happens to reuse the number.
- Persist the pair as columns `index_identity TEXT NOT NULL` and `revision INTEGER NOT NULL` on every run, status, freshness, and coverage table.
- Keep Eros lifecycle strings (`selected_revision`, `completed_revision`, `result_revision`, `last_run_revision`) as extra TEXT. They describe run phases. They are not the freshness key.
- Watermarks: `PRIMARY KEY (test_case_id, index_identity)`. A new index identity starts a new watermark instead of colliding with the old one.

## External identifiers (no cross-database FKs)

`ct.db` is self-contained. It must not reference `files(id)`, `symbols(id)`, or `search_docs`. Internal FKs inside `ct.db` are allowed.

Replace Eros FKs as follows:

- File: `file_path` + `content_hash` (`blake3:<hex>`, same form as `files.content_hash`).
- Symbol: `symbol_name` + `symbol_path` (name + path keys).
- Coverage map files also store `content_hash` so a path rename does not look like the same file.

`CtSchema.Apply` turns `PRAGMA foreign_keys=ON` and WAL. `schema_version` is `INSERT OR IGNORE` so a newer file stays newer. Task 2 must fail visibly on newer or corrupt schema and must not create `ct.db` on status reads.

## Daemon protocol

Layout under `<workspace>/.miller/ct/` (sibling of `ct.db`, which lives at `<workspace>/.miller/ct.db`):

- `daemon-v1.lock` — OS exclusive lock. The open `FileShare.None` handle is the lease (Task 10).
- `daemon.lease.json` — `CtDaemonLeaseRecord`: PID, process start time UTC, acquired-at, heartbeat, workspace root, Miller version.
- `daemon.heartbeat.json` — frequent heartbeat without rewriting the lease.
- `daemon.status.json` — `running` / `paused` / `stopped` plus reason.
- `commands/<id>.request.json` and `commands/<id>.ack.json` — file channel for `run` and `stop`. No sockets.

PID reuse: identity is PID **and** process start time. A new process that reuses a PID cannot match a dead lease.

Command ids are `[A-Za-z0-9._-]+` so path helpers cannot be pointed outside `commands/`. Path helpers must not create directories. Status reads never create `ct.db` or the control-plane directory.

Channel semantics:

- Writer creates `*.request.json` then waits for `*.ack.json`.
- Ack states: `requested` (optional echo), `acknowledged`, `rejected`.
- `stop` is graceful: status becomes `paused` or `stopped` with a reason. Task 10 kills the process tree on hard stop (`Process.Kill(entireProcessTree: true)`).
- A request without an ack before lease loss is unacked. The next owner must not treat it as done.

Unavailable deltas stay `ContinuousTestDeltaCompleteness.Unavailable` and must not enqueue work. They must not carry revision endpoints. That replaces Eros's poller full-scope fallback.

## Windows file-locking discipline (Task 5+ / 10)

Recorded here so later tasks do not re-decide it:

- Build and execute only inside per-generation directories. Never the workspace `bin`/`obj`.
- Generation directory names are short hashes (MAX_PATH).
- An undeletable generation dir becomes reap debt (`ct_generation_reap_debt`). It is not a run failure.
- Artifact moves retry on sharing violations (`MILLER_PROMOTE_RETRY_TIMEOUT` precedent).
- Process kill uses `Process.Kill(entireProcessTree: true)`. No POSIX signals on Windows.
- App-control `0x800711C7` is a run-level execution outcome. Affected tests stay stale. Verdict is `Partial` or `Unknown`, never `Green` on incomplete results.

## Environment

- `MILLER_CT=off` (also `0`/`false`/`no`) is the kill switch. Unset stays on. Mirror of `MILLER_SEMANTIC=off` tokens, with `no` included to match `ScanGovernor`.
- `MILLER_CT_WORKSPACE_ROOT` replaces `EROS_WORKSPACE_ROOT`.
- Temp-path prefix is `miller-ct` (not `eros-ct`). Task 6 owns `CtTempPaths`.
- Other CT variables use the `MILLER_CT_` prefix.

## Verdict

`ContinuousTestFreshness.Evaluate`:

- `Green` only when every case is complete at the selected `(index identity, revision)` and watch health is good.
- `Red` when that complete set includes a red case.
- `Partial` when staleness is known or the proven key does not match the selected key.
- `Unknown` when the set is empty, any case is unknown/running, or watch health is not good.

## Project shape

- `Miller.Testing` references `Miller.Core` + `Miller.Indexing` and copies Indexing's Sqlite packages (`Microsoft.Data.Sqlite` 10.0.9, `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3). Do not call `Batteries_V2.Init()`.
- Do not reference `Miller.Server`. Task 12 adds Server → Testing.
- Do not use `InternalsVisibleTo` into Indexing. Task 4 adds a public fact adapter.
- The store must never expose a raw SQLite connection.

## Judgment calls

- `ContinuousTestRole` replaces Eros `TestRole` so the CT enum does not collide with other Miller names.
- `ContinuousTestCase` replaces Eros `TestCase` and stores path+hash / name+path instead of `file_id`/`symbol_id`.
- `CtCoverageMapRecord` and `CtCoverageNarrowingEvidence` live in `Miller.Testing` because selection contracts need them. They are not store types.
- Provider `SelectedRevision` stays a string (Eros providers consume it). `IndexIdentity` is required beside it.
- `CtDaemonProtocol` path APIs compute paths only. Task 10 creates files.
