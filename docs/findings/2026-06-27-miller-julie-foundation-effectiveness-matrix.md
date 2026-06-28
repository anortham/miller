# Miller Julie Foundation Effectiveness Matrix

Date: 2026-06-27

## Finding

Julie is a baseline and a source of lessons, not a parity target or clone target.

The replacement story is Miller plus `julie-extractors` plus Eros. Miller owns deterministic code navigation, retrieval, workspace lifecycle, and CLI/export contracts. `julie-extractors` owns parser-backed extraction. Eros owns semantic/vector retrieval, guidance, confidence views, history, and commercial orchestration.

This matrix should drive Miller toward a better deterministic agent foundation, not toward Julie's exact UX or tool count. Hard gates apply to Miller behavior and Eros-facing contracts. Julie deltas are report-only inputs for adaptation candidates.

## Raw Evidence

Original narrow search/inspect benchmark:

- [Finding](2026-06-27-julie-miller-search-inspect-benchmark.md)
- [Summary](benchmarks/2026-06-27-search-inspect/summary.md)
- [Results CSV](benchmarks/2026-06-27-search-inspect/results.csv)
- [Prep CSV](benchmarks/2026-06-27-search-inspect/prep.csv)

Foundation matrix generated evidence:

- Task 3 retrieval, inspect, and ambiguity: [summary](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json)
- Task 4 workflows: [summary](benchmarks/2026-06-27-foundation-matrix/task4-workflows/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json)
- Task 5 Eros contracts: [summary](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.json)
- Task 6 adoption: [summary](benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.md), [JSON](benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.json)
- Adaptation candidates: [Markdown](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md), [CSV](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv), [JSON](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json)

## Hard Gates Versus Report-Only Deltas

Miller hard-gated rows passed in the generated matrix:

| area | hard-gated result | interpretation |
|---|---:|---|
| Task 3 retrieval, inspect, ambiguity | `miller.search` 48/48 pass; `miller.inspect` 34/34 pass | Miller found every expected anchor under the selected scoring mode. Top-rank gaps remain product improvement inputs. |
| Task 4 workflows | `miller.context` 5/5 pass; `miller.impact` 3/3 pass; hard-gated `trace.refs` rows pass | Workflow anchors and follow-up hints are present where those rows are gated. Report-only trace outcomes still reveal recovery opportunities. |
| Task 5 Eros contracts | `miller.cli` 15/15 pass | JSON and JSONL contracts are parseable and advertised through `capabilities --json`. |
| Task 6 adoption | telemetry JSONL and onboarding JSON parseability pass | The parseable fact surface is proven. Usage interpretation is report-only. |

Julie rows in the new matrix were skipped/report-only in the local run, so they do not fail or pass Miller. The older narrow search/inspect benchmark remains the source for Julie comparison facts. That evidence shows Julie can be more compact by default and sometimes more forgiving on fuzzy intent, while Miller is stronger on deterministic exact lookup in this sample.

## Product Interpretation

Miller is not missing the underlying data for the current foundation rows. The repeated pattern is that expected anchors are usually present, but not always first or packaged with the clearest next action.

Important report-only gaps:

- `retrieval.docs`, `retrieval.source_auto`, and some source-explicit rows passed by presence but often missed top rank.
- `inspect` passed all gated rows, but Zod and similar versioned targets still need clearer ambiguity handling.
- Task 4 captured useful structured workflow states such as `needs-search`, `no-path`, and unsupported bridge cases. Those states should route agents to the next useful existing tool.
- Task 6 shows local usage does not prove product quality. Trace and impact are low-use in this telemetry window, but they are existing deterministic tools that need better discovery, not replacement.

## Eros Foundation Contracts

Task 5 is the important Eros result: Miller's public process contracts work as a foundation. The JSON rows cover `capabilities`, workspace status/health/onboarding, and read commands such as search, inspect, context, impact, trace, and patterns. The JSONL rows cover content, telemetry, symbols, references, and complexity exports.

The recommendation is to keep hardening these CLI/export contracts when Eros has concrete needs. Do not turn contract gaps into new MCP tools by default.

## Adoption Summary

Task 6 separates the hard gate from interpretation:

- Hard gate: telemetry export JSONL parsed, onboarding JSON parsed, and required fields were present.
- Report-only: usage volume, low-use tools, empty rates, common misses, and friction.

The local telemetry window shows `trace` and `impact` are low-use deterministic tools. That is a guidance and onboarding signal, not a reason to remove them or add replacement surfaces.

## Rejected Moves

- Do not add the MCP metrics tool back. Metrics remain CLI/export and dashboard/Eros-facing facts.
- Do not move semantic/vector retrieval into Miller. Semantic workflows belong in Eros.
- Do not clone Julie's UX blindly. Adapt the useful behavior into Miller's smaller tool set.
- Do not expand the MCP surface when an existing tool, CLI/export contract, skill, or dashboard presentation can solve the workflow.

## Top Adaptation Candidates

| rank | category | candidate | why it matters |
|---:|---|---|---|
| 1 | route recovery | Improve first-call routing and recovery for source/text and ambiguous lookup intent inside existing `search` and `inspect` output. | Highest-impact/locality match: the data is present, but agents need better recovery and rerun guidance. |
| 2 | ambiguity guidance | Make inspect ambiguity explicit when multiple packages, versions, tests, or generated definitions match the same target. | Prevents editing the wrong definition after an apparently successful inspect. |
| 3 | output usefulness | Promote compact edit-orientation output before full-body reads. | The old Julie evidence shows a compact default can be useful; Miller already has `overview`, but guidance and usage lag. |
| 4 | graph workflow | Improve fallback text for `needs-search`, `no-path`, and unsupported bridge outcomes. | Keeps graph semantics honest while reducing dead ends. |
| 5 | Eros contract | Keep contract rows as CLI/export regression gates. | Protects Eros integration without growing MCP. |
| 6 | adoption guidance | Use telemetry/onboarding to improve existing-tool discovery. | Converts real local friction into better starter commands and examples. |

Full candidate details are in the generated [adaptation candidate report](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md).

## Recommended Next Implementation Goals

1. **Implement a focused `search`/`inspect` recovery slice.**
   - Keep the MCP tool set unchanged.
   - In `search auto`, add bounded source/content rescue guidance when primary symbol/file hits look weak but source/content evidence exists.
   - In `inspect`, prefer non-test concrete definitions when exact names collide, and render scoped rerun examples when multiple definitions remain plausible.
   - Cover the work with focused rows from Task 3, the existing search/inspect gate, and docs updates for first-call routing.

2. Improve trace outcome guidance for `needs-search`, `no-path`, and unsupported bridge cases.

3. Move the Task 5 Eros CLI contract rows into standard branch or release verification guidance.

4. Update onboarding and agent guidance so low-use deterministic tools are easier to discover without changing the tool surface.
