# Task 6 — Dashboard trends + `workspace health` history surfacing

**Status:** COMPLETE. `commit SHA: none - parallel-lead-commit`

**One-line test summary:** Fast suite green — 3004 passed / 0 failed / 0 skipped (22s, under the 30s ceiling); assigned scope (`~Dashboard|~WorkspaceHealth`) 94 passed; `WorkspaceRenderTests` 47 passed.

## What I implemented

1. **Trend read model + reader** (`DashboardIndexFactsReader.ReadTrends`): probes the workspace's append-only
   `history.db` (sibling of `symbols.db`) via `MetricHistoryStore.ReadTrend(path, metrics, limit:0, maxPoints:50)`,
   groups the flattened points per metric, and builds one `DashboardTrendSeries` per metric that has ≥1 recorded
   point — in the exact canonical order `symbol_count, complexity_p90, clone_group_count, marker_total,
   dead_code_candidate_count`. Absent metric ⟹ no row (never a 0 row). Read-only sidecar open, **no index
   hydration** — same probe shape as `ReadSearchSidecarStatus`. Follows the local-metrics pattern: called directly
   in `ReadSnapshot` (not routed through `DashboardIndexFactsCache`, matching `ReadLocalMetricsPanel`).
2. **Records + pure SVG helper** (`DashboardData.cs`): `DashboardTrendSeries` (metric/label/points/first/latest,
   `HasTrend` = ≥2 points), `DashboardWorkspaceTrendsPanel` (`HasData`, `Empty(id)`), and `DashboardSparkline` — a
   pure static helper producing the SVG `polyline points` string over a fixed `0 0 100 24` viewBox (min at bottom,
   max at top; flat series ⟹ centred line; <2 points ⟹ empty string). `Trends` wired onto `DashboardSnapshot`.
3. **`WorkspaceTrendsPanel.razor`** (thin): one row per available metric; ≥2 points ⟹ inline SVG sparkline +
   delta label; <2 points ⟹ `no trend data yet — run miller report`; empty `Series` ⟹ panel empty-state line.
   Added to `WorkspaceDetailStack.razor` where the local-metrics panel lives. Local-first CSS (sparkline geometry,
   theme-var stroke) appended to `dashboard.css`.
4. **`workspace health` history line**: `WorkspaceHealthFacts` gained an optional `MetricHistoryStatus History`
   (threaded through `Create`); both call sites in `WorkspaceTool` (own + cross-workspace health) now pass
   `MetricHistoryStore.ReadStatus(MetricSnapshotAggregates.HistoryDbPathFor(dbPath))`. `WorkspaceRender.Health`
   renders compact `history_db: present  N snapshots  <size>  schema vX[  corrupt-recovered]` / `absent[
   corrupt-recovered]`, and a JSON `index.history_db` block (`present`/`schema_version`/`snapshot_count`/
   `size_bytes`/`corrupt_recovered`).

## Verification

- **Invariant:** dashboard trend + health surfaces are read-only aggregate facts, no full-index load; absent metric
  is an absent row.
