# Dashboard interaction cleanup — audit, decision, execution

Date: 2026-08-26
Scope: `src/Miller.Dashboard`
Campaign item 5 / TODO backlog entry "Dashboard cleanup pass".

The prompting question was: "if we've swapped to blazor for it, we don't need the htmx assets."
The audit below answers it — the dashboard never swapped to interactive Blazor, so the question
inverts. This document records what each stack actually carries, which one won, and what was
deleted.

## 1. What the dashboard actually is

`DashboardHostPipeline.ConfigureServices` calls `services.AddRazorComponents()` and nothing else.
There is no `AddInteractiveServerComponents`, no `AddInteractiveWebAssemblyComponents`, no
`@rendermode` on any component, and no `blazor.web.js` script tag anywhere in the tree. Razor
Components here is a **server-side template engine that emits static HTML**. It contributes zero
runtime interactivity: no circuit, no WebSocket, no WASM.

Every live behaviour on every page therefore comes from one of four sources:

- an **htmx** attribute,
- an **Alpine** directive,
- **plain SSR** (a link, a `<form method="post">`, a native `<details>`),
- **custom JS** in `wwwroot/js/dashboard-site.js` (delegated listeners, no framework).

## 2. Interaction inventory

### 2.1 htmx — 6 panels, 8 in-page interactions

| Panel / file | Interaction | Attributes |
|---|---|---|
| `ActivityFeedPanel.razor` | Live Activity feed poll | `hx-get /fragments/activity`, `hx-trigger every 5s`, `hx-ext morph`, `hx-swap morph:outerHTML` |
| `RefreshStatusPanel.razor` | Refresh-progress poll into the detail stack | `hx-get /fragments/refresh-status`, `hx-trigger every 2s`, `hx-target #workspace-detail-stack`, `hx-ext morph`, `hx-swap morph:outerHTML` |
| `TelemetryPanel.razor` | Telemetry poll | `hx-get /fragments/telemetry`, `hx-trigger every 30s`, `hx-ext morph`, `hx-swap morph:outerHTML` |
| `TelemetryPanel.razor` | Manual "Refresh" button | `hx-get /fragments/telemetry`, `hx-target #telemetry-panel`, `hx-swap morph:outerHTML` |
| `WorkspaceIndex.razor` | Workspace list poll | `hx-get /fragments/workspaces`, `hx-trigger every 30s`, `hx-ext morph`, `hx-swap morph:outerHTML` |
| `WorkspaceTestsPanel.razor` | Tests panel poll (merged to main 2026-08-26) | `hx-get /fragments/tests`, `hx-trigger every 5s`, `hx-ext morph`, `hx-swap morph:outerHTML` |
| `WorkspaceDetailPanel.razor` | "Refresh index" action | `hx-post /fragments/refresh`, `hx-target #workspace-detail-stack`, `hx-ext morph`, `hx-swap morph:outerHTML`, `hx-disabled-elt this` |
| `WorkspaceDetailPanel.razor` | "Open folder" action | `hx-post /workspaces/{id}/open-folder`, `hx-swap none`, `data-toast-success` |

Attribute census: 8 `hx-swap`, 6 `hx-get`, 6 `hx-ext`, 5 `hx-trigger`, 3 `hx-target`, 2 `hx-post`,
1 `hx-disabled-elt` = **31 htmx attribute uses**.

htmx is not only markup here. `dashboard-site.js` hangs **eight** behaviours off htmx's event bus,
and each one is load-bearing:

- `htmx:configRequest` — adds the `X-Miller-Dashboard: 1` CSRF header on every request (the
  server-side gate for the antiforgery-free POSTs) and attaches `If-None-Match` from the per-element
  ETag store.
- `htmx:afterOnLoad` — captures the fragment `ETag` into the module-scope store.
- `htmx:beforeSwap` — suppresses the swap on a `304` (htmx 2 would otherwise swap in an empty body)
  and captures open `<details>` state.
- `htmx:afterSwap` — re-humanizes timestamps, rebinds details state, reapplies visibility polling.
- `htmx:afterRequest` — success toast for `data-toast-success` actions.
- `htmx:responseError`, `htmx:sendError`, `htmx:timeout` — failure toasts.

The whole conditional-GET design (`FragmentETagAsync` in `DashboardHostPipeline`, the `304`
short-circuit, `DashboardFragmentCachingTests`) is built on htmx request semantics. Replacing htmx
would mean rewriting the fragment cache protocol, the CSRF header rule, and all seven panels.

### 2.2 Alpine — 2 components, 16 directives

