# Web Stack Structural Facts Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Use parser-backed ASP.NET, htmx, and Vue structural facts to improve `trace mode=bridge` so daily htmx and Vue client routes link to ASP.NET server endpoints.

**Architecture:** Keep extraction ownership in `julie-extractors` and deterministic trace ownership in Miller. Miller reads selected `structural_facts` into the existing bridge graph build, reduces route-ready web facts into the same client-call and endpoint candidate model that `RouteBridge` already scores, and leaves `patterns` as the generic fact discovery surface.

**Tech Stack:** .NET 10, C#, SQLite via `Microsoft.Data.Sqlite`, Miller Core bridge graph/resolver code, `julie-extractors` Rust structural fact emitters for any extractor-side route-fact contract gaps.

**Architecture Quality:** Approved shape is a small structural-fact input seam on `BridgeProviderContext`, a focused `WebStackStructuralFactReducer` in Miller Core, and unchanged MCP tool surface. Main risk is contract drift between `julie-extractors` metadata and Miller bridge consumption, especially for Vue route expressions.

## Global Constraints

- Do not add a new MCP tool; enhance existing `trace mode=bridge` and existing bridge provider behavior.
- Miller must not own parser recognition or raw AST queries; new parser-backed Vue route facts belong in `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge`.
- Keep `patterns` generic over `pattern_id`; do not special-case htmx or Vue in `PatternsTool`.
- htmx source facts use `htmx.attribute.v1` metadata keys `framework`, `attribute_name`, `attribute_value`, `target_path`, `verb`, `query_family`, and `pattern_version`.
- ASP.NET source facts use `aspnet.minimal_api.route.v1` metadata keys `framework`, `api_style`, `route_template`, `route_source`, `verb`, `handler_kind`, optional `handler_name`, `query_family`, and `pattern_version`.
- Current Vue source facts are `vue.sfc_section.v1` and `vue.template_directive.v1`; route matching requires route-ready Vue metadata rather than ad hoc source-text parsing in Miller.
- Match only route/navigation facts with normalized path metadata; static asset references and CSS/script resources must not become bridge route edges.
- Verb-known client facts must require a matching endpoint verb; verb-unknown facts may match by route only and retain the existing medium-confidence/verb-unknown behavior.
- Use TDD, keep each task compiling, and commit after each worker slice.
- Follow `@razorback:test-driven-development`, `@razorback:architecture-quality`, and `@miller:miller-editing` during execution.

---

## File Structure

### julie-extractors contract prerequisite

- Modify `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge/crates/julie-extractors/src/base/web_structural_facts.rs:19-20,39,697-761`
  - Add `vue.route_reference.v1` route-ready facts for Vue template navigation.
  - Reuse the existing Vue section/directive scanner where it has enough data, and add narrow plain-attribute handling for router/link route targets.
- Modify `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge/crates/julie-extractors/src/tests/vue/structural_facts.rs:48-140`
  - Add route-reference fixture coverage for `RouterLink`, `router-link`, bound `:to`, and click/router navigation where the parser can prove a literal route.

### Miller bridge ingestion and reducer

- Create `src/Miller.Core/Contracts/StructuralFactRecord.cs`
  - Core value record for selected structural facts: fact id, pattern id, language, path, capture/node kind, containing symbol id, source span, confidence, and raw metadata JSON.
- Create `src/Miller.Core/Graph/WebStackStructuralFactReducer.cs`
  - Convert `aspnet.minimal_api.route.v1` facts into `ControllerEndpoint` inputs.
  - Convert `htmx.attribute.v1` and `vue.route_reference.v1` facts into `TsClientCall` inputs with route text, carrier, language, symbol/test status, and evidence site.
- Modify `src/Miller.Core/Graph/IBridgeProvider.cs:17-25`
  - Add `IReadOnlyList<StructuralFactRecord> StructuralFacts` to `BridgeProviderContext`.
- Modify `src/Miller.Core/Graph/BridgeGraphBuilder.cs:37-59,81-89`
  - Add structural facts to both `Build` overloads while preserving existing call sites with empty defaults where needed.
