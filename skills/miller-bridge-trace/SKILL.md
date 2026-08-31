---
name: miller-bridge-trace
description: Use when tracing Miller bridge paths in supported providers: dotnet-web URL literals and fetch/axios client requests to ASP.NET signals, client requests to Next.js route handlers or Nuxt server routes, Next.js/Nuxt route references to file routes, Vue/React references to route definitions, or backend-http client requests to Express/FastAPI/Flask/Django/Spring/Go/gin/echo/Rails/NestJS/Laravel/Phoenix/axum/actix/Symfony/Ktor route facts.
user-invocable: true
arguments: "<client call, URL, route, endpoint, DTO, entity, or table>"
allowed-tools: mcp__miller__trace, mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__workspace, mcp__miller__patterns
---

# Miller Bridge Trace

Miller bridge tracing is provider-scoped evidence, not generic semantic magic. Current providers are `dotnet-web`
(TypeScript/JavaScript/Vue URL literals and fetch/axios client requests to ASP.NET endpoints — annotation,
minimal-API, and attribute-route facts — plus DTOs, entities, and EF/table signals), `nextjs` (route references
to Next.js file routes), `nextjs-api` (fetch/axios client requests to Next.js App Router route handlers), `nuxt`
(NuxtLink route references to Nuxt file routes), `nuxt-api` (fetch/axios client requests to Nitro server routes),
`vue` (route references to Vue route definitions), `react` (route references to React route definitions), and
`backend-http` (fetch/axios/`requests`/`httpx`/`net/http`/`Net::HTTP`/Ktor/Guzzle/`Http`-facade/`Req`/reqwest
client requests — spanning python/go/java/ruby/vue and, since julie-extract 2.8.0, kotlin/php/elixir/rust beyond
js/ts; julie-extract 2.16.0 adds the deferred client families OkHttp/Retrofit/Spring WebClient/RestTemplate
(Kotlin), Symfony HttpClient/cURL (PHP), Tesla/HTTPoison/Finch/`:httpc` (Elixir), and hyper/ureq (Rust), all
generic over the fact's `client` label — to Express/Fastify/FastAPI/Flask/Django/Spring (Java **and** Kotlin)/Go
net/http/gin/echo/Rails/NestJS/Laravel/Phoenix/axum/actix/Symfony/Ktor route facts, with cross-file mount-prefix
composition and rails/laravel/phoenix resource-route expansion).

## Workspace targeting (required)

Every workspace-bound Miller MCP call must name its target with `workspace_id`. Miller does not infer the
workspace from the launch directory, environment variables, MCP Roots, or a previous call.

```text
workspace(operation="list")
workspace(operation="open", path="/absolute/project")
```

Use the ID those return on every `search`, `inspect`, `context`, `trace`, `impact`, `edit`, `patterns`,
`content`, and `tests` call, and on every scoped `workspace` operation (`status`, `health`, `onboarding`,
`refresh`, `full`, `leader`). The examples below write it as `workspace_id="<id>"`.

`workspace` `list`, `open`, `remove`, `prune`, and `dashboard` need no ID.
`content(operation="search", workspace_id="all")` stays the read-only cross-workspace text audit.
`current` and `primary` are CLI-only selectors; MCP refuses them.

## Workflow

1. Find a concrete anchor:

```text
search(workspace_id="<id>", query="<URL, route, method, controller, DTO, entity, or table>")
search(workspace_id="<id>", query="<route text>", regions="string_literal")
```

For Next.js/Nuxt and Vue/React navigation bridges, links require matching route-reference facts plus file-route
or route-definition structural facts. If target facts look missing, check patterns before assuming bridge support:

```text
patterns(workspace_id="<id>", operation="search", query="nextjs")
patterns(workspace_id="<id>", operation="search", query="nuxt")
patterns(workspace_id="<id>", operation="search", query="vue")
patterns(workspace_id="<id>", operation="search", query="react")
patterns(workspace_id="<id>", operation="search", query="route")
```