- **Scope/command (assigned):**
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "(FullyQualifiedName~Dashboard|FullyQualifiedName~WorkspaceHealth)&Category!=Scale"`
  → **94 passed, 0 failed** (314 ms).
- **Worker ceiling:** `scripts/test.sh` → **3004 passed, 0 failed, 0 skipped**, 22s wall (<30s ceiling). Build 0
  warnings / 0 errors (Release, warnings-as-errors).
- Extra: `--filter FullyQualifiedName~WorkspaceRenderTests` → 47 passed (my health-render tests live there and fall
  outside the assigned `~WorkspaceHealth` substring — see concerns).
- **Timestamp:** 2026-07-07.

## Files changed (all within owned scope)

- `src/Miller.Dashboard/DashboardIndexFactsReader.cs` — `ReadTrends` + metric set/labels + dead-code const.
- `src/Miller.Dashboard/DashboardData.cs` — trend records, `DashboardSparkline`, `Trends` on snapshot + wiring.
- `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor` — **new** thin panel.
- `src/Miller.Dashboard/Components/WorkspaceDetailStack.razor` — panel placed after local-metrics.
- `src/Miller.Dashboard/wwwroot/dashboard.css` — sparkline/trend styles (additive; judgment call, see below).
- `src/Miller.Server/Tools/WorkspaceHealthFacts.cs` — optional `History` field threaded through `Create`.
- `src/Miller.Server/Tools/WorkspaceRender.cs` — compact `history_db` line + JSON `history_db` block + helpers.
- `src/Miller.Server/Tools/WorkspaceTool.cs` — `ReadHistoryStatus` helper, passed at both health call sites.
- `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` — trend reader + snapshot + sparkline geometry tests.
- `tests/Miller.Tests/Server/WorkspaceRenderTests.cs` — health compact + JSON history-surfacing tests.

## Miller calls + confirmations (branch new files Read directly per instructions)

- `ToolSearch select:...` loaded the Miller MCP tool schemas (deferred). The Miller index serves the **main**
  checkout; every file I edited on `feat/metric-history` is either new-on-branch or diverged, so per the task's
  hard requirement I Read them directly rather than trusting the stale main index. Files Read + confirmed:
  `MetricSnapshotAggregates.cs` (metric-name consts `SymbolCount/ComplexityP90/CloneGroupCount/MarkerTotal`,
  `HistoryDbPathFor`), `MetricHistoryStore.cs` (`ReadTrend` signature + snapshot_id ordering + uniform-stride
  downsample; `ReadStatus` → `MetricHistoryStatus(Present,SchemaVersion,SnapshotCount,SizeBytes,CorruptRecovered)`,
  never throws), `DashboardIndexFactsReader.cs` (`ReadSearchSidecarStatus` probe template, read-only open),
  `DashboardData.cs` (`DashboardLocalMetricsPanel`/`DashboardSnapshot`/`ReadSnapshot`/`ReadLocalMetricsPanel` — the
  not-cached direct-read pattern), `WorkspaceLocalMetricsPanel.razor`/`ContextSavingsPanel.razor`/
  `WorkspaceDetailStack.razor` (panel + CSS conventions + stack wiring), `WorkspaceHealthReader.cs`/
  `WorkspaceHealthFacts.cs` (`Create` shape + optional-arg convention), `WorkspaceRender.cs` (Health compact/JSON
  render points, sidecar label + JSON-writer patterns), `WorkspaceTool.cs` (~237 own + ~339 cross-workspace health
  `Create` call sites, `ReadLeaderFacts` helper style).

## API-shape evidence

- `MetricHistoryStore.ReadTrend(string, IReadOnlyList<string>, int limit, int maxPoints)` returns
  `IReadOnlyList<MetricHistoryTrendPoint(SnapshotId,RecordedAtUtc,ArtifactId,Revision,Source,Metric,Value)]`,
  ordered by snapshot_id, per-metric downsampled; `File.Exists` false ⟹ empty (drives the missing-db empty panel).
- `MetricSnapshotAggregates.HistoryDbPathFor(symbolsDbPath)` → sibling `history.db` (throws only on a dir-less path,
  caught in `ReadTrends`).
- `MetricHistoryStore.ReadStatus` is best-effort/non-throwing → safe inside the health path with no guard.
- Metric-name consts consumed from `MetricSnapshotAggregates`; `dead_code_candidate_count` has no shared const in
  `src/Miller.Indexing` yet (only in the sibling's test), so pinned as
  `DashboardIndexFactsReader.DeadCodeCandidateCountMetric`.

## Self-review findings

- Absent-vs-zero honoured: `values.Count == 0` ⟹ `continue` (no row); a 1-point metric IS a present series with
  `HasTrend=false` (renders the run-`miller report` hint), distinct from an absent metric.
- No index hydration on any dashboard path: `ReadTrends` opens only `history.db` (read-only); `ReadStatus` likewise.
- Trends deliberately NOT cached (mirrors `ReadLocalMetricsPanel`) — consistent with the existing panel pattern.
- Source-gen JSON: the new nested records reach the graph via `DashboardSnapshot.Trends`, so they serialize under
  the existing `DashboardJsonContext` with no extra `[JsonSerializable]` (build 0-warning confirms).

## Judgment calls

1. **`dead_code_candidate_count` literal:** no shared const exists in `src/Miller.Indexing` on this branch (the
   sibling task adds the producer), so I pinned it locally as `DeadCodeCandidateCountMetric` with a note to switch
   to a shared const when it lands. I did not touch any sibling file.
2. **`dashboard.css`:** not in the explicit owned-file list, but the sparkline styles are part of this panel and
   purely additive (new `.trend-*`/`.sparkline` selectors, theme-var driven, local-first — no external assets). The
   only Batch-B sibling (Task 3, heavy arms/CLI) does not touch the dashboard, so no conflict.
3. **JSON placement:** put `history_db` inside the health JSON `index` object next to `search_sidecar`/
   `content_corpus` (the other Miller-owned sidecars) for locality; additive, existing keys untouched.
4. **`maxPoints=50, limit=0`:** downsampling to 50 as specified; `limit=0` (unbounded snapshots) so the whole series
   is considered before the 50-point stride, matching the store's "≤0 = no limit" contract.

## Concerns

- My health compact/JSON surfacing tests live in `WorkspaceRenderTests` (that is where Health rendering is tested),
  which is **not** matched by the assigned filter's `~WorkspaceHealth` substring — so the assigned-scope run
  (94 passed) exercises the trend reader + the `WorkspaceHealth*` facts tests but not the two render tests. I ran
  `WorkspaceRenderTests` explicitly (47 passed) and the full fast suite (3004 passed) to cover them. No behavior
  gap; only a filter-substring mismatch.
- `history.db` grows append-only; `ReadTrend` reads all matching rows then downsamples to 50 in-memory. Fine for a
  workspace-local sidecar today; if snapshot volume ever explodes, add a snapshot-count cap via the `limit` arg.
