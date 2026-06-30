# Patterns JSON Contract v1

Status: active local contract for `miller patterns --json` and the MCP `patterns` tool with `format=json`.

The `patterns` surface reads generic `structural_facts` emitted by `julie-extractors`. Miller does not run raw
AST queries here. It lists, groups, filters, and renders known extractor facts by `pattern_id`.

Unknown future `pattern_id` values are valid observed facts. Consumers must not require a hard-coded Miller
catalog entry before accepting a row.

When `julie-extractors` emits a `pattern_catalog` table (all supported languages), list output overlays
`label`, `description`, `tags`, and `expected_metadata_keys` and sets `catalog` to `"known"`. Observed facts
still decide existence; unknown ids continue to list and search unchanged.

Common extractor-backed examples include:

- `aspnet.minimal_api.route.v1` for ASP.NET minimal API route mappings in C#.
- `htmx.attribute.v1` for htmx attributes in HTML and Razor.
- `alpine.directive.v1` for Alpine directives in HTML and Razor.

## Commands

```bash
miller patterns [list] [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--json]
miller patterns summary [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory] [--facet KEY] [--json]
miller patterns search (--pattern ID | --query TEXT) [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]
miller patterns export --jsonl [--workspace-id SELECTOR] [--workspace DIR]
```

MCP uses the same names with snake_case parameters: `operation`, `pattern_id`, `language`, `path`, `where`,
`query`, `group_by`, `facet`, `workspace_id`, `ensure_fresh`, `limit`, and `format=json`.

`list` and `summary` reject `query` with a usage error. `query` is only valid for `search`.

## List

```json
{
  "schema_version": 1,
  "operation": "list",
  "patterns": [
    {
      "pattern_id": "htmx.attribute.v1",
      "label": "htmx attribute",
      "count": 4,
      "catalog": "known",
      "languages": ["html", "razor"],
      "captures": ["attribute"],
      "description": "An htmx attribute usage",
      "tags": ["htmx", "html"],
      "expected_metadata_keys": ["name", "value"]
    }
  ],
  "next_actions": [
    {
      "tool": "patterns",
      "reason": "search observed structural facts for this pattern",
      "args": {"operation": "search", "pattern_id": "htmx.attribute.v1"}
    }
  ]
}
```

`label` defaults to `pattern_id` when no catalog row exists. `description`, `tags`, and
`expected_metadata_keys` are omitted when absent.

`next_actions` is additive and bounded. It is present for list output when Miller can derive useful follow-up
commands from observed `pattern_id` values.

## Summary

Default grouping is `(language, pattern_id, capture_name)`.

Optional `group_by`:

- `language_pattern_capture` (default) — same as omitting the flag.
- `file` — adds `path` per group.
- `directory` — adds `directory` (repo-relative, first two path segments).

Optional `facet` — when set, groups also include `facet_value` read from a top-level metadata key.

```json
{
  "schema_version": 1,
  "operation": "summary",
  "group_by": "file",
  "groups": [
    {
      "language": "razor",
      "pattern_id": "htmx.attribute.v1",
      "capture_name": "attribute",
      "path": "Views/Orders.cshtml",
      "count": 2
    }
  ]
}
```

`language`, exact path, safe prefix/suffix path, and `where` filters are pushed into SQL. Other path globs use
the same C# glob fallback as Miller read tools so `*` and `?` do not cross `/`.

## Search

Search accepts either an exact `pattern_id` or a free-text `query`. A free-text query maps to every observed
`pattern_id` containing the substring, then searches across those pattern ids (bounded per pattern id).

```json
{
  "schema_version": 1,
  "operation": "search",
  "pattern_id": "htmx.attribute.v1",
  "matches": [
    {
      "fact_id": "fact-hx-get",
      "pattern_id": "htmx.attribute.v1",
      "language": "razor",
      "path": "Views/Orders.cshtml",
      "capture_name": "attribute",
      "node_kind": "attribute",
      "containing_symbol_id": "sym-orders",
      "confidence": 1.0,
      "span": {
        "start_line": 1,
        "start_column": 9,
        "end_line": 1,
        "end_column": 25,
        "start_byte": 8,
        "end_byte": 24
      },
      "metadata": {
        "name": "hx-get",
        "value": "/orders"
      }
    }
  ]
}
```

`metadata` is present when `metadata_json` is valid JSON object data. If a row has malformed metadata, search
keeps the row for unfiltered output and writes `metadata_error`; metadata-filtered searches skip malformed rows.

### Filters

- `--where key=value` — exact match on one top-level metadata property. Repeat the flag or separate values with
  `;` in MCP (`where=name=hx-get;verb=GET`) to AND multiple filters. Metadata predicates run in SQL via guarded
  `json_extract` (strings compare as strings; numbers/booleans/objects compare as raw JSON text).
- `--path GLOB` — workspace-relative glob pushed into SQL when representable; semantics match other Miller read
  tools (`Views/**`, `**/*.cs`, exact paths).
- `--language LANG` — exact language filter.

No-match search JSON includes the same recovery context as compact output:

- `empty_reason`: `no_such_pattern_id`, `filtered_out`, `no_metadata_match`, `no_facts`, or `query_no_match`.
- `near_matches`: observed `pattern_id` values close to the query.
- `active_filters`: applied `language`, `path`, and `where` filters.
- `next_actions`: bounded recovery calls such as `operation=list`, `operation=summary`, or a concrete
  `pattern_id` search.

If a requested `pattern_id` exists but `language`, `path`, or `where` filters remove every row, the empty result
is still successful and output names the active filters so callers can loosen them deliberately.

## Export (CLI only)

`miller patterns export --jsonl` emits one JSON line per `structural_facts` row, ordered
`(path, start_byte, structural_fact_id)`. Advertised under `supported_export_formats` in `capabilities --json`.
Incompatible artifacts or a missing `structural_facts` table exit `3`.

## Exit Codes

| Code | Meaning |
|---:|---|
| `0` | Success. Empty result arrays are still successful. |
| `2` | Usage or workspace selector error, such as `search` without `--pattern` or `--query`, or `query` on `list`/`summary`. |
| `3` | Operational failure, such as no usable index or incompatible/missing `structural_facts`. |
| `1` | Unexpected failure converted to a clean CLI error line. |
