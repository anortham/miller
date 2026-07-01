# Next.js Bridge Trace Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Make `trace mode=bridge` resolve pure Next.js route references to Next.js file routes without requiring ASP.NET backend evidence.

**Architecture:** Keep the existing `trace` MCP/CLI surface and the provider-scoped bridge graph. Add a concrete `nextjs` bridge provider that consumes `nextjs.route_reference.v1` and `nextjs.file_route.v1` structural facts, emits navigation edges, and leaves `dotnet-web` responsible for frontend-to-ASP.NET bridges. Extract shared structural-route helpers so future framework providers follow the same fact-to-candidate-to-edge pattern instead of copying private `dotnet-web` logic.

**Tech Stack:** .NET 10, Miller.Core bridge graph and resolver contracts, Miller.Indexing provider selection, Miller.Server `TraceTool`, julie-extractors `structural_facts`.

**Architecture Quality:** Affected modules are `Miller.Core.Graph`, `Miller.Core.Resolver`, `Miller.Indexing`, and `Miller.Server.Tools.TraceTool`. Caller-facing interface remains `trace mode=bridge`; the internal interface adds a `nextjs` provider plus shared structural route adapters. Architecture risk is medium because this establishes the reusable pattern for framework-only providers and touches bridge JSON/compact rendering.

## Global Constraints

- Do not add a new MCP tool.
- Do not move parser recognition into Miller; consume `structural_facts` emitted by julie-extractors.
- Support pure Next.js route navigation with the released facts `nextjs.route_reference.v1` and `nextjs.file_route.v1`.
- Do not claim Next.js API route handlers, server actions, middleware rewrites, `basePath`, locale routing, or runtime route generation unless julie-extractors emits stable facts for them.
- Keep `dotnet-web` behavior for htmx/Vue/React/Next-to-ASP.NET bridge traces unchanged except for shared helper extraction.
- Keep `patterns` as the raw structural-fact surface; relationship resolution belongs in `trace mode=bridge`.
- Default provider selection must let a pure Next.js workspace use bridge trace without a root `miller.json`.
- Explicit `miller.json` `bridge.providers` remains authoritative: a configured provider list runs only the named providers.
- Bridge JSON changes must be additive except for adding new enum values for new edge/node kinds.
- Test behavior through caller-facing interfaces: `BridgeGraphBuilder`, `RepositoryIndexLoader`, and `TraceTool.Run`.
- Use TDD for each task: add the failing behavior test first, verify the failure, implement, then verify the pass.

---

## Architecture Quality

**Affected modules:** `Miller.Core.Graph` provider contracts and graph node classification; `Miller.Core.Resolver` bridge edge kinds, signals, and route matching; `Miller.Indexing` provider selection; `Miller.Server.Tools.TraceTool` bridge target resolution, rendering, and diagnostics; bridge docs and skills.

**Caller-facing interface:** The public interface remains `trace mode=bridge` and `trace(format="json")`. Internally, `IBridgeProvider.BuildCandidates(BridgeProviderContext)` remains the provider seam; this plan adds `NextJsBridgeProvider` and shared route-fact adapters without changing the MCP tool signature.

**Depth/locality check:** Framework-specific fact reduction stays behind providers. Shared code handles only framework-neutral structural route extraction, test-path filtering, and route matching primitives; it must not know about ASP.NET handlers or Next.js application semantics beyond metadata already emitted as facts.

**Test surface:** Unit tests should prove provider behavior through `BridgeGraphBuilder.Build`, repository loading through `RepositoryIndexLoader.Load`, and user-facing behavior through `TraceTool.Run`. Private helpers are not the primary test surface unless route normalization needs focused edge-case coverage.

**Seams/adapters:** `IBridgeProvider` is the adapter seam. `StructuralRouteFactAdapter` is a shared helper, not a second provider abstraction. `NextRouteBridge` is the resolver that turns Next route references plus file routes into scored candidate edges.

**Rejected shortcuts:** Do not reuse `BridgeKind.Hits` for Next navigation; HTTP calls and client-side navigation are different relationships. Do not label Next page routes as ASP.NET `Endpoint` nodes. Do not query SQLite from `TraceTool` to patch diagnostics after graph construction. Do not parse `next.config.*` or package files in Miller for this slice.

**Architecture risk:** medium. The core risk is keeping graph/rendering semantics general enough for future framework providers while avoiding speculative routing support that extractor facts do not justify.

## File Structure

- Create: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
  - Shared extraction for route-bearing structural facts, metadata key lookup, route display normalization, and test-path filtering.
