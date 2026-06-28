# Foundation Matrix Gate Calibration

Gate status: **PASS**

## Hard Gates

These are the only hard gates for this final baseline run. They protect shipped Miller behavior and active Eros-facing process contracts without turning known product-improvement work into a blocker.

| gate | observed | required | status | rationale |
|---|---:|---:|---|---|
| `miller.exact_symbol.present.original_nine` | 9 / 9 | 9 / 9 | PASS | protects shipped exact-symbol lookup across the original nine-repo baseline |
| `miller.file.present.original_nine` | 9 / 9 | 7 / 9 | PASS | protects file lookup while leaving known route-rank improvement work report-only |
| `miller.source_auto.present.original_nine` | 9 / 9 | 8 / 9 | PASS | protects automatic source rescue without freezing top-rank tuning |
| `miller.inspect_overview.present.original_nine` | 9 / 9 | 9 / 9 | PASS | protects compact inspect orientation across the original nine-repo baseline |
| `eros.contracts.parse_failures` | 0 parse failures / 15 rows | 0 parse failures | PASS | protects JSON/JSONL parseability for active Eros process contracts |

## Report-Only Miss Summary

- Julie rows are report-only: 69/97 present, 29/97 top-ranked, 54/97 selected-mode pass.
- Miller top-rank gaps stay report-only unless a row uses `path_top`: 18 present-but-not-top rows (ambiguity.unscoped=1, inspect.full=1, inspect.overview=1, inspect.summary=1, retrieval.docs=7, retrieval.region=1, retrieval.source_auto=2, retrieval.source_explicit=2, retrieval.symbol=2).
- Workflow call-count-to-anchor remains report-only: 39/56 required anchors present across 16 workflow rows.
- miller.cli latency/output-size are report-only: median 114 ms, median 4952 chars across 15 rows.
- miller.context latency/output-size are report-only: median 272 ms, median 2771 chars across 5 rows.
- miller.impact latency/output-size are report-only: median 238 ms, median 1856 chars across 3 rows.
- miller.inspect latency/output-size are report-only: median 17 ms, median 1866 chars across 34 rows.
- miller.search latency/output-size are report-only: median 24 ms, median 991 chars across 48 rows.
- miller.trace latency/output-size are report-only: median 228 ms, median 1799 chars across 7 rows.
- No metrics CLI contract rows are present in this manifest; metrics remain report-only.
- Adoption and telemetry interpretation remains report-only; parseability evidence lives in the Task 6 adoption run.
