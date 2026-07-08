# Metric History & Trends (P4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Build the `history.db` metric-history sidecar with hybrid writers, the `miller metrics history` CLI verb, dashboard trend sparklines, and the workspace-remove lock-coordination fix, per [2026-07-07-metric-history-design.md](2026-07-07-metric-history-design.md).

**Architecture:** A new append-only workspace-local SQLite sidecar owned by `MetricHistoryStore` (Miller.Indexing); the index leader records cheap SQL aggregates after converge (skip-on-busy, never blocking `_opsGate`), CLI commands append the heavy metrics they compute (canonical params only, artifact-identity guarded); reads are a CLI verb plus dashboard sparklines. A new `history.lock` joins `indexer.lock`/`content.lock`, and `workspace remove` learns to coordinate with all three.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite (WAL), xUnit, Razor Components (dashboard).

**Architecture Quality:** Approved shape: one store class behind one file; producers are single-call hooks; schema is the durable seam (`docs/contracts/metrics-history-v1.md`). Risk: medium (durable schema + first multi-process-append sidecar) — mitigations are all in the design doc's doubt-pass section and are binding.

## Global Constraints

- The design doc **is the spec**; its Write semantics, lock rules, and error-handling table are binding. Read it before implementing.
- `source` values: `converge | report | churn | risk | candidates` (the computing operation). `UNIQUE(artifact_id, revision, source)`.
- Trend ordering is by `snapshot_id`; `recorded_at_utc` is display/filter metadata only.
- **A missing metric is an absent row, never 0.**
- Leader-side history writes: `busy_timeout` ≈ 0, skip-on-busy, no retry loop, never block `_opsGate`. History failures never fail or delay indexing or the computing command.
- Schema policy: `meta.schema_version = 1`; leader-only transactional migration; newer-than-known version ⟹ skip writes + log; CLI may create-at-current-version only when the file is absent.
- Lock order everywhere: indexer `SingleWriterLock` → `content.lock` → `history.lock`. `history.lock` is held only for the append transaction.
- Heavy arms record **only canonical-parameter runs** (each operation's existing default range/limit/filters); non-default runs render normally but skip recording.
- No new MCP tool. Dashboard reads are read-only aggregate facts — no index hydration.
- `Miller.Core` stays zero-I/O (the store lives in `Miller.Indexing`). Build must stay 0 warnings / 0 errors.
- Tests that spawn `julie-extract` MUST be `[Trait("Category","Scale")]` + `ScaleTestSupport.RequireJulieServer()`; everything else stays in the fast suite.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClass>&Category!=Scale"` for the task's test classes (the explicit `Category!=Scale` term is required because a command-line `--filter` overrides the csproj default).

**Worker ceiling:** `scripts/test.sh` (fast suite). Workers do not run the scale suite.

**Worker gate invariant:** each task's acceptance criteria prove the new behavior through the public interface (store API, CLI output, rendered dashboard facts), not private plumbing.

**Lead affected-change scope:** `scripts/test.sh` after each batch lands.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings/0 errors) + `scripts/test.sh` + `scripts/test.sh scale` before handoff.