- Modify `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:22-56,114-174,311-334`
  - Merge structural-fact endpoints with annotation-based controller endpoints.
  - Merge structural-fact client calls with literal-based TypeScript/JavaScript calls before calling `RouteBridge.Resolve`.
  - Add evidence counts for `dotnet-web.structuralFacts`, `dotnet-web.aspnetMinimalRoutes`, `dotnet-web.htmxCalls`, and `dotnet-web.vueCalls`.
- Modify `src/Miller.Indexing/SqliteBridgeReader.cs:45-57,274-291`
  - Read selected bridge-relevant `structural_facts` rows into `BridgeData`.
  - Limit the query to `aspnet.minimal_api.route.v1`, `htmx.attribute.v1`, and `vue.route_reference.v1`.
- Modify `src/Miller.Indexing/RepositoryIndexLoader.cs:77-89`
  - Pass `bridgeData.StructuralFacts` into `BridgeGraphBuilder.Build`.

### Trace UX and tests

- Modify `src/Miller.Server/Tools/TraceTool.cs:603-673,1156-1173`
  - Preserve current bridge output format.
  - Add `patterns` next actions for route-fact diagnostics when bridge evidence exists but a target is not on a bridge or links are missing within depth.
- Modify `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
  - Document that `trace mode=bridge` now consumes ASP.NET, htmx, and Vue route structural facts, and that `patterns` is the fact-audit fallback.
- Test `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:158-434`
  - Add htmx and Vue structural-fact bridge graph tests.
- Test `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs:44-90`
  - Add structural-facts schema and read-contract tests.
- Test `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:38-110,138-284`
  - Add integration-style fixture rows showing loader builds bridge edges from structural facts.
- Test `tests/Miller.Tests/Resolver/RouteBridgeTests.cs:63-144`
  - Add or adjust coverage only if the route reducer needs a new carrier/verb behavior; otherwise keep RouteBridge unchanged.
- Test `tests/Miller.Tests/Tools/TraceToolTests.cs:857-1181`
  - Add JSON/compact assertions for provider evidence counts and new fallback next actions.
- Test `tests/Miller.Tests/Server/AgentInstructionsTests.cs:136-150`
  - Keep server instructions synchronized with tool behavior.

## Architecture Quality

**Approved module shape:** `julie-extractors` emits route-ready facts; Miller Indexing reads only selected bridge-relevant fact rows; Miller Core reduces those facts into existing route bridge contracts; Miller Server renders existing graph output and recovery actions.

**Interface contract:** `StructuralFactRecord` is a Core contract, not a pattern-tool row. It carries raw metadata JSON so the Core reducer can parse stable top-level keys without taking a dependency on `PatternFactsReader`.

**Risk:** Medium. The bridge graph sits on a cross-repo contract and can create false links if route metadata is too broad. The plan controls that risk by requiring explicit `target_path`/`route_template` metadata, preserving verb matching rules, and proving no static asset links.

**Rejected shortcuts:** Do not parse Vue or htmx source text in Miller. Do not route through `PatternsTool`. Do not add a `trace patterns` mode or new MCP tool. Do not treat generic `vue.template_directive.v1` expressions as routes unless the extractor emits a route-ready fact or route-ready metadata.

## Verification Strategy

**Project source of truth:** Miller `AGENTS.md` for .NET gates; `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge` Cargo/xtask docs and existing release evidence for extractor gates.

**Worker red/green scope:** Run the narrowest targeted test command for the touched slice.
- julie-extractors route fact slice: `cd /Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge && cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture`
- Miller Core/Indexing slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests|FullyQualifiedName~SqliteBridgeReaderTests|FullyQualifiedName~RepositoryIndexLoaderBridgeTests"`
- Miller Trace UX slice: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests|FullyQualifiedName~AgentInstructionsTests"`

**Worker ceiling:** Workers may run `scripts/test.sh` in Miller and `cargo xtask test default` in julie-extractors for their own diagnosis. Workers do not own release, publish, scale, certification, or real-world gates.

