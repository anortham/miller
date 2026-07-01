# Web Bridge Route-Fact Consumption Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Fix the Miller-side consumption problems found in the 2026-07-01 cross-repo review of web structural facts so dynamic routes bridge correctly, confidence signals stay honest, and each bridge provider owns a coherent slice of the evidence.

**Architecture:** No new MCP tools and no new extractor ownership. All changes deepen the existing fact-to-candidate-to-edge pipeline: `StructuralRouteFactAdapter` (fact interpretation), `FileRouteMatcher`/`FileRouteBridge`/`FileRouteBridgeProvider` (file-route navigation), `DotnetWebBridgeProvider` (frontend-to-ASP.NET), and `RouteNormalizer` (canonical route keys). The companion extractor plan is `~/source/julie-extractors/docs/plans/2026-07-01-web-route-facts-hardening.md`; Tasks 1–6 here need no extractor changes, Task 7 gains real-world value only after the extractor's H3/H4/M3 fixes ship, and Task 8 is the pin-bump/coverage slice after that release.

**Tech Stack:** .NET 10, Miller.Core bridge graph, Miller.Server `TraceTool`, julie-extractors `structural_facts` (pinned binary, currently 2.5.9).

**Architecture Quality:** Affected modules are `Miller.Core.Graph`, `Miller.Core.Resolver`, and tests. Caller-facing surface stays `trace mode=bridge` (MCP + CLI). Architecture risk is medium: Task 4 re-draws the ownership boundary between `DotnetWebBridgeProvider` and the file-route providers, and Task 5 changes confidence-band behavior that dogfood consumers may have observed.

## Global Constraints

- Do not add a new MCP tool.
- Do not move parser recognition into Miller; consume `structural_facts` as emitted.
- Test behavior through caller-facing interfaces: `BridgeGraphBuilder` and `TraceTool.Run`, not private helpers.
- The fast suite must stay fast; any test spawning `julie-extract` is `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- Build stays 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- Existing green behavior (htmx→ASP.NET High edges with explicit `hx-get`/`hx-post` verbs, static file-route bridging, DTO/entity/table bridges) must not regress.

## Review Findings Being Addressed

From the 2026-07-01 review (checkpoint `checkpoint_92e28948`, fast suite 2555/2555 and scale 30/30 green — none of these are covered by current tests):

1. **HIGH, verified live:** dynamic Next.js/Nuxt file routes never bridge. `StructuralRouteFactAdapter.RoutePath` prefers `normalized_route_template` (colon style `/users/:id`, emitted only when a route is dynamic) over `route_path` (bracket style `/users/[id]`), but `FileRouteMatcher` only recognizes bracket forms. Live repro: `<Link href="/users/42">` vs `app/users/[id]/page.tsx` → `route_no_bridge_link`.
2. `RouteNormalizer.ParamPattern` folds `${p}`, `{p}`, `:p` but not bracket segments `[id]`, `[...slug]`, `[[...slug]]`.
3. Ambiguous file-route matches are dropped instead of resolved by specificity, and the match computation runs twice (`FileRouteBridge.Resolve` + `FileRouteBridgeProvider.CountAmbiguousMatches`).
4. `DotnetWebBridgeProvider` over-consumes: it treats Next/Nuxt **file routes** and all framework route references as client calls to ASP.NET endpoints, reports "active" in pure frontend repos, and creates duplicate observation nodes (raw vs normalized path) alongside `FileRouteBridgeProvider`.
5. Verb honesty: vue/react/next/nuxt navigation references get a synthesized `GET` carrier (`vue.get` etc.), producing verb-KNOWN `RouteVerbMatch` High edges — inconsistent with the "never assume GET" doctrine used for fetch/axios calls.
6. htmx `data-hx-*` attributes are unsupported on the Miller side (`HtmxVerb` switch) as well as the extractor side.
7. No reference→definition navigation bridging for Vue/React SPAs (only Next/Nuxt file-route providers exist), so pure SPA repos get no bridge at all.

---

## File Structure

- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` — fact-kind-aware route path selection; `data-hx-*` attribute normalization.
- Modify: `src/Miller.Core/Resolver/FileRouteMatcher.cs` — recognize colon-style dynamic/catch-all/optional segments.
- Modify: `src/Miller.Core/Resolver/RouteNormalizer.cs` — bracket dynamic segments in `ParamPattern`.
- Modify: `src/Miller.Core/Resolver/FileRouteBridge.cs` — single-pass matching with specificity precedence and ambiguity result.
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs` — consume the shared match result; drop the duplicate counting pass; (Task 7) generalize to reference→definition descriptors.
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` — narrower structural consumption, honest verb signals, deduped observation nodes.
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Resolver/RouteNormalizerTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`, `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (Scale).

## Task 1: Dynamic File-Route Matching (the verified bug)

