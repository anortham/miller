# Dashboard Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Close the remaining defects and rough edges from the 2026-07-08 dashboard audit — spine resilience, endpoint parity, live list refresh, honest timestamps, theme/contrast hygiene, telemetry query efficiency, and detail-page feedback — without changing the dashboard's read-only, local-first contract.

**Architecture:** Seven polish slices against the existing `Miller.Dashboard` project. No new projects, no new MCP tools, no new endpoints — one orphaned fragment route gets its first consumer. The only cross-task contract is a new `DashboardFormat.RelativeTime` helper (Task 2) consumed by Task 6. JSON payload changes are strictly additive.

**Tech Stack:** .NET 10, ASP.NET Razor Components (SSR), Microsoft.Data.Sqlite, htmx + Alpine (CSP build), xUnit.

**Architecture Quality:** No new module boundaries. Risk is concentrated in Task 1 (degrade shapes for the page spine — must not mask real corruption; every degrade carries the underlying message) and Task 3 (theme-token refactor via CSS `light-dark()` — a rendering-only change verified live by the lead). Everything else follows existing patterns (panel degrade shapes, htmx polling attributes, Alpine CSP components).

## Global Constraints

- `dotnet build Miller.slnx -c Release` must stay 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- Fast suite stays pure and under the 30s wrapper budget; no new test spawns `julie-extract` or touches the real user home (`RegistryIsolationConventionTests` + the no-real-home invariant from the 2026-07-08 registry-hygiene plan remain binding).
- **Command-line `--filter` overrides the csproj default Scale exclusion.** Any worker running a scoped filter MUST prefix it with `Category!=Scale&` (e.g. `--filter "Category!=Scale&FullyQualifiedName~DashboardRegistryReadTests"`), or the Scale tests run and the process may crash without `.tools/julie-extract` staged.
- Dashboard stays local-first and CSP-safe: no CDN/network dependency, no inline `<script>` in razor markup, Alpine stays on the CSP build (`alpine-components.js` factories only), htmx stays `selfRequestsOnly` + `allowEval:false`.
- JSON contract changes are ADDITIVE only: never rename or remove an existing property in `/index.json`, `/workspaces.json`, `/snapshot.json`, `/telemetry.json`, `/activity.json`, `/diagnostics.json`. New fields use snake_case `JsonPropertyName` like the existing records.
- The dashboard remains a read surface: no new mutations beyond the existing refresh/open-folder actions; it must not hydrate full indexes for list/detail views.
- No MCP tool or tool-description changes (the `AgentInstructionsTests` budgets are untouched by this plan).
- Contrast target for small text: WCAG AA 4.5:1 against the surface it renders on, in BOTH themes.
- Temp-dir hygiene: any new test temp dir is tracked and deleted on test completion, with `SqliteConnection.ClearAllPools()` before deletion when SQLite files may live under it (repo convention, see `CliDispatchTests.Dispose`).

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section) + `scripts/test.sh`.

**Worker red/green scope:** `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "Category!=Scale&FullyQualifiedName~<TestClassName>"` for the class(es) each task touches.

**Worker ceiling:** `scripts/test.sh` (fast suite, <30s budget). Workers do not run the Scale suite.

**Worker gate invariant:** Each task's new tests prove the task's acceptance criteria (corrupt shared DB renders instead of 500 / relative timestamps render server-side / counts and markup contracts hold / P95 query result is unchanged by the rewrite).

