# Static SSR, Minimal API, htmx, and Alpine Pattern

Miller uses this stack for small, local-first web surfaces where the server should remain the source of truth and the browser should not become a SPA runtime. The current reference implementation is **`Miller.Dashboard`** — the loopback workspace/telemetry UI shipped beside the `miller` binary.

This document is a **code pattern**, not a product spec. It describes how we wire static SSR Razor components, Minimal API endpoints, htmx fragment swaps, and Alpine CSP for ephemeral client state.

## Baseline

Use this stack when building:

- Local or loopback operator UIs (dashboard, status pages, small admin surfaces).
- Read-mostly views with a few explicit mutations (refresh, converge, approve).
- Surfaces that must ship as vendored `wwwroot` assets with **no npm bundler** and **no CDN** at runtime.

Core ingredients:

- ASP.NET Core **static SSR** Razor Components.
- **Minimal API** routes that return `RazorComponentResult<TComponent>` HTML fragments.
- **htmx** for every server round-trip the browser initiates.
- **Alpine CSP build** for ephemeral client-side state only.
- App-owned CSS under `wwwroot` (Miller Dashboard uses `dashboard.css`, not a shared component library).
- Vendored browser libraries under `wwwroot/lib`; no Blazor Server circuits, Blazor WASM, MVC views/controllers, or browser JSON **write** endpoints for UI mutations.

Miller also exposes **read-only JSON** beside some HTML surfaces (for example `workspaces.json`, `telemetry.json` on the dashboard). That is an ops/scripting contract, not the browser UI transport — interactive regions still swap HTML.

Mental model:

```text
Route or fragment endpoint
  -> Razor component (shell or fragment)
  -> read model / service (DashboardData, component model, domain service)
  -> SQLite / registry / artifact readers
```

Browser UI sends forms or htmx requests and receives HTML. Persistence and business rules stay in C# services. JavaScript is glue, not the application runtime.

## Miller Reference Implementation

Primary example: **`src/Miller.Dashboard/`**

| Area | Path | Role |
|---|---|---|
| Host wiring | `Program.cs` | Kestrel, static asset routes, `MapDashboardEndpoints` |
| HTML + fragment routes | `Endpoints/DashboardEndpoints.cs` | `MapGet` pages, `/fragments/*`, read-only JSON |
| Page shells | `Components/WorkspacesShell.razor`, `WorkspaceShell.razor` | Full-document first paint |
| Fragments | `Components/*Panel.razor`, `WorkspaceDetailStack.razor` | htmx swap targets |
| Read models | `DashboardData.cs`, `DashboardIndexFactsReader.cs` | Aggregate reads; no full index hydration |
| Head assets | `Components/DashboardHead.razor` | htmx config, theme-init, CSS |
| Footer scripts | `Components/DashboardScripts.razor` | site JS + Alpine load order |
| Site glue | `wwwroot/js/dashboard-site.js` | Delegated clicks, htmx error toasts, relative times |
| Alpine factories | `wwwroot/js/alpine-components.js` | `Alpine.data(...)` registrations |
| Pre-paint theme | `wwwroot/js/theme-init.js` | Blocking head script |
| Vendored libs | `wwwroot/lib/htmx/`, `wwwroot/lib/alpine/` | htmx + CSP Alpine build |

Tests that lock the rendered contract:

- `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs`
- `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs`

## Responsibilities

| Concern | Owner | Rule |
|---|---|---|
| Browser route / first paint | Page shell or `@page` route | Render real content on GET. No htmx-on-load bootstrap. |
| Dynamic region | Razor fragment | No `@page`; `[Parameter]` inputs; reusable from shell and endpoint. |
| Fragment load | Minimal API GET | `RazorComponentResult<TComponent>`, not JSON. |
| Mutation | Minimal API POST | Call service; return refreshed HTML fragment. Antiforgery when auth matters. |
| Business invariant | Service / read model | Validation, mutations, transactions — not in Razor. |
| Authorization | Host policy (when used) | Loopback dashboard today has no auth; future surfaces document policy at route. |
| Page/fragment data | Read model or component model | Keep endpoints thin; compose in `DashboardData` or dedicated builders. |
| Server round-trip | htmx | `hx-get` / `hx-post`, explicit target/swap. |
| Ephemeral client state | Alpine | Filters, toggles, wizard steps, modal-local UI. |
| Cross-cutting browser glue | `dashboard-site.js` (or `site.js`) | Delegated handlers, theme toggle, copy, htmx error surfacing, poll pause when tab hidden. |

