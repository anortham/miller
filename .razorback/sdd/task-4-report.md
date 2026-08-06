# Task 4 report — SqliteOnlineBackup page-stepped copier

## Worktree state

- Path: `/Users/murphy/source/miller/.claude/worktrees/rebind-p3-miller-wiring`
- Branch: `rebind-p3-miller-wiring`
- HEAD at start: `b0d96b75`; HEAD at report time: `db6c9d7b` (the lead committed sibling tasks during the run).
- Both owned files are still untracked (`??`) — `parallel-lead-commit`, no `git add`/`git commit` was run.

## What I implemented

`src/Miller.Indexing/SqliteOnlineBackup.cs` — two public types:

1. `BackupOutcome` — `sealed record BackupOutcome(BackupOutcome.Kind Result, string? FailureReason)` with a
   nested `Kind` enum (`Completed | BudgetExhausted | Failed`), two singleton static properties, and a
   `Failed(string reason)` factory. Shape mirrors the repo's existing `ScanOutcome`
   (`src/Miller.Server/Hosting/ScanOutcome.cs:26`) rather than an abstract-record hierarchy.
2. `SqliteOnlineBackup` — one static class:
   - `public static TimeSpan ResolveBudget()` → `internal static TimeSpan ResolveBudget(Func<string, string?>)`.
     Reads `MILLER_REBIND_COPY_BUDGET`: positive seconds first, then `TimeSpan.Parse`; unset/invalid → 3 minutes.
   - `public static BackupOutcome Copy(string sourceDb, string destinationDb, TimeSpan budget,
     Func<DateTimeOffset> clock, CancellationToken ct)` → `internal` overload taking `int pagesPerStep`.
     The public entry passes the `PagesPerStep = 1024` constant.

Copy protocol:

- Destination trio (`db`, `-wal`, `-shm`) deleted before the copy starts and again on every non-completed exit.
- Source opened through `SqliteReadOnlyAccess.Open` (`Mode=ReadOnly`, `Pooling=false`, WAL-sidecar directory
  probe). Destination opened `ReadWriteCreate`, `Pooling=false`. Zero writes to the source: no checkpoint,
  no lock, no WAL disturbance.
- `raw.sqlite3_backup_init(dest, "main", source, "main")`, then a loop that checks `ct` and
  `clock() >= deadline` **before each** `raw.sqlite3_backup_step(backup, pagesPerStep)`.
  `SQLITE_DONE` → `Completed`; `SQLITE_OK` → continue; `SQLITE_BUSY`/`SQLITE_LOCKED` → 25 ms pause, continue
  (a source write restarts the backup internally — expected, and the budget bounds the livelock);
  anything else → `Failed` with `sqlite3_errmsg`.
- `sqlite3_backup_finish` is always called (including on the throwing path); a non-`SQLITE_OK` finish downgrades
  an otherwise-`Completed` copy to `Failed`.
- Infrastructure exceptions (`SqliteException`, `IOException`, `UnauthorizedAccessException`,
  `InvalidOperationException`, `FileNotFoundException`) become `Failed(reason)` — the caller falls back to a
  plain bootstrap scan, so this is best-effort and never throws at it. `OperationCanceledException` is
  re-thrown after cleanup.

`Microsoft.Data.Sqlite`'s `BackupDatabase` is not used anywhere.

## Verification

