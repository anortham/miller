# Report: Dashboard workspace remove + prune

## Worktree state used

- Path: `/Users/murphy/source/miller/.worktrees/dashboard-workspace-remove`
- Branch: `feat/dashboard-workspace-remove`
- Start commit: `4ccc0a1` (clean); end commit: `41c7910` (clean, `git status --short --branch` empty)
- All work stayed in this worktree; the main checkout was never touched.

## What was implemented

1. **`src/Miller.Server/Workspaces/WorkspaceRemoval.cs`** (new, public static): `RemoveById(registry, selector, liveRoot)` / `RemoveByPath(registry, path, liveRoot)` returning the existing `WorkspaceRemoveResult`. One private `Remove(...)` core carries: live-root refusal via `WorkspaceSafety.IsLiveWorkspace` (only when `liveRoot` non-null), missing-dir orphan-row prune, the unconditional in-use refusal via `WorkspaceWriteLeases.TryAcquireForRemove` (indexer → content → history co-held across the delete, commit `46a5190` semantics preserved verbatim), delete-except-held-locks + emptied-dir cleanup, registry row removal. `RemoveByPath` keeps the gone-root best-effort prune (R4) via `WorkspaceRegistryRootMatcher.FindByPossiblyMissingPath`. `RemoveById` throws `KeyNotFoundException` (selector resolution unchanged, `WorkspaceRegistrySelector.Resolve`).
2. **`CliDispatch.WorkspaceRemove`** is now a thin caller (parse selector → helper with `liveRoot: null` → render + `RemoveExitCode`); `RemoveMillerDir` deleted. All 142 `CliDispatchTests` (incl. 13 remove tests) pass **untouched**.
3. **Dashboard endpoints** (`DashboardEndpoints.cs`): `POST /workspace/remove` (`[FromForm] string? workspace_id` → `RemoveById(..., liveRoot: null)`), `POST /workspaces/prune` (`IFormCollection` binding → `WorkspaceRegistryPrune.Run(protectedWorkspaceId: null, dryRun: false)`). Both PRG back to `/` with `?notice=<code>&detail=<text>` (closed code vocabulary: `removed`, `removed-registration`, `remove-refused-in-use`, `remove-refused-live`, `remove-not-found`, `remove-error`, `pruned`). Degrade discipline: `KeyNotFoundException` → not-found notice; `SqliteException | IOException | InvalidOperationException | UnauthorizedAccessException` → `remove-error` notice (same precise filter set the panel readers use); no 500 path. `DashboardIndexFactsCache.Clear()` after mutations. `GET /` gained `notice`/`detail` query params forwarded to the shell.
4. **UI** (`WorkspaceIndex.razor` + `WorkspacesShell.razor`): per-row (live and stale) `details/summary` Remove action expanding to an SSR confirm form (`<AntiforgeryToken/>`, hidden `workspace_id`, consequence copy naming `.miller` deletion + `workspace open` recovery, Confirm button, Cancel link) — rendered once via a Razor-template `RenderFragment` so live/stale can't drift. "Prune N stale" button (form) at the top of the stale section body; summary copy now reads "N missing root — prune below or run `miller workspace prune`" (augment, keeps the existing test assertion). Notice block renders next to the existing registry-error notice; unknown codes render nothing (the param is craftable). No new CSS file and no new class names — reused `subtle-link`, `refresh-button`, `detail-actions`, `muted`, `notice`/`error-notice`.
5. **Host wiring** (`Program.cs`): `services.AddAntiforgery()` + `app.UseAntiforgery()` between `UseRouting` and `UseEndpoints`.
6. **ADR** `docs/adr/ADR-0002-dashboard-registry-lifecycle-mutations.md`: records the user-approved reversal of the 2026-07-08 registry-hygiene Task 5 read-only decision, the guard rails, and what stays forbidden (no full-index hydration for list/detail; no new MCP surface). CLAUDE.md dashboard sentence updated; `scripts/sync-agents.sh` run; `cmp -s CLAUDE.md AGENTS.md` passes.