## Page Shells vs `@page` Routes

Two valid shapes appear in Miller:

### A. Shell components from `MapGet` (dashboard today)

`Miller.Dashboard` does **not** use `MapRazorComponents<App>()`. Full pages are self-contained shell components returned directly from Minimal API routes:

```csharp
endpoints.MapGet("/", () =>
    new RazorComponentResult<WorkspacesShell>(new { Index = ..., Activity = ..., Telemetry = ... })
    {
        PreventStreamingRendering = true,
    });
```

`WorkspacesShell.razor` and `WorkspaceShell.razor` include `<!doctype html>`, `<head>` (`DashboardHead`), body content, and footer scripts (`DashboardScripts`). First paint is complete HTML.

Use this for **small, standalone loopback tools** with a handful of routes.

### B. `@page` routes under `Components/Pages` (larger apps)

For multi-screen authenticated apps, use routeable Razor pages with `@page`, a shared layout, and `MapRazorComponents<App>()`. The same fragment and htmx rules apply; only the first-paint wiring differs.

Either shape must deliver **real first paint without htmx**. Do not ship an empty shell that fetches the screen on load.

**Async lifecycle guard (shape B only).** If a route page loads its model in `OnInitializedAsync` with a real async I/O call, keep `@if (_model is null)` before rendering children. Static SSR can process an intermediate render before the awaited work completes; without the guard, a child dereferencing a null model returns 500. This stays hidden when tests use synchronous in-memory fixtures — use nullable model fields and guard in production pages.

## Razor Fragments

Fragments live under `Components/` (group by feature when a surface grows). They should:

- Avoid `@page`.
- Accept explicit `[Parameter]` inputs.
- Render encoded Razor markup.
- Carry htmx attributes for server-driven updates.
- Include antiforgery hidden fields when embedded in authenticated forms.
- Be safe to render from both a page shell and a `/fragments/...` endpoint.

Dashboard examples:

- `ActivityFeedPanel` — `hx-get` poll with `data-poll-trigger` + visibility pause in site JS.
- `TelemetryPanel` — same polling pattern; optional columns hidden on narrow viewports via CSS.
- `WorkspaceDetailPanel` — `hx-post` refresh swapping `#workspace-detail-stack`.
- `WorkspaceIndex` — Alpine `x-data="workspaceIndexFilter"` for client-only row filtering.

Canonical mutation trigger (dashboard refresh):

```razor
<button type="button"
        class="refresh-button"
        hx-post="/fragments/refresh?workspace_id=@Esc(Facts.WorkspaceId)"
        hx-target="#workspace-detail-stack"
        hx-swap="outerHTML"
        hx-disabled-elt="this">
    Refresh index
</button>
```

Keep real `method` and `action` on forms when you add them. htmx enhances HTML; it should not be the only thing making a control understandable.

## Endpoint Groups

Group feature routes in `Endpoints/<Feature>Endpoints.cs` with a `Map<Feature>Endpoints` extension. Dashboard pattern:

```csharp
internal static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(
        IEndpointRouteBuilder endpoints,
        DashboardPaths paths,
        string launchDirectory)
    {
        endpoints.MapGet("/", () => new RazorComponentResult<WorkspacesShell>(...) { PreventStreamingRendering = true });
        endpoints.MapGet("/fragments/activity", (string? workspace_id) =>
            new RazorComponentResult<ActivityFeedPanel>(...) { PreventStreamingRendering = true });
        endpoints.MapPost("/fragments/refresh", (string workspace_id) =>
            new RazorComponentResult<WorkspaceDetailStack>(...) { PreventStreamingRendering = true });
    }

    public static void MapDashboardJsonEndpoints(...) { /* read-only ops JSON */ }
}
```