**Worker gate invariant:** Targeted tests prove route-ready structural facts are emitted, read into the bridge input, reduced into endpoint/client candidates, scored by existing route bridge logic, and rendered by existing trace output.

**Lead affected-change scope:** After Miller implementation batches, run `scripts/test.sh` from the Miller worktree. After extractor prerequisite changes, run `cd /Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge && cargo xtask test default`.

**Branch gate:** Before handoff, Miller must pass `scripts/test.sh`. If extractor code changed, julie-extractors must pass `cargo fmt --check`, `cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture`, and `cargo xtask test default`.

**Replay/metric evidence:** Hard gates are the two dogfood bridge assertions: htmx `/todos` to ASP.NET `MapGet("/todos")`, and Vue route reference `/todos` to the same endpoint. Evidence counts are report-only except that nonzero `dotnet-web.htmxCalls`, `dotnet-web.vueCalls`, and `dotnet-web.aspnetMinimalRoutes` are required in the relevant tests.

**Escalation triggers:** Broaden to `scripts/test.sh all` only if indexing/extract paths start spawning the real `julie-extract` binary or scale fixtures change. Broaden julie-extractors beyond default only if parser grammar, artifact schema, or contract/export tests change.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless the failure is caused by the test written for the same task before implementation.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp in the task completion note. If the same HEAD already has a passing ledger entry for the required scope, reuse that evidence instead of rerunning the same expensive gate.

## Model Routing

**Project source of truth:** No `RAZORBACK.md` exists in `/Users/murphy/source/miller`; use current harness defaults and `inherit` for all tiers unless the user overrides routing at approval time.

**Strategy tier:** planning, architecture, decomposition, lead review, finding triage.
- Harness mapping: inherit

**Implementation tier:** bounded worker tasks from this clear plan.
- Harness mapping: inherit

**Mechanical tier:** docs, fixtures, formatting, and rote contract updates with no gate interpretation ownership.
- Harness mapping: inherit

**Gate-interpretation reviewer:** reviewer tier for reading the plan, failing test or replay, and diff to decide whether the test or implementation is wrong.
- Harness mapping: inherit

**Escalation tier:** security, subtle correctness, high blast radius, weak tests, repeated failures, and gate interpretation.
- Harness mapping: inherit

**Worker eligibility:** Workers may take one vertical task when the task names exact files, input/output contracts, and worker red/green command.

**Escalation triggers:** Missing Vue route fact capability, metadata contract mismatch, repeated route false positives, bridge graph performance regressions, or any need to add a new MCP tool.

**Mechanical exclusion:** Mechanical workers cannot own failing tests, replay evidence, metrics, or acceptance gates. Split docs-only updates from evidence interpretation.

**Unsupported harness behavior:** If the harness cannot choose models per agent, use `inherit`, note it in the task ledger, and continue.

## Tasks

### Task 1: Add Vue Route-Reference Facts In julie-extractors

**Files:**
- Modify: `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge/crates/julie-extractors/src/base/web_structural_facts.rs:19-20,39,697-761`
- Modify: `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge/crates/julie-extractors/src/tests/vue/structural_facts.rs:48-140`

**Interfaces:**
- Consumes: Existing Vue scanning helpers `scan_vue_sections`, `scan_markup_attributes`, and `parse_vue_directive`.
- Produces: `vue.route_reference.v1` structural facts with metadata keys `query_family=frontend_navigation`, `framework=vue`, `source_kind`, `target_path`, `verb=GET`, `pattern_version=1`, plus `attribute_name` or `expression` when available.

**What to build:** Emit route-ready Vue facts for proven literal navigation targets. Cover plain `to="/todos"` on `RouterLink`/`router-link`, bound `:to="'/todos'"`, and literal router navigation expressions when the existing parser can prove the string value.

**Approach:** Add a new pattern id to the Vue web pattern list and a focused fact builder near `vue_template_directive_fact`. Do not weaken existing `vue.template_directive.v1`; route matching gets a distinct route-ready fact so Miller does not parse arbitrary Vue expressions.