**Escalation triggers:** any change to `IndexerService`/`IndexerSidecarConverger`/`CrossWorkspaceRefreshService` or `SingleWriterLock` requires the scale suite at the branch gate (already included above).

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: MetricHistoryStore + lock | None - serial | Create: `src/Miller.Indexing/MetricHistoryStore.cs`, `src/Miller.Indexing/MetricHistoryWriteLock.cs`, `tests/Miller.Tests/Indexing/MetricHistoryStoreTests.cs` | Yes | Contract-first: every other task consumes the store API and schema. |
| Task 2: Leader converge arm | Batch A | Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (if wiring needs it); Create: `src/Miller.Indexing/MetricSnapshotAggregates.cs`, `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs`, `tests/Miller.Tests/Server/IndexerSidecarConvergerHistoryTests.cs`, one Scale e2e test file | No | None - safe parallel batch. |
| Task 5: Remove lock coordination | Batch A | Modify: `src/Miller.Indexing/SingleWriterLock.cs`, `src/Miller.Server/Cli/CliDispatch.cs` (WorkspaceRemove region only, ~:1854), `src/Miller.Server/Tools/WorkspaceTool.cs` (remove path only); Test: `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs` (extend), `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (remove-race additions only) | No | None - safe parallel batch. |
| Task 3: Heavy arms record | Batch B | Modify: `src/Miller.Server/Tools/ReportTool.cs`, `src/Miller.Server/Tools/MetricsTool.cs` (churn/risk recording), `src/Miller.Server/Cli/CliDispatch.cs` (Metrics/Report/ReferencesCandidates handler regions, ~:582–:780); Test: `tests/Miller.Tests/Server/ReportToolTests.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/RiskMetricsToolTests.cs` (extend) | No | None - safe parallel batch (file-disjoint from Task 6). |
| Task 6: Dashboard trends + health | Batch B | Modify: `src/Miller.Dashboard/DashboardIndexFactsReader.cs`, `src/Miller.Dashboard/DashboardData.cs`, `src/Miller.Indexing/WorkspaceHealthReader.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs` (health section); Create: `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor`; Test: dashboard reader + health facts test files | No | None - safe parallel batch (file-disjoint from Task 3). |
| Task 4: CLI `metrics history` + contract doc | None - serial | Modify: `src/Miller.Server/Tools/MetricsTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs` (Metrics handler), `src/Miller.Server/Cli/CliCapabilities.cs`; Create: `docs/contracts/metrics-history-v1.md`; Test: `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (extend) | Yes | Touches `MetricsTool.cs` + `CliDispatch.cs` after Task 3 edits the same files. |
| Task 7: Boundary docs sync | None - serial | Modify: `CLAUDE.md`, `AGENTS.md` (generated), `README.md`, `docs/README.md` | Yes | Documents what shipped; must run after all code tasks land. |

Commit mode: Tasks 1, 4, 7 `serial-worker-commit`; Batch A and Batch B tasks `parallel-lead-commit`.

---

### Task 1: MetricHistoryStore + MetricHistoryWriteLock

**Files:**
- Create: `src/Miller.Indexing/MetricHistoryStore.cs`
- Create: `src/Miller.Indexing/MetricHistoryWriteLock.cs`
- Test: `tests/Miller.Tests/Indexing/MetricHistoryStoreTests.cs`

**Interfaces:**
- Consumes: `SingleWriterLock` flock mechanics as the pattern for `MetricHistoryWriteLock` (`ContentCorpusWriteLock` in `src/Miller.Indexing/ContentCorpusWriteLock.cs` is the closer template — copy its shape, lock file name `history.lock`).
- Produces (later tasks depend on these exact shapes):
  - `MetricHistorySnapshot(string WorkspaceId, string ArtifactId, long Revision, string ExtractorVersion, string MillerVersion, string Source, IReadOnlyList<MetricHistoryPoint> Metrics)` where `MetricHistoryPoint(string Metric, double Value, string? DetailJson)`.
  - `MetricHistoryStore.RecordConverge(string historyDbPath, MetricHistorySnapshot snapshot)` — INSERT OR IGNORE semantics, skip-on-busy (returns a result enum `Recorded | SkippedBusy | SkippedNewerSchema | SkippedDuplicate`, never throws for those).
  - `MetricHistoryStore.RecordRun(string historyDbPath, MetricHistorySnapshot snapshot, Func<(string ArtifactId, long Revision)> identityRecheck)` — per-source upsert in one transaction; calls `identityRecheck` inside the transaction and returns `SkippedIdentityChanged` on mismatch.
  - `MetricHistoryStore.ReadTrend(string historyDbPath, IReadOnlyList<string> metrics, int limit, int maxPoints)` returning ordered-by-`snapshot_id` points `{ SnapshotId, RecordedAtUtc, ArtifactId, Revision, Source, Metric, Value }` with uniform-stride downsampling to `maxPoints`.
  - `MetricHistoryStore.ReadStatus(string historyDbPath)` → `(bool Present, int SchemaVersion, long SnapshotCount, long SizeBytes, bool CorruptRecovered)` for health.
  - `public const int SchemaVersion = 1`; `HistoryDbFileName = "history.db"`.

**Contract inputs:** Design doc schema DDL (Storage section) verbatim; write semantics and error-handling table are binding. `MillerVersion.Current` exists for the miller_version stamp (caller supplies it — the store takes strings, no dependency on Miller.Server).

**File ownership:** Create: `src/Miller.Indexing/MetricHistoryStore.cs`, `src/Miller.Indexing/MetricHistoryWriteLock.cs`, `tests/Miller.Tests/Indexing/MetricHistoryStoreTests.cs`

