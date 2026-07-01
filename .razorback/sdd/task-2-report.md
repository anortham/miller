# Task 2 Report: Next Route Matching And Navigation Edges

## Status

Implemented Task 2 on branch `nextjs-bridge-trace` without committing. The existing report file contained stale content from a different task, so this report replaces it.

## Files Changed

- Created `src/Miller.Core/Resolver/NextRouteMatcher.cs`
- Created `src/Miller.Core/Resolver/NextRouteBridge.cs`
- Modified `src/Miller.Core/Resolver/BridgeKind.cs`
- Modified `src/Miller.Core/Resolver/Signal.cs`
- Modified `src/Miller.Core/Graph/BridgeGraph.cs`
- Modified `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`
- Modified `tests/Miller.Tests/Graph/BridgeGraphTests.cs`
- Modified `tests/Miller.Tests/Resolver/BridgeScorerTests.cs`
- Updated `.razorback/sdd/task-2-report.md`

Pre-existing dirty changes from Task 1 and other approved work were preserved.

## Miller Calls Used

- `workspace status` on `/Users/murphy/source/miller`
  - Confirmed the workspace index was fresh on branch `nextjs-bridge-trace`.
- `context` for Task 2 with `BridgeKind`, `SignalRule`, `BridgeGraph`, `BridgeScorer`, `StructuralRouteReference`, `StructuralFileRoute`, and `CandidateEdge`
  - Confirmed the relevant production seams and test surfaces for the approved plan.
- `inspect` on `BridgeKind`, `SignalRule`, `BridgeGraph`, `BridgeScorer`, `StructuralRouteFactAdapter`, `CandidateEdge`, `EdgeRef`, and the three target test files
  - Confirmed current enum values, scorer behavior, graph node-kind mapping, Task 1 route records, and local test style before editing.
- `impact target=BridgeKind`
  - Confirmed the planned worker tests cover the immediate enum/scorer/graph impact; `TraceTool` rendering surfaced as downstream Task 4 work.
- `workspace refresh`
  - Polled/swapped to revision 15 after new files were created; the leader watcher had kept the index fresh.
- `inspect` on `NextRouteBridge` and `NextRouteMatcher`
  - Confirmed the new indexed symbols and pure resolver/matcher bodies after implementation.
- `trace mode=refs` for `BridgeKind.NavigatesTo`, `SignalRule.RouteReferenceMatch`, and `BridgeNodeKind.NextRoute`
  - Confirmed references are limited to the new resolver, graph mapping, and targeted tests in this slice.
- `impact changed_paths=[Task 2 files]`
  - Rechecked downstream impact. It was broader/noisier than the task scope because the workspace already has many dirty bridge changes, but the actionable Task 2 surface stayed aligned with the brief.

## Tests Run

- Red check:
  - Command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphTests|FullyQualifiedName~BridgeScorerTests|FullyQualifiedName~BridgeGraphBuilderTests"`
  - Result: failed as expected with `CS0117: 'SignalRule' does not contain a definition for 'RouteReferenceMatch'`.
  - Proved the new tests hit the missing Task 2 scoring surface before implementation.
- Green worker verification:
  - Command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~BridgeGraphTests|FullyQualifiedName~BridgeScorerTests|FullyQualifiedName~BridgeGraphBuilderTests"`
  - Result: passed, 71 passed, 0 failed, 0 skipped.
  - Proves the new bridge enum/scorer/node-kind semantics and pure Next route matching behavior while keeping the focused graph builder tests green.

## Acceptance Criteria

- [x] Static reference `/settings` connects to file route `/settings` with `BridgeKind.NavigatesTo`.
- [x] Dynamic reference `/users/123` connects to `/users/[id]` and `/users/{}` with High confidence.
- [x] Catch-all reference `/docs/a/b` connects to `/docs/[...slug]`; `/docs` does not.
- [x] Optional catch-all reference `/docs` and `/docs/a/b` both connect to `/docs/[[...slug]]`.
- [x] Route-group file route `/(admin)/settings` matches reference `/settings`.
- [x] Ambiguous file route matches produce no navigation edge.
- [x] `BridgeGraph.NodeKindFor(BridgeKind.NavigatesTo, Target)` returns `BridgeNodeKind.NextRoute`.
- [x] Worker-scope verification passes.

## Plan Mismatch Or Concerns

- The approved plan acceptance text says changes are committed, but the task handoff explicitly said `DO NOT commit`; no commit was made.
- Provider evidence counts for ambiguity were intentionally not implemented in this task per the clarification. Task 3 owns Next.js provider evidence counts.
- `TraceTool` rendering/JSON support for `NavigatesTo` is not included here; that remains Task 4.
