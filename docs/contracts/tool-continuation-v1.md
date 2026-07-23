# Tool Continuation Contract v1

Status: active for `inspect depth=full` bodies. Phase 3 reference-list migrations must reuse this
foundation instead of defining another token format or MCP tool.

## Budget

An inspect full-body page contains at most 16 KiB of UTF-8 source text. A page never splits a UTF-8 code
point. Compact and JSON calls page the same byte sequence deterministically.

Small bodies remain byte-identical and need no continuation. A body that exceeds the budget returns an
opaque continuation token:

- Compact: the final `next:` action repeats `inspect` with the exact symbol ID and `continuation`.
- JSON: `body_start_offset`, `body_end_offset`, `body_truncated`, and `body_continuation`.

Offsets are zero-based UTF-8 byte offsets within the extracted body text. `body_continuation` is `null`
on the final page.

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

## Rejection

Continuation requires `depth=full`. Miller returns a typed refusal when the token is malformed,
non-canonical, unsupported, outside the current body, or bound to a different workspace, symbol, hash, or
source span.

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

If a large extracted body lacks the hash or positive source span required to issue a safe token, Miller
returns `unavailable/continuation_identity_unavailable` and recommends a workspace refresh.
