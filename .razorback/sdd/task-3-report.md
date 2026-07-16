# Task 3 — Styled 404, version footer, JSON links open in new tab

**Status:** COMPLETE
**Commit SHA:** none — parallel-lead-commit (no `git add` / `git commit` run)
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes` @ `780b51d` (dirty — my owned files + Task 2's in-flight files)

> This file replaced a stale `task-3-report.md` from an earlier plan's numbering ("Theme tokens via
> light-dark() + contrast", mtime 12:18, alongside equally stale task-4..task-7 reports). Wrote here per the
> lead's explicit path instruction. Flagging in case those older reports need preserving elsewhere.

## What I implemented

1. **Styled 404 page** — new `NotFoundPage.razor`, a minimal full-document component (doctype/head/body,
   `<DashboardHead Title="Miller — Not found" />`, `<DashboardScripts />`) mirroring the shells' structure.
   Hero-style layout (`.dashboard-hero` / `.hero-copy` / `.eyebrow` / `.hero-subtitle`), the exact endpoint
   message in a `.not-found` section, plus two routes back to `/` (a `.back-link` in the hero and a
   `.subtle-link` under the message). One parameter: `Message` (string).
2. **Endpoint 404 branch** — `DashboardEndpoints.cs` `/workspace` not-registered branch now returns
   `RazorComponentResult<NotFoundPage>` with `StatusCode = StatusCodes.Status404NotFound` and
   `PreventStreamingRendering = true`, replacing `Results.NotFound(text)`. Message text preserved
   byte-for-byte. No other branch touched.
3. **Version footer** — `<footer class="site-footer">` in both shells: `miller <MillerVersion.Current>`
   plus a `/diagnostics.json` link.
4. **New-tab JSON links** — all four `.api-link` anchors in each shell carry `target="_blank" rel="noopener"`.
5. **CSS** — `.site-footer` / `.site-footer-version` / `.site-footer-link` / `.not-found` /
   `.not-found-message` appended at the very END of `dashboard.css` (lines 1437-1487), append-only.

## Verification ledger

| Invariant | Scope | Command | Result | Timestamp |
|---|---|---|---|---|
| 404 renders styled HTML w/ message + link to `/`; footer + new-tab links on both shells; reflected id escaped | focused | `dotnet test … --filter "(Category!=Scale)&(FullyQualifiedName~DashboardNotFound)"` | **PASS** 5/5 (573ms) | 2026-07-16 |
| No regression across fast suite | worker | `scripts/test.sh` | **PASS** 3538/3538 (20s run, 25s wall — under 30s ceiling) | 2026-07-16 |
| 0 warnings / 0 errors (warnings-as-errors) | worker ceiling | `dotnet build Miller.slnx -c Release` | **PASS** 0W/0E | 2026-07-16 |

TDD: the HTTP test was written first and **watched fail** — first on the shared build break, then on a real
red (`Not found: "miller 1.10.0+780b51d027cd"`) — before the implementation turned it green.

Note: an initial `scripts/test.sh` reported 52s wall (> 30s ceiling). Re-run warm was 23s/25s. The overage was
cold Release-build overhead inside the timed region, **not** a leaked slow test — my 5 tests total ~570ms.

## Files changed

| File | Change |
|---|---|
| `src/Miller.Dashboard/Components/NotFoundPage.razor` | **created** (38 lines) |
| `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs` | 404 branch only (+8/−2) |
| `src/Miller.Dashboard/Components/WorkspaceShell.razor` | api-links + footer (+12/−4) |
| `src/Miller.Dashboard/Components/WorkspacesShell.razor` | api-links + footer (+12/−4) |
| `src/Miller.Dashboard/wwwroot/dashboard.css` | +51 appended at EOF, no edits above |
| `tests/Miller.Tests/Server/DashboardNotFoundTests.cs` | **created** (5 tests) |

No file outside my ownership was touched. `DashboardFormat.cs`, onboarding/pattern panels, and
`DashboardActivityFeedTests.cs` (Task 2) untouched; Task 1's middleware and fragment routes untouched.

## Miller calls used

| Call | What it confirmed |
|---|---|
| `inspect(target='MapDashboardEndpoints', depth='full')` | Exact 404 branch (`Results.NotFound` at :63), the `(IResult)` cast idiom on the sibling return, and the `PreventStreamingRendering = true` object-initializer pattern on all 7 `RazorComponentResult` usages. |
| `search(query='MillerVersion', mode='symbol')` | Definition at `src/Miller.Server/MillerVersion.cs:13`, `public static class`. |
| `inspect(target='MillerVersion', depth='overview')` | Accessor shape: `public static string Current { get; }` (:16) — a property, not a method. 27 dependents; already called from `DashboardEndpoints.BuildRuntimeInfo:309`. |
| `inspect(target='src/Miller.Dashboard/Components/WorkspaceShell.razor')` | Symbol list + hero/footer placement; `Snapshot`/`Activity` params. |
| `inspect(target='ReadSnapshot', depth='full')` | Selection is registry-driven via `SelectWorkspace`; facts-read degrades to `null` — proving a stub `symbols.db` in my seed still resolves `SelectedWorkspaceId` (corroborated by the existing `ReadSnapshot_UnreadableWorkspaceDbReturnsFactsErrorNotCrash`). This validated the 200-path test. |

## API-shape evidence (no guessed shapes)

- **`MillerVersion.Current`** — property, not method. Miller `inspect` depth=overview (above). Referenced
  fully-qualified as `@Miller.Server.MillerVersion.Current` in the shells because `_Imports.razor` is **not**
  in my ownership and already lacks a `Miller.Server` using; `DashboardEndpoints.cs:309` uses the same
  fully-qualified form, so this matches existing practice.
- **`RazorComponentResult.StatusCode`** — verified against the real .NET 10 ref assembly rather than memory:
  `strings …/Microsoft.AspNetCore.App.Ref/10.0.10/ref/net10.0/Microsoft.AspNetCore.Components.Endpoints.dll`
  → `get_StatusCode` / `set_StatusCode` / `IStatusCodeHttpResult` / `set_PreventStreamingRendering`. Settable
  via object initializer, exactly as the plan assumed.
- **TestServer helper** — `DashboardMutationEndpointTests:148` `StartHostAsync()`:
  `HostBuilder().ConfigureWebHost(w => w.UseTestServer().ConfigureServices(DashboardHostPipeline.ConfigureServices).Configure(app => DashboardHostPipeline.Configure(app, _paths, _dir)))`.
  Copied verbatim, plus its `DashboardPaths` ctor arity (5) and
  `registry.UpsertSeen(id, displayId, root, indexDbPath, WorkspaceRegistryState.Current, DateTimeOffset)` seed shape.
- **Blazor HTML encoding** — `HtmlEncoder.Default` escapes `+` → `&#x2B;` and `'` → `&#x27;`. Confirmed
  empirically by dumping rendered HTML (`miller 1.10.0&#x2B;780b51d027cd`), not assumed.

