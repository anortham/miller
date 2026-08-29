# Review-fix report: complete edit symbol recall

## Result

DONE for the owned implementation and focused verification. The Release builds were attempted after
the focused tests passed, but both were blocked by a concurrent syntax error in the other worker's
unowned `WorkspaceRegistryPrune.cs` edits. The lead must rerun those builds after that work is valid.

## Finding addressed

Edit target resolution now has an explicit complete-current-workspace-recall path. In family-store mode, normal named
reads may use a readable lagging search sidecar; that sidecar cannot discover symbols absent from its
own rows. The edit path now uses the live session symbol projection for the current workspace, so a
symbol present in the live store but absent from the sidecar remains resolvable while convergence is
pending.

## Miller and API-shape evidence

- Listed all owned source and test files with Miller `inspect` before reading bodies.
- Inspected `IWorkspaceSymbolReadProvider`, `WorkspaceIndexProvider.ResolveSymbolRead`,
  `ResolveCurrentSymbolRead`, `ResolveFamilyStoreLookup`, `EditTool.Edit`, and the relevant test
  providers/tests at full depth.
- Traced `IWorkspaceSymbolReadProvider` with Miller `trace mode=refs`; the interface has the
  production provider, `EditTool`, `InspectTool`, and test doubles as consumers/implementations.
- Miller showed the normal current family-store route enters `ResolveFamilyStoreLookup`, which opens
  `_openStoreSymbolSearch` when a readable sidecar is available, while `_loadSessionSymbolSearch`
  builds the complete live-session projection. The implementation keeps that routing unchanged for
  ordinary reads.
- Miller impact identified the provider, edit tool, and the existing routing/test implementations;
  no public MCP surface or storage contract was changed.

## RED

After adding the provider regression test, before production changes:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~ResolveCompleteCurrentSymbolRead_CurrentFamilyStoreUsesTheSessionProjectionWhenTheSidecarLags"
exit=1
CS1061: WorkspaceIndexProvider does not contain a definition for ResolveCompleteCurrentSymbolRead
```

After adding the edit-tool call-count coverage, the assigned focused union remained RED with the same
missing provider method. This proves the tests required the new API rather than passing against the
old sidecar route.

## Changes

Owned files modified:

- `src/Miller.Server/Workspaces/IWorkspaceSymbolReadProvider.cs`
  - Added `ResolveCompleteCurrentSymbolRead` with a default implementation delegating to
    `ResolveSymbolRead(null, WorkspaceRefreshMode.None)` so existing test doubles and non-provider
    implementations retain source compatibility.
  - The no-argument shape makes the complete-recall promise explicitly current-workspace-only.
- `src/Miller.Server/Workspaces/WorkspaceIndexProvider.cs`
  - Overrides the complete-current method.
  - Uses the existing current symbol-read context with a `completeRecall` flag.
  - The complete family-store branch uses the existing `_loadSessionSymbolSearch` projection loader
    through the existing symbol-read cache and telemetry wrapper.
  - Non-current selectors and ordinary `ResolveSymbolRead` routing are unchanged.
- `src/Miller.Server/Tools/EditTool.cs`
  - Uses complete recall for the initial context and the stale-recovery fresh-context callback.
- `tests/Miller.Tests/Server/WorkspaceIndexProviderTests.cs`
  - Proves the lagging sidecar misses a live-only symbol while complete recall finds it through the
    session projection.
- `tests/Miller.Tests/Server/EditToolTests.cs`
  - The provider double records complete calls separately. Preview proves one complete call and no
    ordinary call; stale recovery proves two complete calls and no ordinary calls.

No other files were modified by this packet. The concurrent worker has separate unowned modifications
in `WorkspaceRegistryPrune.cs` and `WorkspaceRegistryPruneTests.cs`; they are not part of this fix.

## GREEN and verification

Focused command required by the packet:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~WorkspaceIndexProviderTests|FullyQualifiedName~EditToolTests|FullyQualifiedName~IndexLevelContextTests"
Passed: 323, Failed: 0, Skipped: 0
```

`git diff --check` passed with exit 0.

Required Release builds were attempted in parallel after the focused tests passed:

```text
dotnet build src/Miller.Server/Miller.Server.csproj -c Release --no-restore
dotnet build tests/Miller.Tests/Miller.Tests.csproj -c Release --no-restore
```

Both exited 1 before compiling the owned change because the concurrent unowned edit at
`src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs:172` was syntactically incomplete:

```text
CS1026: ) expected
CS1514: { expected
CS1002: ; expected
CS1513: } expected
```

The lead must rerun both builds once the other worker's edit is repaired; this packet did not alter or
revert that file.

## Mutation and worktree state

- No staging or commit was performed.
- No MCP tool, cache, sidecar, storage schema, or caller-facing edit argument was added.
- Worktree: `/home/murphy/source/miller/.worktrees/tool-latency-health`
- Branch: `fix/tool-latency-health`
- HEAD: `31aac1fe3153a5a1adfa8ef60b814994a9e44934`
- Final observed state: dirty, with the five owned implementation/test files above, the report file,
  the concurrent worker's two unowned prune files, and an existing untracked Goldfish memory file.

## Scoped lead re-review correction

The complete-recall provider API was narrowed to the only supported use case:

- `IWorkspaceSymbolReadProvider.ResolveCompleteCurrentSymbolRead()` has no selector or refresh
  parameters and defaults to `ResolveSymbolRead(null, WorkspaceRefreshMode.None)`.
- `WorkspaceIndexProvider.ResolveCompleteCurrentSymbolRead()` always returns the current complete
  projection; it cannot accidentally promise complete recall for a registered selector.
- `EditTool` and its initial/retry test coverage use the no-argument method.

The focused union was rerun after this correction:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~WorkspaceIndexProviderTests|FullyQualifiedName~EditToolTests|FullyQualifiedName~IndexLevelContextTests"
Passed: 323, Failed: 0, Skipped: 0
```

`git diff --check` passed with exit 0. No staging or commit was performed.

## Concerns for lead review

- Rerun the two Release builds after `WorkspaceRegistryPrune.cs` is syntactically valid.
- Keep the no-argument complete-current path edit-only; do not route inspect/context/impact/trace
  through it.
