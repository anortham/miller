# Task 4 Report: Trace Rendering, Route Targets, And Diagnostics

## Status

Implemented Task 4 for the approved Next.js bridge trace plan in the dirty `nextjs-bridge-trace` checkout. No commit was created.

## Files Changed

- `src/Miller.Server/Tools/TraceTool.cs`
- `tests/Miller.Tests/Tools/TraceToolTests.cs`
- `.razorback/sdd/task-4-report.md`

## Miller Calls Used

- `workspace(status)`: confirmed `/Users/murphy/source/miller` was indexed, fresh, and on the expected workspace before edits.
- `context(query="Task 4 Next.js bridge trace rendering route string targets diagnostics TraceTool NavigatesTo NextRoute TraceToolTests")`: confirmed the relevant plan section, `TraceTool`, `TraceToolTests`, `BridgeKind.NavigatesTo`, and `BridgeNodeKind.NextRoute`.
- `inspect(target="src/Miller.Server/Tools/TraceTool.cs")`: confirmed the allowed TraceTool symbols and line-area functions for route filtering, diagnostics, rendering, and JSON.
- `inspect(target="tests/Miller.Tests/Tools/TraceToolTests.cs")`: confirmed existing bridge route-string test patterns and the target test region.
- `inspect` on `RunBridge`, `FilterRouteTargetEdges`, `TryBuildRouteDiagnostic`, `TryResolveBridgeRouteTarget`, `BridgeLine`, `RenderBridgeJson`, `WriteBridgeLink`, `BridgeKindJson`, and `BridgeNodeKindJson`: confirmed the exact rendering and JSON helpers to change.
- `inspect` on `BridgeGraph.NodeIdOf`, `BridgeGraph.NodeKindFor`, `BridgeKind`, `NextRouteBridge`, `NextJsBridgeProvider.BuildCandidates`, and `NextJsBridgeProvider.BuildObservationNodes`: confirmed Task 1-3 contracts for `NavigatesTo`, `NextRoute`, observation nodes, and `nextjs.*` evidence counts.
- `impact(target="TraceTool")`: confirmed the relevant worker test surface included `TraceToolTests`.
- `impact(changed_paths=[TraceTool.cs, TraceToolTests.cs])`: checked post-edit blast radius; output was broad/noisy because the checkout already contains approved dirty Tasks 1-3 changes, so the explicit worker gate remained the controlling verification.

## Implementation Summary

- Route-string bridge targets now match route-bearing `BridgeKind.Hits` and `BridgeKind.NavigatesTo` edges.
- Route filtering keeps matching HTTP route hits or Next navigation edges, then follows non-route downstream bridge edges as before.
- Observation-only route target resolution now checks observed `TsType`, `Endpoint`, and `NextRoute` nodes by display-normalized route, not only by one synthesized ID shape.
- Diagnostics now prefer Next.js-specific route messages when `nextjs` evidence or `NextRoute` facts are present:
  - `nextjs_route_no_file_match`
  - `nextjs_route_no_reference_match`
  - `nextjs_route_ambiguous_file_match`
  - `nextjs_route_no_bridge_link`
- Compact labels now render `BridgeKind.NavigatesTo` as `--navigates_to-->`.
- JSON now emits `kind: "navigates_to"` and node kind `"next_route"`.

## Tests Run

- RED: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~Bridge_RouteStringTarget_NextJs"`
  - Result: failed 5/5 as expected before production changes.
  - Proved the new tests were exercising missing behavior: no Next navigation edge emitted, and missing diagnostics returned generic `trace_note`.
- GREEN focused: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~Bridge_RouteStringTarget_NextJs"`
  - Result: passed 5/5.
  - Proved pure Next route-string rendering, JSON enum/node output, and Next-specific diagnostics.
- Worker gate: `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "FullyQualifiedName~TraceToolTests"`
  - Result: passed 57/57.
  - Proved the full TraceTool worker scope, including existing dotnet-web route diagnostics, still passes.
- Whitespace check: `git diff --check -- src/Miller.Server/Tools/TraceTool.cs tests/Miller.Tests/Tools/TraceToolTests.cs`
  - Result: passed with no output.

## Acceptance Checklist

- [x] `TraceTool.Run(... target: "/settings", mode: "bridge" ...)` in a pure Next graph emits the matching navigation edge.
- [x] Compact output labels Next navigation distinctly from HTTP route hits.
- [x] JSON output includes `kind: "navigates_to"` and node kind `next_route`.
- [x] A route reference with no matching file route returns `nextjs_route_no_file_match`.
- [x] A file route with no matching reference returns `nextjs_route_no_reference_match`.
- [x] Ambiguous file-route matches return `nextjs_route_ambiguous_file_match`.
- [x] Existing frontend/backend diagnostics for `dotnet-web` still pass unchanged.
- [x] Worker-scope verification passes.

## Plan Mismatch Or Concerns

- No plan mismatch found.
- The ambiguous Next.js diagnostic uses provider evidence counts plus observed route nodes. `TraceTool` still does not parse structural facts or re-run Next route matching, per plan constraints.
- The checkout had approved pre-existing dirty changes, including prior edits in `TraceTool.cs` and `TraceToolTests.cs`; this report does not attempt to separate or revert them.
