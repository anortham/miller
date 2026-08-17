# julie-extractors Windows lock — 2026-08-17

- **Date:** 2026-08-17
- **Host:** native Windows
- **Workspace:** `C:\source\julie-extractors` (`julie-extractors-2a8adf92e707`,
  `2a8adf92e70745859ca1cb9c82b42c4202726d72664f0fcaab3777331952a0d3`)
- **Family store:** `C:\Users\alann\.miller\stores\ae07681c-bfa5-4a72-9880-6c9ab39e6334`
- **This session's Miller:** plugin `1.19.4+10db3160e82b`, pid 14212, bound to `C:\source\miller`
  as a **reader** of julie-extractors
- **Pinned producer on this Miller:** `julie-extract 2.33.5`
- **Store `binary_version`:** `2.33.1`

This note diagnoses the three-day stuck workspace. It does not change product
behavior. It does not run `workspace full`. It does not delete store or
registry files.

## Verdict

The next step is a **julie-extractors / operator refresh**, not a Miller code
fix.

`locking protocol` is not a Miller string. It is SQLite `SQLITE_PROTOCOL`
(code 15, `FileLockingProtocolFailed`). julie-extractors names and retries it
in `crates/julie-extract-artifact/src/store/wal_retry.rs`. Miller only stored
the producer message as `workspaces.last_error`.

No live process holds the workspace lock. The wedge is a **stranded
`claimed` import** in `coord.db` plus a three-day-old scan-failure journal.
Nothing is the indexer leader for this root, so nothing retries.

## Live status (no refresh)

Miller MCP `workspace list filter=julie-extractors`, then
`operation=status|health|leader` with `workspace_id=julie-extractors`.
The workspace tool has no `ensure_fresh` flag. Status and health assemble
facts. They do not call `workspace refresh` or `workspace full`.

| Surface | Result |
|---|---|
| list | `state: error`, `last_error: locking protocol`, `last_seen_at: 2026-08-14T00:04:05.2689663+00:00` |
| freshness | `scan_failing` |
| scan_failure | `IncrementalReconcile` ×1 at `2026-08-14T00:04:05.2653009Z`; `jobs: 4`; `exit_code: null`; `next_attempt_utc: 2026-08-14T00:04:36.4319220Z` (already past) |
| leader | `pid: null`; `alive: null`; this process is not the leader |
| store | `state: ready`; `resolution_state: unbound`; `store_log_sequence: 6873`; `index_level: symbols`; `upgrade_owed: true`; `policy: full` |
| artifact extractor | `2.33.1` (store_meta `binary_version`) vs this process `2.33.5` |
| search sidecar | stale |
| content corpus | stale (revision 6787 vs expected 6873) |
| vectors | unavailable (no completeness stamp) |
| health verdict | `degraded` — workspace readable |

`C:\source\julie-extractors\.miller\scan-failure.json` matches the status
object. The journal was written at `2026-08-14T00:04:05.2681733Z`.

## Who holds the lock

**No live holder.**

Checked 2026-08-17:

- PID **19780** (`claim_owner` `cli-19780`) is not running.
- PID **42668** (`scan.progress` / spool owner) is not running.
- No `julie-extract` process is running.
- Miller pid 14212 is the miller-workspace plugin. It is a reader here.
- `.miller/indexer.lock` is a 0-byte file dated `2026-08-13T20:15:57Z`.
  An `r+b` open succeeded, so no process holds `FileShare.None`.
- Family `coord.db` is not exclusively held.
- `writer_lease` is **empty**.
- `maintenance_intent` is **empty**.

The leftover coordinator row is the real wedge:

| Field | Value |
|---|---|
| `request_id` | `aa643189cf7240b7a96bce4af218f34b` |
| `kind` | `import` |
| `state` | `claimed` |
| `requester_id` / `claim_owner` | `cli-19780` |
| created | `2026-08-14T00:03:39.036Z` |
| last heartbeat | `2026-08-14T00:03:54.241Z` |
| payload | `requested_level: "full"` for root `C:\source\julie-extractors` |

`store.json` was written at `2026-08-14T00:03:38Z`. The view row in
`store.db` last moved to `unbound` at `2026-08-14T00:03:43.843Z`.
`coord.db`, `store.db`, and `scan-failure.json` all have last-write
`2026-08-14T00:04:05Z`. That is the same instant as the registry error.

A later scan on **2026-08-16T03:21:13Z** left
`.miller/spool/julie-extract-scan-owned-spool-42668-1786850473018115100.jsonl`
(20,402,494 bytes) and `scan.progress` at phase `artifact_write`, pid 42668.
`symbols.db` last write stays `2026-08-13T20:20:27Z`. That scan died before
it promoted. The process is gone. The spool is an orphan, not a lock.

## String ownership

Miller source has no `locking protocol` literal. A lexical `search
query="locking protocol" mode=source` on this miller workspace returned no
code hits.

julie-extractors owns the words:

