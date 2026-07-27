# Miller trace JSON v1 contract

`miller trace <target> --json` and MCP `trace(format="json")` return structured trace data for the same three
trace modes as compact output:

- `mode=path`: shortest call-like path from `target` to `to`; `path_kind=dependency` /
  `--path-kind dependency` explicitly opts into all dependency-edge kinds.
- `mode=refs`: exact target-resolved references with unresolved name fallback kept separate.
- `mode=bridge`: provider-scoped cross-language bridge links.

Compact trace output remains the human reading surface. JSON is the stable machine surface for Eros and other
local integrations.

Overall outcome classification follows the
[Tool Diagnostics Contract v1](tool-diagnostics-v1.md). When present, top-level `diagnostic` and
`diagnostic_schema_version` are authoritative for empty/refusal/error outcome and MCP error-channel behavior.

## Top-level shape

```json
{
  "mode": "path | refs | bridge",
  "target": "GetUser",
  "to": null,
  "depth": 3,
  "limit": 20,
  "emitted": 2,
  "nodes_visited": 2,
  "note": null,
  "resolved_target": {},
  "resolved_to": null,
  "provider": null,
  "nodes": [],
  "links": [],
  "diagnostics": [],
  "next_actions": []
}
```

## Common fields

- `mode`: normalized mode requested by the caller.
- `target`: original target string.
- `to`: original destination string for `mode=path`; otherwise `null`.
- `depth`: effective hop bound, clamped to at least `1`.
- `limit`: effective rendered-result cap, clamped to at least `1`.
- `emitted`: rendered neighbour/link/node count after `limit`.
- `nodes_visited`: traversal work count before render truncation where available.
- `note`: human-readable empty/diagnostic note, or `null` on normal results.
- `diagnostics`: objects with `code` and `message` when a note should be machine-classified.
- `next_actions`: bounded recovery suggestions for empty/diagnostic results. Each action has `tool`, `reason`,
  and `args`; the field is always present and is empty when no recovery guidance applies.

`diagnostics[]` is retained as mode-specific algorithm evidence. It may coexist with top-level `diagnostic`;
the array explains the trace result while the top-level object classifies the call outcome. Consumers must
use top-level `diagnostic` for outcome and error handling.

Example `next_actions` row:

```json
{
  "tool": "trace",
  "reason": "check extracted identifier references from the source endpoint",
  "args": {"target": "SearchRoutePlanner", "mode": "refs"}
}
```

## Symbol graph mode

`mode=path` emits:

- `resolved_target`: source symbol object.
- `resolved_to`: destination symbol object.
- `path_kind`: normalized applied path filter (`call` or `dependency`); an invalid request echoes its normalized
  value alongside `invalid_path_kind`.
- `hops`: full shortest-path hop count, or `null` when no path is available.
- `nodes`: path symbols in order, truncated by `limit`.
- `links`: ordered evidence-bearing links between adjacent rendered path nodes. The v1 compatibility field
  `kind` remains `dependency_path`; additive `edge_kind` carries the extractor relationship kind. Each link also
  carries `source`, `target`, numeric `confidence`, `provenance`, and one-based `hop`.

The default `path_kind=call` admits `calls`, `call`, `invokes`, and `instantiates` edges. It excludes imports,
type-only uses, inheritance, and other broad dependencies so the result is not mislabeled as a call path.
`path_kind=dependency` admits every extracted graph edge while preserving its actual kind, confidence, and
provenance on each hop.

`path_kind=call` is independent of refs-mode `reference_kind=call`: the former filters graph edge kinds for a
path, while the latter filters exact and fallback reference rows.

An unknown path kind returns `invalid_path_kind`. A `no_path` diagnostic means Miller found no eligible extracted
graph path within the requested `depth`; it is not proof that the symbols are unrelated. `next_actions` points at
refs/source search and, for very shallow depth, at one bounded depth bump.

## References mode

`mode=refs` resolves the requested symbol to its exact symbol ID, then reads target-resolved evidence from the
extracted identifier-resolution, relationship, and pending-resolution data. Unresolved same-name rows are a
separate fallback tier. When multiple definitions share the target name, fallback is suppressed instead of
mixing homonyms into the exact result.

Mode-specific top-level fields:

- `reference_kind`: normalized filter value (`call`, `variable_ref`, `type_usage`, `member_access`, or `import`),
  or `null` when all kinds are included.
