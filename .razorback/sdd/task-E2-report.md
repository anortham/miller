# Task E2 report — `near_duplicate_group_count` history metric + dashboard + report rollup

**Status:** COMPLETE. `commit SHA: none - parallel-lead-commit`

## Worktree state

- Path: `/Users/murphy/source/miller/.claude/worktrees/semantic-integration`
- Branch: `worktree-semantic-p2` @ `189b96d` (ahead 13 of origin), dirty with parallel-worker edits
  (impl-b2 `src/Miller.Indexing/Semantic/*`, impl-d2 `EditTool`/`EditService`/`TextReplaceMatcher` — untouched)
- No `git add` / `git commit` / push performed.

## The two decisions the brief asked me to make

### 1. Heavy arm, not converge — confirmed

`near_duplicate_group_count` records **only when a command actually runs the Type-2 arm**:
`miller metrics clones --near-duplicates` (new `source='clones'`) and `miller report --near-duplicates`
(existing `source='report'`). The converge arm is untouched.

Confirmed against the code, not assumed: `MetricSnapshotAggregates` (cheap arm) reads only `symbols.db`
aggregates — no disk body reads — while the Type-2 scan costs one hash-verified body read per candidate.
Putting it on converge would put ~2000 disk reads on the leader's per-revision hot path, which is exactly
E1's concern #1. `RecordHeavyArmSnapshot` (`CliDispatch.cs:1103`) already carries identity capture, the
mid-command identity re-check, and best-effort failure semantics, so the metric rides existing machinery.

**A clones run is always canonical.** Unlike churn/risk, no `metrics clones` flag can move the recorded
value: the count is computed from a fixed-bound scan *before* any display limit (see below). Encoding a
false "canonical = default params" gate would have thrown away comparable data points for no benefit.

### 2. Truncation — surfaced, and it suppresses the metric

E1's concern #2 was that the 2000-candidate cap is silent. Two changes:

- **Visible.** Compact output (both `metrics clones` and `report`) gains
  `near-duplicate scan truncated at 2000 candidate symbols — the group count is a floor and is not recorded.`
  `metrics clones --json` gains a `near_duplicate_scan` object (`candidate_cap`, `candidates_truncated`,
  `group_count`); `report --json` gains `clones.near_duplicate_truncated`. All present **only when the arm
  ran**, so flag-off output stays byte-identical.
- **Never recorded.** A truncated scan writes **no** history point. The contract's rule is "exact or absent,
  never misleading", and a capped scan's count is a floor over an arbitrary path-ordered prefix — recording
  it would poison the series into a sawtooth exactly the way `clone_group_count`-from-report would. A
  *complete* scan that found nothing records `0` (absent-vs-zero holds in both directions).

## Implementation

**Making the count comparable across producers.** The scan now runs the analyzer with `MaxGroups =
int.MaxValue` and takes the first `renderLimit` groups for display. `NearDuplicateAnalyzer.AssembleGroups`
applies `MaxGroups` *after* its total ordering, so `Take(limit)` of an uncapped run is byte-identical to a
capped run — rendering is unchanged, while `GroupCount` becomes the exact number of groups found,
insensitive to `--limit` and to the report's `SectionLimit`. That is why `near_duplicate_group_count` is
recordable from `report` even though `clone_group_count` is not.

| File | Change |
|---|---|
| `MetricsTool.cs` | `MetricHistoryHeavyArm`: `ClonesSource`, `NearDuplicateGroupCount`, `NearDuplicateScanDetail`. `ReadNearDuplicateGroups` → internal `ScanNearDuplicates` returning `NearDuplicateScan(Groups, GroupCount, CandidatesTruncated, CandidateCap)`. `NearDuplicateSnapshotMetrics(scan)` (the exact-or-absent gate). `NearDuplicateTruncationNote(scan)`. Renderers take the scan; JSON gains `near_duplicate_scan`. |
| `CliDispatch.cs` | `metrics clones --near-duplicates` is recordable under `ClonesSource`; `report` gains `--near-duplicates` (usage + parse + passthrough). |
| `ReportTool.cs` | `Run(..., nearDuplicates, nearDuplicateCandidateCap)`; `ReportFacts.NearDuplicates`; compact `near-duplicate groups: N` line; JSON `clones.near_duplicate_groups` / `near_duplicate_truncated`; `BuildSnapshotMetrics` appends the point. |
| `DashboardIndexFactsReader.cs` | one `TrendMetrics` entry — `("near_duplicate_group_count", "Near-duplicate groups")`. |
| `metrics-history-v1.md` | additive: new source `clones`, the metric in the requestable set, and a comparability/truncation paragraph. |

