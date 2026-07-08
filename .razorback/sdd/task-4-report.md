# Task 4 report — Workspace list slice (auto-refresh, ARIA, sort)

**Status: DONE**

## Worktree state
- Path: `/Users/murphy/source/miller/.worktrees/dashboard-polish`
- Branch: `feat/dashboard-polish`
- HEAD: `2fcc829` (Batch A merged)
- Dirty (all owned files, no others):
  - `src/Miller.Dashboard/Components/WorkspaceIndex.razor`
  - `src/Miller.Dashboard/wwwroot/dashboard.css`
  - `src/Miller.Dashboard/wwwroot/js/alpine-components.js`
  - `src/Miller.Dashboard/wwwroot/js/dashboard-site.js`
  - `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`
- Did NOT `git add`/`commit` (parallel-lead-commit mode). Did NOT touch `WorkspacesShell.razor` — no shell change needed.

## What I built

### ARIA (A1-a11y / A6)
- Dropped `role="table"` from both `.ws-index` grids (live + stale) and `role="row"` from the header div and every `<a class="ws-index-row">`. The list is now an honest set of links; the visual grid is carried entirely by CSS classes.
- Removed `aria-hidden="true"` from the header row (`WorkspaceIndex.razor:48`) because it now holds interactive sort buttons that must reach AT. The decorative rail span keeps its own `aria-hidden`.

### Sortable headers (client-side, progressive enhancement)
- Workspace/Files/Symbols/Rev headers are `<button type="button" class="ws-sort…" data-sort-col=… aria-sort="none" x-on:click="onSort($event)">` (`WorkspaceIndex.razor:50-55`). State/Languages stay plain spans (not sortable per plan).
- Each row emits clean sort keys via `@attributes="SortKeys(entry)"` → `data-sort-files/symbols/rev/name` (`WorkspaceIndex.razor:59,88` + `SortKeys` helper). Fact-less rows use `-1` (sort below real counts). JS sorts values, never formatted `"1,234"`/`"—"`.
- `workspaceIndexFilter` gained `onSort`/`applySort`/`reflectSortButtons`. Each `.ws-index` grid sorts independently; the header row is not a `.ws-index-row`, so re-appending sorted rows leaves the header in place. `aria-sort` set ascending/descending/none. Numeric default descending on first click, name ascending.
- Appended `.ws-sort` CSS at the END of `dashboard.css` (did NOT touch Task 3 token section): button-chrome reset, focus ring, `::after` caret driven by `aria-sort`.

### Auto-refresh (A6)
- `hx-get="/fragments/workspaces"` + `data-poll-trigger="every 30s"` + `hx-trigger="every 30s"` + `hx-swap="outerHTML"` on `<section id="workspace-index">` (`WorkspaceIndex.razor:1-9`), same pattern as `TelemetryPanel.razor:3-8`. The orphaned `/fragments/workspaces` route already returns a rendered `WorkspaceIndex` (the same section) — outerHTML swap replaces the section with itself. No endpoint change, no `hx-select`. Visibility gating is automatic: `applyVisibilityPolling()` already strips `hx-trigger` on hidden documents for every `[data-poll-trigger]`.

### State survives swaps
- The poll swaps the whole section, destroying the Alpine component's DOM + reactive state. Solved with a module-scope store `window.__millerWorkspaceIndexState` (owned/initialized in `dashboard-site.js`, mirroring `__millerOpenIssueDetails`). `init()` rehydrates query + sort column/dir + stale-open and re-applies filter/sort/aria; every mutation (`onInput`, `onSort`, stale `toggle` listener, `applyFilter`) writes back. Store lives outside the swapped DOM, so a swap can't clear it.
- Manual stale-section open state is now tracked (a `toggle` listener bound once via `data-stale-bound`), surviving the poll — not just the prior auto-open-while-filtering case.

### Registry error notice (Task 1 `Index.Error`)
- `<p class="notice error-notice"><strong>Registry unavailable</strong> @Index.Error</p>` after the panel heading when `Index.Error` is set (`WorkspaceIndex.razor:29-32`), reusing the exact `.notice.error-notice` styling other panels use.

