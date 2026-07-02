# Task 2 Report — `backend-http` Provider: Direct Joins + Registration

**Status:** COMPLETE (green, committed)
**Branch:** `feat/backend-http-boundary`

(This report replaces stale content from a prior `nextjs-bridge-trace` SDD run.)

## What was implemented

A standalone bridge provider `BackendHttpBridgeProvider` that joins backend HTTP client requests to
server route-template handlers, plus its registration in all three required sites.

- **New:** `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs`
  - `sealed class BackendHttpBridgeProvider : IBridgeProvider`, `const ProviderId = "backend-http"`,
    static `Instance`, private ctor, `Id => ProviderId`.
  - `BuildCandidates`: ONE ordered fact loop (`OrderBy(Path, Ordinal).ThenBy(Span.StartByte)`) collecting
    `clientRequests` (`TryReadClientRequest`), `backendRoutes` (`TryReadBackendRoute`), `mountFacts`
    (`TryReadMountFact` — collected, not composed), and a `railsMountCount` (every
    `fact.PatternId == BridgeStructuralPatterns.RailsMount`, evidence-only, never read).
  - `routeHandlers = new List<StructuralRouteHandler>(backendRoutes)` kept as a distinct local — the T3/T4
    append point before the single `FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers)` call.
  - Evidence keys: `backend-http.clientRequests`, `.routeFacts`, `.mounts` (= `mountFacts.Count +
    railsMountCount`), `.candidates`, `.ambiguousMatches`. No T3/T4 keys added.
  - Skip `"no backend-http bridge evidence"` iff all four counts are zero; else `ActiveResult` with
    observation nodes (client → canonical-route `TsType`, each `routeHandlers` entry → `Endpoint`),
    mirroring `ApiRouteBridgeProvider.BuildObservationNodes` and built over `routeHandlers` so T3/T4
    composed/expanded handlers get nodes for free.
- **Modified:** `src/Miller.Core/Graph/BridgeGraphBuilder.cs` — appended `BackendHttpBridgeProvider.Instance`
  to `DefaultProviders`.
- **Modified:** `src/Miller.Indexing/BridgeProviderSelection.cs` — appended to `DefaultProviders` and added
  `BackendHttpBridgeProvider.ProviderId => BackendHttpBridgeProvider.Instance` to the `CreateProvider` switch
  so `"backend-http"` is valid in `miller.json` `bridge.providers`.

All verb rules are inherited unchanged from `FileRouteBridge.ResolveClientRequests` (verb equal → High
`RouteVerbMatch`; verb different → no edge; verb null → Medium `RouteOnlyMatch`/`verb_unknown`; equally-specific
verb-exact tie → ambiguous, no edge; edge target bound to handler `containing_symbol_id`). The resolver was NOT
touched.

## Files changed

- `src/Miller.Core/Graph/BackendHttpBridgeProvider.cs` (new)
- `src/Miller.Core/Graph/BridgeGraphBuilder.cs` (DefaultProviders +1)
- `src/Miller.Indexing/BridgeProviderSelection.cs` (DefaultProviders +1, CreateProvider switch +1)
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (+9 provider tests)
- `tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs` (default-list assertion updated + 1 end-to-end selection test + fixture helper)

## Tests (caller-facing: `BridgeGraphBuilder.Build` / `BridgeProviderSelection.ProvidersForDatabase`)

`tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (9 new tests):
- `BackendHttp_express_post_route_hits_client_request_bound_to_handler_symbol_High` — verb-equal Express POST
  joins client to the handler symbol at High, verb-known; evidence counts (`clientRequests/routeFacts/
  candidates=1`, `ambiguousMatches/mounts=0`).
- `BackendHttp_colon_param_fastapi_route_hits_concrete_path_client_High` — `/api/users/42` folds against
  `:user_id` canonically → High.
- `BackendHttp_two_equally_specific_routes_are_ambiguous_no_edge` — specificity tie → `ambiguousMatches=1`,
  `candidates=0`, no edge.
- `BackendHttp_gin_any_route_with_no_verb_hits_client_Medium_verb_unknown` — verbless gin → Medium,
  `IsVerbUnknown`.
- `BackendHttp_post_client_and_get_only_spring_route_produce_no_edge` — verb-known-different → no edge.
- `BackendHttp_django_path_pattern_no_verb_hits_client_Medium_verb_unknown` — Django path URLconf (verbless) →
  Medium.
- `BackendHttp_client_only_repo_is_active_with_observation_node_and_no_edges` — pure-frontend repo is ACTIVE,
  emits a client `TsType` observation node with `backend-http` provenance, `candidates=0`, no Hits edge.
- `BackendHttp_mounts_evidence_counts_mount_facts_and_evidence_only_rails_mount` — `mounts=2` (1 express mount +
  1 evidence-only `rails.mount`), no composed edge.
- `BackendHttp_no_evidence_skips_with_reason_and_zero_counts` — skip reason + all-zero counts.

`tests/Miller.Tests/Indexing/RepositoryIndexLoaderBridgeTests.cs`:
- Updated `ProvidersForDatabase_NoConfig_ReturnsAllDefaultBridgeProviders` expected list to append
  `"backend-http"` (would otherwise break — the only pre-existing brittle exact-list assertion).
- Added `Load_RootMillerJsonBackendHttpProvider_BridgesClientRequestToBackendRoute` — end-to-end through the
  production `RepositoryIndexLoader.Load` path with a `miller.json` selecting `["backend-http"]`: proves the
  `CreateProvider` switch maps the id AND that `express.route.v1` loads through the Task-1 SQL whitelist and
  bridges a POST client to the containing handler symbol at High.

## Verification

Invariant each check proves, exact command, result, timestamp:

| Invariant proven | Command | Result |
| --- | --- | --- |
| RED — 9 backend-http behaviors fail before impl (feature missing, not typos) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal` | Failed: 9, Passed: 113 (expected) |
| GREEN — provider behaviors + evidence/skip/observation nodes | same filter | Passed: 122, Failed: 0 |
| Selection + end-to-end Load path (CreateProvider + SQL whitelist) | `dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~RepositoryIndexLoaderBridgeTests&Category!=Scale" -v minimal` | Passed: 17, Failed: 0 |
| Existing providers byte-identical (full fast suite, incl. nextjs-api/nuxt-api/dotnet-web shared-client fixtures) | `scripts/test.sh` | Passed: 2663, Failed: 0, 21s (<30s ceiling) |
| 0 warnings / 0 errors (warnings-as-errors) | `dotnet build Miller.slnx -c Release` | Build succeeded, 0 Warning(s), 0 Error(s) |