`Program.cs` maps static assets (`/dashboard.css`, `/js/*`, `/lib/*`) then calls the endpoint extensions. Keep HTML fragment routes and JSON export routes visually separated when both exist.

## Rendering Fragments

GET handlers and POST mutation results return `RazorComponentResult<TComponent>`:

```csharp
return new RazorComponentResult<WorkspaceDetailStack>(new
{
    Snapshot = snapshot,
    Activity = activity,
    RefreshResult = result,
})
{
    PreventStreamingRendering = true,
};
```

Always set `PreventStreamingRendering = true` on fragment results. If a component (or ancestor) ever gains `[StreamRendering]`, static SSR can emit `<blazor-ssr>` placeholders that require `blazor.web.js` — this pattern deliberately does not load the Blazor runtime. The flag keeps fragments arriving as final HTML.

## POST Mutation Flow

When a surface performs mutations:

1. Validate antiforgery (skip only for deliberate loopback-only tools with no cookie session — document the exception).
2. Read form or route parameters.
3. Call the service / read model chokepoint.
4. Return the refreshed fragment (or a focused error fragment).
5. Never mutate when validation or authorization fails.

Dashboard refresh follows step 3–4 via `DashboardData.TryRefreshWorkspace`, clears `DashboardIndexFactsCache`, re-reads snapshot/activity, and swaps `WorkspaceDetailStack`.

Validation and expected domain failures should return **HTML with visible feedback**. Unexpected infrastructure failures should log and surface via global htmx error handlers (see below).

## Antiforgery

For authenticated browser surfaces:

- Every server-rendered form includes `__RequestVerificationToken`.
- POST endpoints validate antiforgery before mutation.
- Tests assert missing/invalid token fails without mutating.
- Non-form htmx POST triggers include the token via `hx-include` on a containing form, or isolate the trigger in its own form.

`Miller.Dashboard` is **loopback-only** with no cookie session, so its htmx-triggered POSTs — `POST /fragments/refresh`, `POST /workspaces/{workspace_id}/open-folder`, and the JSON `POST /workspaces/{workspace_id}/refresh` — carry no antiforgery token. They are not unguarded: each **requires the `X-Miller-Dashboard: 1` request header** and answers `400` without it. A cross-origin `<form>` cannot set a custom header at all, and a cross-origin `fetch` that sets one becomes a CORS preflight the dashboard never answers — so the header proves the caller is the dashboard's own page. `dashboard-site.js` attaches it to every htmx request from one `htmx:configRequest` listener; any non-browser caller of the JSON refresh POST must send it too. The registry-lifecycle form posts (`/workspace/remove`, `/workspaces/prune`) are real forms and stay antiforgery-validated (ADR-0002).

Header checks substitute for antiforgery only because there is no ambient credential to steal — the guard stops a cross-origin page from driving the local dashboard, nothing more. On an authenticated surface, validate antiforgery; do not copy this pattern.

The same pipeline refuses any request whose `Host` is not a loopback name (`localhost`, `127.0.0.1`, `[::1]`) with `403`, before routing — a DNS-rebinding guard, since same-origin policy would otherwise hand an attacker's rebound domain every dashboard read. Port is not checked: any port reaching the process is the one it bound.

Shared validation helper pattern:

```csharp
internal static async Task<IResult?> ValidateAntiforgeryAsync(
    IAntiforgery antiforgery,
    HttpContext httpContext)
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
        return null;
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Antiforgery token validation failed.");
    }
}
```

## htmx Rules

htmx owns server-initiated round-trips.

Use htmx for:

- Mutations (refresh, save, delete).
- Fragment refresh and polling (`hx-trigger="every Ns"` — pause when tab hidden).
- Dependent lookups and server-side filtering.
- Feedback region swaps.

Do not use htmx for:

- Local visibility toggles.
- Client-only filtering of already-rendered rows (use Alpine).
- Wizard step navigation that does not need server truth.

Important rules:

