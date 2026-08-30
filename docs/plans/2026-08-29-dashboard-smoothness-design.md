# Dashboard smoothness — design

Date: 2026-08-29
Status: approved, ready to implement
Branch: `worktree-dashboard-smoothness`

## Problem

The dashboard blinks and does not feel live. The user asked whether to replace htmx with Blazor
Server, since the dashboard runs locally and a WebSocket would be a local connection.

The answer is no. Measurement showed the dashboard is not a rendering-model problem. It is a
stalling problem plus one unscoped CSS rule. A Blazor Server circuit would leave the two largest
costs exactly where they are, reproduce the unkeyed-list defect under a different name (`@key`
instead of `id`), add per-tab server state, and force a reload in every open tab on every Miller
upgrade — `DashboardCliLauncher` deliberately replaces a dashboard running a different build.

## Evidence

Measured on the author's own registry (59 workspaces), warm, from the existing Release build.

| Request | Now | With `MILLER_INDEX_STORE=off` |
|---|---|---|
| `/fragments/workspaces` (polls every 30s) | 3.01 s | 0.013 s |
| `GET /` | 3.04 s | 0.078 s |
| `GET /workspace?workspace_id=…` | 2.86 s | 0.98 s |
| `/fragments/refresh-status` (polls every 2s) | 2.82 s, 102,646 bytes | — |
| `/fragments/activity` (polls every 5s) | 0.005 s | — |

The 3 seconds is pure CPU, confirmed by sampling `utime+stime` from `/proc/<pid>/stat` around three
requests (`wall=2.961s cpu=2.960s`). It is not an expired `busy_timeout`.

Two claims from the first analysis pass were **refuted** and must not be re-introduced:

- "Server-rendered `Ns ago` text defeats the fragment ETag." False. With a persistent cookie jar the
  ETag is stable and the conditional GET returns 304. The earlier measurement used a cookie-less
  `curl`, which mints a fresh antiforgery cookie per request and changes the ETag salt by design —
  exactly what `FragmentWorkspaces_WithADifferentAntiforgeryCookie_ReturnsFresh200WithADifferentETag`
  asserts. The waste is still real, because `FragmentETagAsync` renders the whole fragment before it
  can hash and compare, so a 304 still costs the full 3 seconds.
- "`WorkspaceTestsPanel` leaks its poll timer the same way `RefreshStatusPanel` does." False. `kt`
  calls `Pt` on the swap root **before** walking `Ht`, so a self-targeted swap re-inits the node and
  `De` clears the timer. Only an **ancestor-targeted** swap leaks. Do not change that panel's swap.

## Root causes and fixes

### C1 — the facts cache is bypassed in the default configuration

`DashboardIndexFactsCache.Read` returns at lines 24-25 before it reaches its own cache whenever
store mode is on, and store mode is the default. The class exists, in its own doc comment, "so the
landing page does not open every `symbols.db` on each request", and in the shipped configuration it
never runs. `MILLER_DASHBOARD_INDEX_CACHE_SECONDS` is inert for the same reason.