## Self-review findings (acted on)

1. **Reached for a `SiteFooter` component, then removed it.** It would have been a new abstraction (approved
   shape: "one new page component, no new abstractions") **and** a file outside my ownership. The spec scopes
   the footer to the two shells only, and the shells already duplicate their hero/theme-switch blocks verbatim
   — inlining matches the local idiom. Reverted before it reached the build.
2. **New XSS surface — covered with a test.** The endpoint previously emitted `workspace_id` as `text/plain`
   (inherently inert); it now reflects that user input into **HTML**. Razor auto-escapes, but that invariant
   was load-bearing and untested, so I added
   `WorkspaceGet_WithScriptInjectionId_EscapesIdIntoInertText`: `?workspace_id=<script>alert(1)</script>`
   returns 404 with the payload escaped to `&lt;script&gt;…` and asserts the raw tag is absent. Passes.
3. **Test asserted escaped entities; fixed the test, not the code.** My first version hard-coded
   `&#x27;`/`&#x2B;`. Brittle and it obscured intent — the browser renders the correct glyphs. Switched to
   `WebUtility.HtmlDecode(html)` and asserted the human-readable string.
4. **Did not assert an ETag on `/workspace`** — Task 1's middleware covers `/fragments/*` only, per briefing.
5. Zero comments in tests; no narration comments in code. The only doc comments are XML summaries on the
   `Message` parameter and the test class.

## Judgment calls

- `NotFoundPage.razor:31` — **no footer on the 404 page.** Chose spec-literal ("add a footer to both shells")
  over symmetry, because adding it would have forced either a shared `SiteFooter` component (new abstraction,
  unowned file) or a third copy of the markup. Flagging for the lead: if a footer on the 404 is wanted, the
  clean move is a `SiteFooter` component in a follow-up that owns all three call sites.
- `DashboardEndpoints.cs:63` — returned the concrete `RazorComponentResult<NotFoundPage>` **without** an
  `(IResult)` cast. The sibling branch already carries the cast, so C# best-common-type resolves the lambda to
  `IResult`. Verified by the clean Release build; adding a second cast would have been redundant.
- `NotFoundPage.razor:12,28` — **two** links back to `/` (hero back-link + body link). The hero `.back-link`
  mirrors `WorkspaceShell` so the chrome is consistent; the body link puts the escape hatch next to the
  message. Acceptance only requires "a link to `/`".
- `NotFoundPage.razor:17-23` — kept the theme-switch button so the 404 isn't the one page that ignores the
  user's theme. Dropped the `.api-actions` nav and `.hero-metrics` (no workspace to describe).
- `dashboard.css` — reused only existing custom properties (`--surface`, `--rule`, `--rule-strong`,
  `--ink-soft`, `--muted`, `--accent-ink`, `--radius`, `--font-mono`, `--tnum`). All are `light-dark()`, so
  both themes are covered with no new theme rules.

## Concerns

- **None blocking.** Fast suite and Release build are green.
- **Shared-tree timing (resolved):** Task 2's mid-TDD state broke the shared build twice (`DashboardFormatTests.cs`
  `FormatCount` overload, then `WorkspaceOnboardingPanel.razor` RZ1010). Neither was mine; I waited for green
  rather than touching their files. Worth noting for the lead: in a shared working tree, a worker's "watch it
  fail" step can be masked by another worker's compile error.
- **For the lead's final gate:** the fast-suite tripwire is wall-clock and includes build time — a cold
  `scripts/test.sh` reports ~52s and trips the 30s ceiling. Warm re-run is 23-25s. Not a leaked slow test.
- My 5 tests spin up in-memory TestServer hosts (~570ms total, no `julie-extract` subprocess), so they
  correctly stay out of `Category=Scale` — same precedent as `DashboardMutationEndpointTests`.