- Return **HTML**, not JSON, for browser UI updates.
- Use `hx-get` for idempotent reads; `hx-post` for mutations.
- Set `hx-target` and `hx-swap` explicitly.
- Use `hx-disabled-elt` on mutation triggers (`this` when the button posts; `find button[type='submit']` on forms).
- Set `allowEval=false` in htmx config (see `DashboardHead.razor`).
- Avoid `hx-on`, `hx-vals js:`, and eval-dependent trigger filters.
- Use `data-poll-trigger` + site JS to strip `hx-trigger` while `document.visibilityState === 'hidden'`.

Polling example (activity feed):

```razor
<section id="activity-feed-panel"
         hx-get="/fragments/activity?workspace_id=@Esc(Feed.WorkspaceId)"
         data-poll-trigger="every 5s"
         hx-trigger="every 5s"
         hx-swap="outerHTML">
```

## Alpine Rules

Alpine owns **ephemeral** state. If another request, user, or database row must see the value, submit it through htmx and process server-side.

Use Alpine for:

- Client-only filters (`workspaceIndexFilter` on the workspace table).
- Show/hide sections and modal-local UI.
- Pre-submit repeaters and wizard step state (larger forms).

Do not use Alpine for:

- Fetching or saving server data.
- Rendering server data from JSON APIs.
- Replacing service validation.

### Registered components

Register factories in `wwwroot/js/alpine-components.js` before loading Alpine core:

```javascript
document.addEventListener('alpine:init', function () {
    Alpine.data('workspaceIndexFilter', function () {
        return {
            query: '',
            onInput: function (event) {
                this.query = event.target.value;
                this.applyFilter();
            },
            applyFilter: function () {
                var q = (this.query || '').trim().toLowerCase();
                this.$el.querySelectorAll('.ws-index-row').forEach(function (row) {
                    var text = row.textContent.toLowerCase();
                    row.hidden = q.length > 0 && text.indexOf(q) < 0;
                });
            },
        };
    });
});
```

Markup:

```razor
<section id="workspace-index" x-data="workspaceIndexFilter">
    <input type="search"
           x-bind:value="query"
           x-on:input="onInput($event)" />
    ...
</section>
```

In Razor, prefer longhand `x-on:` and `x-bind:` — `@click` / `:value` shorthand collides with Razor.

### CSP build constraints

Miller ships the **Alpine CSP build** (`wwwroot/lib/alpine/cspalpine.min.js`). Inline `x-*` attribute expressions are limited (no arrow functions, destructuring, or globals). Complex logic lives in `alpine-components.js` factory bodies, which are normal external JavaScript.

Prefer `x-bind:value` + `x-on:input` over `x-model` for consistency across input types.

### JSON islands

Seed Alpine state from server-rendered JSON when needed:

```razor
<script type="application/json" data-workflow-initial>
    @((MarkupString)registryJson)
</script>
```

Serialize with `System.Text.Json`. Do not hand-build JSON strings in Razor.

## htmx And Alpine Together

- Put the Alpine root around local state.
- Put htmx targets inside that root when swapped content must reuse the same local state pattern.
- Let htmx replace child fragments; Alpine auto-initializes `x-data` in swapped markup when the factory was registered at startup.

Do not add a blanket `htmx:afterSwap -> Alpine.initTree(...)` hook unless a browser test proves a specific swap mode needs it — it can double-initialize.

`alpine:init` fires once at startup. Fragments loaded later must reference factories already registered in `alpine-components.js`.

## Asset And CSP Setup

Miller Dashboard head (`DashboardHead.razor`):

```razor
<meta name="htmx-config" content='{"selfRequestsOnly":true,"allowEval":false}'>
<link rel="stylesheet" href="/dashboard.css">
<script src="/lib/htmx/htmx.min.js"></script>
<script src="/js/theme-init.js"></script>
```

Footer (`DashboardScripts.razor`):

```razor
<div id="dashboard-toast-container" class="dashboard-toast-container" aria-live="polite"></div>
<script src="/js/dashboard-site.js"></script>
<script defer src="/js/alpine-components.js"></script>
<script defer src="/lib/alpine/cspalpine.min.js"></script>
```