## Verification ledger

| Invariant | Scope | Command | Commit | Result | Timestamp (UTC) |
|---|---|---|---|---|---|
| Helper behavior slices (red→green per slice) | slice | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorkspaceRemovalTests"` | pre-commit tree of `1b67924` | red (CS0103) → 10/10 pass | 2026-07-09 ~00:20Z |
| CLI remove tests pass UNTOUCHED | slice | `... --filter "FullyQualifiedName~CliDispatchTests"` | pre-commit tree of `1b67924` | 142/142 pass, file unmodified | 2026-07-09 ~00:22Z |
| Dashboard render hooks (red→green) | slice | `... --filter "FullyQualifiedName~DashboardRegistryReadTests"` | pre-commit tree of `b5ac69f` | red (4 fail) → 51/51 pass | 2026-07-09 ~00:35Z |
| Fast suite, wall <30s | worker ceiling | `scripts/test.sh` | `41c7910` (HEAD) | 3134/3134 pass, 20s wall | 2026-07-09T00:49:38Z |
| Release build 0 warnings / 0 errors | worker ceiling | `dotnet build Miller.slnx -c Release` | `41c7910` (HEAD) | 0W / 0E | 2026-07-09T00:49:38Z |

Scale suite not run (per scope); no test spawns julie-extract; all new tests use per-test temp dirs (never real `~/.miller` — enforced by `MILLER_REGISTRY_DB`-style explicit paths in tests and temp registries).

**Live end-to-end smoke** (temp registry + temp `.miller`, Release binary on port 4993, then killed):
- `GET /` renders both remove confirm forms, the "Prune 1 stale" button, and antiforgery tokens.
- `POST /workspace/remove` **without** token → **400** (CSRF rejected, not 500, no mutation).
- With cookie+token → **302** `/?notice=removed&detail=<root>`; `.miller` dir deleted; registry row gone.
- `POST /workspaces/prune` with token → **302** `/?notice=pruned&detail=1`; gone-root row pruned.
- Notices render ("Removed … — its .miller index data was deleted…", "Pruned 1 stale registration.").
- `GET /fragments/workspaces` (the 30s htmx swap source) also serves a valid token; a POST using a fragment-sourced token succeeds — the poll cannot strand the form with a dead token.

## Commits (owned files only)

- `1b67924` refactor(server): extract WorkspaceRemoval core from CLI workspace remove
- `b5ac69f` feat(dashboard): workspace remove + prune from the all-workspaces view
- `41c7910` docs: ADR-0002 dashboard registry-lifecycle mutations; update dashboard rule

## Miller calls used (API-shape evidence)

- `context(query="workspace remove CLI verb…")` — located `WorkspaceRemove` (CliDispatch.cs:2130), `RemoveMillerDir` (:2195), `WorkspaceRemoveResult` factories (WorkspaceRender.cs:142–183), `SingleWriterLock`.
- `inspect(target=WorkspaceRemove, depth=full, scope=src/Miller.Server/Cli/CliDispatch.cs)` — full body: proved selector resolution, R4 gone-root prune, `FindByRoot` fallback, and that the CLI applies **no** live-root check (comment: "minus the in-process live workspace refusal") — hence `liveRoot: null` from the CLI keeps behavior identical.
- `inspect(target=RemoveMillerDir, depth=full)` — lease co-hold order and `DeleteContentsExceptLock`/`TryDeleteEmptiedDir` semantics moved verbatim.
- `inspect(target=WorkspaceSafety, depth=full)` — `IsLiveWorkspace(candidateRoot, liveRoot)` signature + canonical/lexical fallback semantics.
- `inspect(target=WorkspaceRegistryPrune, depth=full)` — `Run(registry, protectedWorkspaceId, dryRun)` → `Result(DryRun, Pruned, Kept)`; dashboard passes `protectedWorkspaceId: null`.
- `inspect(target=WorkspaceWriteLeases, depth=full)` — `TryAcquireForRemove(millerDir, Func<string, IDisposable?>)`, `SidecarLockFileNames`, refusal-on-null contract.
- `inspect(target=src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs)` — endpoint map shape before targeted reads.
- Targeted `Read`/`grep` after inspects for: razor components, `DashboardPaths` (registry path source — same `paths.RegistryDbPath` the list view uses), `DashboardData` catch-filter style, `alpine-components.js` (sort/filter DOM contract: `.ws-index-row` nodes are re-appended on sort and `hidden`-toggled on filter — this forced the remove control INSIDE the row element), `dashboard.css` (row grid = 7-column `<a>`; `.ws-index-row` styles are class-based, so a `<div>` keeps layout/hover), registry DDL for the smoke seed.
- Antiforgery specifics verified by compiling AND live: .NET 10 minimal APIs with form binding (`[FromForm]`, `IFormCollection`) validate antiforgery once `AddAntiforgery()`/`UseAntiforgery()` are wired — proven by the live 400-without-token / 302-with-token smoke, including `IFormCollection`-bound prune.