**Lead affected-change scope:** `scripts/test.sh` after each merged batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` (0 warnings) + `scripts/test.sh all` before handoff/PR. Nothing here touches the extract path; `all` is cheap insurance.

**Replay/metric evidence:** Task 7 (lead live check) records report-only evidence: dashboard restarted from the branch build, both themes render, list auto-refresh observed, corrupt-DB simulation degrades instead of 500. Report-only, not a hard gate.

**Escalation triggers:** Any change to `JulieExtractRunner`, freshness, leadership, or `CrossWorkspaceRefreshService` internals (not planned) ⟹ run the Scale suite.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task. Reuse passing evidence for the same HEAD instead of rerunning.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Page-spine resilience + endpoint parity | Batch A | Modify `src/Miller.Dashboard/DashboardData.cs` (spine readers + `RenderSnapshotJson`), `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | No | None - safe parallel batch. |
| Task 2: Formatting foundations (relative time + bytes) | Batch A | Modify `src/Miller.Dashboard/DashboardFormat.cs`, `src/Miller.Dashboard/Components/TelemetryPanel.razor`, `src/Miller.Dashboard/Components/ActivityFeedPanel.razor`; Create `tests/Miller.Tests/Server/DashboardFormatTests.cs`; Test `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` | No | None - safe parallel batch. |
| Task 3: Theme tokens via light-dark() + contrast | Batch A | Modify `src/Miller.Dashboard/wwwroot/dashboard.css`, `src/Miller.Dashboard/wwwroot/js/theme-init.js` | No | None - safe parallel batch. |
| Task 4: Workspace list slice (auto-refresh, ARIA, sort) | Batch B | Modify `src/Miller.Dashboard/Components/WorkspaceIndex.razor`, `src/Miller.Dashboard/Components/WorkspacesShell.razor`, `src/Miller.Dashboard/wwwroot/js/dashboard-site.js`, `src/Miller.Dashboard/wwwroot/js/alpine-components.js`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` | Yes | Task 1 owns `DashboardRegistryReadTests.cs` in Batch A; Task 3 owns `dashboard.css` styles this task's markup must not fight. Runs after Batch A merges. |
| Task 5: Telemetry query efficiency + display-id fix | Batch B | Modify `src/Miller.Dashboard/DashboardData.cs` (`ReadToolStats`/`ComputeP95`/`ReadRecentErrors` region); Test `tests/Miller.Tests/Server/TelemetrySummaryTests.cs` | Yes | Task 1 owns `DashboardData.cs` in Batch A. Runs after Batch A merges. Disjoint from Task 4 — safe to run in parallel with it. |
| Task 6: Detail-page polish (feedback, sparklines, id chips) | None - serial (after Batch B) | Modify `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`, `src/Miller.Dashboard/Components/WorkspaceLocalMetricsPanel.razor`, `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor`, `src/Miller.Dashboard/wwwroot/js/dashboard-site.js`, `src/Miller.Dashboard/wwwroot/dashboard.css`; Test `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` | Yes | Task 4 owns `dashboard-site.js` and Task 3 owns `dashboard.css` in earlier batches; Task 2 owns `DashboardActivityFeedTests.cs` and produces the `RelativeTime` contract this task consumes. |
| Task 7: Lead live verification (ops, report-only) | None - serial (last) | No repo files; operates on the running local dashboard | Yes | Needs Tasks 1–6 built into the dashboard binary. |

Commit mode: `parallel-lead-commit` for Batches A and B; `serial-worker-commit` acceptable for Tasks 6–7 if executed by the lead directly.

---

### Task 1: Page-spine resilience + endpoint parity

**Files:**
- Modify: `src/Miller.Dashboard/DashboardData.cs` — `ReadWorkspaces` (:505), `ReadTelemetrySummary` (:549), `ReadRecentActivity` (:588), `ReadContextSavings` (:728, its `OpenReadOnly` sits OUTSIDE the try that starts a few lines below), `RenderSnapshotJson` (:1233)
- Modify: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs` — `/workspaces/{workspace_id}/refresh` (`MapDashboardJsonEndpoints`, uses throwing `RefreshWorkspace`), `/snapshot.json` (never passes `launchDirectory`)
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Interfaces:**
- Consumes: existing degrade shapes — `DashboardContextSavingsSummary.NotTracked`, empty `DashboardActivityFeed`, the per-panel `"unavailable"` pattern; `DashboardData.TryRefreshWorkspace` (the non-throwing path `/fragments/refresh` already uses).
- Produces: `ReadSnapshot` and the `/` composition path never throw for a corrupt/truncated `workspaces.db` or `telemetry.db`; `DashboardWorkspaceIndex` gains an additive nullable `Error` (`error`) carrying the registry read failure message; `RenderSnapshotJson(registryDbPath, telemetryDbPath, workspaceId, preferredWorkspaceRoot = null)` overload so `/snapshot.json` selects the same default workspace as `/workspace`.

**Contract inputs:** Audit finding A1 (2026-07-08): `OpenReadOnly` + queries in the four spine readers throw `SqliteException`/`IOException` on a corrupt shared DB and 500 every page and JSON route; only the per-workspace `symbols.db` case is protected (test `ReadSnapshot_UnreadableWorkspaceDbReturnsFactsErrorNotCrash`). A4: the JSON refresh endpoint 500s on an unregistered id while the htmx one degrades. A5: `/snapshot.json` falls back to telemetry-count selection because `launchDirectory` is never passed.