Load order:

1. htmx (blocking, not deferred).
2. `theme-init.js` (blocking pre-paint theme).
3. Body content.
4. Site glue JS.
5. Alpine component registrations (`defer`).
6. Alpine CSP core (`defer`).

CSP posture for strict surfaces:

- `script-src 'self'`.
- No executable inline scripts in Razor components.
- No inline `on*=` handlers — use `data-*` attributes and delegated listeners in site JS.
- No `unsafe-eval`; htmx `allowEval=false`; Alpine CSP build.
- `<script type="application/json">` data islands are allowed.

Map JS and font assets explicitly from `Program.cs` (or `UseStaticFiles` with a known web root). The dashboard copies `wwwroot/**` to the build output via `Miller.Dashboard.csproj`.

## Service Boundaries

Endpoints and Razor components should not own reusable domain behavior.

- **`Miller.Core`** — pure logic, zero I/O (ranking, resolver contracts). Never query SQLite from Razor.
- **Read models** (`DashboardData`, future component-model services) — compose aggregate queries for fragments.
- **Infrastructure services** — mutations, subprocesses, registry writes.

Endpoint-local helpers are fine for HTTP parsing and view-model assembly. Business rules that another entry point could bypass must live in shared services.

## Error Handling

Split:

- Expected validation/domain failure → HTML fragment with visible message (200 + feedback).
- Missing antiforgery → `400`, no mutation.
- Unauthorized → framework forbidden/unauthorized, no mutation.
- Infrastructure failure → log; surface via global htmx handlers.

**htmx does not swap on non-2xx responses.** Without global handlers, a failed refresh looks like a dead button. `dashboard-site.js` registers:

```javascript
document.body.addEventListener('htmx:responseError', function (event) {
    var status = event.detail && event.detail.xhr ? event.detail.xhr.status : 0;
    var msg = status === 400
        ? 'Request was rejected. Refresh the page and try again.'
        : 'Something went wrong. Your action was not saved.';
    window.showDashboardToast(msg, 'danger');
});

document.body.addEventListener('htmx:sendError', function () {
    window.showDashboardToast('Could not reach the dashboard server.', 'danger');
});

document.body.addEventListener('htmx:timeout', function () {
    window.showDashboardToast('The request timed out. Please try again.', 'danger');
});
```

### Cookie auth and htmx

If a future Miller surface uses cookie authentication, configure login redirects to return `401` for `HX-Request` instead of swapping the login page into a fragment target. Handle `401` in `htmx:responseError` with `window.location.assign(loginUrl)` or an `HX-Redirect` response header.

The loopback dashboard does not need this today.

## Accessibility With Swapped Fragments

- Mark toast/feedback hosts with `aria-live="polite"`.
- When a swap removes the focused element, focus may drop to `<body>` — prefer swaps that preserve focus targets or move focus deliberately.
- Pair slow `hx-post` actions with visible busy state (`htmx-request` class or `aria-busy` on the target region).

## Testing Pattern

Miller uses layered tests without requiring Playwright for every change.

### L1: Service and unit tests

Test business rules without HTTP — validation, read models, mutation semantics, error paths. Keep these in the fast `Category!=Scale` suite.

### L2: Component and endpoint contract tests

Render Razor components with `HtmlRenderer` and assert the **HTML contract**:

- Expected element ids and panel structure.
- `hx-get` / `hx-post` / `hx-target` / `hx-swap` attributes.
- `x-data` hooks when Alpine owns local state.
- Vendored script references (`/js/dashboard-site.js`, `/js/theme-init.js`) — not inline `onclick=`.
- No Blazor runtime script on static SSR surfaces.

Source-scan tests can assert route registration in `Endpoints/*.cs` and asset routes in `Program.cs`.

For POST mutations with antiforgery, assert valid token mutates, invalid token does not.

### L3: Browser characterization (optional)

Use Playwright only when JavaScript behavior is hard to prove from HTML alone: Alpine filter interaction after htmx swap, focus management, toast visibility on simulated `htmx:responseError`. No hard sleeps; assert real outcomes.

