# Task 3 — Heavy arms record what they compute

Status: **DONE** (implementation + tests complete; assigned verification green).
Commit SHA: none - parallel-lead-commit.

## What I implemented

After each of the four heavy commands renders successfully with **canonical (default) params**, the CLI
handler builds a `MetricHistorySnapshot` from the facts it just composed and calls
`MetricHistoryStore.RecordRun`. No fact is recomputed for recording — the tool cores surface the already-composed
points and the handler does the write. Tool cores stay side-effect-free.

- **`MetricsTool.cs`**
  - New file-local `internal static class MetricHistoryHeavyArm`: the shared heavy-arm vocabulary — `snapshots.source`
    values (`report|churn|risk|candidates`), the heavy-only metric names (`churn_files_changed`, `risk_top_score`,
    `risk_rows`, `dead_code_candidate_count`, `dead_code_suppressed_total`), and the `RangeLimitDetail(range,limit)`
    `detail_json` params builder (Utf8JsonWriter — AOT-clean, no reflection serializer).
  - `MetricsToolResult` gains `IReadOnlyList<MetricHistoryPoint>? SnapshotMetrics` (null for clones/complexity).
  - `RunChurn` → one `churn_files_changed` point = distinct changed paths among the bounded churn rows, range+limit
    in `detail_json`. `RunRisk` → `risk_rows` (always, a genuine 0 when git is available) + `risk_top_score`
    (ABSENT when 0 rows — a max over nothing is undefined, per absent-vs-zero).
- **`ReportTool.cs`**
  - `ReportToolResult` gains `IReadOnlyList<MetricHistoryPoint> SnapshotMetrics`, built by a pure `BuildSnapshotMetrics`
    projection of `ReportFacts`: `symbol_count`/`file_count`/`language_count`, `clone_group_count` (from the report's
    bounded clone list, `{"section_limit":N}` stamped in `detail_json` so it is distinguishable from the leader
    converge arm's exact count), `marker_total` (only when the marker section is available; per-marker breakdown in
    `detail_json`), and — only when git sections are available — `churn_files_changed` and `risk_top_score`.
  - Cheap-arm names reuse `MetricSnapshotAggregates.*`; git names reuse `MetricHistoryHeavyArm.*` — single-sourced.
- **`CliDispatch.cs`** (only the `Metrics`, `Report`, `ReferencesCandidates` regions + a new recorder block)
  - `HeavyArmIdentity` record + `CaptureHeavyArmIdentity(ctx)` — captures `(workspace_id, artifact_id, revision,
    extractor_version)` from `symbols.db` **before** the command computes (via `FreshnessReader` +
    `ExtractBinaryVersionReader`; `WorkspaceId.FromCanonicalRoot` fallback). Returns null (skip silently) when there
    is no `.miller`, no artifact_id, or no revision.
  - `RecordHeavyArmSnapshot(...)` — the single shared hook: no-op on non-canonical / no-metrics / no-identity;
    otherwise `RecordRun` with a `RecheckHeavyArmIdentity` callback that re-reads the live identity inside the append
    transaction (mismatch ⟹ `SkippedIdentityChanged`; read failure ⟹ guaranteed-mismatch sentinel). Any failure is
    swallowed to a stderr warning — command output and exit code are untouched.
  - `CandidateSnapshotMetrics` + `SuppressionDetailJson` — `dead_code_candidate_count` and `dead_code_suppressed_total`
    (full totals; `--limit` bounds only the display), per-rule suppressed breakdown in `detail_json`.
  - Wiring: `Report` (canonical = default range/limit, no test-filter), `Metrics` (churn/risk only; canonical = default
    range/limit, no test-filter, no `--include-commits`), `ReferencesCandidates` (canonical = default limit). Identity
    is captured before compute in each; recording happens after successful render.

## Verification

