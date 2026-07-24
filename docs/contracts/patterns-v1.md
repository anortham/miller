# Patterns v1 Contract

`patterns` reads generic `structural_facts` emitted by `julie-extractors`. Miller does not recognize parser
shapes or execute raw AST queries.

## Query Fan-Out

Free-text `operation=search query=...` considers every observed `pattern_id` before selecting at most 25 IDs
for fact retrieval. JSON reports:

- `pattern_ids_considered_count`: all observed IDs examined against the query.
- `pattern_ids_matched_count`: all IDs whose value contains the query, case-insensitively.
- `pattern_ids_returned_count`: matched IDs selected for retrieval.
- `pattern_ids_omitted_count`: matched IDs omitted by the 25-ID fan-out bound.
- `pattern_id_fanout_truncated`: true when any matched ID was omitted.
- `matched_pattern_ids`: the selected IDs, ordered by observed fact count descending and then ID ordinal.

Compact output reports the same values on one deterministic line:

```text
pattern_id_fanout: considered=N matched=N returned=N omitted=N truncated=true|false
```

The diagnostics are present for matching and non-matching query searches. A search result `limit` bounds fact
rendering after every selected pattern ID has been queried; it never changes the fan-out counts.

## Summary Grouping

- `language_pattern_capture`: language, pattern ID, and capture name.
- `file`: full normalized workspace-relative file path.
- `directory`: full normalized parent path, with `\` converted to `/` and repeated separators removed.
- `top_directory`: the first segment of the normalized parent path.

`directory` never silently collapses a deep parent to a top-level rollup. `top_directory` is the explicit
rollup.

## Exactness And Bounds

`PatternFactsReader` aggregates the full filtered population. List and summary counts are not computed from a
rendering-limited prefix. Search rendering remains bounded to 1–500 fact rows, and free-text pattern-ID
fan-out remains bounded to 25 returned IDs. Ordering is ordinal and deterministic.

The JSON schema remains additive version 1. Existing fields retain their meanings; query fan-out diagnostics
are additive.
