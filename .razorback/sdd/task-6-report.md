# Task 6 Report: Adoption And Episode Analysis

## Changed Files

- `scripts/bench-foundation-matrix.py`
- `scripts/benchlib/reporting.py`
- `scripts/benchlib/scoring.py`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.json`
- `.razorback/sdd/task-6-report.md`

## Miller Evidence Used

- `workspace status` confirmed `/Users/murphy/source/miller/.worktrees/foundation-effectiveness-matrix` was indexed and fresh.
- `content search/read` confirmed the Task 6 section in `docs/plans/2026-06-27-miller-julie-foundation-effectiveness-plan.md`.
- `context` identified the foundation matrix runner, reporting, scoring, and existing Task 3/4/5 evidence as the relevant implementation area.
- `inspect` reviewed `scripts/bench-foundation-matrix.py`, `scripts/benchlib/reporting.py`, `scripts/benchlib/scoring.py`, `write_outputs`, `main`, `score_contract_json`, and `summarize_foundation_matrix` before edits.
- `impact` on the final diff confirmed the changed benchmark scripts and likely test surface.

## Adoption Inputs Parsed

- `miller telemetry export --jsonl`
  - Parsed 13,471 non-empty JSONL rows.
  - Sampled 200 telemetry rows for schema fields: `tool`, `ts`, `outcome`, `result_count`.
  - Filtered adoption metrics to workspace root `/Users/murphy/source/miller`.
- `miller workspace onboarding --json --workspace-id /Users/murphy/source/miller`
  - Parsed onboarding JSON successfully.
  - Required fields present: `telemetry`, `start_here`, `tool_mix`.
  - Included `common_misses` and `friction` sections when present.

## Generated Evidence

- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.md`
- `docs/findings/benchmarks/2026-06-27-foundation-matrix/task6-adoption/adoption-summary.json`

Generation command:

```bash
python3 scripts/bench-foundation-matrix.py --adoption-only --out-dir docs/findings/benchmarks/2026-06-27-foundation-matrix/task6-adoption
```

## Verification

- Passed: `python3 -m py_compile scripts/benchlib/*.py scripts/bench-julie-miller-search-inspect.py scripts/bench-foundation-matrix.py`
- Passed: `python3 scripts/bench-julie-miller-search-inspect.py --repos miller --skip-julie --skip-miller-refresh --gate --out-dir /tmp/miller-search-inspect-task6-smoke`
- Passed: `python3 scripts/bench-foundation-matrix.py --tasks contract.cli.json,contract.cli.jsonl --skip-julie --out-dir /tmp/miller-contract-task6-regression --gate`
- Passed: adoption output assertion for required files, telemetry/no-telemetry parse state, onboarding JSON parse, report-only boundaries, and low-use-tool section.
- Passed: `git diff --check`

## Concerns

- Prior Task 4 workflow evidence was run with `--skip-julie`, so Task 6 records that no Julie-style one-call superiority conclusion can be drawn from that prior run.
- Adoption metrics are from the local telemetry window only and are explicitly report-only; the summary warns against raw usage-volume conclusions and MCP surface expansion by default.
