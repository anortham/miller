# Task 1 Report: Shared Structural Route Fact Adapter

## Status

Complete for worker scope. No commit was made because the task handoff explicitly says the checkout is already dirty and the lead owns committing after inline review.

## Files Changed

- Created `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
- Modified `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs`
- Modified `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

Note: `DotnetWebBridgeProvider.cs` and `BridgeGraphBuilderTests.cs` already had approved dirty changes before this task. This task layered the shared adapter extraction and focused adapter tests on top of that state without reverting or overwriting unrelated work.

## What Changed

- Added `StructuralRouteFactAdapter` as an internal mechanical adapter for structural route facts.
- Added `StructuralRouteReference` and `StructuralFileRoute` records.
- Moved frontend route metadata lookup and test-fact filtering out of `DotnetWebBridgeProvider`.
- Updated `DotnetWebBridgeProvider.ReduceStructuralClientCalls` to:
  - read route-reference facts through `TryReadRouteReference`;
  - read Next file-route facts through `TryReadFileRoute`;
  - convert adapter records into the existing `TsClientCall` rows consumed by `RouteBridge`.
- Added focused tests for:
  - route-reference extraction and default verb behavior;
  - file-route extraction;
  - test filtering via `SymbolDetail.IsTest` and test-like paths.

## Miller Calls Used

- `workspace status`: confirmed `/Users/murphy/source/miller` index was fresh before code orientation.
- `context` for Task 1 with `DotnetWebBridgeProvider` and `BridgeGraphBuilderTests`: confirmed the plan section, target provider, and test entry points.
- `content search/read` for `docs/plans/2026-07-01-nextjs-bridge-trace-support.md`: confirmed Task 1 files, interfaces, and acceptance criteria.
- `inspect src/Miller.Core/Graph/DotnetWebBridgeProvider.cs`: listed provider symbols and located the private structural frontend route helpers.
- `inspect ReduceStructuralClientCalls`, `TryReduceStructuralFrontendRouteFact`, `IsStructuralFrontendTestFact`, `IsFrontendRoutePattern`, `FrontendRoutePath`, `DefaultVerbForFrontendRouteFact`, `IsTestPath`, and `MetadataString`: confirmed the exact extraction behavior to preserve.
- `inspect BridgeGraphBuilderTests` plus target structural route tests: confirmed existing caller-facing provider tests and fixture shape.
- `inspect StructuralFactRecord`, `SymbolDetail`, `TsClientCall`, and `LiteralRecord`: confirmed consumed record shapes and `SymbolDetail.IsTest`.
- `impact TryReduceStructuralFrontendRouteFact`: confirmed local blast radius into `ReduceStructuralClientCalls` and `BuildCandidates`.
- `workspace refresh`: refreshed Miller to revision 6 after edits.
- `inspect StructuralRouteFactAdapter` and `ReduceStructuralClientCalls`: confirmed final adapter API and provider usage.
- `content search StructuralRouteFactAdapter`: confirmed final source/test references after extractor refs did not emit the new symbol refs.

## Tests Run

RED:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphBuilderTests"
```

Result: failed for the expected reason before production code existed:

- `CS0103: The name 'StructuralRouteFactAdapter' does not exist in the current context`

GREEN:

```bash
dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphBuilderTests"
```

Result:

- Passed: 31
- Failed: 0
- Skipped: 0
- Duration: 59 ms

Invariant proved: the adapter extraction preserves existing `BridgeGraphBuilder` structural route bridge behavior, including frontend route facts yielding high-confidence `BridgeKind.Hits` edges and test-path route facts being excluded.

## Acceptance Criteria

- [x] Existing `StructuralFacts_FrontendRouteFacts_YieldHitsEdgeToMinimalApiHandler` cases still produce one high-confidence `BridgeKind.Hits` edge.
- [x] Existing `StructuralFacts_FrontendRouteFacts_FromTestPaths_AreIgnored` still excludes test facts.
- [x] Adapter code has no ASP.NET-specific endpoint handling and no Next.js-specific matching policy.
- [x] Worker-scope verification passes.
- [ ] Changes committed.

Commit note: intentionally not done because the task handoff says not to commit; the lead will commit after inline review.

## Architecture Quality

**Affected modules:** `Miller.Core.Graph` and existing graph-builder tests.

**Caller-facing interface:** unchanged. `BridgeGraphBuilder.Build` remains the exercised behavior surface; no MCP or CLI surface changed.

**Depth/locality check:** route fact extraction and test filtering moved into a shared helper. Endpoint handling and `TsClientCall` conversion remain in `DotnetWebBridgeProvider`.

**Test surface:** existing provider-level `BridgeGraphBuilder` tests still prove the route bridge behavior; focused internal tests cover the newly exposed adapter contract that future tasks will consume.

**Seams/adapters:** `StructuralRouteFactAdapter` is a helper, not a provider abstraction. It does not emit bridge candidates.

**Rejected shortcuts:** did not move ASP.NET endpoint resolution into the adapter, did not add Next route matching, and did not change MCP/CLI contracts.

**Architecture risk:** low for this slice.

## Plan Mismatch Or Concerns

- Plan/task acceptance includes committing, but the direct task handoff overrides that with "DO NOT commit." No commit was made.
- The workspace had approved pre-existing dirty changes, including dirty changes in the files touched by this task. I did not revert or normalize those unrelated changes.
