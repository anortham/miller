# Patterns JSON Contract v1

Status: active local contract for `miller patterns --json` and the MCP `patterns` tool with `format=json`.

The `patterns` surface reads generic `structural_facts` emitted by `julie-extractors`. Miller does not run raw
AST queries here. It lists, groups, filters, and renders known extractor facts by `pattern_id`.

Unknown future `pattern_id` values are valid observed facts. Consumers must not require a hard-coded Miller
catalog entry before accepting a row.

## Commands

```bash
miller patterns [list] [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--json]
miller patterns summary [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--json]
miller patterns search --pattern ID [--workspace-id SELECTOR] [--workspace DIR] [--language LANG] [--path GLOB] [--where key=value] [--limit N] [--json]
```

MCP uses the same names with snake_case parameters: `operation`, `pattern_id`, `language`, `path`, `where`,
`workspace_id`, `ensure_fresh`, `limit`, and `format=json`.

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
  ]
}
```

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

`--where key=value` is an exact string comparison against one top-level metadata property. The first slice supports
one `--where` filter.

## Exit Codes

| Code | Meaning |
|---:|---|
| `0` | Success. Empty result arrays are still successful. |
| `2` | Usage or workspace selector error, such as `search` without `--pattern`. |
| `3` | Operational failure, such as no usable index or incompatible/missing `structural_facts`. |
| `1` | Unexpected failure converted to a clean CLI error line. |
