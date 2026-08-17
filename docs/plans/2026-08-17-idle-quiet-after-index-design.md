# Idle quiet after index current

- **Date:** 2026-08-17
- **Status:** approved to implement (user: quiet when not in use, after indexing finished)
- **Host evidence:** native Windows, Miller `1.19.4+7d4a6905`, julie-extract 2.33.6, family `9f173abc-…9386`

## Problem

After the index is `exact` and `current`, the user still sees CPU and disk activity.

Measured on this machine after the last resolve at 14:56:54:

- `julie-extract` is not running.
- Leader `miller` CPU is 0 over 5 s and 10 s samples.
- `store.db` is 1.9 GB. `store.db-wal` is **4.0 GB** and last grew at 14:56:54.
- `store.db-shm` still updates while idle.
- Two `miller.exe` processes poll freshness every 500 ms.
- Each poll opens `store.db` read-only (`FamilyStoreReadSession.Probe`).

The leftover work is not extract. It is:

1. Four SQLite opens per second against a multi-GB WAL store.
2. A 4 GB write-ahead log that live readers keep from shrinking.

## Goal

When status is `current`, the queue is empty, and the user is not saving files or calling tools:

- no `julie-extract` process
- leader CPU near zero
- `store.db-wal` small or gone
- `store.db-shm` not updating every second

## Non-goals

- Do not skip resolve for markdown in this slice.
- Do not change crossover.
- Do not raise timeouts.
- Do not add MCP tools.
- Do not put the revision cursor into `store.json` schema 1.
- Do not vacuum the live family store by hand.
- Do not unload the semantic sidecar in this slice.

## Architecture Quality

**Affected modules:** `WorkspaceReadSessionFactory.Probe`, new `StoreFreshnessStamp`, new `StoreWalCheckpoint`, `StoreWorkspaceCoordinator` after `ReadRequiredState`, `IndexerService` empty-tick retry.

**Caller-facing interface:**

- `StoreFreshnessStamp.TryRead(storeRoot, viewId)` / `Write(storeRoot, document)`
- `WorkspaceReadSessionFactory.Probe` prefers a valid stamp and does not open `store.db`
- `StoreWalCheckpoint.TryTruncate(databasePath)` returns `Ok` / `Busy` / `Skipped`
- No CLI, MCP, or `store.json` schema change

**Depth/locality check:** Local to idle poll and post-write maintenance. Extract and resolve contracts stay the same.

**Test surface:** Stamp read/write/fallback tests. Probe-without-opening-store tests. Checkpoint busy/ok tests. Coordinator publishes the stamp from the re-read state.

**Seams/adapters:** One stamp file next to the family store. One checkpoint helper reused by the coordinator and the empty debounce tick.

**Rejected shortcuts:** Stuffing the cursor into `store.json`. `immutable=1` on the live store. Skipping resolve for docs. Manual VACUUM.

**Architecture risk:** medium. A stamp published before the store write is visible would freeze readers on an old revision.

## Design

### 1. Freshness stamp (stop opening `store.db` when idle)

After a store write, the leader writes a tiny JSON stamp next to the family store:

`{store_root}/freshness-stamp-{view_id}.json`

Fields (snake_case, schema 1):

- `schema_version` (1)
- `family_id`
- `store_root`
- `view_id`
- `workspace_root`
- `store_log_sequence`
- `manifest_generation`
- `manifest_hash`
- `store_instance_id`
- `binary_version`

Publish rules:

- Invalidate **every** `freshness-stamp-*.json` in the store root **before** import, update, delete, or resolve starts. Overwrite with an unreadable schema first, then delete. A leftover trusted stamp is the failure mode. Shared-version events can advance sibling views, so one-view delete is not enough.
- Write only after `StoreWorkspaceCoordinator` re-reads the committed state (`ReadRequiredState` / the `after` snapshot). Do not publish from the import result alone.
- Republish after **every** committed store-log advance: import, update, delete, and resolve. An incremental one-file save must not leave a stale stamp.
- Bind the stamp to the pointer identity (family, store root, view, workspace root). A mismatch is invalid.
- Atomic replace, same pattern as `StoreWorkspacePointer.Write` (temp file + `File.Move` overwrite).
- `.miller` is already in `WatchPathFilter.SkipSegments`. The stamp lives under `~\.miller\stores\…`, outside the workspace watch.

Read rules (`WorkspaceReadSessionFactory.Probe` when store mode is on):

1. Read `.miller/store.json` as today.
2. If a stamp exists and matches the pointer identity, return it as `WorkspaceFreshnessProbe` and do **not** open `store.db`.
3. If the stamp is missing, unreadable, schema-wrong, or identity-mismatched, fall through to `FamilyStoreReadSession.Probe` (opens `store.db`).
4. First poll after bind may use a valid stamp. If none exists, open the store once and do not invent a stamp from a reader.

`FreshnessService` keeps the 500 ms tick and the promote/inode rule. The cheap poll just stops opening the multi-GB database when nothing changed.

### 2. WAL checkpoint (shrink the leftover 4 GB log)

After a successful coordinator cycle, and only when no extract child is running, try:

`PRAGMA wal_checkpoint(TRUNCATE)`

on `store.db` and `coord.db`.

- `Ok` (busy flag 0): done.
- `Busy` (busy flag 1): request one retry on the next **empty** debounce tick.
- Never run TRUNCATE inline on the scan path. A 4 GB checkpoint can take real time. Schedule it after the coordinator returns, on an empty debounce tick, **after** `_opsGate` is released.
- `JulieStoreClient` holds read transactions on `store.db` and `coord.db` for the whole producer process. TRUNCATE reports BUSY until those anchors release. That is the safety rail, not a bug.
- Do not use PASSIVE as a substitute for “done.” PASSIVE can leave a large WAL in place.
- Reuse the same busy measurement as `JulieStoreClientTests.WalCheckpointBusy`.

Readers already dispose the probe connection each tick. Tool sessions may still pin the WAL; BUSY then retry is the expected path.

### 3. Out of scope this slice

Resolve-after-docs, sidecar rewrite size, and semantic-sidecar memory stay for a later slice.

## Acceptance

- Probe with a matching stamp does not open `store.db` (test with a fixture that has no store file, or a file that would throw if opened).
- A mismatched or missing stamp still opens `store.db` and returns the live cursor.
- Coordinator writes the stamp from the re-read `after` state, not the import report.
- Checkpoint TRUNCATE returns busy while a mutation anchor is held, and 0 after release (existing busy test pattern).
- Watcher does not start extract when the stamp file is written.
- Live dogfood after rebuild: with index `current` and no saves, `store.db-shm` mtime does not advance every second, and `store.db-wal` shrinks after a successful checkpoint.

## Test plan

Focused (fast suite):

- `FamilyStoreReadSessionTests` / new `StoreFreshnessStampTests`
- `WorkspaceReadSessionFactory` probe-with-stamp tests
- `StoreWalCheckpointTests`
- Coordinator stamp-publish test if one already covers `ReadRequiredState`

Do not run Scale unless the extract path is touched (it is not).