- Create: `src/Miller.Core/Graph/NextJsBridgeProvider.cs`
  - Concrete `IBridgeProvider` for `nextjs.route_reference.v1` and `nextjs.file_route.v1`.
- Create: `src/Miller.Core/Resolver/NextRouteBridge.cs`
  - Pure resolver that emits navigation `CandidateEdge` rows from Next route references to file routes.
- Create: `src/Miller.Core/Resolver/NextRouteMatcher.cs`
  - Pure matching for static, dynamic, catch-all, optional catch-all, and route-group path shapes.
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:295-383`
  - Replace private structural frontend route extraction and test filtering with shared adapter calls.
- Modify: `src/Miller.Core/Graph/BridgeGraph.cs:11-26,220-246`
  - Add Next route node classification and map the new navigation edge kind to node kinds.
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:22`
  - Include the `nextjs` provider in default provider construction.
- Modify: `src/Miller.Core/Resolver/BridgeKind.cs:8-34`
  - Add a navigation relationship kind for route reference to file route.
- Modify: `src/Miller.Core/Resolver/Signal.cs:10-49`
  - Add a structural signal rule for Next route-reference/file-route matches.
- Modify: `src/Miller.Indexing/BridgeProviderSelection.cs:8-64`
  - Add `nextjs` to default provider selection and explicit `miller.json` provider creation.
- Modify: `src/Miller.Server/Tools/TraceTool.cs:722-845,969-1026,1093-1147,1557-1889`
  - Include Next route edges in route-string target resolution, filtering, diagnostics, compact labels, and JSON enum serialization.
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md:41-46`
  - Update bridge scope language to mention provider-scoped `dotnet-web` and `nextjs` support.
- Modify: `docs/contracts/trace-json-v1.md:106-133`
  - Document the new bridge provider and additive edge/node enum values.
- Modify: `README.md:270-273,627-631`
  - Update bridge scope wording without turning bridge trace into an all-stack claim.
- Modify: `skills/miller-bridge-trace/SKILL.md:3-67`
  - Update the repository skill copy to mention Next.js file-route navigation.
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md:3-67`
  - Keep the mirrored agent skill in step with `skills/miller-bridge-trace/SKILL.md`.
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:549-759`
  - Provider-level structural fact and graph behavior.
- Test: `tests/Miller.Tests/Graph/BridgeGraphTests.cs:54-90`
  - Node id and node kind behavior for the new relationship.
- Test: `tests/Miller.Tests/Resolver/BridgeScorerTests.cs:188-230`
  - Confidence behavior for the new structural signal.
- Test: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:248-323`
  - Default/configured provider loading and pure Next graph population.
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs:993-1168,1384-1421`
  - Compact and JSON bridge output for route-string targets, provider status, and labels.
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs:22-136`
  - Embedded tool guidance stays current and within instruction budgets.

## Task 1: Shared Structural Route Fact Adapter

**Files:**
- Create: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:295-383`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:549-759`

**Interfaces:**
- Consumes: `StructuralFactRecord`, `SymbolDetail.IsTest`, and structural metadata keys `target_path`, `attribute_value`, `normalized_route_template`, and `route_path`.
- Produces: `internal static class StructuralRouteFactAdapter` with `TryReadRouteReference(...)`, `TryReadFileRoute(...)`, `IsTestFact(...)`, and route-bearing records `StructuralRouteReference` and `StructuralFileRoute`.

**What to build:** Extract the frontend route fact reduction currently private in `DotnetWebBridgeProvider` into a shared adapter. Preserve all current htmx/Vue/React/Next-to-ASP.NET behavior while making the same tested route-reference and test-path filtering available to `NextJsBridgeProvider`.

**Approach:** Keep the adapter mechanical and data-shaped: it reads fact metadata, rejects test-path/container facts, returns normalized route-bearing records, and does not emit bridge candidates. `DotnetWebBridgeProvider.ReduceStructuralClientCalls` should use the adapter and still produce the same `TsClientCall` rows for `RouteBridge`.

**Acceptance criteria:**
- [x] Existing `StructuralFacts_FrontendRouteFacts_YieldHitsEdgeToMinimalApiHandler` cases still produce one high-confidence `BridgeKind.Hits` edge.
- [x] Existing `StructuralFacts_FrontendRouteFacts_FromTestPaths_AreIgnored` still excludes test facts.
- [x] Adapter code has no ASP.NET-specific endpoint handling and no Next.js-specific matching policy.
- [x] Worker-scope verification passes; commit deferred because this checkout had approved pre-existing dirty bridge changes.

