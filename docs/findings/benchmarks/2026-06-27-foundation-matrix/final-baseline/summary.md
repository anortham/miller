# Miller Foundation Matrix Benchmark

Manifest: `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix/scripts/benchmarks/miller-foundation-cases.json`
Repos: eros, express, flask, gson, jq, julie, miller, newtonsoft, zod

Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. `pass` records the selected scoring mode: top-ranked for `path_top`, otherwise presence. Hard gates require the selected scoring mode to pass, while Julie rows are report-only.

Workflow fields keep path scoring intact: `expected_anchor_count`/`expected_anchors_present` score required workflow anchors, `first_useful_anchor` records the first matched anchor, `follow_up_hint_present` records guidance such as `next inspect`, `readiness` records edit/inspect/search state, and `workflow_outcome` records structured `ok`, `needs-search`, `unsupported`, or `no-path` outcomes.

Contract fields are explicit: `contract_parse_ok` records JSON/JSONL parsing, `required_fields_present` and `required_row_fields_present` record required contract fields, `advertised_commands_present` records `capabilities --json` coverage, `sampled_jsonl_rows` records the JSONL sample checked, and `contract_outcome` records `ok`, `empty_allowed`, `unsupported`, or the failure class.

Calibrated hard gates are named aggregate thresholds for original-nine Miller retrieval/inspect behavior plus Eros-facing CLI contract parseability. Julie deltas, top-rank gaps, workflow call-count-to-anchor, latency, output-size, metrics CLI rows, and adoption interpretation are report-only calibration notes.

| tool | tasks | pass | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| julie.blast_radius | 3 | 0 | 0 | 0 | 0 | 2 | 3 | 157 |
| julie.call_path | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 215 |
| julie.deep_dive | 34 | 20 | 16 | 20 | 0 | 7 | 95 | 793 |
| julie.fast_refs | 5 | 0 | 0 | 0 | 0 | 0 | 1 | 126 |
| julie.fast_search | 48 | 34 | 13 | 46 | 0 | 11 | 122 | 642 |
| julie.get_context | 5 | 0 | 0 | 3 | 0 | 2119 | 3777 | 10206 |
| miller.cli | 15 | 15 | 0 | 15 | 0 | 114 | 810 | 4952 |
| miller.context | 5 | 5 | 0 | 5 | 0 | 272 | 472 | 2771 |
| miller.impact | 3 | 3 | 0 | 3 | 0 | 238 | 238 | 1856 |
| miller.inspect | 34 | 34 | 30 | 34 | 0 | 17 | 49 | 1866 |
| miller.search | 48 | 48 | 34 | 48 | 0 | 24 | 150 | 991 |
| miller.trace | 7 | 6 | 0 | 5 | 0 | 228 | 404 | 1799 |

## Breakdown By Task Class

