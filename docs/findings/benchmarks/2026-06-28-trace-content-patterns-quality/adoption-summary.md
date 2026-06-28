# Miller Foundation Adoption Analysis

## Parseability Gate

Status: PASS
Telemetry JSONL: parsed=True no_telemetry=False non_empty_lines=14694 sampled_rows=200
Onboarding JSON: parsed=True required_fields=3/3

Parseability is the hard gate. Usage and friction interpretation below is report-only.

## Telemetry Window

Window: 2026-06-28T17:04:39.308Z to 2026-06-28T17:33:07.374Z
Exported calls: 14694
Miller workspace calls: 140

## Core Tool/Op Mix

| tool | op | calls | result count | avg ms | p95 ms |
|---|---|---:|---:|---:|---:|
| search | source | 10 | 16 | 1043 | 4876 |
| search | content | 7 | 3 | 959 | 4987 |
| search | file | 5 | 10 | 192 | 209 |
| inspect | full | 35 | 37 | 997 | 4919 |
| inspect | summary | 10 | 312 | 512 | 3366 |
| context | usage | 1 | 81 | 5362 | 5362 |
| trace | refs | 20 | 43 | 320 | 580 |
| trace | path | 9 | 0 | 66 | 478 |
| trace | bridge | 5 | 0 | 101 | 487 |
| trace | auto | 2 | 0 | 495 | 498 |
| impact | default | 0 | 0 | 0 | 0 |

## Empty And Error Rates

| tool | op | calls | empty | empty rate | errors | error rate |
|---|---|---:|---:|---:|---:|---:|
| search | source | 10 | 7 | 70.0% | 0 | 0.0% |
| search | content | 7 | 5 | 71.4% | 0 | 0.0% |
| search | file | 5 | 2 | 40.0% | 0 | 0.0% |
| inspect | full | 35 | 0 | 0.0% | 0 | 0.0% |
| inspect | summary | 10 | 0 | 0.0% | 0 | 0.0% |
| context | usage | 1 | 0 | 0.0% | 0 | 0.0% |
| trace | refs | 20 | 11 | 55.0% | 0 | 0.0% |
| trace | path | 9 | 9 | 100.0% | 0 | 0.0% |
| trace | bridge | 5 | 5 | 100.0% | 0 | 0.0% |
| trace | auto | 2 | 2 | 100.0% | 0 | 0.0% |
| impact | default | 0 | 0 | 0.0% | 0 | 0.0% |

## Onboarding Starter Commands

- run workspace health first when taking over this repo
- use search to find candidate symbols, then inspect the selected result before editing
- use inspect depth=overview for first symbol reads; use depth=full only when you need complete bodies
- use context when you need a bounded map of the code around a task
- use impact before refactors or risky edits

## Common Misses And Friction

| source | tool | op | calls | reason | empty | errors | p95 ms |
|---|---|---|---:|---|---:|---:|---:|
| common_misses | trace | refs | 11 | no_references |  |  |  |
| common_misses | trace | path | 9 | no_path |  |  |  |
| common_misses | content | read | 8 | KeyNotFoundException |  |  |  |
| common_misses | content | search | 8 | no_content_hits |  |  |  |
| common_misses | patterns | search | 8 | no_pattern_facts |  |  |  |
| common_misses | search | source | 7 | no_text_hits |  |  |  |
| common_misses | search | content | 5 | no_text_hits |  |  |  |
| common_misses | trace | bridge | 5 | no_bridge_path |  |  |  |
| common_misses | search | file | 2 | no_symbol_hits |  |  |  |
| common_misses | trace | auto | 2 | no_trace_edges |  |  |  |
| friction | content | read | 8 |  | 0 | 8 | 22 |
| friction | trace | refs | 20 |  | 11 | 0 | 580 |
| friction | trace | path | 9 |  | 9 | 0 | 478 |
| friction | content | search | 8 |  | 8 | 0 | 140 |
| friction | patterns | search | 8 |  | 8 | 0 | 41 |
| friction | search | source | 10 |  | 7 | 0 | 4876 |
| friction | search | content | 7 |  | 5 | 0 | 4987 |
| friction | trace | bridge | 5 |  | 5 | 0 | 487 |
| friction | trace | auto | 2 |  | 2 | 0 | 498 |
| friction | search | file | 5 |  | 2 | 0 | 209 |

## Low-Use Deterministic Tools

Low-use is report-only. It identifies where current agents rarely exercise existing deterministic tools; it does not recommend new MCP tools.

| tool | calls | share | note |
|---|---:|---:|---|
| impact | 0 | 0.0% | report-only low usage in this local telemetry window |
| context | 1 | 1.0% | report-only low usage in this local telemetry window |

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