- Invariant/scope: fast suite only (temp-SQLite + seeded `symbols.db` fixtures; no julie-extract spawn). Assigned
  worker scope command:
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "(FullyQualifiedName~ReportTool|FullyQualifiedName~MetricsTool|FullyQualifiedName~RiskMetrics)&Category!=Scale"`
  → **Passed: 28, Failed: 0** (318 ms).
- Worker ceiling: `scripts/test.sh` → **Passed: 3018, Failed: 0** (21 s, under the 30 s ceiling).
- Build: `dotnet build src/Miller.Server/Miller.Server.csproj -c Release` → **0 warnings / 0 errors** (warnings are
  errors repo-wide).
- Timestamp: 2026-07-07 (local run this session).

Acceptance criteria coverage:
- Default-params report / churn / risk / candidates each write their snapshot — `Cli_Report_DefaultParams_WritesReportSnapshot`,
  `Cli_Candidates_DefaultParams_WritesCandidatesSnapshot`, `RecordHeavyArmSnapshot_Churn_WritesChurnSnapshotToHistoryDb`,
  `RecordHeavyArmSnapshot_ChurnThenRisk_...` (risk half).
- Non-default params ⟹ normal output, no snapshot — `Cli_Report_NonDefaultRange_SkipsRecording`,
  `RecordHeavyArmSnapshot_NonCanonicalRun_SkipsRecording`.
- History-write failure ⟹ output + exit code unchanged, warning logged — `Cli_Candidates_HistoryWriteFailure_LeavesOutputAndExitCodeUnchangedAndWarns`
  (history.db path made a directory ⟹ append throws ⟹ swallowed to stderr warning, exit 0, stdout intact).
- Churn-then-risk at one revision: two snapshots, independent timestamps — `RecordHeavyArmSnapshot_ChurnThenRisk_WritesTwoIndependentSnapshotsAtOneRevision`.
- Extra: absent-vs-zero (`Run_GitAndMarkersUnavailable_...`, `RunRisk_ZeroRows_OmitsTopScoreButRecordsRowCountZero`),
  identity guard (`RecordHeavyArmSnapshot_IdentityChangedMidCommand_SkipsRecording`), fact-surfacing
  (`Run_SurfacesIndexCloneMarkerAndGitSnapshotMetrics`, `RunChurn_SurfacesChurnFilesChangedSnapshotMetric`,
  `RunRisk_SurfacesTopScoreAndRowCountSnapshotMetrics`).

## Files changed

- `src/Miller.Server/Tools/MetricsTool.cs`
- `src/Miller.Server/Tools/ReportTool.cs`
- `src/Miller.Server/Cli/CliDispatch.cs`
- `tests/Miller.Tests/Server/MetricsToolTests.cs`
- `tests/Miller.Tests/Server/RiskMetricsToolTests.cs`
- `tests/Miller.Tests/Server/ReportToolTests.cs`

`git status` confirms exactly these six (no new files, `MetricSnapshotAggregates.cs` untouched — sibling-safe).

## Miller calls + confirmations

Miller MCP serves the main checkout; branch-new files (`MetricHistoryStore.cs`, `MetricSnapshotAggregates.cs`,
`MetricHistoryWriteLock.cs`) were Read directly per the task. `ToolSearch` loaded the deferred
`mcp__miller__inspect/search/context` schemas (confirming the server contract is live); the API-shape evidence
below came from direct Read of branch files (authoritative for exact edits on `feat/metric-history`).

## API-shape evidence (verified by Read)

- `MetricHistoryStore.RecordRun(historyDbPath, snapshot, Func<(string,long)> identityRecheck, DateTime? recordedAtUtc=null)`
  → `MetricHistoryWriteResult` (per-source upsert; recheck inside the append txn). `RecordRun` catches only
  busy/corruption `SqliteException` — a non-busy/corrupt open failure (e.g. history.db is a directory) propagates,
  which my recorder catch turns into a warning. (`MetricHistoryStore.cs`)
- `MetricHistorySnapshot(WorkspaceId, ArtifactId, Revision, ExtractorVersion, MillerVersion, Source, Metrics)`;
  `MetricHistoryPoint(Metric, Value(double), DetailJson)`; `HistoryDbFileName="history.db"`;
  `MetricSnapshotAggregates.HistoryDbPathFor(symbolsDbPath)` sibling path + reused `SymbolCount/FileCount/
  LanguageCount/CloneGroupCount/MarkerTotal` name consts. (`MetricHistoryStore.cs`, `MetricSnapshotAggregates.cs`)
- Identity reads reuse `FreshnessReader.ArtifactId()` + `LatestRevision()` and `ExtractBinaryVersionReader.TryRead`
  (both null-tolerant); `WorkspaceId.FromCanonicalRoot`. (`FreshnessReader.cs`, `ExtractBinaryVersionReader.cs`)
- `ReportTool.Run(...)` composes `ReportFacts` (IndexSection scalars, MarkerSection.Available/Total/Counts, bounded
  `Clones`, `GitSections.Available/Churn/Risk`); `ChurnReport(Range, Rows)` / `ChurnRow.Path`;
  `RiskReport(Range, Rows)` / `RiskRow.Score(long)`. (`ReportTool.cs`, `GitChurnAnalyzer.cs`, `RiskRanking.cs`)
- `MetricsTool.Run(...)` churn/risk go through `GitChurnAnalyzer.Read` / `RiskRanking.Read`; result carried in
  `MetricsToolResult`. (`MetricsTool.cs`)
- `DeadCodeCandidateReader.Read(dbPath, root)` → `DeadCodeCandidateReport(Result, LanguageCoverage, LiteralScan,
  Artifact)`; `DeadCodeResult(Candidates, Suppressions(all 11 rule ids), Examined, NeedsLiteralScan)`;
  `DeadCodeCandidates.SuppressionRuleIds` gives canonical order for the breakdown. (`DeadCodeCandidateReader.cs`,
  `DeadCodeCandidates.cs`)
- `report`/`metrics`/`references` are CLI-only surfaces — `ReportTool.Run`/`MetricsTool.Run` are called only by
  `CliDispatch` + tests (grep-verified), so there is no MCP tool wrapper to also record from.

## Self-review findings

- Verified existing CLI tests are unaffected: `Metrics_Churn_Json` uses `--range` (non-canonical → skip);
  clones/complexity are not churn/risk; existing `references candidates` fixtures seed no revision
  (`LatestRevision()==0` → capture null → skip). Full suite (3018) confirms no regressions.
- `err` is used as the warn writer; on a clean write nothing is written, so `Assert.Empty(errText)` in existing
  tests still holds.
- Nullable `DetailJson` args to `Assert.Contains` guarded with `!` (warnings-are-errors).

## Judgment calls

1. **Shared name/detail vocabulary home.** Heavy-arm source/metric-name consts + the `detail_json` params builder
   live in a file-local `internal static MetricHistoryHeavyArm` inside `MetricsTool.cs` (an owned file), NOT in
   `MetricSnapshotAggregates.cs` (owned by nobody in Batch B and read by the Task 6 sibling — editing it risked a
   conflict). Cheap-arm names still come from `MetricSnapshotAggregates`. **Task 4/6 read side:** the authoritative
   strings are `MetricHistoryHeavyArm.*` (churn/risk/candidates) + `MetricSnapshotAggregates.*` (converge/report
   shared) — reference those or the forthcoming contract doc, don't re-declare literals.
2. **Report `clone_group_count` is display-bounded.** The report composes only a top-`section_limit` clone list, so
   its `clone_group_count` is `min(true, section_limit)`, unlike the leader converge arm's exact count. Per
   "reuse already-composed facts, do not recompute" I record what the report composed and stamp `{"section_limit":N}`
   in `detail_json` so a consumer can tell the two apart. If Task 4/6 want an exact report-time clone count, that
   would require a fresh SQL count (a recompute) — deliberately not done here.
3. **Recording seam.** Metrics/report tool cores return the metric points (pure data); the DB write lives entirely in
   the CLI handler. One shared static recorder owns the boilerplate; no new abstractions/files beyond that.
4. **Test seam.** `RecordHeavyArmSnapshot` takes an optional `recordedAtUtc` (forwarded to `RecordRun`) so the
   churn-then-risk test can assert independent timestamps deterministically.

## Concerns / scope notes

- **Recording is CLI-only** (as scoped). `report`/`metrics`/`references` have no MCP tool wrapper, so there is no
  gap today — but if a future MCP surface computes these facts it would need its own hook.
- **Report CLI test spawns real `git`** (`ProcessGitHistoryReader`, fails fast on the non-repo temp dir → git
  sections unavailable). This mirrors the existing fast-suite `Metrics_Churn_Json` pattern and does not spawn
  julie-extract, so it correctly stays out of the Scale category.
- Non-canonical churn/risk gating includes `--include-commits` (default false). `--include-commits` does not change
  `churn_files_changed`, so this is conservative-but-harmless: such a run renders normally and simply skips recording.

---

## Fix round 1 (lead inline review) — 2026-07-07

**Finding (lead):** the report arm recorded a display-bounded `clone_group_count` (`report.Clones.Count`, capped
at `SectionLimit`) under the SAME metric name the converge arm records EXACTLY. `MetricHistoryStore.ReadTrend`
flattens points by metric name across all sources and ignores `detail_json`, so the merged Task 6 sparkline would
mix exact and truncated values into a sawtooth. The `detail_json` bound stamp documented but could not prevent it.

**Fix applied:** removed `clone_group_count` from the report arm entirely — it is now an ABSENT row, not a bounded
value (design rule: a metric is exact or absent, never misleading). The leader converge arm already records the
exact `clone_group_count` every revision and owns it; the report arm does NOT recompute an exact count.
`SectionLimitDetail` helper deleted (now unused). Comment in `ReportTool.BuildSnapshotMetrics` states the rationale.

**Other report-arm metrics re-checked vs their converge twin (as requested):**
- `symbol_count` / `file_count` / `language_count` — report uses `WorkspaceIndexFactsReader.ReadSymbolCounts`; the
  converge arm uses the identical `COUNT(*), COUNT(DISTINCT path), COUNT(DISTINCT language)` shape. **Exact, consistent.**
- `marker_total` — report bounds at `MarkerSearch.MaxLimit` (500); the converge arm bounds at `MarkerSearchLimit`
  (500, = `MarkerSearch.MaxLimit`). **Same bound, mixes cleanly.**
- `churn_files_changed` / `risk_top_score` — no converge twin (the converge arm does no git). `risk_top_score` is
  the global max (risk rows are score-desc ⟹ limit-insensitive), so it is stable across the report and risk arms.
  `churn_files_changed` is a bounded projection shared only with the churn arm (both heavy git arms) — noted, no
  converge-twin hazard.

**Tests updated:** `Run_SurfacesIndexMarkerAndGitSnapshotMetrics_ButNotBoundedCloneCount` (renamed; now asserts
`clone_group_count` ABSENT while index/marker/churn/risk points remain) and
`Run_GitAndMarkersUnavailable_SnapshotMetricsHoldOnlyIndexCounts` (renamed; asserts only the three exact index
scalars survive, `clone_group_count` absent).

**Re-verification:** assigned scope filter → **Passed: 28, Failed: 0** (277 ms). Build 0 warnings / 0 errors.
Files touched this round: `src/Miller.Server/Tools/ReportTool.cs`, `tests/Miller.Tests/Server/ReportToolTests.cs`
(still within the 6 owned files). commit SHA: none - parallel-lead-commit.
