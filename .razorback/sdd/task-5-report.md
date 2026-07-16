# Task 5 — Workspace list UX

Status: COMPLETE
Worktree: `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
Branch: `worktree-dashboard-ux-fixes` (from `c09b7f3`)

## Ledger

| Step | Outcome |
|---|---|
| Miller orientation (ReadIndex, entry record, telemetry schema, call sites) | done |
| Tests written first, watched red | done (CS1729/CS1501/CS1061 — no `LastActivityTs`, no 2-arg `ReadIndex`) |
| `DashboardData`: `LastActivityTs` + grouped telemetry read + degrade | done |
| `DashboardEndpoints`: 3 call sites threaded with telemetry path | done |
| `WorkspaceIndex.razor`: table roles, Last used, right-rail remove, no-facts, stretched link | done |
| `alpine-components.js`: aria-sort → columnheader; `activity` column | done |
| `dashboard-site.js`: `/` focuses the filter | done |
| `dashboard.css`: grid, stretched link, right rail, idle carets | done |
| Focused tests | 100/100 pass |
| Fast suite (`scripts/test.sh`) | 3559/3559 pass, 22s |
| `dotnet build Miller.slnx -c Release` | 0 warnings / 0 errors |

## Files changed

- `src/Miller.Dashboard/DashboardData.cs` — `DashboardWorkspaceIndexEntry.LastActivityTs` (nullable, defaulted, `last_activity_ts` in JSON); `ReadIndex(registryDbPath, telemetryDbPath = null)`; private `ReadLastActivityByWorkspace`; `RenderIndexJson` gains the optional path.
- `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs` — `/`, `/fragments/workspaces`, `/index.json` pass `paths.TelemetryDbPath`.
- `src/Miller.Dashboard/Components/WorkspaceIndex.razor` — table/row/columnheader/cell roles; Last used column; remove control relocated to a right-rail cell; no-facts + empty-index notes; inline `text-decoration` style dropped; `Now` field.
- `src/Miller.Dashboard/wwwroot/js/alpine-components.js` — `reflectSortButtons` writes aria-sort to `btn.closest('[role="columnheader"]')`; `activity` documented in the sort-column set.
- `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` — `/` keydown focuses+selects `#workspace-filter`.
- `src/Miller.Dashboard/wwwroot/dashboard.css` — 9-column grid, stretched link, right-rail remove, idle two-way caret, fact notes, responsive.
- `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` — 4 new `ReadIndex` last-activity tests; roles guard rewritten; cell-count invariant; aria-sort placement.
- `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` — 5 new rendered-list tests.

## Miller calls (orientation evidence)

| Call | Proved |
|---|---|
| `inspect(target='ReadIndex', depth='full')` | signature `ReadIndex(string registryDbPath)` at `DashboardData.cs:459`, full body, callees |
| `trace(target='ReadIndex', mode='refs')` | **6** references — `DashboardData.cs:507` (`RenderIndexJson`), `DashboardEndpoints.cs:44,130`, `DashboardFragmentCachingTests.cs:237`, `DashboardRegistryReadTests.cs:1530,1733`. The brief named 2 call sites; trace found a third production one (`RenderIndexJson`). |
| `inspect(target='DashboardWorkspaceIndexEntry', depth='full')` | positional record `(Workspace, Facts, RootExists)` + `HasFacts`/`IsStale` computed; 20 dependents |
| `search(query='tool_calls', mode='source')` | **no such table** — disproved the brief's "telemetry calls table" wording |
| `grep` of `DashboardData.cs` telemetry readers → `TelemetryLedger.cs:19` DDL | real table is `tool_telemetry` |

## API-shape evidence (no guessed shapes)

- **Telemetry schema** — `src/Miller.Server/Telemetry/TelemetryLedger.cs:19-21`:
  `CREATE TABLE IF NOT EXISTS tool_telemetry (id TEXT PRIMARY KEY, ts TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), tool TEXT NOT NULL, op TEXT, workspace_id TEXT, ...)`.
  Table `tool_telemetry`, columns `ts` (TEXT) and `workspace_id` (TEXT). Confirmed in use by `DashboardData.cs:583,650,786,1340` and `ReadTotals` (`MIN(ts), MAX(ts) FROM tool_telemetry`).
- **`ts` is fixed-width ISO-8601 UTC** (`%Y-%m-%dT%H:%M:%fZ`) ⇒ lexicographic `MAX(ts)` is chronological. Noted in the reader's doc comment.
- **Degrade discipline** — copied from `ReadTelemetrySummary` (`DashboardData.cs:591`):
  `catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException)`, plus the existing `OpenReadOnly` + `TableExists` guards.
- **`rel-ts` pattern** — `ActivityFeedPanel.razor:33` / `TelemetryPanel.razor:70`:
  `<time class="rel-ts timestamp" datetime="@ts" data-ts="@ts">@RelativeTime(ts, Now)</time>` with `private readonly DateTimeOffset Now = DateTimeOffset.UtcNow;`. Mirrored exactly.
- **Epoch key** — `2026-06-12T10:00:00.000Z` → `1781258400`, verified independently before use.

## Judgment calls