## Judgment calls
- **No `WorkspacesShell.razor` change** — poll attrs belong on the `WorkspaceIndex` section (the node the fragment returns); the shell needed nothing.
- **`data-sort-*` keys on rows** rather than parsing cell text — robust numeric sort, no locale/`"—"` parsing.
- **CSP-safe `onSort($event)`** reading `data-sort-col` off `event.currentTarget`, mirroring `onInput($event)` — avoids string-literal args through the Alpine CSP parser.
- **Store name `__millerWorkspaceIndexState`** to match the existing `__millerOpenIssueDetails` convention.
- **Caret via CSS `::after` on `aria-sort`** so the visual indicator is a pure function of the ARIA state.

## Miller calls used
- Loaded `mcp__miller__*` schemas; `search mode=source` returned no hits (render tests are string-assertion based; located via grep on the known file), then Read the actual worktree files. Index reflects main @ 6207978; worktree files are source of truth — confirmed the worktree `WorkspaceIndex.razor` already carried the Batch A stale-split/filter/`ws-filter-empty` and preserved them.
- Read `DashboardEndpoints.cs:87-94` (not owned) → confirmed `/fragments/workspaces` returns `RazorComponentResult<WorkspaceIndex>` with `Index` param (section swapped as-is). Confirmed `TelemetryPanel.razor` poll attribute names and `dashboard-site.js` generic `[data-poll-trigger]` gating.

## API-shape evidence
- `DashboardWorkspaceIndex.Error` = `string? Error = null`, JSON `error` (`DashboardData.cs:443`) — Task 1 field, rendered.
- `DashboardWorkspaceIndexEntry(Workspace, Facts, RootExists)`; `HasFacts`, `Facts.FileCount/SymbolCount`, `Workspace.LastRevision (long?)`, `Workspace.DisplayId` — used for sort keys.
- `@attributes` Dictionary<string,object> renders ints invariantly → `data-sort-files="4"`, `data-sort-rev="42"` (asserted green).

## Tests added (`DashboardRegistryReadTests.cs`, TDD red→green verified)
- `WorkspaceIndex_DropsAriaTableAndRowRoles`
- `WorkspaceIndex_SortableHeadersAreButtonsWithAriaSort`
- `WorkspaceIndex_PollsWorkspacesFragmentVisibilityGated`
- `WorkspaceIndex_RendersRegistryErrorNotice`
- `WorkspaceIndex_OmitsErrorNoticeWhenErrorIsNull`
- Shared `SampleWorkspaceIndex(error)` helper. Existing `WorkspacesShell_RendersIndexListHooksAndLinks` (stale split, prune hint, `root missing`, filter hooks) still passes unchanged.

## Gate invariants + results
- **worker-red-green**: `dotnet test … --filter "Category!=Scale&FullyQualifiedName~WorkspaceIndex"` → 62 passed, 0 failed. Proves: Error notice renders (and is omitted when null), ARIA table/row roles gone, sort headers are buttons with aria-sort + row sort keys, poll attributes present, stale/prune/filter contract intact. Red phase confirmed 4 new tests failing against the old markup first.
- **worker-ceiling**: `scripts/test.sh` (fast suite) → 3096 passed, 0 failed, wall 18s (< 30s ceiling). Proves no regression.
- **Build**: `dotnet build Miller.slnx -c Release` → 0 warnings / 0 errors.

## What Task 7 must LIVE-verify (JS behavior — not unit-testable here)
1. Landing list issues `GET /fragments/workspaces` ~every 30s and swaps in place.
2. Polling stops when the tab is hidden and resumes on focus.
3. A swap does NOT clear: (a) filter input text, (b) active sort column + direction (rows stay ordered, caret/`aria-sort` persist), (c) a manually-opened stale `<details>` section.
4. Clicking a sort header reorders rows client-side with no server round-trip; re-click toggles asc/desc; numeric sorts by value (fact-less rows sink), Workspace sorts by name case-insensitively; live and stale grids sort independently; header row stays put.
5. `aria-sort` on the active header announces asc/desc to a screen reader; anchor rows read as a list of links (no phantom table).
6. With JS off the list renders in server default order (no sort/poll) and stays usable — acceptable per plan.
7. CSP: no console violations (Alpine CSP build, no inline expressions/scripts).