**Files:**
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` (`RoutePath`, `TryReadFileRoute`)
- Modify: `src/Miller.Core/Resolver/FileRouteMatcher.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: `nextjs.file_route.v1` / `nuxt.file_route.v1` metadata carrying both `route_path` (bracket form) and `normalized_route_template` (colon form, dynamic routes only).
- Produces: `StructuralFileRoute.Route` populated with the bracket-form `route_path` for file-route facts; `FileRouteMatcher.Matches` accepting both bracket and colon dynamic forms.

**What to build:** Two complementary fixes so neither representation can silently fail again. First, for **file-route facts** select `route_path` before `normalized_route_template` (route **references** keep the current `target_path`/`attribute_value` preference). Second, teach `FileRouteMatcher` the colon forms as dynamic segments: `:id` (dynamic), `:slug*` (catch-all), `:slug*?` or `:slug?` (optional catch-all), so a colon template that reaches the matcher still works.

**Approach:** Keep the matcher's existing bracket handling untouched; add colon recognition inside `IsDynamic`/`IsCatchAll`/`IsOptionalCatchAll`. Distinguish fact kinds in the adapter rather than adding matcher-side heuristics about which key was chosen. Cover: `/users/42` vs `/users/[id]`, `/docs/a/b` vs `/docs/[...slug]`, `/docs` vs `/docs/[[...slug]]`, and the same three against colon templates.

**Acceptance criteria:**
- [x] A `nextjs.route_reference.v1` for `/users/42` bridges to a `nextjs.file_route.v1` for `app/users/[id]/page.tsx` in `BridgeGraphBuilderTests`.
- [x] Same for a Nuxt reference vs `pages/blog/[slug].vue`.
- [x] Colon templates (`/users/:id`, `/docs/:slug*`) match equivalently through `FileRouteMatcher`.
- [x] All existing static-route tests stay green.
- [x] Worker-scope verification passes, committed.

## Task 2: Bracket Segments in RouteNormalizer

**Files:**
- Modify: `src/Miller.Core/Resolver/RouteNormalizer.cs` (`ParamPattern` / `Canonicalize`)
- Test: `tests/Miller.Tests/Resolver/RouteNormalizerTests.cs`

**Interfaces:**
- Consumes: raw route strings from structural facts and client calls.
- Produces: canonical route keys where `[id]`, `[...slug]`, and `[[...slug]]` fold to the same `{}` placeholder as `{id}`, `:id`, and `${id}`.

**What to build:** Extend canonicalization so bracket-style dynamic segments produce the same normalized key as the other dynamic syntaxes. This keeps observation-node dedupe and route-key comparisons consistent when bracket paths flow into `DotnetWebBridgeProvider` and diagnostics.

**Approach:** Extend the compiled regex (or add a pre-pass) for `[[...name]]`, `[...name]`, `[name]` — ordered longest-first so optional catch-all is not partially consumed. Add normalization equivalence tests: `/users/[id]` == `/users/{id}` == `/users/:id` after canonicalization.

**Acceptance criteria:**
- [x] Bracket, brace, colon, and template-literal dynamic segments canonicalize identically.
- [x] Existing normalizer tests stay green.
- [x] Worker-scope verification passes, committed.

## Task 3: Ambiguity Precedence Instead of Dropping

**Files:**
- Modify: `src/Miller.Core/Resolver/FileRouteBridge.cs`
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: `StructuralRouteReference` + `StructuralFileRoute` lists.
- Produces: a single `FileRouteBridge.Resolve` result that carries both the resolved edges and the ambiguous-reference count, so the provider stops recomputing matches.

**What to build:** When one reference matches multiple file routes, pick the most specific match using file-router precedence (static segment > dynamic `[id]` > catch-all `[...slug]` > optional catch-all `[[...slug]]`, compared per segment) instead of dropping the edge. Only an exact specificity tie remains ambiguous: keep the current drop-the-edge behavior for ties and keep reporting the count in provider notes. Delete `CountAmbiguousMatches` and return the count from `Resolve`.

**Approach:** Compare candidate file routes pairwise by segment specificity vector, mirroring Next.js route resolution order. Keep the result type small (edges + ambiguous count); don't build a general router.

**Acceptance criteria:**
- [ ] `/users/42` matching both `/users/[id]` and `/users/[...slug]` yields one edge to `/users/[id]`.
- [ ] Two equally-specific matches still produce no edge and an ambiguous count of 1.
- [ ] Matching is computed once per build (no duplicate pass).
- [ ] Worker-scope verification passes, committed.

## Task 4: Scope DotnetWebBridgeProvider Structural Consumption

