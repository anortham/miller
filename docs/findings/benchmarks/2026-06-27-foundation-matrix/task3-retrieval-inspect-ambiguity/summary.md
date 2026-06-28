# Miller Foundation Matrix Benchmark

Manifest: `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix/scripts/benchmarks/miller-foundation-cases.json`
Repos: eros, express, flask, gson, jq, julie, miller, newtonsoft, zod

Scoring: `present` means the expected file path appeared in the result. `top` records whether the first parsed path was the expected file. Hard gates require presence, while Julie rows are report-only.

| tool | tasks | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|
| julie.deep_dive | 34 | 0 | 0 | 34 | 0 | 0 | 0 |
| julie.fast_search | 48 | 0 | 0 | 48 | 0 | 0 | 0 |
| miller.inspect | 34 | 30 | 34 | 0 | 17 | 46 | 1866 |
| miller.search | 48 | 34 | 48 | 0 | 28 | 152 | 991 |

## Breakdown By Task Class

| task class | tool | route | rows | hard | present | top | empty | adaptations | median ms |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| ambiguity.scoped | julie.deep_dive | skipped | 3 | 0 | 0 | 0 | 3 | 0 | 0 |
| ambiguity.scoped | miller.inspect | mcp | 3 | 3 | 3 | 3 | 0 | 0 | 15 |
| ambiguity.unscoped | julie.deep_dive | skipped | 4 | 0 | 0 | 0 | 4 | 0 | 0 |
| ambiguity.unscoped | miller.inspect | mcp | 4 | 4 | 4 | 3 | 0 | 0 | 16 |
| inspect.full | julie.deep_dive | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| inspect.full | miller.inspect | mcp | 9 | 9 | 9 | 8 | 0 | 0 | 20 |
| inspect.overview | julie.deep_dive | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| inspect.overview | miller.inspect | mcp | 9 | 9 | 9 | 8 | 0 | 0 | 19 |
| inspect.summary | julie.deep_dive | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| inspect.summary | miller.inspect | mcp | 9 | 9 | 9 | 8 | 0 | 0 | 15 |
| retrieval.docs | julie.fast_search | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| retrieval.docs | miller.search | mcp | 9 | 9 | 9 | 2 | 0 | 0 | 60 |
| retrieval.file | julie.fast_search | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| retrieval.file | miller.search | mcp | 9 | 9 | 9 | 9 | 0 | 0 | 14 |
| retrieval.region | julie.fast_search | skipped | 3 | 0 | 0 | 0 | 3 | 0 | 0 |
| retrieval.region | miller.search | mcp | 3 | 3 | 3 | 2 | 0 | 0 | 114 |
| retrieval.source_auto | julie.fast_search | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| retrieval.source_auto | miller.search | mcp | 9 | 9 | 9 | 7 | 0 | 0 | 81 |
| retrieval.source_explicit | julie.fast_search | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| retrieval.source_explicit | miller.search | mcp | 9 | 9 | 9 | 7 | 0 | 0 | 45 |
| retrieval.symbol | julie.fast_search | skipped | 9 | 0 | 0 | 0 | 9 | 0 | 0 |
| retrieval.symbol | miller.search | mcp | 9 | 9 | 9 | 7 | 0 | 0 | 19 |

Raw CSV: `docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv`
Raw JSON: `docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json`

## Gate

Status: PASS
