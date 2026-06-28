# Miller Foundation Matrix Benchmark

Manifest: `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix/scripts/benchmarks/miller-foundation-cases.json`
Repos: eros, flask, julie, miller, zod

Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. `pass` records the selected scoring mode: top-ranked for `path_top`, otherwise presence. Hard gates require the selected scoring mode to pass, while Julie rows are report-only.

Workflow fields keep path scoring intact: `expected_anchor_count`/`expected_anchors_present` score required workflow anchors, `first_useful_anchor` records the first matched anchor, `follow_up_hint_present` records guidance such as `next inspect`, `readiness` records edit/inspect/search state, and `workflow_outcome` records structured `ok`, `needs-search`, `unsupported`, or `no-path` outcomes.

| tool | tasks | pass | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| julie.blast_radius | 3 | 0 | 0 | 0 | 3 | 0 | 0 | 0 |
| julie.call_path | 2 | 0 | 0 | 0 | 2 | 0 | 0 | 0 |
| julie.fast_refs | 5 | 0 | 0 | 0 | 5 | 0 | 0 | 0 |
| julie.get_context | 5 | 0 | 0 | 0 | 5 | 0 | 0 | 0 |
| miller.context | 5 | 5 | 0 | 5 | 0 | 268 | 490 | 2771 |
| miller.impact | 3 | 3 | 0 | 3 | 0 | 220 | 230 | 1856 |
| miller.trace | 7 | 6 | 0 | 5 | 0 | 212 | 405 | 1799 |

## Breakdown By Task Class

| task class | tool | route | rows | hard | pass | present | top | anchors | readiness | empty | adaptations | median ms |
|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|
| context.workflow | julie.get_context | skipped | 5 | 0 | 0 | 0 | 0 |  | unsupported | 5 | 0 | 0 |
| context.workflow | miller.context | mcp | 5 | 5 | 5 | 5 | 0 | 15/15 | inspect-ready | 0 | 0 | 268 |
| impact.workflow | julie.blast_radius | skipped | 3 | 0 | 0 | 0 | 0 |  | unsupported | 3 | 0 | 0 |
| impact.workflow | miller.impact | mcp | 3 | 3 | 3 | 3 | 0 | 13/13 | edit-ready | 0 | 0 | 220 |
| trace.bridge | julie.call_path | skipped | 1 | 0 | 0 | 0 | 0 |  | unsupported | 1 | 0 | 0 |
| trace.bridge | miller.trace | mcp | 1 | 0 | 1 | 0 | 0 | 2/2 | unsupported | 0 | 0 | 69 |
| trace.path | julie.call_path | skipped | 1 | 0 | 0 | 0 | 0 |  | unsupported | 1 | 0 | 0 |
| trace.path | miller.trace | mcp | 1 | 0 | 1 | 0 | 0 | 3/3 | no-path | 0 | 0 | 212 |
| trace.refs | julie.fast_refs | skipped | 5 | 0 | 0 | 0 | 0 |  | unsupported | 5 | 0 | 0 |
| trace.refs | miller.trace | mcp | 5 | 4 | 4 | 5 | 0 | 15/15 | inspect-ready, needs-search | 0 | 0 | 216 |

Raw CSV: `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv`
Raw JSON: `docs/findings/benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json`

## Gate

Status: PASS
