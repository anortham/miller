# Miller trace JSON v1 contract

`miller trace <target> --json` and MCP `trace(format="json")` return structured trace data for the same four
trace modes as compact output:

- `mode=auto`: bounded caller/callee neighbourhood around one symbol.
- `mode=path`: shortest dependency path from `target` to `to`.
- `mode=refs`: name-based identifier references for one resolved symbol.
- `mode=bridge`: provider-scoped cross-language bridge links.

Compact trace output remains the human reading surface. JSON is the stable machine surface for Eros and other
local integrations.

## Top-level shape

```json
{
  "mode": "auto | path | refs | bridge",
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

Example `next_actions` row:

```json
{
  "tool": "trace",
  "reason": "check extracted identifier references from the source endpoint",
  "args": {"target": "SearchRoutePlanner", "mode": "refs"}
}
```

## Symbol graph modes

`mode=auto` emits:

- `resolved_target`: symbol object with `id`, `symbol_id`, `name`, `kind`, `file`, `line`, `role`, and `hop`.
- `nodes`: the target symbol followed by reached neighbour symbols.
- `links`: synthetic neighbour links from the target id to each reached id. These links describe trace reachability,
  not a precise caller-vs-callee direction; use each node's `hop` for distance.

`mode=path` emits:

- `resolved_target`: source symbol object.
- `resolved_to`: destination symbol object.
- `hops`: full shortest-path hop count, or `null` when no path is available.
- `nodes`: path symbols in order, truncated by `limit`.
- `links`: ordered `dependency_path` links between adjacent rendered path nodes.

A `no_path` diagnostic means Miller found no extracted dependency-graph path within the requested `depth`; it is
not proof that the symbols are unrelated. `next_actions` points at refs/source search and, for very shallow depth,
at one bounded depth bump.

## References mode

`mode=refs` emits name-based identifier references from the extracted `identifiers` table. The result is honest
about confidence: extractor rows currently match by identifier name, not by resolved target symbol id, so homonyms
can appear.

Mode-specific top-level fields:

- `reference_kind`: normalized filter value (`call`, `variable_ref`, `type_usage`, `member_access`, or `import`),
  or `null` when all kinds are included.
- `include_definition`: whether the resolved definition is repeated in `nodes`.
- `references`: rendered reference rows after filtering and `limit`.

Reference row fields:

- `name`: referenced identifier name.
- `kind`: identifier/reference kind.
- `file`: workspace-relative file path.
- `line`: 1-based occurrence line.
- `containing_symbol_id`: enclosing symbol id when the extractor reported one, otherwise `null`.
- `confidence`: currently `name_based`.

`resolved_target` is still the resolved symbol object. `nodes` contains the target symbol only when
`include_definition` is true. `links` is empty because name-based reference occurrences are rows, not graph edges.

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

Bridge node `kind` values include `file_route` for framework file-route nodes.

Bridge is provider-scoped. Current packaged providers are:

- `dotnet-web`: TypeScript/JavaScript client URL calls, ASP.NET endpoints, DTOs/entities, AutoMapper, and Entity
  Framework/Dapper table evidence.
- `nextjs`: route references to Next.js file routes. It does not claim API route handlers, server actions,
  middleware rewrites, redirects, or runtime routing unless extractor facts exist for them.
- `nuxt`: NuxtLink route references to Nuxt file routes. It does not claim Nitro/server API routes, route rules,
  middleware redirects, or runtime routing unless extractor facts exist for them.

Empty bridge results are valid when a workspace is outside those providers or no bridge evidence exists.
`not_on_bridge` and `no_bridge_links` diagnostics include `next_actions` for ordinary refs, ordinary graph
neighbours, source text search, and structural `patterns` checks rather than implying unsupported stacks have
bridge coverage.
