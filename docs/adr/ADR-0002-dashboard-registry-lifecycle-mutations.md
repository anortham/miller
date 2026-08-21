# ADR-0002: Dashboard registry-lifecycle mutations (remove / prune)

**Status:** Accepted
**Date:** 2026-07-08
**Reverses:** the 2026-07-08 registry-hygiene plan Task 5 decision that kept the dashboard strictly
read-only (user-approved reversal, 2026-07-08)

Design: [`docs/plans/2026-07-08-dashboard-workspace-remove-design.md`](../plans/2026-07-08-dashboard-workspace-remove-design.md).

## Context

The all-workspaces view surfaces stale registrations (missing roots, errored rows) and tells the
user to leave the dashboard and run `miller workspace remove` / `miller workspace prune`. That
round-trip is the single worst friction in the registry-hygiene workflow: the dashboard already
shows exactly which rows are dead, but could not act on them. The registry-hygiene plan (Task 5)
had deliberately kept the dashboard read-only; the user approved reversing that decision for
registry-lifecycle operations specifically.

The dashboard was, until now, a pure reader: registry rows, shared telemetry, and read-only
aggregate facts from workspace artifacts.

## Decision

The dashboard MAY perform **registry-lifecycle mutations** — and only those — via two form-post
endpoints in `DashboardEndpoints`:

- `POST /workspace/remove` (form field `workspace_id`) → `WorkspaceRemoval.RemoveById(...,
  liveRoot: null)`, the same shared core the CLI `workspace remove` verb calls. The dashboard
  process serves no workspace in-process, so the live-root refusal does not apply. Active
  **writers** remain protected by the in-use refusal (the delete only happens while holding the
  indexer + content + history write leases). Pure **readers** hold no lease and are NOT blocked —
  identical to CLI remove: a reader whose index files disappear fails loudly on its next reopen
  (the per-poll reopen discipline guarantees it notices), and the index is rebuildable with
  `workspace open`.
- `POST /workspaces/prune` → the existing `WorkspaceRegistryPrune.Run` helper (non-dry-run,
  no protected row).

Guard rails that make this safe:

- **Shared core, not a parallel implementation.** Removal semantics (selector resolution,
  gone-root prune, lease co-holding, registry row removal, and the family-store sidecar reclaim that
  follows it) live once in `src/Miller.Server/Workspaces/WorkspaceRemoval.cs`; the CLI and the
  dashboard are thin callers. The dashboard therefore reclaims a removed member's per-view sidecars
  with no dashboard-side code and no new notice code: the reclaim reports itself in the result the
  core returns, and the notice vocabulary below stays closed.
- **Antiforgery on every mutation form.** Both forms embed `<AntiforgeryToken/>` and the endpoints
  bind form data, which opts them into ASP.NET's antiforgery validation (wired via
  `AddAntiforgery()` + `UseAntiforgery()` in the dashboard host). An arbitrary web page cannot
  POST mutations to the local port; a token-less post gets 400, not a mutation.
- **Post-redirect-get with a closed notice vocabulary.** Outcomes (removed / refused-in-use /
  not-found / pruned N / degraded error) redirect back to `/` as `?notice=<code>&detail=<text>`;
  unknown codes render nothing. No exception escapes as a 500 — the endpoints use the same
  precise-filter degrade discipline as the panel readers.
- **Removals are recoverable.** A removed registration's index is rebuildable with
  `workspace open`; the confirm UI says so.

## What stays forbidden

- The dashboard remains barred from **hydrating full workspace indexes** just to render
  list/detail views (registry + telemetry + read-only aggregate artifact facts only).
- No other mutation class is authorized by this ADR. Anything beyond registry lifecycle
  (remove/prune, and the pre-existing refresh trigger) needs its own decision.
- No new MCP tool or tool operation — this is a dashboard/CLI surface only.
