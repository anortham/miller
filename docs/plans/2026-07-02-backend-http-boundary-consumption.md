# Backend HTTP Boundary Consumption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use razorback:subagent-driven-development when subagent delegation is available. Fall back to razorback:executing-plans for single-task, tightly-sequential, or no-delegation runs.

**Goal:** Consume julie-extractors v2.7.0's backend HTTP boundary facts so `trace mode=bridge` resolves client requests (fetch/axios/requests/httpx/HttpClient/net/http/HttpRequest/Net::HTTP) to server handlers across the 16 new route/mount families — Express, Fastify, FastAPI, Flask, Django, Spring, Go net/http, gin, echo, and Rails — via the universal `normalized_route_template` join key.

**Architecture:** Miller-side companion to the shipped julie-extractors v2.7.0 backend-http-boundary lane (release notes: `~/source/julie-extractors/docs/release-notes/v2.7.0.md`, "Miller Handoff" section). Extends `docs/plans/2026-07-01-http-boundary-bridge-consumption.md` (shipped): the `ApiRouteBridgeProvider` join machinery (client_request → route handler via `FileRouteBridge.ResolveClientRequests`) generalizes to ONE new provider, `backend-http`, that is generic over the 10 route-template families. Three Miller-owned enrichment passes feed it: cross-file mount-prefix composition (the four mount/include facts), Rails resource-route expansion (Rails semantics are Miller's job per the handoff), and Rails `controller_action` symbol binding. Independently, `RouteBridge.IsRealClientCall`'s blanket csharp exclusion is narrowed so C# `HttpClient` structural facts become first-class client evidence into dotnet-web (service-to-service bridging). No new MCP tools; no extractor ownership.

**Tech Stack:** .NET 10, Miller.Core bridge graph, Miller.Server `TraceTool`, julie-extract 2.7.0 pinned binary (pin bump already landed: commit `2c5fcb5`, fast 2634 / scale 38 green).

**Architecture Quality:** Affected modules: `Miller.Core.Graph` (patterns, adapter, one new provider), `Miller.Core.Resolver` (`RouteBridge.IsRealClientCall` narrowing only — `FileRouteBridge`/`FileRouteMatcher` are consumed as-is), `Miller.Indexing` (provider registration), `Miller.Server` (TraceTool lists), tests, docs/skills. Caller-facing surface stays `trace mode=bridge` (MCP + CLI). Risk is medium and concentrated in Task 3 (cross-file mount anchoring must stay honest — a wrong anchor fabricates edges) and Task 5 (relaxing the csharp client exclusion must not resurrect the test-HttpClient noise it was built to block). If code reality contradicts a decided shape below, workers report a plan mismatch instead of redesigning locally.

## Global Constraints

- Do not add a new MCP tool.
- Do not move parser recognition into Miller; consume `structural_facts` as emitted by 2.7.0.
- Every new pattern id MUST be appended to `BridgeStructuralPatterns.BridgeFactPatternIds` (`src/Miller.Core/Graph/BridgeStructuralPatterns.cs:24`) — it is the `SqliteBridgeReader.ReadStructuralFacts` SQL load whitelist; an id absent there never reaches any provider (silent no-op).
- Verb honesty doctrine: verbs are evidence, never assumptions. `verb_source="attested"` and `"default"` are both verb-known. A handler fact with NO verb (Express `app.all`, verbless Fastify registration, gin/echo `Any`, method-less `@RequestMapping`, verbless Go patterns, verbless Rails DSL) yields a nullable verb → route-only Medium `verb_unknown` edges, exactly like suffix-less Nuxt today.
- Join key doctrine (from the v2.7.0 handoff): client `target_path` joins against `normalized_route_template` (`:param` flavor). Miller side: `RouteNormalizer.Canonicalize` already folds `:p`/`{p}`/`${p}` → `{}` and trims slashes, so upstream trailing-slash preservation does not split joins. Route-fact join key precedence is `effective_route_template` ?? `normalized_route_template` (uniform for all 10 route families; same precedence doctrine as ASP.NET).
- Only `url_kind="path"` client requests are bridge candidates (existing `TryReadClientRequest` behavior — do not change).
- Prefix facts are never endpoints: `spring.request_mapping.v1` with `attribute_kind="class_route"` mirrors ASP.NET `controller_route` and MUST be excluded from endpoint emission (counted in evidence only).
- Django `url_pattern` facts with `route_syntax="regex"` have no `normalized_route_template` and stay out of the bridge (honest exclusion; the adapter's blank-route rejection already handles this — do not synthesize a route from the regex).
- Metadata string arrays (`only`, `except`) arrive as raw JSON text through `ParseMetadata`; parse them with `System.Text.Json` where Task 4 needs them. `dynamic_segments`/`route_tokens` are NOT needed — do not parse them.
- Test behavior through caller-facing interfaces: `BridgeGraphBuilder.Build` and `TraceTool.Run`, not private helpers.
- Any test spawning `julie-extract` is `[Trait("Category","Scale")]` via `ScaleTestSupport.RequireJulieServer()`.
- Build stays 0 warnings / 0 errors; fast suite stays fast.
- Existing green behavior must not regress: htmx→ASP.NET, fetch/axios→ASP.NET/Next/Nuxt, navigation file routes, Vue/React ref→def, DTO/entity/table bridges, F4 client-call dedupe semantics.
- Language parity (CLAUDE.md load-bearing rule): this feature is not done until it is verified per-language on a real extract — Task 7 covers all 10 languages the new families span (js, jsx, ts, tsx, python, csharp, go, java, ruby, vue on the client side).
- Do not push, tag, publish, or release. Local commits only. Goldfish checkpoint BEFORE each commit; `.memories/` ships in the commit.

## Decided Consumption Contracts (from julie's `docs/contracts/structural-fact-patterns.json` @ 2.7.0)

Route-template families (10) — all emit `route_template` + `normalized_route_template` (django: optional, path-syntax only) + optional `verb`/`verb_source`, optional `effective_route_template` where the framework has same-file prefixes:

- `express.route.v1` (js/jsx/ts/tsx): optional `route_group_prefix`/`effective_route_template` (same-file `app.use`). Verb omitted for `app.all`.
- `fastify.route.v1` (js/jsx/ts/tsx): verb omitted for all-method registrations.
- `fastapi.route.v1` (python): `verb` always; optional `router_prefix`/`effective_route_template`.
- `flask.route.v1` (python): `verb` always; optional `blueprint`, `url_prefix`, `effective_route_template`.
- `django.url_pattern.v1` (python): `route_syntax` (`path`|`regex`); `normalized_route_template` optional (path only); `view_target` always; NO verb (Django views are verb-agnostic at URLconf level) → always route-only Medium.
- `spring.request_mapping.v1` (java): `attribute_kind` (`class_route`|`http_method`|`request_mapping`); optional `class_route_template`/`effective_route_template`; verb optional (absent for method-less `@RequestMapping`).
- `go.net_http.route.v1` (go): optional `host` (IGNORE for joining — normalization uses path only, per upstream doctrine); verb optional.
- `gin.route.v1` / `echo.route.v1` (go): optional `route_group_prefix`/`effective_route_template`; verb omitted for `Any`.
- `rails.route.v1` (ruby): optional `scope_path`/`effective_route_template`; optional `controller_action` (`users#show`), `route_name`; verb optional.

Mount/include facts (4 prefix-join inputs + 1 evidence-only):

- `express.router_mount.v1`: `mount_path`, `normalized_mount_path`, `mount_target` (source text of mounted expression) — all always.
- `fastapi.include_router.v1`: `mount_target` always; `mount_path`/`normalized_mount_path` optional (literal `prefix=` only).
- `flask.blueprint_registration.v1`: `mount_target` always; `mount_path`/`normalized_mount_path` optional (literal `url_prefix=` only).
- `django.url_include.v1`: `mount_path`, `normalized_mount_path`, `included_module` (module literal, e.g. `"users.urls"`) always; optional `namespace`.
- `rails.mount.v1`: mounts Rack apps/engines whose internal routes are not in the fact stream — evidence count only, no join semantics.

Expansion input: `rails.resource_route.v1` (ruby): `resource_name`, `resource_kind` (`collection`|`singular`), optional `only`/`except` (string arrays as raw JSON), optional `scope_path`. No route template — Miller expands (Task 4).

Client extension: `http.client_request.v1` now spans vue/js/jsx/tsx/ts/python/csharp/go/java/ruby with `client` values including `requests`, `httpx`, `httpclient`, `net/http`, `java.net.http`, `net::http`. Shape unchanged (`target_path`, `url_kind`, `verb` always, `verb_source`, optional `import_source`) — `TryReadClientRequest` is language-agnostic and needs no change.

---

## File Structure

- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs` — 16 new pattern-id constants + whitelist entries.
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs` — backend-route read (`TryReadBackendRoute` → reuses `StructuralRouteHandler`), mount-fact read (`TryReadMountFact` → new `StructuralMountFact` record).
- Create: `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs` — the `backend-http` provider: fact collection, mount composition, Rails expansion, `FileRouteBridge.ResolveClientRequests` join, evidence, observation nodes.
- Modify: `src/Miller.Core/Resolver/RouteBridge.cs:229-246` — narrow `IsRealClientCall`'s csharp exclusion to legacy literals only.
- Modify: `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` — only if the structural-call path needs a marker for Task 5's narrowing (follow code reality on `TsClientCall`'s existing fields first).
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23` + `src/Miller.Indexing/BridgeProviderSelection.cs:8,72` — register `backend-http` in BOTH `DefaultProviders` lists + `CreateProvider` switch.
- Modify: `src/Miller.Server/Tools/TraceTool.cs` — diagnostics table (`FileRouteDiagnosticProviders`, `:1008`), evidence-key lists, next_actions, doc header, MCP `[Description]` provider list.
- Modify: `docs/contracts/trace-json-v1.md`, `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md`, `.agents/skills/miller-bridge-trace/SKILL.md` (+ regen `skills/`).
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`, `tests/Miller.Tests/Server/AgentInstructionsTests.cs`, `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs` (Scale).

## Task 1: Whitelist + Adapter Reads for the 16 New Families

**Files:**
- Modify: `src/Miller.Core/Graph/BridgeStructuralPatterns.cs`
- Modify: `src/Miller.Core/Graph/StructuralRouteFactAdapter.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (adapter tests follow the existing direct-call precedent at `:1061-1156`)

**Interfaces:**
- Consumes: `StructuralFactRecord` rows for the new pattern ids (shapes in "Decided Consumption Contracts").
- Produces: constants `ExpressRoute`, `ExpressRouterMount`, `FastifyRoute`, `FastApiRoute`, `FastApiIncludeRouter`, `FlaskRoute`, `FlaskBlueprintRegistration`, `DjangoUrlPattern`, `DjangoUrlInclude`, `SpringRequestMapping`, `GoNetHttpRoute`, `GinRoute`, `EchoRoute`, `RailsRoute`, `RailsResourceRoute`, `RailsMount` — all 16 appended to `BridgeFactPatternIds`; a `BackendRoutePatternIds` set (the 10 route-template families) consumed by the provider; `TryReadBackendRoute(fact, symbolsById, out StructuralRouteHandler)` returning the existing `StructuralRouteHandler` record (route = `effective_route_template` ?? `normalized_route_template`, nullable UPPERCASE verb, `containing_symbol_id` passthrough, `IsTestFact` filtered, spring `attribute_kind="class_route"` rejected); `TryReadMountFact(fact, symbolsById, out StructuralMountFact)` with `StructuralMountFact(Fact, MountPath, MountTarget, IncludedModule, FilePath)` (MountPath = `normalized_mount_path` ?? `mount_path`; facts with neither are rejected — an un-prefixed `include_router`/`register_blueprint` composes nothing).

**What to build:** The load whitelist plus two adapter reads. `TryReadBackendRoute` deliberately reuses `StructuralRouteHandler` so `FileRouteBridge.ResolveClientRequests` consumes backend routes with zero resolver changes. Keep `TryReadRouteHandler` (Next/Nuxt) byte-identical — the new read is a sibling, not an extension, because precedence differs (`route_path` does not exist on backend families; `effective_route_template` must win over `normalized_route_template`).

**Approach:** Rails `resource_route` and the mount facts are NOT `TryReadBackendRoute` inputs (no route template) — the provider consumes them separately in Tasks 3–4. `rails.mount.v1` gets a constant and whitelist entry but no adapter read beyond evidence counting.

**Acceptance criteria:**
- [ ] All 16 ids load through `SqliteBridgeReader` (fixture-fact test proves facts reach providers).
- [ ] `TryReadBackendRoute`: effective-template precedence proven (fastapi `router_prefix` fixture), nullable verb (express `app.all` shape), spring `class_route` rejected, django regex-syntax fact rejected (blank route), test facts rejected.
- [ ] `TryReadMountFact`: django `included_module` read; prefix-less fastapi include rejected.
- [ ] Existing Next/Nuxt handler and navigation reads byte-identical (existing tests green).
- [ ] Worker-scope verification passes, committed.

## Task 2: `backend-http` Provider — Direct Joins + Registration

**Files:**
- Create: `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
- Modify: `src/Miller.Core/Graph/BridgeGraphBuilder.cs:23`, `src/Miller.Indexing/BridgeProviderSelection.cs:8,72`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: Task 1 reads; `FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers)` exactly as `ApiRouteBridgeProvider.BuildCandidates` does (`src/Miller.Core/Graph/FileRouteBridgeProvider.cs:152-190` is the template).
- Produces: provider `BackendHttpBridgeProvider` with `const ProviderId = "backend-http"` + `Instance`, registered in BOTH `DefaultProviders` lists and `CreateProvider` (`"backend-http"` valid in `miller.json` `bridge.providers`); `BridgeKind.Hits` edges targeting the handler fact's `containing_symbol_id` when present, else synthesized `Endpoint` nodes; evidence keys `backend-http.clientRequests`, `.routeFacts`, `.mounts`, `.candidates`, `.ambiguousMatches` (Tasks 3–4 add `.composedRoutes`, `.unanchoredMounts`, `.expandedResourceRoutes`); observation nodes for unmatched client requests (canonical-route `TsType`) and route facts (`Endpoint`), same as `ApiRouteBridgeProvider.BuildObservationNodes`.
- Produces for Tasks 3–4: private collection points where mount facts and resource-route facts are gathered per `BuildCandidates` run, plus the `List<StructuralRouteHandler>` the composition/expansion passes append to before the resolve call.

**What to build:** One provider generic over `BackendRoutePatternIds` — a single fact loop collecting client requests (via `TryReadClientRequest`), backend routes (via `TryReadBackendRoute`), mount facts, and resource-route facts, then one `ResolveClientRequests` call. Verb rules come free from the resolver: handler verb equal → High; different → no edge; handler verb null → Medium `verb_unknown`. Skip result `"no backend-http bridge evidence"` when all counts are zero.

**Approach:** Do NOT make it 10 providers or extend `ApiRouteBridgeDescriptor` into a list-shape — a class of its own keeps the descriptor record honest and gives the enrichment passes a home. Per-family breakdowns go in evidence counts only if a count is cheap (e.g. `backend-http.routeFacts` total is required; per-framework counts are optional, report-only). Same client facts also feed dotnet-web/nextjs-api/nuxt-api — graph-level signature dedupe handles overlap (targets differ); observation-node `TryAdd` collapses duplicates (existing precedent).

**Acceptance criteria:**
- [ ] Fixture facts: `fetch("/api/users", {method:"POST"})` + express `router.post("/api/users")` → verb-known High `Hits` edge bound to the containing handler symbol.
- [ ] Colon-param join: client `/api/users/42` + fastapi `normalized_route_template=/api/users/:user_id` → High (canonical `{}` fold); two equally-specific routes → ambiguous count, no edge.
- [ ] Verb-null arm: gin `Any` route (no verb) + GET client → Medium `verb_unknown`; POST client + GET-only spring `http_method` fact → no edge.
- [ ] Django url_pattern (never a verb) + GET client → Medium `verb_unknown`.
- [ ] Registered in both `DefaultProviders` lists + `CreateProvider`; `miller.json` selection works; pure-frontend repo (client requests, zero backend routes) emits observations without fabricated edges.
- [ ] Existing providers' behavior byte-identical (all existing tests green).
- [ ] Worker-scope verification passes, committed.

## Task 3: Cross-File Mount-Prefix Composition

**Files:**
- Modify: `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2's collected mount facts + backend routes; workspace symbols (`context.SymbolsById`) for identifier anchoring.
- Produces: composed `StructuralRouteHandler` entries (RoutePath = `JoinRoute(mountPrefix, route.RoutePath)`-equivalent; keep the route fact's verb/symbol/file/line) appended before the resolve call; evidence keys `backend-http.composedRoutes` and `backend-http.unanchoredMounts`.

**What to build:** The Miller-side half of the handoff's "mount/include facts are the cross-file prefix-join inputs". For each mount fact, anchor it to route facts in another file, then emit composed route variants so `fetch("/users/42")` matches express `router.get("/:id")` mounted at `app.use("/users", usersRouter)`. Two anchor tiers, both deterministic:

1. **Module-path anchor (django):** `included_module` `"users.urls"` → route facts whose workspace-relative path ends `users/urls.py` (dots → path separators, `.py` appended). Zero or multiple matching files → no compose, count `unanchoredMounts`.
2. **Identifier anchor (express/fastapi/flask):** take the trailing identifier token of `mount_target` (`usersRouter`, `users.router` → `router` qualified by module `users`, `users_bp`); compose only when exactly ONE non-test file both defines a symbol with that name (via `context.SymbolsById`) and owns route facts of the matching framework family. Zero or ties → no compose, count `unanchoredMounts`. For dotted fastapi targets (`users.router`), require the module segment to match the defining file's stem as well.

Compose only route facts WITHOUT `effective_route_template` (a same-file-prefixed fact is already mounted upstream; composing again double-prefixes). The original un-composed handler entry is REPLACED by the composed one for anchored (mount, route-file) pairs — the router-local path (`/:id`) is not client-reachable once mounted.

**Approach:** Ambiguity poisons rather than degrades, mirroring upstream doctrine — never emit both a composed and heuristic alternative. Composed routes keep normal band rules (verb-matched → High): both endpoints of the join are source-attested facts and the anchor was required to be unambiguous. `rails.mount.v1` is counted in `.mounts` but never composes. If `SymbolsById` cannot answer "which file defines identifier X" without an O(symbols) scan per mount, build one name→files lookup per `BuildCandidates` run — do not add reader/SQL surface for this.

**Acceptance criteria:**
- [ ] Express fixture: routes file (`router.get("/:id")`, symbol `usersRouter` exported) + mount file (`app.use("/users", usersRouter)` fact) + client `fetch("/users/42")` → High edge; router-local `/{}` no longer emitted as an endpoint for that pair.
- [ ] Django fixture: `url_include` (`mount_path="/shop/"`, `included_module="shop.urls"`) + `url_pattern` in `shop/urls.py` → composed Medium `verb_unknown` edge for a matching GET client.
- [ ] Two files defining `usersRouter` → no composed edges, `unanchoredMounts` counted; prefix-less fastapi include → no compose.
- [ ] Fact with `effective_route_template` never double-composed.
- [ ] Worker-scope verification passes, committed.

## Task 4: Rails Semantics — Resource Expansion + controller_action Binding

**Files:**
- Modify: `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: Task 2's collected `rails.resource_route.v1` facts and `rails.route.v1` handlers; `context.SymbolsById` for controller lookup.
- Produces: expanded `StructuralRouteHandler` entries for resource routes; endpoint symbol binding for Rails facts carrying `controller_action`; evidence key `backend-http.expandedResourceRoutes`.

**What to build:** The handoff's "rails.resource_route.v1 needs Rails-semantics expansion on Miller's side". Expansion is deterministic Rails doctrine:

- `resource_kind="collection"` (`resources :users`): index `GET /users`, create `POST /users`, new `GET /users/new`, edit `GET /users/:id/edit`, show `GET /users/:id`, update `PATCH /users/:id` AND `PUT /users/:id`, destroy `DELETE /users/:id`.
- `resource_kind="singular"` (`resource :profile`): show `GET /profile`, create `POST /profile`, new `GET /profile/new`, edit `GET /profile/edit`, update `PATCH /profile` AND `PUT /profile`, destroy `DELETE /profile` (no index, no `:id`).
- `only`/`except` (raw JSON string arrays — parse with `System.Text.Json`) filter the action set; `scope_path` prefixes the paths. Every expanded route is verb-known (Rails semantics are as deterministic as fetch's spec-default GET) → High on verb match.

Symbol binding: for expanded routes and for `rails.route.v1` facts with `controller_action` (`"users#show"`), bind the endpoint to the `<CamelCase(name)>Controller` method symbol (`UsersController` + `show`) when exactly one non-test match exists in `SymbolsById`; otherwise fall back to the fact's `containing_symbol_id` (usually null in `routes.rb`) → synthesized `Endpoint` node. Expanded resource routes use the conventional action names as the lookup (`index`, `show`, …).

**Approach:** Expanded handlers carry the resource fact's file/line (routes.rb) so trace output points at the declaring DSL line; the bound symbol (when found) carries the controller location. Unambiguous-or-nothing on controller binding — never bind on name similarity.

**Acceptance criteria:**
- [ ] `resources :users` fixture fact → 8 expanded routes; `only: [:index, :show]` → 2; `except` honored; singular variant correct; `scope_path="/admin"` prefixes all.
- [ ] Client `fetch("/users/42")` GET → High edge to show route; `DELETE` client → High to destroy; expanded routes join through the same canonical fold.
- [ ] `controller_action`/conventional binding: `UsersController#show` symbol present → edge targets that symbol id; controller absent → synthesized endpoint node, edge still emitted.
- [ ] Worker-scope verification passes, committed.

## Task 5: C# Structural Client Requests Through dotnet-web

**Files:**
- Modify: `src/Miller.Core/Resolver/RouteBridge.cs:229-246` (`IsRealClientCall`)
- Modify (only if needed per code reality): `src/Miller.Core/Graph/DotnetWebBridgeProvider.cs` (`ToClientCall(StructuralClientRequest)`, `:569`)
- Test: `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs`, `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: `http.client_request.v1` facts in csharp (also go/java/ruby/python — these already flow; csharp is the one the filter blocks).
- Produces: decided contract — a **structural-fact-derived** csharp client call is a real client call (it is M2-disciplined, non-test-filtered evidence of an outbound `HttpClient` request); a **legacy csharp url literal** stays excluded (the design-§4 heuristic it encodes — "a csharp url literal is a test HttpClient call" — remains true for raw literals). dotnet-web gains service-to-service edges: C# `HttpClient` call → ASP.NET endpoint.

**What to build:** Narrow the exclusion at `RouteBridge.cs:236` so it applies only to calls that did NOT originate from a structural client-request fact. Inspect `TsClientCall`'s existing fields first (it already carries an attestation-ish member — follow code reality); if no existing field distinguishes structural origin, add one defaulted so every current construction site is byte-identical in behavior. Update the XML doc on `IsRealClientCall` to state both halves of the contract.

**Approach:** `IsTestFact` + the fact's `is_test` already filter test-project HttpClient calls on the structural path — assert this with a test-path csharp fixture fact (rejected), plus a non-test csharp fact (edge emitted). Check F4 `DedupeClientCalls` interplay: a csharp structural request has no legacy-literal twin under the legacy exclusion, so suppression sets simply gain entries — assert no behavior change for js-side dedupe. The backend-http provider (Task 2) needs no change — `ResolveClientRequests` has no language filter.

**Acceptance criteria:**
- [ ] Non-test csharp `http.client_request.v1` fact (`HttpClient.GetAsync("/api/users/42")` shape) + ASP.NET attribute-route endpoint → verb-known High edge through dotnet-web.
- [ ] Test-path csharp client fact → rejected (no edge); legacy csharp url literal → still excluded.
- [ ] Existing js/ts client behavior and F4 dedupe byte-identical (existing tests green).
- [ ] Worker-scope verification passes, committed.

## Task 6: Trace Surface — Diagnostics, Evidence Keys, Render

**Files:**
- Modify: `src/Miller.Server/Tools/TraceTool.cs` (`FileRouteDiagnosticProviders` `:1008`, `HasRouteFactEvidence`/next_actions lists, class doc header, MCP `[Description]` provider list)
- Test: `tests/Miller.Tests/Tools/TraceToolTests.cs`

**Interfaces:**
- Consumes: Task 2–5 evidence keys, provider id, observation nodes.
- Produces: route diagnostics covering `backend-http` (provider display noun pair, e.g. `("backend-http", "Backend", "route fact")` — follow the existing tuple shape); fallback next_actions referencing the new pattern ids (e.g. `patterns operation=search pattern_id=express.route.v1`) when route facts are absent; `[Description]`/doc-header provider list = `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, `backend-http`.

**What to build:** Wire `backend-http` into the trace tool's hardcoded lists so `not_on_bridge`/route-string diagnostics and pattern-audit next_actions cover the new evidence. Compact and JSON must agree on kind/label/band/flags for backend edges (`Hits` already renders as `route` — assert, don't change).

**Acceptance criteria:**
- [ ] Route-string trace over an unmatched backend client request surfaces `backend-http` diagnostics with correct nouns.
- [ ] next_actions reference the new pattern ids appropriately when evidence is absent/present.
- [ ] Compact + JSON agree on kind/label/band/flags for backend edges (fixture-fact TraceTool tests).
- [ ] Worker-scope verification passes, committed.

## Task 7: Live Scale Coverage — All 10 Languages (Language-Parity Gate)

**Files:**
- Modify: `tests/Miller.Tests/Indexing/LiveBridgeTraceTests.cs`

**Interfaces:**
- Consumes: pinned 2.7.0 binary; all consumption legs (Tasks 1–6).
- Produces: live proof via `ScaleTestSupport.RequireJulieServer()` + `TempWorkspace` + `ExtractAndLoad` + `TraceTool.Run`, following the existing fixture-writer pattern (`WriteNextApiFixture` et al.).

**What to build:** Grouped polyglot fixtures — one temp workspace per scenario group, not one per family — covering every new family live:

1. **JS/TS group:** express route + cross-file router mount (`app.use`) + fastify route + `fetch`/axios clients → direct High, mounted High, fastify High.
2. **Python group:** fastapi route (with `router_prefix`), flask blueprint route + `register_blueprint(url_prefix=...)` cross-file, django `path()` urlpattern + `url_include`, `requests.get`/`httpx` clients → fastapi High, flask composed High, django Medium `verb_unknown`.
3. **Go group:** `net/http` Go-1.22 pattern (`"GET /api/items/{id}"`), gin group route, echo route + `http.Get` client → verb-attested High; gin `Any` → Medium.
4. **Java group:** Spring `@RestController` with class-level `@RequestMapping` + `@GetMapping` + `HttpRequest` builder client → High; method-less `@RequestMapping` → Medium.
5. **Ruby group:** `config/routes.rb` draw block with verb DSL + `resources :users` + controller class + `Net::HTTP` client → DSL High, expanded-resource High bound to the controller method symbol.
6. **C# service-to-service:** `HttpClient` call + attribute-routed controller → High through dotnet-web (Task 5 live proof).

Per the language-parity rule, add one live assertion enumerating per-family fact counts from the extract (`SELECT pattern_id, COUNT(*)` over the fixture workspace's `structural_facts`) proving every one of the 16 ids emits on this extract — a family that silently emits zero fails the test, not the reader.

**Approach:** `_output.WriteLine` evidence; compact render lines asserted (`--route-->` + band). Scale trait via `ScaleTestSupport.RequireJulieServer()` or the convention guard fails the build. If a family emits nothing live that the release notes claim, STOP and report upstream — do not soften the parity assertion.

**Acceptance criteria:**
- [ ] All six scenario groups green via `scripts/test.sh scale`.
- [ ] Per-family emission assertion covers all 16 pattern ids.
- [ ] Worker-scope verification passes, committed.

## Task 8: Docs, Instructions, Skill Sync + Branch Gate

**Files:**
- Modify: `docs/contracts/trace-json-v1.md`
- Modify: `src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md` + `tests/Miller.Tests/Server/AgentInstructionsTests.cs`
- Modify: `.agents/skills/miller-bridge-trace/SKILL.md` → run `scripts/sync-plugin-skills.sh`

**Interfaces:**
- Consumes: shipped behavior from Tasks 1–7.
- Produces: docs matching reality; green `AgentInstructionsTests`.

**What to build:** (a) trace-json-v1.md: add `backend-http` to the provider scope list with its evidence-count names, the mount-composition and Rails-expansion semantics (including the unambiguous-anchor rule and `verb_unknown` arms), and the csharp client-request contract; state what is NOT claimed (regex Django patterns, `rails.mount` engine internals, non-literal templates — upstream M2 silence). (b) MILLER_AGENT_INSTRUCTIONS.md: provider list + fact-feed sentence gains `backend-http` and the five new client languages; update `AgentInstructionsTests` exact strings in lockstep. (c) Skill: provider list, backend families, pattern-audit examples (`pattern_id=express.route.v1`); regen `skills/` and confirm byte-identical. CLAUDE.md needs no edit (it does not enumerate providers) — verify; if an edit IS needed, run `scripts/sync-agents.sh` + `cmp -s CLAUDE.md AGENTS.md`.

**Acceptance criteria:**
- [ ] `AgentInstructionsTests` green with updated strings; docs match shipped behavior.
- [ ] Skill regenerated; `skills/` matches `.agents/skills/`.
- [ ] Branch gate green: `dotnet build Miller.slnx -c Release` + `scripts/test.sh` + `scripts/test.sh scale`.
- [ ] Goldfish checkpoint written; all work committed locally. No push.

## Verification Strategy

**Project source of truth:** `CLAUDE.md` (Testing section), `scripts/test.sh`.

**Worker red/green scope:**
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal`
- `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~TraceToolTests&Category!=Scale" -v minimal`
- Task 8: `--filter "FullyQualifiedName~AgentInstructionsTests&Category!=Scale"`

**Worker ceiling:** Full fast suite via `scripts/test.sh` (baseline 2634 green at `2c5fcb5`, <30s tripwire).

**Worker gate invariant:** New facts load and bridge with honest verbs/bands; mount composition and Rails expansion emit only on unambiguous anchors; every pre-existing bridge behavior stays green; no duplicate endpoints/edges from overlapping evidence.

**Lead affected-change scope:** Miller `impact` over the working diff plus the focused tests it lists; `dotnet build Miller.slnx -c Release` after each coherent batch.

**Branch gate:** `dotnet build Miller.slnx -c Release` + `scripts/test.sh`; `scripts/test.sh scale` mandatory for Task 7 and at the final commit (baseline 38 green at `2c5fcb5`), recommended after Tasks 3–5.

**Replay/metric evidence:** Task 7 live scenarios and the 16-family emission assertion are hard gates; evidence-count values in trace output are report-only.

**Escalation triggers:** SQLite reader contract changes; `MillerExtractContract` gate mismatches against 2.7.0; confidence-band changes beyond what Tasks 2–5 specify; failures in non-bridge trace modes; a 2.7.0 family emitting nothing live that the release notes claim (report upstream, stop); mount-anchor false positives observed live (tighten the anchor, never band-aid with a lower band).

**Assigned verification failure:** Workers stop and report when assigned verification fails, unless this plan explicitly says to update that gate.

**Verification ledger:** Record invariant, command, scope label, commit SHA, result, timestamp per task. Reuse passing evidence for the same HEAD.

## Model Routing

**Project source of truth:** none (`RAZORBACK.md` absent; no harness routing docs) → `inherit` for all tiers.

**Strategy / Implementation / Mechanical / Gate-interpretation / Escalation tiers:** Harness mapping: `inherit` (session model). **Worker eligibility:** all tasks are bounded with decided contracts; any worker may take them. **Mechanical exclusion:** Task 8's doc edits own no test gates beyond `AgentInstructionsTests`; Tasks 1–7 own the suites they run. **Escalation triggers:** Task 3 anchor-rule decisions and Task 5 exclusion-narrowing decisions escalate to the session lead if code reality contradicts the decided shape.