1. **Rewrote `WorkspaceIndex_DropsAriaTableAndRowRoles` → three well-formedness tests. FLAGGED.**
   The existing guard asserted `DoesNotContain("role=\"table\"")` / `role="row"` — a direct contradiction of Task 5's acceptance criteria. `git log -S` traced it to `4c28d90`, where rows were `<a class="ws-index-row">`: an anchor hosting `role="row"` with cell children *is* malformed ARIA, which is what the guard's own comment says ("must not carry **malformed** table/row ARIA roles"). Rows are `<div>`s now, so complete table roles are well-formed and the guard's intent is preserved, not weakened. I own the file; the replacement is stronger than a deletion:
   - `WorkspaceIndex_TableRolesAreWellFormed` — roles present **and** `DoesNotContain("<a class=\"ws-index-row\"")` keeps the original anchor-row prohibition alive.
   - `WorkspaceIndex_EveryRowHasSameCellCountAsHeaderColumns` — 8 columnheaders × 2 rows == 16 cells; catches the incomplete-roles failure the original guard was really defending against.
   - `WorkspaceIndex_AriaSortLivesOnColumnHeadersNotButtons`.
2. **Third `ReadIndex` call site.** The brief named two (`DashboardEndpoints.cs:44,130`); trace found `RenderIndexJson` → `/index.json`. Threaded it too — the plan explicitly allows the additive `index.json` field, and leaving it would make the JSON feed disagree with the page.
3. **Optional `telemetryDbPath` parameter** rather than required: keeps `RenderIndexJson` and the 3 unowned test call sites (`DashboardFragmentCachingTests.cs:237`, `DashboardRegistryReadTests.cs:1530,1733`) compiling untouched. Null path ⇒ null timestamps, same as a missing DB.
4. **`workspace-path` raised above the stretched link** (`z-index: 1`). The plan offered this as a choice ("if it should stay selectable"). Raised it: the path's `title` tooltip exists precisely because the path is ellipsis-truncated, and the overlay would swallow both the tooltip and text selection. Cost: the second line is not a click target; the name line, all six data cells, and the row padding still are.
5. **Last used placed second-to-last, before an actions column** (9 columns total). Fixed-width columns trimmed (`7.5→7rem`, `6→5.5rem`, `7→6.5rem`, `5.5→4.5rem` rev, langs `1.7→1.4fr`, main `2.4→2.2fr`) to absorb the two new columns. Task 10 owns the final responsive sweep.
6. **`never`, not `—`, for no activity** — `—` already means "no facts" in the neighbouring cells; a distinct word keeps the two absences distinguishable. Carries `title="no agent tool calls recorded for this workspace"`.
7. **Test asserts the split title string.** Blazor renders attribute values through `HtmlEncoder.Default`, which emits the em dash as `&#x2014;`. Asserting the literal spec string would fail on an encoding detail, so the test asserts `title="index facts unavailable` and `open the workspace to inspect"`. Rendered output is correct — browsers decode the entity.

## Self-review

- **Caught and fixed a real CSS bug during self-review**: I first wrote `.ws-row-actions details[open] { position: absolute }`, which would have yanked the `Remove…` summary out of flow and collapsed the cell the instant the row expanded. Now only `details[open] > form` floats (`top: calc(100% + 6px); right: 0`), anchored to `details { position: relative }`; the summary stays in flow.
- **Verified the real rendered HTML**, not just string assertions: rendered `WorkspaceIndex` to a temp probe, eyeballed the markup (correct roles, `aria-sort` on wrappers not buttons, `data-sort-activity="1781258400"`, `<time …>34d ago</time>`, remove control in its right-rail cell with `data-issue-details`/`data-issue-id="remove-ws-a"` intact, no inline `text-decoration` style), then deleted the probe. `git status` confirms only owned files changed.
- **Inherited contracts intact**: `hx-ext="morph"` + `morph:outerHTML` + `every 30s` still on the section (4 matches); `htmx:configRequest` / `If-None-Match` / `X-Miller-Dashboard` / `htmx:beforeSwap` 304 guard / `shouldSwap = false` all still present (5 matches). New `/` listener is an additive `document.addEventListener('keydown')` that touches none of them.
- **Morph-safe**: sort state still lives only in `window.__millerWorkspaceIndexState`; `reflectSortButtons` runs from the same `rehydrate()` on `htmx:afterSwap`, now writing to the columnheader. No state parked on DOM nodes.
- **CSP-Alpine safe**: only `x-on:click="onSort($event)"` and property access; no expressions added.
- `node --check` passes on both JS files.
- Tests carry no narration comments; the two comments present state non-obvious constraints (HtmlEncoder behavior, aria-sort rationale).

## Concerns / notes for the lead

1. **The rewritten roles guard is the one thing worth a second look** — see judgment call 1. Task 5's plan and the pre-existing test were in direct conflict; I resolved it toward the plan because the guard's stated intent (no *malformed* ARIA) is satisfied, and its real teeth (`<a>` rows) are preserved in the replacement.
2. **`:has()` is used for the first time in `dashboard.css`** (`.ws-row-actions:has(details[open])`, keeping an open confirm visible when the pointer drifts). Baseline across modern browsers since Dec 2023 and the dashboard is local-first, so it is safe; `:focus-within` independently covers the click-to-open case, so an unsupporting browser degrades to "confirm hides on pointer-out", never to a broken control.
3. **Stale grid has `role="table"` with no header row** — the stale section never rendered a header, so its cells are positional. Valid ARIA (a table need not have column headers) and better than the previous unlabeled div soup; flagging in case Task 10 wants a header there.
4. **Filter matches remove-form text** (typing `remove` matches every row) — pre-existing, since `applyFilter` reads `row.textContent` and the confirm form was already inside the row. Not touched; Task 6/10 could scope the filter to the name/path.
5. **`ReadIndex` opens the telemetry DB once per call**, one grouped query, no per-workspace fan-out — no new N+1 on the 30s fragment poll.