Timestamp: 2026-07-02 (local session). Commit SHA: recorded on the `feat: backend-http bridge provider` commit
on `feat/backend-http-boundary` (see `git log -1`).

## Miller calls used (orientation) and what each confirmed

- `inspect ApiRouteBridgeProvider depth=full` — the template: single ordered fact loop, `ResolveClientRequests`
  call, evidence-count dict, skip/active branch, `BuildObservationNodes` (client→`TsType`, handler→`Endpoint`).
- `inspect FileRouteBridge depth=full` — confirmed `ResolveClientRequests(IReadOnlyList<StructuralClientRequest>,
  IReadOnlyList<StructuralRouteHandler>)` and `internal HandlerDisplay(StructuralRouteHandler)` signatures + verb
  rules; resolver binds edge target to `containing_symbol_id`.
- `inspect StructuralRouteFactAdapter depth=full` — confirmed `TryReadClientRequest` (language-agnostic),
  `TryReadBackendRoute` (gates on `BackendRoutePatternIds`, verb nullable, Spring `class_route` rejected),
  `TryReadMountFact` (gates on the 4 mount families, `rails.mount` deliberately NOT read).
- `inspect BridgeStructuralPatterns depth=full` — confirmed `BackendRoutePatternIds` (10 families) and
  `RailsMount = "rails.mount.v1"`.
- `inspect BridgeProviderResult depth=full` / `inspect BridgeProviderContext depth=full` — confirmed
  `ActiveResult(edges, counts, observationNodes)` / `Skipped(reason, counts)` factories and context fields
  `.StructuralFacts` / `.SymbolsById`.
- `inspect StructuralMountFact depth=full` — confirmed the record shape (collected, not composed in T2).
- Read `BridgeGraphBuilder.cs`, `BridgeProviderSelection.cs`, `FileRouteBridgeProvider.cs`,
  `RepositoryIndexLoaderBridgeTests.cs` — confirmed the exact registration sites, the wrapper-class precedent,
  `FileRouteBridgeProvider.RouteDisplay` (`public static`), and the `Fact(...)` dictionary test helper.

## API-shape evidence

`FileRouteBridge.ResolveClientRequests`, `FileRouteBridge.HandlerDisplay`,
`FileRouteBridgeProvider.RouteDisplay`, `BridgeGraph.SynthesizeId`, `BridgeNodeKind.{TsType,Endpoint}`,
`BridgeProviderResult.{ActiveResult,Skipped}`, `BridgeStructuralPatterns.{BackendRoutePatternIds,RailsMount}`,
`StructuralRouteFactAdapter.{TryReadClientRequest,TryReadBackendRoute,TryReadMountFact}` — all confirmed by
Miller `inspect` before use (above). Evidence key strings and the skip reason are asserted verbatim by the new
tests.

## Self-review findings

- Adapters self-gate by pattern id and the three families (`http.client_request.v1`, the 10
  `BackendRoutePatternIds`, the 4 mount families) plus `rails.mount.v1` are DISJOINT, so the `if/continue`
  ordering cannot misclassify a fact. Verified by the passing per-family tests.
- Shared client facts also feed dotnet-web/nextjs-api/nuxt-api; no dedupe added — graph-level signature dedupe
  and observation-node `TryAdd` handle overlap (existing precedent). The full fast suite (incl. those fixtures)
  stayed green, confirming byte-identical existing behavior.
- Only one pre-existing test asserted the exact default-provider list; updated it. No other test asserts
  `ActiveProviders.Count`/`SkippedProviders.Count` (grep-verified), so the additive default provider is safe.

## Judgment calls (file:line — X over Y because …)

- `BackendHttpBridgeProvider.cs:44-72` — collect only `mountFacts` + `railsMountCount` (no resource-route list),
  over the plan prose's "collecting … resource-route facts", because the authoritative Task 2 spec scopes the
  seam to `routeHandlers` + `mountFacts`, `rails.resource_route.v1` is excluded from `BackendRoutePatternIds`
  and has no adapter read yet (Task 4 adds expansion), and adding an unused list now is speculative
  extensibility the architecture-quality bar forbids. The resource-route collection point is a Task 4 concern.
- `BackendHttpBridgeProvider.cs:77-84` — evidence key literals written inline
  (`"backend-http.clientRequests"`, …) rather than via a descriptor `EvidenceKey` helper, because this provider
  is standalone (not descriptor-driven) — matching the plan's "a class of its own keeps the descriptor record
  honest" and keeping T3/T4's added keys visible in one place.
- Selection/end-to-end test placed in `RepositoryIndexLoaderBridgeTests.cs` (the existing
  `ProvidersForDatabase`-oriented file) rather than a new file, following the established precedent for
  `nextjs-api`/`nuxt-api` selection tests.
