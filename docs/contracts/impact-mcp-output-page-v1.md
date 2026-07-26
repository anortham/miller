# Miller impact MCP output-page v1 contract

MCP `impact` responses are capped at 12,288 UTF-8 bytes without truncating the complete unpaged response. When
the normal response fits, Miller returns the existing compact text or JSON unchanged. When it does not fit,
Miller returns a JSON transport envelope containing a byte-identical fragment:

```json
{
  "schema_version": 1,
  "kind": "impact_output_page",
  "format": "json",
  "output_fragment": "{\"note\":null,\"impacted\":[",
  "output_start_byte": 0,
  "output_end_byte": 4096,
  "output_total_bytes": 48123,
  "output_truncated": true,
  "continuation": "opaque-token",
  "note": "Concatenate output_fragment values in byte order to recover the complete impact response."
}
```

The envelope fields have these meanings:

- `schema_version`: exactly `1`.
- `kind`: exactly `impact_output_page`.
- `format`: the requested complete-output format, exactly `compact` or `json`.
- `output_fragment`: the next UTF-8-safe fragment of the complete response.
- `output_start_byte` / `output_end_byte`: the fragment's half-open byte range in the complete UTF-8 response.
- `output_total_bytes`: the complete response size.
- `output_truncated`: `true` while more bytes remain.
- `continuation`: an opaque token for the next page, or `null` on the final page.
- `note`: fixed reconstruction guidance.

Repeat the same MCP call arguments with `continuation` set to the returned token. Concatenate
`output_fragment` values in ascending, contiguous byte order. When `format` is `json`, parse only after the final
fragment has been appended. The reconstruction is byte-identical to the unpaged result.

Continuation tokens are stateless and bound to the resolved workspace and SHA-256 hash of the complete response.
If workspace state or call inputs change the complete output, Miller refuses the old token. Pages never split a
UTF-8 code point.

Continuation refusals use the typed diagnostic codes `continuation_invalid`, `continuation_offset_invalid`,
`continuation_workspace_mismatch`, `continuation_symbol_mismatch`, `continuation_hash_mismatch`, or
`continuation_span_mismatch`. Restart by repeating the original Impact call without `continuation`; Impact does
not attach an unrelated tool recovery action.

This is an MCP transport contract only. The CLI never returns this envelope and remains the complete single-body
machine channel. `output_truncated` describes transport paging, not graph traversal, revision-delta completeness,
the requested impact `limit`, or compact's existing 6,000-character presentation cap.

Failure diagnostics are not continuation-paged. If failure detail itself exceeds the MCP ceiling, Miller replaces
it with the bounded typed diagnostic `impact_diagnostic_output_too_large`.

## Capability

The feature string is `impact_mcp_output_page`. It appears in `miller capabilities --json` under `features` and
as a `json_contracts` row named `impact_mcp_output_page`, command `impact --json`, `schema_version` 1, pointing
to this document. Consumers that can receive large MCP Impact results must negotiate this feature before
assuming the top-level response is always the reconstructed Impact or revision-delta envelope.
