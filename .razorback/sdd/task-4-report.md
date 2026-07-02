# Task 4 report — Rails semantics: resource expansion + controller_action binding

Branch: `feat/backend-http-boundary`. Status: **DONE** (worker-scope verification green).

## What I implemented

Extended `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs` (same provider file, no new type/SQL/reader
surface) with Rails semantics — Rails is Miller's job per the julie handoff.

1. **Collect `rails.resource_route.v1` facts.** Added a fact-loop branch: the pattern matches none of the
   existing reads (it is NOT in `BackendRoutePatternIds`), so raw `StructuralFactRecord`s are collected into
   `resourceFacts`. The `IsTestFact` filter mirrors the route/mount reads so a test-scoped routes file never
   expands. The skip guard now also requires `resourceFacts.Count == 0`, so a resource-fact-only repo is ACTIVE.

2. **Resource expansion (`ExpandResourceRoutes`).** Deterministic Rails doctrine → verb-known handlers:
   - Base path segment = `resource_name` with a leading `:` stripped.
   - `resource_kind="collection"` → 8 entries (index, create, new, edit, show, update as **PATCH and PUT**,
     destroy); `resource_kind="singular"` → 7 entries (no index, no `:id`). Unknown kind → expand nothing
     (honest, never fabricate).
   - `only`/`except` filter the ACTION set (7 conventional actions); `scope_path` prefixes every path via the
     existing `JoinRoute` helper.
   - Each expanded handler carries the resource fact's **routes.rb file/line** (trace points at the DSL line),
     `Fact` = the resource fact, uppercase verb, and `ContainingSymbolId` = the bound controller method id (see
     binding) or the fact's containing symbol id (usually blank → synthesized endpoint node).
   - New evidence key `backend-http.expandedResourceRoutes` = count of expanded entries (full collection = 8;
     `only:[:index,:show]` = 2).

