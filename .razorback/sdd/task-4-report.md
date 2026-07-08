# Task 4 Report: `workspace prune` operation

**Status:** Complete
**implementation commit SHA:** 41cc29f
**Worker:** subagent (dashboard-registry-hygiene worktree)

## Summary

Finished the partial `workspace prune` implementation: registry GC that removes rows whose `canonical_root` no longer exists on disk. Exposed on both MCP (`operation=prune`, `dry_run` bool default false) and CLI (`miller workspace prune [--dry-run] [--json]`). Renamed and fixed `WorkspaceToolPruneTests.cs` (was `.parallel-blocker`).

## Implementation

### Core helper — `WorkspaceRegistryPrune.cs`

Static helper composing `WorkspaceRegistry.List()` + `Directory.Exists()` + `WorkspaceRegistry.Remove()`. No julie spawn, no `symbols.db` open.

- `Run(registry, protectedWorkspaceId, dryRun)` returns `Result(DryRun, Pruned entries, Kept count)`
- Protected workspace_id is skipped even when its root is missing

### MCP — `WorkspaceTool.cs`

- `case "prune"` → `Prune(json, dryRun)`
- `dry_run` parameter (default false) on `Workspace()` method
- Protects `_workspace.WorkspaceId` (current process workspace)
- Tool `[Description]` updated to mention prune (686 chars, ≤900 budget)

### CLI — `CliDispatch.cs`

- `case "prune"` → `WorkspacePrune(ctx, json, dryRun: o.Has("dry-run"), outw)`
- Resolves current row via `FindCurrentWorkspaceRow` and passes its `workspace_id` as protected
- Usage strings updated in help text

### Render — `WorkspaceRender.cs`

- `WorkspacePruneResult` / `WorkspacePruneEntry` record structs
- Compact: `pruned: N` / `would prune: N` + up to 10 `  display_id root` lines + `kept: M`
- JSON: `{ "dry_run", "pruned": [{ "workspace_id", "display_id", "root" }], "kept" }`

### Tests — `WorkspaceToolPruneTests.cs`

Renamed from `.parallel-blocker`; fixed compile issues:

1. `Action<>` delegate type params (removed invalid named generic args)
2. Added `using Miller.Server.Cli`
3. `RecordingDashboardLauncher.EnsureRunning` (not `Launch`)
4. `GetArrayLength()` instead of `Assert.Single(JsonElement)`

Seven tests:

| Test | Coverage |
|------|----------|
| `Prune_RemovesRowsWithMissingRoots` | Removes missing-root row, keeps current + existing |
| `Prune_DryRun_ListsWithoutRemoving` | Dry-run lists without delete |
| `Prune_NeverPrunesCurrentWorkspace_EvenWhenRootMissing` | Current row protected when root deleted |
| `Prune_CompactOutput_CapsExamplesAt10` | 12 pruned, only 10 example lines |
| `Prune_JsonOutput_MatchesShape` | JSON contract |
| `Prune_JsonDryRun_ListsWithoutRemoving` | JSON dry-run |
| `RegistryPrune_RemovesOnlyMissingRoots` | Unit-level helper test |

## Verification

```
dotnet test tests/Miller.Tests/Miller.Tests.csproj \
  --filter "FullyQualifiedName~WorkspaceToolPruneTests|FullyQualifiedName~AgentInstructionsTests" \
  -c Release
```

**Result:** Passed — 50/50 (7 prune + 43 AgentInstructions), 164 ms

## Acceptance criteria

- [x] `miller workspace prune` and `workspace(operation="prune")` remove rows with missing roots and report them
- [x] `--dry-run` / `dry_run=true` lists candidates without removing
- [x] `--json` matches specified shape; compact caps examples at 10
- [x] `AgentInstructionsTests` (description budgets) pass
- [x] Worker-scope verification passes

## Files touched (Task 4 scope)

| File | Action |
|------|--------|
| `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs` | Created (prior worker) |
| `src/Miller.Server/Tools/WorkspaceTool.cs` | Modified (prior worker) |
| `src/Miller.Server/Cli/CliDispatch.cs` | Modified (prior worker) |
| `src/Miller.Server/Tools/WorkspaceRender.cs` | Modified (prior worker) |
| `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs` | Created (renamed + fixed) |

`WorkspaceRegistry.cs` unchanged — plain composition sufficient.

## Notes

- Task text says "current/primary"; implementation guards the current workspace `workspace_id` (MCP: `_workspace.WorkspaceId`, CLI: `FindCurrentWorkspaceRow`). Registry has no separate primary state; the `primary` selector resolves to the current row.
- `MILLER_AGENT_INSTRUCTIONS.md` was not changed because the plan required operation plumbing, CLI/help text, and the workspace tool description, not a new discovery route.
- Tool-level tests cover the MCP path and the shared prune helper; CLI dispatch mirrors the same helper and render contract.
