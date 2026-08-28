# Worktree view retirement design

**Status:** approved direction, written specification awaiting review

## Goal

Stop removed worktrees from leaving live family-store views, per-view sidecars, and registry rows behind.
Give agents a short lifecycle rule at session start, make missed cleanup visible, and use the pinned
extractor's existing `store maintain retire-view` command from `workspace remove` and `workspace prune`.

## Current evidence

The Miller family currently has 12 producer views. Git reports two active worktrees: the main checkout and
`tool-latency-health`. Nine producer views point to missing roots. One additional registered root exists on
disk under `.claude/worktrees/ct-dogfood-round2`, but Git does not consider it a worktree; this design does
not remove that root or its view automatically.

The registry and Miller-owned sidecars already have removal paths. The producer view does not:

- `WorkspaceRemoval` and `WorkspaceRegistryPrune` capture the exact `store_members.view_id` before deleting
  the registry row.
- `StoreSidecarReclaim` deletes Miller-owned per-view search, content, and vector files.
- `StoreMaintenanceRunner` runs coordinator GC but does not retire producer views.
- `julie-extract store maintain retire-view --store <root> --view <id> --apply --json` transactionally removes
  the named producer view and its manifests. Without `--apply`, it is a read-only preview.

The missing producer retirement leaves old manifests live. Those manifests pin old file versions, and
name-first reference queries can examine family-wide identifier rows before applying the current view filter.

## Design

### 1. Agent lifecycle rule

Add one short rule to `hooks/miller-routing-block.md`, delivered by the existing SessionStart and
SubagentStart hook:

> After `git worktree remove <path>` succeeds, call Miller `workspace remove` for that exact old path. If
> cleanup was missed, inspect `workspace list`, run `workspace prune` as a dry run, then apply it.

The hook remains static, injection-only, fail-open, and off-switchable with `MILLER_SESSION_HOOKS=0`.
No startup command scans or mutates the registry.

### 2. Missing-root hint

When compact `workspace list` reports one or more missing roots, add a compact-only next step naming the
read-only prune preview. JSON keeps its current fields and byte shape. This is a reminder, not automatic
cleanup.

### 3. Producer view retirement

Add a Miller-owned adapter around the pinned extractor's `retire-view` command. It accepts only a captured
family store root and exact view id. It never hashes a root or rediscovers a view by listing producer data.

`workspace remove` and applied `workspace prune` use this order for each family-store member:

1. Capture the existing registry member, including `store_root` and `view_id`.
2. Preview producer retirement and validate the returned family/view identity.
3. Apply producer retirement.
4. Record the existing sidecar-reclaim intent.
5. Delete the registry row and member mapping.
6. Reclaim Miller-owned sidecars and run existing store maintenance.

Producer retirement is idempotent. An already-retired exact view counts as success. If preview or apply
fails, the registry row remains, the command reports the failure, and a later remove/prune retries safely.
This deliberately favors a visible stale registry row over an untraceable producer view.

Dry-run prune performs steps 1 and 2 only. It does not change the producer store, registry, or sidecars.

### 4. Existing stale views

After the implementation is verified, perform one-time maintenance on this Miller family:

- Preview retirement for the nine views whose roots are missing.
- Compare exact family id, view id, and root inventory with the captured audit.
- Apply retirement one view at a time.
- Remove the seven matching stale registry members through the existing targeted removal path.
- Reclaim their per-view sidecars and run producer GC.
- Handle the two producer-only legacy views with the pinned extractor's exact view-id retirement command;
  they have no registry member left to drive Miller removal.
- Recount producer views, registry members, sidecars, and active Git worktrees before reporting success.

The existing `ct-dogfood-round2` directory remains untouched because its root exists. Removing that registered
workspace is a separate explicit decision.

## Error handling

- Never retire the current workspace or a view still claimed by another registry member.
- Never apply a preview whose family or view identity differs from the captured target.
- A missing extractor, timeout, busy store, malformed JSON, or nonzero exit keeps the registry mapping and
  reports an actionable failure.
- A sidecar reclaim failure keeps the existing `.reclaim-owed` behavior.
- No cleanup command deletes a source checkout or worktree directory.

## Architecture quality

**Affected modules:** session-hook routing text, workspace list compact rendering, workspace removal/prune,
and one extractor-process adapter in the indexing boundary.

**Caller-facing interface:** existing `workspace remove`, `workspace prune`, and `workspace list`. No new MCP
tool or argument is added.

**Depth/locality check:** Miller owns workspace lifecycle and calls the producer through one adapter. Tool code
does not learn the extractor command shape.

**Test surface:** hook delivery, compact list rendering, removal/prune orchestration, adapter JSON parsing,
dry-run zero-write behavior, idempotent retirement, and failure-before-registry-delete ordering.

**Rejected shortcuts:** reminder-only guidance, automatic startup pruning, broad machine-wide prune for the
one-time cleanup, view-id rediscovery after registry deletion, and direct writes to producer SQLite tables.

**Architecture risk:** medium. The producer command exists and is exact, but removal ordering is load-bearing.

## Acceptance criteria

- [x] SessionStart and SubagentStart guidance names targeted workspace removal after worktree deletion.
- [x] Compact workspace list points missing roots to a dry-run prune; JSON output is unchanged.
- [x] Applied remove/prune retires the exact producer view before deleting its registry member.
- [x] Dry-run prune performs no producer, registry, or sidecar writes.
- [x] Producer failure leaves the registry member intact and returns an actionable error.
- [x] Repeating retirement for an already-absent exact view succeeds safely.
- [x] Existing sidecar reclaim and store-maintenance behavior remains intact.
- [x] Focused hook, workspace removal/prune, adapter, and rendering tests pass.
- [x] Fast suite, Scale suite, Release build, secrets scan, and dependency audit pass.
- [x] The nine missing Miller views are previewed, retired, reclaimed, and absent from the final inventory.
- [x] The main checkout, task worktree, and `ct-dogfood-round2` root remain untouched.
