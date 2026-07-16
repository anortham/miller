# Task 6 — Telemetry panel polish (sortable columns, recent-errors alignment)

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes`
**Base:** 6d40280 (T5)

## Ledger

| Step | Result |
| --- | --- |
| Miller-first orientation | Done — model shapes from `inspect`, not guessed |
| Tests written first, watched red | Done — 2 of 3 new tests red on `x-data="telemetryTableSort"` / `recent-error-row` |
| Implementation | Done — razor + Alpine component + CSS |
| Focused verification | Green — 45/45 `~DashboardActivityFeed` |
| Full fast suite | Green — 3562/3562, 24s |
| Release build | Green — 0 warnings / 0 errors |
| `node --check` | Green |
| Plan checkboxes ticked | Done — Task 6 section only (lines 261-263) |

## Files changed

- `src/Miller.Dashboard/Components/TelemetryPanel.razor` — `x-data="telemetryTableSort"` on the section; sort buttons inside `<th>` for Tool/Calls/Avg/p95/Max/Errors/Est tokens with `aria-sort="none"` on the `<th>`; `.telemetry-row` + `@attributes="SortKeys(tool)"` on each `<tr>`; recent-errors cells get fixed grid classes and always render (`Cell()` em-dash placeholder).
- `src/Miller.Dashboard/wwwroot/js/alpine-components.js` — new `telemetryTableSort` component (+96 lines), CSP-safe.
- `src/Miller.Dashboard/wwwroot/dashboard.css` — recent-errors five-column grid; new "Telemetry sortable headers" section.
- `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` — 3 new tests + `CountOccurrences` helper.
- `docs/plans/2026-07-16-dashboard-ux-fixes.md` — Task 6 checkboxes.

## Miller calls (API-shape evidence)

| Call | Proves |
| --- | --- |
| `inspect(target='DashboardToolStat', depth='full')` | `DashboardData.cs:34` — `Tool string`, `Calls long`, `AvgMs double`, `P95Ms long`, `MaxMs long`, `ErrorCount long`, `SumEstTokens long`, `LastCallTs/LastOutcome/LastErrorTs/LastErrorKind string?`. Every `data-sort-*` key is rendered from these model values, never parsed from formatted text. |
| `inspect(target='DashboardRecentError', depth='full')` | `DashboardData.cs:47` — `Ts string`, `Tool string`, `Op string?`, `ErrorKind string?`, `DurationMs long`, then optional `Id`/`WorkspaceId`/`WorkspaceDisplayId`/`ErrorMessage`/`ErrorDetail`. `Op` and `ErrorKind` being nullable is exactly why the grid needed always-rendered cells. |

Worktree bytes read directly (changed since the Miller baseline by T1/T5): `TelemetryPanel.razor`, `alpine-components.js`, `WorkspaceIndex.razor`, `dashboard.css`.

## Judgment calls

1. **New `.telemetry-sort` CSS class rather than reusing `.ws-sort`** (`dashboard.css:1484-1529`). Task 5's caret rules key off `[role="columnheader"][aria-sort=...]`. A real `<th>` has an *implicit* columnheader role, and CSS attribute selectors only match explicit attributes — so `.ws-sort`'s carets would never light up inside a `<th>`. Extending Task 5's selector lists would mean editing its section, which Task 6 does not own. Cost: ~12 duplicated lines of button reset. Noted for the Task 10 visual sweep as a possible merge into one shared `.col-sort`.
2. **Sortable columns = the 7 named in the spec.** "Last call" and "Last error" got no sort button (not in scope, and both would need epoch keys like T5's `ActivityEpochSeconds`).
3. **`Cell()` em-dash placeholder for null `Op`/`ErrorKind`** (`TelemetryPanel.razor:169-172`). The old markup dropped the op `<span>` entirely when null — with a fixed 5-column grid that shifts kind/duration one column left. This is the actual root cause of the "wraps unpredictably" symptom: 5-6 children in a 4-column `150px 150px 1fr auto` grid.
4. **`data-sort-avg` formatted `0.###` invariant, not raw `double.ToString()`.** Blazor renders `@attributes` object values through `ToString()`, which is culture-sensitive; an invariant string keeps `parseFloat` correct under any test/host culture.
5. **`data-sort-*` on `<tr>`, not `<td>`** — the plan allowed either; `<tr>` matches T5's row-level convention and is what the sort comparator reads.

## Self-review

- **Contract inputs preserved:** `hx-ext="morph"`, `hx-swap="morph:outerHTML"`, `data-poll-trigger="every 30s"`, `hx-trigger="every 30s"`, and the Refresh button's morph swap all untouched. Verified by the existing green `WorkspacesShell_MountsMachineWideTelemetryPanel` / `WorkspaceShell_RendersVisibleTelemetryAndHtmxTargets`.
- **`telemetry-col-optional` survives:** guarded by a new test asserting exactly 10 occurrences (5 `<th>` + 5 `<td>`) — this one passed on first run by design, as a regression guard.
- **Issue-expander machinery untouched:** `data-issue-details`, `data-issue-id`, `data-copy-target`, `.copy-box` unchanged; `.recent-error-detail { grid-column: 1 / -1; }` already existed and still gives the expander its own full-width row.
- **Store pattern copied exactly:** module-level `window.__millerTelemetrySortState`, `init()` rehydrate, element-scoped `htmx:afterSwap` re-apply, `persist()` on mutation. No state parked on DOM nodes.
- **CSP-safe:** `x-on:click="onSort($event)"` only — same subset as `workspaceIndexFilter`. `Shells_IncludeDashboardBehaviorScripts`' `DoesNotContain("onclick=")` still green.
- **No new abstractions:** one Alpine component; no endpoint, model, or `DashboardData` change. Sort is client-side presentation only.
- **Unowned tests:** no edits needed. `DashboardRegistryReadTests` sort assertions are all `ws-*`-scoped and stayed green.

## Concerns

- **`.ws-sort` / `.telemetry-sort` duplication** — see judgment call 1. Deliberate, ownership-driven; the natural cleanup is Task 10.
- **Two independent sort stores** (`__millerWorkspaceIndexState`, `__millerTelemetrySortState`) — correct today (different panels, different columns), but a third sortable table should prompt a shared factory rather than a third store.
- **Sort is per-page-load, not persisted** across a browser reload (module store, not `localStorage`) — matches T5 behavior exactly; flagging only in case a later task wants durable preferences.
- **No plan mismatch found.** The plan's "sort keys on `<td>` or `<tr>`" ambiguity is the only open choice and is resolved above.
