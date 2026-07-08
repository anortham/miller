# Task 5 — `workspace remove` coordinates with all workspace-local locks

**Status:** COMPLETE
**Test summary:** assigned scope 153/153 pass; WorkspaceTool 61/61; full fast suite 2982/2982 (20s, under 30s ceiling).
**Commit SHA:** none — parallel-lead-commit (no `git add`/`commit`).

## What I implemented

Both `workspace remove` paths now acquire ALL THREE workspace-local write leases in a single fixed order
before any delete, so a remove can never race an in-flight sidecar write:

1. **`SingleWriterLock.DeleteContentsExceptLock`** — generalized to take an explicit
   `IReadOnlySet<string>? additionalHeldLockFileNames` (default `null`). It always skips the intrinsic
   `indexer.lock` and, in addition, skips exactly the caller-supplied held lock names. It is an EXPLICIT
   set, not a blanket `*.lock` skip — a stray unheld `.lock` file is treated as index debris and deleted
   (a leaked lock stays visible instead of being silently preserved). The parameterless call is
   byte-for-byte back-compatible (only `indexer.lock` survives). The existing why-comment discipline is
   preserved and extended.

2. **New shared helper `WorkspaceWriteLeases`** (added to `SingleWriterLock.cs`) — one small helper, not a
   lock-manager abstraction. `TryAcquireForRemove(millerDir, acquireIndexerLock, timeout?)` acquires the
   indexer lock (via a caller-supplied try-acquire: CLI passes `SingleWriterLock.TryAcquire`, server passes
   its injected `_acquireWriterLock`), then `content.lock` (`ContentCorpusWriteLock`), then `history.lock`
   (`MetricHistoryWriteLock`), each with a short timeout. ANY lease unavailable ⟹ it releases whatever it
   already took (reverse order) and returns `null` — the caller's existing refused-in-use result, nothing
   deleted. `Dispose` releases all three in reverse order. It also exposes `SidecarLockFileNames`
   (`content.lock` + `history.lock`) as the explicit skip-set for `DeleteContentsExceptLock`, so the fixed
   lock order and the skip-set live in exactly one place and cannot drift between the two remove paths.

3. **CLI `CliDispatch.WorkspaceRemove` → `RemoveMillerDir`** — swapped the lone `SingleWriterLock.TryAcquire`
   guard for `WorkspaceWriteLeases.TryAcquireForRemove`; delete now passes `SidecarLockFileNames`. Result
   shapes (`WorkspaceRemoveResult.RefusedInUse`, exit code 3) unchanged.

4. **Server `WorkspaceTool` remove (both branches: path-only `Remove` and `RemoveResolvedTarget`)** — same
   swap, using the injected `_acquireWriterLock` as the indexer-acquire. Result shapes and telemetry outcomes
   unchanged. The non-remove `_acquireWriterLock` prime-scan site (~:680) was left untouched.

## Why (defect fixed)

Pre-existing bug: CLI content imports hold `content.lock` WITHOUT the indexer lock, so the old remove
(indexer-lock-only guard) could delete `content.db` mid-import (Windows sharing-violation crash / POSIX
unlinked-inode writes). `history.lock` (new this branch) is a third uncoordinated lock with the same
exposure. Remove now co-holds all three.

## Verification

- **Invariant:** remove acquires indexer→content→history and refuses (deletes nothing) if any is held; held
  lock files survive the gutting and are cleaned up after release; back-compat delete keeps only `indexer.lock`.
- **Assigned scope command:**
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "(FullyQualifiedName~SingleWriterLock|FullyQualifiedName~CliDispatch)&Category!=Scale"`
- **Result:** Passed 153 / Failed 0 / Skipped 0 (~5s).
- **Ceiling command:** `scripts/test.sh` → Passed 2982 / Failed 0, wall time 20s (< 30s ceiling).
- **Also ran (touched-file guard):** `--filter "FullyQualifiedName~WorkspaceTool&Category!=Scale"` → 61/61.
- **Build:** `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors.
- **Timestamp:** 2026-07-07.

## Files changed

