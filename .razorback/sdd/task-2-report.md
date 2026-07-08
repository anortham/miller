# Task 2 — Leader converge arm + aggregates (Batch A)

## Status
COMPLETE. Assigned fast tests + Scale e2e green; worker-ceiling `scripts/test.sh` green (2995 passed).
Commit SHA: none - parallel-lead-commit.

## What I implemented
- **`src/Miller.Indexing/MetricSnapshotAggregates.cs`** (new): the cheap arm.
  - `ReadConvergeMetrics(string symbolsDbPath, IRegionSearchIndex? regionIndex)` → `IReadOnlyList<MetricHistoryPoint>`:
    `symbol_count`, `file_count`, `language_count` (always), `clone_group_count` (always; 0 is real),
    `complexity_p50/p90/max` (absent when no complexity rows), and `marker_total` **only when a region index is
    supplied** (per-marker breakdown in `detail_json`). One read-only connection, one bounded aggregate pass.
  - `RecordConverge(symbolsDbPath, workspaceId, revision, millerVersion, regionIndex=null, onError=null, recordedAtUtc=null)`
    → `MetricHistoryWriteResult?`: reads artifact identity + aggregates, builds a `source='converge'` snapshot, and
    calls `MetricHistoryStore.RecordConverge`. **Never throws, never blocks** (catch-all → `onError` + return null;
    the store is skip-on-busy). Returns null when nothing recorded (no revision/workspace/artifact-id, no metrics).
  - `HistoryDbPathFor(symbolsDbPath)` helper (sibling `history.db`).
- **`src/Miller.Server/Hosting/IndexerSidecarConverger.cs`**: added `RecordConvergeHistory(...)` called at the end of
  `Converge`, AFTER the content/search sidecar steps, independent of their success. Passes `regionIndex: null` and an
  `onError` that logs a warning via the existing `_logger`.
- **`src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`**: single `MetricSnapshotAggregates.RecordConverge`
  call at the end of `TryConvergeSidecar` (the one-shot refresh path). No logger on this service ⟹ silent swallow,
  matching its existing best-effort sidecar behaviour.
- `MillerServiceRegistration.cs` **not modified** — recording is a static call with no new dependency to wire.

## Verification
- Command 1 (assigned): `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --no-build --filter "(FullyQualifiedName~MetricSnapshotAggregates|FullyQualifiedName~IndexerSidecarConvergerHistory)&Category!=Scale"`
  → **13 passed, 0 failed**.
- Command 2 (Scale, real `.tools/julie-extract` 2.11.0 present): `... --filter "FullyQualifiedName~MetricHistoryConvergeScale"`
  → **1 passed, 0 failed**.
