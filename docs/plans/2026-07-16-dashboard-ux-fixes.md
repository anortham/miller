# Dashboard UX Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Eliminate the dashboard's polling flicker and land the full set of fixes from the 2026-07-16 dashboard deep-dive review (bugs, presentation, usability, hardening, mobile).

**Architecture:** Keep the documented static SSR + htmx + Alpine pattern (`docs/reference/static-ssr-htmx-alpine-pattern.md`) — no Blazor circuits. Flicker is fixed by vendoring idiomorph and switching polled fragments to morph swaps, plus an ETag/304 short-circuit so unchanged fragments never repaint. All other fixes are localized to `src/Miller.Dashboard` components, CSS, JS, and the host pipeline.

**Tech Stack:** .NET 10 static SSR Razor components, htmx 2.0.4 (vendored), Alpine CSP build (vendored), idiomorph 0.7.4 (to vendor), SQLite readers.

**Architecture Quality:** No new projects or seams. One new small static class (`DashboardRefreshJobs`) for background refresh jobs, one new middleware concern (fragment ETag) inside `DashboardHostPipeline`, one new page component for styled 404. Main risk: morph swaps interacting with Alpine client state — Task 1 owns that interaction and proves it with HTTP-level + rendered-HTML tests.

## Global Constraints

- Local-first: NO CDN or external network calls from the dashboard at runtime; all JS libs vendored under `wwwroot/lib/` and served by explicit routes in `DashboardHostPipeline.Configure`.
- Alpine runs the CSP build: attribute expressions are property access and `foo($event)` calls only — no inline logic in `x-on`/`x-bind`.
- htmx config stays `{"selfRequestsOnly":true,"allowEval":false}`.
- Every fragment endpoint keeps `PreventStreamingRendering = true`.
- Build is warnings-as-errors (`Directory.Build.props`); 0 warnings tolerated.
- JSON contract surfaces (`workspaces.json`, `telemetry.json`, `activity.json`, `snapshot.json`, `index.json`, `diagnostics.json`, POST `/workspaces/{id}/refresh`) keep their response shapes. The refresh POST gains a required `X-Miller-Dashboard: 1` header (CSRF guard) — update any doc under `docs/` that documents that endpoint in the same task.
- Comments discipline: no narration comments; only non-obvious constraints. Tests carry zero comments.
- Do not edit `CLAUDE.md`/`AGENTS.md`.
- Component tests follow the existing `RenderComponentAsync<T>` pattern in `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`; HTTP-level tests follow the TestServer pattern in `tests/Miller.Tests/Server/DashboardMutationEndpointTests.cs`.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` Testing section; `scripts/test.sh`.

**Worker red/green scope:** `scripts/test.sh` (fast suite, ~20-30s). A focused filter is allowed but MUST include the Scale exclusion explicitly: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "(Category!=Scale)&(FullyQualifiedName~<TestClass>)"` (a command-line `--filter` overrides the csproj default).

**Worker ceiling:** fast suite + `dotnet build Miller.slnx -c Release`.

**Worker gate invariant:** each task's new/changed behavior has at least one test that fails before the change and passes after; the fast suite stays green; the Release build stays at 0 warnings.