## Task 2: Next Route Matching And Navigation Edges

**Files:**
- Create: `src/Miller.Core/Resolver/NextRouteMatcher.cs`
- Create: `src/Miller.Core/Resolver/NextRouteBridge.cs`
- Modify: `src/Miller.Core/Resolver/BridgeKind.cs:8-34`
- Modify: `src/Miller.Core/Resolver/Signal.cs:10-49`
- Modify: `src/Miller.Core/Graph/BridgeGraph.cs:11-26,220-246`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:549-759`
- Test: `tests/Miller.Tests/Graph/BridgeGraphTests.cs:54-90`
- Test: `tests/Miller.Tests/Resolver/BridgeScorerTests.cs:188-230`

**Interfaces:**
- Consumes: `StructuralRouteReference` and `StructuralFileRoute` from Task 1.
- Produces: `NextRouteBridge.Resolve(IReadOnlyList<StructuralRouteReference> references, IReadOnlyList<StructuralFileRoute> fileRoutes)` returning `IReadOnlyList<CandidateEdge>`.
- Produces: new `BridgeKind.NavigatesTo`, new `SignalRule.RouteReferenceMatch`, and new `BridgeNodeKind.NextRoute`.

**What to build:** Add the pure route matcher and candidate-edge resolver for Next.js navigation. A route reference such as `<Link href="/settings">` or `router.push("/users/123")` should connect to the matching file route fact for `app/settings/page.tsx`, `pages/settings.tsx`, or `app/users/[id]/page.tsx`.

**Approach:** `NextRouteMatcher` should match static routes exactly, treat `[id]` as one dynamic segment, treat `[...slug]` as one-or-more trailing segments, treat `[[...slug]]` as zero-or-more trailing segments, and ignore route groups like `(admin)` when comparing route templates. Matching emits `SignalRule.RouteReferenceMatch` as a structural breadcrumb. Ambiguous matches should emit no edge and leave enough provider evidence counts for diagnostics rather than choosing a route arbitrarily.

**Acceptance criteria:**
- [x] Static reference `/settings` connects to file route `/settings` with `BridgeKind.NavigatesTo`.
- [x] Dynamic reference `/users/123` connects to file route `/users/[id]` or `/users/{}` with high confidence.
- [x] Catch-all reference `/docs/a/b` connects to `/docs/[...slug]`; `/docs` does not.
- [x] Optional catch-all reference `/docs` and `/docs/a/b` both connect to `/docs/[[...slug]]`.
- [x] Route-group file route `/(admin)/settings` matches reference `/settings`.
- [x] Ambiguous file route matches produce no navigation edge; provider evidence counts remain Task 3.
- [x] `BridgeGraph.NodeKindFor(BridgeKind.NavigatesTo, Target)` returns `BridgeNodeKind.NextRoute`.
- [x] Worker-scope verification passes; commit deferred because this checkout had approved pre-existing dirty bridge changes.

## Task 3: Next.js Provider And Provider Selection

**Files:**
- Create: `src/Miller.Core/Graph/NextJsBridgeProvider.cs`
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:22`
- Modify: `src/Miller.Indexing/BridgeProviderSelection.cs:8-64`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:178-191,706-759`
- Test: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:248-323`

**Interfaces:**
- Consumes: `IBridgeProvider.BuildCandidates(BridgeProviderContext)`, Task 1 adapter records, and Task 2 `NextRouteBridge`.
- Produces: provider id `nextjs` with `NextJsBridgeProvider.Instance`.
- Produces: default provider set `[dotnet-web, nextjs]` when no root `miller.json` provider list is present.

**What to build:** Add `NextJsBridgeProvider` and wire it into default and configured provider selection. The provider should become active when it sees at least one usable Next route reference or file route fact, emit navigation candidates when matches exist, and emit observation nodes for route references and file routes so diagnostics can explain unmatched routes.

**Approach:** Provider evidence counts should use stable keys such as `nextjs.routeReferences`, `nextjs.fileRoutes`, `nextjs.candidates`, and `nextjs.ambiguousMatches`. If `miller.json` specifies `"bridge": {"providers": ["dotnet-web"]}`, only `dotnet-web` runs. If it specifies `"nextjs"`, only `nextjs` runs. Unknown provider behavior remains a skipped provider entry and must not silently fall back to defaults.