**File ownership:** Modify `src/Miller.Dashboard/DashboardData.cs` (spine readers + `RenderSnapshotJson`), `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Make the machine-wide reads degrade the same way the per-workspace reads already do, so a corrupt shared DB produces a rendering page with an honest error instead of a plain-text 500. Bring the JSON refresh endpoint onto the non-throwing path, and give `/snapshot.json` the same default-workspace selection as the `/workspace` page.

**Approach:** Wrap each spine reader's connection open + queries in the repo's precise catch filter (`SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException` — mirror the panel readers, do NOT blanket-catch). On catch: `ReadWorkspaces` propagates nothing — instead `ReadIndex`/`ReadSnapshot` callers receive an empty list plus the new `DashboardWorkspaceIndex.Error`; telemetry/activity/context-savings readers return their existing empty/NotTracked shapes (carry the exception message into an existing `Error`/message field where the shape has one — inspect the records first with Miller, do not invent fields beyond the one named above). `WorkspaceIndex.razor` is owned by Task 4 — do NOT touch it; the `Error` field only needs to serialize and be asserted in tests this task (Task 4 renders it). Fix `ReadContextSavings` by moving the `OpenReadOnly` inside its try. Switch `/workspaces/{workspace_id}/refresh` to `TryRefreshWorkspace` and render the failed result as JSON (same `RenderRefreshJson`). Add the `preferredWorkspaceRoot` parameter to `RenderSnapshotJson` and pass `launchDirectory` from the endpoint. Tests: corrupt-file fixtures (write garbage bytes to a `.db` path, as the existing unreadable-workspace test does) for registry and telemetry → `ReadSnapshot` + `ReadIndex` + `ReadRecentActivity` + `ReadContextSavings` return degrade shapes, no throw; snapshot-parity test proving `RenderSnapshotJson` with a preferred root selects the same workspace `/workspace` would.

**Acceptance criteria:**
- [x] Corrupt `workspaces.db` → `ReadIndex` returns an empty index with `Error` set; `ReadSnapshot` returns a snapshot (no throw).
- [x] Corrupt `telemetry.db` → telemetry summary, activity feed, and context savings degrade to their empty shapes (no throw).
- [x] `/workspaces/{id}/refresh` with an unregistered id returns a JSON failed-result body, not a 500.
- [x] `/snapshot.json` (via `RenderSnapshotJson` with a preferred root) selects the same default workspace as the `/workspace` page.
- [x] Existing `DashboardRegistryReadTests` still pass.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 2: Formatting foundations (relative time + bytes)

**Files:**
- Modify: `src/Miller.Dashboard/DashboardFormat.cs` — `FormatBytes` (:21, caps at MB); add `RelativeTime`
- Modify: `src/Miller.Dashboard/Components/TelemetryPanel.razor` — window label (:149 renders raw `"from {WindowStartTs} to {WindowEndTs}"` ISO strings)
- Modify: `src/Miller.Dashboard/Components/ActivityFeedPanel.razor` — `<time class="rel-ts" data-ts=…>` elements currently render the raw ISO string as their text until JS runs
- Create: `tests/Miller.Tests/Server/DashboardFormatTests.cs`
- Test: `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Interfaces:**
- Consumes: `dashboard-site.js:89` re-humanizes `time.rel-ts[data-ts]` client-side — keep the `data-ts` attribute and `rel-ts` class contract EXACTLY as-is so live updates keep working.
- Produces: `DashboardFormat.RelativeTime(DateTimeOffset value, DateTimeOffset now)` returning strings like `"3m ago"` / `"2h ago"` / `"5d ago"` (match the JS humanizer's buckets in `dashboard-site.js:87-112` so server text and first client repaint agree); `DashboardFormat.FormatBytes` gains a GB tier. **Task 6 consumes `RelativeTime` — its exact name and signature are load-bearing.**

**Contract inputs:** Audit finding A3: every `<time>` flashes the raw ISO timestamp before JS runs and renders it forever with JS off; the telemetry window label is never humanized at all. `FormatBytes` renders multi-GB as `"3000.0 MB"`. Timestamps are stored UTC ISO (`WorkspaceRegistry.FormatTimestamp` "O") — this is display-only.

**File ownership:** Modify `src/Miller.Dashboard/DashboardFormat.cs`, `src/Miller.Dashboard/Components/TelemetryPanel.razor`, `src/Miller.Dashboard/Components/ActivityFeedPanel.razor`; Create `tests/Miller.Tests/Server/DashboardFormatTests.cs`; Test `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Server-side humanized timestamps so the page is correct at first paint and without JS, plus a GB tier for byte formatting. JS keeps refreshing the relative text afterwards exactly as today.

**Approach:** `RelativeTime` is pure and takes `now` explicitly (deterministic tests; callers pass `DateTimeOffset.UtcNow`). In the razor components, render `RelativeTime(...)` as the `<time>` element's text while keeping `data-ts`/`datetime` attributes carrying the ISO value. For the telemetry window label, humanize both endpoints (a short absolute form like `"Jun 8 14:02"` is acceptable for window bounds if relative reads oddly — pick one and test it). Unparseable timestamp strings fall back to rendering the raw value (never throw). GB tier mirrors the existing MB formatting style ("N1" + suffix).

**Acceptance criteria:**
- [x] `DashboardFormatTests` pin `RelativeTime` buckets (seconds/minutes/hours/days) and `FormatBytes` GB tier, including the fallback for unparseable input.
- [x] Rendered activity feed markup contains humanized text inside `time.rel-ts` while `data-ts` still carries the ISO value (render test).
- [x] Telemetry window label no longer contains a raw `+00:00` ISO string (render test).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 3: Theme tokens via light-dark() + contrast

**Files:**
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` — `:root` token block, the duplicated dark blocks (`@media (prefers-color-scheme: dark)` at :65-93 and `html[data-theme="dark"]` at :95-116), `--muted` (:37 light `#8c8576`, dark `#8f897a`)
- Modify: `src/Miller.Dashboard/wwwroot/js/theme-init.js`

**Interfaces:**
- Consumes: the theme toggle stamps `data-theme` on `<html>` (existing `theme-init.js` + toggle button contract — `#theme-toggle` and `#theme-toggle-label` IDs must keep working).
- Produces: one definition per theme token; `--muted` (and any other sub-4.5:1 small-text token found while editing) meets WCAG AA 4.5:1 against `--surface` in both themes.

**Contract inputs:** Audit findings A5 (two near-identical ~25-line dark token blocks — every change must be made twice) and A7 (`--muted` ≈3:1 drives 10-12px labels/paths/timestamps everywhere). The dashboard is local-first and served to the developer's own modern browser; CSS `light-dark()` (baseline since 2024) is acceptable.

**File ownership:** Modify `src/Miller.Dashboard/wwwroot/dashboard.css`, `src/Miller.Dashboard/wwwroot/js/theme-init.js`

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Collapse the duplicated dark-theme variable blocks into single-definition tokens using CSS `light-dark()`, and raise muted-text contrast to AA.

**Approach:** In `:root`, set `color-scheme: light dark` and define each themed token once as `--token: light-dark(<light>, <dark>)`. Replace the two dark blocks with two tiny rules: `html[data-theme="dark"] { color-scheme: dark; }` and `html[data-theme="light"] { color-scheme: light; }` — the explicit stamp then overrides the OS preference with no token duplication, and no-JS users still get OS-preference dark via `color-scheme: light dark`. Verify `theme-init.js` needs no behavior change (it only stamps `data-theme`); update its comments if they describe the old block layout. Choose new `--muted` values that measure ≥4.5:1 against `--surface` in each theme (verify with a contrast calculation, not by eye — record the computed ratios in the task report). Keep every non-token rule untouched; this task changes token definitions only.

**Acceptance criteria:**
- [x] Each theme token is defined exactly once (no duplicated dark block); grep shows one occurrence per token name in the token section.
- [x] `--muted` contrast ≥4.5:1 against `--surface` in both themes (computed ratios recorded in the task report).
- [ ] Manual toggle still overrides OS preference in both directions (verified live in Task 7).
- [x] Build passes; no razor/test changes needed.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 4: Workspace list slice (auto-refresh, ARIA, sort)

**Files:**
- Modify: `src/Miller.Dashboard/Components/WorkspaceIndex.razor` — ARIA roles (`role="table"`/`role="row"` on anchors and divs), section root (htmx polling attrs), render the Task 1 `Index.Error` notice
- Modify: `src/Miller.Dashboard/Components/WorkspacesShell.razor` — only if the polling wrapper needs the section id/attrs adjusted
- Modify: `src/Miller.Dashboard/wwwroot/js/alpine-components.js` — `workspaceIndexFilter` (filter/sort state survives swaps)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` — swap-aware reapply, following the existing issue-detail open-state-survives-swaps pattern
- Test: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Interfaces:**
- Consumes: the orphaned `GET /fragments/workspaces` route (already returns a rendered `WorkspaceIndex` — no endpoint change needed); the existing polling pattern (`hx-get` + `hx-trigger="every 30s"` on `TelemetryPanel.razor:5-7`, visibility-gated in `dashboard-site.js:114-125`); Task 1's `DashboardWorkspaceIndex.Error`.
- Produces: the landing list refreshes itself every 30s (visibility-gated) without losing the user's filter text, sort choice, or manually-opened stale section; rows are plain anchors (no ARIA table roles); columns Files/Symbols/Rev/Workspace are client-side sortable.

**Contract inputs:** Audit findings A1-a11y (anchors with `role="row"` inside a `role="table"` with no `columnheader`/`cell` children — malformed ARIA that demotes links), A6 (list goes stale until manual reload; `/fragments/workspaces` referenced by nothing), A8 partial (filter state). The Alpine component owns `query` + `autoOpenedStale` state today; an htmx swap replaces the section DOM and would destroy that state — preserving it across swaps is the core design problem of this task, solve it deliberately (e.g. keep the filter input OUTSIDE the swapped fragment, or re-init from a persisted value on `htmx:afterSwap`), don't bolt it on.

**File ownership:** Modify `src/Miller.Dashboard/Components/WorkspaceIndex.razor`, `src/Miller.Dashboard/Components/WorkspacesShell.razor`, `src/Miller.Dashboard/wwwroot/js/dashboard-site.js`, `src/Miller.Dashboard/wwwroot/js/alpine-components.js`; Test `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 1 owns `DashboardRegistryReadTests.cs` in Batch A; Task 3 owns `dashboard.css` styles this task's markup must not fight. Runs after Batch A merges.

**What to build:** A workspace list that stays current on its own, reads correctly to assistive tech, sorts by its numeric columns, and shows the Task 1 registry-error notice when the registry is unreadable.

**Approach:** ARIA: drop `role="table"`/`role="row"`/the `aria-hidden` header-row role usage; rows stay `<a class="ws-index-row">` (a list of links is honest semantics; keep the visual grid via CSS classes, which do not depend on the roles). Auto-refresh: swap only the list markup (rows + stale section), not the panel heading/filter input — putting the `hx-get="/fragments/workspaces"` target on an inner container whose fragment response matches is the cleanest state-preservation move; gate with the existing visibility mechanism. NOTE: `/fragments/workspaces` returns the whole `WorkspaceIndex` component — if you swap an inner container instead, use `hx-select` to pick the matching inner node from the response rather than changing the endpoint. After each swap, re-apply the current filter and sort (listen once in `dashboard-site.js`, mirroring the issue-detail open-state pattern). Sort: extend `workspaceIndexFilter` (or a sibling CSP factory) with a `sortBy(column)` toggle that reorders the row elements in place; header cells become `<button>`s with `aria-sort` state. Render the `Index.Error` notice with the existing notice/error styles when set. Render tests: no `role="table"` in output; sortable headers are buttons; error notice renders when `Error` is set; polling attributes present.

**Acceptance criteria:**
- [x] List markup contains no ARIA table roles; rows remain anchors; sortable headers are buttons with `aria-sort`.
- [x] Landing list polls `/fragments/workspaces` every 30s, visibility-gated, and a swap does not clear filter text, sort order, or a manually-opened stale section (JS behavior; markup contract render-tested, live-verified in Task 7).
- [x] `Index.Error` (Task 1) renders as a visible notice with the existing error styling.
- [x] Existing list render tests still pass (stale split, prune hint, filter empty-state).
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 5: Telemetry query efficiency + display-id fix

**Files:**
- Modify: `src/Miller.Dashboard/DashboardData.cs` — `ReadToolStats` (:1404, two correlated subqueries per group row), `ComputeP95` (:1566, called per tool at :1474 — each an `ORDER BY duration_ms LIMIT 1 OFFSET n` sort), `ReadRecentErrors` (:1487, hardcodes `WorkspaceDisplayId: null` at :1537)
- Test: `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`

**Interfaces:**
- Consumes: `tool_telemetry` schema as the existing queries read it (guarded by the existing `ColumnExists` checks for older telemetry schemas); the registry display-id lookup pattern the activity feed already uses for its entries.
- Produces: identical `DashboardToolStat` values (same counts, same P95s) from a single grouped pass; `DashboardRecentError.WorkspaceDisplayId` resolved from the registry the same way the activity feed resolves ids.

**Contract inputs:** Audit findings B1 (O(tools × rows·log rows) — per-tool P95 sort plus two correlated subqueries per row) and B5 (telemetry-summary errors never show display ids while the activity feed does). The rewrite must preserve exact output; the tests pin representative fixtures BEFORE the rewrite and must pass unchanged after.

**File ownership:** Modify `src/Miller.Dashboard/DashboardData.cs` (`ReadToolStats`/`ComputeP95`/`ReadRecentErrors` region); Test `tests/Miller.Tests/Server/TelemetrySummaryTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 1 owns `DashboardData.cs` in Batch A. Runs after Batch A merges. Disjoint from Task 4 — safe to run in parallel with it.

**What to build:** One pass over `tool_telemetry` computing per-tool counts and P95s (SQLite window functions — `PERCENT_RANK`/`NTILE` over `PARTITION BY tool`, or a rank-per-partition CTE matching the existing OFFSET semantics exactly), replacing the per-tool sorts and correlated subqueries. Resolve display ids for recent errors.

**Approach:** Write the pinning tests FIRST against the current implementation (several tools, uneven call counts, known durations → exact expected P95 per the current OFFSET formula; include the 1-row and 2-row edge cases where OFFSET rounding matters). Then rewrite the SQL and prove byte-identical results. Keep the `ColumnExists` degradation for old schemas. For display ids, read the workspace map once (it is already available in the calling scope — inspect `ReadTelemetrySummary` at :549 for what is in hand) instead of adding per-row lookups.

**Acceptance criteria:**
- [x] Pinning tests written against the CURRENT implementation pass unchanged against the rewrite (same counts and P95s, including 1- and 2-sample tools).
- [x] The rewritten path issues one grouped query for stats + P95 (no per-tool query loop).
- [x] Recent errors carry resolved display ids when the workspace is registered; null only for unregistered ids.
- [x] Worker-scope verification passes and the change is handed to the lead per commit mode.

### Task 6: Detail-page polish (feedback, sparklines, id chips)

**Files:**
- Modify: `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor` — refresh button (:16 region), open-folder button (:22 region), artifact id row, any raw-ISO `<time>` text
- Modify: `src/Miller.Dashboard/Components/WorkspaceLocalMetricsPanel.razor` — clone body hash display
- Modify: `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor` — sparkline block (:34-39)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` — open-folder success toast
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` — chip + sparkline-label styles only (additive)
- Test: `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Interfaces:**
- Consumes: `DashboardFormat.RelativeTime(DateTimeOffset value, DateTimeOffset now)` from Task 2; the existing toast mechanism in `dashboard-site.js` (htmx responseError/sendError/timeout already toast); the existing copy-button pattern (`Copy id` / `Copy workspace_id` / `Copy path` in `WorkspaceDetailPanel.razor`).
- Produces: user-visible feedback for both detail-page actions; sparklines with min/max labels; long hashes/ids rendered as truncated copyable chips with `title` carrying the full value.

**Contract inputs:** Audit findings A9 (refresh signals work only via global opacity; open-folder is silent on success and looks like a link), B-sparklines (no scale), B-raw-ids (artifact id and clone body hash rendered raw). Jargon: give `title` explanations to the terms on the detail page the audit called out — "sidecar", "artifact", "revision" — one plain-English sentence each.

**File ownership:** Modify `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor`, `src/Miller.Dashboard/Components/WorkspaceLocalMetricsPanel.razor`, `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor`, `src/Miller.Dashboard/wwwroot/js/dashboard-site.js`, `src/Miller.Dashboard/wwwroot/dashboard.css`; Test `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Serialization required:** Yes

**Dependency reason:** Task 4 owns `dashboard-site.js` and Task 3 owns `dashboard.css` in earlier batches; Task 2 owns `DashboardActivityFeedTests.cs` and produces the `RelativeTime` contract this task consumes.

**What to build:** Honest action feedback and readable data on the workspace detail page: a refresh button that says it is refreshing, an open-folder action that confirms success and looks like a button, sparklines with scale, and copyable truncated chips for long identifiers.

**Approach:** Refresh: use htmx's indicator contract (`hx-indicator` + a "Refreshing…" label span shown via the `.htmx-request` class on the button) instead of only the global opacity rule. Open-folder: style as a real button (existing button classes, not `.subtle-link`); in `dashboard-site.js`, toast success on `htmx:afterRequest` with a 2xx for that button (match by id or data attribute — keep it CSP-safe, no inline JS). Sparklines: render min/max (and latest value) as small text labels beside the svg from the series data already in hand — no charting library. Chips: truncate to the first 12 chars + ellipsis, full value in `title`, reuse the existing copy-button pattern for the artifact id and clone body hash. Any `<time>` on the detail page that still renders raw ISO text gets the Task 2 `RelativeTime` treatment. Render tests: refresh button carries the indicator markup; open-folder is a button with the toast hook attribute; sparkline labels present; chips truncate with full-value `title`.

**Acceptance criteria:**
- [x] Refresh button shows an in-progress label via the htmx indicator mechanism (render-tested markup).
- [x] Open-folder renders as a button and success produces a toast (markup render-tested; toast live-verified in Task 7).
- [x] Sparklines display min/max/latest labels derived from the series.
- [x] Artifact id and clone body hash render as truncated copyable chips with full value in `title`; sidecar/artifact/revision labels carry `title` explanations.
- [x] Worker-scope verification passes and the change is committed per commit mode.

### Task 7: Lead live verification (ops, report-only)

**Files:**
- No repo changes. Operates on the running local dashboard (`workspace` tool `operation=dashboard`, port 4977).

**Interfaces:**
- Consumes: Tasks 1–6 built into `src/Miller.Dashboard/bin/Release/net10.0/Miller.Dashboard.dll`.
- Produces: report-only evidence in the final report.

**Contract inputs:** The dashboard launcher reuses a running instance — kill the existing `Miller.Dashboard` process first, then relaunch via the `workspace` tool so the new build serves.

**File ownership:** No repo files; live machine state only.

**Serialization required:** Yes

**Dependency reason:** Needs Tasks 1–6 built into the dashboard binary.

**What to build (ordered, all report-only):**
1. Restart the dashboard from the branch build; record `/`, `/workspace`, `/snapshot.json` all 200.
2. Theme: toggle light/dark both directions; confirm tokens render in both and muted text is readable (Task 3's recorded ratios are the gate; this is the eyeball check).
3. List: watch one 30s auto-refresh land; type a filter, wait for the next poll, confirm the filter text and matches survive the swap; sort a column.
4. Detail: run a refresh (label appears), open folder (toast appears), inspect sparkline labels and id chips.
5. Corruption drill: copy `~/.miller/telemetry.db` aside, truncate the copy, point a scratch dashboard at it via `MILLER_TELEMETRY_DB` (do NOT touch the real file), confirm pages render degraded with the error notice instead of 500. Delete the scratch copy.
6. Timestamps: confirm no raw ISO flash on load (hard refresh with cache disabled).

**Acceptance criteria:**
- [ ] All six checks recorded in the final report with observed values.
- [ ] The real `~/.miller/telemetry.db` and `workspaces.db` were not modified (row-count before/after recorded).

---

## Out of scope (deliberately)

- **Async/queued refresh with timeout** — refreshing still runs `julie-extract` synchronously on the request thread. Fixing this properly needs a design pass over `CrossWorkspaceRefreshService` (cancellation, a job model, progress surfacing) and touches the indexing path; it is a plan of its own, not a polish slice.
- **`ReadIndex` cold-load parallelism** — ~55 serial `symbols.db` opens behind a 30s cache is acceptable today; revisit when registries reach hundreds of live rows.
- **Sortable/filterable server-side list, workspace search, docs links** — beyond polish; fold into a future dashboard feature plan if wanted.