**Dashboard rode the existing mechanism as designed.** One line. Count-level sparkline only — no
per-symbol detail, no live compute on render (ADR-0002, and rendering must not pay the disk-read cost).

## Judgment calls

1. **Dashboard file.** The brief named `DashboardData.cs:982` (`ReadLocalMetricsPanel`). The trend
   mechanism actually lives in `DashboardIndexFactsReader.ReadTrends`; `ReadLocalMetricsPanel` is a live
   per-render clone read. I changed the trend reader instead. Adding a live near-duplicate compute to the
   panel would put 2000 hash-verified body reads on every dashboard render — the exact cost the whole
   design avoids. `DashboardData.cs` is unmodified.
2. **`report --near-duplicates` rather than always-on.** "Report rollup includes the count" needed a way
   for the report to *have* a count. Making it unconditional would put the disk-read cost on every
   `miller report`. Opt-in keeps the rollup byte-identical when absent and mirrors the `metrics clones`
   posture. The report's `canonical` gate is unchanged: the flag does not alter range/limit/test-filter and
   the metric is flag-insensitive, so a `--near-duplicates` report still records its normal metrics too.
3. **Injectable candidate cap (`nearDuplicateCandidateCap`, internal, defaulted).** My first truncation
   test built a 2001-row fixture; `JulieDbFixture` writes one file to disk + one un-batched INSERT per row,
   costing **~10s per test** — it would have blown the fast-suite budget. The cap is now a defaulted
   internal parameter and the tests pass `candidateCap: 2`. Both `Run` methods are `internal`, so no public
   surface widened. Writing that test also caught a **real bug**: the rendered `candidate_cap` and the
   `detail_json` bound were reading the compile-time constant rather than the cap actually used — the scan
   now carries its own `CandidateCap`.
4. **Shared fixture in a new file** (`tests/Miller.Tests/Server/NearDuplicateFixtures.cs`) rather than
   editing `JulieDbFixture.cs`, so `metrics clones` and `report` tests exercise identical bodies (and
   therefore prove the identical recorded value) without touching a file other workers may be in.

## Verification

| Scope | Invariant proved | Command | Result |
|---|---|---|---|
| worker red→green | The metric records only from a complete scan, under the right source, at a value no display limit can move; a truncated scan is absent and says so; the dashboard plots it count-only | `dotnet test … --filter "FullyQualifiedName~MetricsTool\|FullyQualifiedName~ReportTool\|FullyQualifiedName~DashboardRegistryRead\|FullyQualifiedName~MetricSnapshotAggregates\|FullyQualifiedName~RiskMetricsTool\|FullyQualifiedName~CliDispatchTests.Report"` | **126 passed, 0 failed** |
| worker (Type-2 surface) | Flag-off output byte-stability + all near-duplicate behaviour | `dotnet test … --filter "FullyQualifiedName~NearDuplicate"` | **29 passed, 0 failed, 2s** |
| ceiling | Nothing else in the repo regressed and the fast suite stayed fast | `scripts/test.sh` | **3842 passed, 0 failed, 2 skipped — 23s run, 27s wall (ceiling 30s)** |
| ceiling | 0 warnings / 0 errors under `TreatWarningsAsErrors` | `dotnet build Miller.slnx -c Release` | **Build succeeded, 0 warnings** |

The initial red state was a compile failure (tests referenced `near_duplicate_group_count` surfacing,
`ReportTool.Run(nearDuplicates:)`, and the trend row before any existed). Timestamp 2026-07-20; Scale suite
not run (nothing here spawns `julie-extract`).

