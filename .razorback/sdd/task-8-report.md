# Task 8: Trends time axis — Worker Report

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes`
**Baseline:** 7ba6f22 (T7)
**Commit:** see ledger below

## Ledger

| Step | Result |
| --- | --- |
| Miller orientation (record shape, construction sites, point record, reader loop, contract) | done |
| Failing tests written (reader bounds ×3, panel render ×3) | red — CS1739/CS1061, fields absent |
| `DashboardTrendSeries` additive fields + `HasRecordedWindow` | done |
| `DashboardIndexFactsReader.ReadTrends` populates bounds from plotted points | done |
| `WorkspaceTrendsPanel.razor` renders window line | done |
| `dashboard.css` `.sparkline-window` | done |
| Focused suite `(Category!=Scale)&(FullyQualifiedName~Trend)` | green — 23/23 |
| Fast suite `scripts/test.sh` | green — 3574 passed, 0 failed, 26s (< 30s ceiling) |
| `dotnet build Miller.slnx -c Release` | Build succeeded, 0 Warning(s), 0 Error(s) |
| Plan Task 8 checkboxes ticked | done |

## Files changed

| File | Change |
| --- | --- |
| `src/Miller.Dashboard/DashboardData.cs` | `DashboardTrendSeries` gains `FirstRecordedAtUtc`/`LatestRecordedAtUtc` (nullable, defaulted, JSON `first_recorded_at_utc`/`latest_recorded_at_utc`) + `[JsonIgnore] HasRecordedWindow` |
| `src/Miller.Dashboard/DashboardIndexFactsReader.cs` | grouping loop now keeps `MetricHistoryTrendPoint` rows (was `double`) so bounds come from the plotted endpoints |
| `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor` | `.sparkline-window` line under the scale, gated on `HasRecordedWindow`; `WindowLabel` helper |
| `src/Miller.Dashboard/wwwroot/dashboard.css` | `.sparkline-window` rule (muted, mono, 11px, opacity .8) beside `.sparkline-scale` |
| `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | `RecordSnapshotAt` overload (explicit `recordedAtUtc`); 3 reader tests |
| `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` | 3 panel render tests |
| `docs/plans/2026-07-16-dashboard-ux-fixes.md` | Task 8 checkboxes |

## Miller calls + API-shape evidence

No guessed shapes. Each below is a real call or `grep` against the worktree.

1. `inspect(target='DashboardTrendSeries', depth='full')` → `src/Miller.Dashboard/DashboardData.cs:231`; params
   `(Metric, Label, Points IReadOnlyList<double>, First double, Latest double)`; one child `HasTrend => Points.Count >= 2`.
2. `trace(target='DashboardTrendSeries', mode='refs')` → **14 refs, only 2 CONSTRUCTION sites**:
   `DashboardIndexFactsReader.cs:84` (`call`) and `DashboardActivityFeedTests.cs:758` (`call`). Every other ref is
   `type_usage`. ⟹ appending two defaulted params is safe; no construction site needed a change. Confirmed by the
   green build.
3. `grep -n "record MetricHistoryTrendPoint" src/ -r` → `src/Miller.Indexing/MetricHistoryStore.cs:42`; the timestamp
   property is **`RecordedAtUtc`** (string, non-null), positionally
   `(SnapshotId, RecordedAtUtc, ArtifactId, Revision, Source, Metric, Value)`.
4. `inspect(target='ReadTrend', depth='full')` → `MetricHistoryStore.cs:317`. Body evidence: the SQL orders
   `BY sm.metric, s.snapshot_id`; the returned rows are **downsampled before return**
   (`foreach group … result.AddRange(UniformStride(group.ToList(), maxPoints))`), then re-sorted by
   `(SnapshotId, Metric)`.
5. `grep "UniformStride" -A 20` (`MetricHistoryStore.cs:590`) → endpoints are preserved: `idx = round(i*(n-1)/(maxPoints-1))`
   gives `i=0 → 0` and `i=maxPoints-1 → n-1`.
6. `grep -n "AbsoluteShort" src/Miller.Dashboard/DashboardFormat.cs` → `:82`, `AbsoluteShort(string?)` →
   `"MMM d, HH:mm 'UTC'"`, raw fallback on unparseable, `string.Empty` on null/blank. In razor scope via
   `Components/_Imports.razor:5` (`@using static Miller.Dashboard.DashboardFormat`).
7. `grep -n "recorded_at_utc" docs/contracts/metrics-history-v1.md` → **`:63`** "Points are ordered by **`snapshot_id`**
   (the append order), never by `recorded_at_utc`", and `:109` "display metadata; not the sort axis".
8. `grep "RecordConverge" / "FormatTimestamp"` → `RecordConverge(path, snapshot, DateTime? recordedAtUtc = null)`
   (`:147`), stored via `FormatTimestamp` (`:582`) as `yyyy-MM-ddTHH:mm:ss.fffffffZ`. This is why the tests can pin
   exact timestamp strings.