| Scope | Command | Result |
|---|---|---|
| worker (TDD loop) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Debug --filter "FullyQualifiedName~SqliteOnlineBackupTests"` | **15 passed, 0 failed**, 221 ms |
| worker ceiling (full fast suite) | `scripts/test.sh` | **6054 passed, 0 failed, 2 skipped**, 25 s test duration / 30 s wrapper wall time, Release build **0 warnings / 0 errors** |

Timestamp: 2026-08-05, ~19:07 local.

### Invariant each test proves

| Test | Invariant |
|---|---|
| `Copy_PopulatedDatabase_ProducesAnIntactRowEqualCopy` | A completed copy is a valid database with the same rows (`PRAGMA integrity_check` = `ok`, row-count equality). |
| `Copy_CompletedCopy_LeavesASelfContainedDestinationFile` | A completed copy is one file — no `-wal`/`-shm` left for Task 6 to promote around. |
| `Copy_BudgetElapsedBetweenSteps_ReportsExhaustedAndDeletesThePartialDestination` | The budget is enforced **between steps** (injected clock, `pagesPerStep: 1`, ~200-page source), returns `BudgetExhausted`, deletes the partial trio, and leaves the source file byte-identical (SHA-256 before/after). |
| `Copy_CancelledToken_ThrowsAndDeletesThePartialDestination` | Cancellation is honoured and still cleans up. |
| `Copy_SourceWithALiveWriterConnection_CopiesEveryCommittedRowWithoutTouchingTheSourceFile` | The core rebind claim: with a live WAL writer connection held open, the copy completes, carries all 2000 committed rows, passes `integrity_check`, and the source `symbols.db` bytes are unchanged. |
| `Copy_MissingSource_ReportsFailureNamingThePath` | A broken source is a `Failed` outcome naming the path, not an exception into the orchestrator. |
| `ResolveBudget_ReadsSecondsAndTimeSpanSpellings` (4 cases) | `"90"`, `"0.5"`, `"00:00:42"`, `"00:04:00"` all parse. |
| `ResolveBudget_UnsetOrInvalid_FallsBackToThreeMinutes` (5 cases) | null / `""` / `"0"` / `"-00:00:01"` / garbage → 3 minutes. |

### Live-writer test is a real proof, not a tautology

I probed the scenario the test builds: after 2000 WAL-mode inserts the source `symbols.db` main file is
**4096 bytes** (header page only) while all rows sit in a **313 KB `-wal`**. So
`RowCount(destination) == 2000` can only be satisfied by reading *through* the uncheckpointed WAL, and the
unchanged main-file hash is genuine evidence of zero writes to the source database file.

### Mutation checks (the tests discriminate)

Both load-bearing lines were mutated and the suite caught each; the file was then restored and verified
byte-identical to the pre-mutation original.

1. Cleanup narrowed to `Kind.Failed` only (skipping `BudgetExhausted`) →
   `Copy_BudgetElapsedBetweenSteps_...` fails on `Assert.False()`. This also proves a *partial destination
   really existed*, i.e. a step ran before the budget check fired.
2. Budget check neutralised → the same test fails on `Assert.Equal()` (got `Completed`).

### Fast-suite wall-clock note (not attributable to this task)

The first `scripts/test.sh` run tripped the 30 s tripwire at 145 s. That was machine contention: five parallel
task workers were building and testing simultaneously (load average ~20, 36 concurrent `dotnet` processes).
Measured under the same load:

- fast suite **excluding** `SqliteOnlineBackupTests`: 33 s test duration — already over the ceiling without me.
- fast suite **including** them: 31 s.
- Once sibling builds drained, `scripts/test.sh` reported **30 s, pass**.

Isolated, the 15 tests total **221 ms**. The suite's slowest tests are pre-existing and unrelated
(`MarkerSearchTests` ~45 s and `MetricSnapshotAggregatesTests` ~48 s under load).

## Files changed

- Created `src/Miller.Indexing/SqliteOnlineBackup.cs`
- Created `tests/Miller.Tests/Indexing/SqliteOnlineBackupTests.cs`

Nothing else was touched.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect target=SqliteReadOnlyAccess depth=full` | The read-only open discipline to reuse verbatim: `Mode=ReadOnly` + `Pooling=false` (the 2026-06-11 Eros unlinked-inode finding), the WAL `-shm` writable-directory probe, and the deliberate refusal of `immutable=1` (it silently drops uncheckpointed `-wal` rows under a live writer — exactly the case rebind copies). Confirmed `Open(string)` is the single entry point, `src/Miller.Indexing/SqliteReadOnlyAccess.cs:28`. |
| `search query="MILLER_PROMOTE_RETRY_TIMEOUT" mode=source` | Located the env-parsing precedent at `src/Miller.Indexing/FullRebuildPromotion.cs:12`. |
| `inspect target=FileOperationRetryOptions depth=full` | The exact parsing shape I mirrored: `double.TryParse` with `NumberStyles.Float`/`InvariantCulture` + `> 0` + NaN/Infinity guards, then `TimeSpan.TryParse` + `> TimeSpan.Zero`, else a default constant — plus the `public X Default => DefaultForEnvironment(Environment.GetEnvironmentVariable)` / `internal DefaultForEnvironment(Func<string,string?>)` test seam I copied for `ResolveBudget`. |
| `inspect target=FullRebuildPromotion depth=full` | The trio naming my cleanup mirrors: `<path>.rebuild` + `-wal` + `-shm`, and `PrepareRebuildTarget`'s "delete all three before writing" precedent (`FullRebuildPromotion.cs:85-101`). |
| `inspect target=ScanOutcome depth=overview` | The house style for a typed best-effort outcome: `sealed record` + nested `Kind` enum + static factories, with failures captured as a `Kind` rather than thrown at the caller. `BackupOutcome` follows it. |

