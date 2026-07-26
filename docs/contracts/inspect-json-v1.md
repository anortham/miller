# Inspect MCP and JSON Contract v1

Status: active for the `inspect` MCP tool and additive JSON fields shared with CLI/process output.

## MCP bounds

MCP file listings return at most 10 symbols even when `limit` is larger. Every final MCP response is at most
12 KiB of UTF-8. Miller never truncates a completed JSON envelope to meet that bound. If indexed metadata and
diagnostics cannot fit, inspect returns `refusal/output_metadata_too_large`, records zero served results, and
asks the caller to narrow the target or depth.

Static tool cores and CLI/process calls retain their requested file-list limit and are not subject to the final
MCP envelope bound.

## File targets

File JSON keeps the top-level `file` and `children` fields. Each returned child includes:

- `name`, `kind`, `language`, `file`, `line`, `end_line`, `signature`, and `symbol_id`;
- `parent_symbol_id`;
- `nesting_depth`, with top-level symbols at zero;
- `test_evidence`, using the shared typed test-role shape.

`children_total_count`, `children_returned_count`, `children_omitted_count`, and `children_truncated` describe the
full matched file population. Compact file listings show `start-end` line spans when an end line exists and
label nested rows with the same stable parent chain, including when a kind filter hides the parent row.

## Symbol targets

Every symbol JSON object includes the definition fields above except `nesting_depth`, which is a file-list
projection. `test_evidence` contains:

- `is_test`
- `test_case`
- `test_container`
- `test_lifecycle`
- `status`
- `reason`

MCP symbol documentation is capped at 2 KiB of UTF-8 and adds `doc_truncated=true` only when shortened.
CLI/process JSON retains the complete extractor documentation.

`depth=summary` stops after definition, documentation, visibility, and test-role evidence.

`depth=overview|full` retains `children`, exact/fallback inbound references, call-like `callers`, non-call
`referenced_by`, and exact/fallback outgoing callees. It also adds:

- `test_locations` plus total/returned/omitted/truncated counts, derived only from exact containing symbols whose
  typed extractor evidence marks them as tests;
- `implements` and `extends`, derived from exact/fallback outgoing relationship evidence;
- `implementations` and `subtypes`, derived from exact/fallback inbound relationship evidence.

Each typed relationship property contains `exact`, `fallback`, and `coverage`. Rows retain symbol identity,
definition and site locations, provenance, confidence, resolution tier, and resolution status. Miller does not
parse signatures to invent required methods, parameter types, return types, exports, or dependencies. Those
sections may be added only when the extractor artifact provides language-parity facts.

The test-location count fields are `test_locations_total_count`, `test_locations_returned_count`,
`test_locations_omitted_count`, and `test_locations_truncated`. The total is derived from the complete
deduplicated exact-reference set before the displayed reference page is bounded.

Typed relationship `coverage` contains `exact_available`, `exact_returned`, `exact_truncated`,
`fallback_available`, `fallback_returned`, and `fallback_truncated`. Inbound `implementations` and `subtypes`
coverage also includes `fallback_status`, because same-name ambiguity can suppress inbound fallback rows.
Overview returns at most 3 rows per tier in each typed relationship section and 3 test locations. Full MCP
returns at most 10 per tier and 10 test locations. Full CLI/process output retains up to 50 test locations and
10 rows per typed relationship tier. Larger sets report the omitted counts rather than silently claiming
completeness.

## Bodies and continuation

Overview body previews are bounded. Full body text is paged under the stateless, hash/span-bound
[Tool Continuation Contract v1](tool-continuation-v1.md). A continuation replays the same ordered UTF-8 byte
sequence or returns a typed refusal when its workspace, symbol, extractor hash, source span, or offset no longer
matches.

## Diagnostics

Resolved objects remain the success payload. Not-found targets, ambiguity, continuation refusals, output-budget
refusals, provider failures, and hard errors follow the shared
[Tool Diagnostics Contract v1](tool-diagnostics-v1.md).
