---
name: miller-bridge-trace
description: Use when tracing Miller bridge paths in supported providers: dotnet-web URL literals to ASP.NET signals, or nextjs route references to file routes.
user-invocable: true
arguments: "<client call, URL, route, endpoint, DTO, entity, or table>"
allowed-tools: mcp__miller__trace, mcp__miller__search, mcp__miller__inspect, mcp__miller__context, mcp__miller__workspace, mcp__miller__patterns
---

# Miller Bridge Trace

Miller bridge tracing is provider-scoped evidence, not generic semantic magic. Current providers are `dotnet-web`
(TypeScript/JavaScript/Vue URL literals to ASP.NET endpoints, DTOs, entities, and EF/table signals) and `nextjs`
(route references to Next.js file routes).

## Workflow

1. Find a concrete anchor:

```text
search(query="<URL, route, method, controller, DTO, entity, or table>")
search(query="<route text>", regions="string_literal")
```

For Next.js, bridge links require `nextjs.route_reference.v1` and `nextjs.file_route.v1` structural facts. If
Next route facts look missing, check patterns before assuming bridge support:

```text
patterns(operation="search", query="nextjs")
patterns(operation="search", query="route")
```

2. Inspect the best anchor when names are ambiguous:

```text
inspect(target="<symbol>", depth="full")
```

3. Run bridge trace:

```text
trace(target="<anchor>", mode="bridge")
```

If the anchor name is ambiguous but you know the file, pass scope instead of doing a JSON search for the id:

```text
trace(target="<symbol>", mode="bridge", scope="<file>")
```

Unsupported-provider or no-link bridge results include `Next:` / JSON `next_actions`. Follow those fallbacks
before calling the bridge absent: usually `patterns(query="route")`, `trace(mode="refs")`, or
`search(mode="source")`, depending on the output.

4. If the user asks for a specific route from one node to another, use path mode first, then bridge mode if the path crosses provider boundaries:

```text
trace(target="<from>", mode="path", to="<to>")
trace(target="<from>", mode="bridge")
```

## Reading Results

- `[verb-unknown]` means the URL matched but HTTP verb evidence is reduced.
- `[ambiguous]` means multiple plausible links exist.
- No bridge path can mean no provider is selected, missing extractor evidence, stale index, or a real absence of cross-language linkage.
- `nextjs` covers route references to file routes; API handlers, server actions, middleware rewrites, and redirects
  need extractor facts before bridge can claim them.

## Report

State:

- Provider assumption: `dotnet-web`, `nextjs`, or both
- Start node and end node if present
- Link confidence and any flags
- Missing evidence if the trace stops early
- Next concrete check, such as inspecting a controller action or DTO

Do not present bridge output as all-language or all-stack support. It is evidence for the selected provider only.
