# Task 1 Report — MetricHistoryStore + MetricHistoryWriteLock

## What I implemented

The contract-first owner of the new append-only `history.db` sidecar, plus its cross-process lock:

- **`src/Miller.Indexing/MetricHistoryWriteLock.cs`** — a `history.lock` lease, mechanically identical to
  `ContentCorpusWriteLock` (exclusive `FileShare.None` handle on a sibling lock file). `AcquireFor(path, timeout)`
  with `TimeSpan.Zero` = single non-blocking attempt (leader converge), positive timeout = poll-until-expiry
  (CLI heavy arms / `workspace remove`). Throws `TimeoutException` on expiry.
- **`src/Miller.Indexing/MetricHistoryStore.cs`** — static store with the full public contract:
  - Records: `MetricHistoryPoint`, `MetricHistorySnapshot`, `MetricHistoryTrendPoint`, `MetricHistoryStatus`.
  - Enum `MetricHistoryWriteResult { Recorded, SkippedBusy, SkippedNewerSchema, SkippedDuplicate, SkippedIdentityChanged }`.
  - `RecordConverge` — `INSERT OR IGNORE` dedup, non-blocking (file lock `TimeSpan.Zero` + skip-on-busy).
  - `RecordRun` — per-source upsert (delete this `(artifact_id, revision, source)` row → FK `ON DELETE CASCADE`
    clears its metrics → re-insert), with `identityRecheck` invoked inside the append transaction.
  - `ReadTrend` — ordered by `snapshot_id`, per-metric uniform-stride downsample to `maxPoints`, `limit` bounds to
    the most-recent snapshots, absent metric = absent row.
  - `ReadStatus` — `(Present, SchemaVersion, SnapshotCount, SizeBytes, CorruptRecovered)`, best-effort/never-throws.
  - Corruption recovery: `PRAGMA quick_check` / open-exception ⟹ rename aside to `history.db.corrupt-<utc-stamp>`
    (+ delete `-wal`/`-shm`), start fresh.
  - Schema: idempotent `CREATE ... IF NOT EXISTS` matching the design DDL verbatim; `meta.schema_version = 1`;
    newer `schema_version` ⟹ skip writes, file untouched (checked before any WAL/DDL runs).
  - `public const int SchemaVersion = 1`; `public const string HistoryDbFileName = "history.db"`.

## Verification

- **Invariant:** the store's public API honors the design's write/read/recovery semantics.
- **Assigned scope/command:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~MetricHistoryStore&Category!=Scale"`
- **Result:** `Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 1 s`
- **Ceiling:** `scripts/test.sh` (fast suite) → `Passed! Failed: 0, Passed: 2972`, wall 17s (< 30s tripwire).
  (First run tripped a **pre-existing flaky** test `IndexerServiceLeadershipTests.StartAsync_ArtifactMatchesOwn_RunsOnlyTheStartupDeltaScan`;
  it passes in isolation and on rerun. My change is purely additive — `git status` shows only 3 new files, zero edits
  to existing sources — so it cannot affect IndexerService.)
- **Build:** `dotnet build src/Miller.Indexing -c Release` → 0 warnings / 0 errors.
- **Timestamp:** 2026-07-07 (session local).

## Files changed

- `src/Miller.Indexing/MetricHistoryStore.cs` (new)
- `src/Miller.Indexing/MetricHistoryWriteLock.cs` (new)
- `tests/Miller.Tests/Indexing/MetricHistoryStoreTests.cs` (new)

## Miller calls used + what each confirmed

- `inspect SqliteReadOnlyAccess depth=full` — confirmed the read-only open helper's exact shape:
  `internal static SqliteConnection Open(string dbPath)`, `Mode=ReadOnly`, `Pooling=false`, throws
  `FileNotFoundException` when absent + `InvalidOperationException` on the WAL-dir-not-writable trap. I call it from
  `ReadTrend`/`ReadStatus`, guarding `File.Exists` first (it throws on absent) and catching both exception types.
- `inspect CloneGroupReader depth=overview` — confirmed the reader-class idiom (`public static`, `Read(...)` with
  clamped args) that `SqliteReadOnlyAccess` serves; my read methods mirror that static-reader shape.