**Serialization required:** Yes

**Dependency reason:** Contract-first: every other task consumes the store API and schema.

**What to build:** The single owner of `history.db`: schema creation (WAL, idempotent DDL + `meta.schema_version`), the three write paths, trend/status reads, corruption rename-aside recovery (`history.db.corrupt-<utc-stamp>`), and the `history.lock` lease class. All writes take `MetricHistoryWriteLock` for the transaction only.

**Approach:** Follow `ContentCorpusWriteLock` for the lock (same timeout/poll shape, `LockFileName = "history.lock"`). Follow `SearchIndexWriter`'s SQLite hygiene but do NOT copy its temp-file-swap build — this file is append-only. Newer-`schema_version` check happens before any write; corruption detection on open exceptions and `PRAGMA integrity_check` failure. Skip-on-busy: `busy_timeout = 0` for `RecordConverge` (catch `SqliteException` busy codes → `SkippedBusy`); `RecordRun` may use a short (≤2s) busy_timeout.

**Acceptance criteria:**
- [ ] Schema matches the design DDL; `meta.schema_version = 1`.
- [ ] Converge dedup: second `RecordConverge` for the same `(artifact_id, revision)` returns `SkippedDuplicate`, no row change.
- [ ] Per-source upsert: `RecordRun` for `source='churn'` then `source='risk'` at the same revision yields two snapshots with independent timestamps; re-running churn replaces only the churn snapshot.
- [ ] `RecordRun` identity mismatch ⟹ `SkippedIdentityChanged`, nothing written.
- [ ] Busy writer ⟹ `RecordConverge` returns `SkippedBusy` without blocking (test with a held write transaction).
- [ ] Newer `schema_version` in meta ⟹ both writers skip and report, file untouched.
- [ ] Corrupt file ⟹ renamed aside, fresh DB created, `ReadStatus.CorruptRecovered` true.
- [ ] `ReadTrend` orders by `snapshot_id` (test with out-of-order `recorded_at_utc`), downsamples to `maxPoints` by uniform stride.
- [ ] Absent metric ⟹ absent row (no zero-fill anywhere).
- [ ] Worker-scope verification passes and the change is committed by the worker (serial-worker-commit).

---

### Task 2: Leader converge arm + aggregates

**Files:**
- Create: `src/Miller.Indexing/MetricSnapshotAggregates.cs`
- Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`
- Modify: `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Test: `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs`, `tests/Miller.Tests/Server/IndexerSidecarConvergerHistoryTests.cs`, plus one Scale e2e test (converge on a real fixture writes a converge snapshot)

**Interfaces:**
- Consumes: Task 1's `MetricHistoryStore.RecordConverge` + `MetricHistorySnapshot`. Existing readers for the aggregates: `CloneGroupReader`-style SQL over `symbols.db` (`GROUP BY body_hash`), `complexity_metrics` table (see `ComplexityRankingReader`), and the region index for marker counts (only when available — follow how `ReportTool.ReadMarkerSection` probes availability).
- Produces: `MetricSnapshotAggregates.ReadConvergeMetrics(string symbolsDbPath, IRegionSearchIndex? regionIndex)` → the converge metric list: `symbol_count`, `file_count`, `language_count`, `complexity_p50|p90|max`, `clone_group_count`, and `marker_total` (+ per-marker `detail_json`) only when the region index is available.

**Contract inputs:** The converge hook runs inside the existing `TryConvergeSidecar` flow under `_opsGate` — recording is a best-effort step AFTER the sidecar converge attempt, independent of its success (design: "Cheap arm"). Extractor version comes from `artifact_metadata.binary_version`; workspace_id/revision are already in scope at the hook sites.

