# Patterns JSON Contract v1

Status: active local contract for `miller patterns --json` and the MCP `patterns` tool with `format=json`.

The `patterns` surface reads generic `structural_facts` emitted by `julie-extractors`. Miller does not run raw
AST queries here. It lists, groups, filters, and renders known extractor facts by `pattern_id`.

Unknown future `pattern_id` values are valid observed facts. Consumers must not require a hard-coded Miller
catalog entry before accepting a row.

Common extractor-backed examples include:

- `aspnet.minimal_api.route.v1` for ASP.NET minimal API route mappings in C#.
- `htmx.attribute.v1` for htmx attributes in HTML and Razor.
- `alpine.directive.v1` for Alpine directives in HTML and Razor.

## Commands

```bash
miller patterns [list] [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--json]
miller patterns summary [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--json]
miller patterns search (--pattern ID | --query TEXT) [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]
```

MCP uses the same names with snake_case parameters: `operation`, `pattern_id`, `language`, `path`, `where`,
`query`, `workspace_id`, `ensure_fresh`, `limit`, and `format=json`.

## List

```json
{
  "schema_version": 1,
  "operation": "list",
  "patterns": [
    {
      "pattern_id": "htmx.attribute.v1",
      "label": "htmx.attribute.v1",
      "count": 4,
      "catalog": "observed",
      "languages": ["html", "razor"],
      "captures": ["attribute"]
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

`next_actions` is additive and bounded. It is present for list output when Miller can derive useful follow-up
commands from observed `pattern_id` values.

## Summary

```json
{
  "schema_version": 1,
  "operation": "summary",
  "groups": [
    {
      "language": "razor",
      "pattern_id": "htmx.attribute.v1",
      "capture_name": "attribute",
      "count": 2
    }
  ]
}
```

## Search

Search accepts either an exact `pattern_id` or a free-text `query`. A free-text query maps to every observed
`pattern_id` containing the substring, then searches across those pattern ids.

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
        "framework": "htmx",
        "verb": "GET",
        "attribute_name": "hx-get",
        "attribute_value": "/orders",
        "target_path": "/orders"
      }
    }
  ]
}
```

`metadata` is present when `metadata_json` is valid JSON object data. If a row has malformed metadata, search
keeps the row for unfiltered output and writes `metadata_error`; metadata-filtered searches skip malformed rows.

`--where key=value` is an exact string comparison against one top-level metadata property. It can be used with
`--pattern` or `--query`. The first slice supports one `--where` filter.

No-match search output may include:

- `near_matches`: observed `pattern_id` values close to the query.
- `next_actions`: bounded recovery calls, such as `operation=list`, `operation=summary`, or a concrete
  `pattern_id` search.

If a requested `pattern_id` exists but `language`, `path`, or `where` filters remove every row, the empty result
is still successful and the compact output names the active filters so callers can loosen them deliberately.

## Exit Codes

| Code | Meaning |
|---:|---|
| `0` | Success. Empty result arrays are still successful. |
| `2` | Usage or workspace selector error, such as `search` without `--pattern` or `--query`. |
| `3` | Operational failure, such as no usable index or incompatible/missing `structural_facts`. |
| `1` | Unexpected failure converted to a clean CLI error line. |
