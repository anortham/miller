# HTTP Boundary Bridge Consumption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Consume julie-extractors v2.6.0's HTTP boundary facts so `trace mode=bridge` resolves `fetch("/api/x")`/axios call sites to the server handler that serves them — Next.js route handlers, Nuxt server routes, and ASP.NET attribute-routed controllers — and bump the extractor pin to 2.6.0.

**Architecture:** This is the Miller-side companion to `~/source/julie-extractors/docs/plans/2026-07-01-http-boundary-facts.md` (shipped as v2.6.0). No new MCP tools; no extractor ownership. Three consumption legs over the existing fact→candidate→edge pipeline: (1) `http.client_request.v1` becomes first-class client-call evidence in `DotnetWebBridgeProvider` (joining ASP.NET `effective_route_template`), (2) `aspnet.attribute_route.v1` becomes structural endpoint evidence beside the minimal-API arm, (3) two new descriptor-driven providers (`nextjs-api`, `nuxt-api`) bridge client requests to `nextjs.route_handler.v1`/`nuxt.server_route.v1` with verb-aware, segment-specific matching reusing `FileRouteBridge`/`FileRouteMatcher`. The htmx JSX/Vue coverage extension flows through the existing path unchanged (verified: nothing in the bridge path branches on fact language) and only needs live coverage.

**Tech Stack:** .NET 10, Miller.Core bridge graph, Miller.Server `TraceTool`, julie-extract 2.6.0 pinned binary.

**Architecture Quality:** Affected modules: `Miller.Core.Graph`, `Miller.Core.Resolver`, `Miller.Indexing` (provider selection + fact whitelist), `Miller.Server` TraceTool render, tests, docs/skills. Caller-facing surface stays `trace mode=bridge` (MCP + CLI). Risk is medium: Task 4 extends the verb-blind `FileRouteBridge` with verb semantics and a second edge kind (`Hits`), and Task 3 must dedupe three overlapping evidence sources (legacy url-literals, annotation endpoints, new structural facts) without band regressions. If code reality contradicts a decided shape below, workers report a plan mismatch instead of redesigning locally.

## Global Constraints