## Negative Regression Guards

Keep tests or source scans that make the architecture hard to regress:

- No MVC controllers or Razor views for these surfaces.
- No executable inline scripts or `onclick=` in Razor components.
- No Blazor interactive render mode or `blazor.web.js` on static SSR pages.
- First paint contains real content (not htmx load bootstrap).
- Global htmx error handlers present in site JS.
- Vendored htmx/Alpine paths served from `wwwroot/lib`.

`Miller.Dashboard` tests in `DashboardRegistryReadTests` and `DashboardActivityFeedTests` encode several of these checks.

## Checklist For A New Miller UI Surface

1. **Host**
   - `AddRazorComponents()`.
   - Map static assets from `wwwroot`.
   - `Endpoints/<Feature>Endpoints.cs` for HTML fragments and mutations.
   - Add antiforgery + auth when the surface is not loopback-only.

2. **Components**
   - Page shell or `@page` route for first paint.
   - Feature fragments under `Components/`.
   - `DashboardHead`-style shared head + `DashboardScripts`-style footer, or shared layout for `@page` apps.

3. **First paint**
   - GET route returns full useful HTML.
   - No htmx-on-load page bootstrap.

4. **htmx**
   - GET fragments for polls and partial refresh.
   - POST for mutations with explicit target/swap.
   - `hx-disabled-elt` on mutation controls.
   - Global `htmx:responseError` / `htmx:sendError` / `htmx:timeout` handlers in site JS.

5. **Alpine**
   - `alpine-components.js` factories for non-trivial local state.
   - Client-only filters and toggles — not server fetch.

6. **Tests**
   - Component HTML contract tests (fast suite).
   - POST/antiforgery tests when mutations are authenticated.
   - Playwright only if needed.

## Common Mistakes

- Returning JSON to browser UI because it feels easier — return HTML fragments.
- Putting domain validation only in an endpoint — another caller will bypass it.
- Hollow first paint that htmx fills on load — you lose SSR benefits.
- Using Alpine to fetch or save server data — htmx owns that.
- Inline `onclick=` or inline `<script>` blocks in Razor — use site JS + `data-*` delegation.
- Inline Alpine logic the CSP build cannot parse — move to `Alpine.data` factories.
- `hx-include` mistaken for payload filtering — use `hx-params` or isolated forms.
- Non-2xx htmx responses with no global error handler — dead buttons.
- Mutation buttons without `hx-disabled-elt` — double-submit risk.
- Copying dashboard's no-antiforgery refresh into an authenticated surface.

## When This Stack Stops Fitting

This pattern has an interactivity ceiling. Do not contort a client-heavy experience into htmx when the core UX is:

- Drag-and-drop boards or visual scheduling.
- Large virtualized grids (tens of thousands of rows, client-side sort/filter).
- Real-time collaborative views.
- Offline-first capture.
- Heavy canvas/chart editing.

A practical growth signal: `alpine-components.js` trending past ~1,500 lines means the surface is fighting the pattern.

### Agent ergonomics

This stack is favorable for agent-driven development in Miller:

- **Deterministic verification.** `dotnet test` renders components and scans route registration without a browser.
- **Behavior in text artifacts.** Razor, `hx-*` attributes, CSS, and a few hundred lines of vendored JS are diffable.
- **No client bundler.** Dashboard assets copy straight into release packages.

Escalation (whole-screen WASM island or full Blazor WASM) trades that loop for richer client interactivity — choose it when the UI requirement clearly exceeds htmx/Alpine, not by default.

## Decision Rule

When unsure:

- Needs server truth, persistence, authorization, or cross-user visibility? → **htmx** POST/GET HTML.
- Local, temporary, from already-rendered DOM? → **Alpine**.
- Business rule? → **service** (`Miller.Core` or infrastructure).
- Page/fragment data shaping? → **read model / component model** (`DashboardData`, etc.).
- Machine-facing API for scripts/agents? → explicit JSON routes (read-only exports OK); do not replace HTML fragment mutations with JSON writes from the browser UI.
