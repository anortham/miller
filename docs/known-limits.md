# Known limits

- Family-store mode is default-on. Set `MILLER_INDEX_STORE=off` for the legacy standalone compatibility
  and rollback path. Until the Ph4/Ph5 A7 durable reader-pin
  protocol lands, producer GC must not run concurrently with live Miller family-store readers; an unpinned
  non-current generation may be reclaimed during a long read. The explicit standalone path is unaffected.
- Search and content family-store sidecars apply store file changes in place when the sidecar stamp and a complete
  revision delta match (A8, shipped in v1.19.4). A failed or ineligible delta still falls back to a full
  WriteStoreView with no log. There is no local reproducible cost gate, so A8 is implemented, not proven closed.
  Convergence is serialized by the family sidecar lease, so concurrent workspaces can leave a sidecar stale until
  a later converge succeeds; store readers refuse a stale sidecar rather than serving a legacy artifact.
- Local semantic/vector retrieval is owned by Miller and **on by default**. Set `MILLER_SEMANTIC=off`
  for the permanent zero-work path: no broker path derivation, model access, process, accelerator
  probe, vector read/write, or semantic telemetry. `MILLER_SEMANTIC=shadow` builds and measures
  without fusing; `MILLER_SEMANTIC=on` explicitly selects the default serving behavior.
  `MILLER_SEMANTIC_MODEL` selects the embedding encoder (the default `bge-small-en-v1.5-f32` at 134 MB
  and 384 dimensions, or the optional `qwen3-0.6b-f16` at 1.2 GB and 512 dimensions, marginally
  stronger at ~8x the build time), downloaded with `miller semantic prepare --model <id>`. A
  randomized-holdout canary that gates default-on uses `MILLER_SEMANTIC_CANARY=on` for the legacy v2
  profile or `decision` for the bounded v3 decision profile (requires semantic on/shadow), and is read
  with `miller telemetry canary --contract 2|3` / `--gate`; see the operator runbook
  [`findings/2026-07-21-p5-canary-runbook.md`](findings/2026-07-21-p5-canary-runbook.md). The boundary
  decision is
  [`adr/ADR-0003-semantic-retrieval-ownership.md`](adr/ADR-0003-semantic-retrieval-ownership.md) and
  the program design is
  [`plans/2026-07-19-miller-semantic-integration-design.md`](plans/2026-07-19-miller-semantic-integration-design.md).
  Concurrent Miller sessions using the same broker/protocol/model identity share one user-local broker
  and one loaded model. A user-global accelerator lease allows at most one broker identity to own
  acceleration; other identities use CPU, and accelerator resource exhaustion demotes the holder to
  CPU. Fleet-level semantics (cross-workspace ranking, embeddings-as-a-service) remain out of scope.
- Region search is explicit at query time and indexed by default: call
  `search --regions comment|doc_comment|string_literal`. Set `MILLER_REGION_INDEX=0` to opt out, or
  `MILLER_REGION_MAX_BYTES=<n>` to lower or raise the per-region byte cap for very large
  comment/string-literal corpora.
- Ambiguous targets may need a file path, a more specific symbol, or a symbol ID. The CLI reports
  ambiguity instead of guessing.
- Bridge trace (`trace mode=bridge`) is provider-scoped, not a general all-language/all-framework
  feature. Current providers are `dotnet-web` (ASP.NET controllers, TypeScript/JS client URL calls,
  AutoMapper, Entity Framework), `nextjs`/`nextjs-api` (route references to file routes, client
  requests to Next.js route handlers), `nuxt`/`nuxt-api` (NuxtLink route references to Nuxt file
  routes, client requests to Nuxt server routes), `vue` (Vue route references to route definitions),
  `react` (React route references to route definitions), `blazor` (navigation references to Razor page
  routes), and `backend-http` (client requests to
  Express/Fastify/FastAPI/Flask/Django/Spring/Go/gin/echo/Rails/NestJS/Laravel/Phoenix/axum/actix/
  Symfony/Ktor route templates). API handlers, server actions, middleware rewrites, redirects, and
  runtime route rules need extractor facts before bridge can claim them. The mode intentionally builds a full
  provider-scoped bridge graph through the session-scoped `SessionBridgeGraphLoader`, without hydrating the
  repository dependency graph. Normal `search`, `inspect`, graph-only `context`, `impact`, non-bridge `trace`,
  and workspace status/list stay on projection-specific read paths.
- Blazor `.razor` component dependencies resolve fully qualified tags exactly and simple tags only
  with namespace evidence from the reference fact, source component, inherited `_Imports.razor`
  `@using`/`@namespace` directives, or a bounded nearest-single-`.csproj` root/folder heuristic.
  Aliased, static, generic, conditional, imported, property-expanded, conflicting, ambiguous, unsafe,
  oversized, or malformed inputs fail closed. `.cshtml` and `_ViewImports.cshtml` component resolution
  remains deferred because the pinned julie-extract 2.31.3 emits directives but no component-reference
  facts for that surface. See
  [the namespace-resolution evidence](findings/2026-07-14-blazor-namespace-resolution.md).
- The main `miller` release binary publishes with Native AOT (no .NET SDK required to run it). The
  packaged dashboard helper stays self-contained/non-AOT because ASP.NET Razor Components do not yet
  support Native AOT.
- A rebuilt MCP server is picked up only after the MCP client restarts the Miller subprocess. Use
  `workspace status` and compare the `pid` in the header to confirm the restart actually loaded a new
  process.
