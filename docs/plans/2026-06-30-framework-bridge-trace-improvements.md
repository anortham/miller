# Framework Bridge Trace Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make `trace mode=bridge` consume framework structural facts precisely enough for htmx/Vue dogfooding and reusable enough to guide future framework bridges.

**Architecture:** Keep the existing `trace` MCP/CLI surface. Deepen the bridge internals by carrying route-target intent through target resolution, rendering diagnostics from bridge candidates, and representing structural endpoints that do not have extracted handler symbols as synthetic bridge nodes. The dotnet-web provider remains the first concrete adapter; future framework support should follow the same fact-to-candidate-to-edge pattern.

**Tech Stack:** .NET 10, Miller.Core bridge graph, Miller.Server `TraceTool`, julie-extractors `structural_facts`.

**Architecture Quality:** Affected modules are `Miller.Core.Graph` and `Miller.Server.Tools.TraceTool`. Caller-facing interface remains `trace mode=bridge`; the deeper interface is an internal bridge target context and bridge candidate metadata. Architecture risk is medium because this sets the reusable pattern for additional framework bridges.

## Global Constraints

- Do not add a new MCP tool.
- Do not move parser recognition into Miller; consume `structural_facts` emitted by julie-extractors.
- Test behavior through the caller-facing bridge interfaces: `BridgeGraphBuilder` and `TraceTool.Run`.
- URL route targets must be more precise than symbol targets when route evidence exists.
- Diagnostics must distinguish missing frontend facts, missing backend facts, and missing matched pairs.
- Synthetic endpoints must be clearly represented as bridge endpoint nodes, not fake code symbols.

---

## File Structure

- Modify: `src/Miller.Core/Graph/BridgeGraph.cs`
  - Holds bridge-node metadata needed for route lookups and synthetic endpoints.
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs`
  - Converts structural frontend and ASP.NET route facts into bridge candidates and synthetic endpoint nodes.
- Modify: `src/Miller.Server/Tools/TraceTool.cs`
  - Resolves route-string targets with route filters and renders no-match diagnostics.
- Modify: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`
  - Proves provider-level structural fact behavior.
- Modify: `tests/Miller.Tests/Tools/TraceToolTests.cs`
  - Proves user-facing trace behavior.
- Keep: `src/Miller.Indexing/SqliteBridgeReader.cs`
  - Existing fact ingestion remains sufficient for this slice unless tests expose a reader gap.

## Task 1: Precise Route-String Bridge Targets

**Interfaces:**
- Consumes: `TraceTool.Run(... mode: "bridge" ...)`, `BridgeGraph.Edges`, `BridgeGraph.Walk`.
- Produces: a route-scoped bridge run where a URL target emits only `Hits` edges whose route matches that URL.

**What to build:** When the target is a route string such as `/admin/connectors/save`, resolve the owning bridge start node as today, but carry the normalized route into the walk/render step. Filter emitted bridge edges to route-matching `Hits` edges while preserving normal symbol target behavior.

**Approach:** Add an internal route filter/result object in `TraceTool` rather than changing the public tool signature. Apply the filter after graph walking and before limit/rendering. JSON and compact output must agree.

**Acceptance criteria:**
- [x] A route string targeting one of several component routes emits only the matching route edge.
- [x] Symbol targets still emit all incident bridge edges.
- [x] Compact and JSON output both respect the same filter.
- [x] Worker-scope verification passes.

## Task 2: Actionable Frontend/Backend Route Diagnostics

**Interfaces:**
- Consumes: bridge graph edges and route-bearing bridge nodes.
- Produces: compact and JSON diagnostics that explain route facts seen and why no edge exists.

**What to build:** For route-string targets with no matched bridge edge, render a diagnostic that distinguishes: no frontend route facts, frontend facts exist but no backend route facts, backend facts exist but none match, and route facts exist but no symbol/synthetic endpoint could be linked.

**Approach:** Use bridge graph node/edge metadata rather than querying SQLite from `TraceTool`. Provider adapters should leave enough node metadata for `TraceTool` to explain what it knows. Keep diagnostic text bounded by listing a few observed routes.

**Acceptance criteria:**
- [x] A Vue route fact with no matching backend endpoint reports the frontend route exists and names observed backend routes.
- [x] A backend route fact with no matching frontend route reports the backend route exists and names observed frontend routes.
- [x] A route with neither side present keeps the existing not-found guidance.
- [x] JSON diagnostics carry a stable code and message.
- [x] Worker-scope verification passes.

## Task 3: Synthetic Structural Endpoints

**Interfaces:**
- Consumes: `aspnet.minimal_api.route.v1` facts with no handler symbol, especially `handler_kind=lambda`.
- Produces: bridge endpoint nodes identified by structural fact identity/file/line, with display based on HTTP verb and route.

**What to build:** Represent structural ASP.NET endpoints without handler symbols as synthetic endpoint nodes so bridge traces can still show route linkage and diagnostics can count backend route facts.

**Approach:** Do not create fake `SymbolDetail` rows. Extend bridge candidate creation to emit endpoint `EdgeRef`s with no `SymbolId` but with stable display/file metadata and a synthetic `BridgeNode`. Keep service-parameter `Consumes` edges disabled for synthetic endpoints.

**Acceptance criteria:**
- [x] A Vue/htmx route fact matching a lambda minimal API route yields a high-confidence `Hits` edge to a synthetic endpoint node.
- [x] Synthetic endpoints appear as endpoint nodes with file and line evidence.
- [x] Unmatched frontend/backend route facts are retained as observation nodes for diagnostics.
- [x] Non-route DTO/entity/table bridge behavior is unchanged.
- [x] Worker-scope verification passes.

## Task 4: Reusable Framework Adapter Pattern

**Interfaces:**
- Consumes: provider-private reducers in `DotnetWebBridgeProvider`.
- Produces: named helper types/functions that make future framework support follow the same shape without copying ad hoc JSON parsing.

**What to build:** Refactor the structural fact reduction into clear helper units for frontend route facts and backend endpoint facts. The goal is legibility and repeatability, not a new public abstraction.

**Approach:** Keep helpers private unless a second provider proves a public seam. Names should reflect the durable concepts: frontend route observation, backend route endpoint, synthetic endpoint node, route evidence. Avoid speculative provider registries.

**Acceptance criteria:**
- [x] htmx, Vue, and ASP.NET route handling are readable as instances of the same pattern.
- [x] Tests still cover behavior through `BridgeGraphBuilder` and `TraceTool.Run`, not private helpers.
- [x] No new MCP tool or extractor ownership moves into Miller.
- [x] Worker-scope verification passes.

## Verification Strategy

**Project source of truth:** `AGENTS.md` and `CLAUDE.md`.

**Worker red/green scope:** Focused tests:
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~TraceToolTests -v minimal`
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~BridgeGraphBuilderTests -v minimal`

**Worker ceiling:** Full fast suite via `scripts/test.sh`.

**Worker gate invariant:** Route targets are precise, missing bridge evidence is explained, synthetic endpoints can be traced, and existing bridge behavior remains green.

**Lead affected-change scope:** `mcp__miller.impact` over the working diff plus focused tests.

**Branch gate:** `dotnet build Miller.slnx -c Release`, `scripts/test.sh`, and scale suite only if indexing/extractor read behavior changes materially.

**Escalation triggers:** SQLite reader contract changes, julie-extractors schema changes, or any failure in non-bridge trace modes.

**Assigned verification failure:** Investigate and fix within this plan unless the failure requires a product decision or extractor release.
