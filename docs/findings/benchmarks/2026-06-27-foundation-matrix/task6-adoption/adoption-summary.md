# Miller Foundation Adoption Analysis

## Parseability Gate

Status: PASS
Telemetry JSONL: parsed=True no_telemetry=False non_empty_lines=13471 sampled_rows=200
Onboarding JSON: parsed=True required_fields=3/3

Parseability is the hard gate. Usage and friction interpretation below is report-only.

## Telemetry Window

Window: 2026-05-31T13:17:18.924Z to 2026-06-28T00:53:17.125Z
Exported calls: 13471
Miller workspace calls: 4787

## Core Tool/Op Mix

| tool | op | calls | result count | avg ms | p95 ms |
|---|---|---:|---:|---:|---:|
| search | default | 939 | 9553 | 31 | 100 |
| search | content | 132 | 588 | 68 | 131 |
| search | source | 131 | 650 | 69 | 156 |
| search | auto | 122 | 876 | 192 | 350 |
| search | symbol | 35 | 599 | 78 | 580 |
| search | all-text | 27 | 177 | 123 | 250 |
| search | file | 25 | 100 | 15 | 44 |
| search | regions | 9 | 11 | 97 | 121 |
| search | markers | 3 | 11 | 127 | 138 |
| inspect | default | 1641 | 15385 | 5 | 22 |
| inspect | full | 509 | 1248 | 23 | 88 |
| inspect | summary | 220 | 6302 | 27 | 127 |
| inspect | overview | 36 | 36 | 22 | 40 |
| context | default | 160 | 15863 | 64 | 477 |
| context | off | 37 | 2393 | 143 | 2281 |
| context | usage | 35 | 5231 | 537 | 2354 |
| trace | default | 78 | 1145 | 1 | 9 |
| trace | refs | 15 | 214 | 30 | 213 |
| trace | path | 3 | 0 | 149 | 235 |
| trace | auto | 2 | 0 | 1 | 1 |
| impact | default | 83 | 1692 | 2 | 9 |
| impact | git_diff | 16 | 927 | 52 | 116 |
| impact | target | 13 | 279 | 35 | 219 |
| impact | changed_paths | 3 | 152 | 5 | 10 |

## Empty And Error Rates

| tool | op | calls | empty | empty rate | errors | error rate |
|---|---|---:|---:|---:|---:|---:|
| search | default | 939 | 147 | 15.7% | 41 | 4.4% |
| search | content | 132 | 45 | 34.1% | 0 | 0.0% |
| search | source | 131 | 46 | 35.1% | 0 | 0.0% |
| search | auto | 122 | 0 | 0.0% | 0 | 0.0% |
| search | symbol | 35 | 0 | 0.0% | 0 | 0.0% |
| search | all-text | 27 | 7 | 25.9% | 0 | 0.0% |
| search | file | 25 | 11 | 44.0% | 0 | 0.0% |
| search | regions | 9 | 1 | 11.1% | 1 | 11.1% |
| search | markers | 3 | 0 | 0.0% | 0 | 0.0% |
| inspect | default | 1641 | 228 | 13.9% | 7 | 0.4% |
| inspect | full | 509 | 33 | 6.5% | 0 | 0.0% |
| inspect | summary | 220 | 26 | 11.8% | 0 | 0.0% |
| inspect | overview | 36 | 0 | 0.0% | 0 | 0.0% |
| context | default | 160 | 8 | 5.0% | 8 | 5.0% |
| context | off | 37 | 0 | 0.0% | 0 | 0.0% |
| context | usage | 35 | 0 | 0.0% | 0 | 0.0% |
| trace | default | 78 | 50 | 64.1% | 1 | 1.3% |
| trace | refs | 15 | 5 | 33.3% | 0 | 0.0% |
| trace | path | 3 | 3 | 100.0% | 0 | 0.0% |
| trace | auto | 2 | 2 | 100.0% | 0 | 0.0% |
| impact | default | 83 | 31 | 37.3% | 0 | 0.0% |
| impact | git_diff | 16 | 0 | 0.0% | 0 | 0.0% |
| impact | target | 13 | 0 | 0.0% | 0 | 0.0% |
| impact | changed_paths | 3 | 0 | 0.0% | 0 | 0.0% |

## Onboarding Starter Commands

- run workspace health first when taking over this repo
- use search to find candidate symbols, then inspect the selected result before editing
- use context for broad orientation before reading whole files

## Common Misses And Friction

| source | tool | op | calls | reason | empty | errors | p95 ms |
|---|---|---|---:|---|---:|---:|---:|
| common_misses | search | default | 88 | empty |  |  |  |
| common_misses | inspect | default | 71 | empty |  |  |  |
| common_misses | search | source | 46 | no_text_hits |  |  |  |
| common_misses | search | content | 45 | no_text_hits |  |  |  |
| common_misses | inspect | full | 33 | not_found |  |  |  |
| common_misses | inspect | summary | 26 | not_found |  |  |  |
| common_misses | content | search | 24 | no_content_hits |  |  |  |
| common_misses | trace | default | 15 | empty |  |  |  |
| common_misses | search | default | 14 | InvalidOperationException |  |  |  |
| common_misses | search | file | 11 | no_symbol_hits |  |  |  |
| friction | search | default | 360 |  | 88 | 14 | 133 |
| friction | content | read | 54 |  | 0 | 11 | 12 |
| friction | inspect | default | 777 |  | 71 | 5 | 33 |
| friction | workspace | status | 93 |  | 1 | 4 | 50 |
| friction | context | default | 70 |  | 0 | 4 | 607 |
| friction | workspace | open | 12 |  | 0 | 2 | 5017 |
| friction | search | regions | 9 |  | 0 | 1 | 121 |
| friction | edit | replace_text | 4 |  | 0 | 1 | 67 |
| friction | search | source | 131 |  | 46 | 0 | 156 |
| friction | search | content | 132 |  | 45 | 0 | 131 |

## Low-Use Deterministic Tools

Low-use is report-only. It identifies where current agents rarely exercise existing deterministic tools; it does not recommend new MCP tools.

| tool | calls | share | note |
|---|---:|---:|---|
| trace | 98 | 2.3% | report-only low usage in this local telemetry window |
| impact | 115 | 2.7% | report-only low usage in this local telemetry window |

## Julie-Style Workflow Candidates

| row | repo | intent | Miller outcome | Julie outcome | note |
|---|---|---|---|---|---|
| n/a | n/a | n/a | n/a | n/a | Prior Task 4 workflow evidence was run with Julie rows skipped, so no Julie-style one-call superiority conclusion is drawn. |

## Usage/Adoption Interpretation

- Tool exists and is parseable: proven by the parseability gate above.
- Agents actually use it: estimated only from the available local telemetry window.
- Workflow still causes friction: inferred only from empty/error rates, onboarding misses, and prior workflow candidates.

## Do Not Infer

- Do not rank product quality by raw usage volume alone.
- Do not treat low usage as proof that a tool is unnecessary.
- Do not propose MCP surface expansion by default; prefer improving existing tools, CLI/export contracts, skills, or dashboard presentation.
