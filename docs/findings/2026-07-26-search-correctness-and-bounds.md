# Search Correctness And Bounds Evidence

**Date:** 2026-07-26
**Status:** implementation complete; final Claude review clean

## Outcome

Miller keeps the shared deterministic reranker, mixed routing, AND-to-OR
relaxation, and optional local semantic retrieval delivered in Phase 4. The
takeover re-audit now closes the remaining request-validation, zero-vector-work,
telemetry-truthfulness, and universal MCP-output gaps without adding a tool or
changing lexical ranking.

## Accepted audit findings

| Finding | Disposition |
|---|---|
| Unknown modes silently fell back to `auto` | MCP returns `invalid_request`; CLI returns usage exit 2; documented aliases remain explicit |
| Explicit source mode consulted the semantic chunk arm | Source mode is lexical-only and performs zero semantic queries |
| Source mode emitted an obsolete semantic consultation note | The note and its vector probe were removed |
| Auto rescue telemetry always claimed a source attempt | The field becomes true immediately before the source-corpus query |
| Content, source, region, and marker snippets could dominate MCP output | Snippets use a 512-byte Unicode-safe bound with additive JSON evidence |
| Semantic, hybrid, rescue, and non-symbol routes lacked one final ceiling | Every MCP Search route is guarded by one 12 KiB UTF-8 budget |
| Oversized metadata could only be controlled by raw truncation | Search returns the standard `output_metadata_too_large` diagnostic |
| Overflow guidance suggested raising an agent-facing limit | MCP guidance asks callers to narrow the query or filters |

## Architecture

The work stays inside the existing Search MCP boundary and shared
`BoundAgentOutput` seam. CLI and pure-core paths remain exhaustive. Stable
identities are preserved rather than truncated, and the existing typed diagnostic
contract handles requests whose metadata still cannot fit. No MCP tool, result
envelope, extractor contract, or semantic ownership boundary changed.

## Claude review loop

The first broad process exhausted its 15-turn cap while exploring historical
Search code and returned no verdict. A new bounded process reviewed the current
delta and found one CLI drift: strict mode parsing escaped through the generic
exit-1 failure handler. The finding was reproduced with a red CLI test, then
fixed at the dispatcher boundary so an unknown `--mode` returns usage exit 2
with the Search usage banner.

The first follow-up confirmed that fix and found one telemetry gap: the final
budget refusal ran after positive result and canary attribution. A red telemetry
assertion reproduced the stale result count. Search now checks the budget before
canary stamps and common result attribution, and the refusal records zero
returned rows. A final fresh bounded process returned `verdict=approve` with no
findings.

## Local verification

- Focused UTF-8 output-budget tests: 18 passed, 0 failed, 0 skipped.
- Affected Search gate: 969 passed, 0 failed, 0 skipped.
- Fast suite: 5,035 passed, 2 environment skips, 0 failed.
- Scale suite: 91 passed, 3 configured runtime/platform skips, 0 failed.
- Release build: 0 warnings, 0 errors.
- Native AOT publish: `osx-arm64` completed and the published binary reported
  `1.14.0+2d2ff720bb40`.
- Contract: [`search-mcp-v1.md`](../contracts/search-mcp-v1.md).

The required fresh Claude review loop is clean.
