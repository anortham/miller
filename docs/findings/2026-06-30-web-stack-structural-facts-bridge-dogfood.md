# Web Stack Structural Facts Bridge Dogfood Evidence

Date: 2026-06-30

## Scope

This records the final evidence for using parser-backed structural facts to improve Miller `trace mode=bridge` for the two daily web stacks in scope:

- htmx route attributes to ASP.NET minimal API routes.
- Vue route references to ASP.NET minimal API routes.

## Commits

Miller worktree: `/Users/murphy/source/miller/.worktrees/web-stack-structural-facts-bridge`

- `ba2a99bc1e18e1a6382ccf9cc8aa0c29f3309b2d` - latest Miller implementation/bookkeeping commit before this evidence note.
- Key implementation commits:
  - `f7f88e364242c6ff6ddeed61d80c634a7f2a1964` - structural fact bridge input seam.
  - `88c8d02a3a16a570954e25daf172012f5506ded8` - ASP.NET minimal API and htmx bridge reducer.
  - `d380ef1229de35810df8030093a3e410314f2df6` - Vue route structural fact bridge.
  - `6783e54b098f5ea608f829588b150de435575be0` - trace bridge recovery guidance.

julie-extractors worktree: `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge`

- `330877da48afeb33106d12b3317748d533570267` - latest extractor commit.
- Key implementation commits:
  - `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` - Vue route-reference structural facts.
  - `330877da48afeb33106d12b3317748d533570267` - capability matrix claim for `vue.route_reference.v1`.

## Verification Ledger

| Scope | Command | Commit | Result | Timestamp |
| --- | --- | --- | --- | --- |
| branch-gate / Miller fast suite | `scripts/test.sh` | `ba2a99bc1e18e1a6382ccf9cc8aa0c29f3309b2d` + Task 6 evidence working tree | Passed: 2513 passed, 0 failed, 0 skipped; wall time 13s | 2026-06-30T15:43:39Z |
| branch-gate / extractor format | `cargo fmt --check` | `330877da48afeb33106d12b3317748d533570267` | Passed, exit 0 | 2026-06-30T15:42:09Z |
| branch-gate / Vue route fact narrow test | `cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture` | `330877da48afeb33106d12b3317748d533570267` | Passed: 1 passed, 0 failed | 2026-06-30T15:42:09Z |
| branch-gate / extractor default suite | `cargo xtask test default` | `330877da48afeb33106d12b3317748d533570267` | Passed: julie-extractors, artifact, CLI, contract, writer, and doctest suites passed | 2026-06-30T15:42:09Z |

## Hard-Gate Assertions

Miller fast suite includes these task-specific assertions:

- htmx `/todos` to ASP.NET `MapGet("/todos", ...)`:
  - `StructuralFacts_htmx_get_hits_minimal_api_mapget_with_client_and_endpoint_evidence`
  - Proves a `htmx.attribute.v1` `hx-get` fact and an `aspnet.minimal_api.route.v1` `GET` fact produce a high-confidence `Hits` edge with both client and endpoint evidence.
  - Proves provider evidence counts: `dotnet-web.structuralFacts=2`, `dotnet-web.aspnetMinimalRoutes=1`, `dotnet-web.htmxCalls=1`.
- htmx verb mismatch:
  - `StructuralFacts_htmx_post_does_not_match_minimal_api_mapget_for_same_route`
  - Proves `hx-post="/todos"` does not match a `MapGet("/todos")` endpoint.
- htmx non-route attributes:
  - `StructuralFacts_htmx_non_route_attributes_do_not_produce_client_calls`
  - Proves `hx-target` does not become a route client call.
- Vue `RouterLink` / `router-link` `/todos` to ASP.NET `MapGet("/todos", ...)`:
  - `StructuralFacts_vue_router_link_hits_minimal_api_mapget_with_client_and_endpoint_evidence`
  - Proves a `vue.route_reference.v1` fact with `target_path="/todos"` and `verb="GET"` produces a high-confidence `Hits` edge with both client and endpoint evidence.
  - Proves provider evidence counts: `dotnet-web.structuralFacts=2`, `dotnet-web.aspnetMinimalRoutes=1`, `dotnet-web.vueCalls=1`.
- Vue bound literal route:
  - `StructuralFacts_vue_bound_to_literal_hits_minimal_api_mapget`
  - Proves a bound literal `:to="'/todos'"` route fact bridges to `MapGet("/todos")`.
- Vue missing or nonliteral route metadata:
  - `StructuralFacts_vue_route_facts_without_target_path_do_not_produce_client_calls`
  - Proves Vue facts without `target_path` or with nonliteral expressions do not become client calls.
- Repository loader bridge graph:
  - `Load_PopulatesBridgeGraph_FromVueRouteStructuralFacts`
  - Proves SQLite `structural_facts` rows flow through `SqliteBridgeReader`, `RepositoryIndexLoader`, and `BridgeGraphBuilder` into a high-confidence bridge edge.
- Trace fallback guidance:
  - `Bridge_NotOnBridge_WithRouteFactEvidence_OffersPatternAudits`
  - `Bridge_NotOnBridge_WithRouteFactEvidence_JsonCarriesPatternAudits`
  - `Bridge_CannotStart_WithRouteFactEvidence_JsonCarriesPatternAudits`
  - `Bridge_NoLinksWithinDepth_WithHtmxRouteFactEvidence_OffersPatternAudits`
  - Prove compact and JSON bridge fallbacks include `patterns` recovery actions when route structural facts are relevant.

Extractor default suite includes:

- `vue_emits_route_reference_facts`
  - Proves `vue.route_reference.v1` facts are emitted for `RouterLink`, `router-link`, bound literal `:to`, and literal router navigation, while non-route directives do not emit route facts.

## Result

The bridge now consumes route-ready structural facts from `julie-extractors` for ASP.NET minimal APIs, htmx, and Vue without adding a new MCP tool and without parsing htmx/Vue source text inside Miller.
