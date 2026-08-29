# External review fix: prune safety and producer-work bound

## Outcome

Fixed the two approved `WorkspaceRegistryPrune` review findings without changing the
MCP/result shape or exact targeted removal path.

- A missing-root family-store member is eligible for producer retirement only when the
  persisted registry row proves a removed linked worktree: `GitIsLinked == true`, a
  nonblank `GitDir`, an existing `GitDir` parent, and neither a directory nor a file at
  `GitDir`.
- Unknown lineage, an unavailable admin parent, or an existing admin directory keeps the
  registry/store member and reports an actionable existing `RetirementFailure`. The error
  says that linked-worktree removal is not confirmed and directs the caller to exact
  `workspace remove` after confirming removal; it does not claim the producer is unhealthy.
- A prune invocation attempts at most one confirmed family-store producer target. The
  budget is consumed before preview/apply, including failed producer attempts, and applies
  to dry-run and apply. Further targets remain registered with a rerun hint.
- Missing non-store rows retain the previous prune behavior.

## Miller/API-shape evidence

The task worktree was registered as `tool-latency-health-a83c6af15018`. Miller inspection
confirmed:

- `WorkspaceRegistryPrune.Run` is the public six-parameter entry point and its existing
  `RetirementFailure` carries `StoreViewRetirementOutcome`.
- `WorkspaceRegistryRow` persists `GitIsLinked`, `GitDir`, and `GitDirCreatedAtUtc`.
- `WorkspaceLineage` and `WorkspaceRegistry.UpsertSeen` are the existing persistence path
  for those fields; no registry column or public contract was added.
- Exact `Run` callers are the dashboard redirect, CLI prune, workspace tool, and existing
  prune tests. The pre-edit impact report identified the prune tests plus the tool/render
  surfaces as the focused verification set.
- `WorkspaceRemoval.TryRetireView` already performs the validated preview/apply sequence;
  the fix gates that existing call and does not add a producer command or lease.

## TDD evidence

Tests were added before the production change. The first usable RED run was:

```text
dotnet test --filter "FullyQualifiedName~WorkspaceRegistryPruneTests"
Failed: 3, Passed: 18, Skipped: 0, Total: 21
```

The failures were the expected missing-lineage action assertion and the dry-run/apply
one-target cap assertions. An earlier attempt was temporarily blocked by an unrelated
concurrent worker's incomplete `WorkspaceIndexProvider` test edit; once that source edit
was present, the behavioral RED above was captured.

After implementation:

```text
dotnet test --filter "FullyQualifiedName~WorkspaceRegistryPruneTests"
Passed: 21, Failed: 0, Skipped: 0

dotnet test --filter "FullyQualifiedName~WorkspaceRegistryPruneTests|FullyQualifiedName~WorkspaceToolPruneTests|FullyQualifiedName~WorkspaceRenderTests|FullyQualifiedName~WorkspaceToolTests"
Passed: 256, Failed: 0, Skipped: 0
```

The workspace-tool prune assertion was updated to the same honest diagnostic, and the
256-test union was rerun successfully after that correction.

The new tests cover confirmed linked-worktree removal, unavailable lineage with an
actionable kept row, non-store preservation, and one-target dry-run/apply caps. They also
assert that retained rows and store memberships remain and that no producer callback runs
when lineage is not confirmed.

## Verification

```text
dotnet build src/Miller.Server/Miller.Server.csproj -c Release
Build succeeded. 0 Warning(s), 0 Error(s)

dotnet build tests/Miller.Tests/Miller.Tests.csproj -c Release
Build succeeded. 0 Warning(s), 0 Error(s)

git diff --check
clean
```

Per the bounded packet, the bare suite, Scale suite, secrets scan, and dependency audit
were not run. Exact targeted removal and the deliberate missing-extractor refusal were
not changed.

## Exact modified files owned by this packet

- `src/Miller.Server/Workspaces/WorkspaceRegistryPrune.cs`
- `tests/Miller.Tests/Server/WorkspaceRegistryPruneTests.cs`
- `tests/Miller.Tests/Server/WorkspaceToolPruneTests.cs`
- `.razorback/sdd/2026-08-29-direction-aware-impact-reads/review-fix-prune-report.md`

## Mutation/worktree state

The tests use temporary registries and filesystem paths. The unavailable-lineage and
budget cases verify no producer call and retained registry/store rows; the existing dry-run
tests verify no sidecar deletion. No persistent store or workspace registry was modified.

Final state when this report was written:

```text
path: /home/murphy/source/miller/.worktrees/tool-latency-health
branch: fix/tool-latency-health
HEAD: 31aac1fe3153a5a1adfa8ef60b814994a9e44934
git status: dirty
worktrees:
  /home/murphy/source/miller [main] 058199ca
  /home/murphy/source/miller/.worktrees/tool-latency-health [fix/tool-latency-health] 31aac1fe
```

The task worktree also contains concurrent, unowned edits in `EditTool`,
`IWorkspaceSymbolReadProvider`, `WorkspaceIndexProvider`, and their tests, plus an
untracked Goldfish memory file. This packet did not edit, stage, or commit those files.