**File ownership:** Modify: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`, `src/Miller.Server/Hosting/MillerServiceRegistration.cs` (if wiring needs it); Create: `src/Miller.Indexing/MetricSnapshotAggregates.cs`, `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs`, `tests/Miller.Tests/Server/IndexerSidecarConvergerHistoryTests.cs`, one Scale e2e test file

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** The cheap arm: a pure-SQL aggregates reader and its single call site in the leader's converge path (both `IndexerSidecarConverger` and the cross-workspace refresh path). Any exception is caught and logged; the converge outcome is unchanged.

**Approach:** Keep the aggregates reader read-only (`SqliteReadOnlyAccess.Open`, same as `CloneGroupReader`). Complexity percentiles via SQL (`ORDER BY value LIMIT 1 OFFSET (count*p)/100` pattern or read values and compute in C# — either, but bounded). Do not add any polling, timer, or watcher: the hook is strictly within the existing converge call. The Scale test rides the existing julie-spawning workspace-test pattern with `ScaleTestSupport.RequireJulieServer()` and the class-level Scale trait.

**Acceptance criteria:**
- [ ] After a (simulated) converge, `history.db` holds one `source='converge'` snapshot with the metric set above; marker metrics absent when no region index.
- [ ] A history failure (locked/corrupt file) does not change converge behavior or throw out of the hook (test by pre-holding the lock / pre-corrupting).
- [ ] Same-revision re-converge records nothing new (dedup observed through the store).
- [ ] Scale e2e: real extract → converge → snapshot present with plausible values.
- [ ] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit).

---

### Task 3: Heavy arms record what they compute

**Files:**
- Modify: `src/Miller.Server/Tools/ReportTool.cs`
- Modify: `src/Miller.Server/Tools/MetricsTool.cs` (churn/risk recording only — do not touch the history verb, that's Task 4)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`Metrics` :582, `Report` :627, `ReferencesCandidates` :731 handler regions)
- Test: extend `tests/Miller.Tests/Server/ReportToolTests.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/RiskMetricsToolTests.cs`

**Interfaces:**
- Consumes: Task 1's `MetricHistoryStore.RecordRun` with the identity-recheck callback; existing tool cores (`ReportTool.Run`, `MetricsTool.Run`, the candidates path via `DeadCodeCandidateReader`).
- Produces: recorded metric names later read by Task 4/6 — `source='report'`: the report's index/marker/complexity/clone scalars plus `churn_files_changed`/`risk_top_score` when its git sections are available; `source='churn'`: `churn_files_changed`; `source='risk'`: `risk_top_score`, `risk_rows`; `source='candidates'`: `dead_code_candidate_count`, `dead_code_suppressed_total` (suppressed breakdown in `detail_json`).

**Contract inputs:** Canonical-params rule: record only when the run used the operation's existing defaults (range/limit/filters as defined by the current `MetricsTool`/`ReportTool` default constants — read them, don't invent). Params go in `detail_json` regardless. Identity: capture `(artifact_id, revision)` from `artifact_metadata` before computing; the store re-checks inside the append transaction. Recording only happens for registered workspaces with a `.miller` dir; failures warn and never change the command's exit code or output.

**File ownership:** Modify: `src/Miller.Server/Tools/ReportTool.cs`, `src/Miller.Server/Tools/MetricsTool.cs` (churn/risk recording), `src/Miller.Server/Cli/CliDispatch.cs` (Metrics/Report/ReferencesCandidates handler regions, ~:582–:780); Test: `tests/Miller.Tests/Server/ReportToolTests.cs`, `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/RiskMetricsToolTests.cs` (extend)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (file-disjoint from Task 6).

**What to build:** After each of the four commands renders successfully with canonical params, build a `MetricHistorySnapshot` from the values it just computed and call `RecordRun`. One small shared helper (in `CliDispatch` or a tool-side static) may own the "registered workspace? canonical params? capture identity" boilerplate.

**Approach:** Do not recompute anything for recording — reuse the values already in the composed facts records. Non-canonical run tests assert NO snapshot was written. Keep the tools' pure cores side-effect-free where possible: prefer recording from the CLI handler layer, passing in the computed facts.

**Acceptance criteria:**
- [ ] Default-params `miller report` / `metrics churn` / `metrics risk` / `references candidates` each write their snapshot with the metric names above.
- [ ] Non-default params (e.g. `--range 90d`) ⟹ normal output, no snapshot.
- [ ] History-write failure ⟹ command output and exit code unchanged, warning logged.
- [ ] Churn-then-risk at one revision: two snapshots, independent timestamps.
- [ ] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit).

---

### Task 4: `miller metrics history` + contract doc

**Files:**
- Modify: `src/Miller.Server/Tools/MetricsTool.cs` (new `history` operation)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`Metrics` handler: parse `--metric`, `--limit`)
- Modify: `src/Miller.Server/Cli/CliCapabilities.cs` (advertise the new read surface)
- Create: `docs/contracts/metrics-history-v1.md`
- Test: extend `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: Task 1's `ReadTrend`; metric names from Task 2/3 (default set: `symbol_count`, `complexity_p90`, `clone_group_count`, `marker_total`, `dead_code_candidate_count`).
- Produces: `miller metrics history [--metric a,b,…] [--limit N] [--json]`; JSON envelope `{ workspace_id, metrics: [{ metric, points: [{ recorded_at_utc, artifact_id, revision, source, value }] }] }`, documented in `docs/contracts/metrics-history-v1.md` (schema DDL + CLI contract + stability rules, modeled on `docs/contracts/references-candidates-v1.md`).

