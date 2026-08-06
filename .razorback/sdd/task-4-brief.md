### Task 4: SqliteOnlineBackup page-stepped copier

**Files:**
- Create: `src/Miller.Indexing/SqliteOnlineBackup.cs`
- Test: `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs`

**Interfaces:**
- Consumes: `SQLitePCL.raw` (`sqlite3_backup_init`, `sqlite3_backup_step`, `sqlite3_backup_finish`,
  `sqlite3_backup_remaining`/`pagecount`) via the already-referenced
  `SQLitePCLRaw.bundle_e_sqlite3`; `SqliteReadOnlyAccess` conventions for the source open
  (read-only, `Pooling=false`).
- Produces: `SqliteOnlineBackup.Copy(string sourceDb, string destinationDb, TimeSpan budget, Func<DateTimeOffset> clock, CancellationToken ct) → BackupOutcome`
  where `BackupOutcome` is `Completed | BudgetExhausted | Failed(reason)`. A public
  `static TimeSpan ResolveBudget()` reading `MILLER_REBIND_COPY_BUDGET` (seconds or `TimeSpan`
  format, default 3 minutes — same parsing shape as `MILLER_PROMOTE_RETRY_TIMEOUT`).

**Contract inputs:** contract design §4: page-stepped loop (NOT `Microsoft.Data.Sqlite`'s
`BackupDatabase` — one uncancellable `step(-1)` makes the budget unenforceable); budget checked
between steps; a source write restarting the backup is expected behavior the budget bounds;
zero writes to the source (read-only open, no checkpoint). Destination is the caller-supplied
`.rebuild` path; on `BudgetExhausted`/`Failed` the helper deletes its partial destination trio
before returning.

**File ownership:** Create `src/Miller.Indexing/SqliteOnlineBackup.cs`; Test `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The bounded, cancellable artifact snapshot: a raw SQLite backup loop stepping N
pages (start at 1024; constant, not configurable) with the wall-clock budget and cancellation
token checked between steps.

**Approach:** Fast tests use small real SQLite files in temp dirs (registry tests already do this
in the fast suite). Prove: a live-writer copy is consistent (write to the source between steps via
a hook seam or small page count, destination still passes `PRAGMA integrity_check`), and budget
exhaustion via an injected clock that jumps past the budget after the first step — no real
waiting. Verify the source file's bytes/mtime are untouched after a copy.

**Acceptance criteria:**
- [ ] Copy of a populated DB passes `PRAGMA integrity_check` and row-count equality.
- [ ] Budget exhaustion (injected clock) returns `BudgetExhausted`, deletes the partial
      destination trio, and leaves the source byte-identical.
- [ ] Source opened read-only: a copy of a write-locked/live source succeeds without writing to it.
- [ ] `ResolveBudget` parses seconds and `TimeSpan` spellings and defaults sanely.
- [ ] Worker-scope verification passes and the change is handed to the lead per
      parallel-lead-commit.

