# Patterns Contract v1

Status: active local contract for `miller patterns` and the MCP `patterns` tool in compact and JSON formats.

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
miller patterns summary [--workspace-id SELECTOR] [--workspace DIR] [--pattern ID] [--language LANG] [--path GLOB] [--where key=value] [--group-by file|directory|top_directory] [--facet KEY] [--json]
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

List JSON also reports `patterns_total_count`, `patterns_returned_count`, `patterns_omitted_count`, and
`patterns_truncated`. When filters are present, JSON includes `active_filters` and compact output names every
active `language`, `path`, and `where` filter even when the result is empty. CLI output is exhaustive. MCP
output retains the largest deterministic prefix that fits the 12 KiB budget and reports exact omissions. The
MCP projection orders pattern IDs by observed fact count
descending and then ID ordinal so the most useful patterns survive truncation; exhaustive CLI output remains
ID-ordinal. Bounded `next_actions` are derived from the returned prefix and never name a hidden pattern ID.
Observed counts and optional catalog overlays are read through one transaction, so a rebuild promote cannot
combine facts from one artifact snapshot with labels from another.

`next_actions` is additive and bounded. It is present for list output when Miller can derive useful follow-up
commands from observed `pattern_id` values.

## Summary

Default grouping is `(language, pattern_id, capture_name)`.

Optional `group_by`:

- `language_pattern_capture` (default) — same as omitting the flag.
- `file` — adds `path` per group.
- `directory` — adds the full normalized repo-relative parent path.
- `top_directory` — adds the first segment of the normalized parent path.