**Contract inputs:** Default `--limit 20`; compact output one line per snapshot, newest last. Empty/missing history ⟹ friendly "no trend data yet — run `miller report`" line (exit 0), matching the design's read-surface section.

**File ownership:** Modify: `src/Miller.Server/Tools/MetricsTool.cs`, `src/Miller.Server/Cli/CliDispatch.cs` (Metrics handler), `src/Miller.Server/Cli/CliCapabilities.cs`; Create: `docs/contracts/metrics-history-v1.md`; Test: `tests/Miller.Tests/Server/MetricsToolTests.cs`, `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (extend)

**Serialization required:** Yes

**Dependency reason:** Touches `MetricsTool.cs` + `CliDispatch.cs` after Task 3 edits the same files.

**What to build:** The read verb and its stable contract doc. Follow the existing `metrics` operation pattern (`RunClones`/`RunComplexity` shape: parse → reader → compact/JSON render).

**Acceptance criteria:**
- [ ] Compact and `--json` outputs match the contract doc; `--metric` filters; `--limit` bounds.
- [ ] Ordering by `snapshot_id` is observable (seeded out-of-order timestamps render in insertion order).
- [ ] Empty history is a friendly exit-0 message in compact and an empty `metrics` array in JSON.
- [ ] `capabilities --json` lists the new surface.
- [ ] Worker-scope verification passes and the change is committed by the worker (serial-worker-commit).

---

### Task 5: `workspace remove` coordinates with all workspace-local locks

**Files:**
- Modify: `src/Miller.Indexing/SingleWriterLock.cs` (`DeleteContentsExceptLock` → skip every held lock file; debris cleanup after release)
- Modify: `src/Miller.Server/Cli/CliDispatch.cs` (`WorkspaceRemove` :1854 region)
- Modify: `src/Miller.Server/Tools/WorkspaceTool.cs` (remove path, ~:795)
- Test: extend `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs` and `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs`

**Interfaces:**
- Consumes: `ContentCorpusWriteLock` (`content.lock`, exists today) and Task 1's `MetricHistoryWriteLock` (`history.lock`).
- Produces: remove semantics later tasks and users rely on: remove acquires indexer lock → `content.lock` → `history.lock` (short timeouts), refuses-in-use if any is held past timeout, deletes contents skipping every lock file it holds, then releases and best-effort deletes lock debris + emptied dir.

**Contract inputs:** This fixes a pre-existing defect (remove-vs-content-import race) — see the design's multi-process section and doubt-pass cycle 3. Both remove paths (CLI + `WorkspaceTool`) must get the same behavior; keep the existing refused-in-use result shapes.

**File ownership:** Modify: `src/Miller.Indexing/SingleWriterLock.cs`, `src/Miller.Server/Cli/CliDispatch.cs` (WorkspaceRemove region only, ~:1854), `src/Miller.Server/Tools/WorkspaceTool.cs` (remove path only); Test: `tests/Miller.Tests/Indexing/SingleWriterLockTests.cs` (extend), `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` (remove-race additions only)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Generalize the delete helper to take the set of held-lock file names (or discover `*.lock` and skip all of them — choose the explicit set; silent skipping of unknown lock files hides bugs), and make both remove paths acquire all three leases in the fixed order before deleting.

**Approach:** Preserve the existing comment discipline in `DeleteContentsExceptLock` (it documents WHY). Regression test: hold `content.lock` from a second handle, run remove, assert refused-in-use rather than a crash or partial delete; repeat for `history.lock`.

**Acceptance criteria:**
- [x] Remove acquires indexer → content → history locks; any unavailable ⟹ refused-in-use, nothing deleted.
- [x] Held lock files survive `DeleteContentsExceptLock`; debris removed after release; emptied dir removal unchanged.
- [x] Regression: remove during an in-flight content import no longer deletes `content.db` mid-write.
- [x] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit).

---

### Task 6: Dashboard trends + `workspace health` surfacing

**Files:**
- Modify: `src/Miller.Dashboard/DashboardIndexFactsReader.cs` (read trends from `history.db`)
- Modify: `src/Miller.Dashboard/DashboardData.cs` (trend facts records)
- Create: `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor`
- Modify: `src/Miller.Indexing/WorkspaceHealthReader.cs` + `src/Miller.Server/Tools/WorkspaceRender.cs` (health: history sidecar status/size)
- Test: extend the dashboard reader test file and the health facts tests (locate via the existing test names for `DashboardIndexFactsReader` / `WorkspaceHealthFacts`)

**Interfaces:**
- Consumes: Task 1's `ReadTrend`/`ReadStatus`; the sparkline metric set: `symbol_count`, `complexity_p90`, `clone_group_count`, `marker_total`, `dead_code_candidate_count`; `maxPoints=50`.
- Produces: a Trends section on the workspace detail page; `workspace health` (compact + `--json`) gains a history line (present/size/snapshot count/corrupt-recovered).

**Contract inputs:** Dashboard reads are read-only and must not hydrate indexes (CLAUDE.md dashboard rule). Sparklines render inline SVG in the existing dashboard style (see `WorkspaceLocalMetricsPanel.razor` / `ContextSavingsPanel.razor` for the established panel + CSS conventions); metrics with <2 points render "no trend data yet — run `miller report`". Dashboard is local-first: no external assets.

**File ownership:** Modify: `src/Miller.Dashboard/DashboardIndexFactsReader.cs`, `src/Miller.Dashboard/DashboardData.cs`, `src/Miller.Indexing/WorkspaceHealthReader.cs`, `src/Miller.Server/Tools/WorkspaceRender.cs` (health section); Create: `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor`; Test: dashboard reader + health facts test files

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (file-disjoint from Task 3).

**What to build:** The trend read model (per-metric point lists, downsampled), a sparkline panel on the workspace detail stack, and the health status line in both `workspace health` render paths.

**Approach:** Follow `ReadSearchSidecarStatus` in `DashboardIndexFactsReader` for the sidecar-probe pattern. Keep SVG generation in C# (testable), the `.razor` file thin. Absent metrics simply don't get a sparkline row.

**Acceptance criteria:**
- [ ] Workspace detail shows sparklines for available metrics from a seeded `history.db`; <2 points ⟹ the empty-state line.
- [ ] Missing `history.db` ⟹ panel renders the empty state; no error.
- [ ] `workspace health` compact + JSON include history status/size; corrupt-recovered is surfaced.
- [ ] No full-index load added to any dashboard path.
- [ ] Worker-scope verification passes and the verified diff is handed to the lead (parallel-lead-commit).

---

### Task 7: Boundary docs sync

**Files:**
- Modify: `CLAUDE.md` (replacement-boundary section), `README.md`, `docs/README.md`
- Generate: `AGENTS.md` via `scripts/sync-agents.sh`

**Interfaces:**
- Consumes: everything shipped in Tasks 1–6.
- Produces: docs that match the product.

**Contract inputs:** Design doc "Boundary housekeeping" section: P4 changes from designed-not-built to shipped; the dead-code sentence records that count-level report/dashboard surfacing was approved 2026-07-07 (per-symbol detail stays CLI-only); README/site copy mentions metric trends where metrics/report are listed; `docs/README.md` maps the new contract doc as active.

**File ownership:** Modify: `CLAUDE.md`, `AGENTS.md` (generated), `README.md`, `docs/README.md`

**Serialization required:** Yes

**Dependency reason:** Documents what shipped; must run after all code tasks land.

**What to build:** The boundary/doc truth-up. Edit `CLAUDE.md` only, then run `scripts/sync-agents.sh` and confirm `cmp -s CLAUDE.md AGENTS.md`.

**Acceptance criteria:**
- [ ] CLAUDE.md boundary paragraph reflects shipped P4 + the dead-code surfacing approval; AGENTS.md regenerated byte-identical.
- [ ] README + docs/README.md updated; no stale "designed-not-built" language remains.
- [ ] Worker-scope verification passes (fast suite — `AgentInstructionsTests` guards doc/tool sync) and the change is committed by the worker (serial-worker-commit).
