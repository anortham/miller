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

- Final baseline: [summary](benchmarks/2026-06-27-foundation-matrix/final-baseline/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json), [calibration notes](benchmarks/2026-06-27-foundation-matrix/final-baseline/calibration.md)
- Task 3 retrieval, inspect, and ambiguity: [summary](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json)
- Task 4 workflows: [summary](benchmarks/2026-06-27-foundation-matrix/task4-workflows/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task4-workflows/results.json)
- Task 5 Eros contracts: [summary](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/summary.md), [results CSV](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.csv), [results JSON](benchmarks/2026-06-27-foundation-matrix/task5-eros-contracts/results.json)
- Task 6 adoption: [summary](benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.md), [JSON](benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.json)
- Adaptation candidates: [Markdown](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.md), [CSV](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.csv), [JSON](benchmarks/2026-06-27-foundation-matrix/adaptation-candidates.json)

## Hard Gates Versus Report-Only Deltas

The final baseline gate is calibrated to named aggregate thresholds plus active Eros-facing CLI contracts. It passed with this evidence:

| gate | final baseline result | hard threshold | interpretation |
|---|---:|---:|---|
| Miller exact-symbol retrieval present on the original nine repos | `9/9` | `9/9` | Protects shipped exact-symbol lookup. |
| Miller file retrieval present on the original nine repos | `9/9` | at least `7/9` | Protects file lookup without freezing ranking work. |
| Miller source-auto retrieval present on the original nine repos | `9/9` | at least `8/9` | Protects automatic source rescue without making top rank a blocker. |
| Miller inspect overview present on the original nine repos | `9/9` | `9/9` | Protects compact inspect orientation. |
| Eros-facing JSON/JSONL contract parse failures | `0/15` parse failures | `0` parse failures | Protects the public CLI/export process contracts used by Eros. |

The final baseline also keeps the existing narrow search/inspect benchmark gate green. It continues to protect the older focused search/inspect thresholds separately from this broader foundation matrix.

Julie rows stay report-only in the final baseline: `69/97` present, `29/97` top-ranked, and `54/97` selected-mode pass. Those counts do not fail or pass Miller; they identify adaptation candidates and calibration context.

## Product Interpretation

Miller is not missing the underlying data for the current foundation rows. The repeated pattern is that expected anchors are usually present, but not always first or packaged with the clearest next action.

Important report-only gaps:

- `retrieval.docs`, `retrieval.source_auto`, and some source-explicit rows passed by presence but often missed top rank. The final baseline has `18` Miller present-but-not-top path rows across retrieval, inspect, ambiguity, and region rows.
- `inspect` passed all gated rows, but Zod and similar versioned targets still need clearer ambiguity handling.
- Task 4 captured useful structured workflow states such as `needs-search`, `no-path`, and unsupported bridge cases. The final baseline records `39/56` workflow anchors present across `16` workflow rows, and that call-count-to-anchor signal remains report-only.
- Task 6 shows local usage does not prove product quality. Trace and impact are low-use in this telemetry window, but they are existing deterministic tools that need better discovery, not replacement.
- Miller latency and output-size medians are report-only unless they become extreme enough to block interactive use. The final baseline median latencies were: `miller.search` 24 ms, `miller.inspect` 17 ms, `miller.context` 272 ms, `miller.trace` 228 ms, `miller.impact` 238 ms, and `miller.cli` 114 ms.
- No metrics CLI contract rows are present in the final manifest; metrics remain CLI/export and dashboard/Eros-facing report-only facts.

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
