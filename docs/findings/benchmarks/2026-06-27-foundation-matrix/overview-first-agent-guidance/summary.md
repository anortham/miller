# Miller Foundation Matrix Benchmark

Manifest: `/Users/murphy/source/miller/.worktrees/overview-first-agent-guidance/scripts/benchmarks/miller-foundation-cases.json`
Repos: miller

Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. `pass` records the selected scoring mode: top-ranked for `path_top`, otherwise presence. Hard gates require the selected scoring mode to pass, while Julie rows are report-only.

Workflow fields keep path scoring intact: `expected_anchor_count`/`expected_anchors_present` score required workflow anchors, `first_useful_anchor` records the first matched anchor, `follow_up_hint_present` records guidance such as `next inspect`, `readiness` records edit/inspect/search state, and `workflow_outcome` records structured `ok`, `needs-search`, `unsupported`, or `no-path` outcomes.

Contract fields are explicit: `contract_parse_ok` records JSON/JSONL parsing, `required_fields_present` and `required_row_fields_present` record required contract fields, `advertised_commands_present` records `capabilities --json` coverage, `sampled_jsonl_rows` records the JSONL sample checked, and `contract_outcome` records `ok`, `empty_allowed`, `unsupported`, or the failure class.

Calibrated hard gates are named aggregate thresholds for original-nine Miller retrieval/inspect behavior plus Eros-facing CLI contract parseability. Julie deltas, top-rank gaps, workflow call-count-to-anchor, latency, output-size, metrics CLI rows, and adoption interpretation are report-only calibration notes.

| tool | tasks | pass | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| julie.deep_dive | 1 | 0 | 0 | 0 | 1 | 0 | 0 | 0 |
| miller.cli | 1 | 1 | 0 | 1 | 0 | 206 | 206 | 8806 |
| miller.inspect | 1 | 1 | 1 | 1 | 0 | 53 | 53 | 2736 |

## Breakdown By Task Class

| task class | tool | route | rows | hard | pass | present | top | anchors | readiness | empty | adaptations | median ms |
|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|
| contract.cli.json | miller.cli | cli | 1 | 1 | 1 | 1 | 0 |  |  | 0 | 0 | 206 |
| inspect.overview | julie.deep_dive | skipped | 1 | 0 | 0 | 0 | 0 |  | unsupported | 1 | 0 | 0 |
| inspect.overview | miller.inspect | mcp | 1 | 1 | 1 | 1 | 1 |  |  | 0 | 0 | 53 |

Raw CSV: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/results.csv`
Raw JSON: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/results.json`

## Gate

Status: PASS

### Thresholds

| gate | observed | required | status | rationale |
|---|---:|---:|---|---|
| `miller.exact_symbol.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects shipped exact-symbol lookup across the original nine-repo baseline |
| `miller.file.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects file lookup while leaving known route-rank improvement work report-only |
| `miller.source_auto.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects automatic source rescue without freezing top-rank tuning |
| `miller.inspect_overview.present.original_nine` | 1 / 1 | 1 / 9 | PASS | protects compact inspect orientation across the original nine-repo baseline |
| `eros.contracts.parse_failures` | 0 parse failures / 1 rows | 0 parse failures | PASS | protects JSON/JSONL parseability for active Eros process contracts |

Calibration notes: `docs/findings/benchmarks/2026-06-27-foundation-matrix/overview-first-agent-guidance/calibration.md`
