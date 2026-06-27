# Julie vs Miller Search/Inspect Benchmark

Repos: miller, julie, eros, express, flask, gson, newtonsoft, zod, jq

Scoring: `top` means the first visible file/result was the expected file. `present` means the expected file appeared anywhere in the output. `empty` means the tool returned a no-result/not-found/index-required response.

| tool | tasks | top | present | empty | median ms | p95 ms | median chars |
|---|---:|---:|---:|---:|---:|---:|---:|
| julie.deep_dive.overview | 9 | 8 | 9 | 0 | 20 | 86 | 1129 |
| julie.fast_search | 27 | 8 | 25 | 0 | 13 | 71 | 480 |
| miller.inspect.full | 9 | 9 | 9 | 0 | 19 | 42 | 7129 |
| miller.inspect.overview | 9 | 9 | 9 | 0 | 16 | 39 | 1961 |
| miller.search.auto | 27 | 23 | 25 | 0 | 23 | 219 | 1118 |
| miller.search.source | 9 | 8 | 9 | 0 | 69 | 117 | 2076 |

## Breakdown By Task

| task | tool | tasks | top | present | empty | median ms | median chars |
|---|---|---:|---:|---:|---:|---:|---:|
| file | julie.fast_search | 9 | 2 | 8 | 0 | 40 | 483 |
| file | miller.search.auto | 9 | 7 | 7 | 0 | 12 | 1008 |
| inspect_symbol | julie.deep_dive.overview | 9 | 8 | 9 | 0 | 20 | 1129 |
| inspect_symbol | miller.inspect.full | 9 | 9 | 9 | 0 | 19 | 7129 |
| inspect_symbol | miller.inspect.overview | 9 | 9 | 9 | 0 | 16 | 1961 |
| source | julie.fast_search | 9 | 0 | 8 | 0 | 10 | 586 |
| source_auto | miller.search.auto | 9 | 8 | 9 | 0 | 98 | 1099 |
| source_best | miller.search.source | 9 | 8 | 9 | 0 | 69 | 2076 |
| symbol | julie.fast_search | 9 | 6 | 9 | 0 | 11 | 409 |
| symbol | miller.search.auto | 9 | 8 | 9 | 0 | 19 | 1199 |

Raw results: `docs/findings/benchmarks/2026-06-27-search-inspect/results.csv`
Prep timings: `docs/findings/benchmarks/2026-06-27-search-inspect/prep.csv`

## Gate

Status: PASS