The bypass is **not** an accident. It was added by `d7743cd1` ("feat: surface family store
provenance") and is documented in `docs/findings/2026-08-09-index-store-ph3-acceptance.md:91-92`:
"Dashboard store facts bypass the legacy artifact timestamp cache, so current-generation changes
cannot be hidden behind an unchanged legacy file." It guards against the **wrong freshness signal**,
not against caching. Under store mode `<workspace>/.miller/symbols.db` is optional and frozen, so
`TryGetIndexWriteTicks` returns 0 or a constant and the freshness test is trivially true.

**Fix.** Cache in both modes, with a mode-appropriate witness.

- Legacy mode keeps the `symbols.db` write ticks it has always used.
- Store mode uses `WorkspaceReadSessionFactory.Probe(...)`. Probe shares `Open`'s branch, so it
  cannot disagree with the read. Its fast path is one pointer read plus one `freshness-stamp-<view>.json`
  read; its slow path opens `store.db` for three point queries with no temp projection and no level
  stamp scan. `StoreWorkspaceCoordinator.Submit` invalidates every stamp in the store root before any
  mutation, and a stamp is only rewritten from a real probe. Continuous testing already uses this
  same probe as its per-poll freshness key (`ContinuousTestRevisionPoller.cs:423-449`).
- A probe that cannot answer returns null and the caller reads uncached. An unreadable or unbound
  store is never served from cache.

Rejected witnesses, with reasons: freshness-stamp mtime alone (stamps are deleted as routine cache
work, so absence proves nothing); `store.db` mtime (the path changes on generation promotion and the
file is shared by every view in the family); `artifact_id` (a TEMP table filled with constants at
session open, constant per family, readable only through the expensive session); `PRAGMA data_version`
(reports changes one connection has not yet seen, and the dashboard opens a new connection per read).

**The witness goes in the stamp, not the key.** The cache key is identity only — `"store|"` or
`"legacy|"` plus `WorkspaceId`. Everything volatile goes in the stamp string: the probe fields, the
registry row's `LastRevision`, `State`, `LastScanAt` and `LastError`, and the sidecar file state.
This matters for three reasons found by the adversarial pass:

- `MarkScanned` writes `last_scan_at` on every converge even when the store sequence does not move,
  so a key that omits it would show a frozen `last_scan_at` in `/index.json`.
- Sidecar convergence can move `search.db` / `content.db` to `current` without moving the
  `store_log` sequence, and the facts copy that status.
- Creating a legacy export while store mode is on flips `legacy_preserved`/`native` and
  `available`/`export_required` without moving any store state.
- A key containing `LastRevision` leaks a dictionary entry on every revision bump and never releases
  a removed workspace. Identity-only keys do not.

**Raise the default TTL from 30 s to 120 s.** `/fragments/workspaces` polls every 30 s and the TTL is
30 s. With the two equal the entry expires almost exactly as the next poll arrives, so the cache
would hit rarely or never for the one fragment that costs 3.01 s. A longer TTL is more defensible
now than before, because the stamp is a true change witness: any store advance invalidates
immediately regardless of the TTL, and the TTL is only a backstop.

**Add deterministic per-workspace jitter** so all 59 entries do not expire in the same poll. Derive
it from the `WorkspaceId` hex string, **not** `string.GetHashCode` — .NET randomizes that per
process, so it is not deterministic across restarts.

**Honest scope.** This converts "3 s on every poll" into "3 s once per TTL window", not "3 s into
0.013 s". The 0.013 s figure is the store-mode-**off** number, a different code path (`ReadLegacy`
against `symbols.db`). A cache miss still pays the full cost for all 59 workspaces. It also does not
touch C2: `ReadSnapshot` opens its own store session and does not use this cache.

### C2 — a 102 KB response to fill an empty span, on a timer that never stops

`RefreshStatusPanel` polls `/fragments/refresh-status` every 2 s while a refresh job runs, with
`hx-target="#workspace-detail-stack"`. The endpoint returns `DetailStackResult` — all ten detail
panels, 102,646 bytes, 2.82 s — to update a one-line status span.

Worse, **the poll never stops.** htmx implements `every 2s` as a self-rescheduling `setTimeout`
chain (`ct`), with the element, handler, trigger spec and URL all captured in a closure at process
time. Nothing is re-read from the live attributes on a tick. The only `clearTimeout` is in `De`,
reached from `Pt` (init) when the attribute hash changes. After a swap, `kt` runs `Pt` on the swap
root and then on everything matching `Ht`'s selector — which requires an htmx attribute. The
terminal render strips every htmx attribute from the span, leaving only `id` and `class`, so it
matches nothing, `Pt` never fires on it, and `De` never runs. Idiomorph preserves the node by id, so
`bodyContains` stays true. The result: after one refresh completes, the browser keeps fetching a
102 KB / 2.82 s response every 2 seconds, forever, to update a span that renders empty because
`DashboardRefreshJobs.Peek` consumed the job on first observation.

`applyVisibilityPolling` is not a rescue: it selects `[data-poll-trigger]`, which the same swap
removed, and `htmx:afterSwap` fires before the settle tasks anyway.

**Fix.**