## Judgment calls

- `WorkspaceIndex.razor:68` — **rows changed from `<a class="ws-index-row">` to `<div class="ws-index-row">` with the workspace name as the link.** Forms/details are interactive content and invalid (and broken) inside an anchor, and the sort JS re-appends `.ws-index-row` nodes so the remove UI cannot be a sibling. The div keeps every grid/hover/filter/sort hook (class-based CSS + `data-sort-*` preserved). Cost: the full-row click target shrinks to the name link. Updated the one render test that pinned `<a class="ws-index-row"` (allowed file; CLI tests untouched).
- `WorkspaceIndex.razor:71` — `style="text-decoration: none"` on the name anchor: the stylesheet (not modifiable per ownership) has no anchor reset, and every other link-ish control in the design sets `text-decoration: none`; a UA underline on every row name would be a visual regression. Inline-style precedent exists (meter widths).
- `WorkspaceIndex.razor:113` — **prune button in the stale details body, not inside `<summary>`**: interactive content inside `summary` is invalid HTML and click/toggle behavior is browser-dependent. The summary copy points at it ("prune below"), which also keeps the existing `miller workspace prune` copy assertion green (spec allowed replace/augment).
- `DashboardEndpoints.cs` — outcome notices ride a **closed code vocabulary** (`?notice=<code>`) rather than free text, so a crafted URL cannot render arbitrary "success" copy; only `remove-error` echoes an exception message (HTML-encoded by Razor).
- `WorkspaceRemoval.Remove` — live-root refusal placed before the missing-dir branch, matching the server `WorkspaceTool.Remove` ordering; unreachable from CLI/dashboard today (both pass `liveRoot: null`, pinned by `RemoveById_LiveRoot_RefusedLive` / `RemoveById_DifferentLiveRoot_StillRemoves`).
- Prune endpoint binds `IFormCollection` (documented form-binding trigger for antiforgery validation) since the form has no data field; verified live.
- Confirm-open state does not survive the 30s htmx section poll (the swap re-renders closed). Accepted: the pre-existing query-param alternative loses state on the same swap; not load-bearing for correctness.
- No goldfish checkpoint was committed: `.memories/` is outside this task's explicit file-ownership list; the lead may checkpoint at integration.

## Self-review findings (fixed before reporting)

- Razor string-parameter gotcha: `Notice="Notice"` passed a literal; fixed to `Notice="@Notice"` (caught by the shell-forwarding render test).
- Duplication risk of the confirm form across live/stale sections resolved with a single Razor-template `RenderFragment` in `@code`.

## Issues / concerns

- The remove summary chip ("Remove…", `subtle-link` styling) adds visible height to each row; if the user finds the list too busy, a one-line CSS tweak (outside this task's file ownership) could compact it.
- `POST /workspaces/prune` prunes **all** missing-root registrations machine-wide (CLI parity, no protected row since the dashboard serves no workspace); the button label shows the count so the scope is visible before clicking.
