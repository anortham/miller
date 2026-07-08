# Task 4 — `miller metrics history` read verb + metrics-history-v1 contract

## What I implemented

`miller metrics history [--metric a,b,…] [--limit N] [--json] [--workspace-id SELECTOR] [--workspace DIR]`
— a read-only trend over the workspace `history.db` sidecar, following the existing metrics-operation
pattern (parse → reader → compact/JSON render).

- **`MetricsTool.RunHistory`** (new): reads `MetricHistoryStore.ReadTrend(historyDbPath, wanted, limit,
  maxPoints: 0)` (no downsampling — that stays a dashboard concern) and renders compact or the stable JSON
  envelope. Added `DefaultHistoryLimit = 20`, `HistorySchemaVersion = 1`, and `DefaultHistoryMetrics`
  (`symbol_count, complexity_p90, clone_group_count, marker_total, dead_code_candidate_count`, keyed off
  the canonical producer consts).
  - Compact: `# metric history` header, tab-separated column header, one line per snapshot oldest-first /
    newest-last; absent metric renders `-` (never `0`); integral values printed without a decimal tail.
  - JSON: `{ schema_version, workspace_id, metrics: [{ metric, points: [{ recorded_at_utc, artifact_id,
    revision, source, value }] }] }`. Series in requested order; a metric with no points is omitted
    (empty/missing history ⟹ `metrics: []`).
  - Empty history: compact exit-0 `no trend data yet — run \`miller report\`.`; JSON `workspace_id` +
    empty `metrics`.
- **`CliDispatch.Metrics`**: added `history` to the operation whitelist/usage and branched it out to a new
  `MetricsHistory` handler before the git-backed churn/risk recorder path. `MetricsHistory` resolves the
  read context, requires the index, parses `--metric` (comma-separated AND repeated via
  `ParseMetricFilter`/`CollectRepeatedOptionValues`) and `--limit`, resolves `workspace_id`
  (`ResolveWorkspaceId` — same bootstrap-id-else-canonical-root logic as `CaptureHeavyArmIdentity`), and
  renders. Updated `HelpText`.
- **`CliCapabilities`**: added `metrics history --json` to `json_commands` and a `metrics_history` v1 entry
  to `json_contracts` pointing at the new doc.
- **`docs/contracts/metrics-history-v1.md`** (new): modeled on `references-candidates-v1.md` — invocation/
  selectors, default metric-set table, exit codes, snapshot_id ordering rule, compact + JSON contracts,
  write-arm summary, `history.db` DDL, stability rules.

## Verification