- `ToolSearch` (miller tool schemas) — loaded `inspect`/`search`/`context` for orientation.
- Direct reads (contract inputs named in the task, not discovery): `ContentCorpusWriteLock.cs` (lock template),
  `SearchIndexWriter.cs` (SQLite hygiene: `Pooling=false`, `SqliteType` bound params, `ClearAllPools` before file
  moves — I copied the hygiene, NOT the temp-swap build since history is append-only).

## API-shape evidence for every existing symbol relied on

- `SqliteReadOnlyAccess.Open(string) : SqliteConnection` — verified via `inspect` (body shown): opens `Mode=ReadOnly`,
  `Pooling=false`; `FileNotFoundException` if file missing, `InvalidOperationException` on SQLITE_READONLY. Used in
  both read paths behind a `File.Exists` guard + catch of both exception types.
- `ContentCorpusWriteLock` — verified by Read: `AcquireFor(string, TimeSpan?)`, `FileShare.None`, `TimeSpan.Zero`
  yields an immediate `TimeoutException`. `MetricHistoryWriteLock` is a faithful copy with `LockFileName="history.lock"`.
- `Microsoft.Data.Sqlite` 10.0.9 (`Miller.Indexing.csproj`) — `SqliteCommand.ExecuteNonQuery()` returns the affected
  row count (used to detect `INSERT OR IGNORE` dedup → `SkippedDuplicate`); `SqliteConnectionStringBuilder.DefaultTimeout`
  drives `sqlite3_busy_timeout` (see judgment calls).

## Self-review findings (fixed before reporting)

- **30s hang in the busy test (fixed).** First cut set `PRAGMA busy_timeout` on the connection; Microsoft.Data.Sqlite
  re-applies `sqlite3_busy_timeout(DefaultTimeout)` on every command and clobbers it, so a held write lock retried for
  the 30s default `DefaultTimeout`. Second cut used `DefaultTimeout=0` → **infinite** (30-min test kill). Final: drive
  busy via the connection string `DefaultTimeout` (whole seconds; floor 1s). See judgment calls.
