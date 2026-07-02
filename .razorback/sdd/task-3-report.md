# Task 3 — Cross-File Mount-Prefix Composition — Worker Report

**Status:** COMPLETE (green, committed).
**Branch:** `feat/backend-http-boundary`.

## What was implemented

`BackendHttpBridgeProvider.BuildCandidates` now runs a Miller-side mount-prefix
composition pass on the collected `mountFacts` + `backendRoutes` before the
`FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers)` call. For each
composing mount fact it anchors (deterministically, unambiguous-or-nothing) to route
facts in ANOTHER file and APPENDS composed `StructuralRouteHandler` variants to
`routeHandlers` — `RoutePath = JoinRoute(mount.MountPath, route.RoutePath)`, with the
route fact's `Verb`, `ContainingSymbolId`, `FilePath`, `Line`, and `Fact` unchanged
(implemented as `route with { RoutePath = ... }`). Two new evidence keys are emitted:
`backend-http.composedRoutes` (count of composed handlers appended) and
`backend-http.unanchoredMounts` (count of mount facts that failed to anchor).

Composition is **strictly additive**: `routeHandlers` starts as `new List<>(backendRoutes)`
and only ever `AddRange`s composed variants — no original entry is removed or mutated
(route facts carry no receiver identity, so router-ownership of a route is unprovable;
replacing would hide legitimate direct routes).

## Anchor algorithm as built (matches the decided spec)

- **Family gate.** Iterate `mountFacts` only (rails.mount excluded upstream by Task 1's
  `TryReadMountFact`, so it never composes). `RouteFamilyForMount(mount.Fact.PatternId)`
  maps `express.router_mount.v1→express.route.v1`, `fastapi.include_router.v1→fastapi.route.v1`,
  `flask.blueprint_registration.v1→flask.route.v1`, `django.url_include.v1→django.url_pattern.v1`;
  anything else → null → never composes.
- **Tier 1 — module-path anchor (django only).** `IncludedModule` `"shop.urls"` → suffix
  `"shop/urls.py"` (dots→`/`, `+ ".py"`). Among django route facts, collect distinct normalized
  paths that end at a **segment boundary** with that suffix (`path == suffix` or
  `path.EndsWith("/" + suffix)`). Exactly one distinct file → anchor; zero/multiple → null.
- **Tier 2 — identifier anchor (express/fastapi/flask).** `ExtractIdentifier(mount.MountTarget)`:
  trim; drop from the first `(` onward (`express.json()`→`express.json`, `require("./x")`→`require`);
  split on `.`; identifier = last identifier-like segment, module = preceding segment when dotted.
  No identifier-like token → null. Candidate files = { non-test files defining a symbol named
  `identifier` } ∩ { files owning ≥1 backend route of the matching family }. For a **dotted fastapi**
  target, additionally require the candidate file's stem (`users.py`→`users`) == module. Exactly one
  candidate → anchor; zero/tie → null.
- **Compose step.** For the single anchor file, compose EVERY matching-family route fact in that file
  that lacks `effective_route_template` (already same-file prefixed ⇒ skip, would double-prefix).
- **One name→files lookup** (`BuildNameToFiles`) built ONCE per `BuildCandidates` run (no O(symbols)
  scan per mount); no new reader/SQL surface.

### How an ambiguous/tied anchor emits ZERO composed routes (load-bearing)

Both anchor tiers return `string?`: `AnchorByModulePath` returns the file only when `files.Count == 1`;
`AnchorByIdentifier` returns the file only when `candidates.Count == 1` (after the fastapi stem filter).
Any zero-or-tied result returns `null`, and the caller does `unanchored++; continue;` — the compose loop
is skipped entirely for that mount. The compose loop is reached ONLY inside the `anchorFile is not null`
branch, so **not a single composed handler is appended from an ambiguous, tied, or absent anchor.** A
wrong/uncertain anchor composes nothing rather than fabricating a false bridge edge.

## Verification ledger

- **Invariant:** new backend HTTP mount facts compose prefixed routes ONLY on an unambiguous cross-file
  anchor; a zero/tied/absent anchor composes nothing (`unanchoredMounts` counted); composition is additive
  (originals retained); an `effective_route_template` fact is never double-composed; all pre-existing bridge
  behavior stays green.
- **Command (worker red/green scope):**
  `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal`
  → **Passed: 132, Failed: 0.**
- **Focused new-tests run** (`...BridgeGraphBuilderTests.BackendHttp...`): RED first (10 failing on missing
  composition), then GREEN → **Passed: 19, Failed: 0.**
- **Release build:** `dotnet build Miller.slnx -c Release` → **0 Warning(s) / 0 Error(s)**.
- **Worker ceiling:** `scripts/test.sh` → **Passed: 2673, Failed: 0**, fast wall time 13s (<30s ceiling).
- **Scope label:** worker. **Timestamp:** 2026-07-02. **Commit SHA:** see git log (Task 3 commit). Scale NOT run.

