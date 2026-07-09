# Dashboard Workspace Remove + Prune — Design

**Date:** 2026-07-08
**Status:** Approved (user, this session)
**Supersedes:** the "no deletion from the dashboard" decision in
[2026-07-08-dashboard-registry-hygiene.md](2026-07-08-dashboard-registry-hygiene.md) Task 5 —
the user explicitly asked for removal from the dashboard UI.

## Goal

Let the user remove a workspace registration (and its `.miller` index data) directly from the
dashboard's all-workspaces view, and one-click prune all stale (missing-root) registrations —
with full behavioral parity with the CLI `workspace remove` / `workspace prune` verbs.

## What to build

1. **`src/Miller.Server/Workspaces/WorkspaceRemoval.cs` (new).** Extract the removal core out of
   `CliDispatch.WorkspaceRemove` / `CliDispatch.RemoveMillerDir` (private, `CliDispatch.cs:2130`/`:2195`)
   into a shared static helper returning the existing `WorkspaceRemoveResult` struct
   (`src/Miller.Server/Tools/WorkspaceRender.cs:142`). Shape (adjust naming to repo idiom during
   implementation, keep the semantics):
   - `WorkspaceRemoval.RemoveById(WorkspaceRegistry registry, string selector, string? liveRoot)`
   - `WorkspaceRemoval.RemoveByPath(WorkspaceRegistry registry, string path, string? liveRoot)`
   - Both preserve, in one place: registry row resolution (`WorkspaceRegistrySelector`), the
     gone-root best-effort prune (R4), the live-root refusal (`WorkspaceSafety.IsLiveWorkspace`,
     applied only when `liveRoot` is non-null), the in-use lock refusal (unconditional), lock
     co-hold coordination before gutting `.miller` (see `46a5190`), and registry row removal.
   - `CliDispatch` becomes a thin caller: resolve args → call helper → render + exit code.
     CLI behavior is unchanged; existing CLI remove tests must pass untouched.
2. **Dashboard POST endpoints** in `src/Miller.Dashboard/Endpoints/DashboardEndpoints.cs`:
   - `POST /workspace/remove` — form field `workspace_id`; calls `WorkspaceRemoval.RemoveById`
     with `liveRoot: null` (the dashboard process has no current workspace; the in-use lock guard
     still protects actively served workspaces).
   - `POST /workspaces/prune` — calls the existing
     `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs` helper (non-dry-run).
   - Both follow POST-redirect-GET back to the all-workspaces view, carrying a short outcome
     message (removed / refused: in use / not found / pruned N) rendered via the existing notice
     pattern (`4c28d90` registry error notice). No exception may escape as a 500 — same degrade
     discipline as the panel readers.
3. **UI** in `src/Miller.Dashboard/Components/WorkspaceIndex.razor` (+ `WorkspacesShell.razor` if
   the notice/counts live there):
   - Per-row **Remove** action on every entry (live and stale) that expands to an inline confirm:
     "Removes this registration and deletes its `.miller` index data (rebuildable via
     `workspace open`). Confirm / Cancel". SSR forms only — no JS framework, matching the
     dashboard's existing style; reuse existing card/list class names, no new CSS file.
   - **Prune N stale** button on the stale-section header (replaces/augments the current
     "run `miller workspace prune`" hint).
   - **Antiforgery tokens on both forms** (Razor Components SSR built-in), so an arbitrary web
     page cannot POST mutations to the local port.
4. **ADR** in `docs/adr/` (next number): the dashboard may perform registry-lifecycle mutations
   (remove/prune) via POST endpoints, but remains barred from hydrating full workspace indexes
   for list/detail views. Update the CLAUDE.md dashboard sentence to match.

## Error handling

- Refused-in-use / not-found render as an inline notice on the list, not an error page.
- Removing the workspace row that the dashboard itself was launched for is allowed (it is just a
  registration); the in-use lock guard is what protects live-served workspaces.

## Testing

- `WorkspaceRemoval` unit tests: removed / refused-in-use / gone-root prune / not-found, over
  per-test temp registries and temp `.miller` dirs (Task-3 isolation rules: never touch the real
  `~/.miller`).
- Render tests in `tests/Miller.Tests/Server/DashboardRegistryReadTests.cs` asserting the confirm
  form hooks and the prune button (follow `WorkspacesShell_RendersIndexListHooksAndLinks`).
- Existing CLI remove/prune tests pass unchanged (proves the extraction preserved behavior).
- Fast suite stays fast; no test spawns julie-extract.

## Acceptance criteria

- [ ] Remove button on every row with confirm step; on confirm the registration row is removed and
      its `.miller` dir deleted; refusal (in use) shows a visible notice.
- [ ] Prune-stale button removes exactly the missing-root rows and reports the count.
- [ ] CLI `workspace remove` / `workspace prune` behavior unchanged; existing tests pass untouched.
- [ ] Both forms carry antiforgery protection.
- [ ] ADR recorded; CLAUDE.md dashboard rule updated.
- [ ] `dotnet build Miller.slnx -c Release` 0 warnings / 0 errors; `scripts/test.sh` passes.
- [ ] Live verification: rebuilt dashboard removes a scratch workspace registration and prunes a
      seeded stale row end-to-end.