- Do not add a new MCP tool.
- Do not move parser recognition into Miller; consume `structural_facts` as emitted by 2.6.0.
- New pattern ids MUST be appended to `BridgeStructuralPatterns.BridgeFactPatternIds` — it is the `SqliteBridgeReader.ReadStructuralFacts` SQL load whitelist; an id absent there never reaches any provider (silent no-op).
- Verb honesty doctrine: verbs are evidence, never assumptions. `verb_source="attested"` and `verb_source="default"` (fetch/axios spec-default GET — the extractor stays silent when `method:` is non-literal, so "default" genuinely means no method option and the runtime verb is GET by spec) are BOTH verb-known. Navigation references stay verb-unknown as shipped in the 2026-07-01 fixes.
- Verbs are UPPERCASE in every 2.6.0 family; ASP.NET dynamic segments are `{id}`; Next/Nuxt `route_path` is bracket `[id]`; `normalized_route_template` is colon `:id`. `RouteNormalizer.Canonicalize` folds all of these to `{}`.
- Only `url_kind="path"` client requests are bridge candidates. `relative` (resolution depends on the current page URL) and `absolute` (external host) are rejected at the adapter, like verb-less htmx facts.
- ASP.NET join key: `effective_route_template` when present, else `route_template` — uniform across `aspnet.attribute_route.v1` and `aspnet.minimal_api.route.v1` (documented in julie's jsonl-v3.md:608).
- Test behavior through caller-facing interfaces: `BridgeGraphBuilder.Build` and `TraceTool.Run`, not private helpers.
- Any test spawning `julie-extract` is `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- Build stays 0 warnings / 0 errors; fast suite stays fast.
- Existing green behavior must not regress: htmx→ASP.NET High edges, static+dynamic file-route navigation, Vue/React ref→def, DTO/entity/table bridges, honest navigation verbs.
- Do not push, tag, publish, or release. Local commits only.

## Decided Consumption Contracts (from live 2.6.0 scan + emission-code review)

- `http.client_request.v1` (js/jsx/ts/tsx/vue script sections): `client` (`fetch|axios`), `target_path` (as written), `url_kind` (`path|relative|absolute`), `verb` (UPPERCASE, always present), `verb_source` (`attested|default`), `import_source` (axios only).
- `nextjs.route_handler.v1` (js/ts, App Router `route.{js,ts}`): `route_path` (bracket, leading `/`), optional `normalized_route_template` (colon, dynamic only), `verb`, `verb_source="attested"`, `router="app"`; span binds `containing_symbol_id` to the exported handler symbol. Auto-implemented OPTIONS is not emitted.
- `nuxt.server_route.v1` (js/ts, `server/api/**` → `/api` prefix, `server/routes/**` → bare): `route_path`, optional `verb`+`verb_source` (filename method suffix only; suffix-less = handler answers every method), whole-file span (containing symbol may be null).
- `aspnet.attribute_route.v1` (csharp): `attribute_kind` (`controller_route|http_method|route`), `verb` (http_method only, no `verb_source` key), `route_template` (as written), `controller_route_template`, `effective_route_template` (leading `/`, `[controller]`/`[action]` substituted + lowercased), `route_tokens`. `controller_route` facts are class-level prefix facts, NOT endpoints.
- `htmx.attribute.v1` now also emits from JSX (`javascript`/`jsx`/`tsx`, not plain `typescript`) and Vue `<template>`: same shape (`attribute_name` canonical `hx-*`, `data_prefix`, `attribute_value`, optional `verb`/`target_path`).

---

## File Structure

- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs` — 4 new pattern-id constants + whitelist entries.
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` — client-request reader (verb/verb_source/url_kind), handler-definition reader (nullable verb).
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` — client-request client calls; attribute-route endpoints; dedupe.
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs` — verb-aware client-request→handler descriptors (`nextjs-api`, `nuxt-api`).
- Modify: `src/Miller.Core/Resolver/FileRouteBridge.cs` — verb-aware resolve arm producing `Hits` edges.
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs` + `src/Miller.Indexing/BridgeProviderSelection.cs` — register new providers in BOTH `DefaultProviders` lists + `CreateProvider` switch.
- Modify: `src/Miller.Server/Tools/TraceTool.cs` — diagnostics table, evidence-key lists, doc header, stale `[Description]`.
- Modify: `scripts/julie-pins.json` — 2.6.0 + four sha256s.
- Modify: `docs/contracts/trace-json-v1.md`, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `.agents/skills/miller-bridge-trace/SKILL.md` (+ regen `skills/`).
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`, `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (Scale).

## Task 1: Pin Bump to 2.6.0 + Regression Proof

**Files:**
- Modify: `scripts/julie-pins.json`

**Interfaces:**
- Consumes: julie-extractors GitHub release v2.6.0 archives (no upstream sha256 sidecars; hashes computed from downloaded archives).
- Produces: pinned 2.6.0 binary in `.tools/`, green existing suites — every later task's live verification runs against 2.6.0.

**What to build:** Set `version` to `2.6.0` and replace the four asset sha256s with (verified 2026-07-01 from the live release):
- `aarch64-apple-darwin`: `0faad20df602840dd9a04f9c193e1d8e5ffc9a993ee7bfc281a918ab739942e2`
- `x86_64-apple-darwin`: `ecc6cf0847972eff362264c10f4ff2fb4b020537abe23f0627bd1a75ff34ca75`
- `x86_64-unknown-linux-gnu`: `80e3942c197364b7d492e0efa67ab437fc8d713ff11b4b2fd4aacb89eaf912df`
- `x86_64-pc-windows-msvc`: `4a0bf058f27a65793c8cc293ee4f80f3407553e839a8cbcf9e5cf7ecf679b3ee`

Run `scripts/restore-julie-extract.sh`, then `dotnet build Miller.slnx -c Release` (the `VerifyPinnedJulieExtractVersion` guard must pass), then the full existing suites.

**Approach:** 2.6.0 keeps schema_version=3, extract_contract_version=3, blake3 (verified by live scan) — `MillerExtractContract` needs no change. The release-workflow target matrix is unchanged. Version-aware leadership treats 2.6.0 as newer and force-rescans on claim by design; no action needed.

**Acceptance criteria:**
- [ ] `julie-pins.json` at 2.6.0 with the four hashes above; restore succeeds; build guard green.
- [ ] `scripts/test.sh` green and `scripts/test.sh scale` green against the 2.6.0 binary (regression proof BEFORE consumption work).
- [ ] Committed.

## Task 2: Load + Adapt the Four New Fact Families

**Files:**
- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (adapter behavior asserted through `BridgeGraphBuilder.Build` in Tasks 3–4; this task may land as part of Task 3's commit if it cannot be exercised independently — acceptable, report it)

**Interfaces:**
- Consumes: `StructuralFactRecord` rows for the new pattern ids (metadata shapes in "Decided Consumption Contracts").
- Produces: constants `BridgeStructuralPatterns.HttpClientRequest`, `.NextJsRouteHandler`, `.NuxtServerRoute`, `.AspNetAttributeRoute`, all four appended to `BridgeFactPatternIds`; an adapter read path for client requests exposing `RoutePath` (= `target_path`), `Verb` (non-null, from metadata `verb`), verb-source evidence (`attested|default`), and rejection of `url_kind != "path"`; a handler-definition read path exposing `RoutePath` (= `route_path`, bracket form first — same precedence as file routes) and a **nullable** verb (`verb` metadata; absent for suffix-less Nuxt). Existing navigation file routes keep their verb-blind semantics.

**What to build:** The whitelist entries plus adapter support. For the reference side, either extend `TryReadRouteReference` with pattern-aware verb extraction or add a sibling `TryReadClientRequest` — follow whichever keeps htmx/navigation behavior byte-identical. For the definition side, `StructuralFileRoute.Verb` is currently hard-coded `"GET"` and never compared; make handler verbs real (nullable) without changing navigation matching. Keep `IsTestFact` filtering for all new reads.

**Approach:** Client-request carrier synthesis happens in Task 3 (`<client>.<lowerverb>` so `RouteNormalizer.VerbFromCarrier` attests it). Adapter rejects: `url_kind` ≠ `path`, blank `target_path`. Do NOT reject `verb_source="default"` — it is verb-known GET per Global Constraints. Metadata values arrive as strings (non-string JSON values arrive as raw JSON text via `ParseMetadata` — `route_tokens`/`dynamic_segments` arrays are NOT needed by Miller; do not parse them).

**Acceptance criteria:**
- [ ] All four ids load through `SqliteBridgeReader` (fixture-fact test proves facts reach providers).
- [ ] Client-request adapter: path-kind + verb + verb-source read; relative/absolute rejected; test facts rejected.
- [ ] Handler adapter: bracket `route_path` preferred; Nuxt verb-less handler yields null verb; navigation file-route behavior unchanged (existing tests green).
- [ ] Worker-scope verification passes, committed.

## Task 3: dotnet-web — Client Requests + Attribute-Route Endpoints

**Files:**
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: Task 2 adapter reads; existing `TsClientCall`/`ControllerEndpoint`/`RouteBridge.Resolve` machinery.
- Produces: client-call evidence from `http.client_request.v1` (carrier `"<client>.<lowerverb>"`, e.g. `fetch.get`, `axios.post` → verb-known via `VerbFromCarrier`); endpoint evidence from `aspnet.attribute_route.v1` `http_method` facts (route = `effective_route_template` ?? `route_template`; `VerbKey = "http"+verb.ToLowerInvariant()`; handler = `containing_symbol_id`); evidence keys `dotnet-web.clientRequests` and `dotnet-web.attributeRoutes`; observation nodes for unmatched client requests (canonical-route `TsType`) and attribute endpoints (`Endpoint`).

**What to build:** Two reductions plus dedupe. (a) Client requests join the structural client-call list; a `fetch("/api/messages", {method:"POST"})` fixture fact must produce a verb-known `Hits` edge to a `MapPost`/`[HttpPost]` endpoint (High); a bare `fetch("/api/x")` is verb-known GET. (b) `attribute_kind="http_method"` facts become `ControllerEndpoint`s; `attribute_kind="route"` facts (method `[Route]` with no verb attribute) become verb-unknown endpoints ONLY if `RouteBridge.TryBuildHitsEdge` already expresses client-verb-known→endpoint-verb-unknown as an honest Medium route-only edge — verify; if it can't without surgery, count them in evidence and leave endpoint emission to a noted follow-up. `controller_route` facts are never endpoints. (c) Dedupe: an attribute-route structural endpoint and an annotation-derived endpoint for the same `(method SymbolId, VerbKey)` must yield ONE endpoint — structural wins (richer template: `[action]`, absolute `/`/`~/` overrides, token substitution). A client site emitting both a legacy url-literal and a structural client-request fact must yield ONE edge per endpoint, and where bands could differ (bare `fetch`: legacy carrier `fetch` is verb-unknown Medium, structural is verb-known GET High) the surviving edge is the HIGHER band — verify `BridgeGraph` edge-signature dedupe keeps the best-scored edge; fix the dedupe ordering if it keeps an arbitrary one.

**Approach:** Follow the existing `ReduceStructuralClientCalls`/`TryReduceStructuralEndpointFact` shapes. `IsRealClientCall`'s csharp exclusion must not drop js/ts/vue client requests (it won't — language comes from the fact). Keep the "active" gate backend-evidence-based: client requests alone must NOT activate dotnet-web (pure-frontend repos stay inactive — 2026-07-01 Task 4 behavior). Preserve diagnostics vocabulary.

**Acceptance criteria:**
- [ ] Attested fetch/axios facts → verb-known High `Hits` edges against both minimal-API and attribute-route endpoints (fixture-fact tests).
- [ ] Bare fetch (verb_source=default) → GET verb-known; matches GET endpoints High; does NOT match POST-only endpoints.
- [ ] Attribute-route endpoints: `[Route("api/[controller]")]` + `[HttpGet("{id}")]` fixture fact (effective `/api/users/{id}`) matched by client `/api/users/{}`-canonical calls; bare `[HttpPost]` inherits controller template.
- [ ] Dedupe: no duplicate endpoints (annotation+structural same method), no duplicate/downgraded edges (literal+structural same call site); higher band survives.
- [ ] Pure Next.js repo with client requests but zero .NET facts: dotnet-web stays inactive.
- [ ] Existing dotnet-web tests green. Worker-scope verification passes, committed.

## Task 4: nextjs-api / nuxt-api Client→Handler Providers

**Files:**
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs`
- Modify: `src/Miller.Core/Resolver/FileRouteBridge.cs`
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs`, `src/Miller.Indexing/BridgeProviderSelection.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2 adapter reads (client requests; handler definitions with nullable verb); `FileRouteMatcher.Matches` + specificity `BestMatch` (bracket + colon dynamic forms already supported).
- Produces: two provider instances — ProviderId `nextjs-api` (`http.client_request.v1` → `nextjs.route_handler.v1`) and `nuxt-api` (`http.client_request.v1` → `nuxt.server_route.v1`) — emitting `BridgeKind.Hits` edges; static accessor classes (`NextJsApiBridgeProvider`, `NuxtApiBridgeProvider`) with `const ProviderId` + `Instance`; registration in `BridgeGraphBuilder.DefaultProviders`, `BridgeProviderSelection.DefaultProviders`, and `CreateProvider` (`"nextjs-api"`, `"nuxt-api"` in `miller.json` `bridge.providers`); evidence keys `nextjs-api.clientRequests|routeHandlers|candidates|ambiguousMatches`, `nuxt-api.clientRequests|serverRoutes|candidates|ambiguousMatches`.

**What to build:** A verb-aware resolve arm beside the verb-blind navigation path (extend `FileRouteBridgeDescriptor` or add a parallel descriptor — follow code reality; do not perturb navigation semantics). Matching per reference: candidate handlers via `FileRouteMatcher.Matches` (so `fetch("/api/users/42")` matches `route_path=/api/users/[id]`), specificity `BestMatch`, ties ambiguous (count, no edge). Verb rules: handler verb known + equal → High (`RouteVerbMatch`, verb-known); handler verb known + different → no edge; handler verb ABSENT (suffix-less Nuxt = answers every method) → edge with `RouteOnlyMatch` (Medium, `verb_unknown` flag) — the client verb is known but the handler's accepted set is not source-attested, so the edge stays honest-Medium. Edge target: `EdgeRef.SymbolId` = handler fact's `containing_symbol_id` when present (Next.js exported handler symbols — the navigation payoff), else null with an `Endpoint`-kind synthesized node. Source: containing client symbol, else route node. Observation nodes for unmatched references and handlers (route diagnostics).

**Approach:** `BridgeKind.Hits` renders as `route` label and maps Source=TsType/Target=Endpoint in `NodeKindFor` — no new BridgeKind. Signals via `StructuralSignal` exactly like `RouteBridge` so `BridgeScorer` bands come out High/Medium with `IsVerbUnknown` set only on the route-only arm. Same reference facts also feed dotnet-web (Task 3) — precedent: framework route references already feed two providers; graph-level edge dedupe is by signature and the targets differ, so no conflict.

**Acceptance criteria:**
- [ ] Fixture facts: `fetch("/api/messages")` + Next `route.ts` GET handler → `Hits` High, target bound to handler symbol id.
- [ ] `fetch("/api/users/42")` + handler `route_path=/api/users/[id]` → High (segment match); two equally-specific handlers → ambiguous count, no edge.
- [ ] POST client request + GET-only handler → no edge. GET client + Nuxt suffix-less `server/api/notes.ts` → Medium `verb_unknown` edge.
- [ ] Both providers active/skip/evidence-count correctly (`no nextjs-api bridge evidence` when empty); registered in both DefaultProviders lists + `CreateProvider`; `miller.json` selection works.
- [ ] Existing navigation providers byte-identical behavior (all existing tests green).
- [ ] Worker-scope verification passes, committed.

## Task 5: Trace Surface — Diagnostics, Evidence Keys, Render

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs`
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: Task 3–4 evidence keys, provider ids, observation nodes.
- Produces: route diagnostics for the new providers (extend `FileRouteDiagnosticProviders` with `("nextjs-api", "Next.js", "route handler")` and `("nuxt-api", "Nuxt", "server route")` or the sibling-table equivalent); `HasRouteFactEvidence` + `BridgeFallbackNextActions` extended with `dotnet-web.clientRequests`, `dotnet-web.attributeRoutes`, `nextjs-api.*`, `nuxt-api.*` keys; class doc header provider list updated; the stale MCP `[Description]` ("currently dotnet-web", ~line 61) fixed to the real provider set.

**What to build:** Wire the new providers into the trace tool's hardcoded lists so `not_on_bridge`/route-string diagnostics and pattern-audit next_actions cover client-request/handler evidence (e.g. suggest `patterns operation=search pattern_id=http.client_request.v1`). Compact and JSON must agree on flags/bands for the new edges (no render changes expected — `Hits` already renders — but assert it).

**Acceptance criteria:**
- [ ] Route-string trace over an unmatched client request surfaces the new provider diagnostics (`route_no_backend_match`-family) with correct nouns.
- [ ] next_actions reference the new pattern ids when route facts are absent/present appropriately.
- [ ] Compact + JSON agree on kind/label/band/flags for client-request edges (fixture-fact TraceTool tests).
- [ ] Worker-scope verification passes, committed.

## Task 6: Live Scale Coverage (2.6.0 end-to-end)

**Files:**
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`

**Interfaces:**
- Consumes: pinned 2.6.0 binary (Task 1); all consumption legs (Tasks 2–5).
- Produces: live proof, via `ScaleTestSupport.RequireJulieServer()` + `TempWorkspace` + `ExtractAndLoad` + `TraceTool.Run`, following the existing fixture-writer pattern.

**What to build:** Four scenarios: (1) `WriteNextApiFixture` — `app/api/messages/route.ts` (`export async function GET` + `export const POST`) + a client `fetch("/api/messages", { method: "POST" })` → verb-known High `Hits` to the POST handler symbol; assert `structural_facts` contains `nextjs.route_handler.v1` + `http.client_request.v1`. (2) Dynamic: `app/api/users/[id]/route.ts` + `fetch("/api/users/42")` → High. (3) `WriteNuxtServerFixture` — `server/api/messages.get.ts` (`defineEventHandler`) + `axios.get("/api/messages")` (with axios import) → High; plus suffix-less `server/api/notes.ts` + a GET fetch → Medium `verb_unknown`. (4) `WriteHtmxTsxFixture` — TSX component with `hx-post="/todos"` + ASP.NET `[HttpPost("/todos")]`-shaped controller → High (proves the JSX emission flows through), and extend the existing attribute-route controller scenario with an axios call asserting `dotnet-web.attributeRoutes` evidence and NO duplicate edges (annotation + structural dedupe live).

**Approach:** Follow `WriteNextFixture`/`WriteNuxtFixture` exactly; `_output.WriteLine` evidence; `StructuralPatternIds` SQL check for the new ids. Scale trait or the convention guard fails the build.

**Acceptance criteria:**
- [ ] All four scenarios green via `scripts/test.sh scale`.
- [ ] Compact render lines asserted (e.g. `--route-->` + `(High)`), matching TraceTool output.
- [ ] Worker-scope verification passes, committed.

## Task 7: Docs, Instructions, Skill Sync + Branch Gate

**Files:**
- Modify: `docs/contracts/trace-json-v1.md`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` + `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md` → run `scripts/sync-plugin-skills.sh`

**Interfaces:**
- Consumes: shipped behavior from Tasks 1–6.
- Produces: docs that match reality; green `AgentInstructionsTests`.

**What to build:** (a) trace-json-v1.md: add `nextjs-api`/`nuxt-api` to the provider scope list; REVISE the now-partially-false disclaimers ("nextjs … does not claim API route handlers …", "nuxt … does not claim Nitro/server API routes …") to state what IS claimed (source-attested route handlers / Nitro server routes via 2.6.0 facts) and what still is not (server actions, middleware rewrites, redirects, runtime routing, conventional ASP.NET routing); document new evidence-count names. (b) MILLER_AGENT_INSTRUCTIONS.md: provider list becomes `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`; extend the fact-feed sentence (client fetch/axios facts feed `dotnet-web` and the `*-api` providers); update the exact-string assertions in `AgentInstructionsTests` in lockstep. (c) Skill: provider list, API-handler disclaimer, pattern-audit examples (`pattern_id=http.client_request.v1`), frontmatter description; regen `skills/` and confirm byte-identical copy. CLAUDE.md needs no edit (it does not enumerate providers) — verify, and if an edit IS needed, run `scripts/sync-agents.sh` + `cmp -s CLAUDE.md AGENTS.md`.

**Acceptance criteria:**
- [ ] `AgentInstructionsTests` green with updated strings; docs match shipped behavior.
- [ ] Skill regenerated; `skills/` matches `.agents/skills/`.
- [ ] Branch gate green: `dotnet build Miller.slnx -c Release` + `scripts/test.sh` + `scripts/test.sh scale`.
- [ ] Goldfish checkpoint written; all work committed locally. No push.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md`.

**Worker red/green scope:**
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal`
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests&Category!=Scale" -v minimal`
- Task 7: `--filter "FullyQualifiedName~AgentInstructionsTests&Category!=Scale"`

**Worker ceiling:** Full fast suite via `scripts/test.sh`.

**Worker gate invariant:** New facts load and bridge with honest verbs/bands; every pre-existing bridge behavior stays green; no duplicate endpoints/edges from overlapping evidence.

**Lead affected-change scope:** Miller `impact` over the working diff plus the focused tests it lists.

**Branch gate:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh`; `scripts/test.sh scale` mandatory for Tasks 1 and 6, recommended after 3–4.

**Replay/metric evidence:** Task 6 live scenarios are hard gates; evidence-count values in trace output are report-only.

**Escalation triggers:** SQLite reader contract changes, `MillerExtractContract` version mismatches against 2.6.0, confidence-band changes beyond what Tasks 3–4 specify, failures in non-bridge trace modes.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp per task. Reuse passing evidence for the same HEAD.

## Model Routing

**Project source of truth:** none documented (no repo-root `RAZORBACK.md`).

**All tiers:** `inherit` (harness limitation noted). Escalate to the session lead for Task 3 dedupe-ordering decisions and Task 4 verb-semantics decisions if code reality contradicts the approved shape.
