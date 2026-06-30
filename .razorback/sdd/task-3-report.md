# Task 3 Report: ASP.NET Minimal API and htmx Structural Fact Bridge

## Summary

- Added `WebStackStructuralFactReducer` in `Miller.Core.Graph`.
- Reduced `aspnet.minimal_api.route.v1` facts into existing `ControllerEndpoint` inputs.
- Reduced route-bearing `htmx.attribute.v1` facts into existing `TsClientCall` inputs with carriers like `htmx.get` and `htmx.post`.
- Merged reduced structural endpoints and htmx calls in `DotnetWebBridgeProvider` before `RouteBridge.Resolve`.
- Added provider evidence counts for `dotnet-web.structuralFacts`, `dotnet-web.aspnetMinimalRoutes`, and `dotnet-web.htmxCalls`.
- Added focused bridge graph tests for htmx GET hits, htmx POST versus MapGet mismatch, htmx non-route attribute rejection, evidence, and counters.

## Miller Calls Used

- `workspace status path=/Users/murphy/source/miller/.worktrees/web-stack-structural-facts-bridge`: confirmed the requested worktree was registered, fresh, and using current search/content sidecars.
- `content search/read` on `docs/plans/2026-06-30-web-stack-structural-facts-bridge.md`: confirmed Task 3 files, inputs, outputs, and acceptance criteria.
- `context` for Task 3 bridge work: identified `DotnetWebBridgeProvider`, `BridgeGraphBuilderTests`, `RouteBridge`, and the Task 2 structural fact seam as the relevant surfaces.
- `inspect` on `DotnetWebBridgeProvider`, `RouteBridge`, `BridgeGraphBuilderTests`, `StructuralFactRecord`, `BridgeProviderContext`, `TsClientCall`, `ControllerEndpoint`, and `RouteNormalizer`: confirmed existing contracts and route/verb behavior before edits.
- `trace refs` on `DotnetWebBridgeProvider` and `StructuralFactRecord`: confirmed reference shape and that structural facts were only carried through the new Task 2 seam.
- `impact target=DotnetWebBridgeProvider` and `impact git=true`: confirmed expected bridge graph and shared graph-construction test impact.
- `workspace refresh`: refreshed the worktree index after edits; refresh completed at revision 8.

## Verification Ledger

| Scope | Invariant | Command | Commit / Tree | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| Baseline worker scope | Existing branch worker slice was clean before Task 3 edits | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~SqliteBridgeReaderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"` | `b2b32df`, clean tree | PASS: 37 passed, 0 failed | 2026-06-30 UTC |
| TDD red | New tests fail because structural facts are not reduced yet | same worker command | working tree with tests only | EXPECTED FAIL: no htmx Hits edge; missing `dotnet-web.aspnetMinimalRoutes` count | 2026-06-30 UTC |
| Worker scope green | Task 3 bridge behavior and existing worker tests pass | same worker command | working tree after implementation | PASS: 40 passed, 0 failed | 2026-06-30 UTC |
| Fast suite | Shared graph callers remain compatible | `scripts/test.sh` | working tree after implementation | PASS: 2505 passed, 0 failed | 2026-06-30 UTC |
| Whitespace hygiene | No diff whitespace errors | `git diff --check` | working tree after implementation | PASS | 2026-06-30 UTC |

## Acceptance Checklist

- [x] htmx `hx-get` to ASP.NET `MapGet` produces a high-confidence `Hits` edge with both client and endpoint evidence.
- [x] htmx `hx-post` does not match `MapGet` for the same route.
- [x] htmx `hx-target` and other non-route htmx attributes do not produce client route calls.
- [x] Provider evidence counts include nonzero `dotnet-web.structuralFacts`, `dotnet-web.aspnetMinimalRoutes`, and `dotnet-web.htmxCalls` in the htmx fixture.
- [x] Worker-scope verification passes and the task is committed.

## Concerns Or Plan Mismatches

- No plan mismatches.
- Lead inline review found no remaining Task 3 issues.
- I did not modify Task 4 Vue behavior, `PatternsTool`, or MCP surface area.
- Miller does not parse source text in this slice; the reducer uses only `StructuralFactRecord` fields and `MetadataJson`.
- Minimal API structural endpoints intentionally leave response/request DTO fields empty. Task 3 only bridges route hits; inferring DTOs from minimal API signatures would be separate behavior.

## Commit SHA

- `88c8d02a3a16a570954e25daf172012f5506ded8`