- **Scope**: assigned worker scope — metrics + CLI dispatch fast tests.
- **Command**: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter
  "(FullyQualifiedName~MetricsTool|FullyQualifiedName~CliDispatch)&Category!=Scale"`
- **Result**: Passed — 161/161, 0 failed. Build 0 warnings / 0 errors (TreatWarningsAsErrors).
- **Worker ceiling**: `scripts/test.sh` — Passed 3027/3027 (fast suite, 22s wall).
- **Timestamp**: 2026-07-07.

## Files changed

- `src/Miller.Server/Tools/MetricsTool.cs` — `RunHistory` + compact/JSON renderers + consts.
- `src/Miller.Server/Cli/CliDispatch.cs` — `history` branch, `MetricsHistory`, `ParseMetricFilter`,
  `ResolveWorkspaceId`, HelpText.
- `src/Miller.Server/Cli/CliCapabilities.cs` — `json_commands` + `json_contracts` entries.
- `docs/contracts/metrics-history-v1.md` — new stable contract doc.
- `tests/Miller.Tests/Server/MetricsToolTests.cs` — 6 `RunHistory` unit tests + seed helper.
- `tests/Miller.Tests/Server/Cli/CliDispatchTests.cs` — 3 end-to-end tests + capabilities assertions.

## Acceptance criteria → tests

- Compact/JSON match doc; `--metric` filters; `--limit` bounds →
  `RunHistory_Compact_OneLinePerSnapshot_NewestLast_AbsentMetricDash`,
  `RunHistory_Json_EnvelopeFiltersMetrics_BoundsLimit`, `MetricsHistory_Compact_RendersTrendNewestLast`,
  `MetricsHistory_Json_EmitsStableEnvelope`.
- Ordering by `snapshot_id` with out-of-order timestamps → `RunHistory_OrdersBySnapshotId_NotByRecordedAt`.
- Empty history: friendly exit-0 compact; empty `metrics` array JSON →
  `RunHistory_EmptyHistory_Compact_FriendlyExitZeroMessage`,
  `RunHistory_EmptyHistory_Json_EmptyMetricsArrayWithWorkspaceId`,
  `MetricsHistory_EmptyHistory_ExitZeroFriendlyMessage`.
- `capabilities --json` lists the surface → augmented `Capabilities_Json_ReportsErosContractSurface`.
- Default set when `--metric` omitted → `RunHistory_OmittedMetricFilter_UsesDefaultSet`.

## Miller calls + confirmations

`ToolSearch` surfaced the Miller MCP tool schemas; because that index serves the main checkout (predates
this branch), branch-new files (`MetricHistoryStore.cs`, `MetricSnapshotAggregates.cs`, current
`MetricsTool.cs`) and branch-modified files (`CliDispatch.cs`, `CliCapabilities.cs`, both test files) were
Read directly as instructed. API shapes confirmed from source, not memory.

## API-shape evidence

- `MetricHistoryStore.ReadTrend(historyDbPath, IReadOnlyList<string> metrics, int limit, int maxPoints)` →
  `IReadOnlyList<MetricHistoryTrendPoint>`; sorted by `snapshot_id` then metric; `limit>0` selects the most
  recent N snapshots; `maxPoints<=0` = no downsampling (used here) — `MetricHistoryStore.cs` L293-365.
- `MetricHistoryTrendPoint(SnapshotId, RecordedAtUtc, ArtifactId, Revision, Source, Metric, Value)` — L42-49.
- `MetricSnapshotAggregates.HistoryDbPathFor(...)` + cheap-arm name consts — `MetricSnapshotAggregates.cs`.
- `MetricHistoryHeavyArm.DeadCodeCandidateCount` (file-local in `MetricsTool.cs`).
- CLI patterns confirmed against `ReferencesCandidates`, `TryResolveReadContext`, `RequireIndex`,
  `CollectRepeatedOptionValues`, `WriteOutput`, `Usage`.

## Self-review findings

- Order guarantee exercised with a genuinely out-of-order `recorded_at_utc` seed (later `snapshot_id`
  carries an earlier wall-clock) asserting insertion order — matches `ReadTrend`'s snapshot_id sort.
- Absent-vs-zero honored end to end: `-` compact, metric omitted JSON (`..._AbsentMetricDash`,
  `..._UsesDefaultSet`).
- `--metric` accepts comma-separated and repeated forms, de-duplicated first-seen; blank entries fall back
  to the default set.

## Judgment calls

- **`schema_version: 1` added to the JSON envelope** (beyond the plan's `{ workspace_id, metrics }`
  sketch): every sibling `json_contracts` entry carries an inline `schema_version`, and the doc is modeled
  on `references-candidates-v1` which does too. Strictly additive — the specified fields and exact nested
  field names are all present, so an Eros consumer is unaffected. Documented in the contract's stability
  rules. Flagging for visibility since the plan wrote the envelope without it.
- **Metric-name consts left in place** (`MetricSnapshotAggregates` cheap-arm; file-local
  `MetricHistoryHeavyArm` heavy-arm). The read verb references both cleanly; no promotion to a shared home
  was needed, so I avoided the churn.
- **`history` requires the symbols index** (`RequireIndex`) like every other read verb — consistent, and
  yields `workspace_id`/`historyDbPath`. A workspace with `history.db` but no `symbols.db` is an unlikely
  edge; kept conventional behavior.

## Concerns

None blocking. `metrics-json-v1.md` and boundary docs are unchanged (Task 7 scope). The dashboard's
read-time downsampling vs this CLI's no-downsampling behavior is intentional and documented.