- `include_definition`: whether the resolved definition is repeated in `nodes`.
- `references`: compatibility array containing the rendered exact rows followed by fallback rows.
- `exact_references`: target-resolved rows only.
- `fallback_references`: unresolved same-name rows only.
- `reference_coverage`: observed, available, returned, and truncated counts for each tier plus
  `same_name_definition_count` and `fallback_status`.
- `continuation`: opaque artifact-bound token for the next exact/fallback page, or `null`.

Reference row fields:

- `target_symbol_id`: exact target ID, or `null` for unresolved fallback.
- `name`: resolved target name.
- `kind`: extractor identifier/reference kind.
- `canonical_kind`: normalized Miller reference kind.
- `file`: workspace-relative file path.
- `line`, `column`, `end_line`, `end_column`: occurrence position when available.
- `start_byte`, `end_byte`: source byte span when available.
- `containing_symbol_id`: enclosing symbol id when the extractor reported one, otherwise `null`.
- `containing_symbol_name`: resolved enclosing symbol name when available, otherwise `null`.
- `resolution_status`: `exact` or `fallback`.
- `source`: evidence provenance such as `identifier_direct`, `identifier_resolution`, `relationship`, or
  `pending_name`.
- `resolution_tier`: extractor resolution tier when available.
- `confidence`: numeric evidence confidence.
- `reference_site_id`: producer-owned canonical reference-site identity (julie-extract schema 5 and later).
  An exact site is the stable hash of `(file_id, start_byte, end_byte)`; a spanless site is the stable hash of
  `(file_id, row_specific_id)` and is prefixed `reference_site_spanless-`.
- `is_exact`: producer attestation that the site has a verified source span. Distinct from
  `resolution_status`, which reports whether Miller resolved the reference to the target symbol — a row may be
  `resolution_status=exact` with `is_exact=false` when the target is known but the span is not.
- `site_provenance`: `target_token` when the producer attested an exact token span, `spanless` otherwise.
  `is_exact=true` implies `target_token`; `is_exact=false` implies `spanless`.

`resolved_target` is still the resolved symbol object. `nodes` contains the target symbol only when
`include_definition` is true. `links` is empty because reference occurrences are rows, not graph edges.

Reference pages are capped at 16 KiB of UTF-8 JSON. The stateless continuation identity binds the workspace,
target symbol, artifact ID, artifact revision, reference-kind filter, definition flag, requested limit, and
independent exact/fallback offsets. See [Tool Continuation Contract v1](tool-continuation-v1.md).

## Bridge mode

`mode=bridge` emits:

- `provider`: bridge capability status with `active_providers`, `skipped_providers`, `notes`, and
  `evidence_counts`.
- `resolved_target`: bridge start node object with `id`, `kind`, `display`, `file`, `line`, and `role`.
- `nodes`: bridge nodes reached by the rendered links.
- `links`: scored bridge links.

Bridge link fields:

- `source`, `target`: bridge node ids.
- `source_display`, `target_display`: human display labels for the endpoints.
- `kind`: machine kind such as `hits`, `maps_to`, `stored_in`, `responds`, `consumes`, `navigates_to`, or
  `name_match`.
- `label`: compact-output label such as `route`, `CreateMap`, or `DbSet`.
- `score`: numeric confidence score.
- `confidence`: `high` or `medium`.
- `multi_signal`: whether multiple independent positive signals raised the score.
- `flags`: honesty flags such as `ambiguous` and `verb_unknown`.
- `evidence`: edge-level file/line evidence.
- `signals`: typed scoring signals with rule-specific payload and optional evidence.

Bridge node `kind` values include `file_route` for framework file-route and route-definition nodes.

Bridge is provider-scoped. Current packaged providers are:

- `dotnet-web`: TypeScript/JavaScript client URL calls plus fetch/axios client-request facts
  (`http.client_request.v1`), ASP.NET endpoints from annotations, minimal-API facts, and attribute-route
  `http_method` facts (`aspnet.attribute_route.v1`, joined on `effective_route_template` with `route_template`
  fallback), DTOs/entities, AutoMapper, and Entity Framework/Dapper table evidence. htmx facts arrive from HTML,
  JSX/TSX, and Vue templates alike. `controller_route` class-prefix facts and verb-less method-level `route`
  facts are evidence-only, never endpoints. When an attribute-route structural fact and an annotation-derived
  endpoint describe the same method and verb, the structural fact wins and one endpoint is emitted.
