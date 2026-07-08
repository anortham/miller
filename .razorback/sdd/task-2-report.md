# Task 2 report — Formatting foundations (relative time + bytes)

**Status:** DONE

## Worktree state
- Path: `/Users/murphy/source/miller/.worktrees/dashboard-polish`
- Branch: `feat/dashboard-polish`
- Base commit: `6207978` (main)
- Dirty at handoff (my owned files, uncommitted per parallel-lead-commit mode):
  - `M src/Miller.Dashboard/DashboardFormat.cs`
  - `M src/Miller.Dashboard/Components/TelemetryPanel.razor`
  - `M src/Miller.Dashboard/Components/ActivityFeedPanel.razor`
  - `M tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`
  - `?? tests/Miller.Tests/Server/DashboardFormatTests.cs` (created)
- **Not mine — present from parallel workers, left untouched:** `M src/Miller.Dashboard/wwwroot/dashboard.css`, `M tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`, `?? docs/plans/2026-07-08-dashboard-polish.md`.

## What I built
1. **`DashboardFormat.RelativeTime(DateTimeOffset value, DateTimeOffset now)`** — the load-bearing pure contract Task 6 consumes. Buckets mirror `dashboard-site.js:94-106` (`updateRelativeTimes`) exactly: `<5s`→`just now`, `<60s`→`Ns ago`, `<3600s`→`Nm ago`, `<86400s`→`Nh ago`, else `Nd ago`. Future timestamps clamp to `0` seconds (matches JS `Math.max(0, …)`). Integer division reproduces JS `Math.floor` for the (always non-negative) second count.
2. **`DashboardFormat.RelativeTime(string? value, DateTimeOffset now)`** — string overload for the ISO ("O") timestamps stored on the entries. Parses with `RoundtripKind | AssumeUniversal`; unparseable → returns the raw value (never throws); null/empty → empty string.
3. **`DashboardFormat.AbsoluteShort(string? value)`** — short absolute UTC form (`"MMM d, HH:mm 'UTC'"`, e.g. `"Jun 12, 10:00 UTC"`) used for the telemetry window bounds; same parse + raw-fallback contract.
4. **`FormatBytes` GB tier** — added a fourth tier mirroring the existing `"0.0"` + suffix style, so multi-GB reads `"3.0 GB"` instead of `"3000.0 MB"`. B/KB/MB tiers unchanged.
5. **ActivityFeedPanel.razor** — `<time>` inner text now `@RelativeTime(entry.Ts, Now)`; `class="rel-ts timestamp"`, `datetime`, and `data-ts` attributes kept byte-for-byte. Added one `private readonly DateTimeOffset Now = DateTimeOffset.UtcNow;` so all rows humanize against one instant.
6. **TelemetryPanel.razor** — three `<time>` elements (last-call, last-error, recent-error) humanized the same way with the same `Now` field; window label switched from raw `$"from {WindowStartTs} to {WindowEndTs}"` to `$"from {AbsoluteShort(start)} to {AbsoluteShort(end)}"`.

## Judgment calls
- **Window-label format (`TelemetryPanel.razor:150-153`):** chose the short absolute UTC form (`AbsoluteShort`) over a relative label. The brief explicitly permits this when relative "reads oddly" — a fixed reporting window rendered as `"from 26d ago to 26d ago"` is worse than `"from Jun 12, 10:00 UTC to Jun 12, 10:01 UTC"`. Tested with an explicit render assertion.
- **`AbsoluteShort` is a new public helper** (not requested by exact name, but within my owned `DashboardFormat.cs`). Kept it public + unit-tested so the window-label formatting is verifiable in `DashboardFormatTests`, not only via the razor render test.
- **String overload + raw fallback:** the components hold timestamps as `string?`, so the pure `DateTimeOffset` contract needed a string sibling. The pure overload is the load-bearing one Task 6 will call; the string one is the razor adapter.
- **Single `Now` per render:** used a `readonly` field initialized at component construction (fresh instance per `RenderComponentAsync`/fragment render) rather than inlining `DateTimeOffset.UtcNow` per element, so rows in one render are internally consistent.
- Did **not** touch `dashboard-site.js` (Task 4/6), `WorkspaceDetailPanel.razor` (Task 6), or `DashboardData.cs` (Tasks 1/5). `FormatBytes` GB tier changes rendered strings in `ContextSavingsPanel.razor`/`WorkspaceDetailPanel.razor` only for multi-GB values — I did not edit those files; behavior for existing sub-GB values is unchanged.

## Miller calls used
- `inspect DashboardFormat depth=overview` — confirmed the class layout, `FormatBytes` at :21, `@using static` import wiring via `_Imports.razor`.
- `trace FormatBytes mode=refs scope=…DashboardFormat.cs` — 18 refs; the dashboard callers are `ContextSavingsPanel.razor` and `WorkspaceDetailPanel.razor` (not mine). Confirmed no caller depends on the MB cap.
- `impact FormatBytes` — flagged ambiguity across 3 `FormatBytes` defs (dashboard / WorkspaceRender / spike); confirmed my target is the dashboard one.
- Cross-checked with `grep`: the only test asserting a byte string is `DashboardRegistryReadTests.cs:1095` → `"14.5 KB"` (KB tier), which the GB addition does not affect. No test pins a multi-GB MB-string.

## API-shape evidence
- Load-bearing signature present exactly: `public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)` in `DashboardFormat.cs`.
- `data-ts` + `rel-ts` contract preserved: render tests assert `data-ts="2026-06-12T10:00:00.000Z"` and `rel-ts` still present while `>…ISO…</time>` inner text is gone (`DoesNotContain(">2026-06-12T10:00:00.000Z</time>")` + `Contains(" ago</time>")`).

## Gate invariants + results
- **worker-red-green** — `dotnet test … --filter "Category!=Scale&(FullyQualifiedName~DashboardFormatTests|FullyQualifiedName~DashboardActivityFeedTests)"` → **Passed 48/48**. Proves: RelativeTime buckets (seconds/minutes/hours/days + future-clamp), GB tier + KB/MB unchanged, unparseable/null fallback; humanized text renders inside `time.rel-ts` with `data-ts` intact; telemetry window label carries `"Jun 12, 10:00 UTC"` and no raw `from …ISO… to …ISO…` string. (RED first: compile-failed on missing `RelativeTime`/`AbsoluteShort` before implementation.)
- **worker-ceiling** — `scripts/test.sh` → **Passed 3082/3082**, wall 19s. Proves no regression across the fast suite; the run compiled `-c Release` under `TreatWarningsAsErrors`, so 0 warnings / 0 errors is confirmed.

## Concerns
- None blocking. `dashboard.css` and `DashboardRegistryReadTests.cs` are dirty from parallel workers sharing this worktree — the lead should reconcile those against their owning tasks before committing; they are not part of Task 2.