## The downsampling CAUTION — resolved, not worked around

The brief warned the window must match the sparkline's actual first/last **plotted** points, given `TrendMaxPoints = 50`.

Evidence (#4 + #5) shows this is satisfied structurally rather than by extra logic: `ReadTrend` downsamples **before
returning**, so the rows the reader groups *are* the points the panel plots. Taking `metricPoints[0]` /
`metricPoints[^1]` therefore yields exactly the plotted endpoints by construction. And because `UniformStride` always
keeps index `0` and `n-1`, the plotted endpoints are also the true recorded range — the window is correct on both
readings, with no reconciliation code.

Locked in by `ReadTrends_BoundsMatchPlottedEndpointsWhenDownsampled` (51 snapshots → 50 points; asserts point count,
values, and both bounds).

## Self-review

- **Additive:** both new params defaulted `= null`; existing constructions compile untouched (build green, and the
  pre-existing `WorkspaceTrendsPanel_SparklineShowsMinMaxLatestLabels` still passes unmodified).
- **No re-sort:** the reader consumes the store's order verbatim. `ReadTrends_BoundsFollowSnapshotOrderNotRecordedAtOrder`
  writes deliberately out-of-order timestamps and asserts the bounds follow snapshot order — a regression guard against
  a future "helpful" sort.
- **Absent bounds render unchanged:** gate is `HasRecordedWindow` (both bounds non-blank). Two negative tests cover
  no-bounds and one-bound-only.
- **No new abstractions:** two record fields, one computed flag, one render line, one CSS rule.
- **Comments:** none added to tests; source comments state constraints (why the bounds match the plot; why the flag
  exists), not narration.

## Judgment calls

1. **`HasRecordedWindow` as a computed flag** (`DashboardData.cs`) rather than a null-check in the razor. Mirrors the
   existing `HasTrend`/`HasData` idiom in the same file, keeps the `.razor` declarative, and is `[JsonIgnore]`d so
   `snapshot.json` gains only the two data fields.
2. **Window renders inside the `HasTrend` branch.** A single-point series keeps its "No trend data yet" hint with no
   window — the window describes a sparkline, and there is no sparkline there.
3. **Clock-skewed windows render as-is** (possibly reading "later → earlier"). The contract calls `recorded_at_utc`
   writer-clock display metadata, and the plan's stated intent is that the window match the actual first/last plotted
   points. Hiding or reordering a skewed window would misdescribe the plot. Rare; honest when it happens.
4. **`RecordSnapshotAt` overload** instead of changing `RecordSnapshot`'s signature — `params` can't follow an optional
   param, and the existing helper has other callers in the file. Old helper delegates with `null`.
5. **51 snapshots, not 120, in the downsample test.** See concern #1.

## Concerns / notes for the lead

1. **Fast-suite budget is tight and I trimmed my own test to respect it (no action needed, but worth knowing).**
   My first draft of `ReadTrends_BoundsMatchPlottedEndpointsWhenDownsampled` wrote 120 snapshots; under full-suite
   parallel load that measured **2s**, tying it for *slowest test in the fast suite* — against the CLAUDE.md rule that
   the fast suite stays genuinely fast. Trimmed to 51 snapshots (the minimum that triggers 50-point downsampling):
   **413ms**, same coverage.
2. **The `scripts/test.sh` tripwire is cold-build sensitive — not a real breach, but it will bite the next worker.**
   My first `scripts/test.sh` run reported **53s** and `ERROR: fast suite took 53s (> 30s ceiling)`. The script starts
   its timer *before* `dotnet test` (`scripts/test.sh:44-46`), so the timed window includes compiling 6 projects; that
   run was also contending for CPU with a background job of mine. Re-run warm: **26s, passing, 3574/0**. My 6 tests add
   ~0.5s total, so they cannot explain a 23s delta. Flagging because the warm number (26s) still sits close to the 30s
   ceiling against a <10s local target — a pre-existing condition on `main`'s trajectory, outside Task 8's ownership,
   and a plausible false-alarm source for Tasks 9–10.
3. **Attempted baseline measurement was inconclusive and is not evidence.** I stood up a throwaway worktree at 7ba6f22
   in `/tmp` to get a clean baseline; it reported only 2011 tests (vs 3574) with 1 failure — almost certainly missing
   repo setup (e.g. unrestored `.tools/julie-extract`) rather than a real T7 regression. I did **not** chase it and did
   **not** treat it as a baseline; the warm re-run in-tree (concern #2) is the decisive evidence. The `/tmp` worktree was
   removed (`git worktree remove --force`). No other worktree was touched.
4. **No contract doc needed updating.** `docs/contracts/metrics-history-v1.md` governs the `miller metrics history` CLI
   JSON, not the dashboard `snapshot.json`; no contract doc pins the dashboard trend-series shape. The new fields are
   additive to `snapshot.json` as the plan allows.
5. **No plan mismatch.** The contract's `snapshot_id` ordering rule held exactly as the plan described.
