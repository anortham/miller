# Task 7 — Notice toasts, non-navigating Cancel, CSS-driven theme label

**Status:** COMPLETE
**Worktree:** `/Users/murphy/source/miller/.claude/worktrees/dashboard-ux-fixes`
**Branch:** `worktree-dashboard-ux-fixes` (base `b53fd7d`)

> This file previously held a stale report from an older plan (lead live-verification notes); overwritten
> per the task brief.

## Ledger

| Step | Result |
|---|---|
| Orientation (grep `CancelHref`, read current worktree bytes) | 3 call sites found; all changed files read fresh |
| Tests written first (6 new) | RED — 6 failed for the intended reasons |
| Implementation (7 files) | GREEN — 56/56 focused |
| Full fast suite (`scripts/test.sh`) | 3568/3568 pass, 25s (ceiling 30s) |
| `dotnet build Miller.slnx -c Release` | 0 warnings / 0 errors |
| `node --check dashboard-site.js` | OK |

## Files changed

- `src/Miller.Dashboard/Components/WorkspaceRemoveConfirm.razor` — Cancel is now
  `<button type="button" class="subtle-link" data-close-details>Cancel</button>`; `CancelHref` parameter dropped.
  Header comment corrected: it claimed "no JS", which the delegated Cancel handler makes false.
- `src/Miller.Dashboard/Components/WorkspaceDetailPanel.razor` — dropped the `CancelHref` argument only.
- `src/Miller.Dashboard/Components/WorkspaceIndex.razor` — notice paragraph gains `data-notice` +
  `data-notice-tone="ok|danger"` (bound to the existing `NoticeIsError`).
- `src/Miller.Dashboard/Components/{WorkspacesShell,WorkspaceShell,NotFoundPage}.razor` — theme button renders
  both `theme-label-dark`/`theme-label-light` spans; `id="theme-toggle-label"` removed.
- `src/Miller.Dashboard/wwwroot/js/dashboard-site.js` — delegated `data-close-details` handler;
  `mirrorNoticeAsToast()` on DOMContentLoaded; `updateThemeButton` no longer writes the label, now only
  reflects `aria-pressed` (true when dark) across all `[data-toggle-theme]` buttons.
- `src/Miller.Dashboard/wwwroot/dashboard.css` — theme-switch label visibility per `html[data-theme]`.
- `tests/Miller.Tests/Server/DashboardActivityFeedTests.cs` — 6 new tests.
- `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` — **unowned**, minimal intent-preserving update (see below).
- `docs/plans/2026-07-16-dashboard-ux-fixes.md` — Task 7 checkboxes ticked.

## Miller calls / API-shape evidence

Structure came from targeted greps over live worktree bytes. The files in scope were rewritten by
T1/T3/T5/T6 after the Miller index baseline, so per the contract inputs the worktree bytes — not the index —
are the trustworthy source for them; Miller's index would have described superseded code.

- `grep -rn "CancelHref" src/ tests/` → exactly 3 sites: the `<a href>` (`WorkspaceRemoveConfirm.razor:14`),
  the `[Parameter]` declaration (`:22`), and the single `WorkspaceDetailPanel` argument (`:30`). No test
  referenced it by that name.
- `grep -rn "theme-toggle-label" src/Miller.Dashboard/Components …` → 3 shells + `dashboard-site.js:97`.
  Test-tree matches were `bin/Release` build artifacts only, not source.
- Shapes used, all read from source rather than guessed:
  - `showDashboardToast(message, tone)` — `dashboard-site.js:151`; className is
    `'dashboard-toast-' + (tone || 'danger')`. Existing callers pass exactly `'ok'` / `'danger'`.
  - `NoticeIsError` — `WorkspaceIndex.razor:202`, a closed set of 4 error codes.
  - `RenderComponentAsync<TComponent>(Dictionary<string, object?>)` — test helper at line 1084, with a
    `FixedAntiforgeryStateProvider` so `<AntiforgeryToken/>` renders outside a real request.
  - `theme-init.js` stamps `data-theme` (stored value, else `prefers-color-scheme`) pre-paint — the
    contract the CSS switch keys off.

## Self-review

- **Toast tone classes:** `.dashboard-toast-danger` has no CSS rule — verified this is by design: the base
  `.dashboard-toast` is danger-coloured and `.dashboard-toast-ok` (line 1570) overrides it. `'ok'`/`'danger'`
  therefore match the existing convention exactly; no CSS parity work needed.