## API-shape evidence

- **`SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 is already a direct reference**: `src/Miller.Indexing/Miller.Indexing.csproj:17`.
- **Init**: the csproj carries an explicit instruction not to call `SQLITEPCLRaw.Batteries_V2.Init()` — bundled
  `Microsoft.Data.Sqlite` auto-initializes the `e_sqlite3` provider (`Miller.Indexing.csproj:10-11`). I added no
  init call; all 15 tests pass, so the raw API is reachable off the provider Microsoft.Data.Sqlite installs.
- **Backup entry points exist in the referenced assembly**: `strings ~/.nuget/packages/sqlitepclraw.core/3.0.3/lib/net8.0/SQLitePCLRaw.core.dll | grep -i backup`
  → `sqlite3_backup_init`, `sqlite3_backup_step`, `sqlite3_backup_finish`, `sqlite3_backup_remaining`,
  `sqlite3_backup_pagecount`, plus the `sqlite3_backup` handle type.
- **Exact C# signatures verified by compile, not from memory** (`dotnet build src/Miller.Indexing -c Debug`,
  0 warnings under `TreatWarningsAsErrors`), which pins:
  - `raw.sqlite3_backup_init(sqlite3 dest, string destName, sqlite3 source, string sourceName)` → `sqlite3_backup`
  - `raw.sqlite3_backup_step(sqlite3_backup, int nPages)` → `int`
  - `raw.sqlite3_backup_finish(sqlite3_backup)` → `int`
  - `raw.sqlite3_errmsg(sqlite3)` / `raw.sqlite3_errstr(int)` → `utf8z`, with `.utf8_to_string()`
  - `SqliteConnection.Handle` → `SQLitePCL.sqlite3?`
  - `raw.SQLITE_OK/DONE/BUSY/LOCKED` are compile-time constants — proven because they are used as `is ... or ...`
    pattern operands, which the C# compiler rejects for non-constant fields.
- I did **not** use `sqlite3_backup_remaining`/`pagecount`: no progress reporting is in scope (YAGNI), and the
  loop's terminal condition is `SQLITE_DONE`.

## Self-review findings

- The budget is checked **before** each step, so a copy whose budget is already spent does zero SQLite work.
  The deadline is computed from one `clock()` read, so a test clock's read sequence is deterministic.
- `sqlite3_backup_finish` is called exactly once on every path, including the cancellation/throw path, so the
  handle is never leaked and never double-freed.
- The `Completed` path checks `finish`'s return code. Without that, a copy could report success on a
  destination the finish step failed to seal.
- Failure cleanup runs **after** both connections are disposed (the `using` block closes before the outcome is
  inspected), so the delete is not fighting an open handle.
- `Delete` swallows `IOException`/`UnauthorizedAccessException`: the abandon path must not turn a cleanup
  problem into a second failure, and `FullRebuildPromotion.PrepareRebuildTarget` reclaims leftovers under the
  single-writer lock anyway.
- Comment discipline: production code carries XML docs plus two why-comments (the BUSY pause, the cleanup
  swallow). Tests carry a class-level `<summary>` matching `FullRebuildPromotionTests` and zero inline comments.

## Judgment calls

1. **`ResolveBudget` has an internal `Func<string, string?>` seam.** The brief said "tests drive the public
   `Copy`/`ResolveBudget` surface only", but mutating process environment variables in a parallel xUnit suite is
   racy. `FileOperationRetryOptions` (`FullRebuildPromotion.cs:18-21`) solves the identical problem with exactly
   this public-no-arg / internal-injected pair, and its tests drive the internal overload
   (`FullRebuildPromotionTests.cs:161-187`). `InternalsVisibleTo Include="Miller.Tests"` already exists
   (`Miller.Indexing.csproj:23`). The required `public static TimeSpan ResolveBudget()` exists as specified.
2. **`pagesPerStep` is an internal `Copy` overload, not a public parameter.** The brief allowed a test seam via
   `InternalsVisibleTo`; the 1024 constant stays private and non-configurable at the public surface. Without the
   seam the budget-exhaustion test would need a source larger than 1024 pages (~4 MB), which does not belong in
   the fast suite.
3. **Cancellation throws `OperationCanceledException` rather than adding a `Cancelled` outcome.** The specified
   outcome set has three members and no cancellation state; throwing is the idiomatic .NET contract. Cleanup
   still runs before the throw.
4. **A 25 ms pause on `SQLITE_BUSY`/`SQLITE_LOCKED`.** Not a retry framework — one `Thread.Sleep`. Without it a
   contended source turns the (up to 3-minute) budget into a full-core spin hammering `sqlite3_backup_step`.
   `Copy` is already synchronous, so a blocking sleep is consistent with the surface.
5. **`BackupOutcome` lives in `SqliteOnlineBackup.cs`.** The repo's habit is one type per file
   (`ScanOutcome.cs`), but Task 4's file ownership permits creating only this one source file.
6. **Infrastructure exceptions become `Failed`, not throws.** Matches `ScanOutcome`'s documented "an extract
   failure is captured here as `Kind.Failed`, never thrown into the caller" and design §4's "exhaustion abandons
   rebind → plain bootstrap scan". Argument validation still throws, because those are caller bugs.
7. **The destination trio is deleted before the copy begins.** The helper does not *choose* the path, but it must
   own the file it writes: a stale `-wal` beside a freshly written destination is exactly the cross-inode WAL
   replay corruption `FullRebuildPromotion` documents.

## Concerns

- **Untested code path:** the `SQLITE_BUSY`/`SQLITE_LOCKED` branch has no automated test. Provoking a genuine
  `SQLITE_BUSY` from `sqlite3_backup_step` needs a concurrent writer racing at page granularity, which is a
  flaky-by-construction fast test. The budget check surrounding it *is* tested, so the branch cannot hang past
  the budget. Worth an explicit look during Task 6 integration or a Scale-tagged soak.
- **Not in this file, by design:** the design's "skip rebind while the source's `scan.progress` heartbeat is
  fresh" pre-check and the `ScanGovernorAdmission.TryAcquire` wrapping are orchestration concerns and belong to
  Task 6. This helper takes no governor lease and takes no source `SingleWriterLock`, which keeps the governor's
  lock-order rule out of tension as §4 requires.
- **Shared-tree friction during the run:** the test project transiently failed to compile several times because
  sibling parallel workers were mid-edit on `WorkspaceRegistry.cs` and `JulieExtractRunner`. I polled rather than
  touching their files. Neither of my files was ever implicated in those errors.
- **Fast-suite headroom is thin.** The suite sits at 25–33 s against a 30 s tripwire even without my tests. That
  is a pre-existing condition worth the lead's attention, not a Task 4 regression.