**Files:**
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` (`ReduceStructuralClientCalls`, observation-node creation, provider active/diagnostics logic)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: structural facts partitioned by role — route **references** (htmx/vue/react/next/nuxt) vs file-route **definitions** (next/nuxt).
- Produces: dotnet-web client-call candidates built only from route references; file-route definition facts are never treated as client calls (they belong to `FileRouteBridgeProvider` as navigation targets).

**What to build:** Three scope corrections. (a) Exclude `nextjs.file_route.v1` / `nuxt.file_route.v1` from the dotnet-web client-call reduction — a page definition is not a call to a backend. (b) Framework route **references** remain eligible client-call evidence (hybrid apps like the Tycho `/calendar` case depend on this), but the provider must not report itself "active" in a repo whose only evidence is frontend navigation references with zero .NET-side facts (no endpoints, no `Map*` calls, no DbSets). (c) Deduplicate observation nodes: normalize the route once (Task 2's canonical key) so raw and normalized variants of the same path do not create two `TsType` observation nodes across providers.

**Approach:** Partition by `pattern_id` role at the top of the structural reduction using `BridgeStructuralPatterns`. Gate "active" on backend-evidence presence, not candidate-list non-emptiness. Reuse the adapter/normalizer as the single source of the observation-node key. Preserve current diagnostics vocabulary (missing frontend facts / missing backend facts / no matched pairs).

**Acceptance criteria:**
- [ ] A pure Next.js repo (file routes + Link references, no .NET facts) shows `dotnet-web` inactive and `nextjs` file-route bridging active.
- [ ] A hybrid repo (Vue reference + ASP.NET minimal API route) still produces the frontend→endpoint edge.
- [ ] One structural route yields exactly one observation node regardless of which providers saw it.
- [ ] Existing htmx/ASP.NET dogfood-shaped tests stay green.
- [ ] Worker-scope verification passes, committed.

## Task 5: Honest Verbs for Navigation References

**Files:**
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` (`ToClientCall`, `StructuralCarrier`, signal assembly)
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` (`DefaultVerbForRouteReferenceFact`)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: route references with either extractor-attested verbs (htmx `hx-get`/`hx-post`/…) or no verb evidence (vue/react/next/nuxt navigation).
- Produces: verb-known signals only when the verb came from source evidence; navigation references match ASP.NET endpoints route-only and land in the Medium band with a verb-unknown flag.

**What to build:** Stop synthesizing `GET` carriers that upgrade navigation references to verb-KNOWN `RouteVerbMatch` High edges. htmx attributes keep their real verb and High band. Vue/react/next/nuxt navigation references keep matching endpoints by route, but the edge is Medium with the existing `verb_unknown` marker (same doctrine as verb-less fetch calls). Navigation-to-navigation matching (`FileRouteBridgeProvider`) is unaffected — verbs were never part of that comparison.

**Approach:** Replace the assumed-verb defaulting with an explicit "verb evidence: attested | unknown" distinction carried through `TsClientCall`. Where a route-only match would previously be silently GET-matched against a non-GET-only endpoint, prefer matching the endpoint set by route and flagging verb uncertainty rather than filtering to GET. Update `docs/contracts/` trace JSON notes if the band change is visible in documented examples.

**Acceptance criteria:**
- [ ] htmx `hx-post` → `MapPost` stays High with verb-known signal.
- [ ] Vue `<router-link to="/calendar">` → ASP.NET GET endpoint becomes Medium with `verb_unknown` (edge still exists).
- [ ] No structural navigation reference produces a verb-KNOWN signal without extractor verb metadata.
- [ ] Compact and JSON trace output agree on bands/flags.
- [ ] Worker-scope verification passes, committed.

## Task 6: `data-hx-*` Support (Miller side)

**Files:**
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` (attribute-name normalization) and/or `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` (`HtmxVerb`)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: `htmx.attribute.v1` facts whose `attribute_name` is `data-hx-get`, `DATA-HX-POST`, etc. (emission arrives with the extractor's M1 fix; Miller normalizes defensively regardless).
- Produces: verb resolution that strips a leading `data-` prefix and compares case-insensitively before the `hx-*` switch.

**What to build:** Normalize htmx attribute names once at the adapter boundary — lowercase, strip `data-` — so `HtmxVerb` sees canonical `hx-get`/`hx-post`/`hx-put`/`hx-delete`/`hx-patch`. Unknown attributes keep current behavior.

**Approach:** Small, table-driven; test with mixed-case and `data-` prefixed fixture facts fed directly to `BridgeGraphBuilder` (no extractor dependency).

**Acceptance criteria:**
- [ ] `data-hx-post` fact yields the same edge/verb as `hx-post`.
- [ ] Case variants normalize identically.
- [ ] Worker-scope verification passes, committed.

## Task 7: Vue/React Reference→Definition Navigation Provider

**Files:**
- Modify: `src/Miller.Core/Graph/FileRouteBridgeProvider.cs` (generalize descriptors) or Create: `src/Miller.Core/Graph/RouteDefinitionBridgeProvider.cs` if the file-route shape doesn't generalize cleanly
- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs` (if new pattern grouping constants are needed)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: `vue.route_reference.v1` + `vue.route_definition.v1`, `react.route_reference.v1` + `react.route_definition.v1`.
- Produces: `NavigatesTo` edges from navigation references to router-config route definitions, giving pure Vue/React SPA repos a working bridge, with the same ambiguity/diagnostic vocabulary as the Next/Nuxt providers.

**What to build:** The reference→definition analogue of the file-route providers: match reference `target_path` against definition route paths (colon templates like `/users/:id` — Task 1's matcher work covers these). Reuse `FileRouteBridge.Resolve` and Task 3's precedence result.

**Approach:** Prefer extending `FileRouteBridgeProvider`'s descriptor model (framework name, reference patterns, definition patterns) over a new provider class; only split if the definition-fact shape genuinely differs. Note in the provider docs that real-world recall improves after the extractor plan's H3 (route defs in plain `.ts` files), H4 (wrong-object range), and M3 (child relative paths) fixes ship — Miller's logic is testable now against fixture facts.

**Acceptance criteria:**
- [ ] Vue `<router-link to="/users/42">` bridges to a `vue.route_definition.v1` for `/users/:id` in a fixture-fact test.
- [ ] React `<Link to="/settings">` bridges to a `react.route_definition.v1` for `/settings`.
- [ ] Trace diagnostics name the framework when references exist but no definitions do (and vice versa).
- [ ] `trace mode=bridge` provider docs/help text list the new coverage.
- [ ] Worker-scope verification passes, committed.

## Task 8: Extractor Pin Bump, Live Dynamic Coverage, Docs Sync

**Files:**
- Modify: `scripts/julie-pins.json` (after the julie-extractors hardening release)
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (Scale)
- Modify: `docs/contracts/` trace/bridge notes, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` and mirrored skills **only if** provider names or user-visible behavior changed

**Interfaces:**
- Consumes: the julie-extractors release produced by `~/source/julie-extractors/docs/plans/2026-07-01-web-route-facts-hardening.md`.
- Produces: pinned extractor with H1–H4/M1–M7 fixes; live end-to-end proof that dynamic routes and `data-hx-*` bridge.

**What to build:** Bump the pin, re-run `scripts/restore-julie-extract.sh`, then extend `LiveBridgeTraceTests` with the scenarios today's suite lacks: a dynamic Next.js route (`/users/42` → `app/users/[id]/page.tsx`), a dynamic Nuxt route (`pages/blog/[slug].vue`), a `data-hx-*` attribute, and a Vue reference→definition pair. Sync contract docs and agent instructions to match shipped behavior.

**Approach:** This task is blocked on the extractor release; everything before it runs against the current 2.5.9 pin using hand-built fixture facts. Follow the existing `LiveBridgeTraceTests` fixture pattern; remember Scale tests skip when `.tools/julie-extract` is missing.

**Acceptance criteria:**
- [ ] Pin bumped; `VerifyPinnedJulieExtractVersion` build guard passes after restore.
- [ ] Live scale tests cover dynamic Next/Nuxt routes, `data-hx-*`, and Vue ref→def, all green.
- [ ] Contract docs and agent instructions match shipped behavior (`AgentInstructionsTests` green).
- [ ] `scripts/test.sh all` green.
- [ ] Worker-scope verification passes, committed.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` / `AGENTS.md`.

**Worker red/green scope:** Focused tests:
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~BridgeGraphBuilderTests -v minimal`
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~RouteNormalizerTests -v minimal`
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter FullyQualifiedName~TraceToolTests -v minimal`

**Worker ceiling:** Full fast suite via `scripts/test.sh`.

**Worker gate invariant:** Dynamic routes bridge, navigation verbs are honest, providers own disjoint evidence, and all pre-existing bridge behavior stays green.

**Lead affected-change scope:** Miller `impact` over the working diff plus the focused tests it lists.

**Branch gate:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh`; run `scripts/test.sh scale` for Tasks 1, 4, 5, 7 (bridge semantics) and mandatorily for Task 8.

**Escalation triggers:** SQLite reader contract changes, any julie-extractors schema change, failures in non-bridge trace modes, or confidence-band changes beyond what Task 5 specifies.

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, and timestamp per task. Reuse passing evidence for the same HEAD instead of rerunning expensive gates.

## Model Routing

**Project source of truth:** none documented (no repo-root `RAZORBACK.md`); Cursor model selection is IDE-level.

**All tiers:** `inherit` (harness limitation noted). Escalate to the session lead for Task 4/5 semantic decisions if code reality contradicts the approved shape.