**Shared-worktree note.** Several gate runs were blocked or reddened by impl-b2 (`VectorSidecar.cs`,
`Semantic/*`) and impl-d2 (`EditService.cs`, `EditToolTests.cs`) mid-edit states, plus one flaky
`IndexerServiceScanTests` leader-lock timeout under parallel load. Each was waited out and re-run, never
worked around; the numbers above are from runs where the tree compiled clean. Wall-clock readings above the
30s tripwire correlated with concurrent worker builds, not with this change (suite duration held at ~23-28s,
matching E1's 28s baseline).

### Tests added

- `MetricsToolTests` (5): flag-on surfaces the point; count is exact, not bounded by the render limit; a
  complete empty scan records `0`; a truncated scan suppresses the point and renders the note with the
  actual cap; an untruncated scan reports its bounds with no note.
- `MetricsToolTests` (1): `RecordHeavyArmSnapshot` writes `near_duplicate_group_count` under `source='clones'`.
- `ReportToolTests` (3): flag-off omits it from rollup and snapshot; flag-on renders `near-duplicate groups: 2`,
  the JSON fields, and records `2.0`; truncated suppresses the metric but still says what it saw.
- `CliDispatchTests` (2): `report --near-duplicates` accepted and additive; without the flag the clones
  section is unchanged.
- `DashboardRegistryReadTests` (1): the series is plotted after the dead-code row with the right label.

## Miller calls and what they confirmed

| Call | Confirmed |
|---|---|
| `context "metrics history snapshot writer RecordHeavyArmSnapshot record metric names history.db"` | Seeds: `MetricHistoryHeavyArm:21`, `RecordHeavyArmSnapshot:1103`, `ChurnSnapshotMetrics:352`, `ReportTool.BuildSnapshotMetrics:77`, `DashboardIndexFactsReader.ReadTrends` — the whole write/read path in one call |
| `inspect MetricHistoryHeavyArm depth=full` | The source/metric-name vocabulary is 4 sources + 5 metric names + `RangeLimitDetail`; 15 dependents — so a new source/name const belongs here, not inline |
| `inspect RecordHeavyArmSnapshot depth=full` | Signature `(ctx, captured, source, metrics, canonical, recordedAtUtc, warn)`; skips on non-canonical / empty metrics / no identity; re-checks identity inside the append txn; swallows failures to a stderr warning |
| `inspect RunClones depth=full` | Post-E1 shape: 7 params, `SnapshotMetrics: null` — the exact seam I had to fill |
| `inspect ChurnSnapshotMetrics depth=full` | The one-point `[new MetricHistoryPoint(name, value, detail)]` shape my near-duplicate producer mirrors |

## API-shape evidence

- `MetricHistoryPoint(string Metric, double Value, string? DetailJson)` — the shape `ChurnSnapshotMetrics`
  (`MetricsTool.cs:352`) returns; reused verbatim.
- `MetricsToolResult(string Output, int ResultCount, IReadOnlyList<MetricHistoryPoint>? SnapshotMetrics)` —
  `MetricsTool.cs`; the third field is what `CliDispatch.Metrics:676` hands to the recorder.
- `RecordHeavyArmSnapshot(WorkspaceContext, HeavyArmIdentity?, string source, IReadOnlyList<MetricHistoryPoint>, bool canonical, TextWriter)`
  — `CliDispatch.cs:1103`.
- `TrendMetrics` is `(string Metric, string Label)[]` with `MetricHistoryStore.ReadTrend(path, names, limit, maxPoints)`
  — `DashboardIndexFactsReader.cs:18/55`; a metric with no points yields no row (absent, not zero).
- `NearDuplicateAnalyzer.AssembleGroups` truncates to `maxGroups` **after** its total sort — read directly;
  this is what makes uncapped-analyze + `Take(limit)` byte-identical to capped-analyze.

## Concerns for the lead

- **`docs/contracts/metrics-json-v1.md` needs a Clones-section update** covering `kind`/`similarity` (E1's
  open item) *and* my new top-level `near_duplicate_scan` object. Outside my file ownership; not edited.
- **`docs/contracts/cli-eros-v1.md`** documents `report --json` as a stable Eros surface; the additive
  `clones.near_duplicate_groups` / `near_duplicate_truncated` fields should be noted there. Not edited.
- **`MILLER_AGENT_INSTRUCTIONS.md` / tool descriptions untouched** — this is CLI-only, no MCP tool or
  parameter added, `AgentInstructionsTests` green.
- **The 2000 cap is now visible but still a cap.** On a repo with more than 2000 eligible symbols the
  metric is permanently absent (by design — a floor is worse than nothing). If a large repo should get a
  trend, the right fix is a background arm that scans exhaustively, not a bigger CLI cap.
- E1's recommended `Miller.Indexing/NearDuplicateCandidateReader` extraction still stands; the candidate
  SQL remains in `MetricsTool.cs`. I did not take it on (outside ownership, and the seam I added —
  `ScanNearDuplicates` — is the natural thing to move when someone does).

## Self-review

- Byte-stability holds: with `--near-duplicates` absent, `metrics clones` and `miller report` produce
  identical output to HEAD and record identically (test-proven on both).
- Append-only respected: `near_duplicate_group_count` and `clones` are new names; nothing renamed, nothing
  backfilled.
- ADR-0002 respected: the dashboard gained one count series and no per-symbol data; it opens only
  `history.db`.
- No new MCP tool, no MCP parameter, no `ServerInstructions` growth.
- The recording decision lives in one place (`NearDuplicateSnapshotMetrics`) shared by both producers, so
  the two arms cannot drift into recording different values under one name.
- Comment discipline: doc comments on the new public/internal shapes and on the two non-obvious
  invariants (why analyze-unbounded-render-bounded is safe; why truncation suppresses). No narration.
- No unrelated worker files touched; nothing committed or pushed.