- `nextjs`: route references to Next.js file routes (navigation).
- `nextjs-api`: fetch/axios client requests (`http.client_request.v1`) to source-attested Next.js App Router
  route handlers (`nextjs.route_handler.v1`, julie-extract 2.6.0+), emitted as `hits` edges with the `route`
  label. With julie-extract 2.6.1+, both `export async function VERB` and
  `export const VERB = async () => ...` route-handler facts bind to the exported handler symbol.
- `nuxt`: NuxtLink route references to Nuxt file routes (navigation).
- `nuxt-api`: fetch/axios client requests to source-attested Nitro server routes (`nuxt.server_route.v1`,
  julie-extract 2.6.0+), emitted as `hits` edges with the `route` label; a whole-file handler fact without a
  containing symbol targets a synthesized endpoint node.
- `vue`: Vue route references to Vue route definitions.
- `react`: React route references to React route definitions.
- `backend-http`: fetch/axios/`requests`/`httpx`/`net/http`/`java.net.http`/`Net::HTTP`/`HttpClient`/Ktor/
  Guzzle/`Http`-facade/`Req`/reqwest client requests (`http.client_request.v1`), plus the julie-extract 2.16.0
  deferred client families — OkHttp/Retrofit/Spring WebClient/RestTemplate (Kotlin), Symfony HttpClient/cURL
  (PHP), Tesla/HTTPoison/Finch/`:httpc` (Elixir), and hyper/ureq (Rust). The provider is generic over the fact's
  `client` label, so new extractor-emitted clients join without Miller changes. The live parity gate requires 12
  artifact language values: `javascript`, `typescript`, `vue`, `python`, `go`, `java`, `ruby`, `csharp`, `kotlin`,
  `php`, `elixir`, and `rust`; facts from `.jsx` and `.tsx` files preserve the additional `jsx` and `tsx`
  provenance values. These requests join server route-template facts from 18 backend families — Express, Fastify,
  FastAPI, Flask, Django, Spring, Go net/http, gin, echo, Rails, NestJS, Laravel, Phoenix, axum, both actix
  provenances, Symfony, and Ktor. The exact route IDs are `express.route.v1`, `fastify.route.v1`,
  `fastapi.route.v1`, `flask.route.v1`, `django.url_pattern.v1`,
  `spring.request_mapping.v1`, `go.net_http.route.v1`, `gin.route.v1`, `echo.route.v1`, `rails.route.v1`,
  `nestjs.route.v1`, `laravel.route.v1`, `phoenix.route.v1`, `axum.route.v1`, `actix.attribute_route.v1`,
  `actix.scope_route.v1`, `symfony.route.v1`, and `ktor.route.v1`. Matches are emitted as `hits` edges with the
  `route` label. It is
  standalone rather than descriptor-driven and hosts three Miller-owned enrichment passes (cross-file
  mount-prefix composition, Rails resource-route expansion, and Rails `controller_action` symbol binding),
  described below.

Client-request edges are verb-aware. Client verbs are always known — `verb_source=attested`, or the fetch/axios
spec-default GET when no literal `method` option exists — so a verb match against a verb-known handler scores
High; a verb mismatch produces no edge; a suffix-less Nuxt handler (it answers every method, but its accepted
verb set is not source-attested) matches route-only as Medium with the `verb_unknown` flag; likewise a
verb-less backend handler (Express/Fastify all-method registrations, gin/echo `Any`, a method-less Spring
`@RequestMapping`, and every Django URLconf entry — Django is verb-agnostic at the URL level) matches
route-only as Medium `verb_unknown`, so `backend-http` shares these arms. Only
`url_kind=path` client requests are bridge candidates. Route matching is segment-specific (bracket `[id]` and
colon `:id` dynamic segments match one concrete segment); equally-specific ambiguous matches produce no edge
and are counted in diagnostics.

Neither the `*-api` providers nor the dotnet-web boundary consumption claims server actions, middleware
rewrites or redirects, runtime routing, conventional (non-attribute) ASP.NET routing, relative or absolute
client URLs (only `url_kind=path`), or Nuxt `$fetch`/`useFetch` composables (the extractor emits fetch/axios
facts only).