- **Removed a contrived unused test helper** (`UnusedRecheck`) I briefly added then had to silence — deleted it.
- **Strengthened the upsert test** to assert `COUNT(*) FROM snapshot_metrics == 2` after a churn re-run, proving the
  FK `ON DELETE CASCADE` leaves no orphaned metric rows (the JOIN alone wouldn't catch an orphan leak).

## Judgment calls

- **`MetricHistoryStore.cs` (busy budget) — chose connection `DefaultTimeout` (1s floor) over sub-second busy_timeout.**
  Microsoft.Data.Sqlite 10.0.9 overrides `PRAGMA busy_timeout` with `sqlite3_busy_timeout(DefaultTimeout)` on every
  command, and `DefaultTimeout=0` means infinite, not immediate. The leader's real non-blocking guarantee comes from
  the **file lock** (`AcquireFor(path, TimeSpan.Zero)`): every legitimate Miller writer takes `history.lock` first, so
  inside the ops gate a competing Miller writer yields `SkippedBusy` instantly without ever opening the DB. The 1s
  DB-level floor is only a backstop for a *non-Miller* writer holding `history.db`, which does not occur in Miller.
  Documented inline.
- **`MetricHistoryStore.cs` — added an optional `DateTime? recordedAtUtc = null` trailing parameter to `RecordConverge`
  and `RecordRun` instead of an `IClock` abstraction.** The contract signatures stay call-compatible (later tasks call
  them unchanged, defaulting to `DateTime.UtcNow`) while tests seed deterministic / out-of-order timestamps for the
  ordering and independent-timestamp criteria. **This is the only deviation from the literal task signatures — additive
  and backward-compatible.**
- **`MetricHistoryStore.cs` — `foreign_keys=ON` set in `OpenForWrite` (per-connection, no file write) but WAL/DDL kept
  in `EnsureSchema` (runs only after the newer-schema check)** so a newer-schema DB is left byte-for-byte untouched
  (the "file untouched" criterion asserts unchanged file size).
- **`ReadTrend` — `limit`/`maxPoints` ≤ 0 treated as "no limit"/"no downsampling"; downsampling is per-metric series**
  (uniform stride, always includes first + last) since the dashboard renders one sparkline per metric.

## Concerns / notes for later tasks

- **Signature deviation (above):** the optional `recordedAtUtc` param. If a later task prefers a clock abstraction it's
  a trivial swap; the default-arg keeps all call sites valid today.
- **Accepted residual (from design, not a bug):** promotion takes no history lock, so a full-rebuild can still land
  between `RecordRun`'s identity re-check and commit — one old-artifact point may append after a newer converge point,
  a self-healing display-order blip. Matches the design's explicitly-accepted residual; no code guards it.
- **Not in this task (later tasks own):** leader converge hook (Task 2), heavy-arm call sites (Task 3), CLI
  `metrics history` + contract doc (Task 4), `workspace remove` lock coordination incl. generalizing
  `DeleteContentsExceptLock` to skip `history.lock` (Task 5), dashboard/health surfacing (Task 6). The store API shapes
  they depend on are frozen here.
- **Pre-existing flaky test** `IndexerServiceLeadershipTests.StartAsync_ArtifactMatchesOwn_RunsOnlyTheStartupDeltaScan`
  intermittently fails under parallel load — unrelated to this task, flagged for awareness.

## Review fix — reactive corruption recovery (no per-write `quick_check`)

**Finding (lead inline review):** `RecordConverge`/`RecordRun` called `RecoverIfCorrupt` on EVERY write, which ran
`PRAGMA quick_check` — a full-database page scan. history.db is keep-all retention, so that probe's cost grows
linearly with history forever, and it ran inside the leader's cheap converge arm (`_opsGate`). The design's
error-handling table specifies REACTIVE recovery ("on open failure or integrity error the writer renames the file
aside … and starts a fresh one"), not a proactive scan.

**Fix (TDD — tests adjusted first, then implementation):**

- Removed the unconditional `RecoverIfCorrupt` probe from both write paths; deleted the method and the now-unused
  `CorruptProbeTimeoutSeconds` constant. No write path does a full-DB scan anymore.
- Extracted the two transaction bodies into `ConvergeTransaction` / `RunTransaction` (run under the held write
  lock). Recovery is now reactive: the public method catches a corruption-class `SqliteException`
  (`SQLITE_CORRUPT` 11 / `SQLITE_NOTADB` 26 via the existing `IsCorruption` helper) thrown while opening,
  reading meta, `EnsureSchema`, or the write transaction, calls `RenameAside`, and RETRIES THE WRITE ONCE against
  the fresh DB so the current snapshot still lands. No second retry — a corruption on the retry propagates for the
  hook caller to wrap. The existing `IsBusy` catch (→ `SkippedBusy`) is unchanged and ordered before the
  corruption catch.
- `ReadStatus`/`ReadTrend` untouched: reads stay never-throw and never trigger a recovery rename (read-only paths).

**Tests:** renamed the corrupt-file test to `RecordConverge_corrupt_file_is_reactively_renamed_aside_and_the_snapshot_still_lands`
(seed garbage → `RecordConverge` → assert `Recorded`, single `.corrupt-*` sibling, fresh DB holds the snapshot,
`CorruptRecovered`), and added the equivalent `RecordRun_corrupt_file_is_reactively_renamed_aside_and_the_snapshot_still_lands`
(also asserts the metric value round-trips). Both fail against a hypothetical no-recovery build and pass here.

**Verification:**
- `dotnet test … --filter "FullyQualifiedName~MetricHistoryStore&Category!=Scale"` → `Passed! Failed: 0, Passed: 16` (was 15; +1 RecordRun corrupt test).
- `scripts/test.sh` (fast suite) → `Passed! Failed: 0, Passed: 2973`, wall 17s (< 30s tripwire).
- `dotnet build src/Miller.Indexing -c Release` → 0 warnings / 0 errors.

**Judgment calls:**
- Ordered the `IsBusy` catch before the `IsCorruption` catch so a busy DB (a live, valid file) never mis-triggers a
  rename — only genuine corruption codes do.
- On the retry I invoke the transaction helper directly inside the corruption catch (not the outer try), so a busy
  on the retry is not swallowed to `SkippedBusy`. This is effectively unreachable — we hold `history.lock`, so no
  competing Miller writer can hold the DB — and it keeps "retry once, then let it throw" literal.