**Acceptance criteria:**
- [x] `BridgeGraphBuilder.Build(...)` with default providers can build a pure Next navigation edge from structural facts only.
- [x] `BridgeProviderSelection.ProvidersForDatabase(...)` returns both `dotnet-web` and `nextjs` when no config exists.
- [x] Explicit config with `["dotnet-web"]` does not run `nextjs`.
- [x] Explicit config with `["nextjs"]` does not run `dotnet-web`.
- [x] Unknown provider tests still show skipped-provider behavior and do not run defaults.
- [x] Capability report includes `nextjs` active/skipped status and stable evidence counts.
- [x] Worker-scope verification passes; commit deferred because this checkout had approved pre-existing dirty bridge changes.

## Task 4: Trace Rendering, Route Targets, And Diagnostics

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs:722-845,969-1026,1093-1147,1557-1889`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs:993-1168,1384-1421`

**Interfaces:**
- Consumes: bridge graph edges with `BridgeKind.NavigatesTo`, `BridgeNodeKind.NextRoute`, provider capability report evidence counts, and observation route nodes.
- Produces: compact labels and JSON enum strings for `navigates_to` and `next_route`.
- Produces: route-string bridge behavior that works for `/settings` in a pure Next graph.

**What to build:** Extend `TraceTool` so route-string targets, bridge line rendering, JSON rendering, and no-link diagnostics understand Next route navigation. A pure Next route target should not report ASP.NET-specific backend-missing diagnostics.

**Approach:** Route-string resolution should consider route-bearing `Hits` edges and `NavigatesTo` edges. Diagnostics should classify Next cases separately: route reference exists with no file route match, file route exists with no route reference, both exist but no edge due to ambiguity, or no Next route facts observed. Compact labels should read naturally, for example `--navigates_to-->`, while JSON should expose stable snake-case enum values.

**Acceptance criteria:**
- [x] `TraceTool.Run(... target: "/settings", mode: "bridge" ...)` in a pure Next graph emits the matching navigation edge.
- [x] Compact output labels Next navigation distinctly from HTTP route hits.
- [x] JSON output includes `kind: "navigates_to"` and node kind `next_route`.
- [x] A route reference with no matching file route returns `nextjs_route_no_file_match`.
- [x] A file route with no matching reference returns `nextjs_route_no_reference_match`.
- [x] Ambiguous file-route matches return `nextjs_route_ambiguous_file_match`.
- [x] Existing frontend/backend diagnostics for `dotnet-web` still pass unchanged.
- [x] Worker-scope verification passes; commit deferred because this checkout had approved pre-existing dirty bridge changes.

## Task 5: Docs, Agent Guidance, And Contract Text

