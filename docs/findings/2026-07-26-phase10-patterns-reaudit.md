# Phase 10 Patterns re-audit

## Outcome

APPROVE. No Patterns source or contract change is required.

The re-audit treated the Phase 7 implementation and evidence as complete. It did not repeat the shipped
fan-out, aggregation, directory, filter, snapshot, diagnostic, or output-budget work.

## Current evidence

- The freshly built HEAD CLI returned 14 of 14 filtered `aspnet.minimal_api.route.v1` GET facts under
  `src/**`.
- A free-text `json` query under `**/*.json` reported 41 observed IDs considered, 5 matched and retained,
  34,584 total facts, 2 returned, and 34,582 omitted.
- Pattern list, exact search, query fan-out, summary, path-glob fallback, metadata filters, catalog overlay, and
  retained rows use the full-population and single-snapshot rules documented in
  [`patterns-json-v1.md`](../contracts/patterns-json-v1.md).
- The windowed exact-search SQL computes `COUNT(*) OVER()` before applying `LIMIT`, then joins retained identity
  rows back to payload rows in deterministic order.
- MCP list, summary, search, and no-match paths reserve diagnostic headroom and enforce the final 12 KiB
  response ceiling. CLI execution passes no MCP byte budget and remains exhaustive.
- The focused Patterns, reader, CLI, and guidance scope passed 130 tests.

The connected Miller server returned a legacy Patterns JSON shape during dogfood and was rejected as stale
evidence per the takeover handoff. Verification used the freshly built HEAD CLI instead.

## Fresh review

A fresh read-only Claude pass approved the remaining high-risk seams:

- one-transaction population counts and catalog overlay;
- SQL and fallback path/language/metadata filter parity;
- truthful 25-ID fan-out counts and filtered ranking;
- complete coverage and omission fields;
- final-envelope byte bounds;
- exhaustive CLI versus bounded MCP behavior.

The reviewer noted two non-blocking implementation details: a defensive unreachable metadata-key guard would be
an internal failure if parser validation stopped enforcing it, and telemetry result-count meaning differs by
operation. Neither changes output truth or the active contract.

## Ownership

Miller continues to own generic querying, aggregation, filtering, bounded rendering, and diagnostics over
`structural_facts`. Parser recognition and catalog expansion remain in `julie-extractors`. No MCP tool or
parser-specific special case was added.

