# Patterns JSON v2

`miller patterns --json` and `miller patterns export --jsonl` emit `schema_version: 2`.
Version 2 is the schema-5 consumer contract and requires a julie-extract 2.18.0 artifact.

The `list`, `summary`, and `search` envelopes retain their operation-specific fields, bounded coverage,
diagnostics, and next actions. Every envelope reports schema version 2 and a nullable `continuation` field.
Replay a non-null token without changing the request through MCP's `continuation` parameter or the public
process contract `miller patterns ... --continuation TOKEN`. A token is opaque and bound to the workspace,
request, and producer population. The export emits one row per
`structural_facts` row ordered by `(path, start_byte, structural_fact_id)` with:

- `structural_fact_id`, `path`, `language`, `pattern_id`, `capture_name`, and `node_kind`
- nullable `containing_symbol_id`, exact line, column, and byte spans, and `confidence`
- nullable producer-owned `metadata_json`

Miller does not infer parser facts. Marker consumers use only `pattern_id=code.marker.v1`.
