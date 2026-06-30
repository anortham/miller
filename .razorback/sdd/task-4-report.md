# Task 4 Report: Bridge Vue Route Facts

## Summary of Changes

- Extended `WebStackStructuralFactReducer` to consume `vue.route_reference.v1`.
- Reduced Vue facts with nonblank `target_path` and `verb=GET` into synthetic URL literals with:
  - `kind=url`
  - `language=vue`
  - `carrier=vue.get`
  - literal text from `target_path`
  - containing symbol/test status from `containing_symbol_id`
  - file/line evidence from the structural fact span
- Merged Vue calls into the existing `DotnetWebBridgeProvider` route reduction path before `RouteBridge.Resolve`.
- Added provider evidence count `dotnet-web.vueCalls`.
- Added graph and repository-loader tests for Vue RouterLink/router-link facts, bound `:to` literals, missing target metadata, and loader evidence counts.

## Miller Calls Used

- `workspace status` for `/Users/murphy/source/miller/.worktrees/web-stack-structural-facts-bridge`
  - Confirmed the isolated worktree was registered, fresh, and on the requested root.
- `context` for Task 4 reducer/provider/test scope
  - Confirmed the relevant entry points: `WebStackStructuralFactReducer`, `DotnetWebBridgeProvider`, `BridgeGraphBuilderTests`, and `RepositoryIndexLoaderBridgeTests`.
- `inspect WebStackStructuralFactReducer`
  - Confirmed Task 3 already reduced ASP.NET minimal routes and htmx calls, with no Vue branch.
- `inspect DotnetWebBridgeProvider`
  - Confirmed structural routes and htmx calls were already merged before `RouteBridge.Resolve`, and evidence counts were the right extension point.
- `inspect` on existing htmx graph tests and loader fixture methods
  - Confirmed the local test patterns and fixture helpers to extend.
- `trace WebStackStructuralFactReducer.Reduce` and `trace DotnetWebBridgeProvider.BuildCandidates`
  - Confirmed reducer use flows through the dotnet-web provider and provider use flows through bridge graph build/context paths.
- `impact` on planned reducer/provider changes and on the working-tree diff
  - Confirmed the touched surface is the bridge builder/provider path, with the assigned graph/indexing tests in scope.
- `workspace refresh`
  - Refreshed the edited worktree index to revision 12 before post-edit inspection.
- Post-edit `inspect`
  - Confirmed the reducer now has `VueRouteReferencePatternId` and the provider merges `structuralReduction.VueCalls`.

## Verification Ledger

| Scope | Invariant | Command | Commit SHA / Working Tree | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| Baseline worker scope | Task 3 base is green before Task 4 tests | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests\|FullyQualifiedName~SqliteBridgeReaderTests\|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"` | `94d65b4` | Passed: 40, Failed: 0 | 2026-06-30 session |
| TDD red | New Vue tests fail before production implementation for missing Vue route reduction/counting | Same worker command | Working tree before implementation | Failed as expected: 4 Vue failures, no unrelated failures | 2026-06-30 session |
| Worker green | Vue route-reference facts bridge through existing dotnet-web provider and loader fixture | Same worker command | `d380ef1229de35810df8030093a3e410314f2df6` | Passed: 44, Failed: 0 | 2026-06-30T15:23:34Z |
| Diff hygiene | No whitespace or conflict-marker issues | `git diff --check` | `d380ef1229de35810df8030093a3e410314f2df6` | Passed | 2026-06-30T15:23:34Z |

## Acceptance Checklist

- [x] Vue `RouterLink` or `router-link` route fact to `/todos` matches ASP.NET `MapGet("/todos", ...)`.
- [x] Vue bound `:to="'/todos'"` route fact matches ASP.NET `MapGet("/todos", ...)`.
- [x] Vue route facts without `target_path` or with nonliteral expressions produce no client calls.
- [x] Provider evidence counts include nonzero `dotnet-web.vueCalls` in the Vue fixture.
- [x] Worker-scope verification passes.
- [x] Task implementation committed.

## Concerns or Plan Mismatches

- No plan mismatches found.
- No Vue source parsing was added in Miller.
- `PatternsTool` was not edited.
- No MCP tool was added.
- Existing htmx behavior remains on the Task 3 reducer/provider path.

## Commit SHA

- Implementation: `d380ef1229de35810df8030093a3e410314f2df6`
