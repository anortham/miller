# Phase 10 Impact MCP output paging

## Decision

The Phase 6 risk-ranking, traversal-evidence, and test-role work remains valid and was not repeated. The Phase 10
re-audit found one residual transport defect: MCP `impact(format=json)` could return up to 1,000 evidence-rich
rows, while revision-delta mode could also echo an unbounded `changed_paths` list. Compact output had a character
cap, but the final MCP response had no UTF-8 byte ceiling.

Impact now applies a 12,288-byte ceiling only at the MCP boundary. Oversized responses use stateless
`impact_output_page` envelopes whose fragments reconstruct the original response byte-for-byte. The CLI and
shared impact render cores remain unchanged, so complete JSON, traversal truth, revision-delta completeness, and
small-response MCP/CLI byte parity are preserved.

## Evidence

- A normal 250-row impact result pages under the MCP ceiling and reassembles to the exact pure-core JSON.
- A 600-path revision delta pages under the ceiling, reassembles to the exact CLI JSON, and retains all 600
  changed paths.
- Existing small revision-delta MCP/CLI byte-equivalence coverage remains unchanged.
- Continuation tokens bind to the resolved workspace and SHA-256 hash of the complete output, and the shared
  pager preserves UTF-8 code-point boundaries.
- `impact_mcp_output_page` independently advertises the top-level transport shape through both `features` and
  `json_contracts`.
- The fresh read-only Claude review confirmed the missing Impact budget and identified revision-delta
  `changed_paths` as the genuinely unbounded axis. The proposed raw-page approach was tightened locally into a
  valid JSON transport envelope so each MCP response remains machine-readable. Its follow-up findings added
  capability negotiation, compact multi-byte coverage, full envelope invariant coverage, and correct
  continuation-refusal guidance.

## Remaining limitation

Each continuation request deterministically re-runs the Impact computation before validating and returning the
next stateless fragment. Telemetry therefore records each page request as a real Impact invocation. This avoids
server-side spill state and keeps output identity honest, but large paged git-diff calls do repeat traversal and
the git read.