| Component | File | Directives | What it does |
|---|---|---|---|
| `workspaceIndexFilter` | `WorkspaceIndex.razor` | `x-data`, `x-bind:value`, `x-on:input`, 5 × `x-on:click` | Client-side filter of `.ws-index-row`, client-side sort of 5 columns |
| `telemetryTableSort` | `TelemetryPanel.razor` | `x-data`, 7 × `x-on:click` | Client-side sort of 7 telemetry columns |

Both components are written in Alpine's **CSP subset**, which forbids inline expressions — every
directive is either a bare component name or a `foo($event)` call. The bodies live in
`wwwroot/js/alpine-components.js` and are plain imperative DOM code: `querySelectorAll`,
`row.hidden = …`, `grid.appendChild(row)`, `setAttribute('aria-sort', …)`. There is no reactive
template, no `x-show`, no `x-text`, no `x-for`, no two-way `x-model`.

Critically, **Alpine already owns none of the state**. Both components delegate to plain module-level
stores that `dashboard-site.js` declares (`window.__millerWorkspaceIndexState`,
`window.__millerTelemetrySortState`), because a morph swap rewrites DOM attributes and would clobber
anything parked on a node. Both components also bind `htmx:afterSwap` by hand inside `init()` to
reapply the user's view after a poll. So Alpine is supplying exactly three things: component
instantiation, `$root`, and event binding — all of which `dashboard-site.js` already does for nine
other behaviours through one delegated `click` listener.

### 2.3 Plain SSR — no JS at all

- Workspace remove: `<form method="post" action="/workspace/remove">` + `<AntiforgeryToken/>`,
  post-redirect-get (`WorkspaceRemoveConfirm.razor`, ADR-0002).
- Stale prune: `<form method="post" action="/workspaces/prune">` + `<AntiforgeryToken/>`
  (`WorkspaceIndex.razor`, ADR-0002).
- Navigation: `<a href="/workspace?workspace_id=…">` per row, `<a href="/workspaces.json">`.
- Metric/health/trend sparklines (`WorkspaceTrendsPanel.razor`): server-rendered inline `<svg>`,
  no script.
- Disclosure: native `<details>`/`<summary>` for the stale section, remove-confirm, and issue rows.

Neither stack touches these. They keep working whatever we delete.

### 2.4 Custom JS — 9 behaviours, framework-free

All in `dashboard-site.js`, all driven by `data-*` attributes and delegated listeners:

| Behaviour | Signal |
|---|---|
| Theme toggle | `[data-toggle-theme]` delegated click |
| Copy-to-clipboard buttons | `[data-copy-target]` delegated click |
| Cancel a remove-confirm | `[data-close-details]` delegated click |
| Relative timestamps, refreshed every 5s | `time.rel-ts[data-ts]` |
| Keep `<details>` open across a morph swap | `details[data-issue-details]` + `data-issue-id` |
| Mirror a post-redirect-get notice into a toast | `[data-notice]`, `[data-notice-tone]` |
| **Pause polling on a hidden tab** | **`[data-poll-trigger]`** ⇄ `hx-trigger` + `htmx.process()` |
| `/` focuses the workspace filter | `#workspace-filter` |
| Toast container / `showDashboardToast` | `#dashboard-toast-container` |

**What consumes the Tests panel's `data-poll-*` attributes:** `applyVisibilityPolling()` in
`dashboard-site.js`. Every polling panel emits `data-poll-trigger` alongside `hx-trigger`; when the
tab goes hidden the function strips `hx-trigger` and when it returns it restores the value from
`data-poll-trigger`, then calls `htmx.process(el)`. So `data-poll-trigger` is the *durable copy* of
the htmx trigger — it exists so a hidden tab can drop the attribute without losing the value. It is
htmx plumbing, not a separate stack.

### 2.5 Score

