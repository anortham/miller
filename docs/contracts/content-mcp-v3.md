# Content MCP Contract v3

Status: active

This contract covers the agent-facing `content` MCP tool. The process-facing
`miller content export` JSONL feed remains governed by
[`content-corpus-v1.md`](content-corpus-v1.md) and
[`cli-eros-v1.md`](cli-eros-v1.md).

## Operations and hard boundary

The MCP operations are `import`, `add_markdown`, `search`, `read`, `shape`,
`list`, and `remove`. `export` is CLI-only and is not retained as an MCP alias.

Every successful or failed MCP response is valid UTF-8 and at most 12 KiB.
The limit is measured after compact or JSON serialization. Row limits are
secondary controls, not substitutes for the byte ceiling.

Caller-controlled text is rejected before I/O when it exceeds its input limit:

| input | UTF-8 bytes |
|---|---:|
| `operation` | 64 |
| `path` | 4,096 |
| `query` | 2,048 |
| `source_id` | 1,024 |
| `url`, `display_path` | 2,048 |
| `content_kind` | 128 |
| `workspace_id` | 1,024 |
| `format` | 32 |

`format` must be `compact` or `json`. Oversized input returns
`input_too_large`; it is never echoed in full.
Numeric `context_lines` must be 0 through 1,000,000. Arithmetic uses a widened
integer before clamping to the source, so caller values cannot wrap a read
window.

## Search

`limit` is 1 through 100. Blank `content_kind` defaults to `external_file`;
explicit `all` searches workspace source, docs, config, external files, and
web content. Workspace corpus kinds are revision-checked against `symbols.db`.
A stale current corpus is never searched as if current. Each of the five kinds
is isolated independently, so one unavailable workspace kind cannot hide
healthy imported results or the failure state of the other workspace kinds.

Cross-workspace results are merged by local rank, then stable workspace and
source keys. Raw BM25 scores from different corpora are not compared. With
`workspace_id=all|registered`, a stale or broken workspace is isolated and
reported while healthy workspaces still return results. An explicit single
workspace selector fails directly. At most three degraded-workspace detail
objects are rendered; the exact distinct-workspace total and omitted-detail count remain visible. Multiple failing
content kinds in one workspace are collapsed into one workspace detail with a bounded `failed_kinds` array; the
prose message is advisory and may be truncated. An imports-only workspace uses
`diagnostic_code=content_corpus_imports_only`.
When failures prevent a complete zero-hit claim, the diagnostic is
`workspace_search_incomplete`, not `no_results`, and the first recovery action
refreshes a failed workspace.

MCP JSON search always returns one schema-v3 object, including empty results:

1. `schema_version`, `operation`, `query`, `content_kind`
2. `requested_limit`, `probed_candidate_count`, `returned_count`
3. `probed_result_limit_omitted_count`, `output_omitted_count`
4. `output_truncated`, `more_may_exist`
5. `degraded_workspace_count`, optional `diagnostic_code`
6. `degraded_workspaces`, `degraded_workspaces_omitted_count`
7. `results`, `next_actions`

Every result carries `source_id`, line coordinates, bounded display/snippet
text, and `content_hash`. Cross-workspace results also carry the selected
`workspace_id`. The first next action is directly callable as `content read`.
Candidate probing reads at most `requested_limit + 1` ranked hits per selected
corpus. The two `probed_*` counts are therefore lower bounds when
`more_may_exist=true`; they are not represented as exhaustive corpus counts.

The FTS search index loads raw chunk text only for candidate chunk IDs selected
by FTS. Strict and widened FTS arms each select at most 5,000 ranked IDs, and
raw text is hydrated/scored in batches of 400. Opening an index or running a
broad query does not materialize the complete corpus text in managed memory.

## Read

The corpus read window is at most 200 lines and always retains the requested
line. MCP rendering starts with that full window, then removes the farthest
outer lines only when required by the 12 KiB response ceiling. Oversized
windows are centered on the requested line when possible, then shifted to the
start or end boundary so the returned page remains full.

MCP JSON read is schema v3 and reports:

- `content_hash`, requested line, context, source line count, and store clamp;
- returned line range and count;
- `omitted_before`, `omitted_after`, and `output_truncated`;
- per-line truncation and `truncated_line_count`;
- directly callable backward and forward continuations whenever lines were omitted on the corresponding side.

Compact output reports source-window clamping even when the bounded window ends
at the last source line. It emits both earlier and forward recovery calls when a centered window omits both sides.
Forward continuation is shown only when unread lines remain after the returned window, so a forward continuation
chain terminates.
Line text and path metadata are bounded independently of import size.

`content_hash` is shared by import, search, read, shape, and list. A caller can
detect source replacement between discovery and use without re-reading content.
Shape and read obtain source metadata and chunks from one SQLite read
transaction.
For `workspace_source`, `workspace_docs`, and `workspace_config`, both operations also compare the corpus revision
to the selected workspace's current `symbols.db` revision before returning text. A stale corpus fails with the
same actionable freshness diagnostic as workspace-content search. Imported `external_file` and `web` sources are
not workspace-versioned.

## List and shape

Bare `list` and explicit `content_kind=all` inventory the imported
`external_file` and `web` kinds; `all` in search is broader and covers all five
searchable kinds. An explicit concrete list kind inventories one kind. `limit`
is per kind and capped at 20. Exact totals are counted in the same SQLite read
transaction as the returned page. JSON uses schema v3 and
reports exact `total_count`, `returned_count`, and `omitted_count`; rows are
dropped deterministically when their serialized form would exceed 12 KiB.

`shape` uses schema v3 and returns source identity, `content_hash`, exact byte
and line counts, five head lines, five tail lines, and a deterministic
text-derived severity summary. Severity is evidence, not parser truth.

## Import, remove, and diagnostics

The default import ceiling is 25 MiB. A larger explicit `max_bytes` uses the
streaming hash/decode/chunk/insert path; it does not allocate the complete file.
Invalid UTF-8, overlong logical lines, size drift, or other failures roll back
the transaction. Import and remove responses bound every echoed caller field.

Diagnostics are operation-specific. Examples include `missing_path`,
`missing_url`, `import_too_large`, `invalid_utf8`, `invalid_limit`,
`invalid_content_kind`, `missing_source_id`, `ambiguous_source`,
`source_not_found`, `content_corpus_missing`, `content_corpus_stale`,
`line_out_of_range`, and `invalid_context_lines`. Recovery actions are emitted
only when they apply to the failed operation; import failures do not receive
read/search placeholders.

## CLI boundary

MCP bounds do not narrow process-facing workflows:

- `miller content list` remains exhaustive and keeps its flat compact/v1 JSON
  shapes;
- `miller content export` remains CLI-only and streams deterministic JSONL
  rows from SQLite to stdout instead of materializing the corpus in a list and
  then a second complete string; row terminators remain literal LF on every
  platform;
- CLI search/read output retains its existing unpaged process contract.
