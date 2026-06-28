# Trace, Content, And Patterns Quality Baseline

Date: 2026-06-28

This note records the evidence for the trace/content/patterns quality slice. The goal was not to make Miller a
Julie clone; it was to make existing Miller tools more useful to agents without adding MCP tools.

## Starting Signal

The approved plan used machine telemetry to pick the slice:

| tool | calls | ok | empty | error | signal |
|---|---:|---:|---:|---:|---|
| `trace` | 343 | 164 | 177 | 2 | High empty rate; agents already use it. |
| `content` | 466 | 288 | 136 | 42 | Useful, but `content read` had too many errors. |
| `patterns` | 39 | 29 | 9 | 1 | Low usage; likely discoverability and workflow-shape issue. |

The current adoption replay in
[`benchmarks/2026-06-28-trace-content-patterns-quality/adoption-summary.md`](benchmarks/2026-06-28-trace-content-patterns-quality/adoption-summary.md)
is report-only. Its local window covered 140 Miller-workspace calls from `2026-06-28T17:04:39.308Z` to
`2026-06-28T17:33:07.374Z` and still showed the same friction shape: high empty rates for trace path/bridge,
content search misses, content read source errors, and pattern no-match searches.

## Focused Replay Matrix

Focused rows were added to
[`scripts/benchmarks/miller-foundation-cases.json`](../../scripts/benchmarks/miller-foundation-cases.json) for:

- `trace` path no-path compact/JSON recovery.
- `trace` refs empty compact/JSON recovery.
- `trace` bridge unsupported compact/JSON recovery.
- `content` search no-results compact/JSON recovery.
- `content` read missing-source compact/JSON recovery.
- `patterns` list compact/JSON next actions.
- `patterns` search no-match compact/JSON recovery.

The pre-change RED run in `/tmp/miller-red-matrix` failed 12 hard rows:

- `miller.trace`: 1/5 passing, 10/17 anchors present.
- `miller.content`: 0/4 passing, 2/15 anchors present.
- `miller.patterns`: 0/4 passing, 2/13 anchors present.

The final GREEN run is committed under
[`benchmarks/2026-06-28-trace-content-patterns-quality/`](benchmarks/2026-06-28-trace-content-patterns-quality/):

| tool | rows | hard pass | anchors | median ms | note |
|---|---:|---:|---:|---:|---|
| `trace` | 6 | 6/6 | 21/21 | 6 | Path, refs, and bridge empty states expose bounded next actions. |
| `content` | 4 | 4/4 | 15/15 | 39 | No-results/read-error output is parseable and recoverable. |
| `patterns` | 4 | 4/4 | 13/13 | 28 | List/no-match output leads to concrete follow-up searches. |

Matrix gate: PASS.

## Contract And Guidance Changes

- JSON changes are additive: `next_actions`, `diagnostic_code`, and `near_matches` are only added to existing
  read-tool outputs.
- Successful compact output remains bounded and source-id driven.
- Skills and server instructions now route agents to `trace`, `content`, and `patterns` by workflow, not by
  generic feature advertising.
- README, GitHub Pages, and CLI/Eros contract docs document the new recovery behavior.

## Boundary

No MCP tools were added. Miller remains deterministic and local. Pattern recognition still belongs to
`julie-extractors`; semantic/vector retrieval and fleet workflow guidance remain Eros responsibilities.