| Stack | Panels | Interactions | Event hooks | Bytes shipped |
|---|---|---|---|---|
| htmx (+ idiomorph `morph` extension) | 6 | 8 | 8 | 51 KB + 10 KB |
| Alpine (CSP build) | 2 | 2 (filter, sort) | 0 (borrows htmx's) | 62 KB + 12 KB |
| Plain SSR | all | 5 | — | 0 |
| Custom JS | all | 9 | — | 13 KB |

## 3. Decision

**htmx wins. Alpine is removed.**

The decision rule was: pick the stack that already carries the majority of live interactions and can
absorb the minority cheaply. The evidence is one-sided.

1. **htmx carries the majority.** 8 of 10 framework-served interactions, across 6 of the dashboard's
   panels, plus every polled fragment and both POST actions. Alpine carries 2, both on one page each.
2. **htmx cannot be absorbed cheaply; Alpine can.** Removing htmx would mean rewriting the fragment
   ETag/304 protocol, the CSRF header rule, seven poll loops and two POST actions in hand-written
   fetch code — a rewrite, not a cleanup. Removing Alpine means moving ~180 lines of already-plain
   DOM code into the delegated listener that `dashboard-site.js` already runs.
3. **Alpine was never doing Alpine's job.** No reactive template, no reactive state — its own state
   already lives in plain-JS module stores, and it already hand-binds `htmx:afterSwap`. Its only
   contributions are `x-data` instantiation, `$root`, and click binding, and the file it lives in
   already has a delegated click handler serving three other behaviours.
4. **Alpine costs the most bytes for the least work:** 74 KB (62 KB runtime + 12 KB components) vs
   htmx's 61 KB, for 2 interactions instead of 8. Dashboard `wwwroot` ships in all four release
   archives.
5. **Blazor is not a candidate.** The dashboard is a self-contained non-AOT executable that must stay
   local-first. Making it interactive means a Blazor Server circuit (a persistent WebSocket per
   viewer) or a WASM runtime download — both were explicitly out of scope, and neither is needed for
   two client-side table sorts.

**idiomorph stays.** It is not a third stack: it is the htmx extension that `hx-ext="morph"` names,
and all five polling panels depend on it to patch in place instead of tearing the subtree down
(which is what preserves scroll position, focus, and open `<details>` across a poll).

## 4. What was done

### Migrated (Alpine ➝ plain JS in `dashboard-site.js`)

| Was | Now |
|---|---|
| `x-data="workspaceIndexFilter"` on `#workspace-index` | Section found by `#workspace-index`; controller attached on load and re-attached after each swap |
| `x-bind:value="query"` on `#workspace-filter` | The store's query is written back to `input.value` during rehydrate |
| `x-on:input="onInput($event)"` | Delegated `input` listener on `document` matching `#workspace-filter` |
| 12 × `x-on:click="onSort($event)"` | One delegated `click` branch on `[data-sort-col]`, routed to the owning panel by `closest()` |
| `x-data="telemetryTableSort"` on `#telemetry-panel` | Panel found by `#telemetry-panel`; same attach/rehydrate path |
| `alpine:init` + `Alpine.data(...)` registration | Two plain controller objects in `dashboard-site.js` |
| Per-component `this.$root.addEventListener('htmx:afterSwap', …)` | The existing single `htmx:afterSwap` document listener rehydrates both panels |

Behaviour preserved exactly: filter text, auto-open of the stale section while filtering (and
restoring only what the filter auto-opened), the `.ws-filter-empty` note, per-grid sorting that
leaves the header row in place, numeric-vs-name default sort direction, `aria-sort` on the
`role="columnheader"` / `<th>` ancestor rather than the button, and survival of all of it across a
30 s morph poll via the same module-level stores.

### Deleted

| Path | Size |
|---|---|
| `src/Miller.Dashboard/wwwroot/lib/alpine/cspalpine.min.js` | 62 KB |
| `src/Miller.Dashboard/wwwroot/js/alpine-components.js` | 12 KB |

Plus the residue:

- the two `<script>` tags in `DashboardScripts.razor`,
- the `alpinePath` / `alpineComponentsPath` variables and the two `MapMethods` routes in
  `DashboardHostPipeline.Configure`,
- the four `test -f` / asset-list entries in `.github/workflows/release.yml`,
- the Alpine assertions in `DashboardActivityFeedTests` / `DashboardFragmentCachingTests` (replaced
  by guards that the dashboard ships **no** Alpine).

Release-packaging impact: ~74 KB removed from the dashboard `wwwroot` of each of the four platform
archives (`wwwroot` measured 268 KB before, 192 KB after).

### Kept

`htmx.min.js`, `idiomorph-ext.min.js`, `dashboard-site.js`, `theme-init.js`, `dashboard.css`, the two
vendored `.woff2` fonts, the `htmx-config` meta (`selfRequestsOnly`, `allowEval:false`), and every
`hx-*` / `data-poll-trigger` attribute.

## 5. Rule this establishes

The dashboard is **htmx + one plain-JS file**, on top of static-SSR Razor Components. Do not add
Alpine, a second client framework, or an interactive Blazor render mode. New interactive behaviour is
either an htmx fragment swap or a delegated `data-*` handler in `dashboard-site.js`. Recorded in
`CLAUDE.md` under the dashboard notes.
