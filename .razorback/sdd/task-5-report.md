# Task 5 — C# Structural Client Requests Through dotnet-web

**Status:** DONE (green, committed on `feat/backend-http-boundary`).

> Note: this file previously held a report for a superseded plan's Task 5 ("Docs, Agent Guidance"). Under the
> current `docs/plans/2026-07-02-backend-http-boundary-consumption.md`, Task 5 is the RouteBridge change below
> (docs/agent-guidance is now Task 8). Overwritten per the Task 5 worker instruction directing the report here.

## What changed

Narrowed the blanket csharp exclusion in `RouteBridge.IsRealClientCall` so a **structural-fact-derived**
csharp `HttpClient` call becomes a first-class client call (enabling C# service-to-service bridging:
`HttpClient` call → ASP.NET endpoint), while a **legacy csharp url literal stays excluded**.

- `src/Miller.Core/Resolver/RouteBridge.cs` — `IsRealClientCall`: the csharp exclusion now also requires
  `string.IsNullOrWhiteSpace(call.AttestedVerb)`, i.e. it applies only to calls that did NOT originate from
  an `http.client_request.v1` structural fact. XML doc rewritten to state both halves of the decided contract
  (structural-derived csharp call = real client call; legacy csharp url literal = still excluded).
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` — 3 new tests (positive High edge, test-symbol
  rejection, legacy-literal exclusion).
- `tests/Miller.Tests/Tools/TraceToolTests.cs` — 1 new end-to-end `trace mode=bridge` render test.

`DotnetWebBridgeProvider.ToClientCall(StructuralClientRequest)` was **NOT** changed — code reality already sets
`AttestedVerb: request.Verb` (line 581), exactly as the plan predicted, so no new field/flag was needed.

## The AttestedVerb seam (proof no new field needed + test-noise safety)

Verified against code, not memory:

1. **`TsClientCall.AttestedVerb`** (`RouteBridge.cs:45`) — nullable, default `null`; non-null ONLY when reduced
   from a 2.6.0 `http.client_request.v1` fact; null for every legacy url-literal call. (Confirmed via
   `inspect TsClientCall depth=full` — the record's `AttestedVerb` param doc states exactly this.)
2. **`DotnetWebBridgeProvider.ToClientCall(StructuralClientRequest)`** (`:569-582`) constructs the call with
   `new TsClientCall(literal, IsTest: false, request.FilePath, request.Line, AttestedVerb: request.Verb)` —
   the structural path always carries the attested verb. (Read `:560-593`.)
3. **`StructuralRouteFactAdapter.TryReadClientRequest`** (`StructuralRouteFactAdapter.cs:95-124`) rejects
   verbless facts (`IsNullOrWhiteSpace(verb) → false`) AND test facts (`IsTestFact(fact, symbolsById) → false`)
   BEFORE building a `StructuralClientRequest`. So (a) every structural call carries a verb; (b) no test-project
   HttpClient call ever becomes a `StructuralClientRequest`, so it never gets an `AttestedVerb`. (Read the body.)
4. **`IsTestFact`** (`StructuralRouteFactAdapter.cs:250-262`) → true when the containing symbol's `IsTest` flag
   is set OR `IsTestPath(path)` (`/__tests__/`, `.test.`, `.spec.`). So a test-project csharp HttpClient call is
   filtered on the structural path regardless of route.
5. **`ReduceStructuralClientCalls`** (`DotnetWebBridgeProvider.cs:495-531`) processes ALL `http.client_request.v1`
   facts with NO language gate — csharp facts flow through `TryReadClientRequest` → `ToClientCall`. The
   `IsRealClientCall` csharp exclusion was therefore the SOLE block on csharp structural calls. (Read the body +
   `BuildCandidates :47-106`.)
6. Only caller of `IsRealClientCall`: `RouteBridge.Resolve :172` (`trace IsRealClientCall` → 1 reference).
   Narrowing is fully localized.

**Conclusion:** legacy csharp literal ⟹ `AttestedVerb == null` ⟹ still excluded. Structural csharp call ⟹
`AttestedVerb != null` and `IsTest: false` (test facts already rejected upstream) ⟹ admitted. No new field, flag,
or reduction restructure needed — the discriminator already existed.

### How a test-project csharp HttpClient call stays excluded after the change

Two independent guards, both proven by tests:
- **Structural path:** a test-project HttpClient call's `http.client_request.v1` fact is rejected at
  `TryReadClientRequest` by `IsTestFact` (test symbol flag or test path) — it never becomes a
  `StructuralClientRequest`, never reaches `ToClientCall`, so it never carries an `AttestedVerb`. It cannot reach
  `IsRealClientCall` as a structural call at all. (`StructuralFacts_CsharpHttpClientRequestFromTestSymbol_IsExcluded`.)
- **Legacy literal path:** if the same test call also surfaced as a raw url literal, it has `AttestedVerb == null`,
  so the narrowed csharp exclusion still drops it (and `call.IsTest` drops it too when the container is test-flagged).
  (`StructuralFacts_LegacyCsharpUrlLiteral_StaysExcludedEvenWithMatchingEndpoint`, plus the pre-existing
  `Csharp_test_httpclient_literal_does_not_produce_a_hits_edge`.)

The narrowing to `AttestedVerb is null` therefore does NOT resurrect the test-HttpClient noise the exclusion blocks.

### F4 dedupe (no js/ts behavior change)

`DedupeClientCalls` (`DotnetWebBridgeProvider.cs:382-414`) only filters `literalClientCalls` using suppression-set
keys built from structural requests; the structural calls themselves are concatenated separately (`BuildCandidates :63`).
A csharp structural request has no legacy-literal twin admitted (the legacy exclusion still drops csharp literals), so
suppression sets simply gain csharp entries; js/ts literals and their dedupe are untouched. Asserted indirectly by the
full BridgeGraphBuilderTests (147) and RouteBridgeTests staying byte-green.

## Tests (TDD, rigid)

Written failing-first, then made to pass:

| Test | Invariant proven | RED → GREEN |
|---|---|---|
| `StructuralFacts_CsharpHttpClientRequest_HitsAttributeRouteEndpoint_High` | A non-test csharp `http.client_request.v1` (GET, url_kind=path, parameterized path) + `aspnet.attribute_route.v1` `GET /api/users/{id}` → verb-known **High** Hits edge through dotnet-web; `dotnet-web.clientRequests == 1`; source is the csharp client symbol. | RED (no edge, csharp excluded) → GREEN |
| `StructuralFacts_CsharpHttpClientRequestFromTestSymbol_IsExcluded` | **A test-project csharp HttpClient call is still excluded** — rejected at `TryReadClientRequest` (IsTestFact container-flag), no structural call, no edge, `clientRequests == 0`. | Green throughout (regression guard) |
| `StructuralFacts_LegacyCsharpUrlLiteral_StaysExcludedEvenWithMatchingEndpoint` | A legacy csharp url literal (`AttestedVerb == null`, verb-known GetAsync, route that WOULD match the same endpoint) → still no edge. Exclusion, not a route miss, suppresses it. | Green throughout (regression guard) |
| `Bridge_CsharpHttpClientRequest_SurfacesRouteEdgeToAspNetEndpoint` (TraceTool) | The csharp `HttpClient`→ASP.NET bridge surfaces end-to-end in `trace mode=bridge` compact output (`--route-->` to `GetById`, emitted==1). | RED (emitted 0) → GREEN |

**Load-bearing verification:** after correcting the fixtures I temporarily reverted ONLY the production guard
(blanket csharp exclusion restored) and re-ran — the 2 positive tests went RED (2 failed) and the 2 exclusion
tests stayed GREEN, proving the positives fail for the RIGHT reason (the exclusion) and the change is the sole
cause of the new edges. Guard then restored.

### TraceTool render coverage note

The TraceTool bridge render of a High Hits edge is provider/language-agnostic and already covered generically
(`Bridge_RendersChainWithScoreAndBand`, `Bridge_RouteStringTarget_*`). The one new TraceTool test adds value only
as an **end-to-end** check: it runs the REAL `BridgeGraphBuilder` over a csharp structural fact and asserts the
resulting edge renders — the only test proving csharp facts flow all the way from fact to rendered trace output.

## Judgment call — plan example route corrected in the fixture

The plan/acceptance example used `target_path = "/api/users/42"` and asserted it "folds to the endpoint
`/api/users/{}`". Code reality: `RouteNormalizer.ParamPattern` folds `{param}` / `${param}` / `:param` to `{}`,
but a **bare numeric literal segment (`42`) is NOT folded** — `/api/users/42` canonicalizes to `api/users/42`,
which never matches the parameterized endpoint `api/users/{}`. The plan's two statements were internally
inconsistent given the real canonicalizer.

Faithful resolution (plan-consistent OUTCOME preserved): the client `target_path` is `/api/users/{id}` — the shape
julie emits for an interpolated `HttpClient.GetAsync($"/api/users/{id}")`, which folds to `api/users/{}` and matches
the `GET /api/users/{id}` endpoint at verb-known High, exactly as the acceptance criterion intends. This is a
test-fixture correction within authority (not a seam contradiction, not a redesign), noted here. The seam itself
(AttestedVerb discriminator + `ToClientCall` setting it) is exactly as the plan described.

## Verification

| Command | Result |
|---|---|
| `dotnet test … --filter "FullyQualifiedName~BridgeGraphBuilderTests&Category!=Scale" -v minimal` | **147 passed, 0 failed** |
| `dotnet test … --filter "FullyQualifiedName~TraceToolTests&Category!=Scale" -v minimal` | **80 passed, 0 failed** |
| `scripts/test.sh` (fast suite, <30s ceiling) | **2689 passed, 0 failed** (16s wall) |
| `dotnet build Miller.slnx -c Release` | **0 Warning(s), 0 Error(s)** |

Scale suite NOT run (per scope). All commands run 2026-07-02 on branch `feat/backend-http-boundary`.

## Files changed

- `src/Miller.Core/Resolver/RouteBridge.cs` (one condition + XML doc)
- `tests/Miller.Tests/Graph/BridgeGraphBuilderTests.cs` (3 tests)
- `tests/Miller.Tests/Tools/TraceToolTests.cs` (1 test)
- `.razorback/sdd/task-5-report.md` (this report)

## Miller calls used

- `inspect IsRealClientCall depth=full` — the method + its (now-replaced) XML doc + confirmed sole caller `Resolve`.
- `inspect TsClientCall depth=full` — confirmed `AttestedVerb` field, default null, non-null only for structural facts.
- `inspect ToClientCall … depth=full` (disambiguated) + Read `:560-593` — confirmed `AttestedVerb: request.Verb`.
- `inspect TryReadClientRequest depth=full` — confirmed verbless + `IsTestFact` rejection.
- `inspect DedupeClientCalls depth=full` — confirmed F4 filters only legacy literals.
- `inspect IsTestFact depth=full` + Read — confirmed container-flag-or-test-path test detection.
- `inspect RouteNormalizer depth=full` — found the fold rule (root-caused the `42` fixture bug).
- `trace refs IsRealClientCall` — confirmed a single caller before changing.

## Concerns

None blocking. One noted judgment call (fixture route `42` → `{id}` for correct canonicalization). Live per-language
scale coverage of csharp `http.client_request.v1` on a real extract is Task 7's remit, not run here.