| task class | tool | route | rows | hard | pass | present | top | anchors | readiness | empty | adaptations | median ms |
|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|
| ambiguity.scoped | julie.deep_dive | mcp | 3 | 0 | 2 | 2 | 1 |  |  | 0 | 1 | 9 |
| ambiguity.scoped | miller.inspect | mcp | 3 | 0 | 3 | 3 | 3 |  |  | 0 | 0 | 15 |
| ambiguity.unscoped | julie.deep_dive | mcp | 4 | 0 | 0 | 0 | 0 |  |  | 0 | 4 | 0 |
| ambiguity.unscoped | miller.inspect | mcp | 4 | 0 | 4 | 4 | 3 |  |  | 0 | 0 | 12 |
| context.workflow | julie.get_context | mcp | 5 | 0 | 0 | 3 | 0 | 11/15 | inspect-ready | 0 | 5 | 2119 |
| context.workflow | miller.context | mcp | 5 | 0 | 5 | 5 | 0 | 15/15 | inspect-ready | 0 | 0 | 272 |
| contract.cli.json | miller.cli | cli | 10 | 10 | 10 | 10 | 0 |  |  | 0 | 0 | 99 |
| contract.cli.jsonl | miller.cli | cli | 5 | 5 | 5 | 5 | 0 |  |  | 0 | 0 | 162 |
| impact.workflow | julie.blast_radius | mcp | 3 | 0 | 0 | 0 | 0 | 0/13 | edit-ready | 0 | 3 | 2 |
| impact.workflow | miller.impact | mcp | 3 | 0 | 3 | 3 | 0 | 13/13 | edit-ready | 0 | 0 | 238 |
| inspect.full | julie.deep_dive | mcp | 9 | 0 | 9 | 9 | 7 |  |  | 0 | 0 | 66 |
| inspect.full | miller.inspect | mcp | 9 | 0 | 9 | 9 | 8 |  |  | 0 | 0 | 23 |
| inspect.overview | julie.deep_dive | mcp | 9 | 0 | 9 | 9 | 8 |  |  | 0 | 0 | 28 |
| inspect.overview | miller.inspect | mcp | 9 | 9 | 9 | 9 | 8 |  |  | 0 | 0 | 26 |
| inspect.summary | julie.deep_dive | mcp | 9 | 0 | 0 | 0 | 0 |  |  | 0 | 9 | 0 |
| inspect.summary | miller.inspect | mcp | 9 | 0 | 9 | 9 | 8 |  |  | 0 | 0 | 16 |
| retrieval.docs | julie.fast_search | mcp | 9 | 0 | 7 | 7 | 0 |  |  | 0 | 2 | 10 |
| retrieval.docs | miller.search | mcp | 9 | 0 | 9 | 9 | 2 |  |  | 0 | 0 | 50 |
| retrieval.file | julie.fast_search | mcp | 9 | 0 | 0 | 9 | 0 |  |  | 0 | 9 | 101 |
| retrieval.file | miller.search | mcp | 9 | 9 | 9 | 9 | 9 |  |  | 0 | 0 | 15 |
| retrieval.region | julie.fast_search | mcp | 3 | 0 | 3 | 3 | 1 |  |  | 0 | 0 | 29 |
| retrieval.region | miller.search | mcp | 3 | 0 | 3 | 3 | 2 |  |  | 0 | 0 | 120 |
| retrieval.source_auto | julie.fast_search | mcp | 9 | 0 | 9 | 9 | 4 |  |  | 0 | 0 | 9 |
| retrieval.source_auto | miller.search | mcp | 9 | 9 | 9 | 9 | 7 |  |  | 0 | 0 | 79 |
| retrieval.source_explicit | julie.fast_search | mcp | 9 | 0 | 9 | 9 | 4 |  |  | 0 | 0 | 10 |
| retrieval.source_explicit | miller.search | mcp | 9 | 0 | 9 | 9 | 7 |  |  | 0 | 0 | 50 |
| retrieval.symbol | julie.fast_search | mcp | 9 | 0 | 6 | 9 | 4 |  |  | 0 | 3 | 12 |
| retrieval.symbol | miller.search | mcp | 9 | 9 | 9 | 9 | 7 |  |  | 0 | 0 | 20 |
| trace.bridge | julie.call_path | mcp | 1 | 0 | 0 | 0 | 0 | 0/2 | unsupported | 0 | 1 | 0 |
| trace.bridge | miller.trace | mcp | 1 | 0 | 1 | 0 | 0 | 2/2 | unsupported | 0 | 0 | 70 |
| trace.path | julie.call_path | mcp | 1 | 0 | 0 | 0 | 0 | 0/3 | no-path | 0 | 1 | 0 |
| trace.path | miller.trace | mcp | 1 | 0 | 1 | 0 | 0 | 3/3 | no-path | 0 | 0 | 243 |
| trace.refs | julie.fast_refs | mcp | 5 | 0 | 0 | 0 | 0 | 0/15 | inspect-ready, needs-search | 0 | 5 | 0 |
| trace.refs | miller.trace | mcp | 5 | 0 | 4 | 5 | 0 | 15/15 | inspect-ready, needs-search | 0 | 0 | 228 |

Raw CSV: `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv`
Raw JSON: `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json`

## Gate

Status: PASS

### Thresholds

| gate | observed | required | status | rationale |
|---|---:|---:|---|---|
| `miller.exact_symbol.present.original_nine` | 9 / 9 | 9 / 9 | PASS | protects shipped exact-symbol lookup across the original nine-repo baseline |
| `miller.file.present.original_nine` | 9 / 9 | 7 / 9 | PASS | protects file lookup while leaving known route-rank improvement work report-only |
| `miller.source_auto.present.original_nine` | 9 / 9 | 8 / 9 | PASS | protects automatic source rescue without freezing top-rank tuning |
| `miller.inspect_overview.present.original_nine` | 9 / 9 | 9 / 9 | PASS | protects compact inspect orientation across the original nine-repo baseline |
| `eros.contracts.parse_failures` | 0 parse failures / 15 rows | 0 parse failures | PASS | protects JSON/JSONL parseability for active Eros process contracts |

Calibration notes: `docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/calibration.md`
