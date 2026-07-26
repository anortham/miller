# Trace path truth and evidence re-audit — 2026-07-26

## Decision

The Phase 10 Trace re-audit is locally complete. Phase 3 exact refs and bridge behavior remain unchanged. Path
mode now distinguishes call paths from broad dependency paths and preserves evidence for every hop.

## Corrections

- `path_kind=call` is the default and admits only `calls`, `call`, `invokes`, and `instantiates`.
- `path_kind=dependency` is the explicit broad override for imports, type uses, inheritance, and other dependency
  edges. Empty call-path results point to this recovery without claiming the broad result is a call path.
- In-memory and SQLite graphs share an evidence-preserving shortest-path contract. Every hop carries extractor
  edge kind, numeric confidence, and provenance.
- JSON v1 keeps `links[].kind=dependency_path` and adds `edge_kind`, `confidence`, and `provenance`.
- JSON echoes normalized `path_kind`; invalid values retain `invalid_path_kind` through the MCP diagnostic layer.
- Null/blank path kinds default to `call`; depth and limit clamp identically in MCP, CLI, and shared cores.
- Parallel same-pair graph edges retain the existing relationship-priority rule, so call evidence wins over a
  type-only edge.

## Verification

- Focused Trace/graph/CLI/agent-guidance gate: 160 passed, 0 failed.
- Fast suite: 5,075 passed, 2 expected skips, 0 failed.
- Scale suite: 91 passed, 3 expected semantic-runtime skips, 0 failed.
- Release build: 0 warnings, 0 errors.
- Native AOT publish: `osx-arm64` succeeded.
- Fresh Claude code/contract review: `APPROVE`.

The sealed takeover lane remains unspent.