For the HTTP boundary bridges (`dotnet-web` client requests, `nextjs-api`, `nuxt-api`, `backend-http`), audit the
HTTP boundary fact families directly:

```text
patterns(workspace_id="<id>", operation="search", pattern_id="http.client_request.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="aspnet.attribute_route.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="nextjs.route_handler.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="nuxt.server_route.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="express.route.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="fastapi.route.v1")
patterns(workspace_id="<id>", operation="search", pattern_id="rails.resource_route.v1")
```

2. Inspect the best anchor when names are ambiguous:

```text
inspect(workspace_id="<id>", target="<symbol>", depth="full")
```

3. Run bridge trace:

```text
trace(workspace_id="<id>", target="<anchor>", mode="bridge")
```

If the anchor name is ambiguous but you know the file, pass scope instead of doing a JSON search for the id:

```text
trace(workspace_id="<id>", target="<symbol>", mode="bridge", scope="<file>")
```

Unsupported-provider or no-link bridge results include `Next:` / JSON `next_actions`. Follow those fallbacks
before calling the bridge absent: usually `patterns(query="route")`, `trace(mode="refs")`, or
`search(mode="source")`, depending on the output.

4. If the user asks for a specific route from one node to another, use path mode first, then bridge mode if the path crosses provider boundaries:

```text
trace(workspace_id="<id>", target="<from>", mode="path", to="<to>")
trace(workspace_id="<id>", target="<from>", mode="bridge")
```

## Reading Results

- `[verb-unknown]` means the URL matched but HTTP verb evidence is reduced.
- `[ambiguous]` means multiple plausible links exist.
- No bridge path can mean no provider is selected, missing extractor evidence, stale index, or a real absence of cross-language linkage.
- `nextjs` and `nuxt` cover route references to file routes; `vue` and `react` cover route references to route
  definitions. `nextjs-api` and `nuxt-api` claim source-attested Next.js App Router route handlers and Nuxt Nitro
  server routes from fetch/axios client requests: High on verb match (client verbs are always known — attested or
  the fetch/axios spec-default GET), Medium `[verb-unknown]` against suffix-less Nuxt handlers, no edge on verb
  mismatch or ambiguous route matches. Server actions, middleware rewrites/redirects, runtime route rules,
  conventional (non-attribute) ASP.NET routing, relative/absolute client URLs (only `url_kind=path` is bridged),
  and Nuxt `$fetch`/`useFetch` still need extractor facts before bridge can claim them.
- `backend-http` joins client requests to Express/Fastify/FastAPI/Flask/Django/Spring (Java + Kotlin)/Go
  net/http/gin/echo/Rails and (julie-extract 2.8.0) NestJS/Laravel/Phoenix/axum/actix route facts: High on verb
  match, Medium `[verb-unknown]` against a verb-less handler (`app.all`, gin/echo `Any`, a method-less
  `@RequestMapping`, actix `web::route()`, axum `any`, NestJS `@All`, every Django URLconf), no edge on verb
  mismatch or an equally-specific tie. Cross-file mounts (`app.use`/`include_router`/blueprint/`url_include`/
  axum `.nest`/actix `.configure`/phoenix `forward`) and rails/laravel/phoenix `resources`/`resource`/`apiResource`
  compose or expand additively, but mounts only on an unambiguous single-file anchor. It does NOT claim regex-form
  Django url patterns, `rails.mount` engine internals, target-less `laravel.route_prefix` closures, or
  non-literal/dynamic route templates (interpolated/`format!`/heredoc args stay silent upstream).

## Report

State:

- Provider assumption: `dotnet-web`, `nextjs`, `nextjs-api`, `nuxt`, `nuxt-api`, `vue`, `react`, `backend-http`, or a combination
- Start node and end node if present
- Link confidence and any flags
- Missing evidence if the trace stops early
- Next concrete check, such as inspecting a controller action or DTO

Do not present bridge output as all-language or all-stack support. It is evidence for the selected provider only.