## Files changed (Task 3 only, committed)

- `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs` — composition pass wired into `BuildCandidates`;
  two evidence keys; private helpers `ComposeMountedRoutes`, `RouteFamilyForMount`, `AnchorByModulePath`,
  `AnchorByIdentifier`, `BuildNameToFiles`, `ExtractIdentifier`, `IsIdentifierLike`, `JoinRoute`,
  `Normalize`, `Stem`, and the `MountComposition` readonly record struct. Added `using Miller.Core.Contracts;`.
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` — 10 new tests in the backend-http section.

## Tests added (each names its invariant)

1. `..._express_router_mount_composes_prefixed_route_High_and_keeps_original` — happy-path express, High
   composed edge to handler symbol; additivity via `routeFacts==1` + original endpoint observation node present.
2. `..._django_url_include_module_anchor_composes_Medium_verb_unknown` — Tier 1 module anchor; verbless
   django → Medium `verb_unknown`.
3. `..._django_url_include_unmatched_module_composes_nothing_and_counts_unanchored` — **absence poisons**.
4. `..._two_files_define_router_identifier_is_ambiguous_composes_nothing` — **tie poisons**.
5. `..._middleware_mount_target_resolves_to_no_route_owning_file_composes_nothing` — `express.json()` →
   identifier names no route-owning file.
6. `..._prefixless_fastapi_include_is_not_a_mount_and_composes_nothing` — rejected upstream; direct join intact.
7. `..._same_router_mounted_at_two_prefixes_composes_both_variants` — variants under both prefixes, two edges.
8. `..._mixed_file_direct_route_also_gains_a_composed_variant_accepted_tradeoff` — the documented spurious
   compose, pinned + commented.
9. `..._route_with_effective_template_is_never_double_composed` — no `/users/users`; anchored-but-nothing is
   NOT counted unanchored.
10. `..._fastapi_dotted_target_requires_module_to_match_file_stem` — stem filter breaks an otherwise-tie.

## Miller calls used (+ what each confirmed)

- `inspect BackendHttpBridgeProvider depth=full` — Task 2 shape; insertion point is `routeHandlers = new
  List<>(backendRoutes)` right before `ResolveClientRequests`; existing evidence dict.
- `inspect StructuralRouteHandler depth=full` — positional record `Fact, RoutePath, Verb, ContainingSymbolId,
  FilePath, Line` (`with` works; only RoutePath changes).
- `inspect StructuralMountFact depth=full` — `Fact, MountPath, MountTarget, IncludedModule, FilePath`.
- `inspect SymbolDetail depth=full` — `Miller.Core.Contracts.SymbolDetail` has `Name, FilePath, IsTest`.
- `inspect BridgeStructuralPatterns depth=full` — pattern-id constants; mount/rails.mount excluded from
  `BackendRoutePatternIds`.
- `inspect BridgeProviderContext depth=full` — `Symbols`, `SymbolsById`, `StructuralFacts`.

## API-shape evidence

- `StructuralRouteFactAdapter.MetadataString` at `StructuralRouteFactAdapter.cs:326` —
  `public static string? MetadataString(StructuralFactRecord fact, string key)`, null for missing/blank,
  reachable (same `Miller.Core.Graph` namespace). Drives the `effective_route_template` skip.
- `handler.Fact.Path == handler.FilePath` (set equal by `TryReadBackendRoute`).
- `BridgeProviderContext.Symbols` == `BridgeGraphBuilder.Build`'s `symbols` arg (`BridgeGraphBuilder.cs:99-108`).

## Self-review findings

- Ambiguity guarantee verified structurally (both tiers `count == 1`-gated; else `continue`).
- Additive verified: originals never removed/mutated (`with` clones); pinned by tests #1, #7, #8.
- Path normalization (`\`→`/`) applied uniformly across name→files, route-owning files, and anchor compare.
- Flask shares the express identifier-only path (stem is fastapi-only, per spec); covered live in Task 7.

## Judgment calls

- `BackendHttpBridgeProvider.cs` `AnchorByModulePath` — **segment-boundary endsWith** (`path == suffix ||
  path.EndsWith("/" + suffix)`) over a bare `EndsWith` for the django module suffix, because it can only
  REMOVE false anchors (`myshop/urls.py` ≠ `shop/urls.py`), never add — consistent with "poisons, never
  degrades." Plan-consistent tightening, not a redesign.
- Additivity asserted via caller-facing evidence counts + one endpoint observation-node check (test #1)
  rather than reaching into private state — robust to display-normalization changes.
- No dedicated flask unit test (identical shape to express identifier anchor; exercised live in Task 7) —
  judged redundant over the plan's per-tier coverage.

## Concerns / not-in-scope

- No blockers; no plan mismatch — all decided contracts matched code reality (`StructuralRouteHandler.Fact`
  present, `MetadataString` public/reachable, `SymbolDetail.Name/FilePath/IsTest` present).
- Scale suite intentionally not run (worker scope); Task 7 owns live per-language proof.