1. Self-target the poll: `hx-target="this"`, set **explicitly** (omitting it makes htmx walk
   ancestors via `re` and inherit someone else's target). Keep `hx-ext="morph"` and
   `hx-swap="morph:outerHTML"`. A self-targeted swap makes `kt` call `Pt` on the returned node
   itself, the attribute hash changes, and `De` clears the timer. The swap style is irrelevant to
   the stop; only the target is.
2. Return `RazorComponentResult<RefreshStatusPanel>` instead of `DetailStackResult`.
3. Return HTTP **286** on the terminal render as a second belt. htmx 2.0.4 honours it
   (`if(s.status===286){lt(o)}`) and cancels the **requesting** element. This also stops a second
   browser tab's poll, which today spins forever because `DashboardRefreshJobs` is process-global and
   only the first tab to poll sees the outcome.
   Note the trap: `De` deletes every internal-data key except `firstInitCompleted`, so a later `Pt`
   wipes `cancelled`. Never rely on 286 alone while the terminal markup still carries poll attributes.
   Here it is redundant with the self-target, which is the point.
4. Update the ten panels once when the job finishes, without erasing the outcome the reader just
   saw. Add `hx-trigger="miller:refresh-finished from:body"` plus its own `hx-get` on
   `#workspace-detail-stack`. `from:body` parses and resolves correctly in this bundle, and a
   non-poll trigger schedules no timer, so nothing can leak. Fire the event from the terminal
   response's `HX-Trigger` header — the header path **is** in this bundle
   (`if(R(s,/HX-Trigger:/i)){Je(s,"HX-Trigger",o)}`), runs before the swap, and bubbles from `body`.
5. **Preserve the outcome across that refetch.** `Peek` is exactly-once by contract, pinned by
   `Peek_AfterCompletion_ReturnsTheResultExactlyOnce`. Keep that contract. Add a separate
   non-consuming `DashboardRefreshJobs.PeekLastOutcome(workspaceId)` that retains the terminal
   result for 60 seconds, and have `DetailStackResult` render the status span from it. Without this
   the stack refetch re-renders `#refresh-status` empty and the outcome is unrecoverable.
6. Guard the empty selector: `hx-get="/fragments/detail-stack?workspace_id=…"` renders
   `workspace_id=` when nothing is selected, and `DetailStackResult` has no empty-id guard. Render
   the trigger attributes only when a workspace is selected.
7. Do **not** put `hx-ext="morph"` on `#workspace-detail-stack` without checking inheritance: the
   extension's `isInlineSwap` returns true for any unknown swap style, so it would be inherited by
   every panel inside. It is inert today (no `hx-swap-oob`, no `hx-preserve` anywhere), but state it
   deliberately or scope the extension to the panels that already declare it.
8. Rewrite the stale doc comment at `DashboardHostPipeline.cs:161-163`. It claims the running body
   "repeats verbatim between polls"; `Label` interpolates elapsed seconds, so it does not. Keep the
   ETag exclusion — the route's exactly-once semantics still make caching wrong.

### C3 — every automatic poll dims and freezes its own panel

`dashboard.css:1494-1498` sets an unscoped `.htmx-request { opacity: 0.5; pointer-events: none; }`.
htmx adds that class to the **requesting** element (`Zt`: `let t=we(e,"hx-indicator");if(t==null){t=[e]}`),
and every polled panel is its own requester. No component sets `hx-indicator`. Opacity is not in the
transition list at `dashboard.css:102-113`, so it is an instant step down and back.

On the home page this is not a blink. `/fragments/workspaces` takes 3.01 s, so the Workspaces panel
sits at half opacity and ignores every click — filter box, sort headers, rows, Remove control — for
about 3 seconds out of every 30. On the detail page the fragments answer in single-digit
milliseconds, so it is an irregular single-frame flash.

**Fix.** Delete the unscoped rule. Keep the label swap at `dashboard.css:1603-1611` — it is already
scoped to `.refresh-button` and does not depend on the deleted rule. Replace with a busy cue scoped
to the controls the reader actually presses, covering both `.refresh-button.htmx-request` and
`.refresh-button:disabled` (`hx-disabled-elt` sets a real `disabled` attribute). Include the
`:hover` pair — without it `dashboard.css:283-290` repaints an inert button in the accent colours as
soon as the pointer rests on it.

Restore the double-submit guard that `pointer-events: none` was silently providing: add
`hx-disabled-elt="this"` to the two user-clicked controls that lack it — the Open-folder button
(`WorkspaceDetailPanel.razor:23-27`) and the Telemetry Refresh button (`TelemetryPanel.razor:15-19`).

Do **not** use `hx-indicator=".live-dot"`. All three objections hold: only `ActivityFeedPanel` and
`WorkspaceTestsPanel` render a live dot, so `WorkspaceIndex` — the three-second panel — has none; a
bare selector resolves document-wide in htmx 2.0.4 unless prefixed with `find `/`closest `/`next `,
so the workspaces poll would dim the activity panel's dot; and `.live-dot` already runs
`animation: pulse 2s infinite` on opacity, and animation declarations outrank normal author
declarations, so the cue would be nearly invisible.

Add a stylesheet guard test so the unscoped rule cannot come back.

### C4 — client-side churn

- `updateRelativeTimes` writes `textContent` unconditionally on every element on every pass, on a 5 s
  interval over the whole document and on every swap. A value in the `m ago` bucket changes once a
  minute, so 11 of every 12 ticks rewrite identical text. It also runs on a hidden tab, and
  rewriting a text node collapses any selection inside it. Fix: compare before writing, and skip the
  interval while `document.hidden`. Reuse the existing `visibilitychange` listener at line 421 to
  repaint on return. Do **not** put the `hidden` guard inside `updateRelativeTimes` itself — the
  `afterSwap` and `DOMContentLoaded` callers must still repaint.
- `htmx:afterSwap` calls `rehydrateSortableTables()` and `applyVisibilityPolling()` for every swap of
  every panel. Once the reader clicks a sort header, `applyTableSort` runs `grid.appendChild(row)`
  for all 59 rows — detaching and reinserting each one — on every 5 s activity poll. Fix: scope the
  rehydrate to the swapped subtree (`event.target`). Keep the function name;
  `DashboardActivityFeedTests.cs:967` asserts on it.

### C5 — unkeyed list rows

Repeated rows carry no `id`, so idiomorph pairs them positionally. The activity feed is a
newest-first sliding window, so one new entry rewrites every visible row's text, and inside the
420 px scroller the content shifts under the reader.

**Fix.** Give every repeated row a stable `id` from an identity field already in scope — e.g.
`id="activity-@entry.Id"` on the activity `<li>` (the value is already used at
`ActivityFeedPanel.razor:62` as `data-issue-id`). Do the same for the workspace index rows,
telemetry rows, tests case rows, pattern rows and trend rows. Confirm each identity field exists on
the model in `DashboardData.cs` before using it.

Also set `Idiomorph.defaults.ignoreActiveValue = true` so the workspace filter input is not wiped
mid-typing on the 30 s poll.

## Non-goals

- No framework change. htmx, idiomorph, static SSR and the one-JS-file rule all stay.
- No change to `ReadSnapshot`'s per-view session (`DashboardData.cs:1109-1126`). The ~1 s detail-page
  read is real and out of scope; record it, do not fix it here.
- No push/SSE channel and no change beacon. Those were evaluated and deferred.
- No new MCP tool.

## Acceptance criteria

- [ ] No automatic poll dims, fades, or disables its own panel. A stylesheet guard test fails if an
      unscoped `.htmx-request` rule returns.
- [ ] `.refresh-button.htmx-request` and `.refresh-button:disabled` both show a busy cue, and hovering
      an inert button does not repaint it in the accent colours.
- [ ] The Open-folder and Telemetry Refresh buttons carry `hx-disabled-elt="this"`.
- [ ] `DashboardIndexFactsCache` serves from cache in store mode, keyed on identity, witnessed by
      `WorkspaceReadSessionFactory.Probe`. A probe failure reads uncached.
- [ ] A store advance (new manifest generation or `store_log` sequence) invalidates the entry
      immediately, regardless of TTL. A test proves it with a real store.
- [ ] A cached legacy entry is never returned to a store-mode read for the same registry row.
- [ ] The default TTL is 120 s and per-workspace jitter is derived from `WorkspaceId`, not
      `string.GetHashCode`.
- [ ] `/fragments/refresh-status` returns only the status span, never the ten-panel stack.
- [ ] The refresh-status poll stops when the job reaches a terminal state. A test proves the terminal
      render carries no poll attributes **and** that the running render self-targets.
- [ ] The terminal response returns 286. `DashboardMutationEndpointTests.cs:264` is updated — it
      asserts `HttpStatusCode.OK` today and will fail otherwise. Note that 286 passes
      `EnsureSuccessStatusCode`, so a test that only calls that proves nothing about the stop.
- [ ] The ten detail panels update once when a refresh finishes, and the refresh outcome is still
      readable afterwards.
- [ ] `updateRelativeTimes` writes only when the label changed, and does no work on a hidden tab.
- [ ] `rehydrateSortableTables` runs only for the swapped subtree.
- [ ] Every repeated row carries a stable `id`.
- [ ] `dotnet build Miller.slnx -c Release` is 0 warnings / 0 errors.
- [ ] The fast suite passes (baseline: 9233 passed, 0 failed, 9 skipped).

## Work split and file ownership

Each lane owns its files exclusively. No lane edits another lane's files.

| Lane | Source files | Test file |
|---|---|---|
| A — busy cue | `wwwroot/dashboard.css`, `Components/WorkspaceDetailPanel.razor`, `Components/TelemetryPanel.razor` | new `Server/DashboardStylesheetGuardTests.cs` |
| B — client churn | `wwwroot/js/dashboard-site.js` | new `Server/DashboardSiteScriptTests.cs` |
| C — facts cache | `DashboardIndexFactsCache.cs` | `Server/DashboardRegistryReadTests.cs` |
| D — refresh poll | `Components/RefreshStatusPanel.razor`, `Components/WorkspaceDetailStack.razor`, `Endpoints/DashboardEndpoints.cs`, `DashboardRefreshJobs.cs`, `DashboardHostPipeline.cs` | `Server/DashboardMutationEndpointTests.cs` |
| E — stable ids | `Components/ActivityFeedPanel.razor`, `Components/WorkspaceIndex.razor`, `Components/WorkspaceTestsPanel.razor`, `Components/PatternInventoryPanel.razor`, `Components/WorkspaceTrendsPanel.razor` | `Server/DashboardActivityFeedTests.cs` |

Lane C must first promote the private `StoreFixture` in
`tests/Miller.Tests/Indexing/FamilyStoreReadSessionTests.cs:2254` to a shared test helper so a
store-mode cache test can build a real store.

The test suite runs with `MILLER_INDEX_STORE=off` (`tests/Miller.Tests/test.runsettings:5`), so any
store-mode test must pass `storeEnabled: true` explicitly.

## What external review corrected

- **The stamp did not witness every fact it cached.** The facts summarize EVERY view in the family
  store — `StoreMemberSummaryReader` runs `SELECT COUNT(*) FROM views` and labels the whole table —
  and registering or retiring any other workspace moves no single view's manifest generation,
  manifest hash or store-log sequence. The member count and labels went stale for a whole TTL.
  `DashboardStoreViewsWitness` now reads that table's identity once per store root per 2 s and the
  stamp carries it, alongside the producer binary version. Reproduced as a failing test first.
- **The detail-stack refetch could erase a newer refresh.** The refetch is triggered by one refresh
  finishing but arrives a round trip later, and it rendered the retained outcome unconditionally. A
  refresh the reader started inside that window had its running panel morphed away, poll attributes
  and all, so it never reported. `DashboardRefreshJobs.PeekRunning` — a non-consuming, running-only
  read — now outranks the retained outcome.
- **The cache had no bound.** An entry is replaced only when its own workspace is read again, so a
  workspace removed through the CLI or MCP held its fact graph for the life of the process. It now
  trims the least recently cached entries past 256.

## Follow-ups, recorded not fixed

- The detail page still takes about 1 s per view with the store path out of the picture.
  `ReadSnapshot` is slow for reasons beyond this cache.
- There is no `hx-boost`, so every navigation between the home and detail pages is a full document
  load (3.04 s and 2.86 s measured).
- `/fragments/dashboard` (`DashboardEndpoints.cs:115`) and `DashboardContent.razor` are dead — no
  element references them.
- The author's registry holds 59 workspaces of which 41 read unreadable and 29 roots are gone, mostly
  dead `.claude/worktrees`. `workspace prune` shrinks the page and the render cost today.
- `MILLER_DASHBOARD_INDEX_CACHE_SECONDS` was inert in the default configuration until lane C.