**Acceptance criteria:**
- [x] `vue.route_reference.v1` appears in `web_structural_fact_pattern_ids_for_language("vue")`.
- [x] `RouterLink` and `router-link` literal `to` attributes emit `target_path="/todos"` and `verb="GET"`.
- [x] Bound `:to="'/todos'"` emits the same `target_path` only when the expression is a literal string.
- [x] Non-route directives such as `v-if`, `v-model`, and `:class` do not emit route-reference facts.
- [x] Worker-scope verification passes, committed.

### Task 2: Add Miller Structural Fact Bridge Input

**Files:**
- Create: `src/Miller.Core/Contracts/StructuralFactRecord.cs`
- Modify: `src/Miller.Core/Graph/IBridgeProvider.cs:17-25`
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:37-59,81-89`
- Modify: `src/Miller.Indexing/SqliteBridgeReader.cs:45-57,274-291`
- Modify: `src/Miller.Indexing/RepositoryIndexLoader.cs:77-89`
- Test: `tests/Miller.Tests/Indexing/SqliteBridgeReaderTests.cs:44-90`
- Test: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:38-110,138-284`

**Interfaces:**
- Consumes: SQLite `structural_facts` rows with columns already read by `PatternFactsReader`.
- Produces: `BridgeData.StructuralFacts` and `BridgeProviderContext.StructuralFacts`.

**What to build:** Add a raw structural-fact bridge data path that loads only route-relevant fact ids. Keep this separate from `PatternFactsReader` so the bridge graph can build without depending on pattern-tool search/rendering code.

**Approach:** Add a `ReadStructuralFacts` method in `SqliteBridgeReader` using by-name reads and deterministic ordering by `path`, `start_byte`, and `structural_fact_id`. Query only `aspnet.minimal_api.route.v1`, `htmx.attribute.v1`, and `vue.route_reference.v1` so large generic fact sets such as JSON properties are not loaded into every bridge graph.

**Acceptance criteria:**
- [ ] `SqliteBridgeReader.Read` returns selected structural facts with metadata JSON and span intact.
- [ ] Missing or unrelated pattern ids are ignored by the bridge reader.
- [ ] `RepositoryIndexLoader.Load` passes structural facts into `BridgeGraphBuilder.Build`.
- [ ] Existing bridge tests compile without requiring callers to pass structural facts manually.
- [ ] Worker-scope verification passes, committed.

### Task 3: Bridge ASP.NET Minimal API And htmx Facts

**Files:**
- Create: `src/Miller.Core/Graph/WebStackStructuralFactReducer.cs`
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:22-56,114-174,311-334`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:158-434`
- Test: `tests/Miller.Tests/Resolver/RouteBridgeTests.cs:63-144`

**Interfaces:**
- Consumes: `StructuralFactRecord` rows for `aspnet.minimal_api.route.v1` and `htmx.attribute.v1`.
- Produces: `ControllerEndpoint` candidates for minimal API routes and `TsClientCall` candidates for htmx route attributes.

**What to build:** Convert ASP.NET minimal API route facts and htmx route facts into the existing route bridge input model. A `hx-get="/todos"` fact should match `MapGet("/todos", ...)`; `hx-post="/todos"` should match `MapPost("/todos", ...)`; verb mismatches should produce no Hits edge.

**Approach:** Map ASP.NET `verb` metadata to annotation-style verb keys such as `httpget`, and use `route_template` as the endpoint method route. Map htmx `target_path` to a synthetic `LiteralRecord` with `kind=url`, language from the fact, and carrier `htmx.get`, `htmx.post`, `htmx.put`, `htmx.patch`, or `htmx.delete` according to the metadata verb.

**Acceptance criteria:**
- [ ] htmx `hx-get` to ASP.NET `MapGet` produces a high-confidence `Hits` edge with both client and endpoint evidence.
- [ ] htmx `hx-post` does not match `MapGet` for the same route.
- [ ] htmx `hx-target` and other non-route htmx attributes do not produce client route calls.
- [ ] Provider evidence counts include nonzero `dotnet-web.structuralFacts`, `dotnet-web.aspnetMinimalRoutes`, and `dotnet-web.htmxCalls` in the htmx fixture.
- [ ] Worker-scope verification passes, committed.

