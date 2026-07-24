# Content MCP Contract v2

Status: active

This contract covers the agent-facing `content` MCP tool. The process-facing
`miller content export` JSONL feed remains governed by
[`content-corpus-v1.md`](content-corpus-v1.md) and
[`cli-eros-v1.md`](cli-eros-v1.md).

## Operations

The accepted MCP operations are:

- `import`
- `add_markdown`
- `search`
- `read`
- `shape`
- `list`
- `remove`

`export` is not an MCP operation. Bulk export is CLI-only. There is no
deprecated alias or compatibility response for the removed operation.

## List

Bare `list` returns an inventory for `external_file` and `web`. An explicit
`content_kind` returns one kind. `limit` is a per-kind row limit, defaults to
6, and is capped at 20. Exact totals are counted independently of the returned
page.

JSON is an object with this deterministic field order:

1. `schema_version` (`2`)
2. `per_kind_limit`
3. `total_count`
4. `returned_count`
5. `omitted_count`
6. `kinds`

Each kind has `content_kind`, exact `total_count`, `returned_count`,
`omitted_count`, and `sources`. Source order is `display_path`, then
`source_id`. Bare list always reports both imported kinds, including zero
counts.

Compact output is at most 16,000 characters. JSON is at most 48,000
characters. A source display path and URL are each capped at a 240-byte JSON
escaped contribution, so quotes, backslashes, and control characters cannot
inflate the envelope past its contract. Bare list returns at most 40 sources:
20 per imported kind.

The CLI `miller content list` contract is unchanged: compact output remains the
flat source list and `--json` remains the v1 JSON array used by Eros. Its
default remains `external_file`; explicit `--kind all` returns
`external_file` followed by `web` in that same flat shape.

## Read

`read` returns at most 200 source lines. Rendering is bounded independently
from the import cap, so a default-cap source containing one very large logical
line cannot create a correspondingly large tool result.

- Compact line text is capped at 160 UTF-16 characters.
- JSON line text is capped at a 160-byte JSON-escaped UTF-8 contribution.
- Display paths use the corresponding 240-unit cap.
- Compact output reports `read truncated_lines=N line_limit=160` when it
  truncates any line.
- JSON reports top-level `truncated_line_count`; every line object includes a
  `truncated` boolean.

With the 200-line window cap these rules keep compact and JSON read output
within 48,000 characters, including escape-heavy JSON. Truncation returns a
successful bounded read rather than a diagnostic.

## Shape

`shape` requires `source_id` and accepts the same unique display-path alias and
cross-workspace routing as `read`. It returns:

- source identity, kind, byte count, and exact line count;
- the first five and last five lines;
- a deterministic text-derived severity summary.

Each compact rendered line is capped at 240 characters. Each JSON line is
capped at a 240-byte escaped contribution. Compact and JSON output are both
capped at 8,000 characters.

Severity is not parser truth. Each source line contributes to exactly one
bucket, using the first matching bucket in this order:

1. `fatal`: `fatal`, `panic`
2. `error`: `error`, `exception`, `failed`, `failure`
3. `warning`: `warn`, `warning`
4. `info`: `info`, `notice`
5. `debug`: `debug`, `trace`
6. `other`

Matches are case-insensitive whole words. JSON labels this evidence
`severity_basis: "text_derived"` and uses `schema_version: 2`.

## Import memory

The default import ceiling remains 25 MiB. A caller must pass a larger
`max_bytes` value to import a larger file intentionally. That raised-cap path
decodes, hashes, chunks, tokenizes, and inserts incrementally; it does not
allocate a complete-file byte array or a complete normalized string.
The raised-cap path rejects a logical line over 65,536 UTF-16 characters and
never persists a raw chunk over 1,048,576 UTF-16 characters. Size-triggered
chunks retain as much of the normal overlap as fits. The transaction rolls back
on an overlong line, invalid UTF-8, or size drift. Invalid-UTF-8 and
size-drift regressions both cross the 16 KiB decoder buffer and 160-line chunk
threshold before failing, proving rollback after chunk insertion has begun.

## Failures and compatibility

Typed diagnostic codes remain available in JSON. `shape` uses
`missing_source_id`, `ambiguous_source`, `source_not_found`,
`content_corpus_missing`, or `shape_error`.

Read and shape diagnostics are capped at 8,000 characters in compact and JSON
formats. Exact display-path and suffix ambiguity reports return at most five
deterministically ordered candidate source IDs. `miller content` writes both
compact and JSON failure diagnostics to stderr and exits 3; `no_results`
remains a successful empty result. CLI status comes from the tool's structured
execution result, not rendered-text inspection, so successful content that
contains ` failed:` or `diagnostic_code=` still exits 0.

Persistent storage, bounded `read`, source identity, content search,
cross-workspace routing, and the CLI JSONL export schema are unchanged.
