# Pre-merge external-review fix report — metric history (P4)

Two VERIFIED external-review findings on `feat/metric-history`, both on the `history.db` store and its
read consumers. Worktree: `.claude/worktrees/metric-history` (branch `feat/metric-history`).

## Finding 1 (HIGH): corruption recovery discarded the WAL

`MetricHistoryStore.RenameAside` moved only `history.db` aside and then **deleted** `history.db-wal` /
`history.db-shm`. Because the store is WAL-mode and append-only (not derivable), committed snapshots living
only in the `-wal` until checkpoint were irreversibly lost, and the `.corrupt-*` file left behind was not a
complete recoverable SQLite image.

**Fix** (`src/Miller.Indexing/MetricHistoryStore.cs`): `RenameAside` now moves the **whole bundle under one
stamp** — `history.db` → `history.db.corrupt-<stamp>`, `-wal` → `…-<stamp>-wal`, `-shm` → `…-<stamp>-shm`
(`TryMove`, best-effort per sibling, **never delete**). The sibling naming matches the main file's new name,
so the moved-aside bundle stays SQLite-replayable. Fresh-DB restart behavior is unchanged; `RecordConverge` /
`RecordRun` stay best-effort never-throw for busy, and their single reactive-retry now renames the full bundle.

### Empirical mechanism note (why the test fixture is valid-header, not garbage)
A probe (Microsoft.Data.Sqlite 10.0.9) showed SQLite **deletes the orphan `-wal`/`-shm` on the failing
connection's close** for a *header-less garbage* file — before recovery ever runs — and such a wal is
unrecoverable anyway (nothing anchors it). For **valid-header WAL corruption** (the real-world case: torn
write / bad page, header magic intact) SQLite *preserves* the `-wal` on close, so `RenameAside` is exactly
where committed frames get saved. The test therefore builds a valid-header WAL db with 12 KB of
uncheckpointed committed frames, corrupts the body, and asserts the full `.corrupt-<stamp>{,-wal,-shm}`
bundle is preserved and nothing is deleted (`RenameAside_preserves_wal_resident_committed_data_…`). A second
test covers the no-siblings path.

## Finding 2 (MEDIUM): unreadable/corrupt history read as empty

`ReadTrend` swallowed `SqliteException`/`InvalidOperationException` → empty list, and `ReadStatus` degraded a
present-but-unreadable file to `present, schema 0, 0 snapshots`. Net effect: `miller metrics history` showed
the friendly exit-0 "no trend data yet" path on a broken sidecar, the dashboard showed an empty panel, and
`workspace health` showed a healthy-looking history line — violating "sidecar problems fail visibly".

**Fix** — three-state model (absent / readable / present-but-unreadable):

1. `MetricHistoryStore`: new typed `MetricHistoryUnreadableException`. `ReadTrend` throws it for a
   PRESENT-but-unreadable file; ABSENT stays empty-success. `MetricHistoryStatus` gains `Unreadable`
   (defaulted `false`, so existing named-arg call sites are untouched); `ReadStatus` sets it instead of the
   silent schema-0/count-0 default.
2. `CliDispatch.MetricsHistory`: added `MetricHistoryUnreadableException` to the operational-failure catch, so
   a broken sidecar maps to `metrics failed: …` **exit 3**; absent/empty keeps the friendly exit-0 nudge.
   (Contract `docs/contracts/metrics-history-v1.md` already specified exit 3 for unreadable; strengthened the
   "Empty / missing history" section to state the absent-vs-unreadable boundary explicitly.)
3. Dashboard `DashboardIndexFactsReader.ReadTrends`: catches the typed exception and returns an empty panel
   **flagged** via new `DashboardWorkspaceTrendsPanel.Unreadable` (+ `UnreadablePanel` factory);
   `WorkspaceTrendsPanel.razor` switches its empty-state text to "Metric history is unreadable…" instead of
   "No trend data yet".
4. `workspace health` render (`WorkspaceRender`): compact shows `history_db: unreadable` (never
   `present  0 snapshots`); JSON `history_db` block gains an `unreadable` boolean. (No change needed in
   `WorkspaceHealthFacts.cs` — `MetricHistoryStatus` already threads through it.)

## Tests (TDD — authored to fail against pre-fix code: the exception/flag/bundle-move didn't exist)

- `MetricHistoryStoreTests`: wal-resident bundle preservation; no-siblings path; `ReadTrend` throws on
  present-but-unreadable; `ReadTrend` absent = empty-success; `ReadStatus` sets `Unreadable`.
- `MetricsToolTests`: `RunHistory` throws typed exception on unreadable.
- `CliDispatchTests`: `metrics history` on unreadable sidecar → exit 3 + `metrics failed`.
- `DashboardRegistryReadTests`: unreadable → error-flagged empty panel; absent → `Unreadable` false.
- `WorkspaceRenderTests`: health compact `history_db: unreadable`; JSON `unreadable: true`.

## Verification

- Targeted filter (`MetricHistory|MetricsTool|CliDispatch|Dashboard|WorkspaceRender`, `Category!=Scale`):
  **300 passed, 0 failed**.
- `scripts/test.sh` (full fast suite): **3037 passed, 0 failed** (21s).
  - One transient failure appeared on the first fast-suite run in an UNRELATED leadership test
    (`IndexerServiceLeadershipTests.StartAsync_ArtifactOlderThanOwn_…`, a 5s timing test I did not touch);
    it passed in isolation and on the immediate full re-run — a pre-existing parallel-load flake.

## Miller MCP usage / API-shape evidence

Miller MCP was connecting at session start; branch files were read directly (the MCP index predates them, per
instructions). API shapes verified against branch source: `MetricHistoryStatus` positional record + all
construction sites (`ReadStatus`, `HealthWithHistory` tests); `MetricHistoryStore.ReadTrend`/`ReadStatus`
catch blocks; CLI `MetricsHistory` catch filter; `DashboardWorkspaceTrendsPanel` shape + `ReadTrends` call;
`WorkspaceRender` `HistorySidecarLabel` / `WriteHistorySidecarJson`. The SQLite close-time orphan-wal-deletion
behavior was verified empirically with a standalone Microsoft.Data.Sqlite 10.0.9 probe (since removed).

## Concerns

- The wal-preservation test holds a second SQLite connection open across `RenameAside`. SQLite opens with
  `FILE_SHARE_DELETE`, so the rename is permitted cross-platform; the test was executed on macOS only (this
  worktree). Behavior is deterministic here across repeated runs.