`directory` never silently collapses a deep parent to a top-level rollup. Use `top_directory` when that
rollup is intentional. Both modes normalize `\` to `/` and collapse repeated separators.

Optional `facet` — when set, groups also include `facet_value` read from a top-level metadata key. Facet keys
accept letters, digits, underscore, and hyphen; unsupported keys are `refusal/invalid_request`.

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

Summary JSON reports the same coverage shape as `groups_total_count`, `groups_returned_count`,
`groups_omitted_count`, and `groups_truncated`. Filtered summary output includes the same `active_filters`
object or compact filter line as list and search, including filtered-empty results. CLI output is exhaustive.
MCP output retains the largest deterministic group prefix that fits the 12 KiB budget and reports exact
omissions. MCP groups are ordered by
count descending and then the documented ordinal group keys so the largest groups survive truncation; exhaustive
CLI output remains key-ordinal. Refine `pattern_id`, `language`, `path`, `where`, `group_by`, or `facet` when
the summary is truncated.

`language`, exact path, safe prefix/suffix path, and `where` filters are pushed into SQL. Other path globs use
the same C# glob fallback as Miller read tools so `*` and `?` do not cross `/`. Exact coverage requires that
fallback to scan the complete filtered fact population regardless of the requested row limit; it performs one
lightweight identity/path/position pass, then materializes only retained match rows in the same read transaction.

## Search

Search accepts either an exact `pattern_id` or a free-text `query`. A free-text query examines every observed
`pattern_id`, selects matching IDs by observed fact count descending and ID ordinal, then searches at most 25
selected IDs. Observed IDs, filtered fan-out ranking, retained IDs, total matches, and retained match rows share
one read transaction, so an artifact update cannot split one response across snapshots. JSON reports the
complete fan-out decision:

- `pattern_ids_considered_count`: every observed ID examined.
- `pattern_ids_matched_count`: every case-insensitive substring match.
- `pattern_ids_returned_count`: matched IDs selected for fact retrieval.
- `pattern_ids_omitted_count`: matched IDs omitted by the 25-ID bound.
- `pattern_id_fanout_truncated`: whether any matched ID was omitted.
- `matched_pattern_ids`: the selected IDs in deterministic retrieval order.

The fact `limit` is applied globally after every selected pattern ID contributes candidates. It does not alter
the fan-out counts. When more than 25 IDs match and `language`, `path`, or `where` filters are active, IDs with
facts under those filters rank ahead of IDs whose facts would all be filtered out; observed total count and ID
ordinal remain the deterministic tie-breakers. This prevents the 25-ID bound from hiding the only actionable
filtered match without adding a ranking scan when every matched ID will be selected anyway.

```json
{
  "schema_version": 1,
  "operation": "search",
  "pattern_id": "htmx.attribute.v1",
  "matches_total_count": 1,
  "matches_returned_count": 1,
  "matches_omitted_count": 0,
  "matches_truncated": false,
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

The four `matches_*` coverage fields are always present. `matches_total_count` is the exact filtered fact
population before the result limit and MCP byte budget; for query search it covers the selected
`matched_pattern_ids`, while omitted pattern IDs remain visible through the separate fan-out fields. CLI JSON
returns every row admitted by `--limit`. MCP JSON and compact output retain the largest deterministic row
prefix that fits the 12 KiB agent-output budget. Any result-limit or byte-budget omission sets
`matches_truncated=true` and reports the exact omitted count.

List and summary aggregate the complete filtered population rather than a search/rendering prefix. Search
limits remain 1–500 rows and query fan-out remains at most 25 selected pattern IDs. Ordering is ordinal and
deterministic.

### Filters

- `--where key=value` — exact match on one top-level metadata property. Repeat the flag or separate values with
  `;` in MCP (`where=name=hx-get;verb=GET`) to AND at most 16 filters. Metadata predicates run in SQL via
  guarded `json_extract` (strings compare as strings; numbers/booleans/objects compare as raw JSON text).
- `--path GLOB` — workspace-relative glob pushed into SQL when representable; semantics match other Miller read
  tools (`Views/**`, `**/*.cs`, exact paths).
- `--language LANG` — exact language filter.

List and summary accept `where` without `pattern_id` so agents can discover every pattern carrying a metadata
value. Those operations intentionally scan the complete structural-fact population, like their unfiltered
forms; add `pattern_id`, `language`, or `path` when a narrower audit is sufficient. The SQL predicate evaluates
each JSON type and value at most once per row.

Agent inputs are bounded by JSON-encoded byte contribution before execution: `pattern_id` 512, `query` 1,000,
`language` 128, `path` and `where` 2,048 each, and `facet` 256. Oversized values are
`refusal/invalid_request`, not internal failures.

Every MCP response reserves room for its diagnostic envelope and is checked again after diagnostic attachment.
Diagnostic JSON uses the same relaxed encoder as pattern JSON, so safe Unicode and HTML characters are not
expanded after the initial byte-budget decision. If fixed response metadata cannot fit, Miller returns a typed
`output_metadata_too_large` refusal instead of an internal failure.

No-match search JSON includes the same recovery context as compact output:

- `empty_reason`: `no_such_pattern_id`, `filtered_out`, `no_facts`, or `query_no_match`.
- `near_matches`: observed `pattern_id` values close to the query.
- `active_filters`: applied `language`, `path`, and `where` filters.
- `next_actions`: bounded recovery calls such as `operation=list`, `operation=summary`, or a concrete
  `pattern_id` search.

If a requested `pattern_id` exists but `language`, `path`, or `where` filters remove every row, the empty result
is still successful and output names the active filters so callers can loosen them deliberately.
If the requested ID is missing but has near matches, compact and JSON both provide copyable concrete search and
summary actions for the closest observed ID.
When `language` is active, query-no-match `near_matches` are limited to pattern IDs observed in that language;
the exact fan-out considered count remains language-agnostic.

Overall outcome classification follows the
[Tool Diagnostics Contract v1](tool-diagnostics-v1.md). Empty results retain `empty_reason`, `near_matches`,
`active_filters`, and `next_actions` and also add top-level `diagnostic`. Invalid requests use
`refusal/invalid_request`; incompatible, corrupt, unavailable, and unexpected failures use the diagnostic
envelope and the MCP error channel.

## Compact Output

Compact is the default CLI and MCP format. Matching and non-matching query searches report the exact fan-out
decision on one line:

```text
pattern_id_fanout: considered=N matched=N returned=N omitted=N truncated=true|false
```

When an MCP byte budget or search result limit omits rows, compact output reports the corresponding collection:

```text
patterns: total=N returned=N omitted=N truncated=true
groups: total=N returned=N omitted=N truncated=true
matches: total=N returned=N omitted=N truncated=true
```

For list, the following `next:` line asks the caller to narrow `pattern_id`, `language`, or `path`. For summary,
it also names `where` and grouping. Non-truncated output omits these coverage and narrowing lines. CLI list and
summary remain exhaustive; CLI search coverage still reports omissions caused by `--limit`.

Compact search output preserves every active `where` filter, including multi-filter empty states, and renders
all filtered metadata keys before adding general metadata up to the normal four-key minimum. Empty compact
summary output retains its requested `group_by` and `facet`. Truncation recovery actions preserve the active
`language`, `path`, and combined `where` population while reducing only `limit`; JSON writes `limit` as a number
so the returned `args` object can be submitted directly to MCP.

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
