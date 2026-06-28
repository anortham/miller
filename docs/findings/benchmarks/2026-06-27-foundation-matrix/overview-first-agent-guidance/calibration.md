# Foundation Matrix Gate Calibration

Gate status: **PASS**

## Hard Gates

These are the only hard gates for this final baseline run. They protect shipped Miller behavior and active Eros-facing process contracts without turning known product-improvement work into a blocker.

| gate | observed | required | status | rationale |
|---|---:|---:|---|---|
| `miller.exact_symbol.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects shipped exact-symbol lookup across the original nine-repo baseline |
| `miller.file.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects file lookup while leaving known route-rank improvement work report-only |
| `miller.source_auto.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects automatic source rescue without freezing top-rank tuning |
| `miller.inspect_overview.present.original_nine` | 1 / 1 | 1 / 9 | PASS | protects compact inspect orientation across the original nine-repo baseline |
| `eros.contracts.parse_failures` | 0 parse failures / 1 rows | 0 parse failures | PASS | protects JSON/JSONL parseability for active Eros process contracts |

## Report-Only Miss Summary

- Julie rows are report-only: 0/1 present, 0/1 top-ranked, 0/1 selected-mode pass, 1 skipped.
- miller.cli latency/output-size are report-only: median 206 ms, median 8806 chars across 1 rows.
- miller.inspect latency/output-size are report-only: median 53 ms, median 2736 chars across 1 rows.
- No metrics CLI contract rows are present in this manifest; metrics remain report-only.
- Adoption and telemetry interpretation remains report-only; parseability evidence lives in the Task 6 adoption run.