- `src/Miller.Indexing/SingleWriterLock.cs` — generalized `DeleteContentsExceptLock`; added `WorkspaceWriteLeases`.
- `src/Miller.Server/Cli/CliDispatch.cs` — `RemoveMillerDir` (inside `WorkspaceRemove` region only).
- `src/Miller.Server/Tools/WorkspaceTool.cs` — both remove call sites (`Remove` path-only + `RemoveResolvedTarget`).
- `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs` — 7 new tests.
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` — 2 new remove-race regression tests.

## New tests (each maps to an acceptance criterion)

- `DeleteContentsExceptLock_WithHeldSidecarLocks_KeepsThem_ButDeletesUnheldLockDebris` — held locks survive,
  unheld `stale.lock` debris deleted.
- `DeleteContentsExceptLock_DefaultSkipSet_StillKeepsOnlyIndexerLock` — back-compat.
- `WorkspaceWriteLeases_OnAllFreeLocks_AcquiresAllThree` — all three exclusively held after acquire.
- `WorkspaceWriteLeases_WhenIndexerLockHeld_Refuses`
- `WorkspaceWriteLeases_WhenContentLockHeld_Refuses_AndReleasesTheIndexerLock`
- `WorkspaceWriteLeases_WhenHistoryLockHeld_Refuses_AndReleasesIndexerAndContent` (partial-acquire rollback)
- `WorkspaceWriteLeases_Dispose_ReleasesAllThree_MakingThemReacquirable`
- `WorkspaceRemove_DuringInFlightContentImport_RefusedExitThree_ContentDbIntact` (CLI public surface)
- `WorkspaceRemove_DuringInFlightHistoryAppend_RefusedExitThree_HistoryDbIntact` (CLI public surface)

## Codebase orientation (calls + confirmations)

Miller MCP was not used directly — I am a subagent sharing the lead's single Miller MCP connection (per
project memory, one stuck call jams all), so I oriented with Read/grep over the worktree, which holds this
branch's new files that the shared Miller index (serving the main checkout) does not have. Each contract was
verified by reading source, not guessed:

- Read `SingleWriterLock.cs` — confirmed `LockFileName="indexer.lock"`, existing `DeleteContentsExceptLock`
  skipped only `indexer.lock`, `TryDeleteEmptiedDir` best-effort. Preserved the why-comment discipline.
- Read `ContentCorpusWriteLock.cs` — confirmed `LockFileName="content.lock"`,
  `AcquireFor(contentDbPath, TimeSpan?)` derives the lock path from the DB path's DIRECTORY and throws
  `TimeoutException` on expiry; lock lives next to the DB in `.miller/`.
- Read `MetricHistoryWriteLock.cs` (new file, read directly) — confirmed same shape,
  `LockFileName="history.lock"`, `AcquireFor(historyDbPath, TimeSpan?)`.
- Read `MetricHistoryStore.cs` — confirmed `HistoryDbFileName="history.db"` const (used to build the history
  DB path); `ContentCorpusSidecar.ContentDbPathFor` uses literal `"content.db"` (matched that literal).
- grep for `DeleteContentsExceptLock` / `TryDeleteEmptiedDir` callers — confirmed exactly three production
  sites (CLI `RemoveMillerDir`, WorkspaceTool `Remove` + `RemoveResolvedTarget`) plus the two test files;
  safe to change the signature (added an optional param, so no caller breaks).
- Read WorkspaceTool ctor + line ~680 — confirmed `_acquireWriterLock` is injected and the ~680 site is the
  prime-scan path, NOT a remove path; left it untouched.
- Read `WorkspaceToolTests.cs` remove tests — confirmed harness uses real `SingleWriterLock.TryAcquire` by
  default and `NoopLease` in the delete-happy cases; my real sidecar-lock acquisition succeeds on free locks
  and refuses on held ones without breaking them (re-ran: 61/61).

## Judgment calls

- **Short acquire timeout = 2s** (`WorkspaceWriteLeases.DefaultTimeout`). Design says "short timeout (2–5s)";
  a remove is interactive/CI teardown, so refuse-promptly beats block-long. Both CLI regression tests hold
  the lock for the whole call and thus each spend ~2s before the refusal — well within the fast-suite budget
  (full suite 20s). Direct helper tests pass a 200ms timeout to stay fast.
- **`DeleteContentsExceptLock` param is `additionalHeldLockFileNames`** (added to the always-skipped
  `indexer.lock`) rather than a full replacement set — keeps the parameterless overload byte-identical and
  keeps the "explicit, not `*.lock`-blanket" property the task requires.
- **Helper lives in `SingleWriterLock.cs`** (an owned file, same namespace as all three locks) rather than a
  new file, so the fixed lock order + skip-set have a single home. Deliberately minimal (acquire-in-order /
  dispose-in-reverse), not a general lock manager.
- **DB-path literal `"content.db"`** passed to `ContentCorpusWriteLock.AcquireFor` mirrors the existing
  `ContentCorpusSidecar` literal (no shared const exists); the lock only needs the directory.

## Concerns

- None blocking. Minor: the `"content.db"` filename is a literal in two places (sidecar + this helper); a
  shared const would be marginally safer but is out of scope for this task's owned files.
- Cross-process behavior is exercised in-process (a second file handle stands in for another instance),
  matching the existing `SingleWriterLockTests` convention; no julie spawn, so all additions stay fast-suite.
