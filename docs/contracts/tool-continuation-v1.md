# Tool Continuation Contract v1

Status: active for `inspect depth=full` bodies and `trace mode=refs` reference pages.

## Budget

An inspect full-body page contains at most 4 KiB of UTF-8 source text. A page never splits a UTF-8 code
point. Compact and JSON calls page the same byte sequence deterministically.

Small bodies remain byte-identical and need no continuation. A body that exceeds the budget returns an
opaque continuation token:

- Compact: the final `next:` action repeats `inspect` with the exact symbol ID and `continuation`.
- JSON: `body_start_offset`, `body_end_offset`, `body_truncated`, and `body_continuation`.

Offsets are zero-based UTF-8 byte offsets within the extracted body text. `body_continuation` is `null`
on the final page.

Full-depth inspect renders at most 10 exact callees and 10 unresolved fallback callees. JSON
`callee_coverage` reports exact available/returned/truncated counts for both tiers; compact output reports
omitted counts. `trace` remains the exhaustive graph path.

MCP `search` and file-target `inspect` calls return at most 20 rows, regardless of a larger requested limit.
MCP search JSON truncates each signature to the shared 110-character agent-rendering bound. Static tool cores
and CLI/process JSON retain the caller's requested row limit and complete signatures.

A reference page contains at most 16 KiB of UTF-8 JSON. Exact rows are emitted before unresolved fallback rows,
and the continuation maintains independent offsets for both tiers:

- Compact: the final `next:` action repeats `trace mode=refs` with the exact symbol ID and `continuation`.
- JSON: `exact_references`, `fallback_references`, `reference_coverage`, and `continuation`.

The original `limit` remains the total result cap across every page.

## Stateless Identity

The version-1 token contains and checksum-binds:

- workspace ID
- exact symbol ID
- extractor body hash
- source start byte
- source end byte
- next body offset

The token is canonical base64url JSON with a SHA-256 integrity checksum. It is an opaque resumability and
staleness token, not an authorization credential and not a secret.

Miller keeps no spillover session or server-side continuation state.

The version-1 reference token checksum-binds:

- workspace ID
- exact target symbol ID
- artifact ID and extraction revision
- normalized reference-kind filter
- include-definition flag
- total result limit
- next exact and fallback offsets

## Rejection

Body continuation requires `depth=full`. Miller returns a typed refusal when the token is malformed,
non-canonical, unsupported, outside the current body, or bound to a different workspace, symbol, hash, or
source span.

Reference continuation requires `mode=refs` and a current artifact snapshot. A token bound to another artifact
revision, target, filter, definition flag, or limit is refused rather than replayed against changed evidence.

Stable rejection codes are:

- `continuation_invalid`
- `continuation_offset_invalid`
- `continuation_workspace_mismatch`
- `continuation_symbol_mismatch`
- `continuation_hash_mismatch`
- `continuation_span_mismatch`
- `continuation_not_applicable`
- `continuation_target_mismatch`
- `continuation_body_unavailable`
- `output_budget_too_small`
- `continuation_stale`
- `continuation_unavailable`
- `reference_item_too_large`

If a large extracted body lacks the hash or positive source span required to issue a safe token, Miller
returns `unavailable/continuation_identity_unavailable` and recommends a workspace refresh.