The `backend-http` provider grows through three Miller-owned enrichment passes, all unambiguous-or-nothing —
ambiguity poisons a candidate, it never degrades it to a lower band. (1) Cross-file mount-prefix composition
anchors each `express.router_mount.v1`, `fastapi.include_router.v1`, `flask.blueprint_registration.v1`, or
`django.url_include.v1` fact to route facts in exactly ONE anchor file (Django by the included module path, the
others by the mount target's trailing identifier), then APPENDS prefixed route variants
(`JoinRoute(mount_path, route_path)`); a mount that anchors to zero or multiple files composes nothing and is
counted in `unanchoredMounts`. The anchor is usually a different file (the router's own module), but it may be
the file that declares the mount — a same-file router whose prefix the extractor left unfolded; a same-file route
already carrying `effective_route_template` was folded upstream and is skipped, so composition never
double-prefixes it. Composition is strictly additive — the original un-prefixed route facts are always kept,
because a route fact carries no receiver identity proving it belongs to the mounted router rather than a direct
handler in the same file. (2) Rails resource expansion turns each `rails.resource_route.v1` fact
into the concrete verb-known REST handlers Rails doctrine implies (`resources` → 8 collection entries,
`resource` → 7 singular entries, filtered by `only`/`except`, prefixed by `scope_path`). (3) Rails controller
binding resolves a `rails.route.v1` `controller_action` (`users#show`) or an expanded action to the ONE
non-test controller method symbol that matches — a second collision poisons the binding, falling back to a
synthesized endpoint node. Rails semantics are Miller's job per the julie-extractors handoff; the other nine
families carry their own verb-known route templates.

A non-test csharp `http.client_request.v1` structural fact (a `HttpClient` call such as
`HttpClient.GetAsync("/api/users/42")`) is a real client call, so it feeds the `dotnet-web` boundary as
service-to-service client evidence — csharp is no longer blanket-excluded from client bridging. Test-project
HttpClient calls stay filtered by the fact's `is_test` guard.

`backend-http` does NOT claim: Django `url_pattern` facts in `route_syntax="regex"` form (no
`normalized_route_template`, so the blank route is honestly excluded, never synthesized from the regex);
`rails.mount.v1` Rack-app/engine internals (its mounted routes never reach the fact stream — counted as mount
evidence only, never composed or bridged); and any non-literal or dynamic route/mount template the extractor
could not resolve to a literal (upstream M2 silence — Miller bridges only the literal templates
julie-extractors attests).

`provider.evidence_counts` keys added by the boundary consumption: `dotnet-web.clientRequests`,
`dotnet-web.attributeRoutes`, `nextjs-api.clientRequests`, `nextjs-api.routeHandlers`, `nextjs-api.candidates`,
`nextjs-api.ambiguousMatches`, `nuxt-api.clientRequests`, `nuxt-api.serverRoutes`, `nuxt-api.candidates`, `nuxt-api.ambiguousMatches`,
`backend-http.clientRequests`, `backend-http.routeFacts`, `backend-http.mounts`, `backend-http.composedRoutes`,
`backend-http.unanchoredMounts`, `backend-http.expandedResourceRoutes`, `backend-http.candidates`, and
`backend-http.ambiguousMatches`.

Route-string diagnostics cover the new providers with provider-prefixed codes (`nextjs-api_*` / `nuxt-api_*` /
`backend-http_*` over the suffixes `route_no_file_match`, `route_no_reference_match`,
`route_ambiguous_file_match`, `route_no_bridge_link`, and `route_not_observed` — for example
`nextjs-api_route_no_file_match`), phrased with the nouns "client request", "route handler" (Next.js), "server
route" (Nuxt), and "client request"/"route" (backend-http). A provider-prefixed code is
emitted only from route facts that provider itself observed (observation-node provenance): a navigation code
never narrates a client request, and an API code never claims another framework's endpoint. When the route's
facts span frames — for example an unmatched client request whose only matching definition is an ASP.NET
endpoint — or when several providers claim the same route, the diagnostic falls back to the generic
un-prefixed codes (`route_no_backend_match`, `route_no_frontend_match`, `route_no_bridge_link`,
`route_not_observed`).

Empty bridge results are valid when a workspace is outside those providers or no bridge evidence exists.
`not_on_bridge` and `no_bridge_links` diagnostics include `next_actions` for ordinary refs, ordinary graph
neighbours, source text search, and structural `patterns` checks rather than implying unsupported stacks have
bridge coverage.
