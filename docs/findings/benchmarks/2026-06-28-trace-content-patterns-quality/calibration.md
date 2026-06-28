# Foundation Matrix Gate Calibration

Gate status: **PASS**

## Hard Gates

These are the only hard gates for this final baseline run. They protect shipped Miller behavior and active Eros-facing process contracts without turning known product-improvement work into a blocker.

| gate | observed | required | status | rationale |
|---|---:|---:|---|---|
| `miller.exact_symbol.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects shipped exact-symbol lookup across the original nine-repo baseline |
| `miller.file.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects file lookup while leaving known route-rank improvement work report-only |
| `miller.source_auto.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects automatic source rescue without freezing top-rank tuning |
| `miller.inspect_overview.present.original_nine` | 0 / 0 | 0 / 9 | SKIP | protects compact inspect orientation across the original nine-repo baseline |
| `eros.contracts.parse_failures` | 0 parse failures / 0 rows | 0 parse failures | PASS | protects JSON/JSONL parseability for active Eros process contracts |

## Report-Only Miss Summary

- Julie rows are report-only: 0/14 present, 0/14 top-ranked, 0/14 selected-mode pass, 14 skipped.
- Workflow call-count-to-anchor remains report-only: 38/38 required anchors present across 22 workflow rows.
- miller.content latency/output-size are report-only: median 39 ms, median 575 chars across 4 rows.
- miller.patterns latency/output-size are report-only: median 28 ms, median 1302 chars across 4 rows.
- miller.trace latency/output-size are report-only: median 6 ms, median 893 chars across 6 rows.
- No metrics CLI contract rows are present in this manifest; metrics remain report-only.
- Adoption and telemetry interpretation remains report-only; parseability evidence lives in the Task 6 adoption run.