- Ceiling: `scripts/test.sh` → **2995 passed, 0 failed** (fast suite, 21s wall; includes `ScaleTraitConventionTests`).
- Build: `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors**.
- Timestamp: 2026-07-07 (local session).

## Files changed
- Created: `src/Miller.Indexing/MetricSnapshotAggregates.cs`
- Modified: `src/Miller.Server/Hosting/IndexerSidecarConverger.cs`, `src/Miller.Server/Workspaces/CrossWorkspaceRefreshService.cs`
- Tests: `tests/Miller.Tests/Indexing/MetricSnapshotAggregatesTests.cs`,
  `tests/Miller.Tests/Server/IndexerSidecarConvergerHistoryTests.cs`,
  `tests/Miller.Tests/Server/MetricHistoryConvergeScaleTests.cs`

## Miller calls + confirmations (API-shape evidence)
- `inspect IndexerSidecarConverger depth=full` → `Converge(string? symbolsDbPath, string workspaceRoot, string? workspaceId, long revision, bool fullRebuild)` returns void, swallows sidecar failures; confirmed the hook point and that recording must be independent of sidecar success.
- `inspect CrossWorkspaceRefreshService depth=full` → `private void TryConvergeSidecar(string symbolsDbPath, string workspaceRoot, string? workspaceId, long revision)`; runs under the workspace `SingleWriterLock`; confirmed the second hook site.
- `inspect CloneGroupReader depth=full` → clone grouping CTE: `body_hash IS NOT NULL AND body_hash != '' GROUP BY body_hash HAVING COUNT(*) >= 2`; reused the shape for `clone_group_count`.
- `inspect ComplexityRankingReader depth=full` → `complexity_metrics` real columns (`decision_count`, `max_nesting_depth`, …); chose `decision_count` as the percentile scalar.
- `inspect MillerVersion depth=full` → `public static string Current`; namespace `Miller.Server` (NOT `Miller.Indexing`), so the version is passed INTO `RecordConverge` as a parameter (layering).
- `inspect ExtractBinaryVersionReader depth=full` → `TryRead(SqliteConnection)`/`TryRead(string?)`, null-on-any-failure; reused for `extractor_version`.
- `inspect FreshnessReader depth=overview` + read → `ArtifactId()` = `SELECT value FROM artifact_metadata WHERE key='artifact_id'`; my `ReadMetaValue` mirrors it on a shared connection.
- `search WorkspaceIndexFactsReader` + read → `ReadSymbolCounts` SQL `SELECT COUNT(*), COUNT(DISTINCT path), COUNT(DISTINCT language) FROM symbols WHERE name IS NOT NULL` — reused verbatim for the three counts.
- `search ReadMarkerSection` + read `ReportTool`/`MarkerSearch` → marker set {TODO,FIXME,HACK,XXX}, comment kinds {comment,doc_comment}, region-dedup + word-boundary `ContainsMarker`; replicated (MarkerSearch is Server-internal, unreachable from Indexing).
- `search IRegionSearchIndex` / read `RegionSearchHit` → `Search(query, IReadOnlySet<string> kinds, int limit, bool excludeTests)` and hit fields `RawText/Snippet/RegionId`; used in the marker aggregate + the fake test double.
- Read `MetricHistoryStore.cs` / `MetricHistoryWriteLock.cs` (Task 1) → `RecordConverge` is INSERT-OR-IGNORE dedup on `(artifact_id, revision, source)`, TimeSpan.Zero skip-on-busy, reactive corruption rename-aside-and-retry-once; drove the dedup + lock-held + corrupt tests off these.
- Read `JulieDbFixture.cs` → contract-faithful builder seeds `artifact_metadata.artifact_id="artifact-<ws>"`, `binary_version`, `complexity_metrics`, and `extraction_revisions`; used for all fast fixtures.

## Self-review findings
- Only the six owned files changed (`git status --short` verified); no sibling-owned files touched.
- Absent-vs-zero rule honored: complexity omitted on empty facts (`ReadConvergeMetrics_OmitsComplexity_WhenNoComplexityRows`); marker omitted when region index null (`..._OmitsMarkerMetric_WhenNoRegionIndex`); clone/marker `0` recorded when the source IS available.
- Non-blocking/non-throwing verified through the hook: history-lock-held ⟹ skip, no throw, sidecar step still ran; corrupt file ⟹ recover + record, no throw.
- Same-revision dedup verified both directly (`SkippedDuplicate`) and through the converger (one snapshot).

## Judgment calls
- `MetricSnapshotAggregates.RecordConverge — millerVersion is a parameter` rather than calling `MillerVersion.Current`, because `MillerVersion` lives in `Miller.Server` and this class is in the lower `Miller.Indexing` layer. Both hook sites (under `Miller.Server.*`) pass `MillerVersion.Current`.
- `IndexerSidecarConverger.cs / CrossWorkspaceRefreshService.cs — both hooks pass regionIndex: null` (⟹ marker metrics absent on the converge arm). Plan-consistent ("passing null is plan-consistent; note it as a judgment call"): neither converge path opens a region search index, and building one under the ops gate adds I/O the design does not want. Markers are recorded instead by the heavy `miller report` arm (Task 3). `ReadConvergeMetrics` still fully supports a supplied index (tested with a fake).
- `MetricSnapshotAggregates — complexity percentiles over decision_count` (the cyclomatic-style scalar `ComplexityRankingReader` ranks by); no single composite "complexity score" exists in the schema. Type-7 (linear-interpolation) percentiles.
- `MetricSnapshotAggregates — marker vocabulary + word-boundary matcher duplicated from MarkerSearch` because `MarkerSearch` is `internal` to `Miller.Server.Tools` and cannot be referenced from `Miller.Indexing`. Kept minimal and commented "keep the two in step".
- `RecordConverge catches Exception broadly` (not a narrow set) — deliberate for a best-effort telemetry hook the design mandates must "never fail or delay indexing".

## Concerns
- **Marker metrics never recorded by the converge arm in production** (both hooks pass null). By design (heavy `report` arm records markers), but the automatic `converge` trend line will not carry `marker_total`. If product later wants markers on the converge cadence, the hook would open `FtsRegionSearchIndex` on `search.db` — a follow-up, not this task.
- `ReadIdentity` opens a second read-only connection separate from `ReadConvergeMetrics` (2 opens per converge). Negligible for best-effort telemetry; left un-folded to keep the pure metric reader free of identity concerns.
- The corrupt-file recovery path exercises `MetricHistoryStore.RenameAside` → `SqliteConnection.ClearAllPools()` (Task 1 code, process-global). No flakiness observed under the parallel full-suite run, matching Task 1's own corruption test.

---

## Fix round (lead inline review — wire region index into the converge hook)

**Finding addressed:** the marker path was dead in production (both hooks passed `regionIndex: null`). Now both
converge hooks open the region search index just built into `search.db` and pass it to `RecordConverge`, so
`marker_total` (+ per-marker `detail_json`) rides the CONVERGE cadence per the design's cheap-arm table.

**Miller call for this round:** `inspect FtsRegionSearchIndex depth=full` → confirmed `Open(string searchDbPath,
long expectedRevision)` **THROWS** on every unavailability (missing/stale `search.db`, schema mismatch, malformed
meta, region tables/columns absent when region search is disabled) and **never returns null**; the type is **not
`IDisposable`** (per-`Search` connections are `Pooling=false` and disposed). So every failure is caught → null and
no disposal is needed.

**Changes:**
1. `IndexerSidecarConverger`: `RecordConvergeHistory` now takes `searchDbPath` and calls a new `TryOpenRegionIndex`
   that opens `FtsRegionSearchIndex.Open(searchDbPath, revision)` when `_searchEnabled`, catching
   `IOException`/`InvalidOperationException`/`SqliteException`/`ArgumentException` → null (logged at Debug). The
   just-converged `search.db` is at the current `revision`, so `Open`'s revision guard matches; a failed/disabled
   search convergence degrades cleanly to no marker metric. Stale comment updated.
2. `CrossWorkspaceRefreshService.TryConvergeSidecar`: the search db path IS naturally in scope
   (`SymbolSearchSidecar.SearchDbPathFor(symbolsDbPath)`) and the enable flags are on `_sidecar`, so it wires the
   region index symmetrically — gated on `_sidecar.Enabled && _sidecar.RegionOptions.Enabled`, same catch→null.
   (No new parameter plumbed; no asymmetry.)
3. `IndexerSidecarConvergerHistoryTests`: added `Converge_WithRegionSearchDb_RecordsMarkerTotalAndBreakdown` — a
   pre-written region-bearing `search.db` (same shape `SearchIndexWriter` emits) at the converge revision ⟹ the
   snapshot carries `marker_total=1` and `detail_json={"TODO":1,"FIXME":0,"HACK":0,"XXX":0}`. The existing
   search-disabled test keeps the absent (not 0) assertion. `NewConverger` parameterized with `searchEnabled`.
4. Updated the now-stale "the converger holds none" comment.

**Fix-round verification (2026-07-07):**
- Assigned filter: `... --filter "(FullyQualifiedName~MetricSnapshotAggregates|FullyQualifiedName~IndexerSidecarConvergerHistory)&Category!=Scale"` → **14 passed, 0 failed** (was 13; +1 marker-wiring test).
- Scale e2e: `... --filter "FullyQualifiedName~MetricHistoryConvergeScale"` → **1 passed, 0 failed**.
- Worker ceiling: `scripts/test.sh` → **2996 passed, 0 failed** (21s wall).
- Build: `dotnet build Miller.slnx -c Release` → **0 warnings / 0 errors**.

**Note:** the marker vocabulary + `ContainsMarker` duplication in `MetricSnapshotAggregates` is now
production-reachable (as the lead noted), so the "keep the two in step" comment stands as the intended contract.
The Scale e2e still calls `RecordConverge` with `regionIndex: null` (aggregates-over-real-extract proof); the
region-index wiring is proven by the fast converger test above rather than rebuilding a real `search.db` in Scale.