**Lead affected-change scope:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh` after each batch.

**Branch gate:** `scripts/test.sh all` + Release build + live visual smoke: `MILLER_DASHBOARD_PORT=4999 dotnet run -c Release --project src/Miller.Dashboard` then headless-Chrome screenshots of `/` and `/workspace?workspace_id=<id>` at 1440px and 390px in light and dark; no right-edge clipping, no flicker-inducing full swaps (spot-check via devtools-free heuristic: two successive fragment GETs with same content return 304).

**Replay/metric evidence:** screenshots are report-only evidence; the 304 behavior and rendered-HTML assertions are hard gates in tests.

**Escalation triggers:** any change under `src/Miller.Indexing` or `src/Miller.Server` outside `Tools/WorkspaceRender`-adjacent display code requires the scale suite before commit. (None planned.)

**Assigned verification failure:** Workers stop and report when assigned verification fails.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task in the task's final report.

## Parallel Execution Contract

| Task | Parallel batch | File ownership | Serialization required | Dependency reason |
|---|---|---|---|---|
| Task 1: Anti-flicker (morph + ETag/304) | None - serial | `wwwroot/lib/idiomorph/**` (create), `Components/DashboardScripts.razor`, `Components/WorkspaceIndex.razor`, `Components/ActivityFeedPanel.razor`, `Components/TelemetryPanel.razor`, `Components/WorkspaceRemoveConfirm.razor`, `wwwroot/js/dashboard-site.js`, `wwwroot/js/alpine-components.js`, `DashboardHostPipeline.cs`, `Endpoints/DashboardEndpoints.cs` (fragment routes only), tests: `tests/Miller.Tests/Server/DashboardFragmentCachingTests.cs` (create) | Yes | Foundation for later tasks that edit the same hub files; must land first so later tasks build on morph semantics. |
| Task 2: Copy & data presentation | Batch B | `DashboardFormat.cs`, `Components/WorkspaceOnboardingPanel.razor`, `Components/PatternInventoryPanel.razor`, tests: `tests/Miller.Tests/Server/DashboardFormatTests.cs` (create if absent; else extend existing FormatCount coverage), `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (extend) | No | None - safe parallel batch. |
| Task 3: Styled 404, version footer, JSON links | Batch B | `Components/NotFoundPage.razor` (create), `Endpoints/DashboardEndpoints.cs` (GET `/workspace` 404 branch only), `Components/WorkspacesShell.razor`, `Components/WorkspaceShell.razor`, `wwwroot/dashboard.css` (append new `.site-footer` / `.not-found` sections at end of file only), tests: `tests/Miller.Tests/Server/DashboardNotFoundTests.cs` (create) | No | None - safe parallel batch (disjoint file set from Task 2; CSS additions append-only). |
| Task 4: Hardening (Host allowlist + CSRF header) | None - serial | `DashboardHostPipeline.cs`, `Endpoints/DashboardEndpoints.cs`, `wwwroot/js/dashboard-site.js`, docs under `docs/` that document POST `/workspaces/{id}/refresh`, tests: `tests/Miller.Tests/Server/DashboardMutationEndpointTests.cs` (extend) | Yes | Touches `DashboardHostPipeline.cs` and `DashboardEndpoints.cs` after Tasks 1 and 3 edit them. |
| Task 5: Workspace list UX | None - serial | `Components/WorkspaceIndex.razor`, `DashboardData.cs` (ReadIndex + index entry record), `wwwroot/js/alpine-components.js`, `wwwroot/js/dashboard-site.js` (`/` shortcut), `wwwroot/dashboard.css` (ws-index sections), tests: `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` (extend), `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (extend) | Yes | Edits `WorkspaceIndex.razor`/`alpine-components.js` after Task 1 changes their swap semantics. |
| Task 6: Telemetry panel polish | None - serial | `Components/TelemetryPanel.razor`, `wwwroot/js/alpine-components.js`, `wwwroot/dashboard.css` (telemetry + recent-errors sections), tests: `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (extend) | Yes | Shares `alpine-components.js` and CSS with Task 5. |
| Task 7: Notices, cancel, theme label | None - serial | `Components/WorkspaceRemoveConfirm.razor`, `Components/WorkspaceIndex.razor` (notice data attr), `Components/WorkspacesShell.razor` + `Components/WorkspaceShell.razor` (theme button spans), `wwwroot/js/dashboard-site.js`, `wwwroot/dashboard.css` (theme-switch section), tests: `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (extend) | Yes | Shares shells, `WorkspaceIndex.razor`, `dashboard-site.js` with earlier tasks. |
| Task 8: Trends time axis | None - serial | `DashboardData.cs` (DashboardTrendSeries), `DashboardIndexFactsReader.cs`, `Components/WorkspaceTrendsPanel.razor`, `wwwroot/dashboard.css` (trends section), tests: extend the existing trends read/render tests wherever they live (locate via `grep -rn "DashboardTrendSeries" tests/`) | Yes | Shares `DashboardData.cs` with Task 5. |
| Task 9: Async refresh | None - serial | `DashboardRefreshJobs.cs` (create), `Endpoints/DashboardEndpoints.cs` (`/fragments/refresh` + new `/fragments/refresh-status`), `Components/WorkspaceDetailPanel.razor`, `Components/RefreshStatusPanel.razor`, tests: `tests/Miller.Tests/Server/DashboardRefreshJobsTests.cs` (create), `tests/Miller.Tests/Server/DashboardMutationEndpointTests.cs` (extend) | Yes | Edits `DashboardEndpoints.cs` after Task 4. |
| Task 10: Responsive pass + hero grid + final visual sweep | None - serial | `wwwroot/dashboard.css` (broad), minor razor tweaks only if a layout cannot be fixed in CSS, screenshot evidence in task report | Yes | Must run last against final markup from all prior tasks. |

Commit modes: Batch B (Tasks 2, 3) uses `parallel-lead-commit`. All serial tasks use `serial-worker-commit`.

---

### Task 1: Anti-flicker — idiomorph morph swaps + fragment ETag/304

**Files:**
- Create: `src/Miller.Dashboard/wwwroot/lib/idiomorph/idiomorph-ext.min.js` (vendor v0.7.4 — download `https://unpkg.com/idiomorph@0.7.4/dist/idiomorph-ext.min.js`; verify non-empty and starts with JS, record the version in the task report)
- Modify: `src/Miller.Dashboard/DashboardHostPipeline.cs` (static route for the new lib; fragment ETag middleware)
- Modify: `src/Miller.Dashboard/Components/DashboardScripts.razor` (script tag after htmx loads — htmx is in `<head>` via `DashboardHead.razor`, so a plain script tag here is safe)
- Modify: `src/Miller.Dashboard/Components/WorkspaceIndex.razor:4-11`, `Components/ActivityFeedPanel.razor:3-8`, `Components/TelemetryPanel.razor:3-8,14-18` (add `hx-ext="morph"`, change `hx-swap` to `morph:outerHTML` on the polled sections and the telemetry Refresh button)
- Modify: `src/Miller.Dashboard/Components/WorkspaceRemoveConfirm.razor` (persist open state: add `data-issue-details data-issue-id="remove-@WorkspaceId"` to the `<details>`)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` (If-None-Match request header from stored ETag; treat 304 as no-swap in `htmx:beforeSwap`; store ETag from response on the polled element)
- Modify: `src/Miller.Dashboard/wwwroot/js/alpine-components.js` (in `workspaceIndexFilter.init()`, listen for `htmx:afterSwap` on `this.$el` and re-run `applySort`/`reflectSortButtons`/`applyFilter` + stale-open restore, since with morph the component instance survives the swap and `init()` no longer re-fires)
- Test: Create `tests/Miller.Tests/Server/DashboardFragmentCachingTests.cs`

**Interfaces:**
- Consumes: existing fragment GET routes (`/fragments/workspaces`, `/fragments/activity`, `/fragments/telemetry`, `/fragments/dashboard`).
- Produces: fragment GETs emit an `ETag` response header (strong hash of the response body) and honor `If-None-Match` with `304` + empty body. Polled sections carry `hx-ext="morph"` + `hx-swap="morph:outerHTML"`. `<details data-issue-details data-issue-id>` is the generic persist-open contract (already restored by `rememberIssueDetailsState`). Later tasks must keep these attributes when editing the same elements.

**Contract inputs:** htmx 2.0.4 vendored at `wwwroot/lib/htmx/htmx.min.js`; existing persist-open machinery `captureIssueDetailsState`/`rememberIssueDetailsState` in `dashboard-site.js`.

**File ownership:** `wwwroot/lib/idiomorph/**` (create), `Components/DashboardScripts.razor`, `Components/WorkspaceIndex.razor`, `Components/ActivityFeedPanel.razor`, `Components/TelemetryPanel.razor`, `Components/WorkspaceRemoveConfirm.razor`, `wwwroot/js/dashboard-site.js`, `wwwroot/js/alpine-components.js`, `DashboardHostPipeline.cs`, `Endpoints/DashboardEndpoints.cs` (fragment routes only), tests: `DashboardFragmentCachingTests.cs`.

**Serialization required:** Yes

**Dependency reason:** Foundation for later tasks that edit the same hub files; must land first so later tasks build on morph semantics.

**What to build:** Stop the visible panel teardown on every poll. Morph swaps patch only changed nodes; the ETag/304 pass means an unchanged fragment costs no DOM work at all.

**Approach:**
- ETag middleware: wrap only `GET /fragments/*` responses. Buffer the body (fragments are small), compute SHA-256, set `ETag: "<hex>"`. When the request carries a matching `If-None-Match`, return `304` with empty body instead. Implement as an inline `app.Use` in `DashboardHostPipeline.Configure` or a small private static method — follow the existing exception-wrapper style there.
- Antiforgery tokens are deterministic per cookie, so hashes are stable across polls for the same client; the HTTP test must carry the antiforgery cookie between the two requests (reuse the cookie-handling helper pattern from `DashboardMutationEndpointTests`). If the token proves non-deterministic in the test, stop and report — do not strip tokens from fragments.
- Client JS: on `htmx:afterOnLoad`, if the detail element has `data-poll-trigger`, read `xhr.getResponseHeader('ETag')` and stash it in a module-level map keyed by element id (survives morph). On `htmx:configRequest`, attach `If-None-Match` when a stored ETag exists. On `htmx:beforeSwap`, `if (event.detail.xhr.status === 304) event.detail.shouldSwap = false`.
- Manual smoke: run the dashboard on port 4999, confirm in the browser network log (curl is fine: two successive GETs, second with `If-None-Match`) that unchanged fragments 304.

**Acceptance criteria:**
- [x] `GET /fragments/workspaces` responds with an `ETag`; repeating the request with that `If-None-Match` (same cookies) yields `304` with an empty body; a data change yields a fresh 200 + different ETag.
- [x] Rendered `WorkspaceIndex`/`ActivityFeedPanel`/`TelemetryPanel` HTML contains `hx-ext="morph"` and `hx-swap="morph:outerHTML"` on the polled sections (rendered-HTML assertions).
- [x] Remove-confirm `<details>` carries `data-issue-details` + stable `data-issue-id` so its open state survives swaps.
- [x] `/lib/idiomorph/idiomorph-ext.min.js` is served by the pipeline (HTTP test) and loaded by `DashboardScripts`.
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 2: Copy & data presentation (pluralization, unresolved hashes, pattern list)

**Files:**
- Modify: `src/Miller.Dashboard/DashboardFormat.cs:14-15`
- Modify: `src/Miller.Dashboard/Components/WorkspaceOnboardingPanel.razor:36,63-84,136-148`
- Modify: `src/Miller.Dashboard/Components/PatternInventoryPanel.razor:29-44`
- Test: extend `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` (component renders) and FormatCount unit coverage (locate existing FormatCount tests via `grep -rn "FormatCount" tests/`; create `DashboardFormatTests.cs` only if none exist)

**Interfaces:**
- Consumes: `DashboardOnboardingTarget` (`Name`, `Confidence`, `Calls`, `Kind`, `Path`, `Line`), `DashboardPatternFamily` (`Family`, `FactCount`, `PatternCount`, `Languages`, `Captures`).
- Produces: `FormatCount(long value, string singular, string? plural = null)` — the two-arg form keeps its behavior for regular nouns; the three-arg form is used where naive `+s` is wrong ("common miss" → "common misses").

**Contract inputs:** `FormatCount` is used across many panels via `@using static` — the two-arg overload's output must not change for existing callers.

**File ownership:** `DashboardFormat.cs`, `Components/WorkspaceOnboardingPanel.razor`, `Components/PatternInventoryPanel.razor`, tests as listed.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch.

**What to build:** Fix "10 common misss"; collapse meaningless `unresolved_hash` hot-target rows into one summary row; drop the redundant per-pattern sub-line noise.

**Approach:**
- Pluralization: add optional `plural` parameter; `WorkspaceOnboardingPanel.razor:36` passes `"common miss", "common misses"`.
- Hot targets: partition `Onboarding.HotTargets` into named targets (`!string.IsNullOrWhiteSpace(Name)`) and unresolved ones. Render named rows as today; render unresolved ones as ONE trailing row: "N unresolved targets" with summed calls and detail "hashes not present in the current index". If all targets are unresolved, the summary row is the only row.
- Pattern inventory: keep the `family — N facts` row. In the sub-line, show `languages: …` always, add `· N patterns` only when `PatternCount > 1`, and show `captures: …` only when the capture set differs from the family's trailing segment (e.g. family `json.property` capture `property` is redundant; omit).

**Acceptance criteria:**
- [x] Rendered onboarding panel says "common misses" (test with 2+ misses) and shows at most one unresolved-targets row.
- [x] Rendered pattern inventory omits `captures:` when redundant and omits `1 pattern`.
- [x] FormatCount two-arg behavior unchanged (regression assertions for "file"/"symbol").
- [x] Worker-scope verification passes; verified diff handed to the lead per `parallel-lead-commit`.

### Task 3: Styled 404, version footer, JSON links open in new tab

**Files:**
- Create: `src/Miller.Dashboard/Components/NotFoundPage.razor`
- Modify: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs:59-65` (return the component with 404 status instead of `Results.NotFound(text)`)
- Modify: `src/Miller.Dashboard/Components/WorkspacesShell.razor` and `Components/WorkspaceShell.razor` (footer + `target="_blank" rel="noopener"` on `.api-link` anchors)
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (append `.site-footer` and `.not-found` styles at END of file only — Batch B parallel-safety)
- Test: Create `tests/Miller.Tests/Server/DashboardNotFoundTests.cs`

**Interfaces:**
- Consumes: `Miller.Server.MillerVersion.Current` (already referenced by `DashboardEndpoints.BuildRuntimeInfo`).
- Produces: `NotFoundPage` component with parameters `Message` (string) and nothing else; both shells render a `<footer class="site-footer">` containing `miller <version>` and a `diagnostics.json` link.

**Contract inputs:** `RazorComponentResult` supports setting `StatusCode = 404` on the result object.

**File ownership:** `Components/NotFoundPage.razor` (create), `Endpoints/DashboardEndpoints.cs` (GET `/workspace` 404 branch only), `Components/WorkspacesShell.razor`, `Components/WorkspaceShell.razor`, `wwwroot/dashboard.css` (append-only), tests: `DashboardNotFoundTests.cs`.

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (disjoint file set from Task 2; CSS additions append-only).

**What to build:** A styled 404 page (uses `DashboardHead`, hero styling, the not-registered message, and a link back to `/`), a small footer with the Miller version on both shells, and JSON links that stop navigating the dashboard tab away.

**Approach:** `NotFoundPage` is a minimal full-document component (doctype/head/body like the shells) with the message and an `← All workspaces` link. The endpoint keeps the exact message text ("workspace_id '<id>' is not registered — open / for the workspace list.") inside the page body so existing behavior stays discoverable, and sets HTTP 404.

**Acceptance criteria:**
- [x] `GET /workspace?workspace_id=bogus` returns 404 with `text/html`, contains the message and a link to `/`.
- [x] Both shells render the footer with `MillerVersion.Current` and api-links carry `target="_blank"`.
- [x] Worker-scope verification passes; verified diff handed to the lead per `parallel-lead-commit`.

### Task 4: Hardening — Host allowlist + CSRF header on non-form POSTs

**Files:**
- Modify: `src/Miller.Dashboard/DashboardHostPipeline.cs` (Host validation before routing)
- Modify: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs:124-148,150-177,213-221` (require `X-Miller-Dashboard: 1` header on POST `/fragments/refresh`, POST `/workspaces/{id}/open-folder`, POST `/workspaces/{id}/refresh`)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` (add the header to every htmx request via `htmx:configRequest`)
- Modify: any doc under `docs/` documenting POST `/workspaces/{id}/refresh` (locate via `grep -rn "workspaces/{workspace_id}/refresh\|workspaces.*refresh" docs/contracts docs/reference`)
- Test: extend `tests/Miller.Tests/Server/DashboardMutationEndpointTests.cs`

**Interfaces:**
- Consumes: pipeline middleware order (exception wrapper → host check → routing → antiforgery).
- Produces: requests with a Host other than `127.0.0.1[:port]` or `localhost[:port]` get 403 before routing; the three POSTs above return 400 without the `X-Miller-Dashboard` header. Every htmx request site-wide now carries the header (harmless on GETs).

**Contract inputs:** `DashboardPaths.Url` is always `http://127.0.0.1:{port}`; TestServer default host is `localhost`, so the allowlist must accept both loopback names for tests to pass.

**File ownership:** `DashboardHostPipeline.cs`, `Endpoints/DashboardEndpoints.cs`, `wwwroot/js/dashboard-site.js`, contract docs, tests: `DashboardMutationEndpointTests.cs`.

**Serialization required:** Yes

**Dependency reason:** Touches `DashboardHostPipeline.cs` and `DashboardEndpoints.cs` after Tasks 1 and 3 edit them.

**What to build:** Close DNS-rebinding reads (Host check) and cross-origin form-POST CSRF on the endpoints that skip antiforgery (a custom header cannot be attached by a cross-origin form, and fetch with it triggers a failing CORS preflight).

**Approach:** Host check compares `context.Request.Host.Host` against `127.0.0.1`, `localhost`, `[::1]`; wrong host → 403 plain text. Header check per endpoint (shared private helper), 400 with a short message pointing at the header name. Antiforgery-validated form posts (remove/prune) are already covered and stay unchanged.

**Acceptance criteria:**
- [x] TestServer request with `Host: evil.example` → 403; normal host → 200.
- [x] The three POSTs → 400 without the header, succeed with it; remove/prune behavior unchanged.
- [x] Contract doc updated if it documents the JSON refresh POST.
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 5: Workspace list UX (row click, remove de-emphasis, sort affordance, no-facts clarity, last-activity, `/` shortcut)

**Files:**
- Modify: `src/Miller.Dashboard/Components/WorkspaceIndex.razor`
- Modify: `src/Miller.Dashboard/DashboardData.cs` (ReadIndex: join per-workspace last telemetry call timestamp; extend `DashboardWorkspaceIndexEntry` with `LastActivityTs` — locate the record via `grep -n "record DashboardWorkspaceIndexEntry" src/Miller.Dashboard/DashboardData.cs`)
- Modify: `src/Miller.Dashboard/wwwroot/js/alpine-components.js` (new `activity` sort column; aria-sort moves to columnheader wrappers)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` (`/` keydown focuses `#workspace-filter` when not typing in an input/textarea)
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (ws-index sections)
- Test: extend `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` (last-activity read) and `DashboardActivityFeedTests.cs` (rendered list assertions)

**Interfaces:**
- Consumes: telemetry DB schema as already queried by `DashboardData.ReadTelemetrySummary`/`ReadRecentActivity` (reuse the same table/column names — discover them in `DashboardData.cs`, do not invent).
- Produces: `DashboardWorkspaceIndexEntry.LastActivityTs` (ISO string or null); a "Last used" sortable column (`data-sort-activity` epoch-seconds key, `-1` for none); grid header cells wrapped in `role="columnheader"` elements carrying `aria-sort`; the whole grid gets `role="table"` semantics (`role="row"` on rows, `role="cell"` on cells).
- JSON note: if `index.json` serializes index entries, the new field appears there — additive, allowed.

**Contract inputs:** `ReadIndex` currently takes only the registry path (`DashboardEndpoints.cs:28,108`) — it gains the telemetry path parameter; update both call sites. Missing/unreadable telemetry DB must degrade to null timestamps, never throw (same discipline as other readers).

**File ownership:** `Components/WorkspaceIndex.razor`, `DashboardData.cs`, `wwwroot/js/alpine-components.js`, `wwwroot/js/dashboard-site.js`, `wwwroot/dashboard.css` (ws-index), tests as listed.

**Serialization required:** Yes

**Dependency reason:** Edits `WorkspaceIndex.razor`/`alpine-components.js` after Task 1 changes their swap semantics.

**What to build:** Make rows behave like the "click to inspect" hint promises, demote the destructive control, make sorting discoverable and semantically correct, explain fact-less rows, and add the one column that actually helps pruning decisions: when the workspace was last used.

**Approach:**
- Whole-row click: stretched-link pattern — `.ws-index-row { position: relative }`, `.workspace-name::after { content:""; position:absolute; inset:0 }`; interactive elements inside the row (`Remove…` summary, sort buttons don't apply) get `position: relative; z-index: 1` so they stay clickable above the stretch. Remove the inline `style="text-decoration: none"` while here (move to CSS).
- Remove de-emphasis: keep the details/summary control but move it visually to the row's right rail (smaller, muted); reveal on `.ws-index-row:hover` and `:focus-within`, always visible when its `<details>` is open. Preserve the Task 1 persist-open attributes.
- Sort affordance: idle two-way caret (`content: "\2195"; opacity: .35`) on `aria-sort="none"` headers so sortability is visible before the first click.
- No-facts clarity: when `!entry.HasFacts`, render the files/symbols cells as `—` with `title="index facts unavailable — open the workspace to inspect"` and add a muted `no facts` note next to the state chip. When `HasFacts` and `FileCount == 0`, note `empty index`.
- Last used: `MAX(ts)` per workspace from the telemetry calls table, single grouped query, merged into entries by workspace id; rendered with the existing `rel-ts` `<time>` pattern; sortable via `data-sort-activity`.

**Acceptance criteria:**
- [x] Rendered list carries table roles, columnheader-scoped `aria-sort`, idle sort carets, stretched-link rows, right-rail remove control, no-facts notes, and a Last used column.
- [x] `ReadIndex` returns last-activity timestamps with a missing telemetry DB degrading to nulls (test with temp DBs).
- [x] Client sort by Last used orders by the epoch key (attribute assertions on rendered rows).
- [x] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 6: Telemetry panel polish (sortable columns, recent-errors alignment)

**Files:**
- Modify: `src/Miller.Dashboard/Components/TelemetryPanel.razor`
- Modify: `src/Miller.Dashboard/wwwroot/js/alpine-components.js` (new `telemetryTableSort` Alpine component, CSP-safe)
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (telemetry + recent-errors sections)
- Test: extend `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Interfaces:**
- Consumes: the real `<table>` in `TelemetryPanel.razor:37-87`.
- Produces: `<th>` sort buttons with `aria-sort` on the `<th>` (valid there), numeric sort keys via `data-sort-*` attributes on `<td>` or `<tr>`; sort state survives the 30s poll via the module-store pattern from `workspaceIndexFilter` (store keyed by panel id in `window.__millerTelemetrySortState` — declare the shape in `dashboard-site.js` only if needed; otherwise keep it inside `alpine-components.js` module scope).
- Recent-errors rows become a fixed grid: `time | tool | op | kind | duration` with the `view issue details` block on its own full-width row, so wrapping is deterministic.

**Contract inputs:** Task 1's morph semantics — after a poll swap the Alpine instance survives; re-apply sort in an element-scoped `htmx:afterSwap` listener (same pattern Task 1 adds to `workspaceIndexFilter`).

**File ownership:** `Components/TelemetryPanel.razor`, `wwwroot/js/alpine-components.js`, `wwwroot/dashboard.css` (telemetry sections), tests as listed.

**Serialization required:** Yes

**Dependency reason:** Shares `alpine-components.js` and CSS with Task 5.

**What to build:** Click-to-sort on Calls / Avg / p95 / Max / Errors / Est tokens (numeric desc first), Tool (alpha), and a recent-errors list that lines up.

**Acceptance criteria:**
- [ ] Rendered telemetry table carries sort buttons with `aria-sort` on `<th>` and numeric `data-sort-*` keys.
- [ ] Recent-errors entries render the fixed grid classes.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 7: Notice toasts, non-navigating Cancel, CSS-driven theme label

**Files:**
- Modify: `src/Miller.Dashboard/Components/WorkspaceIndex.razor` (notice paragraph gains `data-notice-tone` so JS can mirror it as a toast)
- Modify: `src/Miller.Dashboard/Components/WorkspaceRemoveConfirm.razor` (Cancel becomes `<button type="button" class="subtle-link" data-close-details>Cancel</button>`; drop the `CancelHref` parameter and both call-site arguments — locate with `grep -rn "CancelHref" src/`)
- Modify: `src/Miller.Dashboard/Components/WorkspacesShell.razor` + `Components/WorkspaceShell.razor` (theme button: two spans `theme-label-dark`/`theme-label-light`, no SSR-guessed single label)
- Modify: `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` (delegated `data-close-details` click handler; on DOMContentLoaded mirror a present notice into `showDashboardToast`; drop `updateThemeButton` label writes in favor of CSS, keep `aria-pressed` reflection if trivial)
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (theme-switch label visibility per `html[data-theme]`)
- Test: extend `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

**Interfaces:**
- Consumes: `showDashboardToast(message, tone)` (exists), `NoticeMessage`/`NoticeIsError` (WorkspaceIndex).
- Produces: `data-close-details` — generic delegated control that closes the closest `<details>`; theme button label switches purely via CSS on `html[data-theme]`.

**Contract inputs:** Theme stamping contract: `theme-init.js` sets `data-theme` pre-paint; the CSS label switch must key off that attribute only.

**File ownership:** `Components/WorkspaceRemoveConfirm.razor`, `Components/WorkspaceIndex.razor` (notice attr), both shells (theme spans), `wwwroot/js/dashboard-site.js`, `wwwroot/dashboard.css` (theme section), tests as listed.

**Serialization required:** Yes

**Dependency reason:** Shares shells, `WorkspaceIndex.razor`, `dashboard-site.js` with earlier tasks.

**What to build:** Outcome notices that survive being missed (toast mirrors the inline notice), a Cancel that just closes the confirm instead of reloading the page, and a theme label with no first-paint flash.

**Acceptance criteria:**
- [ ] Cancel button renders with `data-close-details` and no `href`; remove-confirm still posts correctly.
- [ ] Notice paragraph carries the tone attribute; toast mirroring is wired in JS.
- [ ] Theme button renders both labels; CSS shows exactly one per theme.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 8: Trends time axis

**Files:**
- Modify: `src/Miller.Dashboard/DashboardData.cs:231-241` (`DashboardTrendSeries` gains `FirstRecordedAtUtc`/`LatestRecordedAtUtc` nullable strings)
- Modify: `src/Miller.Dashboard/DashboardIndexFactsReader.cs:40-90` (populate from `MetricHistoryTrendPoint.RecordedAtUtc` — verify the point record's actual property name via `grep -n "record MetricHistoryTrendPoint" src/`)
- Modify: `src/Miller.Dashboard/Components/WorkspaceTrendsPanel.razor` (render window bounds under the sparkline scale: `AbsoluteShort(first)` → `AbsoluteShort(latest)`)
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (trends section, if spacing needs it)
- Test: extend the existing trends tests (locate via `grep -rln "DashboardTrendSeries\|WorkspaceTrendsPanel" tests/`)

**Interfaces:**
- Consumes: `metrics-history-v1` contract — `recorded_at_utc` is display metadata, order stays `snapshot_id` (do NOT re-sort by timestamp).
- Produces: additive fields on `DashboardTrendSeries` (appears in `snapshot.json` — additive, allowed).

**Contract inputs:** `AbsoluteShort` in `DashboardFormat.cs` for the label format.

**File ownership:** `DashboardData.cs` (trend records), `DashboardIndexFactsReader.cs`, `Components/WorkspaceTrendsPanel.razor`, `wwwroot/dashboard.css` (trends), tests as listed.

**Serialization required:** Yes

**Dependency reason:** Shares `DashboardData.cs` with Task 5.

**What to build:** Sparklines currently have no time anchor. Show the recorded window ("Jun 12, 10:00 UTC → Jul 16, 16:00 UTC") under each sparkline so "since first" means something.

**Acceptance criteria:**
- [ ] Series carry first/latest recorded timestamps when history has them; panel renders the window; series without timestamps render unchanged.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 9: Async refresh with progress

**Files:**
- Create: `src/Miller.Dashboard/DashboardRefreshJobs.cs`
- Modify: `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs` (POST `/fragments/refresh` starts a job and returns immediately; new GET `/fragments/refresh-status?workspace_id=` renders the stack per job state)
- Modify: `src/Miller.Dashboard/Components/RefreshStatusPanel.razor` + `Components/WorkspaceDetailPanel.razor` (in-progress state polls the status route every 2s targeting `#workspace-detail-stack` with `hx-swap="morph:outerHTML"`; terminal states render without the poll attribute so polling self-terminates)
- Test: Create `tests/Miller.Tests/Server/DashboardRefreshJobsTests.cs`; extend `DashboardMutationEndpointTests.cs`

**Interfaces:**
- Consumes: `DashboardData.TryRefreshWorkspace` (`DashboardData.cs:1417`), `WorkspaceRefreshResult`/`WorkspaceRefreshStatus` from `Miller.Server.Workspaces` (do not modify those types).
- Produces: `DashboardRefreshJobs.Start(workspaceId, Func<WorkspaceRefreshResult> refresh)` → returns existing running job or starts one; `DashboardRefreshJobs.Peek(workspaceId)` → `null` | `Running` | completed result; completed results are consumed (removed) when a status render observes them. Inject the refresh func so tests never spawn real refreshes.
- GET `/fragments/refresh-status` MUST be excluded from (or correctly interact with) Task 1's ETag caching — a Running status must never 304 forever; simplest is to skip the ETag middleware for this route.

**Contract inputs:** Task 4's `X-Miller-Dashboard` header requirement on POST `/fragments/refresh` stays; the new GET status route needs no header. JSON POST `/workspaces/{id}/refresh` stays synchronous (contract unchanged).

**File ownership:** `DashboardRefreshJobs.cs` (create), `Endpoints/DashboardEndpoints.cs` (refresh routes), `Components/RefreshStatusPanel.razor`, `Components/WorkspaceDetailPanel.razor`, tests as listed.

**Serialization required:** Yes

**Dependency reason:** Edits `DashboardEndpoints.cs` after Task 4.

**What to build:** The Refresh button currently blocks the HTTP request for the whole converge. Make it start a background job and let the page poll status, so long refreshes show progress and can't time out.

**Approach:** In-memory `ConcurrentDictionary<string, Lazy<Task<WorkspaceRefreshResult>>>`; duplicate Start for a running workspace returns the running job (no double refresh). `DashboardIndexFactsCache.Clear()` moves to job completion. Status renders: Running → "Refreshing… started Ns ago" + poll attr; Completed → existing `RefreshStatusPanel` result rendering, no poll attr.

**Acceptance criteria:**
- [ ] POST `/fragments/refresh` returns in <1s with in-progress markup (fake slow refresh func in test).
- [ ] Status route renders running state while incomplete and the terminal result exactly once after completion; second Start while running does not spawn a second refresh.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.

### Task 10: Responsive pass, hero-metrics grid, final visual sweep

**Files:**
- Modify: `src/Miller.Dashboard/wwwroot/dashboard.css` (broad: `.hero-metrics` auto-fit; `.detail-actions`, `.language-strip`, `.api-actions` wrap; `.stats-grid`/`.savings-summary`/`.fact-list` column collapse at ≤640px; ws-index mobile grid audit at ≤900px; any right-edge overflow found in screenshots)
- Modify: razor files ONLY if a layout cannot be fixed in CSS (report which)
- Evidence: screenshots at 1440/768/390, light + dark, `/` and `/workspace?workspace_id=<id>`, attached to the task report

**Interfaces:**
- Consumes: final markup from Tasks 1-9.
- Produces: no horizontal page overflow at 390px; hero metrics render as one balanced row on wide screens for both 3- and 4-metric pages.

**Contract inputs:** Screenshot harness: `MILLER_DASHBOARD_PORT=4999 dotnet run -c Release --project src/Miller.Dashboard`, headless Chrome (`/Applications/Google Chrome.app/Contents/MacOS/Google Chrome --headless=new --screenshot=... --window-size=<w>,<h> --hide-scrollbars <url>`); force themes by saving the page HTML with `data-theme` stamped and the theme-init script stripped (pattern from the review session) or rely on default + stamped copies.

**File ownership:** `wwwroot/dashboard.css` (broad), minor razor tweaks only if CSS cannot fix a layout, screenshot evidence.

**Serialization required:** Yes

**Dependency reason:** Must run last against final markup from all prior tasks.

**What to build:** `.hero-metrics` becomes `repeat(auto-fit, minmax(96px, 1fr))` (4 metrics fit one row on `/`, 3 on the workspace page). Then fix every ≤640px clip found in the review: detail-action rows wrap, language pills wrap, JSON nav wraps, metric bands collapse to 2 then 1 columns, no clipped right-aligned numerals.

**Acceptance criteria:**
- [ ] 390px screenshots of both pages show no right-edge clipping and no horizontal scroll.
- [ ] 1440px hero metrics render on one row on both pages, light and dark intact.
- [ ] Worker-scope verification passes and the change is committed per `serial-worker-commit`.
