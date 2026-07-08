# Task 5 Report: All-workspaces view hygiene

**Status:** DONE
**commit SHA:** 08acb8f
**Worktree:** `/Users/murphy/source/miller/.worktrees/dashboard-registry-hygiene`

## Summary

The dashboard all-workspaces view now separates live registrations from stale registrations. `ReadIndex` annotates every row with `RootExists`, returns live/missing/error counts, and the Razor view renders live rows first with stale rows collapsed under a prune hint.

## Implementation

- `DashboardWorkspaceIndexEntry` gained `RootExists` plus an `IsStale` helper.
- `DashboardWorkspaceIndex` gained `LiveCount`, `MissingRootCount`, and `ErrorCount`.
- `DashboardData.ReadIndex` computes `Directory.Exists(canonical_root)` once per registry row and counts live, missing-root, and error rows.
- `WorkspaceIndex.razor` groups rows into live and stale sections while preserving existing row markup and client-side filtering.
- `WorkspacesShell.razor` shows live/registered machine totals and updated copy for the stale section.

## Verification

| Scope | Command | Result |
|---|---|---|
| focused | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~DashboardRegistryReadTests"` | Passed |
| focused review rerun | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorkspaceToolPruneTests|FullyQualifiedName~DashboardRegistryReadTests|FullyQualifiedName~RegistryIsolationConventionTests|FullyQualifiedName~WorkspaceBindingServiceTests"` | Passed: 53, Failed: 0 |
| branch gate | `scripts/test.sh` | Passed: 3051, Failed: 0 |
| branch build | `dotnet build Miller.slnx -c Release` | 0 warnings, 0 errors |
| all gate | `scripts/test.sh all` | Fast: 3051 passed; Scale: 8 passed, 40 skipped because `.tools/julie-extract` is absent in this worktree |

## Live dashboard evidence

Verified against the branch dashboard DLL in a foreground session because this shell runner terminates detached child processes after the launching shell exits.

- `/` returned HTTP 200.
- `/workspace?workspace_id=b45f89f1a795205c6dbb1467face1ab6af89ca5270850036409a36213a6b5d0e` returned HTTP 200.
- `/index.json` returned:
  - `workspace_count: 55`
  - `live_count: 54`
  - `missing_root_count: 0`
  - `error_count: 1`
- Rendered `/` contained `55 registered · 54 live · click to inspect`.
- Rendered `/` contained `1 stale registrations — run miller workspace prune to clean up`.

## Acceptance criteria

- [x] `ReadIndex` returns `RootExists` per entry and correct live/missing/error counts.
- [x] All-workspaces view groups live entries above a de-emphasized stale section with the prune hint.
- [x] Worker-scope and branch verification passed.

## Notes

The single stale row after cleanup is an existing-root `error` registration, not a missing root. That matches the Task 5 grouping rule: stale means missing root or errored.
