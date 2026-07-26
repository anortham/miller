# Search MCP Contract v1

**Status:** Active
**Scope:** MCP `search` responses. CLI and pure-core callers retain their exhaustive rendering contracts.

## Modes

`mode` accepts `auto`, `text`, `symbol`, `file`, `markers`, `content`, `source`,
`external`, `web`, or `all-text`. The compatibility aliases `marker`, `docs`,
`external_file`, and `all_text` map to their canonical modes. An empty mode means
`auto`; any other value returns the shared `invalid_request` diagnostic.
The CLI accepts the same values and treats any other `--mode` as a usage error
with exit code 2 and the Search usage banner.

Explicit `mode=source` is lexical-only. It never queries the semantic symbol or
chunk arms and does not emit semantic rows or a semantic-index consultation note.
Semantic retrieval remains available through the documented symbol retrieval
policy and remains subordinate to the process-wide off switch.

## Output bounds

Every MCP Search success payload is at most 12 KiB when serialized as UTF-8.
Search never truncates the serialized payload into invalid JSON and never invents
a second paging envelope. If complete result metadata cannot fit after row and
field bounds are applied, Search returns the shared
`output_metadata_too_large` diagnostic with guidance to narrow the query or
filters.

Content, source, external, web, source-region, and marker snippets are limited to
512 UTF-8 bytes. Truncation is Unicode code-point safe and preserves the final
ellipsis inside that limit. Paths and stable identities such as `source_id`,
`chunk_id`, `region_id`, and `symbol_id` are never shortened; an oversized
identity therefore causes the typed final-budget refusal.

JSON rows add `snippet_truncated: true` only when the snippet changed. Complete
snippets omit the field. Compact output uses an ellipsis and does not add a
separate marker.

The bound applies to:

- symbol, file, and mixed auto results;
- content and all text-corpus modes;
- source-region and marker searches;
- required semantic and hybrid symbol retrieval;
- automatic documentation and source rescue.

## Telemetry

`auto_source_rescue_attempted` is true only when Search actually issues the
source-corpus query. An earlier documentation rescue, an ineligible request, or
an exception before that query leaves the field false.

## Diagnostics

Search follows [`tool-diagnostics-v1.md`](tool-diagnostics-v1.md). Validation,
availability, and final-budget refusals use that shared diagnostic envelope.
Healthy empty results retain their route-specific evidence and recovery actions.