**Files:**
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md:41-46`
- Modify: `docs/contracts/trace-json-v1.md:106-133`
- Modify: `README.md:270-273,627-631`
- Modify: `skills/miller-bridge-trace/SKILL.md:3-67`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md:3-67`
- Modify: `skills/miller-orientation/SKILL.md:60`
- Modify: `.agents/skills/miller-orientation/SKILL.md:60`
- Modify: `docs/site/index.html:218`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs:22-136`

**Interfaces:**
- Consumes: final provider ids `dotnet-web` and `nextjs`, JSON values `navigates_to` and `next_route`, and the existing bridge-provider capability report shape.
- Produces: accurate prompt-facing and public docs for bridge scope.

**What to build:** Update shipped guidance so agents know `trace mode=bridge` is provider-scoped with `dotnet-web` and `nextjs` coverage, not a generic all-stack semantic bridge. Document that `nextjs` covers route references to file routes and does not claim API handlers or rewrites without extractor facts.

**Approach:** Keep wording short because server instructions have budget tests. Do not update historical release notes. Update both skill copies together and keep JSON contract wording additive.

**Acceptance criteria:**
- [x] Agent instructions no longer say bridge mode is only a `dotnet-web` chain.
- [x] README describes `nextjs` support without implying all-framework bridge coverage.
- [x] JSON contract documents `navigates_to` and `next_route`.
- [x] Bridge trace skill tells agents to use `patterns` when Next route facts are missing.
- [x] Agent instruction tests pass.
- [x] Current public/prompt-facing stragglers (`docs/site/index.html` and `miller-orientation`) are updated.
- [x] Worker-scope verification passes; commit deferred because this checkout had approved pre-existing dirty bridge changes.

## Task 6: End-To-End Fixture And Branch Verification

**Files:**
- Modify: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:38-144,323-360`
- Modify: `tests/Miller.Tests/Tools/TraceToolTests.cs:993-1168`
- Optional scale fixture only if julie-extractors 2.5.7 emits the needed facts from source: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs:557-704`

**Interfaces:**
- Consumes: released julie-extractors 2.5.7 structural facts when scale verification is available.
- Produces: one small pure Next fixture with `nextjs.route_reference.v1` and `nextjs.file_route.v1` facts proving repository load plus bridge trace behavior without ASP.NET facts.

**What to build:** Add an end-to-end pure Next repository-loader fixture using seeded structural facts. If the restored `.tools/julie-extract` emits the same facts from a tiny Next-shaped source tree during scale tests, add a scale test that proves the extractor-to-Miller path. If the extractor fixture cannot emit the required facts, keep the seeded repository-loader test as the hard gate and record the extractor gap in the final report.

**Approach:** The hard gate is Miller behavior over the stable artifact contract. Scale coverage is useful when the restored extractor can produce the facts locally, but the plan must not weaken Miller behavior if scale extraction is unavailable.

**Acceptance criteria:**
- [x] Repository loader builds a bridge graph with `nextjs` active and no ASP.NET facts present.
- [x] `trace mode=bridge` over that graph resolves a route string to a Next file route.
- [x] Scale test is added because local restored `julie-extract 2.5.7` emits `nextjs.route_reference.v1` and `nextjs.file_route.v1` from the fixture source.
- [x] Branch gate verification passes.
- [x] Final report states whether extractor-backed scale evidence was available.

## Verification Strategy

**Project source of truth:** `AGENTS.md` testing section, `tests/Miller.Tests/Miller.Tests.csproj` fast-suite filter, and `docs/contracts/trace-json-v1.md` for bridge JSON compatibility.

**Worker red/green scope:** Use focused `dotnet test tests/Miller.Tests/Miller.Tests.csproj -c Release --filter "<test name or class>"` for the task's tests while developing. This is the lowest-cost TDD loop and still uses the repo test project.

**Worker ceiling:** A worker may run `scripts/test.sh` after its task if the task touched shared bridge enums, provider selection, or `TraceTool`; workers do not own scale-suite acceptance unless assigned Task 6.

**Worker gate invariant:** The focused tests prove the exact behavior named in each task's acceptance criteria. `scripts/test.sh` proves the fast suite remains green with the repo's default `Category!=Scale` filter.

**Lead affected-change scope:** After a coherent batch, run `dotnet build Miller.slnx -c Release` and `scripts/test.sh`.

**Branch gate:** Run `scripts/test.sh all` because this plan touches bridge indexing/provider selection and may add extractor-backed scale coverage.

**Replay/metric evidence:** Hard gates are test pass/fail, build pass/fail, and the exact compact/JSON trace assertions. Provider evidence-count values are hard gates only where tests assert specific keys; counts from live dogfood workspaces are report-only.

**Escalation triggers:** Broaden to `scripts/test.sh all` immediately if changes touch `RepositoryIndexLoader`, `SqliteBridgeReader`, extractor scale fixtures, CLI JSON rendering, or bridge graph enum serialization. Escalate design review if implementation requires changing `IBridgeProvider.BuildCandidates`, reading SQLite from `TraceTool`, or parsing Next config files inside Miller.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp. For scale evidence, also record whether `.tools/julie-extract --version` reports 2.5.7 and which `nextjs.*` pattern ids were observed. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` is present in this repo; use current harness defaults with `inherit`.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit.

**Implementation tier:** bounded worker tasks from this plan.
- Harness mapping: inherit.

**Mechanical tier:** docs, mirrored skill wording, enum serialization updates, and fixture seeding with no gate-interpretation ownership.
- Harness mapping: inherit.

**Gate-interpretation reviewer:** reviewer tier for reading failing tests, replay output, and diffs to decide whether the test or implementation is wrong.
- Harness mapping: inherit.

**Escalation tier:** subtle correctness around route matching, JSON contract changes, provider-selection semantics, or repeated verification failures.
- Harness mapping: inherit.

**Worker eligibility:** Implementation-tier workers may take Tasks 1-5 when they can use Miller orientation tools, write failing tests first, and keep each task compilable before commit.

**Escalation triggers:** Use strategy/escalation tier if a worker needs to alter `IBridgeProvider`, add a new MCP tool, weaken bridge confidence rules, or expand scope to Next API handlers without extractor facts.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the execution ledger, and continue.

## Execution Notes

- Use @razorback:subagent-driven-development after approval when delegation is available.
- Use @razorback:executing-plans only for a single-agent or tightly sequential fallback.
- Use @razorback:test-driven-development for implementation tasks.
- Use @razorback:verification-before-completion before claiming the implementation is complete.
- Keep commits frequent: one clean commit per task after worker-scope verification passes.