- `wal_retry.rs` documents SQLite `SQLITE_PROTOCOL` as `"locking protocol"`.
- `coordinator.rs` says the old per-call `coord.db` open storm made SQLite
  raise that signal and report a corrupt coordinator.
- v2.33.2 retries the signal on read-only opens. Writer lease mutations do
  not retry it. See
  [`2026-08-14-julie-extract-2.33.2-adoption.md`](2026-08-14-julie-extract-2.33.2-adoption.md)
  and julie-extractors
  `docs/findings/2026-08-13-coordinator-connection-reuse.md`.

Miller copied the producer message into the registry. The path is
`IndexerService` startup IncrementalReconcile catch →
`IndexBootstrapService.MarkRegistryError(..., ex.Message)` →
`WorkspaceRegistry.MarkError`. `exit_code: null` matches an exception, not
a julie-extract process exit.

Task 2 made coordinator **quantum** misses retryable and skipped
`MarkRegistryError` for those. `locking protocol` is not in that retry
set. This task does not add it. The string is not Miller-owned.

## Why it stayed stuck

1. The 2026-08-14 IncrementalReconcile failed while producer `2.33.1` was
   still in use. That build is the one that raised `locking protocol` on
   WAL-index recovery.
2. Miller wrote `scan-failure.json` and `last_error=locking protocol`.
3. The import stayed `claimed` by dead `cli-19780`. `writer_lease` is
   empty, so the lease-takeover path that requeues a dead holder's claims
   never ran.
4. julie-extract reaps a stranded **resolve** claim. It does not reap a
   stranded **import** claim by owner death. Import steal uses heartbeat
   age against a 5 s lease (`DEFAULT_LEASE_DURATION_MS`). The heartbeat is
   three days old, so a **new writer drain** may steal the row.
5. `store repair` / `store gc` refuse when any request is `claimed`
   (`maintenance.rs` `EXISTS(... state='claimed')`). Those verbs cannot
   clear this row.
6. No Miller process is the leader on `C:\source\julie-extractors`. The
   past-due `next_attempt_at` therefore never fires. Cross-workspace
   status/health do not start a scan.

## Repair (needs explicit user approval)

This task did **not** run the commands below. Do not run them until a
person approves.

**First repair — operator refresh, not `workspace full`:**

```text
miller refresh --json --wait --workspace-id julie-extractors-2a8adf92e707
```

Equivalent:

```text
miller workspace refresh --json --wait --id julie-extractors-2a8adf92e707
```

That path sets `bypassBackoff: true`. The current plugin producer is
2.33.5, which retries locking-protocol on read-only opens. A new writer
can insert a `writer_lease` row (the table is empty) and steal the stale
import because `claim_heartbeat_at` is far older than 5 s.

Do **not** start with `miller workspace full`. A full rebuild is a second
approval if refresh fails.

Do **not** delete `store.db`, `coord.db`, or the family directory.

Do **not** run `julie-extract store repair` first. The claimed import
makes maintenance report busy.

**If refresh fails on the same claim**, then (second approval) requeue the
dead owner's row in `coord.db` and retry refresh. Requeue is what lease
takeover already does for a dead holder:

```sql
UPDATE requests
SET state = 'queued',
    claim_owner = NULL,
    claim_heartbeat_at = NULL,
    updated_at = CAST(strftime('%s','now') AS INTEGER) * 1000
WHERE request_id = 'aa643189cf7240b7a96bce4af218f34b'
  AND state = 'claimed'
  AND claim_owner = 'cli-19780';
```

That SQL is a store mutation. Do not run it from this findings task.

After a successful refresh, confirm:

- `workspace list` no longer shows `locking protocol`
- `scan-failure.json` is gone or cleared
- `coord.db` has no `claimed` row for `aa643189cf7240b7a96bce4af218f34b`
- `resolution_state` is no longer stuck `unbound`

The next successful scan should also reap the 2026-08-16 orphan spool.

## What this task did not do

- No `workspace full`
- No `workspace refresh`
- No delete of store, registry, lock, spool, or `scan-failure.json`
- No Miller source change
- No `docs/README.md` edit (Task 12 owns that pointer)

Read-only probes: Miller status/health/list/leader, file timestamps, process
list, `coord.db` / `store.db` with `mode=ro`, and an immediate close of
`indexer.lock` after an `r+b` share check. Status/health opened SQLite
sidecars and therefore touched `store.db-shm` / `history.db-*` timestamps
today. That is not a store or registry write.

## Related notes

- Parent dogfood list: [`2026-08-17-windows-dogfood-1.19.4.md`](2026-08-17-windows-dogfood-1.19.4.md) §9
- Producer locking-protocol retry: [`2026-08-14-julie-extract-2.33.2-adoption.md`](2026-08-14-julie-extract-2.33.2-adoption.md)
- Same family of stranded claims on **resolve** (2026-08-12 miller workspace):
  julie-extractors `docs/findings/2026-08-13-coordinator-connection-reuse.md`
  and `coordinator.rs` comments on `06c5e45b` / `cli-36084`