- **`.subtle-link` on a `<button>`** (contract flagged this): verified class-based, not anchor-scoped —
  `.theme-switcher-btn, .api-link, .subtle-link, .refresh-button` share one rule that sets font-family,
  size, border, background and padding explicitly. `<summary class="subtle-link">` in this same component
  already proves non-anchor elements render correctly. No visual regression, no CSS change required.
- **Cancel click vs T5's stretched row link:** `.ws-row-actions` is `position: relative; z-index: 1`
  (dashboard.css:581) while the `.workspace-name::after` overlay has no z-index — the actions cell sits above
  it, so Cancel cannot leak into row navigation. `.ws-row-actions:has(details[open])` keeps the form visible.
- **Details state stays consistent:** setting `details.open = false` fires `toggle`, which the existing
  `rememberIssueDetailsState` listener already handles → the key leaves `openIssueDetails`, so a later morph
  swap will not re-open the cancelled confirm.
- **No-JS degradation:** Cancel falls back to re-clicking the summary (native `<details>`); the theme label
  defaults to "Dark", matching the light-theme colours the CSS serves unstamped.
- **Load-bearing code left untouched:** `htmx:beforeSwap` 304 guard, `htmx:configRequest` headers, `/` keydown,
  `rememberIssueDetailsState`, morph attrs, table roles. Handlers were appended, never reordered.

## Judgment calls

1. **`aria-pressed` written per-button via `[data-toggle-theme]`, not by id.** The three shells each render
   `id="theme-toggle"`, so a `getElementById` write is fine today but silently breaks if a page ever renders
   two toggles. The delegated selector already in use is the safer, equally trivial signal. JS-only (SSR cannot
   know the theme), so a no-JS page correctly exposes a plain button.
2. **Class naming reads as "the label whose text is X", not "shown when X".** Matches the task spec literally
   (`<span class="theme-label-dark">Dark</span>`) and preserves current semantics: the button names the theme
   you switch TO, so `theme-label-dark` ("Dark") shows while the light theme is active.
3. **CSS base rule hides `.theme-label-light` rather than keying both labels off `html[data-theme]`.** With no
   attribute stamped (JS disabled), attribute-only rules would hide *both* labels and render an empty button.
   The base rule keeps the previous SSR default ("Dark") in that case.
4. **Left the "Registry unavailable" paragraph without `data-notice`.** It is a persistent degradation state,
   not a transient post-redirect-get outcome; mirroring it into a self-dismissing toast would be wrong.
5. **`querySelector` (single) for the notice mirror** — the notice is a single post-redirect-get paragraph;
   a loop would imply a multiplicity that cannot occur.
6. **`NotFoundPage` theme button included** under the lead-granted ownership extension; covered by the new
   dual-label test via direct component render (the existing `DashboardNotFoundTests` drives it over HTTP and
   still passes unchanged).

## Plan mismatch

None material. The plan's Task 7 file list named two shells; the third (`NotFoundPage.razor`, added by T3)
carries the same theme button and was covered by the lead-granted extension in the brief. No new abstractions
were introduced — all three behaviors landed on existing seams (delegated click handler, DOMContentLoaded hook,
CSS attribute selector).

## Unowned-test breakage (flagged)

`tests/Miller.Tests/Server/DashboardRegistryReadTests.cs::WorkspaceDetailPanel_RendersRemoveConfirmForm`
asserted `href="/workspace?workspace_id=ws-a"` — precisely the Cancel navigation Task 7 removes. Applied the
minimal intent-preserving update: kept every other assertion (`action`, `workspace_id`, `Confirm remove`,
antiforgery token) and swapped the href assertion for `data-close-details` + `DoesNotContain(">Cancel</a>")`.
The test's stated intent ("the detail page carries the same expandable confirm form") is unchanged; its stale
comment about Cancel's destination was removed with the line it described.

## Concerns

- None blocking.
- Note for Task 10's visual sweep: both theme spans now exist in the DOM, and the hidden one contributes no
  flex gap only because it is `display: none`. If a future rule sets a `gap` on `.theme-switcher-btn`, keep
  the hidden label at `display: none` rather than `visibility: hidden`.