### Task 4: Bridge Vue Route Facts

**Files:**
- Modify: `src/Miller.Core/Graph/WebStackStructuralFactReducer.cs`
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs:22-56`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs:158-434`
- Test: `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs:38-110,138-284`

**Interfaces:**
- Consumes: `vue.route_reference.v1` rows with `target_path` and `verb=GET`.
- Produces: `TsClientCall` candidates for Vue route references.

**What to build:** Convert Vue route-reference facts into client calls that can match ASP.NET minimal API endpoints through existing route normalization and scoring. This covers Vue SFC template navigation without treating every directive expression as a route.

**Approach:** Map Vue navigation facts to synthetic `LiteralRecord` values with `kind=url`, `language=vue`, literal text from `target_path`, carrier `vue.get`, and containing symbol/test status from the fact's `containing_symbol_id`. Preserve file and line evidence from the structural fact span.

**Acceptance criteria:**
- [ ] Vue `RouterLink` or `router-link` route fact to `/todos` matches ASP.NET `MapGet("/todos", ...)`.
- [ ] Vue bound `:to="'/todos'"` route fact matches ASP.NET `MapGet("/todos", ...)`.
- [ ] Vue route facts without `target_path` or with nonliteral expressions produce no client calls.
- [ ] Provider evidence counts include nonzero `dotnet-web.vueCalls` in the Vue fixture.
- [ ] Worker-scope verification passes, committed.

### Task 5: Improve Trace Recovery And Agent Guidance

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs:603-673,1156-1173`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs:857-1181`
- Test: `tests/Miller.Tests/Server/AgentInstructionsTests.cs:136-150`

**Interfaces:**
- Consumes: `BridgeCapabilityReport.EvidenceCounts` from the bridge graph.
- Produces: compact and JSON next actions that point agents to `patterns` when route facts are present or expected.

**What to build:** When `trace mode=bridge` cannot start or cannot walk links, include targeted `patterns` recovery actions for route fact inspection. Keep existing `trace refs`, `trace auto`, and `search source` suggestions.

**Approach:** Extend `BridgeFallbackNextActions` or wrap it with capability-aware additions. Suggested actions should use existing tool surfaces, such as `patterns operation=search query=route`, `patterns operation=search pattern_id=htmx.attribute.v1`, and `patterns operation=search pattern_id=vue.route_reference.v1`.

**Acceptance criteria:**
- [ ] Compact bridge fallback includes patterns next actions when bridge route facts are relevant.
- [ ] JSON bridge fallback includes equivalent structured next actions.
- [ ] Server instructions document htmx/Vue route fact consumption and pattern-audit fallback.
- [ ] Existing bridge trace output for successful paths remains compatible.
- [ ] Worker-scope verification passes, committed.

### Task 6: Dogfood End-to-End And Record Evidence

**Files:**
- Modify: `docs/plans/2026-06-30-web-stack-structural-facts-bridge.md`
- Create: `docs/findings/2026-06-30-web-stack-structural-facts-bridge-dogfood.md`

**Interfaces:**
- Consumes: Passing extractor and Miller test evidence from Tasks 1-5.
- Produces: A short evidence note with exact commands, commit SHAs, and observed bridge behavior.

**What to build:** Run the affected-change and branch gates, then record the hard-gate dogfood results. The evidence file should capture htmx `/todos` to ASP.NET `MapGet("/todos")`, Vue route reference `/todos` to ASP.NET `MapGet("/todos")`, and the provider evidence counts.

**Approach:** Keep this as evidence, not product docs. Do not edit release-facing README metadata unless a release task explicitly follows.

**Acceptance criteria:**
- [ ] Miller `scripts/test.sh` passes.
- [ ] If Task 1 changed julie-extractors, julie-extractors `cargo xtask test default` passes.
- [ ] Evidence file records command, scope label, commit SHA, result, timestamp, and hard-gate assertions.
- [ ] Plan checkboxes are updated to reflect completed tasks.
- [ ] Worker-scope verification passes, committed.