3. **Controller binding (unambiguous-or-nothing).**
   - `BuildControllerMethodIndex` builds a `(controllerClass, action) → unique-method-id` lookup ONCE per run
     (mirrors Task 3's `BuildNameToFiles`; no O(symbols) scan per route). A second collision poisons the key to
     null (ambiguous → never binds). Matches non-test `Kind=="method"` symbols by `ParentClassName` + `Name`.
   - `rails.route.v1` `controller_action` (`"users#show"`): `BindRailsRouteController` rebinds the handler's
     `ContainingSymbolId` to `CamelCase(controller)+"Controller"`.`action` (no inflection — controller_action is
     receiver identity). Unresolved → handler unchanged (falls back to endpoint node).
   - Expanded resource routes map to a **PLURAL** controller: collection `resource_name` is already plural;
     singular `resource_name` is pluralized (`Pluralize`) first. A singular `ProfileController` decoy is never
     bound (different key). Lookup key = the conventional action name.

4. **Helpers (all private, local complexity):** `CamelCase` (snake→Pascal, split on `_`), `Pluralize`
   (`s/x/z/ch/sh`→`es`; consonant+`y`→`ies`; else `+s`), `ParseActionList` (System.Text.Json first, tolerant
   bracket/comma fallback for a raw ruby `[:index, :show]`), `NormalizeAction` / `StripLeadingColon` (tolerant of
   the leading `:` variant), `ComputeAllowedActions`, `ResolveControllerMethod`, `IsVowel`, plus the two static
   route tables and the `ConventionalActions` set.

## Verification

- The invariant each test proves is documented inline in each test's leading comment.
- Command: `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal`
  → **Passed: 144, Failed: 0** (131 prior + 13 new cases). 2026-07-02.
- Rails-only filter run: 13 passed, 0 failed.
- `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)**. 2026-07-02.
- `scripts/test.sh` (fast-suite ceiling) → **Passed: 2685, Failed: 0**, wall 14s (< 30s ceiling). 2026-07-02.
- Scale suite NOT run (per task scope; live all-language coverage is Task 7).

## Files changed

- `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs` — resource-fact collection, controller-method index,
  rails.route rebind, resource expansion, new evidence key, updated skip guard, Task 4 helper region.
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` — 10 new `[Fact]`/`[Theory]` methods (13 cases) + a
  `ResourceFact` builder.

## Miller calls used (and what each confirmed)

- `inspect BackendHttpBridgeProvider depth=full` — the Task 2/3 shape: fact loop, `routeHandlers` assembly,
  `ComposeMountedRoutes`, `JoinRoute`/`Normalize`/`Stem`, evidence dict, skip guard.
- `inspect StructuralRouteHandler depth=full` — ctor `(Fact, RoutePath, Verb, ContainingSymbolId, FilePath, Line)`;
  it CAN carry the routes.rb file/line and the resource fact (no plan mismatch).
- `inspect SymbolDetail (Contracts) depth=full` — record has `Id, Name, Kind, FilePath, Signature, Namespace,
  IsTest, ParentClassName` (no plan mismatch; `ParentClassName` exists).
- `inspect MetadataString`, `inspect BridgeStructuralPatterns depth=full` — `MetadataString` is public on the
  adapter; `RailsResourceRoute`/`RailsRoute`/`RailsMount` constants exist; `RailsResourceRoute` is NOT in
  `BackendRoutePatternIds` (so resource facts fall through to my branch, as the spec states).

## API-shape evidence

- `StructuralRouteHandler` ctor + fields, `SymbolDetail.{Id,Name,Kind,IsTest,ParentClassName}`,
  `StructuralFactRecord.{Metadata,Path,Span,ContainingSymbolId,PatternId}`, the rails constants,
  `JoinRoute`/`Normalize`/`Stem`, and the `FileRouteBridge.ResolveClientRequests` edge shape confirmed via Miller
  inspect plus a direct read of `StructuralRouteFactAdapter.cs` and `FileRouteBridge.cs`.
- **`FileRouteBridge.BuildClientRequestEdge`**: a handler's non-empty `ContainingSymbolId` becomes the edge
  `TargetRef.SymbolId`; empty → synthesized endpoint node (`SymbolId` null). This is exactly the rebind/fallback
  mechanism the spec relies on.
- **Live-binding chain (Task 7 relevance):** julie-extractors emits `SymbolKind::Method` for Ruby `def` with
  `parent_id` set to the class symbol; Miller's `RepositoryIndexLoader.ProjectToSymbolDetails` derives
  `ParentClassName` from the parent symbol's name — so a Rails `UsersController#show` resolves live.

## Contract findings — `~/source/julie-extractors/docs/contracts/structural-fact-patterns.json` (2.7.0, readable)

- **`rails.resource_route.v1`** metadata keys: `pattern_version`, `query_family`, `framework`, `api_style`
  (`"dsl_routing"`), `resource_name` (string, "Declared resource name", always), `resource_kind` (string,
  "collection or singular", always), `only` (**string_array**, "Literal only: action list", optional), `except`
  (**string_array**, "Literal except: action list", optional), `scope_path` (string, optional).
- **`rails.route.v1`** relevant keys: `route_template`, `normalized_route_template` (always), `scope_path`,
  `effective_route_template`, `verb` (uppercase HTTP method, optional), `verb_source`, `controller_action`
  ("Literal controller#action target", optional), `route_name`.
- **The contract does NOT pin the string format** of `resource_name` (`":users"` vs `"users"`) or the
  `only`/`except` elements (`":index"` vs `"index"`) — it only types them as string / string_array. Because the
  fact `Metadata` transport is `IReadOnlyDictionary<string,string>`, a string_array arrives as a JSON-encoded
  string. **Consequence:** my parsing is deliberately tolerant — strip a leading `:` on `resource_name` and on
  each action element, parse `only`/`except` as JSON first and fall back to a bracket/comma split (handles a raw
  ruby `[:index, :show]`). Task 7 must confirm the exact live emission; my code already accepts every plausible
  form the contract permits.

## Self-review findings

- No new MCP tool / reader / SQL surface; complexity stays inside the provider (CLAUDE.md + Task 4 constraint).
- Rebind reads the PRISTINE `backendRoutes` for composition and the route-fact count (decoupled from the rebind
  copy) so `backend-http.routeFacts` and `.composedRoutes` stay byte-identical — verified: all 131 prior tests
  green.
- Controller-method index built once (no per-route O(symbols) scan).
- Adding the always-present `backend-http.expandedResourceRoutes` key does not break any existing test (full fast
  suite green; no test asserts an exact evidence-dict size).

## Judgment calls (file — X over Y because …)

- `BackendHttpBridgeProvider.cs` fact loop — **filter resource facts through `IsTestFact`** over collecting them
  raw, because every other read (route/mount) filters tests and a `resources` declaration in a test-scoped routes
  file should not synthesize product edges. Plan-consistent parity; noted for Task 7.
- `ExpandResourceRoutes` — **unknown `resource_kind` → expand nothing** over defaulting to collection, because
  fabricating a route shape from an unrecognized kind is dishonest; the contract guarantees the two values, and a
  future third kind should be added deliberately.
- `ComputeAllowedActions` — **`only` is intersected with the 7 conventional actions** so a custom/member action
  name in `only:` is ignored (Miller emits only the 7 conventional routes; custom member/collection routes are
  out of scope for this expansion table).
- `BuildControllerMethodIndex` — **require `Kind=="method"`** per the spec ("non-test method symbol"); the live
  Ruby chain above confirms this resolves.
- Key separator is a single space (`controllerClass + ' ' + action`) — safe because CamelCased class names and
  action names contain no spaces.
